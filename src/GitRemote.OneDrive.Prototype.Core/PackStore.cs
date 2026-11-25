using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace GitRemote.OneDrive.Prototype;

internal sealed record ArchivedPack(string Name, DriveItem Pack, DriveItem Index, DriveItem? Times)
{
    internal IEnumerable<DriveItem> Files
    {
        get
        {
            yield return Pack;
            yield return Index;
            if (Times is not null)
                yield return Times;
        }
    }
}

internal sealed class PackStore(OneDriveRepository repository)
{
    private OneDriveStorage Storage => repository.Storage;

    internal async Task<DriveItem[]> InventoryAsync() =>
        (await Storage.ListAsync(repository.PackDirectory.Id)).Where(IsPackFile).ToArray();

    internal ArchivedPack[] Pairs(IEnumerable<DriveItem> inventory)
    {
        var files = inventory.ToDictionary(item => item.Name, StringComparer.Ordinal);
        return files.Values.Where(item => item.Name.EndsWith(".idx", StringComparison.Ordinal))
            .Select(index =>
            {
                var name = index.Name[..^4];
                if (!files.TryGetValue(name + ".pack", out var pack))
                    throw new IOException($"Missing pack for '{index.Name}'.");
                return new ArchivedPack(name, pack, index, files.GetValueOrDefault(name + ".mtimes"));
            }).OrderByDescending(pair => pair.Pack.LastModifiedDateTime).ToArray();
    }

    internal async Task<string> LoadIndexAsync(ArchivedPack pair)
    {
        var path = CachePath("indices", pair.Index);
        if (!File.Exists(path))
            await DownloadCachedAsync(pair.Index, path);
        return path;
    }

    internal async Task<ConcurrentDictionary<string, HashSet<string>>> FindCandidatesAsync(ArchivedPack[] packs,
        HashSet<string> missing)
    {
        var candidates = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var wanted = missing.Select(id => (Id: id, Bytes: Convert.FromHexString(id))).ToArray();
        await Parallel.ForEachAsync(packs, Parallelism(), async (pack, _) =>
        {
            var path = await LoadIndexAsync(pack);
            var index = new PackIndex(await File.ReadAllBytesAsync(path, Storage.Cancellation),
                pack.Name, repository.HashLength);
            var objects = wanted.Where(objectId => index.Contains(objectId.Bytes))
                .Select(objectId => objectId.Id).ToHashSet(StringComparer.Ordinal);
            if (objects.Count > 0)
                candidates[pack.Name] = objects;
        });
        return candidates;
    }

    internal async Task DownloadPackAsync(ArchivedPack pair, string destination, Git git, bool cachePack)
    {
        var pack = Path.Combine(destination, pair.Pack.Name);
        var index = Path.Combine(destination, pair.Index.Name);
        if (cachePack)
        {
            var path = CachePath("packs", pair.Pack);
            if (!File.Exists(path))
                await DownloadCachedAsync(pair.Pack, path);
            File.Copy(path, pack);
            File.SetLastWriteTimeUtc(pack, pair.Pack.LastModifiedDateTime.UtcDateTime);
        }
        else
        {
            await DownloadFileAsync(pair.Pack, pack);
        }
        File.Copy(await LoadIndexAsync(pair), index);
        if (pair.Times is not null)
            await DownloadFileAsync(pair.Times, Path.Combine(destination, pair.Times.Name));

        var validation = await git.RunAsync(["index-pack", "--verify", pack]);
        if (validation.ExitCode != 0)
            throw new IOException($"Invalid Git pack or index '{pair.Name}': {validation.Error.Trim()}");
    }

    internal async Task<Git> MaterializeAsync(string directory, byte[] refs, DriveItem[] inventory, bool cachePacks)
    {
        var git = await Git.InitBareAsync(directory, repository.ObjectFormat, repository.Branch, Storage.Cancellation);
        await File.WriteAllBytesAsync(Path.Combine(directory, "packed-refs"), refs, Storage.Cancellation);
        var destination = Path.Combine(directory, "objects", "pack");
        await Parallel.ForEachAsync(Pairs(inventory), Parallelism(), async (pair, _) =>
            await DownloadPackAsync(pair, destination, git, cachePacks));
        await git.CheckedAsync(["fsck", "--full", "--no-dangling"]);
        return git;
    }

