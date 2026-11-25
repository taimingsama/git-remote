using System.Text;

namespace GitRemote.OneDrive.Prototype;

internal sealed class RemoteHelper(OneDriveRepository repository, Stream input, Stream output)
{
    private readonly RepositoryCoordinator _coordinator = new(repository);
    private readonly PackStore _packs = new(repository);
    private readonly Git _git = new(null, repository.Storage.Cancellation);
    private readonly Dictionary<string, string?> _leases = new(StringComparer.Ordinal);
    private CancellationToken Cancellation => repository.Storage.Cancellation;

    internal async Task RunAsync()
    {
        RepositorySession? session = null;
        PackedRefs? advertised = null;
        PackedRefs? prepared = null;
        var atomic = false;
        var dryRun = false;
        var connectivity = false;
        var objectFormat = false;
        var needsNativeRead = false;

        try
        {
            while (await Wire.ReadLineAsync(input, Cancellation) is { Length: > 0 } line)
            {
                if (line == "capabilities")
                {
                    var capabilities = "fetch\npush\noption\nget\ncheck-connectivity\nobject-format\n";
                    capabilities += repository.Address.Mode switch
                    {
                        TransportMode.Direct or TransportMode.Smart => "connect\n",
                        TransportMode.Stateless => "stateless-connect\n",
                        _ => ""
                    };
                    await SendAsync(capabilities + "\n");
                }
                else if (line.StartsWith("option ", StringComparison.Ordinal))
                {
                    var args = line[7..].Split(' ', 2);
                    if (args.Length != 2)
                    {
                        await SendAsync("error missing option value\n");
                        continue;
                    }
                    var value = Wire.Unquote(args[1]);
                    if (args[0] == "cas")
                    {
                        var fields = value.Split(':', 2);
                        if (fields.Length != 2 || !PackedRefs.IsRefName(fields[0]) || !Git.IsId(fields[1], repository.HashLength))
                        {
                            await SendAsync("error invalid lease\n");
                            continue;
                        }
                        _leases[fields[0]] = fields[1].All(c => c == '0') ? null : fields[1];
                        await SendAsync("ok\n");
                    }
                    else if (args[0] is "atomic" or "dry-run" or "check-connectivity" or "object-format"
                             or "progress" or "cloning" or "force-if-includes")
                    {
                        if (value is not ("true" or "false"))
                        {
                            await SendAsync("error expected true or false\n");
                            continue;
                        }
                        var flag = value == "true";
                        switch (args[0])
                        {
                            case "atomic": atomic = flag; break;
                            case "dry-run": dryRun = flag; break;
                            case "check-connectivity": connectivity = flag; break;
                            case "object-format": objectFormat = flag; break;
                        }
                        await SendAsync("ok\n");
                    }
                    else if (args[0] == "verbosity" && int.TryParse(value, out _) ||
                             args[0] == "pushcert" && value is "if-asked" or "false")
                    {
                        await SendAsync("ok\n");
                    }
                    else if (args[0] is "depth" or "deepen-since" or "deepen-not" or "deepen-relative"
                             or "filter" or "from-promisor" or "no-dependents" or "update-shallow" or "refetch"
                             or "pushcert" or "push-option")
                    {
                        if (repository.Address.Mode != TransportMode.Direct && args[0] is not ("pushcert" or "push-option"))
                        {
                            needsNativeRead = true;
                            await SendAsync("ok\n");
                            continue;
                        }
                        var message = "use a OneDrive URL with ?transport=smart for this Git option";
                        await SendAsync("error " + message + "\n");
                        throw new IOException(message);
                    }
                    else
                    {
                        await SendAsync("unsupported\n");
                    }
                }
                else if (line is "list" or "list for-push")
                {
                    if (needsNativeRead)
                        throw new IOException("The negotiated transport did not connect to native Git. Use ?transport=smart.");
                    if (line == "list")
                        session ??= await _coordinator.EnterAsync("read");
                    advertised = prepared ?? (await repository.ReadRefsAsync()).Refs;
                    prepared = null;
                    var refs = new StringBuilder();
                    if (objectFormat)
                        refs.Append(":object-format ").Append(repository.ObjectFormat).Append('\n');
                    if (advertised.Values.ContainsKey(repository.Head))
                        refs.Append('@').Append(repository.Head).Append(" HEAD\n");
                    foreach (var (name, value) in advertised.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        refs.Append(value.Id).Append(' ').Append(name).Append('\n');
                        if (line == "list" && value.Peeled is not null)
                            refs.Append(value.Peeled).Append(' ').Append(name).Append("^{}\n");
                    }
                    refs.Append('\n');
                    await SendAsync(refs.ToString());
                }
                else if (line.StartsWith("fetch ", StringComparison.Ordinal))
                {
                    RequireSnapshot(session, advertised);
                    var lines = await ReadBatchAsync(line, "fetch ");
                    var roots = lines.Select(request =>
                    {
                        var fields = request.Split(' ', 2);
                        if (fields.Length != 2 || !Git.IsId(fields[0], repository.HashLength))
                            throw new IOException("Invalid fetch request.");
                        var name = fields[1];
                        var direct = advertised!.Values.GetValueOrDefault(name)?.Id;
                        var peeled = name.EndsWith("^{}", StringComparison.Ordinal)
                            ? advertised.Values.GetValueOrDefault(name[..^3])?.Peeled : null;
                        if (fields[0] != direct && fields[0] != peeled && !advertised.Values.Values.Any(value => value.Id == fields[0]))
                            throw new IOException("The requested object was not advertised. Use smart transport for Git object negotiation.");
                        return fields[0];
                    }).ToArray();
                    var keep = await new FetchService(repository, _packs, _git).FetchAsync(roots);
                    if (keep is not null)
                        await SendAsync("lock " + keep + "\n");
                    await SendAsync(connectivity ? "connectivity-ok\n\n" : "\n");
                }
                else if (line.StartsWith("push ", StringComparison.Ordinal))
                {
                    if (advertised is null)
                        throw new IOException("Git must list references before fetching or pushing.");
                    session ??= await _coordinator.EnterAsync("push");
                    await new FetchService(repository, _packs, _git).EnsureObjectFormatAsync();
                    if (await _git.CheckedAsync(["rev-parse", "--is-shallow-repository"]) == "true")
                        throw new IOException("Use ?transport=smart when pushing from a shallow repository.");
                    var service = new PushService(repository, _packs, _git);
                    var changes = new List<PushUpdate>();
                    foreach (var request in await ReadBatchAsync(line, "push "))
                        changes.Add(await service.ResolveAsync(advertised, request, _leases));
                    var result = await service.PublishAsync(changes, session, atomic, dryRun);
                    var statuses = result.DistinctBy(status => status.Name)
                        .Select(status => status.Error is null ? "ok " + status.Name : "error " + status.Name + " " + status.Error);
                    await SendAsync(string.Join('\n', statuses) + "\n\n");
                }
                else if (line.StartsWith("get ", StringComparison.Ordinal))
                {
                    session ??= await _coordinator.EnterAsync("read");
                    await DownloadAsync(line[4..]);
                }
                else if (line.StartsWith("connect ", StringComparison.Ordinal) || line.StartsWith("stateless-connect ", StringComparison.Ordinal))
                {
                    var fields = line.Split(' ', 2);
                    var stateless = fields[0] == "stateless-connect";
                    var service = fields[1];
                    if (!stateless && repository.Address.Mode == TransportMode.Direct && service == "git-upload-pack"
                        && advertised is null)
                    {
                        session ??= await _coordinator.EnterAsync("read");
                        prepared = (await repository.ReadRefsAsync()).Refs;
                        // Only native v2 can announce an unborn symbolic HEAD.
                        if (!prepared.Values.ContainsKey(repository.Head))
                        {
                            await new SmartTransport(repository, _packs).ConnectAsync(service, false, session, input, output);
                            return;
                        }
                    }
                    if ((!stateless && repository.Address.Mode == TransportMode.Smart
                        && service is "git-upload-pack" or "git-receive-pack" or "git-upload-archive")
                        || (stateless && repository.Address.Mode == TransportMode.Stateless && service == "git-upload-pack"))
                    {
                        session ??= await _coordinator.EnterAsync(service == "git-receive-pack" ? "push" : "read");
                        await new SmartTransport(repository, _packs).ConnectAsync(service, stateless, session, input, output);
                        return;
                    }
                    await SendAsync("fallback\n");
                }
                else
                {
                    throw new IOException("Unknown Git remote helper command.");
                }
            }
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
        }
    }

