using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Rung.Simulator;

/// <summary>
/// 一个最小的 Redis 服务器，说 RESP2 协议。
/// <para>
/// 存在的理由和 S7 模拟器一样：验证北向输出不该要求开发机上装 Redis。
/// 它实现的是 Rung 真正会用到的那一小撮命令，外加 StackExchange.Redis
/// 握手时必需的几条（PING / ECHO / INFO / CONFIG GET / CLIENT）。
/// </para>
/// <para>
/// <b>不是 Redis 的替代品</b>：没有持久化、没有过期、没有集群，
/// 数据全在内存里，进程一停就没了。它只用于测试和演示。
/// </para>
/// </summary>
public sealed class RedisSimulatorServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private readonly Lock _gate = new();

    private readonly Dictionary<string, Dictionary<string, string>> _hashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private readonly List<(string Channel, string Message)> _published = [];
    private readonly List<string> _commandLog = [];

    private bool _disposed;

    /// <summary>在回环地址上启动。端口传 0 由系统分配。</summary>
    public RedisSimulatorServer(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    /// <summary>实际监听端口。</summary>
    public int Port { get; }

    /// <summary>连接字符串，直接喂给 StackExchange.Redis。</summary>
    public string ConnectionString => $"127.0.0.1:{Port},connectTimeout=5000,syncTimeout=5000";

    /// <summary>累计处理的命令数。</summary>
    public int CommandCount { get; private set; }

    /// <summary>收到的命令流水，排查客户端握手时非常有用。</summary>
    public IReadOnlyList<string> CommandLog
    {
        get
        {
            lock (_gate)
            {
                return [.. _commandLog];
            }
        }
    }

    /// <summary>读取一个哈希的全部字段，用于断言。</summary>
    public IReadOnlyDictionary<string, string> GetHash(string key)
    {
        lock (_gate)
        {
            return _hashes.TryGetValue(key, out var hash)
                ? new Dictionary<string, string>(hash, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>列出全部键名，用于断言键的命名方案。</summary>
    public IReadOnlyList<string> Keys()
    {
        lock (_gate)
        {
            return [.. _hashes.Keys.Concat(_strings.Keys).Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>取出所有发布过的消息，用于验证变化推送。</summary>
    public IReadOnlyList<(string Channel, string Message)> Published()
    {
        lock (_gate)
        {
            return [.. _published];
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => ServeAsync(client, cancellationToken), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // 正常停机
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            var reader = new RespReader(stream);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var raw = await reader.ReadCommandAsync(cancellationToken).ConfigureAwait(false);
                    if (raw is null)
                    {
                        return;
                    }

                    var command = new RedisCommand(raw);

                    byte[] response;
                    lock (_gate)
                    {
                        CommandCount++;
                        _commandLog.Add(command.ToString());
                        response = Execute(command);
                    }

                    await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException
                                          or EndOfStreamException)
            {
                // 客户端断开
            }
        }
    }

    private byte[] Execute(RedisCommand command)
        => command.Name switch
        {
            // ECHO 必须逐字节回显：StackExchange.Redis 用它校验连接，
            // 载荷是二进制，任何字符编码往返都会把它毁掉
            "ECHO" => Bulk(command.Raw(1)),
            "PING" => command.Count > 1 ? Bulk(command.Raw(1)) : Simple("PONG"),

            "SELECT" or "SUBSCRIBE" or "UNSUBSCRIBE" => Simple("OK"),
            "CLIENT" => ExecuteClient(command),

            // 回错误让 StackExchange.Redis 退回 RESP2。协议实现越少越不容易出错
            "HELLO" => Error("ERR unknown command 'HELLO'"),

            "INFO" => Bulk(BuildInfo()),
            "CONFIG" => ExecuteConfigGet(command),
            "COMMAND" => Array([]),
            "CLUSTER" => Error("ERR This instance has cluster support disabled"),
            "SENTINEL" => Error("ERR unknown command 'SENTINEL'"),

            "SET" => ExecuteSet(command),
            "GET" => _strings.TryGetValue(command.Text(1), out var value) ? Bulk(value) : NullBulk(),
            "DEL" => Integer(DeleteKeys(command)),
            "EXISTS" => Integer(
                _hashes.ContainsKey(command.Text(1)) || _strings.ContainsKey(command.Text(1)) ? 1 : 0),

            // HMSET 是 HSET 的老写法，StackExchange.Redis 在多字段写入时仍会用它
            "HSET" => Integer(ExecuteHashSet(command)),
            "HMSET" => ExecuteHashSet(command) >= 0 ? Simple("OK") : Simple("OK"),
            "HMGET" => ExecuteHashMultiGet(command),
            "HLEN" => Integer(_hashes.TryGetValue(command.Text(1), out var target) ? target.Count : 0),
            "KEYS" => ExecuteKeys(command),
            "EXPIRE" or "PEXPIRE" => Integer(1),
            "TTL" or "PTTL" => Integer(-1),
            "FLUSHDB" or "FLUSHALL" => ExecuteFlush(),
            "HGET" => ExecuteHashGet(command),
            "HGETALL" => ExecuteHashGetAll(command),
            "HDEL" => Integer(ExecuteHashDelete(command)),

            "PUBLISH" => ExecutePublish(command),

            _ => Error($"ERR unknown command '{command.Name}'"),
        };

    /// <summary>CLIENT ID 必须回整数，回 +OK 会让客户端解析失败。</summary>
    private static byte[] ExecuteClient(RedisCommand command)
        => command.Count > 1 && string.Equals(command.Text(1), "ID", StringComparison.OrdinalIgnoreCase)
            ? Integer(1)
            : Simple("OK");

    /// <summary>CONFIG GET 要回 [名, 值] 数组。databases 至少得给一个，否则客户端不知道能选几号库。</summary>
    private static byte[] ExecuteConfigGet(RedisCommand command)
    {
        if (command.Count < 3 || !string.Equals(command.Text(1), "GET", StringComparison.OrdinalIgnoreCase))
        {
            return Array([]);
        }

        return command.Text(2).ToLowerInvariant() switch
        {
            "databases" => Array(["databases", "16"]),
            "replica-read-only" => Array(["replica-read-only", "no"]),
            "timeout" => Array(["timeout", "0"]),
            _ => Array([]),
        };
    }

    private byte[] ExecuteSet(RedisCommand command)
    {
        _strings[command.Text(1)] = command.Text(2);
        return Simple("OK");
    }

    private int DeleteKeys(RedisCommand command)
    {
        var removed = 0;
        for (var i = 1; i < command.Count; i++)
        {
            removed += _hashes.Remove(command.Text(i)) || _strings.Remove(command.Text(i)) ? 1 : 0;
        }

        return removed;
    }

    private int ExecuteHashSet(RedisCommand command)
    {
        if (!_hashes.TryGetValue(command.Text(1), out var hash))
        {
            hash = new Dictionary<string, string>(StringComparer.Ordinal);
            _hashes[command.Text(1)] = hash;
        }

        var added = 0;
        for (var i = 2; i + 1 < command.Count; i += 2)
        {
            added += hash.ContainsKey(command.Text(i)) ? 0 : 1;
            hash[command.Text(i)] = command.Text(i + 1);
        }

        return added;
    }

    private byte[] ExecuteHashMultiGet(RedisCommand command)
    {
        _hashes.TryGetValue(command.Text(1), out var hash);

        var values = new List<string>(command.Count - 2);
        for (var i = 2; i < command.Count; i++)
        {
            values.Add(hash is not null && hash.TryGetValue(command.Text(i), out var value) ? value : string.Empty);
        }

        return Array(values);
    }

    /// <summary>只支持 <c>prefix*</c> 这一种通配，够测试用了。</summary>
    private byte[] ExecuteKeys(RedisCommand command)
    {
        var pattern = command.Text(1);
        var prefix = pattern.EndsWith('*') ? pattern[..^1] : pattern;

        var matched = _hashes.Keys.Concat(_strings.Keys)
            .Where(key => pattern.EndsWith('*')
                ? key.StartsWith(prefix, StringComparison.Ordinal)
                : string.Equals(key, pattern, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        return Array(matched);
    }

    private byte[] ExecuteFlush()
    {
        _hashes.Clear();
        _strings.Clear();
        _published.Clear();

        return Simple("OK");
    }

    private byte[] ExecuteHashGet(RedisCommand command)
        => _hashes.TryGetValue(command.Text(1), out var hash) && hash.TryGetValue(command.Text(2), out var value)
            ? Bulk(value)
            : NullBulk();

    private byte[] ExecuteHashGetAll(RedisCommand command)
    {
        if (!_hashes.TryGetValue(command.Text(1), out var hash))
        {
            return Array([]);
        }

        var flat = new List<string>(hash.Count * 2);
        foreach (var (field, value) in hash)
        {
            flat.Add(field);
            flat.Add(value);
        }

        return Array(flat);
    }

    private int ExecuteHashDelete(RedisCommand command)
    {
        if (!_hashes.TryGetValue(command.Text(1), out var hash))
        {
            return 0;
        }

        var removed = 0;
        for (var i = 2; i < command.Count; i++)
        {
            removed += hash.Remove(command.Text(i)) ? 1 : 0;
        }

        return removed;
    }

    private byte[] ExecutePublish(RedisCommand command)
    {
        _published.Add((command.Text(1), command.Text(2)));
        return Integer(0);
    }

    /// <summary>StackExchange.Redis 会解析 INFO 来判断服务器类型和版本。</summary>
    private static string BuildInfo() => string.Join("\r\n",
        "# Server",
        "redis_version:7.2.0",
        "redis_mode:standalone",
        "os:Rung.Simulator",
        "arch_bits:64",
        "run_id:rungsimulator0000000000000000000000000",
        "tcp_port:0",
        "",
        "# Clients",
        "connected_clients:1",
        "",
        "# Replication",
        "role:master",
        "connected_slaves:0",
        "",
        "# Keyspace",
        "");

    private static byte[] Simple(string value) => Encoding.UTF8.GetBytes($"+{value}\r\n");

    private static byte[] Error(string message) => Encoding.UTF8.GetBytes($"-{message}\r\n");

    private static byte[] Integer(long value)
        => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $":{value}\r\n"));

    private static byte[] NullBulk() => "$-1\r\n"u8.ToArray();

    private static byte[] Bulk(string value) => Bulk(Encoding.UTF8.GetBytes(value));

    private static byte[] Bulk(ReadOnlySpan<byte> payload)
    {
        var header = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"${payload.Length}\r\n"));

        var buffer = new byte[header.Length + payload.Length + 2];
        header.CopyTo(buffer, 0);
        payload.CopyTo(buffer.AsSpan(header.Length));
        buffer[^2] = (byte)'\r';
        buffer[^1] = (byte)'\n';

        return buffer;
    }

    private static byte[] Array(List<string> items)
    {
        var builder = new ArrayBufferWriter<byte>();
        var header = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"*{items.Count}\r\n"));

        builder.Write(header);
        foreach (var item in items)
        {
            builder.Write(Bulk(item));
        }

        return builder.WrittenSpan.ToArray();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 预期内
        }

        _shutdown.Dispose();
    }
}

