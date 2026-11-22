using MofuCore.Server;
using Mofumofu.RemoteProtocol;
using Mofumofu.ServerAgent.Core;
using Mofumofu.ServerAgent.Services;

namespace Mofumofu.ServerAgent.Events;

public sealed class ServerMonitorService : BackgroundService
{
    private readonly AgentOptions _options;
    private readonly ICoreGateway _core;
    private readonly EventJournal _journal;
    private readonly IClock _clock;
    private readonly object _consoleSync = new();
    private string? _previousState;
    private bool _crashPending;
    private bool _intentionalStop;

    public ServerMonitorService(AgentOptions options, ICoreGateway core, EventJournal journal, IClock clock)
    {
        _options = options;
        _core = core;
        _journal = journal;
        _clock = clock;
        ServerManage.ConsoleLine += OnConsoleLine;
    }

    public void NoteIntentionalStop() => _intentionalStop = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Observe();
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Monitoring remains best-effort; durable console and Probe events continue independently.
            }
        }
    }

    public void PublishConsole(string instanceId, string line) => OnConsoleLine(instanceId, line);

    public override void Dispose()
    {
        ServerManage.ConsoleLine -= OnConsoleLine;
        base.Dispose();
    }

    private void OnConsoleLine(string instanceId, string line)
    {
        if (!string.Equals(instanceId, _options.InstanceId, StringComparison.Ordinal))
        {
            return;
        }

        lock (_consoleSync)
        {
            _journal.AppendConsoleAsync(line).GetAwaiter().GetResult();
        }
    }

    private void Observe()
    {
        string current = StatusService.MapState(_core.Poll(_options.InstanceId, 0).Status);
        string? previous = _previousState;
        _previousState = current;
        if (previous is null)
        {
            return;
        }

        bool wasActive = previous is ProtocolServerStates.Running or ProtocolServerStates.Starting
            or ProtocolServerStates.Stopping;
        bool unexpectedStop = wasActive && current == ProtocolServerStates.Offline && !_intentionalStop;
        if (unexpectedStop)
        {
            _crashPending = true;
            AppendEvent(ProtocolEventTypes.ServerCrashed, "The server process stopped unexpectedly.");
        }

        if (wasActive && current == ProtocolServerStates.Offline)
        {
            _intentionalStop = false;
        }

        if (!string.Equals(previous, current, StringComparison.Ordinal))
        {
            AppendEvent(ProtocolEventTypes.ServerStateChanged,
                $"Server state changed from {previous} to {current}.",
                new Dictionary<string, string> { ["previous"] = previous, ["current"] = current });
        }

        if (_crashPending && current is ProtocolServerStates.Starting or ProtocolServerStates.Running &&
            _core.AutoRestartEnabled(_options.InstanceId))
        {
            _crashPending = false;
            AppendEvent(ProtocolEventTypes.ServerAutoRestarted, "Core automatically restarted the server.");
        }
    }

    private void AppendEvent(string type, string message, Dictionary<string, string>? details = null)
    {
        _journal.AppendEventAsync(new ServerEvent
        {
            EventId = "evt_" + Guid.NewGuid().ToString("N"),
            Type = type,
            OccurredAt = _clock.UtcNow,
            Message = message,
            Details = details ?? [],
        }).GetAwaiter().GetResult();
    }
}
