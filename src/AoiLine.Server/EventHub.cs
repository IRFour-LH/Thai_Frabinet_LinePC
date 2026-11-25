using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AoiLine.Server;

/// <summary>
/// Live updates for the web pages (Server-Sent Events): when something changes on a line,
/// every open page is told the line id and reloads that line's data.
/// </summary>
public sealed class EventHub
{
    private readonly ConcurrentDictionary<Channel<string>, byte> _subscribers = new();

    public Channel<string> Subscribe()
    {
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[ch] = 0;
        return ch;
    }

    public void Unsubscribe(Channel<string> ch) => _subscribers.TryRemove(ch, out _);

    /// <summary>"*" = everything changed (e.g. settings).</summary>
    public void Publish(string lineId)
    {
        foreach (var ch in _subscribers.Keys) ch.Writer.TryWrite(lineId);
    }

    public int SubscriberCount => _subscribers.Count;
}
