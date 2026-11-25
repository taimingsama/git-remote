namespace GitRemote.OneDrive.Prototype;

internal sealed class RepositoryMaintenance(OneDriveRepository repository)
{
    private readonly RepositoryCoordinator _coordinator = new(repository);
    private readonly PackStore _packs = new(repository);
    private OneDriveStorage Storage => repository.Storage;

    internal async Task CollectAsync(bool apply, string? prune)
    {
        await using var session = await _coordinator.EnterAsync("gc");
        using var scratch = new Scratch();
        var snapshot = await repository.ReadRefsAsync();
        var inventory = await _packs.InventoryAsync();
        var git = await _packs.MaterializeAsync(scratch.Path, snapshot.Bytes, inventory, cachePacks: true);
        var directory = Path.Combine(scratch.Path, "objects", "pack");
        var args = new List<string> { "-c", "gc.writeCommitGraph=false", "-c", "maintenance.auto=false", "gc", "--quiet" };
        if (prune is not null)
            args.Add("--prune=" + prune);
        await git.CheckedAsync(args.ToArray());
        await git.CheckedAsync(["fsck", "--full", "--no-dangling"]);
        var replacements = Directory.GetFiles(directory).Where(file =>
            Path.GetExtension(file) is ".pack" or ".idx" or ".mtimes").ToDictionary(file => Path.GetFileName(file), StringComparer.Ordinal);
        var before = inventory.Sum(item => item.Size);
        var after = replacements.Values.Sum(file => new FileInfo(file).Length);
        Console.WriteLine($"Objects in pack files: {before} bytes before, {after} bytes after, {before - after} bytes reclaimed.");
        if (!apply)
            return;

        var existing = inventory.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var staged = new Dictionary<string, DriveItem>(StringComparer.Ordinal);
        var replaceTimes = new Dictionary<string, DriveItem>(StringComparer.Ordinal);
        DriveItem? stage = null;
        foreach (var (name, file) in replacements)
        {
            if (existing.TryGetValue(name, out var found))
            {
                if (!name.EndsWith(".mtimes", StringComparison.Ordinal))
                {
                    await Storage.VerifyFileAsync(found, file);
                    continue;
                }
                if (await SameFileAsync(found, file))
                    continue;
                replaceTimes.Add(name, found);
            }
            stage ??= await _packs.StageDirectoryAsync(session.Id);
            staged.Add(name, await _packs.StageAsync(stage.Id, file));
        }

        session.KeepForRecovery();
        try { await _coordinator.FreezeAsync(session.Id, snapshot.Item.ETag); }
        catch (MaintenanceInProgress)
        {
            session.AllowRelease();
            throw;
        }
        for (var attempt = 0; ; attempt++)
        {
            var state = await _coordinator.ReadAsync();
            if (!state.Operations.Keys.Any(id => id != session.Id))
                break;
            if (attempt == 60)
            {
                await _coordinator.ThawAsync(session.Id);
                session.AllowRelease();
                throw new IOException("Existing operations did not finish. Use git onedrive status for details.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), Storage.Cancellation);
        }
        var current = await repository.ReadRefsAsync();
        var latest = await _packs.InventoryAsync();
        if (current.Item.ETag != snapshot.Item.ETag || !SameInventory(inventory, latest))
        {
            await _coordinator.ThawAsync(session.Id);
            session.AllowRelease();
            throw new IOException("The remote repository changed during preparation. Retry GC.");
        }

        foreach (var name in staged.Keys.OrderBy(ArtifactOrder))
        {
            if (replaceTimes.TryGetValue(name, out var times))
            {
                var bytes = await File.ReadAllBytesAsync(replacements[name], Storage.Cancellation);
                await session.BeginWriteAsync(new("times", name, times.Id, times.ETag));
                var changed = await Storage.ReplaceAsync(times, bytes);
                await Storage.VerifyFileAsync(changed, replacements[name]);
                await session.EndWriteAsync();
            }
            else
            {
                await _packs.PromoteAsync(staged[name], replacements[name], session);
            }
        }

        var obsolete = inventory.Where(item => !replacements.ContainsKey(item.Name))
            .OrderBy(item => -ArtifactOrder(item.Name))
            .Select(item => new Deletion(item.Name, item.Id, item.ETag)).ToList();
        // Staging folders from operations already retired cannot publish files.
        foreach (var item in await Storage.ListAsync(repository.Metadata.Id))
        {
            if (item.IsFolder && item.Name.StartsWith("stage-", StringComparison.Ordinal)
                && Guid.TryParseExact(item.Name["stage-".Length..], "N", out _))
                obsolete.Add(new Deletion(item.Name, item.Id, item.ETag));
        }
        var promoted = (await _packs.InventoryAsync()).Where(item => replacements.ContainsKey(item.Name))
            .Select(item => new Deletion(item.Name, item.Id, item.ETag)).ToArray();
        if (promoted.Length != replacements.Count)
            throw new IOException("OneDrive did not confirm the complete replacement object inventory.");
        await _coordinator.RecordDeletionPlanAsync(session.Id, obsolete.ToArray(), promoted);
        await FinishAsync((await _coordinator.ReadAsync()).Maintenance!);
        session.AllowRelease();
    }

    internal async Task RecoverAsync(string id, bool ownerStopped)
    {
        var operation = await _coordinator.RecoverableAsync(id, ownerStopped);
        var state = await _coordinator.ReadAsync();
        if (operation.Pending is { } intent)
        {
            if (!await SettleAsync(id, intent))
                throw new IOException("The previous write cannot yet be reconciled. Its protection is still active.");
            await _coordinator.MarkAsync(id, null);
        }
        if (state.Maintenance is { } maintenance && maintenance.Owner == id)
        {
            if (maintenance.Deletions is not null)
                await FinishAsync(maintenance);
            else
                await _coordinator.ThawAsync(id);
        }
        await _coordinator.LeaveAsync(id);
        Console.WriteLine($"Recovered {id}.");
    }

    private async Task<bool> SettleAsync(string id, WriteIntent intent)
    {
        var current = await Storage.FindByIdAsync(intent.ItemId);
        if (intent.Kind == "promote")
        {
            var target = await Storage.FindAsync(repository.PackDirectory.Id, intent.Target);
            if (target?.Id == intent.ItemId || current is null || current.ETag != intent.ExpectedETag)
                return true;
            var stage = await Storage.FindAsync(repository.Metadata.Id, "stage-" + id);
            if (stage is null || (await Storage.FindAsync(stage.Id, intent.Target))?.Id != current.Id)
                return false;
            try { await Storage.MoveAsync(current, stage.Id, "retired-" + Guid.NewGuid().ToString("N")); }
            catch (GraphFailure ex) when (ex.IsConflict) { }
            return (await Storage.FindByIdAsync(intent.ItemId))?.ETag != intent.ExpectedETag;
        }
        if (intent.Kind == "refs")
        {
            var refs = await repository.ReadRefsAsync();
            if (refs.Item.Id != intent.ItemId)
                return false;
            if (refs.Item.ETag != intent.ExpectedETag)
                return true;
            // A native header variant changes the file version without changing
            // any ref, so a delayed compare-and-swap cannot publish afterward.
            var bytes = refs.Refs.Serialize(fence: true);
            if (refs.Bytes.AsSpan().SequenceEqual(bytes))
                bytes = refs.Refs.Serialize();
            try { await Storage.ReplaceAsync(refs.Item, bytes); }
            catch (GraphFailure ex) when (ex.IsConflict) { }
            var updated = await repository.ReadRefsAsync();
            return updated.Item.Id == intent.ItemId && updated.Item.ETag != intent.ExpectedETag;
        }
        if (intent.Kind == "times")
            return current is not null && current.ETag != intent.ExpectedETag;
        return false;
    }

    private async Task FinishAsync(Maintenance maintenance)
    {
        var refs = await repository.ReadRefsAsync();
        if (refs.Item.ETag != maintenance.RefsETag)
            throw new IOException("The refs changed while in maintenance. The deletion plan was preserved.");
        var inventory = await _packs.InventoryAsync();
        var present = inventory.Select(item => new Deletion(item.Name, item.Id, item.ETag)).ToHashSet();
        if (maintenance.Replacements is null || !maintenance.Replacements.All(present.Contains))
            throw new IOException("Replacement objects are missing. The deletion plan was preserved.");
        foreach (var deletion in maintenance.Deletions!)
            await Storage.DeleteAsync(deletion.ItemId, deletion.ETag);
        await _coordinator.ThawAsync(maintenance.Owner);
    }

    private async Task<bool> SameFileAsync(DriveItem item, string file)
    {
        if (item.Size != new FileInfo(file).Length)
            return false;
        using var remote = new MemoryStream();
        await Storage.DownloadAsync(item, remote);
        var local = await File.ReadAllBytesAsync(file, Storage.Cancellation);
        return remote.GetBuffer().AsSpan(0, (int)remote.Length).SequenceEqual(local);
    }

    private static bool SameInventory(DriveItem[] left, DriveItem[] right) =>
        left.Select(item => (item.Id, item.ETag)).ToHashSet().SetEquals(right.Select(item => (item.Id, item.ETag)));

    private static int ArtifactOrder(string name) => Path.GetExtension(name) switch
    {
        ".pack" => 0,
        ".mtimes" => 1,
        _ => 2
    };
}