    private static void RequireSnapshot(RepositorySession? session, PackedRefs? refs)
    {
        if (session is null || refs is null)
            throw new IOException("Git must list references before fetching or pushing.");
    }

    private async Task<string[]> ReadBatchAsync(string first, string prefix)
    {
        var commands = new List<string> { first[prefix.Length..] };
        while (await Wire.ReadLineAsync(input, Cancellation) is { } line)
        {
            if (line.Length == 0)
                return commands.ToArray();
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                commands.Add(line[prefix.Length..]);
            else
                throw new IOException("Unexpected line in Git command batch.");
        }
        throw new IOException("Incomplete Git command batch.");
    }

    private async Task DownloadAsync(string command)
    {
        var fields = command.Split(' ', 2);
        if (fields.Length != 2)
            throw new IOException("Malformed Git get command.");
        var address = Wire.Unquote(fields[0]);
        var path = Wire.Unquote(fields[1]);
        var prefix = repository.Address with { Mode = TransportMode.Direct };
        if (!address.StartsWith(prefix.Url + "/", StringComparison.Ordinal))
            throw new IOException("Git get requires a file in this OneDrive repository.");
        var file = string.Join('/', address[(prefix.Url.Length + 1)..].Split('/').Select(Uri.UnescapeDataString));
        if (file is not ("HEAD" or "config" or "packed-refs") && !IsObjectPath(file))
            throw new IOException("Git get requires a native repository file.");
        var item = await repository.Storage.FindAsync(repository.Directory.Id, file)
            ?? throw new IOException($"Remote file '{file}' does not exist.");
        var temp = path + ".temp";
        var version = temp + ".etag";
        if (!File.Exists(version) || await File.ReadAllTextAsync(version, Cancellation) != item.ETag)
        {
            File.Delete(temp);
            await File.WriteAllTextAsync(version, item.ETag, Cancellation);
        }
        await using (var stream = new FileStream(temp, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            if (stream.Length > item.Size)
                stream.SetLength(0);
            stream.Position = stream.Length;
            if (stream.Length != item.Size)
                await repository.Storage.DownloadAsync(item, stream, stream.Position);
            if (stream.Length != item.Size)
                throw new IOException("OneDrive download is incomplete.");
        }
        if ((await repository.Storage.FindByIdAsync(item.Id))?.ETag != item.ETag)
            throw new IOException("OneDrive file changed during download. Retry.");
        File.Move(temp, path, overwrite: true);
        File.Delete(version);
    }

    private bool IsObjectPath(string path)
    {
        const string prefix = "objects/pack/pack-";
        var end = prefix.Length + repository.HashLength;
        return path.StartsWith(prefix, StringComparison.Ordinal) && path.Length > end
            && Git.IsId(path[prefix.Length..end], repository.HashLength)
            && path[end..] is ".pack" or ".idx" or ".mtimes";
    }

    private Task SendAsync(string message) => Wire.SendAsync(output, message, Cancellation);
}

internal static class Wire
{
    internal static async Task<string?> ReadLineAsync(Stream input, CancellationToken cancellation)
    {
        using var line = new MemoryStream();
        var next = new byte[1];
        while (await input.ReadAsync(next, cancellation) != 0)
        {
            if (next[0] == '\n')
                return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
            line.WriteByte(next[0]);
            if (line.Length > 1024 * 1024)
                throw new IOException("Git remote helper command is too long.");
        }
        if (line.Length != 0)
            throw new IOException("Incomplete Git remote helper command.");
        return null;
    }

