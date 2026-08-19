namespace Rung.Simulator;

/// <summary>
/// 故障注入开关。
/// <para>
/// 没有真机的情况下，这是唯一能验证重连状态机、超时处理、单点失败隔离的办法。
/// 拔网线不可重复，这些开关可以。
/// </para>
/// </summary>
public sealed class FaultInjection
{
    /// <summary>拒绝所有 COTP 连接，模拟机架槽号配错或 PLC 连接资源耗尽。</summary>
    public bool RejectConnections { get; set; }

    /// <summary>每次应答前的延迟，毫秒。用来触发客户端超时。</summary>
    public int ResponseDelayMs { get; set; }

    /// <summary>完成指定次数的收发之后断开连接。0 表示不断。</summary>
    public int DropAfterExchanges { get; set; }

    /// <summary>每隔这么多秒主动断开一次连接。0 表示不断。</summary>
    public double DropEverySeconds { get; set; }

    /// <summary>对这些 DB 号的读取一律返回"对象不存在"，模拟点位配错。</summary>
    public HashSet<ushort> FailingDbNumbers { get; init; } = [];

    /// <summary>拒绝所有写命令。</summary>
    public bool RejectWrites { get; set; }
}
