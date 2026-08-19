using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Rung.Drivers.Modbus.Tests;

/// <summary>
/// 一个可以随时掐断的 TCP 转发代理。
/// <para>
/// 用来确定性地模拟"链路突然消失"。直接停掉 <c>ModbusTcpServer</c> 并不可靠——
/// 它不保证立刻断开已建立的连接，测试会时灵时不灵。把断链这件事握在自己手里，
/// 就不必再跟第三方库的停机语义较劲。
/// </para>
/// <para>拔网线不可重复，<see cref="Cut"/> 可以。</para>
/// </summary>
internal sealed class TcpLinkProxy : IDisposable
{
    private readonly TcpListener _listener;
    private readonly IPEndPoint _target;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentBag<TcpClient> _connections = [];

    private bool _disposed;

    public TcpLinkProxy(int targetPort)
    {
        _target = new IPEndPoint(IPAddress.Loopback, targetPort);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    /// <summary>代理监听的端口，客户端连这里。</summary>
    public int Port { get; }

    /// <summary>掐断所有已建立的连接，模拟链路中断。</summary>
    public void Cut()
    {
        while (_connections.TryTake(out var connection))
        {
            try
            {
                connection.Close();
            }
            catch (SocketException)
            {
                // 已经断了
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var inbound = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _connections.Add(inbound);

                _ = Task.Run(() => PumpAsync(inbound, cancellationToken), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // 正常停机
        }
    }

    private async Task PumpAsync(TcpClient inbound, CancellationToken cancellationToken)
    {
        using var outbound = new TcpClient();

        try
        {
            await outbound.ConnectAsync(_target, cancellationToken).ConfigureAwait(false);
            _connections.Add(outbound);

            var upstream = inbound.GetStream().CopyToAsync(outbound.GetStream(), cancellationToken);
            var downstream = outbound.GetStream().CopyToAsync(inbound.GetStream(), cancellationToken);

            await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                      or ObjectDisposedException)
        {
            // 任一端断开即结束
        }
        finally
        {
            inbound.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        Cut();
        _listener.Stop();
        _shutdown.Dispose();
    }
}
