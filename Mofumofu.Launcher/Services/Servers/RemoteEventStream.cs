using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mofumofu.RemoteProtocol;

namespace Mofumofu.Launcher.Services.Servers;

public sealed class RemoteEventStream : IDisposable
{
    private const int MaxFrameBytes = 2 * 1024 * 1024;
    private readonly RemoteApiClient _api;
    private readonly Func<string> _token;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _historyGate = new();
    private readonly List<ConsoleEntry> _history = [];
    private readonly Task _runTask;
    private string _cursor = string.Empty;
    private bool _replaySnapshot;

    public RemoteEventStream(RemoteApiClient api, Func<string> token)
    {
        _api = api;
        _token = token;
        _runTask = RunAsync();
    }

    public event EventHandler<ServerConsoleEventArgs>? ConsoleLine;
    public event EventHandler? ConsoleReset;
    public event EventHandler<StatusResponse>? SnapshotReceived;
    public event EventHandler<ServerEvent>? EventReceived;

    public StatusResponse? CurrentStatus { get; private set; }

    public IReadOnlyList<ConsoleEntry> RecentConsole()
    {
        lock (_historyGate) return _history.ToList();
    }

    private async Task RunAsync()
    {
        int failures = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await _api.HandshakeAsync(_token(), _lifetime.Token);
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization", "Bearer " + _token());
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(25);
                await socket.ConnectAsync(_api.BuildWebSocketUri(_cursor), _lifetime.Token);
                failures = 0;
                await ReceiveAsync(socket);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (RemoteApiException error) when (error.IsAuthenticationFailure)
            {
                break;
            }
            catch (ProtocolVersionException)
            {
                break;
            }
            catch
            {
                failures = Math.Min(failures + 1, 8);
            }

            if (_lifetime.IsCancellationRequested) break;
            int delayMs = Math.Min(30000, 1000 * (1 << Math.Min(failures, 5)));
            delayMs += Random.Shared.Next(0, 500);
            try { await Task.Delay(delayMs, _lifetime.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket)
    {
        byte[] buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !_lifetime.IsCancellationRequested)
        {
            using var frame = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, _lifetime.Token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidDataException("Only text WebSocket frames are supported.");
                frame.Write(buffer, 0, result.Count);
                if (frame.Length > MaxFrameBytes) throw new InvalidDataException("WebSocket frame is too large.");
            } while (!result.EndOfMessage);

            string json = Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length);
            WebSocketFrame message = ProtocolJson.Deserialize<WebSocketFrame>(json);
            ProtocolVersion.EnsureCompatible(message.ApiVersion);
            ProcessFrame(message);
            _cursor = message.Cursor;
        }
    }

    private void ProcessFrame(WebSocketFrame message)
    {
        switch (message)
        {
            case ResetFrame:
                lock (_historyGate) _history.Clear();
                _replaySnapshot = true;
                ConsoleReset?.Invoke(this, EventArgs.Empty);
                break;
            case SnapshotFrame snapshot:
                CurrentStatus = snapshot.Status;
                ReplaceHistory(snapshot.RecentConsole);
                if (_replaySnapshot)
                {
                    _replaySnapshot = false;
                    foreach (ConsoleEntry entry in snapshot.RecentConsole)
                        ConsoleLine?.Invoke(this, new ServerConsoleEventArgs(entry.Message, entry.Sequence));
                }
                SnapshotReceived?.Invoke(this, snapshot.Status);
                break;
            case ConsoleFrame console:
                foreach (ConsoleEntry entry in console.Entries) AddConsole(entry);
                break;
            case EventFrame eventFrame:
                EventReceived?.Invoke(this, eventFrame.Event);
                break;
        }
    }

    private void ReplaceHistory(IEnumerable<ConsoleEntry> entries)
    {
        lock (_historyGate)
        {
            _history.Clear();
            foreach (ConsoleEntry entry in entries.TakeLast(800)) _history.Add(entry);
        }
    }

    private void AddConsole(ConsoleEntry entry)
    {
        lock (_historyGate)
        {
            _history.Add(entry);
            if (_history.Count > 800) _history.RemoveAt(0);
        }
        ConsoleLine?.Invoke(this, new ServerConsoleEventArgs(entry.Message, entry.Sequence));
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        try { _runTask.GetAwaiter().GetResult(); } catch { }
        _lifetime.Dispose();
    }
}
