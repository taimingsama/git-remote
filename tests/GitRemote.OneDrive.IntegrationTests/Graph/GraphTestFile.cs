using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Graph;
using Microsoft.Graph.Drives.Item.Items.Item;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;

namespace GitRemote.OneDrive.IntegrationTests.Graph;

internal sealed class GraphTestFile : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly GraphServiceClient _graph;
    private readonly string _token;
    private readonly string _fileName = $"gitremote-graph-{Guid.NewGuid():N}.txt";
    private string? _driveId;
    private string? _fileId;
    private string DriveId => _driveId ?? throw new InvalidOperationException("Resolve the test drive first.");
    private string FileId => _fileId ?? throw new InvalidOperationException("Create the test file first.");
    private DriveItemItemRequestBuilder CreatedFile => _graph.Drives[DriveId].Items[FileId];

    public GraphTestFile(string token)
    {
        _token = token;
        _http = CreateTransport();
        _graph = new GraphServiceClient(_http);
    }

    public async Task ResolveDriveAsync(CancellationToken cancellationToken)
    {
        var drive = await _graph.Me.Drive.GetAsync(
            configuration => configuration.QueryParameters.Select = ["id"], cancellationToken);
        _driveId = drive?.Id ?? throw new InvalidOperationException("Graph returned no OneDrive ID.");
    }

    public async Task<string> CreateAndReadETagAsync(string initialContent, CancellationToken cancellationToken)
    {
        await UploadAsync(Encoding.UTF8.GetBytes(initialContent), cancellationToken);
        return await ReadETagAsync(cancellationToken);
    }

    public async Task UploadAsync(byte[] content, CancellationToken cancellationToken)
    {
        await ResolveDriveAsync(cancellationToken);
        using var body = new MemoryStream(content);
        var uploadedFile = await FindFileByPath(_graph).Content.PutAsync(body, cancellationToken: cancellationToken);
        _fileId = uploadedFile?.Id
            ?? throw new InvalidOperationException("Graph returned no test file ID.");
    }

    public async Task<string> ReadETagAsync(CancellationToken cancellationToken)
    {
        var metadata = await CreatedFile.GetAsync(
            configuration => configuration.QueryParameters.Select = ["eTag"], cancellationToken);
        return metadata?.ETag
            ?? throw new InvalidOperationException("Graph returned no ETag for the test file.");
    }

    public async Task<(HttpStatusCode StatusCode, string Content)> CreateIfAbsentAsync(
        string content, CancellationToken cancellationToken)
    {
        using var transport = CreateTransport();
        using var client = new GraphServiceClient(transport);
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var responseHandler = new NativeResponseHandler();
        var upload = FindFileByPath(client).Content.ToPutRequestInformation(body, configuration =>
            configuration.Options.Add(new ResponseHandlerOption { ResponseHandler = responseHandler }));
        upload.PathParameters.TryAdd("baseurl", client.RequestAdapter.BaseUrl
            ?? throw new InvalidOperationException("Graph returned no base URL for the upload."));

        // The generated PUT builder does not expose this Graph instance annotation.
        upload.URI = new UriBuilder(upload.URI) { Query = "@microsoft.graph.conflictBehavior=fail" }.Uri;
        await client.RequestAdapter.SendAsync(upload, DriveItem.CreateFromDiscriminatorValue,
            cancellationToken: cancellationToken);
        return (GetWriteStatus(responseHandler), content);
    }

    public async Task<(HttpStatusCode StatusCode, string Content)> ReplaceContentIfMatchAsync(
        string replacementContent, string expectedETag, CancellationToken cancellationToken)
    {
        using var transport = CreateTransport();
        using var client = new GraphServiceClient(transport);
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(replacementContent));
        var responseHandler = new NativeResponseHandler();
        await client.Drives[DriveId].Items[FileId].Content.PutAsync(body, configuration =>
        {
            configuration.Headers.Add("If-Match", expectedETag);
            configuration.Options.Add(new ResponseHandlerOption { ResponseHandler = responseHandler });
        }, cancellationToken);

        return (GetWriteStatus(responseHandler), replacementContent);
    }

    public async Task<string> ReadContentAsync(CancellationToken cancellationToken)
    {
        using var content = await CreatedFile.Content.GetAsync(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Graph returned no content for the test file.");
        using var reader = new StreamReader(content);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public async Task<byte[]> DownloadByPathFromAnotherClientAsync(CancellationToken cancellationToken)
    {
        using var transport = CreateTransport();
        using var client = new GraphServiceClient(transport);
        using var content = await FindFileByPath(client).Content.GetAsync(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Graph returned no content for the test file path.");
        using var downloaded = new MemoryStream();
        await content.CopyToAsync(downloaded, cancellationToken);
        return downloaded.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            if (_driveId is not null)
            {
                var fileToDelete = _fileId is not null ? CreatedFile : FindFileByPath(_graph);
                await fileToDelete.DeleteAsync(cancellationToken: cleanup.Token);
            }
        }
        catch (ApiException exception) when (exception.ResponseStatusCode == (int)HttpStatusCode.NotFound)
        {
            // A failed create can leave nothing to clean up.
        }
        finally
        {
            _graph.Dispose();
            _http.Dispose();
        }
    }

    // Preserve the SDK's path-aware Content request builder when uploading or downloading by path.
    private CustomDriveItemItemRequestBuilder FindFileByPath(GraphServiceClient client) =>
        client.Drives[DriveId].Root.ItemWithPath(_fileName);

    private HttpClient CreateTransport()
    {
        // Use a plain transport so each conditional write is sent only once.
        var transport = new HttpClient();
        transport.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return transport;
    }

    private static HttpStatusCode GetWriteStatus(NativeResponseHandler responseHandler)
    {
        using var response = responseHandler.Value as HttpResponseMessage
            ?? throw new InvalidOperationException("Graph returned no HTTP response for the conditional write.");
        return response.StatusCode;
    }
}