    internal async Task<DriveItem> StageDirectoryAsync(string id) =>
        await Storage.EnsureFolderAsync(repository.Metadata.Id, "stage-" + id);

    internal async Task<DriveItem> StageAsync(string directory, string path)
    {
        var name = Path.GetFileName(path);
        var item = await Storage.UploadAsync(directory, name, path);
        await Storage.VerifyFileAsync(item, path);
        return item;
    }

    internal async Task PublishPackAsync(string stageId, string packPath, RepositorySession session)
    {
        var basePath = packPath[..^5];
        foreach (var suffix in new[] { ".pack", ".mtimes", ".idx" })
        {
            var file = basePath + suffix;
            if (!File.Exists(file))
                continue;
            var existing = await Storage.FindAsync(repository.PackDirectory.Id, Path.GetFileName(file));
            if (existing is not null)
            {
                await Storage.VerifyFileAsync(existing, file);
                continue;
            }
            var staged = await StageAsync(stageId, file);
            await PromoteAsync(staged, file, session);
        }
    }

    internal async Task PromoteAsync(DriveItem source, string file, RepositorySession session)
    {
        var name = Path.GetFileName(file);
        await session.BeginWriteAsync(new("promote", name, source.Id, source.ETag));
        try
        {
            await Storage.MoveAsync(source, repository.PackDirectory.Id, name);
        }
        catch (GraphFailure ex) when (ex.IsConflict)
        {
            await session.EndWriteAsync();
            var existing = await Storage.FindAsync(repository.PackDirectory.Id, name);
            if (existing is null)
                throw;
        }
        var observed = await Storage.FindAsync(repository.PackDirectory.Id, name)
            ?? throw new IOException($"OneDrive did not confirm '{name}' was published.");
        await Storage.VerifyFileAsync(observed, file);
        await session.EndWriteAsync();
    }

    internal async Task CleanupStageAsync(string sessionId)
    {
        var stage = await Storage.FindAsync(repository.Metadata.Id, "stage-" + sessionId);
        if (stage is not null)
            await Storage.DeleteAsync(stage.Id, stage.ETag);
    }

    private bool IsPackFile(DriveItem item)
    {
        var name = item.Name;
        var prefixLength = 5 + repository.HashLength;
        return !item.IsFolder && name.Length > prefixLength && name.StartsWith("pack-", StringComparison.Ordinal)
            && Git.IsId(name[5..prefixLength], repository.HashLength)
            && name[prefixLength..] is ".pack" or ".idx" or ".mtimes";
    }

    private string CachePath(string kind, DriveItem item)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            repository.Storage.DriveId + "\n" + repository.Directory.Id + "\n" + item.Id + "\n" + item.ETag)));
        var root = Path.Combine(Authentication.LocalDirectory, kind);
        Directory.CreateDirectory(root);
        return Path.Combine(root, key + Path.GetExtension(item.Name));
    }

    private async Task DownloadCachedAsync(DriveItem item, string cachePath)
    {
        var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await DownloadFileAsync(item, temporary);
            if (!File.Exists(cachePath))
            {
                try { File.Move(temporary, cachePath); }
                catch (IOException) when (File.Exists(cachePath)) { }
            }
        }
        finally { File.Delete(temporary); }
    }

    private async Task DownloadFileAsync(DriveItem item, string file)
    {
        await using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            await Storage.DownloadAsync(item, stream);
            if (stream.Length != item.Size)
                throw new IOException($"Truncated OneDrive file '{item.Name}'.");
        }
        File.SetLastWriteTimeUtc(file, item.LastModifiedDateTime.UtcDateTime);
    }

    private ParallelOptions Parallelism() => new()
    {
        CancellationToken = Storage.Cancellation, MaxDegreeOfParallelism = 4
    };
}
