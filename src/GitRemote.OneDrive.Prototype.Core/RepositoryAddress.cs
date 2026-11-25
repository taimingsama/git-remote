namespace GitRemote.OneDrive.Prototype;

internal enum TransportMode { Direct, Smart, Stateless }

internal sealed record RepositoryAddress(string? Root, string Name, TransportMode Mode)
{
    internal const string Scheme = "onedrive-prototype::";
    internal const string DefaultRoot = "GitRepositories";
    internal string Url => Scheme + string.Join('/', (Root is null ? Name : Root + "/" + Name).Split('/')
        .Select(Uri.EscapeDataString)) + (Mode switch
        {
            TransportMode.Smart => "?transport=smart",
            TransportMode.Stateless => "?transport=stateless",
            _ => ""
        });

    internal static RepositoryAddress Parse(string input)
    {
        if (input.StartsWith(Scheme, StringComparison.Ordinal))
            input = input[Scheme.Length..];
        var parts = input.Split('?', 2);
        var mode = parts.ElementAtOrDefault(1) switch
        {
            null or "transport=direct" => TransportMode.Direct,
            "transport=smart" => TransportMode.Smart,
            "transport=stateless" => TransportMode.Stateless,
            _ => throw new ArgumentException("Expected transport=direct, smart or stateless.")
        };

        var path = parts[0].Split('/').Select(Uri.UnescapeDataString).ToArray();
        foreach (var segment in path)
            ValidateSegment(segment);

        var name = path[^1];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        ValidateSegment(name);
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A repository name accepts one optional .git suffix.");
        return new(path.Length > 1 ? string.Join('/', path[..^1]) : null, name + ".git", mode);
    }

    internal static string ValidateRoot(string root)
    {
        foreach (var segment in root.Split('/'))
            ValidateSegment(segment);
        return root;
    }

    private static void ValidateSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Length > 240 || segment.Trim() != segment
            || segment.EndsWith('.') || segment.Any(char.IsControl)
            || segment.IndexOfAny(['/', '\\', '"', '*', ':', '<', '>', '?', '|', '#', '%']) >= 0)
            throw new ArgumentException("Use a relative OneDrive path with valid, nonempty folder names.");

        var stem = segment.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || segment.Equals("_vti_", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsAsciiDigit(stem[3])))
            throw new ArgumentException($"'{segment}' is a reserved folder name.");
    }
}
