using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GitRemote.OneDrive.Prototype.IntegrationTests.SystemTests;

internal sealed class GraphFixture : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly object _gate = new();
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
    private readonly List<GraphCall> _calls = [];
    private GraphFault? _fault;

    internal string Endpoint { get; private set; } = "";
    internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    internal int OccupiedMoveStatus { get; set; } = 409;
    internal GraphCall[] Calls { get { lock (_gate) return _calls.ToArray(); } }

    private GraphFixture()
    {
        _items.Add("root", new Item("root", "", "", null, Now));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        _application = builder.Build();
        _application.Run(RespondAsync);
    }

    internal static async Task<GraphFixture> StartAsync(CancellationToken cancellation)
    {
        var graph = new GraphFixture();
        await graph._application.StartAsync(cancellation);
        graph.Endpoint = graph._application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return graph;
    }

    internal void FailNext(string verb, string name, bool afterApply)
    {
        lock (_gate) _fault = new(verb, name, afterApply, true, null);
    }

    internal RequestPause PauseNext(string verb, string name, bool afterApply, bool dropResponse = true)
    {
        var pause = new RequestPause();
        lock (_gate) _fault = new(verb, name, afterApply, dropResponse, pause);
        return pause;
    }

    internal void CopyNativeRepo(string path, string destination)
    {
        lock (_gate)
        {
            var repo = Find(_items["root"], path) ?? throw new InvalidOperationException("Fixture repository does not exist.");
            foreach (var item in Children(repo.Id).Where(item => item.Name != "gitremote"))
                Copy(item, destination);
        }
    }

    internal void CorruptFile(string path)
    {
        lock (_gate)
        {
            var file = Find(_items["root"], path) ?? throw new InvalidOperationException("Fixture file does not exist.");
            file.Bytes![file.Bytes.Length / 2] ^= 0x80;
            file.Touch(Now);
        }
    }

    internal string VersionOf(string path)
    {
        lock (_gate) return Find(_items["root"], path)?.ETag ?? throw new InvalidOperationException("Fixture file does not exist.");
    }

    private void Copy(Item item, string parent)
    {
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, item.Name);
        if (item.Bytes is null)
        {
            Directory.CreateDirectory(path);
            foreach (var child in Children(item.Id))
                Copy(child, path);
            return;
        }
        File.WriteAllBytes(path, item.Bytes);
        File.SetLastWriteTimeUtc(path, item.Modified.UtcDateTime);
    }

    private async Task RespondAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var path = context.Request.Path.Value!;
        using var payload = new MemoryStream();
        await context.Request.Body.CopyToAsync(payload, context.RequestAborted);
        var requestBody = payload.ToArray();
        Response response;
        GraphFault? fault;
        lock (_gate)
        {
            var name = TargetName(path);
            fault = _fault?.Verb == method && (name == _fault.Name || name.EndsWith(_fault.Name, StringComparison.Ordinal)
                || name.StartsWith(_fault.Name, StringComparison.Ordinal))
                ? _fault : null;
            if (fault is not null)
                _fault = null;
            _calls.Add(new(method, name, requestBody.Length));
            response = fault is { AfterApply: false } ? new(503) : Route(context, path, method, requestBody);
        }
        if (fault?.Pause is { } pause)
        {
            pause.Reached.TrySetResult();
            await pause.Release.Task.WaitAsync(TimeSpan.FromSeconds(25));
            if (!fault.AfterApply && !fault.DropResponse)
                lock (_gate) response = Route(context, path, method, requestBody);
        }
        if (fault?.DropResponse == true)
            response = new(503);

        context.Response.StatusCode = response.Status;
        if (response.Bytes is { } bytes)
        {
            var range = context.Request.Headers.Range.ToString();
            if (range.StartsWith("bytes=", StringComparison.Ordinal) && long.TryParse(range[6..].TrimEnd('-'), out var offset)
                && offset > 0 && offset < bytes.Length)
            {
                context.Response.StatusCode = 206;
                context.Response.Headers.ContentRange = $"bytes {offset}-{bytes.Length - 1}/{bytes.Length}";
                await context.Response.Body.WriteAsync(bytes.AsMemory((int)offset));
            }
            else
            {
                await context.Response.Body.WriteAsync(bytes);
            }
        }
        else if (response.Value is not null)
        {
            await context.Response.WriteAsJsonAsync(response.Value);
        }
    }

    private Response Route(HttpContext context, string path, string method, byte[] body)
    {
        if (path == "/v1.0/me/drive")
            return new(200, new { id = "fixture" });
        if (path == "/v1.0/drives/fixture/root")
            return new(200, Describe(_items["root"]));
        if (path.StartsWith("/transfer/", StringComparison.Ordinal))
            return Transfer(context, path["/transfer/".Length..], method, body);

        const string prefix = "/v1.0/drives/fixture/items/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return new(404);
        var itemPath = path[prefix.Length..];
        var byPath = itemPath.IndexOf(":/", StringComparison.Ordinal);
        if (byPath >= 0)
        {
            var parentId = itemPath[..byPath];
            if (!_items.TryGetValue(parentId, out var parent))
                return new(404);
            var name = itemPath[(byPath + 2)..];
            const string content = ":/content";
            const string upload = ":/createUploadSession";
            if (name.EndsWith(content, StringComparison.Ordinal) && method == "PUT")
                return Create(parent.Id, name[..^content.Length], body);
            if (name.EndsWith(upload, StringComparison.Ordinal) && method == "POST")
            {
                var id = Guid.NewGuid().ToString("N");
                _uploads.Add(id, new(parent.Id, name[..^upload.Length], new MemoryStream()));
                return new(200, new { uploadUrl = "https://files.test/transfer/" + id });
            }
            var found = Find(parent, name);
            return found is null ? new(404) : new(200, Describe(found));
        }

        var components = itemPath.Split('/');
        if (!_items.TryGetValue(components[0], out var item))
            return new(404);
        var ifMatch = context.Request.Headers.IfMatch.ToString();
        if (ifMatch.Length > 0 && item.ETag != ifMatch)
            return new(412);
        if (components is [_, "content"])
        {
            if (method == "GET" && item.Bytes is not null)
                return new(200, Bytes: item.Bytes);
            if (method == "PUT" && item.Bytes is not null)
            {
                item.Bytes = body;
                item.Touch(Now);
                return new(200, Describe(item));
            }
            return new(404);
        }
        if (components is [_, "children"])
        {
            if (method == "POST")
            {
                using var json = System.Text.Json.JsonDocument.Parse(body);
                return Create(item.Id, json.RootElement.GetProperty("name").GetString()!, null);
            }
            const int pageSize = 2;
            var page = int.TryParse(context.Request.Query["skip"], out var skip) ? skip : 0;
            var all = Children(item.Id).OrderBy(child => child.Name, StringComparer.Ordinal).ToArray();
            var values = all.Skip(page).Take(pageSize).Select(Describe).ToArray();
            var payload = new Dictionary<string, object> { ["value"] = values };
            if (page + pageSize < all.Length)
                payload["@odata.nextLink"] = $"https://graph.microsoft.com{path}?skip={page + pageSize}";
            return new(200, payload);
        }
        if (method == "PATCH")
        {
            using var json = System.Text.Json.JsonDocument.Parse(body);
            var name = json.RootElement.GetProperty("name").GetString()!;
            var parent = json.RootElement.GetProperty("parentReference").GetProperty("id").GetString()!;
            if (!_items.ContainsKey(parent))
                return new(404);
            if (Children(parent).Any(other => other.Id != item.Id && Equal(other.Name, name)))
                return new(OccupiedMoveStatus);
            item.Parent = parent;
            item.Name = name;
            item.Touch(Now, updateTime: false);
            return new(200, Describe(item));
        }
        if (method == "DELETE")
        {
            Delete(item);
            return new(204);
        }
        return new(200, Describe(item));
    }

    private Response Transfer(HttpContext context, string id, string method, byte[] bytes)
    {
        if (!_uploads.TryGetValue(id, out var upload))
            return new(404);
        if (context.Request.Headers.Authorization.Count != 0)
            return new(401);
        if (method == "GET")
            return new(200, new { nextExpectedRanges = new[] { $"{upload.Bytes.Length}-" } });
        var range = context.Request.Headers.ContentRange.ToString().Split([' ', '-', '/']);
        if (range.Length != 4 || !long.TryParse(range[1], out var offset) || !long.TryParse(range[3], out var length))
            return new(400);
        if (offset != upload.Bytes.Length)
            return new(416);
        upload.Bytes.Write(bytes);
        if (upload.Bytes.Length < length)
            return new(202, new { nextExpectedRanges = new[] { $"{upload.Bytes.Length}-" } });
        _uploads.Remove(id);
        using (upload.Bytes)
            return Create(upload.Parent, upload.Name, upload.Bytes.ToArray());
    }

    private Response Create(string parent, string name, byte[]? bytes)
    {
        if (Children(parent).Any(item => Equal(item.Name, name)))
            return new(409);
        var id = Guid.NewGuid().ToString("N");
        var item = new Item(id, parent, name, bytes, Now);
        _items.Add(id, item);
        return new(201, Describe(item));
    }

    private string TargetName(string path)
    {
        if (path.StartsWith("/transfer/", StringComparison.Ordinal) && _uploads.TryGetValue(path["/transfer/".Length..], out var upload))
            return upload.Name;
        var segment = path.Split('/').Skip(5).FirstOrDefault();
        if (segment is not null && _items.TryGetValue(segment, out var item))
            return item.Name;
        if (path.Contains(":/", StringComparison.Ordinal))
            return path.Split(":/", StringSplitOptions.None)[1];
        return path;
    }

    private void Delete(Item item)
    {
        foreach (var child in Children(item.Id).ToArray())
            Delete(child);
        _items.Remove(item.Id);
    }

    private IEnumerable<Item> Children(string parent) => _items.Values.Where(item => item.Parent == parent);
    private Item? Find(Item item, string path)
    {
        foreach (var part in path.Split('/'))
        {
            var child = Children(item.Id).FirstOrDefault(child => Equal(child.Name, part));
            if (child is null)
                return null;
            item = child;
        }
        return item;
    }

    private static bool Equal(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static object Describe(Item item) => new
    {
        id = item.Id,
        name = item.Name,
        eTag = item.ETag,
        size = item.Bytes?.LongLength ?? 0,
        lastModifiedDateTime = item.Modified,
        folder = item.Bytes is null ? new { } : null,
        file = item.Bytes is not null ? new { hashes = new { sha1Hash = Convert.ToHexString(SHA1.HashData(item.Bytes)) } } : null
    };

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync();
        await _application.DisposeAsync();
        foreach (var upload in _uploads.Values)
            upload.Bytes.Dispose();
    }

    private sealed class Item(string id, string parent, string name, byte[]? bytes, DateTimeOffset modified)
    {
        private int _version = 1;
        internal string Id { get; } = id;
        internal string Parent { get; set; } = parent;
        internal string Name { get; set; } = name;
        internal byte[]? Bytes { get; set; } = bytes;
        internal DateTimeOffset Modified { get; private set; } = modified;
        internal string ETag => $"\"{Id}:{_version}\"";

        internal void Touch(DateTimeOffset now, bool updateTime = true)
        {
            _version++;
            if (updateTime)
                Modified = now;
        }
    }

    private sealed record Upload(string Parent, string Name, MemoryStream Bytes);
    private sealed record Response(int Status, object? Value = null, byte[]? Bytes = null);
    private sealed record GraphFault(string Verb, string Name, bool AfterApply, bool DropResponse, RequestPause? Pause);
}

internal sealed record GraphCall(string Method, string Name, int RequestBytes);

internal sealed class RequestPause : IDisposable
{
    internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Dispose() => Release.TrySetResult();
}
