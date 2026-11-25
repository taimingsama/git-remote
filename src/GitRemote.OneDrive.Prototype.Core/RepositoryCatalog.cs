using System.Net;
using System.Text.Json;

namespace GitRemote.OneDrive.Prototype;

internal sealed record DriveSettings(string Root);
internal sealed record RepositoryFormat(int Version, string ObjectFormat);
internal sealed record OneDriveRepository(OneDriveStorage Storage, RepositoryAddress Address, DriveItem Directory,
    DriveItem Metadata, DriveItem PackDirectory, string ObjectFormat, string Head)
{
    internal int HashLength => Git.HashLength(ObjectFormat);
    internal string Branch => Head["refs/heads/".Length..];

    internal async Task<(DriveItem Item, PackedRefs Refs, byte[] Bytes)> ReadRefsAsync()
    {
        var item = await Storage.FindAsync(Directory.Id, "packed-refs")
            ?? throw new IOException("The repository's packed-refs file is missing.");
        var version = await Storage.ReadVersionAsync(item);
        return (version.Item, PackedRefs.Parse(version.Bytes, HashLength), version.Bytes);
    }
}

internal sealed class RepositoryCatalog(OneDriveStorage storage)
{
    private const string SettingsFile = "GitOnedrive.settings.json";
    private const string FormatFile = "gitremote/repository.json";
    internal const string StateFile = "state.json";
    private DriveItem? _root;

    internal async Task<string> GetRootSettingAsync()
    {
        var root = await RootAsync();
        var settings = await storage.FindAsync(root.Id, SettingsFile);
        return settings is null ? RepositoryAddress.DefaultRoot
            : RepositoryAddress.ValidateRoot((await ReadJsonAsync<DriveSettings>(settings)).Root);
    }

