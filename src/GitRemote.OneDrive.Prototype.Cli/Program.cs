namespace GitRemote.OneDrive.Prototype.Cli;

internal static class Program
{
    public static Task<int> Main(string[] args) => RunAsync(args);

    internal static Task<int> RunAsync(string[] args, HttpMessageHandler? transport = null,
        Func<bool, CancellationToken, Task<string>>? testToken = null) => PrototypeRuntime.RunAsync(async cancellation =>
    {
        if (args is [] or ["help"] or ["-h"])
        {
            Console.WriteLine("""
                git onedrive login | logout
                git onedrive config root [directory]
                git onedrive init <repository> [--initial-branch <branch>] [--object-format sha1|sha256]
                git onedrive list [--root <directory>]
                git onedrive url <repository>
                git onedrive gc <repository> [--apply] [--prune=<date>]
                git onedrive status <repository>
                git onedrive recover <repository> <operation-id> [--owner-stopped]
                """);
            return 0;
        }
        var auth = new Authentication();
        if (args is ["login"])
        {
            await auth.LoginAsync(cancellation);
            return 0;
        }
        if (args is ["logout"])
        {
            await auth.LogoutAsync();
            return 0;
        }

        using var runtime = new PrototypeRuntime(auth, transport, testToken, cancellation);
        var catalog = runtime.Catalog;
        switch (args)
        {
            case ["config", "root"]:
                Console.WriteLine(await catalog.GetRootSettingAsync());
                break;
            case ["config", "root", var root]:
                await catalog.SetRootSettingAsync(root);
                Console.WriteLine(root);
                break;
            case ["list"]:
            case ["list", "--root", _]:
                foreach (var item in await catalog.ListAsync(args.Length == 3 ? args[2] : null))
                    Console.WriteLine($"{item.Name[..^4]}\t{item.Url}");
                break;
            case ["url", var address]:
                Console.WriteLine((await catalog.OpenAsync(RepositoryAddress.Parse(address))).Address.Url);
                break;
            case ["init", var address, .. var options]:
                var branch = "main";
                var format = "sha1";
                for (var i = 0; i < options.Length; i += 2)
                {
                    if (i + 1 == options.Length)
                        throw new ArgumentException("Missing init option value.");
                    switch (options[i])
                    {
                        case "--initial-branch": branch = options[i + 1]; break;
                        case "--object-format": format = options[i + 1]; break;
                        default: throw new ArgumentException($"Unknown init option '{options[i]}'.");
                    }
                }
                Console.WriteLine((await catalog.InitAsync(RepositoryAddress.Parse(address), format, branch)).Url);
                break;
            case ["gc", var address, .. var options]:
                var apply = false;
                string? prune = null;
                foreach (var option in options)
                {
                    if (option == "--apply")
                        apply = true;
                    else if (option.StartsWith("--prune=", StringComparison.Ordinal) && option.Length > 8)
                        prune = option[8..];
                    else
                        throw new ArgumentException($"Unknown GC option '{option}'.");
                }
                await new RepositoryMaintenance(await catalog.OpenAsync(RepositoryAddress.Parse(address))).CollectAsync(apply, prune);
                break;
            case ["status", var address]:
                var state = await new RepositoryCoordinator(await catalog.OpenAsync(RepositoryAddress.Parse(address))).ReadAsync();
                Console.WriteLine(state.Maintenance is null ? "Repository is ready." : $"GC maintenance: {state.Maintenance.Owner}");
                foreach (var operation in state.Operations.Values)
                    Console.WriteLine($"{operation.Id}\t{operation.Kind}\t{operation.Machine}\t{operation.Process}\t{operation.Pending?.Target ?? "-"}");
                break;
            case ["recover", var address, var id]:
                await new RepositoryMaintenance(await catalog.OpenAsync(RepositoryAddress.Parse(address))).RecoverAsync(id, false);
                break;
            case ["recover", var address, var id, "--owner-stopped"]:
                await new RepositoryMaintenance(await catalog.OpenAsync(RepositoryAddress.Parse(address))).RecoverAsync(id, true);
                break;
            default:
                throw new ArgumentException("Invalid command. Run git onedrive help.");
        }
        return 0;
    });
}
