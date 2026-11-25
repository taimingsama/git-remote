using System.Diagnostics;
using GitRemote.OneDrive.Prototype;
using Xunit;

namespace GitRemote.OneDrive.Prototype.IntegrationTests.SystemTests;

[Trait("Category", "PrototypeSystem")]
public sealed class PrototypeSystemTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(409)]
    [InlineData(412)]
    public async Task Management_SharedSettingsAndPagination_KeepExistingAddressesStable(int occupiedMoveStatus)
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        graph.OccupiedMoveStatus = occupiedMoveStatus;
        using var first = new PrototypeClient(graph);
        using var second = new PrototypeClient(graph);
        Assert.Equal("GitRepositories", await first.GitAsync("onedrive", "config", "root"));
        await first.GitAsync("onedrive", "config", "root", "Development/Git Work");
        var firstAddress = await second.GitAsync("onedrive", "init", "alpha");
        await second.GitAsync("onedrive", "init", "bravo");
        await second.GitAsync("onedrive", "init", "charlie");
        var list = await first.GitAsync("onedrive", "list");
        Assert.Equal(3, list.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("alpha\tonedrive-prototype::Development/Git%20Work/alpha.git", list);
        Assert.Equal(firstAddress, await first.GitAsync("onedrive", "url", "alpha.git"));
        Assert.NotEqual(0, (await first.RunAsync("onedrive", "init", "ALPHA.git")).ExitCode);
        await first.GitAsync("onedrive", "config", "root", "Other");
        Assert.Empty(await first.GitAsync("onedrive", "list"));
        Assert.Equal(firstAddress, await second.GitAsync("onedrive", "url", firstAddress[..^4]));
        Assert.Equal(list, await second.GitAsync("onedrive", "list", "--root", "Development/Git Work"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedInit_PublishesOnlyACompleteNativeRepository(bool afterApply)
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        graph.FailNext("PATCH", "gitremote-init-", afterApply);
        var result = await client.RunAsync("onedrive", "init", "repo");
        Assert.Equal(afterApply, result.ExitCode == 0);
        if (!afterApply)
        {
            Assert.Empty(await client.GitAsync("onedrive", "list"));
            await client.GitAsync("onedrive", "init", "repo.git");
        }
        var snapshot = Path.Combine(client.Root, "empty.git");
        graph.CopyNativeRepo("GitRepositories/repo.git", snapshot);
        await client.GitAsync("--git-dir=" + snapshot, "fsck", "--full");
        Assert.Equal("refs/heads/main", await client.GitAsync("--git-dir=" + snapshot, "symbolic-ref", "HEAD"));
    }

    [Fact]
    public async Task EmptyClone_UsesTheSameUnbornHeadAsNativeGit()
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var remote = await client.GitAsync("onedrive", "init", "repo", "--initial-branch", "develop");
        var officialDirectory = Path.Combine(client.Root, "official.git");
        await client.GitAsync("init", "--bare", "--initial-branch=develop", officialDirectory);
        var official = new Uri(officialDirectory).AbsoluteUri;
        Assert.Equal(await client.GitAsync("ls-remote", "--symref", official), await client.GitAsync("ls-remote", "--symref", remote));
        var baseline = Path.Combine(client.Root, "from-native");
        var clone = Path.Combine(client.Root, "from-onedrive");
        await client.GitAsync("clone", official, baseline);
        await client.GitAsync("clone", remote, clone);
        Assert.Equal(await client.GitAsync("-C", baseline, "symbolic-ref", "HEAD"),
            await client.GitAsync("-C", clone, "symbolic-ref", "HEAD"));
        Assert.Equal("refs/heads/develop", await client.GitAsync("-C", clone, "symbolic-ref", "HEAD"));
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task DirectPushAndFetch_MatchNativeGitAndTransferOnlyMissingHistory(string format)
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo", "--object-format", format);
        var official = await client.InitNativeAsync(format);
        await client.InitSourceAsync(format);
        var first = await client.CommitAsync("first");
        await client.GitAsync("-C", client.Source, "tag", "-am", "version", "v1");
        await client.GitAsync("-C", client.Source, "push", "--atomic", "--tags", url, "main");
        await client.GitAsync("-C", client.Source, "push", "--atomic", "--tags", official, "main");
        Assert.Equal(await client.GitAsync("ls-remote", "--symref", official), await client.GitAsync("ls-remote", "--symref", url));

        var clone = Path.Combine(client.Root, "clone");
        await client.GitAsync("clone", url, clone);
        Assert.Equal(format, await client.GitAsync("-C", clone, "rev-parse", "--show-object-format"));
        Assert.Equal(first, await client.GitAsync("-C", clone, "rev-parse", "HEAD"));
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(clone, "file.txt"), Cancellation));

        var before = graph.Calls.Length;
        await client.GitAsync("-C", client.Source, "push", url, "main");
        await client.GitAsync("-C", clone, "fetch", "origin");
        Assert.DoesNotContain(graph.Calls.Skip(before), call => call.Name.EndsWith(".pack", StringComparison.Ordinal));

        var second = await client.CommitAsync("second");
        await client.GitAsync("-C", client.Source, "push", url, "main");
        await client.GitAsync("-C", client.Source, "push", official, "main");
        before = graph.Calls.Length;
        await client.GitAsync("-C", clone, "fetch", "origin");
        Assert.Single(graph.Calls.Skip(before), call => call.Method == "GET" && call.Name.EndsWith(".pack", StringComparison.Ordinal));
        Assert.Equal(second, await client.GitAsync("-C", clone, "rev-parse", "origin/main"));
        Assert.Equal(await client.GitAsync("ls-remote", official), await client.GitAsync("ls-remote", url));
        await VerifyNativeAsync(graph, client, second);
    }

    [Theory]
    [InlineData(".pack")]
    [InlineData(".idx")]
    public async Task Fetch_CorruptRemoteArtifactIsRejectedBeforeInstallingRefs(string extension)
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo");
        await client.InitSourceAsync();
        await client.CommitAsync("first");
        await client.GitAsync("-C", client.Source, "push", url, "main");
        var name = graph.Calls.First(call => call.Method == "PATCH" && call.Name.EndsWith(extension, StringComparison.Ordinal)).Name;
        graph.CorruptFile("GitRepositories/repo.git/objects/pack/" + name);
        var clone = Path.Combine(client.Root, "corrupt-clone");
        var result = await client.RunAsync("clone", url, clone);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Invalid Git", result.Error);
    }

    [Fact]
    public async Task DirectPush_UpToDateBranchDoesNotRegisterAnOperation()
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo");
        await client.InitSourceAsync();
        await client.CommitAsync("first");
        await client.GitAsync("-C", client.Source, "push", url, "main");
        var before = graph.Calls.Length;

        await client.GitAsync("-C", client.Source, "push", url, "main");

        Assert.DoesNotContain(graph.Calls.Skip(before), call => call.Name == "state.json");
    }

    [Fact]
    public async Task DirectPush_ReadsCoordinationStateOnceWithoutContention()
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo");
        await client.InitSourceAsync();
        await client.CommitAsync("first");
        await client.GitAsync("-C", client.Source, "push", url, "main");
        await client.CommitAsync("second");
        var before = graph.Calls.Length;

        await client.GitAsync("-C", client.Source, "push", url, "main");

        Assert.Equal(3, graph.Calls.Skip(before).Count(call => call.Method == "GET" && call.Name == "state.json"));
    }

    [Fact]
    public async Task AtomicAndLeaseRejections_PreserveOfficialRefState()
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo");
        var official = await client.InitNativeAsync();
        await client.InitSourceAsync();
        var previous = await client.CommitAsync("previous");
        var latest = await client.CommitAsync("latest");
        foreach (var remote in new[] { url, official })
            await client.GitAsync("-C", client.Source, "push", remote, "main");

        string[] args = ["-C", client.Source, "push", "--porcelain", "--atomic"];
        var remoteRejected = await client.RunAsync([.. args, url, previous + ":refs/heads/main", latest + ":refs/heads/side"]);
        var nativeRejected = await client.RunAsync([.. args, official, previous + ":refs/heads/main", latest + ":refs/heads/side"]);
        Assert.Equal(nativeRejected.ExitCode, remoteRejected.ExitCode);
        Assert.NotEqual(0, remoteRejected.ExitCode);
        Assert.Equal(await client.GitAsync("ls-remote", official), await client.GitAsync("ls-remote", url));

        foreach (var remote in new[] { url, official })
        {
            var stale = await client.RunAsync("-C", client.Source, "push", "--force-with-lease=main:" + previous,
                remote, previous + ":refs/heads/main");
            Assert.NotEqual(0, stale.ExitCode);
            await client.GitAsync("-C", client.Source, "push", "--dry-run", "--force-with-lease=main:" + latest,
                remote, previous + ":refs/heads/main");
        }
        Assert.Equal($"{latest}\trefs/heads/main", await client.GitAsync("ls-remote", url, "refs/heads/main"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentPushes_RejectStaleTargetOrMergeIndependentTargets(bool sameTarget)
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo");
        await client.InitSourceAsync();
        await client.CommitAsync("base");
        await client.GitAsync("-C", client.Source, "push", url, "main");
        var first = await client.CommitAsync("first");
        var second = await client.CommitAsync("second");
        var target = sameTarget ? "main" : "independent";

        using var pause = graph.PauseNext("PUT", "packed-refs", afterApply: false, dropResponse: false);
        using var later = client.Start("git", client.Source, ["push", url, first + ":refs/heads/" + target], redirectInput: false);
        try
        {
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(12), Cancellation);
            await client.GitAsync("-C", client.Source, "push", url, second + ":refs/heads/main");
            pause.Release.TrySetResult();
            await later.WaitForExitAsync(Cancellation);
            Assert.Equal(sameTarget, later.ExitCode != 0);
            Assert.Equal($"{second}\trefs/heads/main", await client.GitAsync("ls-remote", url, "refs/heads/main"));
            if (!sameTarget)
                Assert.Equal($"{first}\trefs/heads/independent", await client.GitAsync("ls-remote", url, "refs/heads/independent"));
            await VerifyNativeAsync(graph, client, second);
        }
        finally { Git.Kill(later); }
    }

    [Fact]
    public async Task Recovery_FencesACommitRequestDelayedAfterClientExit()
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo");
        await client.InitSourceAsync();
        var first = await client.CommitAsync("first");
        await client.GitAsync("-C", client.Source, "push", url, "main");
        var second = await client.CommitAsync("second");
        using var pause = graph.PauseNext("PUT", "packed-refs", afterApply: false, dropResponse: false);
        using var process = client.Start("git", client.Source, ["push", url, "main"], redirectInput: false);
        try
        {
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(12), Cancellation);
            Git.Kill(process);
            await process.WaitForExitAsync(Cancellation);
            var status = await client.GitAsync("onedrive", "status", "repo");
            var orphan = Assert.Single(status.Split('\n').Skip(1)).Split('\t')[0];
            await client.GitAsync("onedrive", "recover", "repo", orphan);
            pause.Release.TrySetResult();
            await client.GitAsync("onedrive", "gc", "repo", "--apply", "--prune=now");
            await VerifyNativeAsync(graph, client, first, "recovered-delayed-commit");
            await client.GitAsync("-C", client.Source, "push", url, "main");
            await VerifyNativeAsync(graph, client, second, "repushed-delayed-commit");
        }
        finally { Git.Kill(process); }
    }

    [Theory]
    [InlineData("smart")]
    [InlineData("stateless")]
    public async Task NativeUploadPack_ShallowsAndLazilyFetchesObjects(string transport)
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo");
        var official = await client.InitNativeAsync();
        await client.InitSourceAsync();
        await client.CommitAsync("old");
        var tip = await client.CommitAsync("new");
        foreach (var remote in new[] { url, official })
            await client.GitAsync("-C", client.Source, "push", remote, "main");

        var smart = url + "?transport=" + transport;
        var shallow = Path.Combine(client.Root, "shallow");
        var native = Path.Combine(client.Root, "native");
        await client.GitAsync("clone", "--depth=1", smart, shallow);
        await client.GitAsync("clone", "--depth=1", official, native);
        Assert.Equal(await client.GitAsync("-C", native, "rev-list", "--count", "HEAD"),
            await client.GitAsync("-C", shallow, "rev-list", "--count", "HEAD"));
        await client.GitAsync("-C", shallow, "fetch", "--unshallow");
        Assert.Equal("2", await client.GitAsync("-C", shallow, "rev-list", "--count", "HEAD"));

        var partial = Path.Combine(client.Root, "partial");
        await client.GitAsync("clone", "--filter=blob:none", "--no-checkout", smart, partial);
        Assert.Equal("true", await client.GitAsync("-C", partial, "config", "--get", "remote.origin.promisor"));
        Assert.Equal(tip, await client.GitAsync("-C", partial, "rev-parse", "HEAD"));
        Assert.Equal("new", await client.GitAsync("-C", partial, "show", "HEAD:file.txt"));
    }

    [Fact]
    public async Task StatelessObjectInfo_UsesNativeGitPackets()
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo");
        await client.InitSourceAsync();
        await client.CommitAsync("blob");
        await client.GitAsync("-C", client.Source, "push", url, "main");
        var oid = await client.GitAsync("-C", client.Source, "rev-parse", "HEAD:file.txt");
        var size = await client.GitAsync("-C", client.Source, "cat-file", "-s", oid);

        using var process = client.Start(client.HelperPath, client.Root, ["origin", url + "?transport=stateless"], redirectInput: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var reader = process.StandardOutput.BaseStream;
        var writer = process.StandardInput.BaseStream;
        try
        {
            await Wire.SendAsync(writer, "capabilities\n", timeout.Token);
            while (await Wire.ReadLineAsync(reader, timeout.Token) is { Length: > 0 }) { }
            await Wire.SendAsync(writer, "stateless-connect git-upload-pack\n", timeout.Token);
            Assert.Equal("", await Wire.ReadLineAsync(reader, timeout.Token));
            var capabilities = new List<string>();
            while (await Packet.ReadTextAsync(reader, timeout.Token) is { Length: > 0 } item)
                capabilities.Add(item.TrimEnd('\n'));
            Assert.Contains("object-info", capabilities);
            await Packet.WriteAsync(writer, "command=object-info\n", timeout.Token);
            await Wire.SendAsync(writer, "0001", timeout.Token);
            await Packet.WriteAsync(writer, "size\n", timeout.Token);
            await Packet.WriteAsync(writer, $"oid {oid}\n", timeout.Token);
            await Wire.SendAsync(writer, "0000", timeout.Token);
            Assert.Equal("size", (await Packet.ReadTextAsync(reader, timeout.Token))?.TrimEnd('\n'));
            Assert.Equal($"{oid} {size}", (await Packet.ReadTextAsync(reader, timeout.Token))?.TrimEnd('\n'));
            Assert.True((await Packet.ReadAsync(reader, timeout.Token))!.IsFlush);
            Assert.Equal("0002"u8.ToArray(), (await Packet.ReadAsync(reader, timeout.Token))!.Header);
            writer.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally { Git.Kill(process); }
    }

    [Fact]
    public async Task Get_ResumesTheSameVersionOfANativeRepositoryFile()
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo");
        var output = Path.Combine(client.Root, "HEAD");
        await File.WriteAllTextAsync(output + ".temp", "ref: ", Cancellation);
        await File.WriteAllTextAsync(output + ".temp.etag", graph.VersionOf("GitRepositories/repo.git/HEAD"), Cancellation);
        using var process = client.Start(client.HelperPath, client.Root, ["origin", url], redirectInput: true);
        var writer = process.StandardInput.BaseStream;
        try
        {
            await Wire.SendAsync(writer, $"capabilities\nget {url}/HEAD {output}\n\n", Cancellation);
            writer.Close();
            await process.WaitForExitAsync(Cancellation).WaitAsync(TimeSpan.FromSeconds(15), Cancellation);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("ref: refs/heads/main\n", await File.ReadAllTextAsync(output, Cancellation));
            Assert.False(File.Exists(output + ".temp"));
        }
        finally { Git.Kill(process); }
    }

    [Fact]
    public async Task SmartReceivePack_PublishesRefsAndServesArchive()
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo");
        var smart = url + "?transport=smart";
        await client.InitSourceAsync();
        var tip = await client.CommitAsync("native");
        await client.GitAsync("-C", client.Source, "push", "--atomic", "-o", "option-to-hook", smart, "main", "main:other");
        Assert.Equal($"{tip}\trefs/heads/main", await client.GitAsync("ls-remote", url, "refs/heads/main"));
        Assert.Equal($"{tip}\trefs/heads/other", await client.GitAsync("ls-remote", url, "refs/heads/other"));
        var archive = Path.Combine(client.Root, "archive.zip");
        await client.GitAsync("archive", "--remote=" + smart, "--format=zip", "--output=" + archive, "main");
        using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
        using var content = new StreamReader(Assert.Single(zip.Entries).Open());
        Assert.Equal("native", await content.ReadToEndAsync(Cancellation));
    }

    [Fact]
    public async Task SmartReceivePack_AcceptsTheSameSignedPushAsNativeGit()
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo");
        var official = await client.InitNativeAsync();
        var officialDirectory = new Uri(official).LocalPath;
        await client.GitAsync("--git-dir=" + officialDirectory, "config", "receive.certNonceSeed", "native-fixture");
        await client.InitSourceAsync();
        var tip = await client.CommitAsync("signed");
        var key = Path.Combine(client.Root, "signing-key");
        (await client.RunProcessAsync("ssh-keygen", client.Root,
            ["-q", "-t", "ed25519", "-N", "", "-f", key, "-C", "gitremote-system"])).Require();

        string[] args = ["-C", client.Source, "-c", "gpg.format=ssh", "-c", "user.signingkey=" + key, "push", "--signed"];
        await client.GitAsync([.. args, official, "main"]);
        await client.GitAsync([.. args, url + "?transport=smart", "main"]);
        Assert.Equal(await client.GitAsync("ls-remote", official), await client.GitAsync("ls-remote", url));
        await VerifyNativeAsync(graph, client, tip);
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task Gc_DefaultRetentionAndExplicitPrune_MatchNativeGit(string format)
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo", "--object-format", format);
        await client.InitSourceAsync(format);
        var main = await client.CommitAsync("main");
        await client.GitAsync("-C", client.Source, "push", url, "main");
        await client.GitAsync("-C", client.Source, "switch", "-qc", "topic");
        var removed = await client.CommitAsync("topic");
        await client.GitAsync("-C", client.Source, "push", url, "topic");
        await client.GitAsync("-C", client.Source, "push", url, "--delete", "topic");
        await client.GitAsync("-C", client.Source, "switch", "-qc", "aged", "main");
        var expired = await client.CommitAsync("old unreachable");
        graph.Now = DateTimeOffset.UtcNow.AddDays(-30);
        await client.GitAsync("-C", client.Source, "push", url, "aged");
        graph.Now = DateTimeOffset.UtcNow;
        await client.GitAsync("-C", client.Source, "push", url, "--delete", "aged");

        var source = Path.Combine(client.Root, "gc-baseline.git");
        graph.CopyNativeRepo("GitRepositories/repo.git", source);
        await client.GitAsync("--git-dir=" + source, "gc");
        await client.GitAsync("onedrive", "gc", "repo", "--apply");
        var current = await VerifyNativeAsync(graph, client, main, "default-gc");
        Assert.Equal((await client.RunAsync("--git-dir=" + source, "cat-file", "-e", removed)).ExitCode,
            (await client.RunAsync("--git-dir=" + current, "cat-file", "-e", removed)).ExitCode);
        Assert.Equal(0, (await client.RunAsync("--git-dir=" + current, "cat-file", "-e", removed)).ExitCode);
        Assert.NotEqual(0, (await client.RunAsync("--git-dir=" + current, "cat-file", "-e", expired)).ExitCode);
        Assert.Equal((await client.RunAsync("--git-dir=" + source, "cat-file", "-e", expired)).ExitCode,
            (await client.RunAsync("--git-dir=" + current, "cat-file", "-e", expired)).ExitCode);

        await client.GitAsync("--git-dir=" + source, "gc", "--prune=now");
        await client.GitAsync("onedrive", "gc", "repo", "--apply", "--prune=now");
        current = await VerifyNativeAsync(graph, client, main, "explicit-gc");
        Assert.NotEqual(0, (await client.RunAsync("--git-dir=" + current, "cat-file", "-e", removed)).ExitCode);
        Assert.Equal((await client.RunAsync("--git-dir=" + source, "cat-file", "-e", removed)).ExitCode,
            (await client.RunAsync("--git-dir=" + current, "cat-file", "-e", removed)).ExitCode);
    }

    [Theory]
    [InlineData(".pack")]
    [InlineData(".idx")]
    public async Task LostPushResponseAndTerminatedPackPromotion_KeepNativeRepositoryValid(string boundary)
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo");
        await client.InitSourceAsync();
        var first = await client.CommitAsync("first");
        graph.FailNext("PUT", "packed-refs", afterApply: true);
        await client.GitAsync("-C", client.Source, "push", url, "main");
        await VerifyNativeAsync(graph, client, first, "lost-ack");

        await client.CommitAsync("second");
        using (var pause = graph.PauseNext("PATCH", boundary, afterApply: true))
        using (var process = client.Start("git", client.Source, ["push", url, "main"], redirectInput: false))
        {
            try
            {
                await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(12), Cancellation);
                Git.Kill(process);
                await process.WaitForExitAsync(Cancellation);
            }
            finally { Git.Kill(process); }
        }
        await VerifyNativeAsync(graph, client, first, "killed");
        var status = await client.GitAsync("onedrive", "status", "repo");
        var orphan = Assert.Single(status.Split('\n').Skip(1)).Split('\t')[0];
        await client.GitAsync("onedrive", "recover", "repo", orphan);
        await client.GitAsync("-C", client.Source, "push", url, "main");
    }

    [Fact]
    public async Task InterruptedGc_ResumesExactDeletionPlanWithoutLosingRefs()
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo");
        await client.InitSourceAsync();
        await client.CommitAsync("first");
        await client.GitAsync("-C", client.Source, "push", url, "main");
        var second = await client.CommitAsync("second");
        await client.GitAsync("-C", client.Source, "push", url, "main");
        graph.FailNext("DELETE", ".idx", afterApply: true);
        Assert.NotEqual(0, (await client.RunAsync("onedrive", "gc", "repo", "--apply")).ExitCode);
        await VerifyNativeAsync(graph, client, second, "gc-interrupted");
        var status = await client.GitAsync("onedrive", "status", "repo");
        var maintenance = status.Split('\n')[0]["GC maintenance: ".Length..].TrimEnd('\r');
        graph.FailNext("DELETE", ".pack", afterApply: true);
        Assert.NotEqual(0, (await client.RunAsync("onedrive", "recover", "repo", maintenance)).ExitCode);
        await VerifyNativeAsync(graph, client, second, "gc-recovery-interrupted");
        await client.GitAsync("onedrive", "recover", "repo", maintenance);
        await VerifyNativeAsync(graph, client, second, "gc-recovered");
        Assert.Equal("Repository is ready.", await client.GitAsync("onedrive", "status", "repo"));
    }

    [Fact]
    public async Task Gc_DrainsAdvertisedFetchAndLeavesOtherRepositoriesAvailable()
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo");
        var other = await client.GitAsync("onedrive", "init", "other");
        await client.InitSourceAsync();
        var tip = await client.CommitAsync("protected");
        await client.GitAsync("-C", client.Source, "push", url, "main");

        using var reader = client.Start(client.HelperPath, client.Source, ["origin", url], redirectInput: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await Wire.SendAsync(reader.StandardInput.BaseStream, "capabilities\nlist\n", timeout.Token);
        for (var response = 0; response < 2; response++)
            while (await Wire.ReadLineAsync(reader.StandardOutput.BaseStream, timeout.Token) is { Length: > 0 }) { }
        using var gc = client.Start("git", client.Root, ["onedrive", "gc", "repo", "--apply"], redirectInput: false);
        try
        {
            while (!(await client.GitAsync("onedrive", "status", "repo")).StartsWith("GC maintenance:", StringComparison.Ordinal))
                await Task.Delay(100, timeout.Token);
            Assert.False(gc.HasExited);
            Assert.NotEqual(0, (await client.RunAsync("ls-remote", url)).ExitCode);
            Assert.Equal(0, (await client.RunAsync("ls-remote", other)).ExitCode);
            reader.StandardInput.Close();
            await reader.WaitForExitAsync(timeout.Token);
            await gc.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, gc.ExitCode);
            await VerifyNativeAsync(graph, client, tip);
        }
        finally
        {
            Git.Kill(reader);
            Git.Kill(gc);
        }
    }

    [Fact]
    public async Task LargeUpload_ResumesSliceAndPublishesVerifiedHistory()
    {
        await using var graph = await GraphFixture.StartAsync(Cancellation);
        using var client = new PrototypeClient(graph);
        var url = await client.GitAsync("onedrive", "init", "repo");
        await client.InitSourceAsync();
        var data = new byte[11 * 1024 * 1024];
        new Random(934).NextBytes(data);
        await File.WriteAllBytesAsync(Path.Combine(client.Source, "large.dat"), data, Cancellation);
        await client.GitAsync("-C", client.Source, "add", "large.dat");
        var expect = await client.CommitAsync("large");
        graph.FailNext("PUT", ".pack", afterApply: false);
        await client.GitAsync("-C", client.Source, "push", url, "main");
        Assert.True(graph.Calls.Count(call => call.Method == "PUT" && call.Name.EndsWith(".pack", StringComparison.Ordinal)) >= 3);
        await VerifyNativeAsync(graph, client, expect);
    }

    private static async Task<string> VerifyNativeAsync(GraphFixture graph, PrototypeClient client, string expected,
        string name = "snapshot")
    {
        var snapshot = Path.Combine(client.Root, name + ".git");
        graph.CopyNativeRepo("GitRepositories/repo.git", snapshot);
        await client.GitAsync("--git-dir=" + snapshot, "fsck", "--full", "--no-dangling");
        var native = Path.Combine(client.Root, name + "-native-clone");
        await client.GitAsync("clone", "--no-local", snapshot, native);
        Assert.Equal(expected, await client.GitAsync("-C", native, "rev-parse", "HEAD"));
        return snapshot;
    }
}
