using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace GitRemote.OneDrive.Prototype;

internal sealed class SmartTransport(OneDriveRepository repository, PackStore packs)
{
    private CancellationToken Cancellation => repository.Storage.Cancellation;

    internal async Task ConnectAsync(string service, bool stateless, RepositorySession session, Stream input, Stream output)
    {
        using var scratch = new Scratch();
        var snapshot = await repository.ReadRefsAsync();
        var git = await packs.MaterializeAsync(scratch.Path, snapshot.Bytes, await packs.InventoryAsync(), cachePacks: true);
        var config = await new Git(null, Cancellation).RunAsync(["config", "--get", "protocol.version"]);
        var version = config.ExitCode == 0 ? config.Output.Trim() : "2";

        if (stateless)
        {
            await StatelessAsync(git, input, output);
            return;
        }

        var arguments = new List<string>();
        var pipeName = "gitremote-" + Guid.NewGuid().ToString("N");
        using var hookCancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        Task? hook = null;
        if (service == "git-receive-pack")
        {
            var hookDirectory = Path.Combine(scratch.Path, "hooks");
            Directory.CreateDirectory(hookDirectory);
            var launcher = Path.Combine(hookDirectory, "proc-receive");
            await File.WriteAllTextAsync(launcher, "#!/bin/sh\nexec \"$GITREMOTE_PROTOTYPE_HOOK_EXE\" --proc-receive\n", Cancellation);
            var preReceive = Path.Combine(hookDirectory, "pre-receive");
            await File.WriteAllTextAsync(preReceive,
                "#!/bin/sh\nif test -n \"$GIT_PUSH_CERT\" && test \"$GIT_PUSH_CERT_NONCE_STATUS\" != OK; then\n" +
                "    printf '%s\\n' 'Invalid Git push certificate nonce.' >&2\n    exit 1\nfi\ncat >/dev/null\n", Cancellation);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(preReceive, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            arguments.AddRange(["-c", "core.hooksPath=" + hookDirectory, "-c", "receive.procReceiveRefs=refs",
                "-c", "receive.advertisePushOptions=true", "-c", "receive.autogc=false",
                "-c", "receive.certNonceSeed=" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32))]);
            hook = ServeHookAsync(pipeName, git, session, hookCancellation.Token);
        }
        if (service == "git-upload-pack")
            AddUploadOptions(arguments);
        arguments.Add(service[4..]);
        arguments.Add(".");
        var start = git.StartInfo(arguments.ToArray());
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.Environment["GIT_PROTOCOL"] = "version=" + (version is "0" or "1" or "2" ? version : "2");
        if (hook is not null)
        {
            start.Environment["GITREMOTE_PROTOTYPE_HOOK_EXE"] = Environment.ProcessPath
                ?? throw new IOException("The smart receive hook needs the installed helper executable.");
            start.Environment["GITREMOTE_PROTOTYPE_HOOK_PIPE"] = pipeName;
        }

