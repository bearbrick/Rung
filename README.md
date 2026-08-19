# Rung

轻量级 PLC 数据采集网关 · A lightweight PLC data acquisition gateway for .NET

把西门子 S7、三菱 MC、欧姆龙 FINS、Modbus 设备的点位可视化配置好，
采集到的数据通过 REST、MQTT 或 Redis 供上层系统使用。单文件部署，无外部依赖。

> **状态：v0.1 开发中。** 当前仓库包含驱动契约层和 S7 协议编解码层，
> 尚不能独立运行。路线图见下方。

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

**每个协议独立选型。** 驱动层通过 `IDeviceDriver` 抽象，Modbus 直接用
FluentModbus，S7 / MC / FINS 走自己的移植实现。不把身家压在任何单一上游库上。

## 仓库结构

```
src/
  Rung.Abstractions/       驱动契约层。第三方按此接口实现驱动即可接入
  Rung.Protocols.S7/       S7comm 纯编解码：地址解析、报文组包、响应解析、批量合并
tests/
  Rung.Protocols.S7.Tests/ 报文夹具 + 字节级断言
third_party/IoTClient/     上游溯源与许可证
docs/                      设计与操作文档
```

## 开发

```bash
dotnet test
```

需要 .NET 10 SDK。测试走 Microsoft.Testing.Platform（.NET 10 起 `dotnet test` 的默认路径），
运行器在 `global.json` 中声明。

## 路线图

- [x] 驱动契约层 `IDeviceDriver` / `TagDef` / `TagValue`
- [x] S7 协议编解码 + 报文夹具测试
- [x] 批量合并算法：按地址连续性合并请求，按 PDU 上限切分
- [ ] 值解码器：字节序（ABCD/CDAB/BADC/DCBA）、线性换算、S7 STRING 头部
- [ ] `Rung.Drivers.S7`：异步传输层、连接管理、重连状态机
- [ ] `Rung.Drivers.Modbus`：基于 FluentModbus
- [ ] `Rung.Core`：SQLite 配置存储、采集调度、点位缓存、写命令队列
- [ ] 北向输出：Redis / REST / SSE / MQTT
- [ ] Web UI：设备列表、点位实时值、手动读写测试
- [ ] 打包：Docker 多架构镜像 + Linux 单文件自包含发布

## 许可

MIT。第三方代码的来源与义务见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。
