namespace GitRemote.OneDrive.Prototype;

internal sealed record PushUpdate(string Name, string? Expected, GitRef? Value, string? Rejection = null);
internal sealed record PushStatus(string Name, string? Error);

internal sealed class PushService(OneDriveRepository repository, PackStore packs, Git git)
{
    internal async Task<PushUpdate> ResolveAsync(PackedRefs advertised, string line, Dictionary<string, string?> leases)
    {
        var fields = line.Split(':', 2);
        if (fields.Length != 2 || !PackedRefs.IsRefName(fields[1]))
            throw new IOException("Malformed push refspec.");
        var name = fields[1];
        var old = advertised.Values.GetValueOrDefault(name)?.Id;
        var hasLease = leases.TryGetValue(name, out var lease);
        if (hasLease && old != lease)
            return new(name, old, null, "stale info");

        if (fields[0].Length == 0)
            return new(name, old, null, name == repository.Head && old is not null ? "deletion of the current branch prohibited" : null);

        var force = fields[0].StartsWith('+');
        var source = fields[0][(force ? 1 : 0)..];
        try
        {
            var oid = await git.ResolveAsync(source);
            var value = await ResolveValueAsync(name, oid);
            if (old is not null && old != oid && !force && !hasLease)
            {
                if (name.StartsWith("refs/tags/", StringComparison.Ordinal))
                    return new(name, old, value, "already exists");
                var ancestry = await git.RunAsync(["merge-base", "--is-ancestor", old + "^{commit}", oid + "^{commit}"]);
                if (ancestry.ExitCode != 0)
                    return new(name, old, value, ancestry.ExitCode == 1 ? "non-fast-forward" : "fetch first");
            }
            return new(name, old, value);
        }
        catch (IOException ex)
        {
            return new(name, old, null, OneLine(ex.Message));
        }
    }

    internal async Task<GitRef> ResolveValueAsync(string name, string id)
    {
        if (!Git.IsId(id, repository.HashLength) || !PackedRefs.IsRefName(name))
            throw new IOException("Invalid ref or object ID.");
        await git.CheckedAsync(["check-ref-format", name]);
        var type = await git.CheckedAsync(["cat-file", "-t", id]);
        if (name.StartsWith("refs/heads/", StringComparison.Ordinal) && type != "commit")
            throw new IOException("a branch must point to a commit");
        var peeled = type == "tag" ? await git.ResolveAsync(id + "^{}") : null;
        if ((await git.MissingAsync([id])).Count != 0)
            throw new IOException("source Git history is incomplete");
        return new(id, peeled);
    }

