using System.Diagnostics;
using GitRemote.OneDrive.Prototype;
using Xunit;

namespace GitRemote.OneDrive.Prototype.IntegrationTests.SystemTests;

internal sealed class PrototypeClient : IDisposable
{
    private readonly Scratch _scratch = new();
    private readonly Dictionary<string, string> _environment;
    private readonly TimeSpan _commandTimeout;
    internal string Root => _scratch.Path;
    internal string Source => Path.Combine(Root, "source");
    internal string HelperPath => Path.Combine(Root, "app",
        OperatingSystem.IsWindows() ? "git-remote-onedrive-prototype.exe" : "git-remote-onedrive-prototype");

    internal PrototypeClient(GraphFixture graph) : this(graph.Endpoint, null) { }

    internal PrototypeClient(string? endpoint, string? token)
    {
        _commandTimeout = TimeSpan.FromSeconds(endpoint is null ? 300 : 60);
        var host = Path.Combine(Root, "app");
        Directory.CreateDirectory(host);
        var components = endpoint is null ? "ProductionHost" : "SystemTestHost";
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, components)))
            File.Copy(file, Path.Combine(host, Path.GetFileName(file)));
        const string name = "GitRemote.OneDrive.Prototype.TestHost";
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        if (endpoint is not null)
            foreach (var alias in new[] { "git-onedrive", "git-remote-onedrive-prototype" })
                File.Copy(Path.Combine(host, name + suffix), Path.Combine(host, alias + suffix), overwrite: true);

        _environment = new()
        {
            ["PATH"] = host + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["GITREMOTE_PROTOTYPE_CACHE_DIRECTORY"] = Path.Combine(Root, "cache"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["GIT_AUTHOR_NAME"] = "GitRemote System",
            ["GIT_AUTHOR_EMAIL"] = "system@example.invalid",
            ["GIT_COMMITTER_NAME"] = "GitRemote System",
            ["GIT_COMMITTER_EMAIL"] = "system@example.invalid",
            ["GIT_COMMITTER_DATE"] = "2025-01-02T10:00:00Z",
            ["GIT_AUTHOR_DATE"] = "2025-01-02T10:00:00Z",
            ["LC_ALL"] = "C"
        };
        if (endpoint is not null)
            _environment["GITREMOTE_TEST_GRAPH_ENDPOINT"] = endpoint;
        if (token is not null)
            _environment["GITREMOTE_PROTOTYPE_ACCESS_TOKEN"] = token;
    }

    internal async Task<string> GitAsync(params string[] args) => (await RunAsync(args)).Require();

    internal Task<GitResult> RunAsync(params string[] args) => RunAsync(Root, args);

    internal Task<GitResult> RunAsync(string directory, string[] args) => RunProcessAsync("git", directory, args);

    internal async Task<GitResult> RunProcessAsync(string program, string directory, string[] args)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var process = Start(program, directory, args, redirectInput: false);
        using var terminate = cancellation.Register(() => Git.Kill(process));
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        var error = process.StandardError.ReadToEndAsync(cancellation);
        try
        {
            await process.WaitForExitAsync(cancellation).WaitAsync(_commandTimeout, cancellation);
            return new(process.ExitCode, await output, await error);
        }
        finally { Git.Kill(process); }
    }

    internal Process Start(string program, string directory, string[] args, bool redirectInput)
    {
        var info = new ProcessStartInfo(program)
        {
            WorkingDirectory = directory,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var (key, value) in _environment)
            info.Environment[key] = value;
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }

    internal async Task InitSourceAsync(string format = "sha1")
    {
        await GitAsync("init", "--object-format=" + format, "--initial-branch=main", Source);
        await GitAsync("-C", Source, "config", "commit.gpgsign", "false");
    }

    internal async Task<string> CommitAsync(string value, string path = "file.txt")
    {
        await File.WriteAllTextAsync(Path.Combine(Source, path), value, TestContext.Current.CancellationToken);
        await GitAsync("-C", Source, "add", "--", path);
        await GitAsync("-C", Source, "commit", "-qm", value);
        return await GitAsync("-C", Source, "rev-parse", "HEAD");
    }

    internal async Task<string> InitNativeAsync(string objectFormat = "sha1")
    {
        var directory = Path.Combine(Root, "official.git");
        await GitAsync("init", "--bare", "--initial-branch=main", "--object-format=" + objectFormat, directory);
        return new Uri(directory).AbsoluteUri;
    }

    public void Dispose() => _scratch.Dispose();
}
