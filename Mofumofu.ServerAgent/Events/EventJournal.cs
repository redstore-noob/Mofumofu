using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Mofumofu.RemoteProtocol;

namespace Mofumofu.ServerAgent.Events;

public sealed class EventJournal
{
    private readonly string _path;
    private readonly int _capacity;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly List<WebSocketFrame> _frames = [];
    private readonly Dictionary<Guid, Channel<WebSocketFrame>> _subscribers = [];
    private long _latest;

    public EventJournal(string path, int capacity, IClock clock)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _path = path;
        _capacity = capacity;
        _clock = clock;
        Load();
    }

    public long LatestCursor => Interlocked.Read(ref _latest);

    public async Task<WebSocketFrame> AppendAsync(Func<long, DateTimeOffset, WebSocketFrame> factory,
        CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            long cursor = _latest + 1;
            WebSocketFrame frame = factory(cursor, _clock.UtcNow);
            ValidateFrameCursor(frame, cursor);
            await PersistAppendAsync(frame, cancellationToken);
            _frames.Add(frame);
            Interlocked.Exchange(ref _latest, cursor);
            if (_frames.Count > _capacity)
            {
                _frames.RemoveRange(0, _frames.Count - _capacity);
                await CompactAsync(cancellationToken);
            }

            foreach ((Guid id, Channel<WebSocketFrame> subscriber) in _subscribers.ToArray())
            {
                if (!subscriber.Writer.TryWrite(frame))
                {
                    _subscribers.Remove(id);
                    subscriber.Writer.TryComplete(new IOException("The WebSocket consumer fell behind the journal."));
                }
            }

            return frame;
        }
        finally
        {
            _sync.Release();
        }
    }

    public Task<ConsoleFrame> AppendConsoleAsync(string line, CancellationToken cancellationToken = default) =>
        AppendAsync((cursor, sentAt) => new ConsoleFrame
        {
            Cursor = FormatCursor(cursor),
            SentAt = sentAt,
            Entries =
            [
                new ConsoleEntry
                {
                    Sequence = cursor,
                    Timestamp = sentAt,
                    Level = "INFO",
                    Source = "minecraft",
                    Message = line,
                },
            ],
        }, cancellationToken).ContinueWith(task => (ConsoleFrame)task.Result,
            cancellationToken, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public Task<EventFrame> AppendEventAsync(ServerEvent serverEvent, CancellationToken cancellationToken = default) =>
        AppendAsync((cursor, sentAt) => new EventFrame
        {
            Cursor = FormatCursor(cursor),
            SentAt = sentAt,
            Event = serverEvent,
        }, cancellationToken).ContinueWith(task => (EventFrame)task.Result,
            cancellationToken, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public async Task<JournalSubscription> OpenAsync(string? cursor,
        Func<string, SnapshotFrame> snapshotFactory, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            string latest = FormatCursor(_latest);
            List<WebSocketFrame> initial = [];
            if (cursor is null)
            {
                initial.Add(snapshotFactory(latest));
            }
            else if (!TryParseCursor(cursor, out long position) || position < 0 || position > _latest ||
                _frames.Count > 0 && position < ParseCursor(_frames[0]) - 1)
            {
                initial.Add(new ResetFrame
                {
                    Cursor = latest,
                    SentAt = _clock.UtcNow,
                    Reason = "The requested cursor is invalid or older than the retained journal.",
                    RequiresSnapshot = true,
                });
                initial.Add(snapshotFactory(latest));
            }
            else
            {
                initial.AddRange(_frames.Where(frame => ParseCursor(frame) > position));
            }

            Channel<WebSocketFrame> channel = Channel.CreateBounded<WebSocketFrame>(
                new BoundedChannelOptions(_capacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                });
            Guid id = Guid.NewGuid();
            _subscribers.Add(id, channel);
            return new JournalSubscription(initial, channel.Reader, () =>
            {
                _sync.Wait();
                try
                {
                    if (_subscribers.Remove(id))
                    {
                        channel.Writer.TryComplete();
                    }
                }
                finally
                {
                    _sync.Release();
                }
            });
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<bool> ContainsEventIdAsync(string eventId, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            return _frames.OfType<EventFrame>().Any(frame =>
                string.Equals(frame.Event.EventId, eventId, StringComparison.Ordinal));
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IReadOnlyList<ConsoleEntry>> RecentConsoleAsync(int maximum,
        CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            return _frames.OfType<ConsoleFrame>().SelectMany(frame => frame.Entries)
                .TakeLast(maximum).ToArray();
        }
        finally
        {
            _sync.Release();
        }
    }

    private void Load()
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(_path))
        {
            return;
        }

        string[] lines = File.ReadAllLines(_path);
        long previous = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            WebSocketFrame frame;
            try
            {
                frame = ProtocolJson.Deserialize<WebSocketFrame>(lines[index]);
            }
            catch when (index == lines.Length - 1)
            {
                break;
            }

            long cursor = ParseCursor(frame);
            if (cursor <= previous)
            {
                throw new InvalidDataException("The event journal cursors are not strictly increasing.");
            }

            _frames.Add(frame);
            previous = cursor;
        }

        _latest = previous;
        if (_frames.Count > _capacity)
        {
            _frames.RemoveRange(0, _frames.Count - _capacity);
            CompactAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private async Task PersistAppendAsync(WebSocketFrame frame, CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(frame) + "\n");
        await using FileStream stream = new(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private async Task CompactAsync(CancellationToken cancellationToken)
    {
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                foreach (WebSocketFrame frame in _frames)
                {
                    byte[] payload = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(frame) + "\n");
                    await stream.WriteAsync(payload, cancellationToken);
                }

                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void ValidateFrameCursor(WebSocketFrame frame, long expected)
    {
        if (ParseCursor(frame) != expected)
        {
            throw new InvalidOperationException("A journal frame did not use its assigned cursor.");
        }
    }

    private static long ParseCursor(WebSocketFrame frame) =>
        !TryParseCursor(frame.Cursor, out long cursor)
            ? throw new InvalidDataException("The journal contains an invalid cursor.")
            : cursor;

    private static bool TryParseCursor(string? value, out long cursor) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out cursor);

    private static string FormatCursor(long cursor) => cursor.ToString(CultureInfo.InvariantCulture);
}

public sealed class JournalSubscription : IDisposable
{
    private readonly Action _dispose;
    private int _disposed;

    internal JournalSubscription(IReadOnlyList<WebSocketFrame> initialFrames,
        ChannelReader<WebSocketFrame> reader, Action dispose)
    {
        InitialFrames = initialFrames;
        Reader = reader;
        _dispose = dispose;
    }

    public IReadOnlyList<WebSocketFrame> InitialFrames { get; }
    public ChannelReader<WebSocketFrame> Reader { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _dispose();
        }
    }
}
