# Rung

轻量级 PLC 数据采集网关 · A lightweight PLC data acquisition gateway for .NET

把西门子 S7、三菱 MC、欧姆龙 FINS、Modbus 设备的点位可视化配置好，
采集到的数据通过 REST、MQTT 或 Redis 供上层系统使用。单文件部署，无外部依赖。

> **状态：v0.1 MVP。** 多台设备并行采集、断线自己按退避重连、数据写进 Redis，
> REST + SSE 接口，以及一个能看实时值、查设备状况、手动读写的 Web 界面。
> SQLite 配置存储与 Excel 导入导出还在路上。

## 跑起来（不需要 PLC）

仓库自带模拟器，连 PLC 和 Redis 都不需要。开两个终端：

```bash
dotnet run --project src/Rung.Simulator -- samples/simulator.json
```

```bash
dotnet run --project src/Rung.Host -- --ConfigPath $PWD/samples/gateway.json
```

然后打开 <http://localhost:5580> —— 点位实时值、设备状况、手动写点位都在里面。
想在终端里看数据流的话，`src/Rung.Cli` 是同一套内核的命令行形态。

> 从源码首次运行需要先构建前端：`cd web && npm install && npm run build`。
> `dotnet publish` 会自动做这一步（没装 Node 的机器加 `-p:SkipWebUi=true` 跳过）。

```
[line1-oven]  PDU 240 字节 · 5 个点位 → 每轮 3 次请求 · 上轮耗时 0.1 ms
[line1-robot] PDU 480 字节 · 3 个点位 → 每轮 1 次请求 · 上轮耗时 3.0 ms
[line2-flaky] PDU 240 字节 · 1 个点位 → 每轮 1 次请求 · 上轮耗时 2.9 ms
  Line1.Oven.Temp                     239.7   line1-oven/DB1.DBW0
  Line1.Oven.Pressure                  1013   line1-oven/DB1.DBD4
  Line1.Oven.Running                  false   line1-oven/DB1.DBX8.0
  Line1.Robot.Angle                  90.986   line1-robot/DB10.DBD0

持续采集中，Ctrl+C 停止。只打印发生变化的点位。
13:44:12.414  Line1.Robot.Angle                        23.152
13:44:12.509  Line2.Flaky.Counter                          24
13:44:15 warn: 设备 line2-flaky 通讯中断（连续第 1 次）：对端关闭了连接，00:00:00.52 后重连
13:44:16 info: 设备 line2-flaky 已连接，PDU 240 字节，1 个点位编译成 1 次请求
```

`--once` 采一轮就退出，适合脚本和现场点位验证；`--timeout <秒>` 控制首次连接的等待上限。

## Web 界面

React + TypeScript + Vite + Ant Design，构建产物输出到 `Rung.Host/wwwroot`，
和后端一起打进单个发布目录，部署时不多一个组件。

- **点位实时值**：SSE 增量更新而非轮询，值变化时闪一下绿底。
  上千个点位靠虚拟滚动，可按点位名/地址过滤、按设备筛选、只看异常
- **设备状况**：连接状态、上轮耗时、`点位/请求` 比（合并效果一眼可见）、
  重连与超时次数。有配置问题或故障的设备可展开看详情
- **手动读写**：现场调试时省一半时间，不用开博途也不用写临时脚本。
  写完显示的是**设备回读值**，与填入值不同就说明 PLC 做了钳位或被联锁改回去了

TypeScript 类型由 OpenAPI 文档生成（`npm run gen:api`）。后端改了 DTO
而前端没重新生成，`npm run lint` 会当场报错——前后端契约漂移是这类项目
最常见的低级 bug 来源，配一次就永久消失。

## Modbus 地址写法

0 基与 1 基混淆是 Modbus 接入时最高频的错误，因此两种写法在语义上刻意区分得很开。

| 写法 | 含义 |
|---|---|
| `HR100` `IR10` `CO5` `DI7` | **0 基**，推荐 |
| `40001` `30001` `10001` `00001` | 经典 **1 基**，`40001` 等于 `HR0` |
| `4x0001` | 同经典 1 基 |
| `HR100.3` | 保持寄存器 100 的第 3 位 |
| `3:HR100` | 指定从站号 3（一条 TCP 连接后面挂多个 RTU 从站） |

不带位偏移的布尔点位按"整寄存器非零为真"解释——很多设备用一整个寄存器表示一个状态位。

**寄存器内的单个位不能写**：Modbus 没有对应的功能码，只能读改写，
而读改写在并发下会丢掉别人刚写进去的位。这里明确拒绝而不是悄悄做。