    internal async Task<PushStatus[]> PublishAsync(IReadOnlyList<PushUpdate> changes, RepositorySession session,
        bool atomic, bool dryRun)
    {
        var duplicates = changes.GroupBy(change => change.Name, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var pending = changes.Select(change => duplicates.Contains(change.Name)
            ? change with { Rejection = "duplicate destination" } : change).ToArray();
        var initial = await repository.ReadRefsAsync();
        var initialUpdates = Evaluate(initial.Refs, pending, atomic);
        if (dryRun || initialUpdates.Applied.Length == 0)
            return initialUpdates.Status;

        string? staged = null;
        var wants = initialUpdates.Applied.Where(change => change.Value is not null)
            .Select(change => change.Value!.Id).Distinct(StringComparer.Ordinal).ToArray();
        if (wants.Length != 0)
        {
            try
            {
                using var scratch = new Scratch();
                var known = new List<string>();
                // Exclude only objects reachable from refs read while this session prevents GC from pruning packs.
                foreach (var root in initial.Refs.Values.Values.Select(value => value.Id).Distinct())
                {
                    var exists = await git.RunAsync(["cat-file", "-e", root]);
                    if (exists.ExitCode == 0 && (await git.MissingAsync([root])).Count == 0)
                        known.Add(root);
                }
                var hashes = await git.MakePacksAsync(Path.Combine(scratch.Path, "pack"), wants, known);
                foreach (var hash in hashes)
                {
                    var localPack = Path.Combine(scratch.Path, $"pack-{hash}.pack");
                    if (new FileInfo(localPack).Length == 12 + repository.HashLength / 2)
                        continue;
                    await git.CheckedAsync(["index-pack", "--verify", localPack]);
                    staged ??= (await packs.StageDirectoryAsync(session.Id)).Id;
                    await packs.PublishPackAsync(staged, localPack, session);
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                var reason = "object upload failed: " + OneLine(ex.Message);
                if (session.IsHeld || atomic)
                    return initialUpdates.Status.Select(status => status.Error is null ? status with { Error = reason } : status).ToArray();
                pending = pending.Select(change => change.Value is not null
                    && initialUpdates.Applied.Any(item => item.Name == change.Name)
                    ? change with { Rejection = reason } : change).ToArray();
            }
        }

        // Only refs selected in the upload plan can be published. A previously
        // rejected ref must never become eligible on a later CAS attempt.
        var initialErrors = initialUpdates.Status.Where(status => status.Error is not null)
            .GroupBy(status => status.Name).ToDictionary(group => group.Key, group => group.First().Error, StringComparer.Ordinal);
        pending = pending.Select(change => initialErrors.TryGetValue(change.Name, out var error)
            ? change with { Rejection = error } : change).ToArray();

        try
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var snapshot = attempt == 0 ? initial : await repository.ReadRefsAsync();
                var proposed = Evaluate(snapshot.Refs, pending, atomic);
                if (proposed.Applied.Length == 0)
                    return proposed.Status;
                var bytes = proposed.Refs.Serialize();
                await session.BeginWriteAsync(new("refs", "packed-refs", snapshot.Item.Id, snapshot.Item.ETag));
                try
                {
                    await repository.Storage.ReplaceAsync(snapshot.Item, bytes);
                }
                catch (GraphFailure ex) when (ex.IsConflict)
                {
                    await session.EndWriteAsync();
                    continue;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    var observed = await repository.ReadRefsAsync();
                    if (observed.Item.ETag == snapshot.Item.ETag || !observed.Bytes.AsSpan().SequenceEqual(bytes))
                        throw new IOException($"Push result is uncertain. Check operation {session.Id} with git onedrive status.", ex);
                }
                await session.EndWriteAsync();
                return proposed.Status;
            }
            return pending.Select(change => new PushStatus(change.Name, change.Rejection ?? "remote refs changed repeatedly")).ToArray();
        }
        finally
        {
            if (staged is not null && !session.IsHeld)
            {
                try { await packs.CleanupStageAsync(session.Id); }
                catch (IOException) { }
                catch (HttpRequestException) { }
            }
        }
    }

    private static (PackedRefs Refs, PushUpdate[] Applied, PushStatus[] Status) Evaluate(
        PackedRefs current, PushUpdate[] changes, bool atomic)
    {
        var refs = new PackedRefs(current.HashLength, new(current.Values, StringComparer.Ordinal));
        var statuses = changes.Select(change =>
        {
            var error = change.Rejection;
            if (error is null && change.Expected != current.Values.GetValueOrDefault(change.Name)?.Id)
                error = "stale info";
            return new PushStatus(change.Name, error);
        }).ToArray();

        var applied = new List<PushUpdate>();
        foreach (var index in Enumerable.Range(0, changes.Length).OrderBy(i => changes[i].Value is null ? 0 : 1))
        {
            if (statuses[index].Error is not null)
                continue;
            var change = changes[index];
            if (change.Value?.Id == change.Expected)
                continue;
            if (change.Value is null)
            {
                refs.Values.Remove(change.Name);
            }
            else
            {
                var conflict = refs.Values.Keys.Any(existing =>
                    existing.StartsWith(change.Name + "/", StringComparison.Ordinal) ||
                    change.Name.StartsWith(existing + "/", StringComparison.Ordinal));
                if (conflict)
                {
                    statuses[index] = statuses[index] with { Error = "reference name conflicts with an existing ref" };
                    continue;
                }
                refs.Values[change.Name] = change.Value;
            }
            applied.Add(change);
        }

        if (atomic && statuses.Any(status => status.Error is not null))
        {
            statuses = statuses.Select(status => status.Error is null ? status with { Error = "atomic push failed" } : status).ToArray();
            return (current, [], statuses);
        }
        return (refs, applied.ToArray(), statuses);
    }

    internal static string OneLine(string message) => message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
