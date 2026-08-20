namespace Rung.Abstractions;

/// <summary>驱动的连接状态。</summary>
public enum DriverState : byte
{
    /// <summary>未连接。</summary>
    Disconnected = 0,

    /// <summary>正在建立连接。</summary>
    Connecting = 1,

    /// <summary>已连接，可以收发。</summary>
    Connected = 2,

    /// <summary>连接已失效，等待重连状态机处理。</summary>
    Faulted = 3,
}

/// <summary>
/// 一份预编译的读取计划。
/// <para>
/// 地址解析和"按地址连续性合并请求"这两件事开销不小，绝不能每个采集周期重做一遍。
/// 把计划提升为契约的一等公民，是为了让每个驱动作者都必须显式地做这件事。
/// </para>
/// </summary>
public interface IReadPlan
{
    /// <summary>计划覆盖的点位，顺序与 <see cref="IDeviceDriver.ExecuteAsync"/> 的目标数组一一对应。</summary>
    IReadOnlyList<TagDef> Tags { get; }

    /// <summary>
    /// 编译计划时发现的配置问题。有问题的点位不参与采集，每轮被置为
    /// <see cref="TagQuality.ConfigError"/>，其余点位不受影响。
    /// </summary>
    IReadOnlyList<TagIssue> Issues { get; }

    /// <summary>
    /// 合并之后真正发往设备的请求次数。
    /// 暴露出来是为了让 Web UI 能显示"128 个点位 → 3 次请求"，
    /// 现场调优采集周期时这个数字比什么都直观。
    /// </summary>
    int RequestCount { get; }
}

/// <summary>
/// 设备驱动契约。一个实例对应一台设备的一条长连接。
/// <para>
/// <b>实现约定：所有成员都不要求线程安全。</b>调用方（采集调度器）负责把同一设备的
/// 读、写请求串行化到单一队列上——读写共用一条通道，写命令插队。
/// 驱动作者因此可以放心使用可变的内部缓冲区。
/// </para>
/// </summary>
public interface IDeviceDriver : IAsyncDisposable
{
    /// <summary>设备唯一标识。</summary>
    string DeviceId { get; }

    /// <summary>当前连接状态。</summary>
    DriverState State { get; }

    /// <summary>
    /// 单次报文能承载的最大字节数。未连接时返回 0。
    /// <para>
    /// 叫「单帧上限」而不是「PDU」，因为只有 S7 真的会协商一个 PDU 长度；
    /// Modbus、MELSEC、FINS 的上限都是协议写死的。把 S7 的术语套到所有协议上，
    /// 现场看到「Modbus 设备 PDU 250 字节」只会犯嘀咕——功能没错，但用词误导。
    /// </para>
    /// <para>
    /// 合并算法依赖它决定切分点，因此必须在 <see cref="ConnectAsync"/> 之后才有效。
    /// </para>
    /// </summary>
    int MaxFrameBytes { get; }

    /// <summary>建立连接并完成协议协商。失败时抛出异常，由上层重连状态机决定退避策略。</summary>
    ValueTask ConnectAsync(CancellationToken cancellationToken);

    /// <summary>主动断开连接。实现必须保证可重复调用。</summary>
    ValueTask DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 编译一份读取计划：解析地址、按连续性合并请求、按 PDU 上限切分。
    /// 点位配置变更时重新调用，正常采集周期内复用同一份计划。
    /// <para>
    /// 单个点位的配置错误不会中断整份计划，而是记入 <see cref="IReadPlan.Issues"/>。
    /// 只有当整批点位都无法编译时才应抛出异常。
    /// </para>
    /// </summary>
    IReadPlan CreateReadPlan(IReadOnlyList<TagDef> tags);

    /// <summary>
    /// 执行一份读取计划，结果按下标写入 <paramref name="destination"/>。
    /// <para>
    /// 单个点位失败不应中断整批：把该点位标记为 <see cref="TagQuality.DeviceError"/> 继续即可。
    /// 只有链路级故障才抛异常。
    /// </para>
    /// </summary>
    /// <returns>质量为 <see cref="TagQuality.Good"/> 的点位数量。</returns>
    ValueTask<int> ExecuteAsync(IReadPlan plan, TagValue[] destination, CancellationToken cancellationToken);

    /// <summary>写入单个点位。写命令是事件驱动的，由调度器插队进设备队列。</summary>
    ValueTask WriteAsync(TagDef tag, TagValue value, CancellationToken cancellationToken);
}

/// <summary>
/// 驱动工厂。Core 层通过协议名查找工厂来创建驱动，因此不必引用任何具体驱动程序集——
/// 这是第三方扩展协议的接入点。
/// </summary>
public interface IDeviceDriverFactory
{
    /// <summary>协议标识，与 <see cref="DeviceOptions.Protocol"/> 匹配，大小写不敏感。</summary>
    string Protocol { get; }

    /// <summary>该协议地址格式的说明，用于 Web UI 的配置提示。</summary>
    string AddressSyntaxHint { get; }

    /// <summary>创建一个驱动实例。此时不应发起任何网络 IO。</summary>
    IDeviceDriver Create(DeviceOptions options);

    /// <summary>
    /// <b>不连接设备</b>地编译一份读取计划，用于离线校验配置。
    /// <para>
    /// 地址解析、类型与地址宽度是否匹配、批量合并成几次请求——这些全是纯逻辑，
    /// 没有理由等到现场连上设备才发现。出差前跑一遍，能省掉的是
    /// "到了现场才知道点位表有二十个地址写错"。
    /// </para>
    /// <para>
    /// 需要协商参数（如 S7 的 PDU 长度）时，实现应当取<b>最保守</b>的假设：
    /// 真机协商出来只会更宽松，因此算出的请求次数是上界，
    /// 不会给人过于乐观的印象。
    /// </para>
    /// </summary>
    IReadPlan CompileOffline(DeviceOptions options, IReadOnlyList<TagDef> tags);
}
