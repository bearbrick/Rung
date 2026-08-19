using System.Collections.Concurrent;
using System.Threading.Channels;
using Rung.Core;

namespace Rung.Host;

/// <summary>
/// 把点位变化扇出给所有 SSE 订阅者。
/// <para>
/// 它本身就是一个 <see cref="ITagSink"/>，因此和 Redis 输出是平级的：
/// 采集器不知道下游有几个消费者，也不该知道。
/// </para>
/// </summary>
public sealed class TagChangeBroadcaster : ITagSink
{
    /// <summary>
    /// 每个订阅者的队列容量。
    /// <para>
    /// 队列满了<b>丢最旧的</b>，而不是阻塞。一个卡住的浏览器标签页绝不能把采集拖停——
    /// 实时视图丢几帧无所谓，产线数据采集停了是事故。
    /// </para>
    /// </summary>
    private const int SubscriberQueueCapacity = 512;

    private readonly ConcurrentDictionary<Guid, Channel<TagView>> _subscribers = new();

    /// <summary>当前订阅者数量。</summary>
    public int SubscriberCount => _subscribers.Count;

    /// <inheritdoc/>
    public ValueTask PublishAsync(IReadOnlyList<TagSnapshot> changed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changed);

        if (_subscribers.IsEmpty || changed.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        foreach (var snapshot in changed)
        {
            var view = TagView.From(snapshot);

            foreach (var subscriber in _subscribers.Values)
            {
                // 有界队列 + 丢最旧，写入永远不会失败，也永远不会阻塞采集线程
                subscriber.Writer.TryWrite(view);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>订阅变化流。调用方断开时自动清理。</summary>
    public async IAsyncEnumerable<TagView> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<TagView>(new BoundedChannelOptions(SubscriberQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        _subscribers[id] = channel;

        try
        {
            await foreach (var view in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return view;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }
}
