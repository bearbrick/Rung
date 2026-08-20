# Rung

轻量级 PLC 数据采集网关 · A lightweight PLC data acquisition gateway for .NET

把西门子 S7、Modbus TCP 设备的点位配置好，采集到的数据通过 REST、SSE、Redis
供上层系统使用。多设备并行、断线自愈、自带 Web 界面，单文件部署无外部依赖。

> **状态：v0.1。** 核心链路完整可用，仓库自带模拟器，没有 PLC 也能完整体验。
> 尚未在真实设备上验证过——详见 [协议正确性](#协议正确性) 一节。

---

## 为什么会有这个东西

工厂里从 PLC 取数这件事，通常有三条路，各有各的难受：

| 做法 | 问题 |
|---|---|
| 应用直连 PLC | 每个系统各连一遍，PLC 连接资源被占满；地址散落在各处代码里 |
| 买商业网关 | 贵、封闭、按点数收费，出了问题只能等厂商 |
| 现有开源方案 | 要么是协议客户端库（只解决"能不能通"），要么是半成品（功能有但用起来不顺手） |

Rung 想做的是中间那一层：**协议客户端之上、应用系统之下**的采集服务。

### 三个明确的目标

**一、业务名与 PLC 地址解耦。**
应用侧只认 `Line1.Oven3.Temp`，永远不碰 `DB1.DBD20`。
电气改了 PLC 程序、地址变了，改一行配置即可，上层系统一行代码不用动。
这是自建网关最大的价值所在——也是整个设计的出发点。

**二、当采集服务而不是当库。**
断线自己按退避重连、一台设备出问题不影响其他设备、状态可观测、
写命令有审计日志。这些是"挂在服务器上跑三个月"和"跑个 demo"的区别。

**三、没有真机也能开发到底。**
工业项目最大的开发摩擦是"必须去现场才能验证"。Rung 自带 S7 设备模拟器、
Modbus 从站模拟器和一个最小 Redis，故障可以注入、信号是活的，
从协议解析到 Web 界面的每一环都能在办公室里验证完。

### 不做什么

- **不存历史数据。** 只管最新值。历史存储是一整个时序数据库的坑，
  会把这个项目拖死。真要历史，往 InfluxDB / TimescaleDB 推就行。
- **不做组态、不做报表、不做 MES。** 那是上层系统的事。
- **不追求支持所有协议。** 先把 S7 和 Modbus 做扎实。

---

## 30 秒跑起来

不需要 PLC，也不需要 Redis。开两个终端：

```bash
dotnet run --project src/Rung.Simulator -- samples/simulator.json
```

```bash
cd web && npm install && npm run build && cd ..
dotnet run --project src/Rung.Host -- --ConfigPath $PWD/samples/gateway.json
```

打开 <http://localhost:5580>。模拟器会起 3 台 S7 设备（其中一台每 15 秒主动掉线）、
1 台 Modbus 从站和一个最小 Redis，网关同时采集它们。

想在终端里看数据流，`src/Rung.Cli` 是同一套内核的命令行形态：

```
[line1-oven] PDU 240 字节 · 5 个点位 → 每轮 3 次请求 · 上轮耗时 0.2 ms
[line1-robot] PDU 480 字节 · 3 个点位 → 每轮 1 次请求 · 上轮耗时 4.3 ms
[line2-flaky] PDU 240 字节 · 1 个点位 → 每轮 1 次请求 · 上轮耗时 0.1 ms
[line2-meter] PDU 250 字节 · 5 个点位 → 每轮 3 次请求 · 上轮耗时 6.2 ms
  Line1.Oven.Temp                     249.6   line1-oven/DB1.DBW0
  Line1.Robot.Angle                  25.577   line1-robot/DB10.DBD0
  Line2.Meter.Voltage               398.441   line2-meter/HR0
  Line2.Meter.Closed                   true   line2-meter/CO0
  Line2.Slave2.Count                      2   line2-meter/2:HR0

持续采集中，Ctrl+C 停止。只打印发生变化的点位。
15:51:21.830  Line1.Robot.Angle                        47.565
15:51:21.830  Line1.Robot.Cycles                            3
```

> Modbus 那行显示的 "PDU 250 字节" 是个措辞遗留：Modbus 不协商 PDU，
> 250 是协议固定的单帧上限（125 个寄存器 × 2）。功能没错，用词该改。

---

## 架构

```
                应用系统 (MES / WMS / SCADA)
                          ↑
        ┌─────────────────┴──────────────────┐
        │  REST · SSE · Redis · Prometheus   │   Rung.Host / Rung.Sinks.*
        ├────────────────────────────────────┤
        │  连接生命周期 · 退避重连 · 分组调度   │   Rung.Core
        │  点位缓存 · 死区过滤 · 写命令队列     │
        ├────────────────────────────────────┤
        │           IDeviceDriver            │   Rung.Abstractions
        ├──────────────────┬─────────────────┤
        │  Rung.Drivers.S7 │ Drivers.Modbus  │
        │  ├ 自研异步传输   │ └ FluentModbus  │
        │  └ Protocols.S7  │                 │
        └──────────────────┴─────────────────┘
                          ↓
                     PLC 设备
```

`IDeviceDriver` 这层抽象经受住了第二种协议的检验：加 Modbus 时，
`IReadPlan`、`TagDef`、`TagValue`、`DeviceWorker`、`GatewayHost` **一行都没改**。
唯一的调整是把字节序换算从 S7 提到契约层共用——那是收敛，不是修补。

---

## 仓库结构

```
src/
  Rung.Abstractions/       驱动契约：IDeviceDriver / TagDef / TagValue / 字节序换算
  Rung.Protocols.S7/       S7comm 纯编解码：地址解析、报文组包、响应解析、批量合并
  Rung.Drivers.S7/         S7 驱动：异步传输、握手、连接管理
  Rung.Drivers.Modbus/     Modbus TCP 驱动：地址语义、批量合并、多从站
  Rung.Core/               采集内核：退避重连、分组调度、点位缓存、写队列、多设备编排
  Rung.Sinks.Redis/        Redis 北向输出
  Rung.Configuration/      配置模型，CLI 与 Host 共用
  Rung.Host/               ASP.NET Core 宿主：REST + SSE + OpenAPI + Prometheus
  Rung.Cli/                命令行形态，终端里看数据流
  Rung.Simulator/          S7 / Modbus 设备模拟器 + 最小 Redis
web/                       React + TS + Vite + Ant Design，产物进 Host/wwwroot
tests/                     386 个测试，全部不需要真实硬件
samples/                   开箱即用的模拟器与网关配置
deploy/ scripts/ docs/     systemd 单元、发布脚本、部署与协议文档
```

**为什么 S7 有独立的 `Protocols` 层而 Modbus 没有**：S7comm 的报文是自己实现的
（没有许可证干净的现成库），所以编解码单独成层，做成无 IO 的纯函数以便逐字节测试；
Modbus 的框架由 FluentModbus 提供，驱动只需处理地址语义和批量合并。

---

## 核心设计

**业务名与地址解耦。** 见上文"三个明确的目标"。

**批量合并决定性能。** 点位按地址连续性合并成尽量少的请求，再按协议上限切分
（S7 按协商 PDU，Modbus 寄存器 125 / 位 2000）。一个状态 DB 里连续排布的
128 个点位，逐个读要 128 次网络往返，合并后只要 2 次。
Web 界面上的 `点位/请求` 一栏就是这个效果的直接体现。

**断线是常态，不是异常。** 每台设备一个工作者，断线后指数退避重连
（1s→2s→4s…30s 封顶，带抖动）。抖动不是锦上添花：一台交换机重启会让几十台设备
同时断线，没有抖动它们会整整齐齐在同一毫秒重连。断线期间缓存降级为 `Stale`
但保留最后已知值——应用侧读到"5 分钟前的 235 度"，比读到 null 有用得多。

**采集按截止时间驱动，不排队。** 设备变慢时截止时间顺延并计入 `OverrunCount`，
不会积压出越滚越大的任务队列——那种积压最后表现为"网关内存一直涨"。

**写命令插队并回读确认。** 读是周期性的，写是事件驱动的，操作员的指令不该等一整轮。
写完立刻从设备回读同一个点位再返回——PLC 会对写入做钳位、取整，
或被联锁逻辑改回去，操作员必须看到真正生效的值。每次写都记 Information 级审计日志。

**单点配置错误不拖垮整台设备。** 上千个点位里配错一两个是常态。
坏点位记入 `IReadPlan.Issues`、每轮置为 `ConfigError`，其余照常采集。
代价是配置错误变安静了，所以 Web 界面必须把 `Issues` 显著暴露出来——
这个折中成立的前提就在这里。

**字节序逐点位可配。** 同一品牌不同型号、甚至同一台 PLC 的不同功能块，
32 位数的字节排列都可能不同，`ABCD` / `CDAB` / `BADC` / `DCBA` 四种在产线上都见过。
换算逻辑放在契约层共用，S7 和 Modbus 不各写一份——字节序错了不会崩，
只会读出一个"看着像那么回事"的错数，是最难查的一类问题。

---

## 协议支持

### 西门子 S7

`DB1.DBW10` · `DB1.DBX0.5` · `DB1.DBD20` · `MW100` · `M100.0` · `I0.0` · `Q1.3` · `T5` · `C3`

支持德文助记符（`E`/`A`/`Z`）和 S7-200 的 V 区。S7-300/400 填 rack 0 / slot 2，
S7-1200/1500 通常是 rack 0 / slot 1。

### Modbus TCP

0 基与 1 基混淆是 Modbus 接入时最高频的错误，两种写法在语义上刻意区分得很开。

| 写法 | 含义 |
|---|---|
| `HR100` `IR10` `CO5` `DI7` | **0 基**，推荐 |
| `40001` `30001` `10001` `00001` | 经典 **1 基**，`40001` 等于 `HR0` |
| `4x0001` | 同经典 1 基 |
| `HR100.3` | 保持寄存器 100 的第 3 位 |
| `3:HR100` | 指定从站号 3（一条 TCP 连接后挂多个 RTU 从站） |

不带位偏移的布尔点位按"整寄存器非零为真"解释。
**寄存器内的单个位不能写**：Modbus 没有对应功能码，只能读改写，
而读改写在并发下会丢掉别人刚写进去的位。这里明确拒绝而不是悄悄做。

---

## 对外接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/health` | 整体健康，有设备掉线为 `degraded` |
| GET | `/api/devices` | 设备状态、上轮耗时、重连次数、配置问题 |
| GET | `/api/tags` | 点位最新值，支持 `?device=` 与 `?prefix=` |
| GET | `/api/tags/{name}` | 单个点位 |
| POST | `/api/tags/{name}/write` | 写点位，**返回回读到的设备实际值** |
| GET | `/api/stream/tags` | 变化的实时推送（SSE） |
| GET | `/metrics` | Prometheus 指标 |
| GET | `/openapi/v1.json` | OpenAPI 文档 |

写入路径上显式写出 `write` 而不是用 `PUT`：这个动作会让产线上的机器真的动起来，
一眼看得出比符合 REST 惯例更重要。

**Redis** 输出把最新值写进 `rung:tag:{业务名}` 的 Hash（字段 `v`/`q`/`t`/`dev`/`addr`），
变化推送到 `rung:changes`，设备状况写 `rung:device:{id}`。
值刻意存成人能直接读懂的文本——现场排障最常用的动作就是
`redis-cli HGETALL rung:tag:Line1.Oven.Temp`，一眼看不懂这个设计就失败了。

**Prometheus** 指标里最值得配告警的是 `rung_device_overruns_total`：
持续增长说明采集周期设得太快，或者点位太多需要拆组。

---

## Web 界面

React 19 + TypeScript + Vite + Ant Design，构建产物输出到 `Rung.Host/wwwroot`，
和后端打进同一个发布目录，部署时不多一个组件。

- **点位实时值**：SSE 增量更新而非轮询，值变化时闪一下绿底，虚拟滚动扛上千点位
- **设备状况**：连接状态、上轮耗时、`点位/请求` 比、重连与超时次数，可展开看配置问题
- **手动读写**：现场调试省一半时间，不用开博途也不用写临时脚本

TypeScript 类型由 OpenAPI 文档生成（`npm run gen:api`）。后端改了 DTO
而前端没重新生成，`npm run lint` 会当场报错。

---

## 模拟器

没有真机也能把整条链路验证完，这是 `Rung.Simulator` 存在的理由。

**信号是活的**：`sine` / `ramp` / `counter` / `toggle` / `randomwalk` / `constant`。
死值只能验证"链路通不通"，会变化的信号才能验证死区过滤、变化推送这些
真正会出问题的地方。随机游走用固定种子，可复现。

**故障可以注入**：拒绝连接、应答延迟、收发 N 次后断开、周期性断线、
指定 DB 返回"对象不存在"、拒绝写命令。拔网线不可重复，这些开关可以。

**S7 报文编码是独立实现的**，不引用 Rung 的任何代码。两边同源的话，
一个写错的偏移量会同时体现在模拟器和被测代码上，测试全绿但真机一读就错。
（Modbus 模拟器直接用 FluentModbus 的服务端——Rung 本来就没有独立的
Modbus 报文实现，同源在这里不损失任何东西。）

**还内置一个最小 Redis**（RESP2 协议），因此北向输出也能在不装 Redis、
不装 Docker 的机器上端到端验证，用的是真实的 StackExchange.Redis 客户端。

---

## 协议正确性

**这是目前最大的未知数，如实写在这里。**

所有测试都跑在模拟器上，386 个用例全绿，但这只证明"实现与我对协议的理解一致"，
不证明"我的理解与真实设备一致"。S7 的报文夹具目前全部是按规范推导的
（`source: spec`），不是真机抓包。

拿到真机后半小时内可以完成替换，流程见 [`docs/protocol-fixtures.md`](docs/protocol-fixtures.md)。
按"错了最难查"排序，最该优先补录的是：REAL 类型的字节序、S7-1500 的优化 DB 访问、
跨 PDU 上限的大批量读。

---

## 配置：JSON / SQLite / Excel

小规模、想进版本控制就用 JSON；点位多、要在界面上改就用 SQLite。

```bash
rung config import 点位表.xlsx --db /var/lib/rung/rung.db   # 导入（也吃 .json）
rung config export 点位表.xlsx --db /var/lib/rung/rung.db   # 导出核对
rung config list --db /var/lib/rung/rung.db                 # 看有哪些设备
rung config check 点位表.xlsx --db /var/lib/rung/rung.db    # 离线校验，不连设备

rung --db /var/lib/rung/rung.db          # CLI 从 SQLite 跑
rung-host --Db /var/lib/rung/rung.db     # 宿主同理
```

**Excel 是这一环最实用的部分**：现场交接时电气工程师给的就是一张表，
能直接导入省掉的是几小时手工誊抄——而手工誊抄正是地址配错的主要来源。
表头用中文（`设备ID` / `点位名` / `数据类型` / `倍率` / `死区` …），
因为读写它的人是电气工程师不是程序员。

解析逐行进行、**错误带行号**，且坏行跳过而不是整份拒绝：

```
! 点位 第 3 行：点位 B 的数据类型 "Fl0at32" 无法识别，可用：Bool / Int8 / ... / Float32 ...
! 点位 第 7 行：点位 X 指向未定义的设备 "typo"，请先在「设备」表里加上
  共 2 行有问题，已跳过；其余照常导入。
```

一张几百行的表里错两行，让人改完重来一遍不如先把对的导进去。

### 出差前先跑一遍 check

地址解析、类型与地址宽度是否匹配、点位是否跨设备重名、批量合并成几次请求——
这些全是纯逻辑，没有理由等到现场连上 PLC 才发现。

```
  line1-oven         s7              5 个点位 → 每轮 1 次请求，每轮取回 22 字节
      ! Line1.Oven.Temp: DB 块号不能为 0
      ! Line1.Oven.Pressure: 地址 DB1.DBW4 宽度为 2 字节，数据类型 Float32 需要 4 字节
  line2-meter        profinet        5 个点位 → 每轮 0 次请求
      ! line2-meter: 未知的协议 "profinet"，可用：s7 / modbus-tcp
  ! 点位名重复：Line1.Oven.Temp（出现在 line1-oven、line1-robot）

发现 4 个问题。请求次数按 PDU 240 的最保守假设估算，真机只会更少。
```

有问题时退出码为 1，可以直接挂进 CI 或交付前的检查脚本。

数据库里枚举一律存字符串——有人拿 SQLite 浏览器打开时，看到 `Float32`
比看到 `9` 有用得多。表结构用 EF Core Migrations 管理，启动时自动应用。

## 部署

```bash
./scripts/publish.sh linux-x64      # 或 linux-arm64
```

产出一个可执行文件加 `wwwroot`，**目标机不需要装 .NET**——离线内网交付时这点很关键。
配套的 systemd 单元、容器镜像、目录与端口约定见 [`docs/deploy.md`](docs/deploy.md)。

默认端口 **5580**：5000 是 Kestrel 默认、8080 到处都是、9090 归 Prometheus，
挑个冷门的省掉部署时的端口撞车。

---

## 开发

```bash
dotnet test          # 386 个测试，不需要任何真实硬件
cd web && npm run lint
```

需要 .NET 10 SDK 和 Node 20+。测试走 Microsoft.Testing.Platform，
运行器在 `global.json` 中声明。

---

## 路线图

- [x] 驱动契约、S7 协议编解码、批量合并、值编解码
- [x] 采集内核：退避重连、分组调度、点位缓存、写命令、多设备编排
- [x] Redis / REST / SSE / Prometheus 输出
- [x] Modbus TCP 驱动
- [x] Web 界面、设备模拟器、单文件与容器交付
- [x] SQLite 配置存储 + Excel 导入导出
- [ ] 点位配置的 Web 编辑
- [ ] 真机验证与报文夹具替换
- [ ] Modbus RTU（串口）、三菱 MC、欧姆龙 FINS
- [ ] MQTT 输出

---

## 许可

MIT。

S7 协议的报文结构与地址解析参考 [IoTClient](https://github.com/zhaopeiym/IoTClient)（MIT）后重写，
溯源与许可证义务见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。
