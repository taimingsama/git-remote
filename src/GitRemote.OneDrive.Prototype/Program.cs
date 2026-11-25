using System.ComponentModel;
using System.Diagnostics;

namespace GitRemote.OneDrive.Prototype;

public static class Program
{
    public static Task<int> Main(string[] args)
    {
        if (args is ["--proc-receive"])
        {
            return PrototypeRuntime.RunAsync(async cancellation =>
            {
                await SmartTransport.RunNativeHookAsync(cancellation);
                return 0;
            });
        }
        if (args is [{ } option, ..] && option.StartsWith("--", StringComparison.Ordinal))
        {
            var management = new[] { option[2..] }.Concat(args.Skip(1)).ToArray();
            if (management[0] == "gc" && management.Length > 2 && management[1] == "--apply")
                management = ["gc", management[2], "--apply", .. management.Skip(3)];
            return RunManagementAsync(management);
        }
        return RunAsync(args);
    }

    // Preserve the original helper's management switches for existing scripts.
    public static Task<int> RunManagementAsync(string[] args) => PrototypeRuntime.RunAsync(async cancellation =>
    {
        var name = OperatingSystem.IsWindows() ? "git-onedrive.exe" : "git-onedrive";
        var sibling = Path.Combine(AppContext.BaseDirectory, name);
        var start = new ProcessStartInfo(File.Exists(sibling) ? sibling : name);
        foreach (var argument in args)
            start.ArgumentList.Add(argument);

        Process process;
        try { process = Process.Start(start)!; }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            throw new IOException("Install git-onedrive alongside the helper or add it to PATH.", ex);
        }
        using (process)
        using (cancellation.Register(() => Git.Kill(process)))
        {
            await process.WaitForExitAsync(cancellation);
            return process.ExitCode;
        }
    });

    internal static Task<int> RunAsync(string[] args, HttpMessageHandler? transport = null,
        Func<bool, CancellationToken, Task<string>>? testToken = null) => PrototypeRuntime.RunAsync(async cancellation =>
    {
        if (args is not [_, var address])
            throw new ArgumentException("Git must supply a remote name and a OneDrive repository address.");

        using var runtime = new PrototypeRuntime(new Authentication(), transport, testToken, cancellation);
        // Git may ask for capabilities before it has set up a local object database.
        using var input = new BufferedStream(Console.OpenStandardInput());
        using var output = Console.OpenStandardOutput();
        var repository = await runtime.Catalog.OpenAsync(RepositoryAddress.Parse(address));
        await new RemoteHelper(repository, input, output).RunAsync();
        return 0;
    });
}
