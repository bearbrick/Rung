using System.Net;
using System.Net.Sockets;
using FluentModbus;

namespace Rung.Drivers.Modbus.Tests;

/// <summary>
/// 测试用的 Modbus 从站，直接用 FluentModbus 自带的服务端实现。
/// <para>
/// 这正是当初选 FluentModbus 而不是自己实现 Modbus 的主要理由之一：
/// 整条链路可以在没有真实设备的情况下自动化验证。
/// </para>
/// </summary>
internal sealed class ModbusTestServer : IDisposable
{
    /// <summary>
    /// 进程内单调递增的端口分配。
    /// <para>
    /// 不用"绑一个 0 号端口、拿到端口再释放"的常见写法：释放到再次绑定之间有个窗口，
    /// 而被停掉的 <see cref="ModbusTcpServer"/> 未必立刻交还端口。结果是新服务端
    /// 以为自己起来了，客户端实际上还在跟上一个测试的残留服务端说话——
    /// 表现为"对端已经销毁但读取依然成功，且返回全 0"，极难定位。
    /// </para>
    /// <para>同一进程内绝不复用端口号，这个问题就从根上没了。</para>
    /// </summary>
    private static int _nextPort = Random.Shared.Next(21000, 42000);

    private readonly ModbusTcpServer _server = new();
    private bool _disposed;

    public ModbusTestServer(params byte[] unitIds)
    {
        Port = StartOnFreePort();

        foreach (var unitId in unitIds.Length > 0 ? unitIds : [(byte)1])
        {
            _server.AddUnit(unitId);
        }
    }

    public int Port { get; }

    /// <summary>
    /// 往保持寄存器写入原始字节。
    /// <para>
    /// <b>必须走字节缓冲区而不是 <c>GetHoldingRegisters()</c> 的 Span&lt;short&gt;。</b>
    /// 后者是主机字节序，在小端机器上写 0x1122 会在线上变成 22 11——
    /// 用它布置测试数据，等于给自己造了一个字节序错觉，
    /// 测试通过但真机上全是反的。
    /// </para>
    /// </summary>
    public void SetHoldingRegisterBytes(byte unitId, int registerOffset, params byte[] wireBytes)
    {
        lock (_server.Lock)
        {
            wireBytes.CopyTo(_server.GetHoldingRegisterBuffer(unitId)[(registerOffset * 2)..]);
        }
    }

    /// <summary>往输入寄存器写入原始字节。</summary>
    public void SetInputRegisterBytes(byte unitId, int registerOffset, params byte[] wireBytes)
    {
        lock (_server.Lock)
        {
            wireBytes.CopyTo(_server.GetInputRegisterBuffer(unitId)[(registerOffset * 2)..]);
        }
    }

    /// <summary>
    /// 设置一个线圈。
    /// <para>
    /// <b>线圈缓冲区是按位打包的</b>：<c>GetCoils(u)[n]</c> 里的 n 是<b>字节</b>索引，
    /// 不是线圈号。写 <c>GetCoils(u)[1] = 1</c> 实际置起的是线圈 8。
    /// 和寄存器缓冲区的字节序一样，是个不会报错的静默陷阱。
    /// </para>
    /// </summary>
    public void SetCoil(byte unitId, int offset, bool value)
    {
        lock (_server.Lock)
        {
            SetBit(_server.GetCoilBuffer(unitId), offset, value);
        }
    }

    /// <summary>设置一个离散输入。同样是按位打包。</summary>
    public void SetDiscreteInput(byte unitId, int offset, bool value)
    {
        lock (_server.Lock)
        {
            SetBit(_server.GetDiscreteInputBuffer(unitId), offset, value);
        }
    }

    private static void SetBit(Span<byte> buffer, int bitIndex, bool value)
    {
        var mask = (byte)(1 << (bitIndex % 8));
        var index = bitIndex / 8;

        buffer[index] = value ? (byte)(buffer[index] | mask) : (byte)(buffer[index] & ~mask);
    }

    /// <summary>读回保持寄存器的原始字节，用于验证写命令。</summary>
    public byte[] GetHoldingRegisterBytes(byte unitId, int registerOffset, int registerCount)
    {
        lock (_server.Lock)
        {
            return _server.GetHoldingRegisterBuffer(unitId)
                .Slice(registerOffset * 2, registerCount * 2).ToArray();
        }
    }

    /// <summary>读回一个线圈。</summary>
    public bool GetCoil(byte unitId, int offset)
    {
        lock (_server.Lock)
        {
            return (_server.GetCoilBuffer(unitId)[offset / 8] & (1 << (offset % 8))) != 0;
        }
    }

    /// <summary>依次尝试下一个端口，直到绑定成功。</summary>
    private int StartOnFreePort()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var port = Interlocked.Increment(ref _nextPort);

            try
            {
                _server.Start(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException)
            {
                // 端口被进程外的东西占了，换下一个
            }
        }

        throw new InvalidOperationException("连续 100 个端口都无法绑定");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _server.Stop();
        _server.Dispose();
    }
}
