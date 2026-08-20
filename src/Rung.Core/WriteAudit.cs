using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rung.Core;

/// <summary>一条写操作的审计记录。</summary>
/// <param name="TimestampUtc">操作时刻，UTC。</param>
/// <param name="Caller">调用方名称，来自 API 密钥。</param>
/// <param name="DeviceId">设备标识。</param>
/// <param name="TagName">业务点位名。</param>
/// <param name="Address">协议地址。排障时手头只有审计文件也能直接看懂。</param>
/// <param name="DataType">数据类型。拆成独立字段而不是拼进值里——
/// 选 JSON Lines 就是为了机器也能解析，让消费方去拆 "266 [Float64]" 这种复合串
/// 是把方便留给自己、麻烦留给别人。</param>
/// <param name="Requested">请求写入的工程值。</param>
/// <param name="Actual">写完从设备回读到的实际值；失败时为空。</param>
/// <param name="Success">是否成功。</param>
/// <param name="Error">失败原因。</param>
public sealed record WriteAuditRecord(
    DateTime TimestampUtc,
    string Caller,
    string DeviceId,
    string TagName,
    string Address,
    string DataType,
    string Requested,
    string? Actual,
    bool Success,
    string? Error);

/// <summary>
/// 写操作审计日志。
/// <para>
/// 独立于普通日志落盘，因为它的全部价值在于"出事之后能查到"——
/// 混在每秒都在刷的采集日志里，等于没有。
/// </para>
/// </summary>
public interface IWriteAuditLog
{
    /// <summary>记录一次写操作。</summary>
    ValueTask RecordAsync(WriteAuditRecord record, CancellationToken cancellationToken);

    /// <summary>读取最近的记录，最新的在前。</summary>
    ValueTask<IReadOnlyList<WriteAuditRecord>> ReadRecentAsync(
        int limit, CancellationToken cancellationToken);
}

/// <summary>不记录任何东西。未配置审计时的默认实现。</summary>
public sealed class NullWriteAuditLog : IWriteAuditLog
{
    /// <summary>单例。</summary>
    public static NullWriteAuditLog Instance { get; } = new();

    /// <inheritdoc/>
    public ValueTask RecordAsync(WriteAuditRecord record, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<WriteAuditRecord>> ReadRecentAsync(
        int limit, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<WriteAuditRecord>>([]);
}

/// <summary>
/// 以 JSON Lines 落盘的审计日志，按天分文件。
/// <para>
/// <b>为什么是 JSON Lines：</b>一行一条记录，既能 <c>grep</c> 也能程序解析，
/// 追加写不需要读取已有内容，文件被截断也只损坏最后一行。
/// 换成 JSON 数组或 XML，任何一种都会让"直接 tail 看最近发生了什么"变得不可能。
/// </para>
/// <para>
/// <b>为什么按天分文件：</b>查审计的场景永远是"某天某个时间段出了事"，
/// 按天切正好对上。按大小切会让同一天的记录散在多个文件里。
/// </para>
/// </summary>
public sealed class JsonLinesWriteAuditLog : IWriteAuditLog, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;

    private DateOnly _lastCleanup = DateOnly.MinValue;

    /// <summary>创建一个审计日志。</summary>
    /// <param name="directory">存放目录，不存在会自动创建。</param>
    /// <param name="retentionDays">保留天数，0 表示永久保留。</param>
    /// <param name="timeProvider">时间源。</param>
    public JsonLinesWriteAuditLog(string directory, int retentionDays = 365, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _directory = directory;
        _retentionDays = retentionDays;
        _time = timeProvider ?? TimeProvider.System;

        Directory.CreateDirectory(directory);
    }

    /// <summary>某一天的审计文件路径。</summary>
    public string FilePathFor(DateOnly day)
        => Path.Combine(_directory, $"write-audit-{day:yyyy-MM-dd}.jsonl");

    /// <inheritdoc/>
    public async ValueTask RecordAsync(WriteAuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var day = DateOnly.FromDateTime(record.TimestampUtc);
        var line = JsonSerializer.Serialize(record, SerializerOptions);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var path = FilePathFor(day);

            // 上一次写到一半被杀会留下没有换行的半行。不补这个换行，
            // 下一条记录会直接接在它后面，把好记录也一起毁掉——
            // 一次崩溃损坏两条，而不是一条
            var prefix = await EndsWithNewlineAsync(path, cancellationToken).ConfigureAwait(false)
                ? string.Empty
                : "\n";

            // 每条都立刻落盘。写命令是低频的操作员动作，
            // 为此攒批换吞吐没有意义——而进程崩掉时丢掉的正是最该查的那条
            await File.AppendAllTextAsync(path, prefix + line + '\n', cancellationToken)
                .ConfigureAwait(false);

            CleanupIfNeeded(day);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<WriteAuditRecord>> ReadRecentAsync(
        int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var records = new List<WriteAuditRecord>(limit);

        // 从今天往回翻，凑够 limit 条就停。查审计几乎总是看最近的
        var day = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

        for (var back = 0; back < 90 && records.Count < limit; back++)
        {
            var path = FilePathFor(day.AddDays(-back));
            if (!File.Exists(path))
            {
                continue;
            }

            var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);

            for (var i = lines.Length - 1; i >= 0 && records.Count < limit; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<WriteAuditRecord>(lines[i], SerializerOptions)
                        is { } parsed)
                    {
                        records.Add(parsed);
                    }
                }
                catch (JsonException)
                {
                    // 进程在写到一半时被杀会留下半行。跳过它继续读——
                    // 因为一行坏了就放弃整个文件，是审计日志最不该有的行为
                }
            }
        }

        return records;
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    /// <summary>文件是否以换行结尾。空文件或不存在都视为是。</summary>
    private static async ValueTask<bool> EndsWithNewlineAsync(
        string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            return true;
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        stream.Seek(-1, SeekOrigin.End);

        var last = new byte[1];
        await stream.ReadExactlyAsync(last, cancellationToken).ConfigureAwait(false);

        return last[0] == (byte)'\n';
    }

    /// <summary>按保留天数清理旧文件。每天只做一次。</summary>
    private void CleanupIfNeeded(DateOnly today)
    {
        if (_retentionDays <= 0 || _lastCleanup == today)
        {
            return;
        }

        _lastCleanup = today;
        var cutoff = today.AddDays(-_retentionDays);

        foreach (var path in Directory.EnumerateFiles(_directory, "write-audit-*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var datePart = name["write-audit-".Length..];

            if (DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day)
                && day < cutoff)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // 删不掉就留着，比因此中断写操作强
                }
            }
        }
    }
}
