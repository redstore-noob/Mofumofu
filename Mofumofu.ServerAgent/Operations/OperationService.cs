using System.Collections.Concurrent;
using Mofumofu.RemoteProtocol;
using Mofumofu.ServerAgent.Events;

namespace Mofumofu.ServerAgent.Operations;

public sealed record OperationCompletion(string? Message = null,
    Dictionary<string, string>? Metadata = null);

public sealed class OperationService
{
    private readonly EventJournal _journal;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, OperationRecord> _operations = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();

    public OperationService(EventJournal journal, IClock clock)
    {
        _journal = journal;
        _clock = clock;
    }

    public async Task<OperationInfo> StartAsync(string kind, string requiredScope,
        Func<CancellationToken, Task<OperationCompletion>> work)
    {
        DateTimeOffset now = _clock.UtcNow;
        OperationRecord record = new()
        {
            RequiredScope = requiredScope,
            Completion = new TaskCompletionSource<OperationInfo>(TaskCreationOptions.RunContinuationsAsynchronously),
            Current = new OperationInfo
            {
                Id = "op_" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
                Kind = kind,
                State = ProtocolOperationStates.Queued,
                CreatedAt = now,
            },
        };
        if (!_operations.TryAdd(record.Current.Id, record))
        {
            throw new InvalidOperationException("Operation ID collision.");
        }

        try
        {
            await PublishAsync(record);
        }
        catch
        {
            _operations.TryRemove(record.Current.Id, out _);
            throw;
        }

        _ = Task.Run(() => ExecuteAsync(record, work), CancellationToken.None);
        return Snapshot(record);
    }

    public async Task<OperationInfo> RunAsync(string kind, string requiredScope,
        Func<CancellationToken, Task<OperationCompletion>> work)
    {
        OperationInfo operation = await StartAsync(kind, requiredScope, work);
        return await _operations[operation.Id].Completion.Task;
    }

    public OperationInfo Get(string operationId, string scope)
    {
        if (!_operations.TryGetValue(operationId, out OperationRecord? record))
        {
            throw new ApiException(StatusCodes.Status404NotFound, ProtocolErrors.OperationNotFound,
                "The operation was not found.");
        }

        if (scope != record.RequiredScope)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, ProtocolErrors.InsufficientScope,
                "The operation requires its original scope.");
        }

        return Snapshot(record);
    }

    public string GetRequiredScope(string operationId)
    {
        if (!_operations.TryGetValue(operationId, out OperationRecord? record))
        {
            throw new ApiException(StatusCodes.Status404NotFound, ProtocolErrors.OperationNotFound,
                "The operation was not found.");
        }

        return record.RequiredScope;
    }

    public IReadOnlyList<OperationInfo> Active() => _operations.Values
        .Select(Snapshot).Where(operation => operation.State is ProtocolOperationStates.Queued
            or ProtocolOperationStates.Running).ToArray();

    public async Task StopAsync()
    {
        await _lifetime.CancelAsync();
        await Task.WhenAll(_operations.Values.Select(record => record.Completion.Task)
            .Select(async task =>
            {
                try { await task; } catch { }
            }));
    }

    private async Task ExecuteAsync(OperationRecord record,
        Func<CancellationToken, Task<OperationCompletion>> work)
    {
        try
        {
            Mutate(record, current => current with
            {
                State = ProtocolOperationStates.Running,
                StartedAt = _clock.UtcNow,
            });
            await PublishAsync(record);
            OperationCompletion completion = await work(_lifetime.Token);
            Mutate(record, current => current with
            {
                State = ProtocolOperationStates.Succeeded,
                CompletedAt = _clock.UtcNow,
                ProgressPercent = 100,
                Message = completion.Message,
                Metadata = completion.Metadata,
            });
            await PublishAsync(record);
        }
        catch (Exception error)
        {
            Mutate(record, current => current with
            {
                State = ProtocolOperationStates.Failed,
                CompletedAt = _clock.UtcNow,
                Error = new ApiError
                {
                    Code = error is ApiException api ? api.Code : ProtocolErrors.OperationFailed,
                    Message = error is ApiException ? error.Message : "The operation failed.",
                    OperationId = record.Current.Id,
                },
            });
            try { await PublishAsync(record); } catch { }
        }
        finally
        {
            record.Completion.TrySetResult(Snapshot(record));
        }
    }

    private Task PublishAsync(OperationRecord record)
    {
        OperationInfo operation = Snapshot(record);
        return _journal.AppendAsync((cursor, sentAt) => new OperationFrame
        {
            Cursor = cursor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SentAt = sentAt,
            Operation = operation,
        });
    }

    private static OperationInfo Snapshot(OperationRecord record)
    {
        lock (record.Sync)
        {
            return record.Current;
        }
    }

    private static void Mutate(OperationRecord record, Func<OperationInfo, OperationInfo> update)
    {
        lock (record.Sync)
        {
            record.Current = update(record.Current);
        }
    }

    private sealed class OperationRecord
    {
        public readonly object Sync = new();
        public required string RequiredScope { get; init; }
        public required TaskCompletionSource<OperationInfo> Completion { get; init; }
        public OperationInfo Current { get; set; } = new();
    }
}
