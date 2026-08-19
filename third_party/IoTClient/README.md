# IoTClient 上游溯源

Rung 的 S7 / 三菱 MC / 欧姆龙 FINS 协议编解码逻辑，参考并移植自 IoTClient。

| 项 | 值 |
|---|---|
| 上游仓库 | https://github.com/zhaopeiym/IoTClient |
| 作者 | 农码一生 (benny) |
| 许可证 | MIT（见同目录 `LICENSE`） |
| 参照提交 | `dbab448e299a8f4d8103571d3723ef47e5656030` |
| 提交时间 | 2026-05-12 |

## 为什么是移植而不是引用

IoTClient 是一个纯托管、无原生依赖的实现，唯一的 NuGet 依赖是串口用的
`System.IO.Ports`，协议报文全部由 `System.Net.Sockets.Socket` 手工拼装。
这个实现质量足以作为起点，但它的**全部 API 都是同步阻塞的**——
`SiemensClient.cs` 里 `async` 出现 0 次。

采集网关需要的是相反的东西：

- 上百台设备各自持有长连接，同步阻塞意味着上百个线程卡在 `Socket.Receive`
- 阻塞的 socket 读取无法用 `CancellationToken` 取消，优雅停机、配置热重载都得靠关 socket 抛异常来实现
- `Result<T>` + `LoggerDelegate` 与 `ILogger` / DI / 异常语义对不上
- 目标框架 `netstandard2.0` 拿不到 `Span<T>` 与 `ArrayPool` 的收益

这些都不是包装一层能解决的，必须改到骨架。因此采取的做法是**只移植一半**：

| 层 | 来源 | 说明 |
|---|---|---|
| 协议编解码 | 移植自 IoTClient | 报文组包、地址解析、响应解析——多年逆向积累的知识 |
| 传输层 | Rung 自研 | 异步 socket、连接管理、批量合并、重连状态机 |

移植过来的部分被重写成**无 IO 的纯函数**（见 `src/Rung.Protocols.S7/`），
因而可以用真实报文夹具做字节级断言。这是 Rung 相对上游最实质的改进：
协议实现的正确性从"看起来能跑"变成"每个字节都被测试锁住"。

## 许可证义务

IoTClient 采用 MIT，允许修改和商业使用，但要求保留版权声明。Rung 的做法：

1. 本目录保留上游 `LICENSE` 原文
2. 仓库根目录的 `THIRD-PARTY-NOTICES.md` 汇总所有第三方代码
3. 每个移植过来的源文件头部标注来源

## 需要对照上游源码时

```bash
git clone https://github.com/zhaopeiym/IoTClient.git /tmp/iotclient
git -C /tmp/iotclient checkout dbab448e299a8f4d8103571d3723ef47e5656030
```

上游源码**不纳入本仓库**：我们没有跟踪它的上游更新，参照的是某一时刻的实现，
把它当作一次性的知识转移，而不是一个持续演进的依赖。
