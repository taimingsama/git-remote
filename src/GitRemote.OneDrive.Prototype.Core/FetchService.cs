namespace GitRemote.OneDrive.Prototype;

internal sealed class FetchService(OneDriveRepository repository, PackStore packs, Git git)
{
    internal async Task<string?> FetchAsync(IReadOnlyList<string> wants)
    {
        await EnsureObjectFormatAsync();
        var missing = await git.MissingAsync(wants);
        if (missing.Count == 0)
            return null;

        using var scratch = new Scratch();
        var stagingGit = await Git.InitBareAsync(scratch.Path, repository.ObjectFormat, repository.Branch, git.Cancellation);
        var alternate = Path.Combine(scratch.Path, "objects");
        var downloaded = new HashSet<string>(StringComparer.Ordinal);
        var destination = Path.Combine(alternate, "pack");
        for (var discovery = 0; discovery < 5 && missing.Count != 0; discovery++)
        {
            var available = packs.Pairs(await packs.InventoryAsync()).Where(pair => !downloaded.Contains(pair.Name)).ToArray();
            while (missing.Count != 0 && available.Length != 0)
            {
                var candidates = await packs.FindCandidatesAsync(available, missing);
                var selected = available.Where(item => candidates.ContainsKey(item.Name))
                    .OrderByDescending(item => candidates[item.Name].Count).ThenBy(item => item.Pack.Size).FirstOrDefault();
                if (selected is null)
                    break;
                await packs.DownloadPackAsync(selected, destination, stagingGit, cachePack: false);
                downloaded.Add(selected.Name);
                available = available.Where(pair => pair.Name != selected.Name).ToArray();
                missing = await git.MissingAsync(wants, alternate);
            }
            if (missing.Count != 0 && discovery < 4)
                await Task.Delay(150 * (discovery + 1), git.Cancellation);
        }
        if (missing.Count != 0)
            throw new IOException($"Remote objects unavailable ({missing.Count} missing, including {missing.First()}).");

        var downloadedPacks = Directory.GetFiles(destination, "*.pack");
        if (downloadedPacks.Length == 1)
            return await InstallAsync(downloadedPacks[0]);

        // The helper protocol hands off one keep file. Consolidate the imported
        // object sets so every object stays protected until Git updates its refs.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(destination, "*.idx"))
        {
            await using var index = File.OpenRead(file);
            var entries = await stagingGit.CheckedAsync(["show-index", "--object-format=" + repository.ObjectFormat], fileInput: index);
            foreach (var line in entries.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                ids.Add(line.Split(' ', 3)[1]);
        }
        var combined = Path.Combine(scratch.Path, "combined.pack");
        await using (var output = File.Create(combined))
            await stagingGit.CheckedAsync(["pack-objects", "--stdout", "--delta-base-offset"],
                string.Join('\n', ids) + "\n", fileOutput: output);

        return await InstallAsync(combined);
    }

    internal async Task EnsureObjectFormatAsync()
    {
        var local = await git.CheckedAsync(["rev-parse", "--show-object-format"]);
        if (local != repository.ObjectFormat)
            throw new IOException($"Git object format mismatch: local {local}, remote {repository.ObjectFormat}.");
    }

    private async Task<string?> InstallAsync(string file)
    {
        await using var pack = File.OpenRead(file);
        var output = await git.CheckedAsync(["index-pack", "--stdin", "--keep=git-remote-onedrive", "--fsck-objects"], fileInput: pack);
        var prefix = "keep\t";
        if (!output.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var id = output[prefix.Length..].Split(['\n', '\r', '\t', ' '], 2)[0];
        if (!Git.IsId(id, repository.HashLength))
            throw new IOException("Git reported an invalid kept pack.");
        var keep = await git.CheckedAsync(["rev-parse", "--path-format=absolute", "--git-path", $"objects/pack/pack-{id}.keep"]);
        return keep;
    }
}
