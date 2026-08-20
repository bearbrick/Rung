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
/// 支持<b>在线重载</b>：改了配置不必重启进程，只有配置真的变了的设备
/// 才会被重启，其余原地继续跑。加一个点位就把全线设备断一遍重连，
/// 代价比它要解决的问题还大。
/// </para>
/// <para>
/// 对外只按<b>业务点位名</b>寻址。应用侧写 <c>Line1.Oven3.Setpoint</c>，
/// 网关自己知道该找哪台设备的哪个地址。
/// </para>
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    private readonly Dictionary<string, IDeviceDriverFactory> _factories;
    private readonly Dictionary<string, ManagedDevice> _devices = new(StringComparer.Ordinal);
    private readonly List<DeviceRegistration> _pending = [];
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    private readonly TagCache _cache;
    private readonly IReadOnlyList<ITagSink> _sinks;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GatewayHost> _logger;

    /// <summary>点位名到设备的路由。整体替换而不是就地改，读方无需加锁。</summary>
    private Dictionary<string, (DeviceWorker Worker, TagDef Tag)> _tagRoutes =
        new(StringComparer.Ordinal);

    private CancellationTokenSource? _hostCancellation;
    private bool _running;
    private bool _disposed;

    /// <summary>创建一个网关宿主。</summary>
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

    /// <summary>当前在跑的设备数。</summary>
    public int DeviceCount => Volatile.Read(ref _running) is true ? _devices.Count : _pending.Count;

    /// <summary>全部设备的运行状况，按设备名排序。</summary>
    public IReadOnlyList<DeviceStatus> DeviceStatuses
        => [.. _devices.Values.Select(static managed => managed.Worker.Status)
            .OrderBy(static status => status.DeviceId, StringComparer.Ordinal)];

    /// <summary>全部点位定义，按业务名排序。</summary>
    public IReadOnlyList<TagDef> AllTags
        => [.. Volatile.Read(ref _tagRoutes).Values.Select(static route => route.Tag)
            .OrderBy(static tag => tag.Name, StringComparer.Ordinal)];

    /// <summary>启动之前登记一台设备。启动之后请改用 <see cref="ReloadAsync"/>。</summary>
    /// <exception cref="RungException">协议未注册，或点位名与已有设备冲突。</exception>
    public void AddDevice(
        DeviceOptions deviceOptions,
        IReadOnlyList<TagDef> tags,
        DeviceWorkerOptions? workerOptions = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_running)
        {
            throw new RungException("网关已经启动，请改用 ReloadAsync 在线更新配置");
        }

        var registration = new DeviceRegistration(deviceOptions, tags, workerOptions);
        Validate([.. _pending, registration]);

        _pending.Add(registration);
    }

    /// <summary>
    /// 启动所有设备的采集循环，直到取消。
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pending.Count == 0)
        {
            throw new RungException("没有注册任何设备");
        }

        _running = true;
        _hostCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await ApplyAsync([.. _pending], CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, _hostCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
        finally
        {
            await StopAllAsync().ConfigureAwait(false);
            _running = false;
        }
    }

    /// <summary>
    /// 在线应用一份新配置。
    /// <para>
    /// 只有签名变了的设备会被停掉重启，其余原地继续跑，采集不中断。
    /// 被移除的设备连同它的缓存点位一起清掉。
    /// </para>
    /// </summary>
    /// <exception cref="RungException">协议未注册，或点位名重复。校验失败时不会有任何改动。</exception>
    public async Task<ReloadResult> ReloadAsync(
        IReadOnlyList<DeviceRegistration> registrations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return await ApplyAsync(registrations, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReloadResult> ApplyAsync(
        IReadOnlyList<DeviceRegistration> registrations,
        CancellationToken cancellationToken)
    {
        // 先整体校验再动手：校验失败时配置应当原封不动，
        // 而不是留下一个"改了一半"的网关
        Validate(registrations);

        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var desired = registrations.ToDictionary(static r => r.DeviceId, StringComparer.Ordinal);
            var added = new List<string>();
            var restarted = new List<string>();
            var removed = new List<string>();
            var unchanged = new List<string>();

            foreach (var deviceId in _devices.Keys.ToList())
            {
                if (!desired.TryGetValue(deviceId, out var wanted))
                {
                    await StopAsync(deviceId).ConfigureAwait(false);
                    _cache.RemoveDevice(deviceId);
                    removed.Add(deviceId);
                    continue;
                }

                if (string.Equals(_devices[deviceId].Registration.Signature, wanted.Signature,
                    StringComparison.Ordinal))
                {
                    unchanged.Add(deviceId);
                    continue;
                }

                await StopAsync(deviceId).ConfigureAwait(false);
                _cache.RemoveDevice(deviceId);
                Start(wanted);
                restarted.Add(deviceId);
            }

            foreach (var registration in registrations.Where(r => !_devices.ContainsKey(r.DeviceId)))
            {
                Start(registration);
                added.Add(registration.DeviceId);
            }

            RebuildRoutes();

            var result = new ReloadResult(added, restarted, removed, unchanged);
            if (result.HasChanges)
            {
                Log.ConfigReloaded(
                    _logger, added.Count, restarted.Count, removed.Count, unchanged.Count);
            }

            return result;
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>
    /// 整体校验。业务点位名必须全局唯一——重名会让写命令路由到错误的设备上，
    /// 这种事故在产线上代价很大。
    /// </summary>
    private void Validate(IReadOnlyList<DeviceRegistration> registrations)
    {
        var seenDevices = new HashSet<string>(StringComparer.Ordinal);
        var seenTags = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var registration in registrations)
        {
            if (!_factories.ContainsKey(registration.Options.Protocol))
            {
                var known = string.Join("、", _factories.Keys.Order(StringComparer.Ordinal));
                throw new RungException(
                    $"未注册的协议 \"{registration.Options.Protocol}\"，已注册的有：{known}");
            }

            if (!seenDevices.Add(registration.DeviceId))
            {
                throw new RungException($"设备标识 \"{registration.DeviceId}\" 重复");
            }

            foreach (var tag in registration.Tags.Where(static tag => tag.Enabled))
            {
                if (seenTags.TryGetValue(tag.Name, out var owner))
                {
                    throw new RungException(
                        $"点位名 \"{tag.Name}\" 在设备 {owner} 和 {registration.DeviceId} 中重复定义，"
                        + "业务名必须全局唯一");
                }

                seenTags[tag.Name] = registration.DeviceId;
            }
        }
    }

    private void Start(DeviceRegistration registration)
    {
        var factory = _factories[registration.Options.Protocol];

        var worker = new DeviceWorker(
            factory,
            registration.Options,
            registration.Tags,
            _cache,
            _sinks,
            registration.WorkerOptions,
            _loggerFactory.CreateLogger<DeviceWorker>());

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _hostCancellation?.Token ?? CancellationToken.None);

        var loop = RunIsolatedAsync(worker, cancellation.Token);

        _devices[registration.DeviceId] = new ManagedDevice(registration, worker, cancellation, loop);
    }

    private async Task StopAsync(string deviceId)
    {
        if (!_devices.Remove(deviceId, out var managed))
        {
            return;
        }

        await managed.Cancellation.CancelAsync().ConfigureAwait(false);

        try
        {
            await managed.Loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 预期内
        }

        await managed.Worker.DisposeAsync().ConfigureAwait(false);
        managed.Cancellation.Dispose();
    }

    private async Task StopAllAsync()
    {
        foreach (var deviceId in _devices.Keys.ToList())
        {
            await StopAsync(deviceId).ConfigureAwait(false);
        }

        RebuildRoutes();
    }

    /// <summary>整体替换路由表。引用赋值是原子的，读方不需要加锁。</summary>
    private void RebuildRoutes()
    {
        var routes = new Dictionary<string, (DeviceWorker, TagDef)>(StringComparer.Ordinal);

        foreach (var managed in _devices.Values)
        {
            foreach (var tag in managed.Registration.Tags.Where(static tag => tag.Enabled))
            {
                routes[tag.Name] = (managed.Worker, tag);
            }
        }

        Volatile.Write(ref _tagRoutes, routes);
    }

    /// <summary>按业务点位名下发写命令，返回回读到的设备实际值。</summary>
    /// <exception cref="RungException">点位名不存在。</exception>
    public Task<TagValue> WriteAsync(
        string tagName,
        TagValue value,
        CancellationToken cancellationToken,
        string caller = "unknown")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Volatile.Read(ref _tagRoutes).TryGetValue(tagName, out var route))
        {
            throw new RungException($"未知的点位名 \"{tagName}\"");
        }

        return route.Worker.WriteAsync(route.Tag, value, cancellationToken, caller);
    }

    /// <summary>该业务点位挂在哪台设备上。</summary>
    /// <exception cref="RungException">点位名不存在。</exception>
    public string DeviceIdOf(string tagName)
        => Volatile.Read(ref _tagRoutes).TryGetValue(tagName, out var route)
            ? route.Worker.Status.DeviceId
            : throw new RungException($"未知的点位名 \"{tagName}\"");

    /// <summary>按业务名取得点位定义。写接口靠它决定如何解释传入的值。</summary>
    public bool TryGetTag(string tagName, out TagDef tag)
    {
        if (Volatile.Read(ref _tagRoutes).TryGetValue(tagName, out var route))
        {
            tag = route.Tag;
            return true;
        }

        tag = null!;
        return false;
    }

    /// <summary>按设备标识取得运行状况。</summary>
    public bool TryGetStatus(string deviceId, out DeviceStatus status)
    {
        if (_devices.TryGetValue(deviceId, out var managed))
        {
            status = managed.Worker.Status;
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
        catch (OperationCanceledException)
        {
            // 停机或被重载停掉
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

        if (_hostCancellation is { } host)
        {
            await host.CancelAsync().ConfigureAwait(false);
        }

        await StopAllAsync().ConfigureAwait(false);

        _hostCancellation?.Dispose();
        _reloadGate.Dispose();
        _pending.Clear();
    }

    private sealed record ManagedDevice(
        DeviceRegistration Registration,
        DeviceWorker Worker,
        CancellationTokenSource Cancellation,
        Task Loop);
}
