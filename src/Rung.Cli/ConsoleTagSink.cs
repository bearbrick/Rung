using System.Globalization;
using Rung.Abstractions;
using Rung.Core;

namespace Rung.Cli;

/// <summary>把变化的点位打到控制台。MVP 阶段的北向输出，将来会被 Redis / MQTT 取代。</summary>
public sealed class ConsoleTagSink(TextWriter output) : ITagSink
{
    /// <inheritdoc/>
    public ValueTask PublishAsync(IReadOnlyList<TagSnapshot> changed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changed);

        foreach (var snapshot in changed)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:HH:mm:ss.fff}  {snapshot.Tag.Name,-28} {Format(snapshot.Value),18}"));
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>按数据类型选择合适的显示形式。</summary>
    public static string Format(TagValue value)
    {
        if (value.Quality is TagQuality.Uninitialized)
        {
            return "—";
        }

        if (value.Quality is not (TagQuality.Good or TagQuality.Stale))
        {
            return $"<{value.Quality}>";
        }

        var text = value.DataType switch
        {
            TagDataType.Float32 or TagDataType.Float64 =>
                value.AsDouble().ToString("0.###", CultureInfo.InvariantCulture),
            TagDataType.Bool => value.AsBool() ? "true" : "false",
            TagDataType.String => value.AsString(),
            TagDataType.Bytes => Convert.ToHexString(value.AsBytes()),
            _ => value.AsInt64().ToString(CultureInfo.InvariantCulture),
        };

        // 陈旧值仍然给出来，但要让人一眼看出它不是当前值
        return value.Quality == TagQuality.Stale ? $"{text} (陈旧)" : text;
    }
}