        try
        {
            await Wire.SendAsync(output, "\n", Cancellation);
            using var process = Process.Start(start)!;
            using var cancel = Cancellation.Register(() => Git.Kill(process));
            using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
            _ = FeedAsync(input, process.StandardInput.BaseStream, inputCancellation.Token);
            var error = process.StandardError.ReadToEndAsync(Cancellation);
            await process.StandardOutput.BaseStream.CopyToAsync(output, Cancellation);
            await output.FlushAsync(Cancellation);
            await process.WaitForExitAsync(Cancellation);
            await inputCancellation.CancelAsync();
            if (process.ExitCode != 0)
                throw new IOException((await error).Trim());
        }
        finally
        {
            await hookCancellation.CancelAsync();
            if (hook is not null)
            {
                try { await hook; }
                catch (OperationCanceledException) when (hookCancellation.IsCancellationRequested) { }
            }
        }
    }

    private async Task StatelessAsync(Git git, Stream input, Stream output)
    {
        var args = new List<string>();
        AddUploadOptions(args);
        args.AddRange(["-c", "protocol.version=2", "upload-pack", "--stateless-rpc"]);
        var baseArguments = args.ToArray();
        await Wire.SendAsync(output, "\n", Cancellation);
        await RunStatelessRequestAsync(git, [.. baseArguments, "--advertise-refs", "."], Stream.Null, output);
        while (true)
        {
            using var request = new Scratch();
            await using var stream = new FileStream(Path.Combine(request.Path, "request"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
            while (true)
            {
                var packet = await Packet.ReadAsync(input, Cancellation);
                if (packet is null)
                    return;
                await stream.WriteAsync(packet.Header, Cancellation);
                await stream.WriteAsync(packet.Data, Cancellation);
                if (packet.IsFlush)
                    break;
            }
            stream.Position = 0;
            await RunStatelessRequestAsync(git, [.. baseArguments, "."], stream, output);
            await Wire.SendAsync(output, "0002", Cancellation);
        }
    }

    private async Task RunStatelessRequestAsync(Git git, string[] arguments, Stream input, Stream output)
    {
        var start = git.StartInfo(arguments);
        start.Environment["GIT_PROTOCOL"] = "version=2";
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        using var cancel = Cancellation.Register(() => Git.Kill(process));
        var send = FeedAsync(input, process.StandardInput.BaseStream, Cancellation);
        var error = process.StandardError.ReadToEndAsync(Cancellation);
        await process.StandardOutput.BaseStream.CopyToAsync(output, Cancellation);
        await output.FlushAsync(Cancellation);
        await Task.WhenAll(process.WaitForExitAsync(Cancellation), send);
        if (process.ExitCode != 0)
            throw new IOException((await error).Trim());
    }

    private static void AddUploadOptions(List<string> args) =>
        args.AddRange(["-c", "uploadpack.allowFilter=true", "-c", "uploadpack.allowReachableSHA1InWant=true",
            "-c", "uploadpack.allowAnySHA1InWant=true", "-c", "transfer.advertiseObjectInfo=true"]);

    private async Task ServeHookAsync(string pipeName, Git git, RepositorySession session, CancellationToken cancellation)
    {
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(cancellation);
        var version = await Packet.ReadTextAsync(pipe, cancellation);
        if (version is null || !version.StartsWith("version=1", StringComparison.Ordinal)
            || await Packet.ReadTextAsync(pipe, cancellation) is not "")
            throw new IOException("Invalid receive-pack hook negotiation.");
        var capabilities = version.Split('\0', 2).ElementAtOrDefault(1)?.Split(' ') ?? [];
        var atomic = capabilities.Contains("atomic");
        var pushOptions = capabilities.Contains("push-options");
        await Packet.WriteAsync(pipe, pushOptions ? "version=1\0push-options\n" : "version=1\n", cancellation);
        await Wire.SendAsync(pipe, "0000", cancellation);

        var changes = new List<PushUpdate>();
        var service = new PushService(repository, packs, git);
        var zero = new string('0', repository.HashLength);
        while (await Packet.ReadTextAsync(pipe, cancellation) is { Length: > 0 } command)
        {
            var fields = command.TrimEnd('\n').Split(' ', 3);
            if (fields.Length != 3 || !Git.IsId(fields[0], repository.HashLength) || !Git.IsId(fields[1], repository.HashLength)
                || !PackedRefs.IsRefName(fields[2]))
                throw new IOException("Malformed receive-pack update.");
            string? expected = fields[0] == zero ? null : fields[0];
            var name = fields[2];
            try
            {
                var value = fields[1] == zero ? null : await service.ResolveValueAsync(name, fields[1]);
                var rejection = value is null && expected is not null && name == repository.Head
                    ? "deletion of the current branch prohibited" : null;
                changes.Add(new(name, expected, value, rejection));
            }
            catch (IOException ex)
            {
                changes.Add(new(name, expected, null, PushService.OneLine(ex.Message)));
            }
        }
        if (pushOptions)
        {
            while (await Packet.ReadTextAsync(pipe, cancellation) is { Length: > 0 }) { }
        }
        var statuses = await service.PublishAsync(changes, session, atomic, dryRun: false);
        foreach (var status in statuses)
            await Packet.WriteAsync(pipe, status.Error is null
                ? $"ok {status.Name}\n" : $"ng {status.Name} {status.Error}\n", cancellation);
        await Wire.SendAsync(pipe, "0000", cancellation);
    }

    internal static async Task RunNativeHookAsync(CancellationToken cancellation)
    {
        var name = Environment.GetEnvironmentVariable("GITREMOTE_PROTOTYPE_HOOK_PIPE")
            ?? throw new IOException("Missing receive-pack hook pipe.");
        if (!name.StartsWith("gitremote-", StringComparison.Ordinal)
            || !Guid.TryParseExact(name["gitremote-".Length..], "N", out _))
            throw new IOException("Invalid receive-pack hook pipe.");
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(10000, cancellation);
        _ = FeedAsync(Console.OpenStandardInput(), pipe, cancellation, close: false);
        await pipe.CopyToAsync(Console.OpenStandardOutput(), cancellation);
    }

    private static async Task FeedAsync(Stream input, Stream target, CancellationToken cancellation, bool close = true)
    {
        try
        {
            await input.CopyToAsync(target, cancellation);
            await target.FlushAsync(cancellation);
        }
        catch (IOException) { }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (close)
                await target.DisposeAsync();
        }
    }
}

internal sealed record Packet(byte[] Header, byte[] Data)
{
    internal bool IsFlush => Header.AsSpan().SequenceEqual("0000"u8);

    internal static async Task<Packet?> ReadAsync(Stream input, CancellationToken cancellation)
    {
        var header = new byte[4];
        if (await input.ReadAsync(header.AsMemory(0, 1), cancellation) == 0)
            return null;
        await input.ReadExactlyAsync(header.AsMemory(1), cancellation);
        if (!int.TryParse(Encoding.ASCII.GetString(header), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size)
            || size == 3 || size > 65520)
            throw new IOException("Invalid Git packet length.");
        var data = new byte[Math.Max(size - 4, 0)];
        await input.ReadExactlyAsync(data, cancellation);
        return new(header, data);
    }

    internal static async Task<string?> ReadTextAsync(Stream input, CancellationToken cancellation)
    {
        var packet = await ReadAsync(input, cancellation);
        return packet is null ? null : Encoding.UTF8.GetString(packet.Data);
    }

    internal static async Task WriteAsync(Stream output, string text, CancellationToken cancellation)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 65516)
            throw new IOException("Git packet too long.");
        await output.WriteAsync(Encoding.ASCII.GetBytes((bytes.Length + 4).ToString("x4", CultureInfo.InvariantCulture)), cancellation);
        await output.WriteAsync(bytes, cancellation);
        await output.FlushAsync(cancellation);
    }
}