    internal static async Task SendAsync(Stream output, string message, CancellationToken cancellation)
    {
        await output.WriteAsync(Encoding.UTF8.GetBytes(message), cancellation);
        await output.FlushAsync(cancellation);
    }

    internal static string Unquote(string text)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
            return text;
        var input = Encoding.UTF8.GetBytes(text[1..^1]);
        using var output = new MemoryStream();
        for (var i = 0; i < input.Length; i++)
        {
            var value = input[i];
            if (value == '\\')
            {
                if (++i == input.Length)
                    throw new IOException("Invalid Git option quoting.");
                value = input[i];
                if (value is >= (byte)'0' and <= (byte)'3' && i + 2 < input.Length
                    && input[i + 1] is >= (byte)'0' and <= (byte)'7' && input[i + 2] is >= (byte)'0' and <= (byte)'7')
                {
                    value = (byte)((value - '0') * 64 + (input[i + 1] - '0') * 8 + input[i + 2] - '0');
                    i += 2;
                }
                else
                {
                    value = value switch
                    {
                        (byte)'n' => (byte)'\n', (byte)'r' => (byte)'\r', (byte)'t' => (byte)'\t',
                        (byte)'b' => (byte)'\b', (byte)'f' => (byte)'\f', (byte)'v' => (byte)'\v',
                        (byte)'a' => (byte)'\a', (byte)'"' or (byte)'\\' => value,
                        _ => throw new IOException("Invalid Git option escape.")
                    };
                }
            }
            output.WriteByte(value);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
