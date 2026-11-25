using System.Text.Json;
using Microsoft.Identity.Client;

namespace GitRemote.OneDrive.Prototype;

internal sealed class PrototypeRuntime : IDisposable
{
    private readonly HttpClient _http;

    internal PrototypeRuntime(Authentication auth, HttpMessageHandler? transport,
        Func<bool, CancellationToken, Task<string>>? accessToken, CancellationToken cancellation)
    {
        _http = new HttpClient(transport ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        Catalog = new RepositoryCatalog(new OneDriveStorage(_http, accessToken ?? auth.GetTokenAsync, cancellation));
    }

    internal RepositoryCatalog Catalog { get; }

    public void Dispose() => _http.Dispose();

    internal static async Task<int> RunAsync(Func<CancellationToken, Task<int>> action)
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, args) =>
        {
            args.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await action(stop.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("OneDrive operation interrupted or timed out.");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or JsonException or InvalidOperationException
                                      or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"git-onedrive: {PushService.OneLine(ex.Message)}");
            return 1;
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("git-onedrive: the OneDrive request did not complete.");
            return 1;
        }
        catch (MsalException ex)
        {
            Console.Error.WriteLine($"git-onedrive: Microsoft sign-in failed ({ex.ErrorCode}).");
            return 1;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
