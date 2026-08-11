using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace EmoteLink.Relay;

/// <summary>
/// In-process fan-out for the localhost administration client. Transfer files are
/// deliberately not placed on this channel; subscribers receive metadata and use
/// the authenticated package endpoint when human review is necessary.
/// </summary>
public sealed class AdminTransferEventBroker
{
    private readonly ConcurrentDictionary<Guid, Channel<TransferAdminEvent>> subscribers = new();
    private long sequence;

    public void Publish(string eventType, TransferAdminDto transfer)
    {
        var message = new TransferAdminEvent(
            Interlocked.Increment(ref sequence),
            DateTimeOffset.UtcNow,
            eventType,
            transfer);
        foreach (var channel in subscribers.Values)
            channel.Writer.TryWrite(message);
    }

    public async IAsyncEnumerable<TransferAdminEvent> Subscribe(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<TransferAdminEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        subscribers[id] = channel;
        try
        {
            await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken))
                yield return message;
        }
        finally
        {
            subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }
}

public sealed record TransferAdminEvent(
    long Sequence,
    DateTimeOffset OccurredAt,
    string EventType,
    TransferAdminDto Transfer);