/// <summary>
/// 一条已解析的命令。参数保留原始字节，只在需要时才按 UTF-8 解释——
/// ECHO 之类的命令载荷是二进制，经不起编码往返。
/// </summary>
internal readonly struct RedisCommand(List<byte[]> parts)
{
    /// <summary>命令名，大写。</summary>
    public string Name { get; } = Encoding.ASCII.GetString(parts[0]).ToUpperInvariant();

    /// <summary>参数个数，含命令名。</summary>
    public int Count => parts.Count;

    /// <summary>按 UTF-8 解释第 <paramref name="index"/> 个参数。</summary>
    public string Text(int index) => Encoding.UTF8.GetString(parts[index]);

    /// <summary>取第 <paramref name="index"/> 个参数的原始字节。</summary>
    public ReadOnlySpan<byte> Raw(int index) => parts[index];

    /// <inheritdoc/>
    public override string ToString()
        => string.Join(' ', parts.Select(static part => Encoding.UTF8.GetString(part)));
}

/// <summary>RESP2 请求解析：客户端发来的永远是"批量字符串数组"。</summary>
internal sealed class RespReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[64 * 1024];

    /// <summary>读出下一条命令；连接关闭时返回 null。</summary>
    public async Task<List<byte[]>?> ReadCommandAsync(CancellationToken cancellationToken)
    {
        var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            return null;
        }

        // 兼容内联命令（telnet 手敲时会用到），正式客户端一律走数组形式
        if (line.Length == 0 || line[0] != '*')
        {
            return [.. line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Encoding.UTF8.GetBytes)];
        }

        var count = int.Parse(line[1..], CultureInfo.InvariantCulture);
        var parts = new List<byte[]>(count);

        for (var i = 0; i < count; i++)
        {
            var header = await ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("命令在批量字符串头部被截断");

            var length = int.Parse(header[1..], CultureInfo.InvariantCulture);
            if (length < 0)
            {
                parts.Add([]);
                continue;
            }

            await stream.ReadExactlyAsync(_buffer.AsMemory(0, length + 2), cancellationToken)
                .ConfigureAwait(false);

            parts.Add(_buffer.AsSpan(0, length).ToArray());
        }

        return parts;
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var used = 0;

        while (true)
        {
            var read = await stream.ReadAsync(_buffer.AsMemory(used, 1), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                return used == 0 ? null : Encoding.UTF8.GetString(_buffer, 0, used);
            }

            if (_buffer[used] == (byte)'\n')
            {
                var length = used > 0 && _buffer[used - 1] == (byte)'\r' ? used - 1 : used;
                return Encoding.UTF8.GetString(_buffer, 0, length);
            }

            used++;
        }
    }
}
