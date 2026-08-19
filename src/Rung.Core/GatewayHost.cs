using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rung.Abstractions;

namespace Rung.Core;

/// <summary>
/// 多设备编排：一个进程管理若干台设备，每台一个 <see cref="DeviceWorker"/>。
/// <para>
/// 设备之间完全独立——一台掉线只影响它自己，其余照常采集。
/// 这是网关最基本的隔离要求：产线上总有那么一两台设备状态不好，
/// 不能让它们拖垮整个采集服务。
/// </para>
/// <para>
/// 对外只按<b>业务点位名</b>寻址。应用侧写 <c>Line1.Oven3.Setpoint</c>，
/// 网关自己知道该找哪台设备的哪个地址。
/// </para>
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    private readonly Dictionary<string, IDeviceDriverFactory> _factories;
    private readonly Dictionary<string, DeviceWorker> _workers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DeviceWorker Worker, TagDef Tag)> _tagRoutes =
        new(StringComparer.Ordinal);

    private readonly TagCache _cache;
    private readonly IReadOnlyList<ITagSink> _sinks;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GatewayHost> _logger;

    private bool _running;
    private bool _disposed;

    /// <summary>创建一个网关宿主。</summary>
    /// <param name="factories">按协议名注册的驱动工厂。</param>
    /// <param name="cache">共享的点位缓存。</param>
    /// <param name="sinks">北向输出。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public GatewayHost(
        IEnumerable<IDeviceDriverFactory> factories,
        TagCache cache,
        IReadOnlyList<ITagSink>? sinks = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(factories);
        ArgumentNullException.ThrowIfNull(cache);

        _factories = factories.ToDictionary(
            static factory => factory.Protocol, StringComparer.OrdinalIgnoreCase);
        _cache = cache;
        _sinks = sinks ?? [];
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<GatewayHost>();
    }

    /// <summary>共享的点位缓存。北向接口从这里读。</summary>
    public TagCache Cache => _cache;

    /// <summary>已注册的设备数。</summary>
    public int DeviceCount => _workers.Count;

    /// <summary>全部设备的运行状况，按设备名排序。</summary>
    public IReadOnlyList<DeviceStatus> DeviceStatuses
        => [.. _workers.Values.Select(static worker => worker.Status)
            .OrderBy(static status => status.DeviceId, StringComparer.Ordinal)];

    /// <summary>
    /// 注册一台设备。必须在 <see cref="RunAsync"/> 之前调用。
    /// </summary>
    /// <exception cref="RungException">协议未注册，或点位名与已有设备冲突。</exception>
    public void AddDevice(
        DeviceOptions deviceOptions,
        IReadOnlyList<TagDef> tags,
        DeviceWorkerOptions? workerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(deviceOptions);
        ArgumentNullException.ThrowIfNull(tags);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_running)
        {
            throw new RungException("网关已经启动，不支持在运行中增删设备");
        }

        if (!_factories.TryGetValue(deviceOptions.Protocol, out var factory))
        {
            var known = string.Join("、", _factories.Keys.Order(StringComparer.Ordinal));
            throw new RungException($"未注册的协议 \"{deviceOptions.Protocol}\"，已注册的有：{known}");
        }

        if (!_workers.TryAdd(deviceOptions.DeviceId, null!))
        {
            throw new RungException($"设备标识 \"{deviceOptions.DeviceId}\" 重复");
        }

        // 业务点位名必须全局唯一——整个设计的前提就是应用侧只认这个名字。
        // 重名会让写命令路由到错误的设备上，这种事故在产线上代价很大
        foreach (var tag in tags.Where(static tag => tag.Enabled))
        {
            if (_tagRoutes.ContainsKey(tag.Name))
            {
                _workers.Remove(deviceOptions.DeviceId);
                throw new RungException(
                    $"点位名 \"{tag.Name}\" 在设备 {deviceOptions.DeviceId} 中重复定义，业务名必须全局唯一");
            }

            _tagRoutes[tag.Name] = (null!, tag);
        }

        var worker = new DeviceWorker(
            factory,
            deviceOptions,
            tags,
            _cache,
            _sinks,
            workerOptions,
            _loggerFactory.CreateLogger<DeviceWorker>());

        _workers[deviceOptions.DeviceId] = worker;

        foreach (var tag in tags.Where(static tag => tag.Enabled))
        {
            _tagRoutes[tag.Name] = (worker, tag);
        }
    }

    /// <summary>
    /// 启动所有设备的采集循环，直到取消。
    /// <para>
    /// 一台设备的工作循环即便抛出未预期的异常也不会影响其他设备——
    /// <see cref="DeviceWorker.RunAsync"/> 本身包含重连循环，正常只会因取消而结束。
    /// </para>
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_workers.Count == 0)
        {
            throw new RungException("没有注册任何设备");
        }

        _running = true;

        var loops = _workers.Values
            .Select(worker => RunIsolatedAsync(worker, cancellationToken))
            .ToArray();

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    /// <summary>
    /// 按业务点位名下发写命令，自动路由到对应设备，返回回读到的设备实际值。
    /// </summary>
    /// <exception cref="RungException">点位名不存在。</exception>
    public Task<TagValue> WriteAsync(string tagName, TagValue value, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_tagRoutes.TryGetValue(tagName, out var route))
        {
            throw new RungException($"未知的点位名 \"{tagName}\"");
        }

        return route.Worker.WriteAsync(route.Tag, value, cancellationToken);
    }

    /// <summary>该业务点位挂在哪台设备上。</summary>
    /// <exception cref="RungException">点位名不存在。</exception>
    public string DeviceIdOf(string tagName)
        => _tagRoutes.TryGetValue(tagName, out var route)
            ? route.Worker.Status.DeviceId
            : throw new RungException($"未知的点位名 \"{tagName}\"");

    /// <summary>按业务名取得点位定义。写接口要靠它决定如何解释传入的值。</summary>
    public bool TryGetTag(string tagName, out TagDef tag)
    {
        if (_tagRoutes.TryGetValue(tagName, out var route))
        {
            tag = route.Tag;
            return true;
        }

        tag = null!;
        return false;
    }

    /// <summary>全部点位定义，按业务名排序。</summary>
    public IReadOnlyList<TagDef> AllTags
        => [.. _tagRoutes.Values.Select(static route => route.Tag)
            .OrderBy(static tag => tag.Name, StringComparer.Ordinal)];

    /// <summary>按设备标识取得运行状况。</summary>
    public bool TryGetStatus(string deviceId, out DeviceStatus status)
    {
        if (_workers.TryGetValue(deviceId, out var worker))
        {
            status = worker.Status;
            return true;
        }

        status = null!;
        return false;
    }

    private async Task RunIsolatedAsync(DeviceWorker worker, CancellationToken cancellationToken)
    {
        try
        {
            await worker.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 停机
        }
        catch (Exception ex)
        {
            // 走到这里说明重连循环本身出了意外。记下来，但绝不能因此让整个网关退出
            Log.DeviceLoopCrashed(_logger, ex, worker.Status.DeviceId);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var worker in _workers.Values)
        {
            if (worker is not null)
            {
                await worker.DisposeAsync().ConfigureAwait(false);
            }
        }

        _workers.Clear();
        _tagRoutes.Clear();
    }
}
