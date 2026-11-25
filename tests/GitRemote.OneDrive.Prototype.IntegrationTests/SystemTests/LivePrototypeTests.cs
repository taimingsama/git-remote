using GitRemote.OneDrive.Prototype;
using Xunit;

namespace GitRemote.OneDrive.Prototype.IntegrationTests.SystemTests;

[Trait("Category", "OneDrivePrototypeLive")]
public sealed class LivePrototypeTests(ITestOutputHelper output)
{
    private const string AccessTokenVariable = "ONEDRIVE_TEST_ACCESS_TOKEN";

    public static bool HasAccessToken => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AccessTokenVariable));

    [Fact(Skip = "Set ONEDRIVE_TEST_ACCESS_TOKEN to enable this live OneDrive test.",
        SkipUnless = nameof(HasAccessToken), Timeout = 720_000)]
    public async Task OneDrive_PushCloneGcAndRawDownload_PreserveNativeRepository()
    {
        var token = Environment.GetEnvironmentVariable(AccessTokenVariable)!.Trim();
        var cancellation = TestContext.Current.CancellationToken;
        var name = "gitremote-system-" + Guid.NewGuid().ToString("N");
        output.WriteLine($"Temporary OneDrive directory: {name}");
        using var client = new PrototypeClient(endpoint: null, token);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var storage = new OneDriveStorage(http, (_, _) => Task.FromResult(token), cancellation);
        var root = await storage.RootAsync();

        Exception? primaryFailure = null;
        try
        {
            var url = await client.GitAsync("onedrive", "init", name + "/repo");
            await client.InitSourceAsync();
            var first = await client.CommitAsync("first");
            await client.GitAsync("-C", client.Source, "push", url, "main");
            var clone = Path.Combine(client.Root, "online");
            await client.GitAsync("clone", url, clone);
            Assert.Equal(first, await client.GitAsync("-C", clone, "rev-parse", "HEAD"));
            output.WriteLine("First push and helper clone verified.");

            var second = await client.CommitAsync("second");
            await client.GitAsync("-C", client.Source, "push", url, "main");
            await client.GitAsync("-C", clone, "fetch", "origin");
            Assert.Equal(second, await client.GitAsync("-C", clone, "rev-parse", "origin/main"));
            var repository = await new RepositoryCatalog(storage).OpenAsync(RepositoryAddress.Parse(url));
            Assert.Equal(second, await VerifyRawCloneAsync("before", repository));
            output.WriteLine("Incremental fetch and native clone before GC verified.");
            await client.GitAsync("onedrive", "gc", url, "--apply");
            Assert.Equal(second, await VerifyRawCloneAsync("after", repository));
            output.WriteLine("Native clone after GC verified.");
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(75));
                var cleanupStore = storage.WithCancellation(cleanup.Token);
                var folder = await cleanupStore.FindAsync(root.Id, name);
                if (folder is { IsFolder: true } && folder.Name == name)
                {
                    await cleanupStore.DeleteAsync(folder.Id, folder.ETag);
                    output.WriteLine("Temporary OneDrive directory removed.");
                }
            }
            catch (Exception cleanupFailure)
            {
                var failure = new IOException($"Could not clean up temporary OneDrive directory '{name}'.", cleanupFailure);
                if (primaryFailure is not null)
                    throw new AggregateException(primaryFailure, failure);
                throw failure;
            }
        }

        async Task<string> VerifyRawCloneAsync(string mode, OneDriveRepository repository)
        {
            var snapshot = Path.Combine(client.Root, mode + ".git");
            await CopyAsync(repository.Directory.Id, snapshot, repositoryRoot: true);
            await client.GitAsync("--git-dir=" + snapshot, "fsck", "--full", "--no-dangling");
            var native = Path.Combine(client.Root, mode + "-native");
            await client.GitAsync("clone", "--no-local", snapshot, native);
            Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(native, "file.txt"), cancellation));
            return await client.GitAsync("-C", native, "rev-parse", "HEAD");
        }

        async Task CopyAsync(string id, string folder, bool repositoryRoot = false)
        {
            Directory.CreateDirectory(folder);
            foreach (var item in await storage.ListAsync(id))
            {
                if (repositoryRoot && item.Name == "gitremote")
                    continue;
                var target = Path.Combine(folder, item.Name);
                if (item.IsFolder)
                    await CopyAsync(item.Id, target);
                else
                {
                    await using var file = File.Create(target);
                    await storage.DownloadAsync(item, file);
                }
            }
        }
    }
}
