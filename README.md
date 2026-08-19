# Rung

轻量级 PLC 数据采集网关 · A lightweight PLC data acquisition gateway for .NET

把西门子 S7、三菱 MC、欧姆龙 FINS、Modbus 设备的点位可视化配置好，
采集到的数据通过 REST、MQTT 或 Redis 供上层系统使用。单文件部署，无外部依赖。

> **状态：v0.1 MVP。** 已经是一个能挂在服务器上长期运行的采集服务：
> 断线自己按退避重连、恢复后继续采集，不需要人工重启。
> 北向输出目前只有控制台，Redis / MQTT / Web UI 还在路上。

## 跑起来（不需要 PLC）

仓库自带一个西门子 S7 设备模拟器。开两个终端：

```bash
dotnet run --project src/Rung.Simulator -- samples/simulator.json
```

```bash
dotnet run --project src/Rung.Cli -- samples/gateway.json
```

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

## 模拟器

没有真机也能把整条链路验证完，这是 `Rung.Simulator` 存在的理由。

**信号是活的**：`sine` / `ramp` / `counter` / `toggle` / `randomwalk` / `constant`。
死值只能验证"链路通不通"，会变化的信号才能验证死区过滤、变化推送这些真正会出问题的地方。
随机游走用固定种子，因而**可复现**——排查时能重放出一模一样的数据序列。

**故障可以注入**：拒绝连接、应答延迟、收发 N 次后断开、每隔若干秒断一次、
指定 DB 返回"对象不存在"、拒绝写命令。拔网线不可重复，这些开关可以。

**报文编码是独立实现的**，不引用 Rung 的任何代码。两边同源的话，一个写错的偏移量
会同时体现在模拟器和被测代码上，测试全绿但真机一读就错。独立实现才能互为对照。

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

**每个协议独立选型。** 驱动层通过 `IDeviceDriver` 抽象，Modbus 直接用
FluentModbus，S7 / MC / FINS 走自己的移植实现。不把身家压在任何单一上游库上。

## 仓库结构

```
src/
  Rung.Abstractions/       驱动契约层。第三方按此接口实现驱动即可接入
  Rung.Protocols.S7/       S7comm 纯编解码：地址解析、报文组包、响应解析、批量合并
  Rung.Drivers.S7/         S7 驱动：异步传输、连接管理、读写执行
  Rung.Core/               采集内核：连接生命周期、退避重连、调度、缓存、写队列
  Rung.Cli/                命令行入口（MVP 的可执行形态）
  Rung.Simulator/          西门子 S7 设备模拟器：活信号 + 故障注入
samples/                   配置文件示例
tests/
  Rung.Protocols.S7.Tests/ 报文夹具 + 字节级断言
  Rung.Drivers.S7.Tests/   进程内假 S7 设备 + 端到端链路测试
  Rung.Core.Tests/         可编程假驱动 + 调度、重连、多设备编排测试
  Rung.Simulator.Tests/    信号源与地址解析
third_party/IoTClient/     上游溯源与许可证
docs/                      设计与操作文档
```

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
- [ ] 北向输出：Redis / REST / SSE / MQTT
- [ ] SQLite 配置存储 + Excel 导入导出
- [ ] `Rung.Drivers.Modbus`：基于 FluentModbus
- [ ] Web UI：设备列表、点位实时值、手动读写测试
- [ ] 打包：Docker 多架构镜像 + Linux 单文件自包含发布

## 许可

MIT。第三方代码的来源与义务见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。
