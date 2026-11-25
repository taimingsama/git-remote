using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace GitRemote.OneDrive.Prototype;

internal sealed class Authentication
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/Files.ReadWrite"];
    private IPublicClientApplication? _application;
    private string? _token;
    private DateTimeOffset _expires;

    internal static string LocalDirectory =>
        Environment.GetEnvironmentVariable("GITREMOTE_PROTOTYPE_CACHE_DIRECTORY")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitRemote.OneDrive");

    internal async Task LoginAsync(CancellationToken cancellationToken)
    {
        var application = await GetApplicationAsync();
        var oldAccounts = (await application.GetAccountsAsync()).ToArray();
        var result = await application.AcquireTokenWithDeviceCode(Scopes, code =>
        {
            Console.Error.WriteLine(code.Message);
            return Task.CompletedTask;
        }).ExecuteAsync(cancellationToken);
        foreach (var account in oldAccounts)
            if (account.HomeAccountId.Identifier != result.Account.HomeAccountId.Identifier)
                await application.RemoveAsync(account);
        Console.WriteLine($"Signed in as {result.Account.Username}.");
    }

    internal async Task LogoutAsync()
    {
        var application = await GetApplicationAsync();
        foreach (var account in await application.GetAccountsAsync())
            await application.RemoveAsync(account);
        _token = null;
    }

    internal async Task<string> GetTokenAsync(bool refresh, CancellationToken cancellationToken)
    {
        var overrideToken = Environment.GetEnvironmentVariable("GITREMOTE_PROTOTYPE_ACCESS_TOKEN");
        if (!string.IsNullOrWhiteSpace(overrideToken))
            return overrideToken;
        if (!refresh && _token is not null && _expires > DateTimeOffset.UtcNow.AddMinutes(2))
            return _token;

        var application = await GetApplicationAsync();
        var accounts = (await application.GetAccountsAsync()).ToArray();
        if (accounts.Length != 1)
            throw new IOException("Run git onedrive login to select a personal Microsoft account.");

        try
        {
            var result = await application.AcquireTokenSilent(Scopes, accounts[0])
                .WithForceRefresh(refresh).ExecuteAsync(cancellationToken);
            _expires = result.ExpiresOn;
            return _token = result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            throw new IOException("The OneDrive session expired. Run git onedrive login.");
        }
    }

    private async Task<IPublicClientApplication> GetApplicationAsync()
    {
        if (_application is not null)
            return _application;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Interactive OneDrive authentication requires Windows.");
        if (!Guid.TryParse(Environment.GetEnvironmentVariable("GITREMOTE_PROTOTYPE_CLIENT_ID"), out var id))
            throw new IOException("Set GITREMOTE_PROTOTYPE_CLIENT_ID to the application registration's client ID.");

        var application = PublicClientApplicationBuilder.Create(id.ToString())
            .WithAuthority("https://login.microsoftonline.com/consumers")
            .WithRedirectUri("https://login.microsoftonline.com/common/oauth2/nativeclient")
            .Build();
        var properties = new StorageCreationPropertiesBuilder($"msal-{id:N}.cache", LocalDirectory).Build();
        var cache = await MsalCacheHelper.CreateAsync(properties);
        cache.VerifyPersistence();
        cache.RegisterCache(application.UserTokenCache);
        return _application = application;
    }
}
