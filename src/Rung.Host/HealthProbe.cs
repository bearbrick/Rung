using System.Globalization;

namespace Rung.Host;

/// <summary>
/// <c>--healthcheck</c> 探针：请求本机的健康接口，用退出码回答容器编排器。
/// <para>
/// 只认 HTTP 200，不看 <c>status</c> 字段是 healthy 还是 degraded——
/// 有设备掉线时网关本身是好的，重启它不但没用，还会把其他正常设备的采集一起中断。
/// 设备级的告警交给监控系统去做。
/// </para>
/// </summary>
internal static class HealthProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var url = ReadOption(args, "--url") ?? "http://127.0.0.1:5580/api/health";
        var timeout = TimeSpan.FromSeconds(5);

        using var client = new HttpClient { Timeout = timeout };

        try
        {
            using var response = await client.GetAsync(new Uri(url)).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return 0;
            }

            await Console.Error.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"健康检查失败：{url} 返回 {(int)response.StatusCode}")).ConfigureAwait(false);

            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"健康检查失败：{url} 无法访问（{ex.Message}）")
                .ConfigureAwait(false);

            return 1;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
