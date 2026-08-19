namespace Rung.Configuration.Storage;

/// <summary>
/// 配置来源。
/// <para>
/// JSON 文件适合小规模和版本控制，SQLite 适合点位多、要在界面上改的场景。
/// 抽象出来是为了让宿主不必关心配置从哪来。
/// </para>
/// </summary>
public interface IConfigStore
{
    /// <summary>该来源的可读描述，用于启动日志。</summary>
    string Description { get; }

    /// <summary>加载完整配置。</summary>
    Task<RungConfig> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>从 JSON 文件加载。</summary>
public sealed class JsonConfigStore(string path) : IConfigStore
{
    /// <inheritdoc/>
    public string Description => $"JSON 文件 {path}";

    /// <inheritdoc/>
    public Task<RungConfig> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RungConfig.Load(path));
    }
}
