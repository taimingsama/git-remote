using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitRemote.OneDrive.Prototype;

internal sealed record DriveItem(
    string Id, string Name, string ETag, long Size, DateTimeOffset LastModifiedDateTime,
    JsonElement Folder, DriveFile? File)
{
    internal bool IsFolder => Folder.ValueKind == JsonValueKind.Object;
}

internal sealed record DriveFile(DriveHashes? Hashes);
internal sealed record DriveHashes(string? Sha1Hash);
internal sealed record FolderPage(DriveItem[] Value, [property: JsonPropertyName("@odata.nextLink")] string? Next);
internal sealed record UploadSession(string UploadUrl, string[]? NextExpectedRanges);
internal sealed record DriveIdentity(string Id);

internal sealed class GraphFailure(HttpStatusCode statusCode)
    : IOException($"OneDrive returned HTTP {(int)statusCode} ({statusCode}).")
{
    internal HttpStatusCode StatusCode { get; } = statusCode;
    internal bool IsConflict => StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed;
    internal bool IsMissing => StatusCode == HttpStatusCode.NotFound;
}

internal sealed class OneDriveStorage(HttpClient http, Func<bool, CancellationToken, Task<string>> token,
    CancellationToken cancellation)
{
    private const int SliceSize = 10 * 1024 * 1024;
    private static readonly Uri Graph = new("https://graph.microsoft.com/v1.0/");
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string? _driveId;
    internal string DriveId => _driveId ?? throw new InvalidOperationException("Resolve the drive first.");
    internal CancellationToken Cancellation => cancellation;

    internal OneDriveStorage WithCancellation(CancellationToken cancellationToken) =>
        new(http, token, cancellationToken) { _driveId = _driveId };

    internal async Task<DriveItem> RootAsync()
    {
        _driveId ??= (await ReadAsync<DriveIdentity>("me/drive?$select=id")).Id;
        return await ReadAsync<DriveItem>(DrivePath("root"));
    }

    internal async Task<DriveItem?> FindAsync(string parentId, string path)
    {
        try { return await ReadAsync<DriveItem>(ItemPath(parentId, path)); }
        catch (GraphFailure ex) when (ex.IsMissing) { return null; }
    }

    internal async Task<DriveItem?> FindByIdAsync(string id)
    {
        try { return await ReadAsync<DriveItem>(Item(id)); }
        catch (GraphFailure ex) when (ex.IsMissing) { return null; }
    }

    internal async Task<IReadOnlyList<DriveItem>> ListAsync(string id)
    {
        var result = new List<DriveItem>();
        var path = Item(id) + "/children?$top=200";
        while (path is not null)
        {
            var page = await ReadAsync<FolderPage>(path);
            result.AddRange(page.Value);
            path = page.Next;
        }
        return result;
    }

    internal async Task<DriveItem> CreateFolderAsync(string parentId, string name)
    {
        using var result = await SendAsync(HttpMethod.Post, Item(parentId) + "/children", () =>
            JsonContent.Create(new Dictionary<string, object>
            {
                ["name"] = name,
                ["folder"] = new { },
                ["@microsoft.graph.conflictBehavior"] = "fail"
            }));
        return await DeserializeAsync<DriveItem>(result);
    }

    internal async Task<DriveItem> EnsureFolderAsync(string parentId, string path)
    {
        DriveItem? folder = null;
        foreach (var part in path.Split('/'))
        {
            folder = await FindAsync(parentId, part);
            if (folder is null)
            {
                try { folder = await CreateFolderAsync(parentId, part); }
                catch (GraphFailure ex) when (ex.IsConflict)
                {
                    folder = await FindAsync(parentId, part) ?? throw new IOException("New folder is not yet visible.");
                }
            }
            if (!folder.IsFolder)
                throw new IOException($"'{part}' is already used by a OneDrive file.");
            parentId = folder.Id;
        }
        return folder!;
    }

    internal async Task<DriveItem> MoveAsync(DriveItem source, string targetParent, string name)
    {
        using var result = await SendAsync(HttpMethod.Patch, Item(source.Id), () =>
            JsonContent.Create(new { name, parentReference = new { id = targetParent } }), RequireETag(source.ETag));
        return await DeserializeAsync<DriveItem>(result);
    }

    internal async Task DeleteAsync(string itemId, string etag)
    {
        try { using var result = await SendAsync(HttpMethod.Delete, Item(itemId), etag: RequireETag(etag)); }
        catch (GraphFailure ex) when (ex.IsMissing) { }
    }

    internal async Task<(DriveItem Item, byte[] Bytes)> ReadVersionAsync(DriveItem item)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            _ = RequireETag(item.ETag);
            using var bytes = new MemoryStream();
            await DownloadAsync(item, bytes);
            var current = await FindByIdAsync(item.Id) ?? throw new IOException("OneDrive file disappeared while reading.");
            if (item.ETag == current.ETag)
                return (item, bytes.ToArray());
            item = current;
        }
        throw new IOException("OneDrive file changed repeatedly while reading.");
    }

    internal async Task<DriveItem> CreateAsync(string parentId, string name, byte[] bytes)
    {
        using var result = await SendAsync(HttpMethod.Put,
            ItemPath(parentId, name) + ":/content?@microsoft.graph.conflictBehavior=fail",
            () => new ByteArrayContent(bytes));
        return await DeserializeAsync<DriveItem>(result);
    }

    internal async Task<DriveItem> ReplaceAsync(DriveItem source, byte[] bytes)
    {
        using var result = await SendAsync(HttpMethod.Put, Item(source.Id) + "/content",
            () => new ByteArrayContent(bytes), RequireETag(source.ETag));
        return await DeserializeAsync<DriveItem>(result);
    }

    internal async Task<DriveItem> UploadAsync(string parentId, string name, string file)
    {
        var length = new FileInfo(file).Length;
        var target = ItemPath(parentId, name) + ":";
        if (length <= SliceSize)
        {
            using var result = await SendAsync(HttpMethod.Put,
                target + "/content?@microsoft.graph.conflictBehavior=fail",
                () => new StreamContent(File.OpenRead(file)));
            return await DeserializeAsync<DriveItem>(result);
        }

        using var created = await SendAsync(HttpMethod.Post, target + "/createUploadSession", () =>
            JsonContent.Create(new { item = new Dictionary<string, string>
            {
                ["name"] = name,
                ["@microsoft.graph.conflictBehavior"] = "fail"
            } }));
        var session = await DeserializeAsync<UploadSession>(created);
        var uri = TransferUri(session.UploadUrl);
        await using var source = File.OpenRead(file);
        var buffer = new byte[SliceSize];
        long offset = 0;
        var failures = 0;
        while (offset < length)
        {
            var count = (int)Math.Min(buffer.Length, length - offset);
            source.Position = offset;
            await source.ReadExactlyAsync(buffer.AsMemory(0, count), cancellation);
            using var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = new ByteArrayContent(buffer, 0, count) };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, length);
            HttpResponseMessage? response = null;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation); }
            catch (HttpRequestException) when (failures < 4) { }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && failures < 4) { }
            using (response)
            {
                if (response?.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                    return await DeserializeAsync<DriveItem>(response);
                if (response?.StatusCode == HttpStatusCode.Accepted)
                {
                    var next = NextOffset(await DeserializeAsync<UploadSession>(response), length);
                    if (next <= offset)
                        throw new IOException("The upload session did not advance.");
                    offset = next;
                    failures = 0;
                    continue;
                }
                if (response is not null && response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable
                    && !Retryable(response.StatusCode))
                    throw new GraphFailure(response.StatusCode);
                if (++failures > 4)
                    throw new IOException("OneDrive did not confirm completion of the upload.");
                await Task.Delay(RetryDelay(response, failures), cancellation);
            }
            var completed = await FindAsync(parentId, name);
            if (completed is not null)
                return completed;
            using var query = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!query.IsSuccessStatusCode)
                throw new GraphFailure(query.StatusCode);
            offset = NextOffset(await DeserializeAsync<UploadSession>(query), length);
        }
        throw new IOException("OneDrive did not return the completed upload.");
    }

    internal async Task DownloadAsync(DriveItem item, Stream output, long offset = 0)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(item, output, offset);
                return;
            }
            catch (Exception ex) when (output.CanSeek && offset == 0 && attempt < 3
                && ex is HttpRequestException or HttpIOException)
            {
                output.SetLength(0);
                output.Position = 0;
                await Task.Delay(RetryDelay(null, attempt), cancellation);
            }
        }
    }

    private async Task DownloadOnceAsync(DriveItem item, Stream output, long offset)
    {
        using var initial = await SendAsync(HttpMethod.Get, Item(item.Id) + "/content", allowRedirect: true,
            range: offset == 0 ? null : offset);
        if (initial.StatusCode == HttpStatusCode.Found)
        {
            var url = initial.Headers.Location ?? throw new IOException("Missing OneDrive download URL.");
            using var request = new HttpRequestMessage(HttpMethod.Get, TransferUri(url.AbsoluteUri));
            if (offset != 0)
                request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            await CopyContentAsync(response, output, offset);
        }
        else
        {
            await CopyContentAsync(initial, output, offset);
        }
    }

    internal async Task VerifyFileAsync(DriveItem item, string file)
    {
        await using var local = File.OpenRead(file);
        if (local.Length != item.Size)
            throw new IOException($"OneDrive returned an unexpected size for '{item.Name}'.");

        if (item.File?.Hashes?.Sha1Hash is { Length: 40 } cloudHash)
        {
            var hash = await SHA1.HashDataAsync(local, cancellation);
            if (!cloudHash.Equals(Convert.ToHexString(hash), StringComparison.OrdinalIgnoreCase))
                throw new IOException($"OneDrive returned an unexpected checksum for '{item.Name}'.");
        }
        else
        {
            using var hash = SHA256.Create();
            await using var sink = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
            await DownloadAsync(item, sink);
            await sink.FlushFinalBlockAsync(cancellation);
            var expected = await SHA256.HashDataAsync(local, cancellation);
            if (!CryptographicOperations.FixedTimeEquals(hash.Hash, expected))
                throw new IOException($"OneDrive returned different bytes for '{item.Name}'.");
        }
        var current = await FindByIdAsync(item.Id);
        if (current?.ETag != RequireETag(item.ETag))
            throw new IOException($"OneDrive changed '{item.Name}' during verification.");
    }

    private async Task CopyContentAsync(HttpResponseMessage response, Stream output, long offset)
    {
        if (!response.IsSuccessStatusCode)
            throw new GraphFailure(response.StatusCode);
        if (offset != 0)
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                output.Position = 0;
                output.SetLength(0);
            }
            else if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != offset)
            {
                throw new IOException("OneDrive returned an unexpected download range.");
            }
        }
        await using var content = await response.Content.ReadAsStreamAsync(cancellation);
        await content.CopyToAsync(output, cancellation);
    }

    private static long NextOffset(UploadSession session, long length)
    {
        if (session.NextExpectedRanges?.FirstOrDefault()?.Split('-')[0] is { } start
            && long.TryParse(start, out var offset) && offset >= 0 && offset < length)
            return offset;
        throw new IOException("Invalid OneDrive upload range.");
    }

    private async Task<T> ReadAsync<T>(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path);
        return await DeserializeAsync<T>(response);
    }

    private async Task<T> DeserializeAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(Json, cancellation)
        ?? throw new IOException("OneDrive returned an empty response.");

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Func<HttpContent>? content = null,
        string? etag = null, bool allowRedirect = false, long? range = null)
    {
        var uri = new Uri(Graph, path);
        if (uri.Scheme != Graph.Scheme || uri.Authority != Graph.Authority
            || !uri.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal))
            throw new IOException("OneDrive returned an unexpected Graph URL.");
        var refreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(refreshed, cancellation));
            refreshed = false;
            request.Content = content?.Invoke();
            if (request.Content is not null && request.Content.Headers.ContentType is null)
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            if (etag is not null)
                request.Headers.TryAddWithoutValidation("If-Match", etag);
            if (range is not null)
                request.Headers.Range = new RangeHeaderValue(range, null);

            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation); }
            catch (HttpRequestException ex) when (attempt < 3 &&
                (method == HttpMethod.Get || ex.HttpRequestError == HttpRequestError.SecureConnectionError))
            {
                await Task.Delay(RetryDelay(null, attempt), cancellation);
                continue;
            }
            if (response.IsSuccessStatusCode || allowRedirect && response.StatusCode == HttpStatusCode.Found)
                return response;
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    refreshed = true;
                    continue;
                }
                if ((method == HttpMethod.Get && Retryable(response.StatusCode) ||
                     response.StatusCode == HttpStatusCode.TooManyRequests) && attempt < 3)
                {
                    await Task.Delay(RetryDelay(response, attempt), cancellation);
                    continue;
                }
                throw new GraphFailure(response.StatusCode);
            }
        }
    }

    private static bool Retryable(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status is >= 500 and <= 599;

    private static TimeSpan RetryDelay(HttpResponseMessage? response, int attempt)
    {
        var retryAfter = response?.Headers.RetryAfter;
        var delay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow)
            ?? TimeSpan.FromMilliseconds(250 * (1 << attempt));
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private static Uri TransferUri(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0)
            return uri;
        throw new IOException("OneDrive returned an invalid transfer URL.");
    }

    private static string RequireETag(string? etag) =>
        !string.IsNullOrWhiteSpace(etag) && etag != "*" && !etag.Any(char.IsControl)
            ? etag : throw new IOException("OneDrive did not return a usable item ETag.");

    private string DrivePath(string relative) => "drives/" + Uri.EscapeDataString(DriveId) + "/" + relative;
    private string Item(string id) => DrivePath("items/" + Uri.EscapeDataString(id));
    private string ItemPath(string id, string path) =>
        Item(id) + ":/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
