using Rung.Abstractions;
using Rung.Core;

namespace Rung.Host;

/// <summary>当前请求的调用方身份。</summary>
/// <param name="Name">调用方名称，未认证时为 <c>anonymous</c>。</param>
/// <param name="CanWrite">是否允许写点位。</param>
public sealed record Caller(string Name, bool CanWrite)
{
    /// <summary>未提供密钥的匿名调用方。</summary>
    public static Caller Anonymous { get; } = new("anonymous", CanWrite: false);
}

/// <summary>
/// API 密钥认证。
/// <para>
/// 刻意不上 ASP.NET Core Identity 或 JWT：网关只需要"这个调用方是谁、能不能写"
/// 两个信息，为此背一整套身份体系不划算，而且离线内网里也没有颁发方。
/// </para>
/// </summary>
public sealed class ApiKeyAuth(IReadOnlyList<ApiKey> keys, bool requireForReads)
{
    /// <summary>密钥请求头。也接受 <c>Authorization: Bearer &lt;key&gt;</c>。</summary>
    public const string HeaderName = "X-Rung-Key";

    /// <summary>已配置的密钥数量。</summary>
    public int KeyCount => keys.Count;

    /// <summary>读接口是否要求密钥。</summary>
    public bool RequireForReads => requireForReads;

    /// <summary>
    /// 写接口是否可用。
    /// <para>
    /// 一个密钥都没配时写接口整个关闭——<b>失败要往关的方向倒</b>。
    /// 配置漏了就把 PLC 写权限对全网敞开，是这类系统最典型的事故成因。
    /// </para>
    /// </summary>
    public bool WriteEnabled => keys.Any(static key => key.CanWrite);

    /// <summary>识别请求的调用方。</summary>
    public Caller Identify(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var presented = ReadKey(request);
        var matched = ApiKeys.Find(keys, presented);

        return matched is null ? Caller.Anonymous : new Caller(matched.Name, matched.CanWrite);
    }

    private static string? ReadKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue(HeaderName, out var header)
            && header.ToString() is { Length: > 0 } value)
        {
            return value;
        }

        var authorization = request.Headers.Authorization.ToString();

        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
    }
}

/// <summary>把认证挂到端点上的过滤器。</summary>
public static class ApiKeyAuthExtensions
{
    private const string CallerKey = "rung.caller";

    /// <summary>取出当前请求的调用方。写审计日志靠它记下"是谁写的"。</summary>
    public static Caller GetCaller(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(CallerKey, out var value) && value is Caller caller
            ? caller
            : Caller.Anonymous;
    }

    /// <summary>要求写权限。</summary>
    public static TBuilder RequireWrite<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(async (context, next) =>
        {
            var auth = context.HttpContext.RequestServices.GetRequiredService<ApiKeyAuth>();

            if (!auth.WriteEnabled)
            {
                return TypedResults.Problem(
                    "写接口未启用：配置里没有任何具备写权限的 API 密钥。"
                    + " 用 rung config key add <名称> --write 生成一个。",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var caller = auth.Identify(context.HttpContext.Request);
            context.HttpContext.Items[CallerKey] = caller;

            if (!caller.CanWrite)
            {
                // 未授权的写尝试是安全审计里最该看到的信号
                var audit = context.HttpContext.RequestServices.GetRequiredService<IWriteAuditLog>();
                await audit.RecordAsync(new WriteAuditRecord(
                    DateTime.UtcNow,
                    caller.Name,
                    "unknown",
                    context.HttpContext.Request.Path.Value ?? string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    null,
                    Success: false,
                    "未授权：密钥缺失或不具备写权限"), context.HttpContext.RequestAborted)
                    .ConfigureAwait(false);

                return TypedResults.Problem(
                    $"需要具备写权限的 API 密钥。请在 {ApiKeyAuth.HeaderName} 头里提供。",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            return await next(context).ConfigureAwait(false);
        });

    /// <summary>要求读权限。配置里没打开 RequireForReads 时放行。</summary>
    public static TBuilder RequireRead<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(async (context, next) =>
        {
            var auth = context.HttpContext.RequestServices.GetRequiredService<ApiKeyAuth>();
            var caller = auth.Identify(context.HttpContext.Request);
            context.HttpContext.Items[CallerKey] = caller;

            if (auth.RequireForReads && caller == Caller.Anonymous)
            {
                return TypedResults.Problem(
                    $"需要 API 密钥。请在 {ApiKeyAuth.HeaderName} 头里提供。",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            return await next(context).ConfigureAwait(false);
        });
}