## HTTP 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/health` | 整体健康，有设备掉线为 `degraded`，可直接接监控探针 |
| GET | `/api/devices` | 全部设备的连接状态、上轮耗时、重连次数、配置问题 |
| GET | `/api/tags` | 点位最新值，支持 `?device=` 与 `?prefix=` 过滤 |
| GET | `/api/tags/{name}` | 单个点位 |
| POST | `/api/tags/{name}/write` | 写点位，**返回回读到的设备实际值** |
| GET | `/api/stream/tags` | 变化的实时推送（SSE） |
| GET | `/openapi/v1.json` | OpenAPI 文档 |
| GET | `/metrics` | Prometheus 指标（挂在根上，抓取端默认路径） |

写入路径上显式写出 `write` 而不是用 `PUT`：这个动作会让产线上的机器真的动起来，
一眼看得出比符合 REST 惯例更重要。返回值是**写完立刻从设备回读**的结果——
PLC 可能对写入做钳位、取整，或被联锁逻辑改回去，操作员必须看到真正生效的值。

默认端口 **5580**：5000 是 Kestrel 默认、8080 到处都是、9090 归 Prometheus，
挑个冷门的省掉部署时的端口撞车。

## 可观测性

`/metrics` 直接可被 Prometheus 抓取，手写暴露格式、不引额外依赖。

```
rung_device_up{device="line1-oven"} 1
rung_device_poll_duration_seconds{device="line1-oven"} 0.000383
rung_device_overruns_total{device="line1-oven"} 0
rung_device_last_success_age_seconds{device="line1-oven"} 0.4
```

几个刻意的选择：耗时用**秒**（Prometheus 的基本单位约定，用毫秒会让所有查询手工换算）；
上次成功采集用**距今秒数**而非绝对时间戳（告警好写，也不必关心两端时钟是否对齐）；
从未成功过时给 **-1** 而不是 0——0 会被误读成"刚刚才采过"，正好反了。

`rung_device_overruns_total` 持续增长是最值得配告警的一个：
它说明采集周期设得太快，或者点位太多需要拆组。

## 模拟器

没有真机也能把整条链路验证完，这是 `Rung.Simulator` 存在的理由。

**信号是活的**：`sine` / `ramp` / `counter` / `toggle` / `randomwalk` / `constant`。
死值只能验证"链路通不通"，会变化的信号才能验证死区过滤、变化推送这些真正会出问题的地方。
随机游走用固定种子，因而**可复现**——排查时能重放出一模一样的数据序列。

**故障可以注入**：拒绝连接、应答延迟、收发 N 次后断开、每隔若干秒断一次、
指定 DB 返回"对象不存在"、拒绝写命令。拔网线不可重复，这些开关可以。

**报文编码是独立实现的**，不引用 Rung 的任何代码。两边同源的话，一个写错的偏移量
会同时体现在模拟器和被测代码上，测试全绿但真机一读就错。独立实现才能互为对照。

**还内置一个最小 Redis**（说 RESP2 协议），因此北向输出也能在不装 Redis、
不装 Docker 的机器上端到端验证——用的是真实的 StackExchange.Redis 客户端。

```
rung:tag:Line1.Oven.Temp
    v=242  q=Good  t=2026-08-19T05:55:04.658Z  dev=line1-oven  addr=DB1.DBW0
rung:device:line2-flaky
    state=Faulted  lastError=对端关闭了连接  consecutiveFailures=1  tags=1  requests=1
```

## 设计要点

**业务名与 PLC 地址解耦。** 应用侧只认 `Line1.Oven3.Temp` 这样的业务名，
永远不碰 `DB1.DBD20`。电气改了 PLC 程序、地址变了，改一行配置即可，
上层系统一行代码不用动。这是自建网关最大的价值所在。

**协议编解码是无 IO 的纯函数。** 报文组包和解析被彻底剥离出传输层，
因而可以用真实报文夹具做字节级断言。协议实现的正确性不靠"看起来能跑"，
靠每个字节都被测试锁住——详见 [`docs/protocol-fixtures.md`](docs/protocol-fixtures.md)。

**批量合并决定性能。** 点位按地址连续性合并成尽量少的请求，再按 PDU 双重上限
（单次读字节数、单请求项数）切分。一个状态 DB 里连续排布的 128 个点位，
逐个读要 128 次网络往返，合并后只要 2 次。

**断线是常态，不是异常。** 每台设备一个工作者，断线后按指数退避重连
（1s→2s→4s…30s 封顶，带抖动）。抖动不是锦上添花：一台交换机重启会让几十台设备
同时断线，没有抖动它们会整整齐齐地在同一毫秒重连。断线期间缓存降级为
`Stale` 但保留最后已知值——应用侧读到"5 分钟前的 235 度"，比读到 null 有用得多。

**北向主推 Redis。** 最新值写进 `rung:tag:{业务名}` 的 Hash，变化推送到
`rung:changes` 频道，设备状况写进 `rung:device:{id}`。网关和应用完全解耦，
网关重启不影响应用读到最后已知值。值刻意存成人能直接读懂的文本——
现场排障最常用的动作就是 `redis-cli HGETALL rung:tag:Line1.Oven.Temp`，
那一眼看不懂的话这个设计就失败了。

