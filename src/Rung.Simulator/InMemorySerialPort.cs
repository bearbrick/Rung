using System.Threading.Channels;
using FluentModbus;

namespace Rung.Simulator;

/// <summary>
/// 一对互联的内存串口。
/// <para>
/// 串口设备不可能进 CI，而 <c>socat</c> 之类的虚拟串口工具也不是每台机器都有。
/// FluentModbus 的客户端和服务端都接受自定义的 <see cref="IModbusRtuSerialPort"/>，
/// 于是可以用两个内存管道把它们对接起来——<b>走的是真实的 RTU 帧，含 CRC 校验</b>，
/// 只是底下的物理层换成了内存。
/// </para>
/// <para>
/// 能测到的：帧格式、CRC、从站寻址、功能码、多从站共线。
/// <b>测不到的</b>：波特率、校验位、RS-485 收发切换时序、线缆干扰——
/// 这些只有真串口能验。
/// </para>
/// </summary>
public sealed class InMemorySerialPortPair : IDisposable
{
    private readonly Channel<byte> _aToB = Channel.CreateUnbounded<byte>();
    private readonly Channel<byte> _bToA = Channel.CreateUnbounded<byte>();

    private bool _disposed;

    /// <summary>创建一对互联的串口。</summary>
    public InMemorySerialPortPair(string name = "mem")
    {
        A = new InMemorySerialPort($"{name}-A", _bToA.Reader, _aToB.Writer);
        B = new InMemorySerialPort($"{name}-B", _aToB.Reader, _bToA.Writer);
    }

    /// <summary>一端。A 写的东西 B 能读到。</summary>
    public InMemorySerialPort A { get; }

    /// <summary>另一端。</summary>
    public InMemorySerialPort B { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _aToB.Writer.TryComplete();
        _bToA.Writer.TryComplete();
    }
}

/// <summary>内存串口的一端。</summary>
public sealed class InMemorySerialPort(
    string portName,
    ChannelReader<byte> input,
    ChannelWriter<byte> output) : IModbusRtuSerialPort
{
    /// <inheritdoc/>
    public string PortName => portName;

    /// <inheritdoc/>
    public bool IsOpen { get; private set; }

    /// <summary>读超时，毫秒。</summary>
    public int ReadTimeout { get; set; } = 1000;

    /// <summary>写超时，毫秒。未使用——内存管道不会阻塞在写上。</summary>
    public int WriteTimeout { get; set; } = 1000;

    /// <inheritdoc/>
    public void Open() => IsOpen = true;

    /// <inheritdoc/>
    public void Close() => IsOpen = false;

    /// <inheritdoc/>
    public int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ReadTimeout);

        // 串口语义是"有多少给多少，至少给一个"，不是"读满为止"。
        // 按读满实现会把 RTU 的帧结束判定卡死——它靠的正是读不满
        try
        {
            var first = await input.ReadAsync(timeout.Token).ConfigureAwait(false);
            buffer[offset] = first;

            var read = 1;
            while (read < count && input.TryRead(out var next))
            {
                buffer[offset + read] = next;
                read++;
            }

            return read;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"串口 {portName} 读取超时");
        }
    }

    /// <inheritdoc/>
    public void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        for (var i = 0; i < count; i++)
        {
            await output.WriteAsync(buffer[offset + i], token).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Close();
}
