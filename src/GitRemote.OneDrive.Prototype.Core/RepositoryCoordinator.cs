using System.Diagnostics;
using System.Text.Json;

namespace GitRemote.OneDrive.Prototype;

internal sealed record WriteIntent(string Kind, string Target, string ItemId, string ExpectedETag);
internal sealed record RepositoryOperation(string Id, string Machine, int Process, DateTimeOffset Started,
    string Kind, WriteIntent? Pending = null);
internal sealed record Deletion(string Name, string ItemId, string ETag);
internal sealed record Maintenance(string Owner, string RefsETag, Deletion[]? Deletions = null, Deletion[]? Replacements = null);
internal sealed class MaintenanceInProgress() : IOException("Another operation already owns GC maintenance.");
internal sealed record RepositoryState(Dictionary<string, RepositoryOperation> Operations, Maintenance? Maintenance = null)
{
    internal RepositoryState() : this(new Dictionary<string, RepositoryOperation>(StringComparer.Ordinal), null) { }
}

internal sealed class RepositoryCoordinator(OneDriveRepository repository)
{
    internal async Task<RepositorySession> EnterAsync(string kind)
    {
        var operation = new RepositoryOperation(Guid.NewGuid().ToString("N"), Environment.MachineName,
            Environment.ProcessId, Process.GetCurrentProcess().StartTime, kind);
        await ChangeAsync(state =>
        {
            if (state.Maintenance is not null)
                throw new IOException($"Repository is in GC maintenance ({state.Maintenance.Owner}).");
            state.Operations.Add(operation.Id, operation);
            return state;
        });
        return new(this, operation.Id);
    }

    internal async Task<RepositoryState> ReadAsync() => (await ReadVersionAsync()).State;

    internal async Task MarkAsync(string id, WriteIntent? intent)
    {
        await ChangeAsync(state =>
        {
            if (!state.Operations.TryGetValue(id, out var operation))
                throw new IOException("The repository session is no longer active.");
            state.Operations[id] = operation with { Pending = intent };
            return state;
        });
    }

    internal async Task LeaveAsync(string id)
    {
        await ChangeAsync(state =>
        {
            if (state.Maintenance?.Owner == id)
                throw new IOException("GC must finish maintenance before releasing its session.");
            if (state.Operations.TryGetValue(id, out var operation) && operation.Pending is null)
                state.Operations.Remove(id);
            return state;
        });
    }

    internal Task FreezeAsync(string id, string refsEtag) => ChangeAsync(state =>
    {
        if (!state.Operations.TryGetValue(id, out var operation) || operation.Kind != "gc" || state.Maintenance is not null)
            throw new MaintenanceInProgress();
        return state with { Maintenance = new(id, refsEtag) };
    });

    internal Task RecordDeletionPlanAsync(string id, Deletion[] deletions, Deletion[] replacements) => ChangeAsync(state =>
    {
        if (state.Maintenance is not { } maintenance || maintenance.Owner != id || state.Operations.Count != 1)
            throw new IOException("GC no longer has exclusive access.");
        return state with { Maintenance = maintenance with { Deletions = deletions, Replacements = replacements } };
    });

    internal Task ThawAsync(string id) => ChangeAsync(state =>
    {
        if (state.Maintenance?.Owner != id)
            throw new IOException("GC no longer owns maintenance.");
        return state with { Maintenance = null };
    });

    internal async Task<RepositoryOperation> RecoverableAsync(string id, bool ownerStopped)
    {
        var state = await ReadAsync();
        if (!state.Operations.TryGetValue(id, out var operation))
            throw new IOException("Unknown operation ID.");
        if (operation.Machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var process = Process.GetProcessById(operation.Process);
                if (!process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - operation.Started.UtcDateTime).TotalSeconds) < 2)
                    throw new IOException("The original operation is still running.");
            }
            catch (ArgumentException) { }
        }
        else if (!ownerStopped)
        {
            throw new IOException("Confirm the original process has stopped on its device with --owner-stopped.");
        }
        return operation;
    }

    private async Task ChangeAsync(Func<RepositoryState, RepositoryState> change)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var (item, state) = await ReadVersionAsync();
            var result = change(state);
            try
            {
                await repository.Storage.ReplaceAsync(item, RepositoryCatalog.ToJson(result));
                return;
            }
            catch (GraphFailure ex) when (ex.IsConflict)
            {
                await Task.Delay(Random.Shared.Next(10, 40) * (attempt + 1), repository.Storage.Cancellation);
            }
        }
        throw new IOException("The repository coordination file changed repeatedly. Retry this operation.");
    }

    private async Task<(DriveItem, RepositoryState State)> ReadVersionAsync()
    {
        var item = await repository.Storage.FindAsync(repository.Metadata.Id, RepositoryCatalog.StateFile)
            ?? throw new IOException("Missing repository coordination file.");
        var version = await repository.Storage.ReadVersionAsync(item);
        var state = JsonSerializer.Deserialize<RepositoryState>(version.Bytes, OneDriveStorage.Json)
            ?? throw new IOException("Invalid repository coordination file.");
        return (version.Item, state);
    }
}

internal sealed class RepositorySession(RepositoryCoordinator coordinator, string id) : IAsyncDisposable
{
    internal string Id { get; } = id;
    private bool _hold;
    internal bool IsHeld => _hold;

    internal async Task BeginWriteAsync(WriteIntent intent)
    {
        _hold = true;
        await coordinator.MarkAsync(Id, intent);
    }

    internal async Task EndWriteAsync()
    {
        await coordinator.MarkAsync(Id, null);
        _hold = false;
    }

    internal void KeepForRecovery() => _hold = true;
    internal void AllowRelease() => _hold = false;

    public async ValueTask DisposeAsync()
    {
        if (!_hold)
            await coordinator.LeaveAsync(Id);
    }
}