**HTTP 接口，实时推送用 SSE。** 点位值、设备状况、写命令走 REST；
变化推送走 Server-Sent Events 而不是 WebSocket——单向推送 SSE 就够，
浏览器 `EventSource` 自带断线重连，走普通 HTTP 所以 nginx 反代零配置。
慢客户端的队列满了丢最旧的而不是阻塞：实时视图丢几帧无所谓，采集停了是事故。

**每个协议独立选型。** 驱动层通过 `IDeviceDriver` 抽象：S7 自己实现报文
（因为没有可用的 MIT 库），Modbus 直接用 FluentModbus（原生异步、维护活跃、
还自带服务端便于测试）。不把身家压在任何单一上游库上。

加 Modbus 时 `IDeviceDriver`、`IReadPlan`、`TagDef`、`TagValue`、
`DeviceWorker`、`GatewayHost` **一行都没改**——抽象层算是经受住了第二种协议的检验。
唯一的调整是把字节序换算从 S7 里提到契约层共用，那是收敛而不是修补。

## 仓库结构

```
src/
  Rung.Abstractions/       驱动契约层。第三方按此接口实现驱动即可接入
  Rung.Protocols.S7/       S7comm 纯编解码：地址解析、报文组包、响应解析、批量合并
  Rung.Drivers.S7/         S7 驱动：异步传输、连接管理、读写执行
  Rung.Drivers.Modbus/     Modbus TCP 驱动：地址语义、批量合并、多从站
  Rung.Core/               采集内核：连接生命周期、退避重连、调度、缓存、写队列
  Rung.Cli/                命令行入口，终端里看数据流
  Rung.Host/               ASP.NET Core 宿主：REST + SSE + OpenAPI
  Rung.Configuration/      配置模型，CLI 与 Host 共用
  Rung.Sinks.Redis/        Redis 北向输出
  Rung.Simulator/          S7 设备模拟器（活信号 + 故障注入）与最小 Redis
samples/                   配置文件示例
tests/
  Rung.Protocols.S7.Tests/ 报文夹具 + 字节级断言
  Rung.Drivers.S7.Tests/   进程内假 S7 设备 + 端到端链路测试
  Rung.Drivers.Modbus.Tests/ FluentModbus 服务端 + 可掐断的 TCP 代理
  Rung.Core.Tests/         可编程假驱动 + 调度、重连、多设备编排测试
  Rung.Simulator.Tests/    信号源与地址解析
  Rung.Sinks.Redis.Tests/  真实 Redis 客户端 × 模拟 Redis 的端到端用例
  Rung.Host.Tests/         走真实 HTTP 管道的接口测试
third_party/IoTClient/     上游溯源与许可证
docs/                      设计与操作文档
```

## 部署

```bash
./scripts/publish.sh linux-x64
```

产出一个可执行文件加 `wwwroot`，**目标机不需要装 .NET**——离线内网交付时这点很关键。
配套的 systemd 单元、容器镜像、目录与端口约定见 [`docs/deploy.md`](docs/deploy.md)。

## 开发

```bash
dotnet test
```

需要 .NET 10 SDK。测试走 Microsoft.Testing.Platform（.NET 10 起 `dotnet test` 的默认路径），
运行器在 `global.json` 中声明。

测试不需要真实 PLC：所有端到端用例都跑在 `Rung.Simulator` 上，
它的报文构造独立于 Rung 另写一遍，因此解析器和编码器互为对照。

## 路线图

- [x] 驱动契约层 `IDeviceDriver` / `TagDef` / `TagValue`
- [x] S7 协议编解码 + 报文夹具测试
- [x] 批量合并算法：按地址连续性合并请求，按 PDU 上限切分
- [x] 值编解码：字节序（ABCD/CDAB/BADC/DCBA）、线性换算、S7 STRING
- [x] `Rung.Drivers.S7`：异步传输层、连接管理、读写执行
- [x] CLI：配置文件驱动的采集与打印
- [x] 采集内核：退避重连、按组独立调度、点位缓存、写命令插队、死区过滤
- [x] 多设备编排：一个进程管多台设备，按业务名路由写命令
- [x] 设备模拟器：活信号 + 故障注入，无需真机即可验证全链路
- [x] Redis 北向输出：最新值 Hash、变化 Pub/Sub、设备状况
- [x] REST + SSE 接口，带 OpenAPI 文档
- [ ] MQTT 输出
- [ ] SQLite 配置存储 + Excel 导入导出（现在还是 JSON 文件）
- [x] `Rung.Drivers.Modbus`：基于 FluentModbus，支持多从站与四种地址写法
- [x] Web UI：点位实时值、设备状况、手动读写
- [x] 打包：Linux 单文件自包含发布 + systemd 单元 + Dockerfile

## 许可

MIT。第三方代码的来源与义务见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。
