using CliProgram = GitRemote.OneDrive.Prototype.Cli.Program;
using HelperProgram = GitRemote.OneDrive.Prototype.Program;

if (args is ["--proc-receive"])
    return await HelperProgram.Main(args);

var management = Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "git-onedrive";
var endpoint = Environment.GetEnvironmentVariable("GITREMOTE_TEST_GRAPH_ENDPOINT")
    ?? throw new InvalidOperationException("No Graph system test service configured.");
var transport = new LoopbackGraph(endpoint);
return management
    ? await CliProgram.RunAsync(args, transport, GetFixtureTokenAsync)
    : await HelperProgram.RunAsync(args, transport, GetFixtureTokenAsync);

static Task<string> GetFixtureTokenAsync(bool refresh, CancellationToken cancellationToken) =>
    Task.FromResult("fixture");

internal sealed class LoopbackGraph(string endpoint) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false })
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.RequestUri = new Uri(new Uri(endpoint), request.RequestUri!.PathAndQuery);
        return base.SendAsync(request, cancellationToken);
    }
}
