using System.Globalization;
using System.Text;
using Rung.Abstractions;
using Rung.Core;

namespace Rung.Host;

/// <summary>
/// 把网关状态渲染成 Prometheus 文本暴露格式。
/// <para>
/// 手写而不是引 OpenTelemetry 的导出器：需要暴露的就这十来个指标，
/// 格式本身也只有三行规则。为此多背一整套依赖树不划算，
/// 而且"单文件、无外部依赖"这句话每多一个包就软一分。
/// </para>
/// <para>纯函数，因此格式细节（转义、类型声明、时间戳单位）都能脱离 HTTP 单独测。</para>
/// </summary>
public static class PrometheusFormatter
{
    /// <summary>指标名前缀，与服务名、Redis key 前缀保持一致。</summary>
    public const string Prefix = "rung";

    /// <summary>渲染全部指标。</summary>
    public static string Render(
        IReadOnlyList<DeviceStatus> devices,
        TagCache cache,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(cache);

        var builder = new StringBuilder(1024);

        Gauge(builder, "up", "网关进程存活", static () => "1", noLabels: true);

        Metric(builder, "device_up", "gauge",
            "设备连接状态：1 已连接，0 未连接",
            devices, static d => d.State == DriverState.Connected ? 1 : 0);

        Metric(builder, "device_consecutive_failures", "gauge",
            "连续失败次数，连接成功后归零",
            devices, static d => d.ConsecutiveFailures);

        Metric(builder, "device_reconnects_total", "counter",
            "累计重连次数",
            devices, static d => d.ReconnectCount);

        Metric(builder, "device_overruns_total", "counter",
            "采集超时次数：一轮还没跑完下一轮就到点了。持续增长说明周期太快或点位需拆组",
            devices, static d => d.OverrunCount);

        Metric(builder, "device_poll_duration_seconds", "gauge",
            "最近一轮采集耗时",
            devices, static d => d.LastPollDuration.TotalSeconds);

        Metric(builder, "device_tags", "gauge",
            "参与采集的点位数",
            devices, static d => d.ActiveTagCount);

        Metric(builder, "device_requests", "gauge",
            "每轮实际发出的请求次数。与点位数一起看即知批量合并效果",
            devices, static d => d.RequestCount);

        Metric(builder, "device_issues", "gauge",
            "配置有误、未参与采集的点位数",
            devices, static d => d.Issues.Count);

        // 用"距今多少秒"而不是绝对时间戳：Prometheus 里 rate() 和告警都更好写，
        // 也不必关心两端时钟是否对齐
        Metric(builder, "device_last_success_age_seconds", "gauge",
            "距上次成功采集过去了多少秒。从未成功过时为 -1",
            devices, d => d.LastSuccessUtc is { } success
                ? Math.Max(0, (nowUtc - success).TotalSeconds)
                : -1);

        var snapshots = cache.Snapshot();
        Gauge(builder, "tags_total", "缓存中的点位总数",
            () => Number(snapshots.Count), noLabels: true);
        Gauge(builder, "tags_good", "质量为 Good 的点位数",
            () => Number(snapshots.Count(static s => s.Value.Quality == TagQuality.Good)), noLabels: true);

        return builder.ToString();
    }

    private static void Metric<T>(
        StringBuilder builder,
        string name,
        string type,
        string help,
        IReadOnlyList<DeviceStatus> devices,
        Func<DeviceStatus, T> selector)
        where T : struct
    {
        Header(builder, name, type, help);

        foreach (var device in devices)
        {
            builder.Append(Prefix).Append('_').Append(name)
                .Append("{device=\"").Append(EscapeLabel(device.DeviceId)).Append("\"} ")
                .Append(Number(selector(device)))
                .Append('\n');
        }
    }

    private static void Gauge(
        StringBuilder builder, string name, string help, Func<string> value, bool noLabels)
    {
        _ = noLabels;

        Header(builder, name, "gauge", help);
        builder.Append(Prefix).Append('_').Append(name).Append(' ').Append(value()).Append('\n');
    }

    private static void Header(StringBuilder builder, string name, string type, string help)
        => builder.Append("# HELP ").Append(Prefix).Append('_').Append(name).Append(' ')
            .Append(EscapeHelp(help)).Append('\n')
            .Append("# TYPE ").Append(Prefix).Append('_').Append(name).Append(' ')
            .Append(type).Append('\n');

    /// <summary>Prometheus 只认小数点，且不接受 NaN/Infinity 以外的特殊写法。</summary>
    private static string Number<T>(T value) where T : struct
        => value switch
        {
            double d => d.ToString("0.######", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0",
        };

    /// <summary>标签值里必须转义反斜杠、双引号和换行，否则会破坏整个抓取结果。</summary>
    private static string EscapeLabel(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>HELP 文本里只需转义反斜杠和换行。</summary>
    private static string EscapeHelp(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