    internal async Task SetRootSettingAsync(string path)
    {
        var value = ToJson(new DriveSettings(RepositoryAddress.ValidateRoot(path)));
        var root = await RootAsync();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var existing = await storage.FindAsync(root.Id, SettingsFile);
            try
            {
                if (existing is null)
                    await storage.CreateAsync(root.Id, SettingsFile, value);
                else
                    await storage.ReplaceAsync(existing, value);
                return;
            }
            catch (GraphFailure ex) when (ex.IsConflict) { }
        }
        throw new IOException("OneDrive settings changed repeatedly. Try again.");
    }

    internal async Task<OneDriveRepository> OpenAsync(RepositoryAddress address)
    {
        var root = await RootAsync();
        var path = address.Root ?? await GetRootSettingAsync();
        var folder = await storage.FindAsync(root.Id, path + "/" + address.Name);
        if (folder is null || !folder.IsFolder)
            throw new IOException($"Repository '{path}/{address.Name}' does not exist.");

        var formatTask = ReadFormatAsync(folder.Id);
        var headTask = ReadHeadAsync(folder.Id);
        var metadataTask = storage.FindAsync(folder.Id, "gitremote");
        var packsTask = storage.FindAsync(folder.Id, "objects/pack");
        await Task.WhenAll(formatTask, headTask, metadataTask, packsTask);

        var metadata = await metadataTask ?? throw new IOException("Missing repository metadata.");
        var packs = await packsTask ?? throw new IOException("Missing repository object directory.");
        if (!metadata.IsFolder || !packs.IsFolder)
            throw new IOException("Invalid repository layout.");
        return new(storage, address with { Root = path, Name = folder.Name }, folder, metadata, packs,
            (await formatTask).ObjectFormat, await headTask);
    }

    private async Task<RepositoryFormat> ReadFormatAsync(string folderId)
    {
        var marker = await storage.FindAsync(folderId, FormatFile)
            ?? throw new IOException("The directory is not an initialized Git OneDrive repository.");
        var format = await ReadJsonAsync<RepositoryFormat>(marker);
        if (format.Version != 1)
            throw new IOException("Unsupported Git OneDrive repository version.");
        _ = Git.HashLength(format.ObjectFormat);
        return format;
    }

    private async Task<string> ReadHeadAsync(string folderId)
    {
        var headFile = await storage.FindAsync(folderId, "HEAD") ?? throw new IOException("Missing repository HEAD.");
        var (_, bytes) = await storage.ReadVersionAsync(headFile);
        var head = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n');
        if (!head.StartsWith("ref: refs/heads/", StringComparison.Ordinal) || !PackedRefs.IsRefName(head[5..]))
            throw new IOException("Invalid repository HEAD.");
        return head[5..];
    }

    internal async Task<IReadOnlyList<RepositoryAddress>> ListAsync(string? rootPath)
    {
        var path = rootPath is null ? await GetRootSettingAsync() : RepositoryAddress.ValidateRoot(rootPath);
        var folder = await storage.FindAsync((await RootAsync()).Id, path);
        if (folder is null)
            return [];
        if (!folder.IsFolder)
            throw new IOException($"'{path}' is not a OneDrive folder.");
        var candidates = (await storage.ListAsync(folder.Id))
            .Where(item => item.IsFolder && item.Name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)).ToArray();
        var results = new RepositoryAddress?[candidates.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = storage.Cancellation },
            async (i, _) =>
            {
                var item = candidates[i];
                var marker = await storage.FindAsync(item.Id, FormatFile);
                if (marker is not null && (await ReadJsonAsync<RepositoryFormat>(marker)).Version == 1)
                    results[i] = new(path, item.Name, TransportMode.Direct);
            });
        return results.OfType<RepositoryAddress>().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal async Task<RepositoryAddress> InitAsync(RepositoryAddress address, string format, string branch)
    {
        var hashLength = Git.HashLength(format);
        using var scratch = new Scratch();
        var git = await Git.InitBareAsync(scratch.Path, format, branch, storage.Cancellation);
        await git.CheckedAsync(["fsck", "--full"]);
        var path = address.Root ?? await GetRootSettingAsync();
        var root = await storage.EnsureFolderAsync((await RootAsync()).Id, path);
        if (await storage.FindAsync(root.Id, address.Name) is not null)
            throw new IOException($"Repository '{path}/{address.Name}' already exists.");

        await ProbeAsync(root.Id);
        var stage = await storage.CreateFolderAsync(root.Id, "gitremote-init-" + Guid.NewGuid().ToString("N"));
        var metadata = await storage.CreateFolderAsync(stage.Id, "gitremote");
        await storage.EnsureFolderAsync(stage.Id, "objects/pack");
        await storage.EnsureFolderAsync(stage.Id, "refs/heads");
        await storage.EnsureFolderAsync(stage.Id, "refs/tags");
        foreach (var file in new[] { "HEAD", "config" })
            await storage.CreateAsync(stage.Id, file, await File.ReadAllBytesAsync(Path.Combine(scratch.Path, file), storage.Cancellation));
        await storage.CreateAsync(stage.Id, "packed-refs", new PackedRefs(hashLength).Serialize());
        await storage.CreateAsync(metadata.Id, StateFile, ToJson(new RepositoryState()));
        await storage.CreateAsync(metadata.Id, "repository.json", ToJson(new RepositoryFormat(1, format)));
        var source = await storage.FindByIdAsync(stage.Id) ?? throw new IOException("Staged repository disappeared.");
        try { await storage.MoveAsync(source, root.Id, address.Name); }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            var observed = await storage.FindAsync(root.Id, address.Name);
            if (observed?.Id != stage.Id)
                throw new IOException($"Could not publish '{path}/{address.Name}'. The staged repository was preserved.", ex);
        }
        return address with { Root = path };
    }

    private async Task ProbeAsync(string root)
    {
        var probe = await storage.CreateFolderAsync(root, "gitremote-probe-" + Guid.NewGuid().ToString("N"));
        var step = "creating a conditional-write test file";
        try
        {
            var file = await storage.CreateAsync(probe.Id, "conditional", [1]);
            step = "checking same-name file creation";
            await ExpectConflictAsync(() => storage.CreateAsync(probe.Id, "conditional", [2]), HttpStatusCode.Conflict);
            step = "replacing a file with its current ETag";
            var write = await storage.ReplaceAsync(file, [3]);
            step = "rejecting an outdated file replacement";
            await ExpectConflictAsync(() => storage.ReplaceAsync(file, [4]), HttpStatusCode.PreconditionFailed);
            step = "reading back the conditionally written bytes";
            var read = await storage.ReadVersionAsync(write);
            if (!read.Bytes.AsSpan().SequenceEqual(new byte[] { 3 }))
                throw new IOException("OneDrive did not return the conditionally written content.");
            step = "rejecting an outdated file deletion";
            await ExpectConflictAsync(() => storage.DeleteAsync(file.Id, file.ETag), HttpStatusCode.PreconditionFailed);

            step = "creating test directories for publication";
            var created = await storage.CreateFolderAsync(probe.Id, "source");
            var occupied = await storage.CreateFolderAsync(probe.Id, "occupied");
            var source = await storage.FindByIdAsync(created.Id) ?? throw new IOException("The test source directory is missing.");
            step = "rejecting a move to an occupied name";
            await ExpectConflictAsync(() => storage.MoveAsync(source, probe.Id, "occupied"),
                HttpStatusCode.Conflict, HttpStatusCode.PreconditionFailed);
            if ((await storage.FindAsync(probe.Id, "occupied"))?.Id != occupied.Id
                || (await storage.FindAsync(probe.Id, "source"))?.Id != source.Id)
                throw new IOException("OneDrive did not preserve the source and destination directories after a collision.");
            step = "publishing a directory with its current ETag";
            var moved = await storage.MoveAsync(source, probe.Id, "published");
            if (moved.Id != source.Id || (await storage.FindAsync(probe.Id, "published"))?.Id != source.Id)
                throw new IOException("OneDrive did not publish the staged directory as expected.");
            step = "rejecting a delayed move with an outdated ETag";
            await ExpectConflictAsync(() => storage.MoveAsync(source, probe.Id, "delayed"), HttpStatusCode.PreconditionFailed);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new IOException($"OneDrive capability check failed while {step}. {ex.Message}", ex);
        }
        finally
        {
            try
            {
                var item = await storage.FindByIdAsync(probe.Id);
                if (item is not null)
                    await storage.DeleteAsync(item.Id, item.ETag);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                Console.Error.WriteLine($"Could not clean up capability check folder '{probe.Name}'. {ex.Message}");
            }
        }
    }

    private static async Task ExpectConflictAsync(Func<Task> action, params HttpStatusCode[] expected)
    {
        try { await action(); }
        catch (GraphFailure ex) when (expected.Contains(ex.StatusCode)) { return; }
        var statuses = string.Join(" or ", expected.Select(status => (int)status));
        throw new IOException($"This OneDrive account did not enforce conditional operations (expected HTTP {statuses}).");
    }

    internal async Task<T> ReadJsonAsync<T>(DriveItem item) =>
        JsonSerializer.Deserialize<T>((await storage.ReadVersionAsync(item)).Bytes, OneDriveStorage.Json)
        ?? throw new IOException($"Invalid repository metadata '{item.Name}'.");

    internal static byte[] ToJson<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, OneDriveStorage.Json);
    private async Task<DriveItem> RootAsync() => _root ??= await storage.RootAsync();
}
