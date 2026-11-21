using System.Runtime.CompilerServices;
using Xunit;

namespace GitRemote.OneDrive.IntegrationTests.Graph;

internal sealed class OneDriveGraphFactAttribute : FactAttribute
{
    private const string TokenVariable = "ONEDRIVE_TEST_ACCESS_TOKEN";

    public OneDriveGraphFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = $"Set {TokenVariable} to enable this live Graph test.";
        SkipUnless = nameof(HasAccessToken);
        SkipType = typeof(OneDriveGraphFactAttribute);
        Timeout = 60_000;
    }

    public static string? AccessToken => Environment.GetEnvironmentVariable(TokenVariable)?.Trim();

    public static bool HasAccessToken => !string.IsNullOrWhiteSpace(AccessToken);
}