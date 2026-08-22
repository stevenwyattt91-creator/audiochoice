using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AudioChoice.Api.Processing;

public interface IScanJobQueue
{
    bool TryQueue(Guid scanID);
    ValueTask<Guid> Dequeue(CancellationToken cancellationToken);
    void Renew(Guid scanID);
    void Complete(Guid scanID);
}

public sealed class ScanJobQueue : IScanJobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly ConcurrentDictionary<Guid, byte> _scheduled = new();

    public bool TryQueue(Guid scanID)
    {
        if (!_scheduled.TryAdd(scanID, 0))
        {
            return false;
        }

        if (_channel.Writer.TryWrite(scanID))
        {
            return true;
        }

        _scheduled.TryRemove(scanID, out _);
        return false;
    }

    public ValueTask<Guid> Dequeue(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);

    public void Complete(Guid scanID) =>
        _scheduled.TryRemove(scanID, out _);

    public void Renew(Guid scanID) { }
}
