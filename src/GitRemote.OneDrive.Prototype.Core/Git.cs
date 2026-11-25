using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GitRemote.OneDrive.Prototype;

internal sealed record GitResult(int ExitCode, string Output, string Error)
{
    internal string Require()
    {
        if (ExitCode != 0)
            throw new IOException(Error.Trim().Length > 0 ? Error.Trim() : $"Git exited with code {ExitCode}.");
        return Output.TrimEnd('\r', '\n');
    }
}

internal sealed class Git(string? directory, CancellationToken cancellation, bool isolated = false)
{
    internal CancellationToken Cancellation => cancellation;

    internal async Task<GitResult> RunAsync(string[] args, string? input = null, Stream? fileInput = null,
        Stream? fileOutput = null, string? alternateObjects = null)
    {
        var start = StartInfo(args, alternateObjects);
        start.RedirectStandardInput = input is not null || fileInput is not null;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        using var cancellationRegistration = cancellation.Register(() => Kill(process));

        var error = process.StandardError.ReadToEndAsync(cancellation);
        var output = fileOutput is null ? process.StandardOutput.ReadToEndAsync(cancellation) : Task.FromResult("");
        var copy = fileOutput is null
            ? Task.CompletedTask : process.StandardOutput.BaseStream.CopyToAsync(fileOutput, cancellation);
        var send = start.RedirectStandardInput ? WriteInputAsync(process, input, fileInput) : Task.CompletedTask;
        await Task.WhenAll(process.WaitForExitAsync(cancellation), send, output, error, copy);
        return new(process.ExitCode, await output, await error);
    }

    internal async Task<string> CheckedAsync(string[] args, string? input = null, Stream? fileInput = null,
        Stream? fileOutput = null, string? alternateObjects = null) =>
        (await RunAsync(args, input, fileInput, fileOutput, alternateObjects)).Require();

    internal ProcessStartInfo StartInfo(string[] args, string? alternateObjects = null)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory ?? Environment.CurrentDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (isolated)
        {
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        }
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["LC_ALL"] = "C";
        if (alternateObjects is not null)
            start.Environment["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = alternateObjects;
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        return start;
    }

    internal async Task<string> ResolveAsync(string expression) =>
        (await RunAsync(["rev-parse", "--verify", "--end-of-options", expression])).Require();

    internal async Task<HashSet<string>> MissingAsync(IEnumerable<string> roots, string? alternateObjects = null)
    {
        var revisions = roots.Distinct(StringComparer.Ordinal).ToArray();
        if (revisions.Length == 0)
            return [];
        var result = await CheckedAsync(["rev-list", "--objects", "--no-object-names", "--missing=print", "--stdin"],
            string.Join('\n', revisions) + "\n", alternateObjects: alternateObjects);
        return result.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith('?')).Select(line => line[1..]).ToHashSet(StringComparer.Ordinal);
    }

    internal async Task<string[]> MakePacksAsync(string prefix, IReadOnlyList<string> roots, IEnumerable<string> excludes)
    {
        if (roots.Count == 0)
            return [];
        var input = string.Join('\n', roots.Concat(excludes.Select(id => "^" + id))) + "\n";
        var hashes = await CheckedAsync(
            ["pack-objects", "--revs", "--delta-base-offset", "--index-version=2", prefix], input);
        return hashes.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    internal static async Task<Git> InitBareAsync(string path, string format, string branch, CancellationToken cancellation)
    {
        Directory.CreateDirectory(path);
        var git = new Git(path, cancellation, isolated: true);
        await git.CheckedAsync(["init", "--bare", "--template=", "--ref-format=files",
            "--object-format=" + format, "--initial-branch=" + branch]);
        return git;
    }

    internal static int HashLength(string format) => format switch
    {
        "sha1" => 40,
        "sha256" => 64,
        _ => throw new IOException($"Unsupported Git object format '{format}'.")
    };

    internal static bool IsId(string id, int length) =>
        id.Length == length && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private async Task WriteInputAsync(Process process, string? input, Stream? source)
    {
        await using var stdin = process.StandardInput.BaseStream;
        try
        {
            if (source is not null)
                await source.CopyToAsync(stdin, cancellation);
            else
                await stdin.WriteAsync(Encoding.UTF8.GetBytes(input!), cancellation);
        }
        catch (IOException) when (process.HasExited)
        {
            // Git's exit status and stderr describe an early rejection.
        }
    }
}

internal sealed class Scratch : IDisposable
{
    internal string Path { get; } = Directory.CreateTempSubdirectory("git-onedrive-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed record GitRef(string Id, string? Peeled = null);

internal sealed class PackedRefs(int hashLength, Dictionary<string, GitRef>? refs = null)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal Dictionary<string, GitRef> Values { get; } = refs ?? new(StringComparer.Ordinal);
    internal int HashLength => hashLength;

    internal static PackedRefs Parse(byte[] bytes, int hashLength)
    {
        var refs = new PackedRefs(hashLength);
        string? previous = null;
        foreach (var raw in Utf8.GetString(bytes).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith('^'))
            {
                if (previous is null || !Git.IsId(line[1..], hashLength) || refs.Values[previous].Peeled is not null)
                    throw new IOException("Malformed peeled reference in packed-refs.");
                refs.Values[previous] = refs.Values[previous] with { Peeled = line[1..] };
                continue;
            }
            var fields = line.Split(' ', 2);
            if (fields.Length != 2 || !Git.IsId(fields[0], hashLength) || !IsRefName(fields[1])
                || !refs.Values.TryAdd(fields[1], new(fields[0])))
                throw new IOException("Malformed or duplicate reference in packed-refs.");
            previous = fields[1];
        }
        return refs;
    }

    internal byte[] Serialize(bool fence = false)
    {
        var text = new StringBuilder(fence ? "# pack-refs with: fully-peeled peeled \n" : "# pack-refs with: peeled fully-peeled \n");
        foreach (var (name, value) in Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append(value.Id).Append(' ').Append(name).Append('\n');
            if (value.Peeled is not null)
                text.Append('^').Append(value.Peeled).Append('\n');
        }
        return Utf8.GetBytes(text.ToString());
    }

    internal static bool IsRefName(string name) =>
        name.StartsWith("refs/", StringComparison.Ordinal) && name.Length > 5
        && !name.Contains("..", StringComparison.Ordinal) && !name.Contains("@{", StringComparison.Ordinal)
        && !name.EndsWith('.') && name.IndexOfAny([' ', '~', '^', ':', '?', '*', '[', '\\']) < 0
        && !name.Any(char.IsControl)
        && name.Split('/').All(part => part.Length != 0 && !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));
}
