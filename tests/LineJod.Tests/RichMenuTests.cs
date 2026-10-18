using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LineJod.Config;
using LineJod.Line;
using LineJod.Services;

namespace LineJod.Tests;

/// <summary>Plays the LINE rich menu API: records every call and answers with scripted statuses.</summary>
public class FakeRichMenuHandler : HttpMessageHandler
{
    public List<string> Calls { get; } = new();
    public HttpStatusCode UploadStatus { get; set; } = HttpStatusCode.OK;
    public string[] ExistingNames { get; set; } = Array.Empty<string>();
    public string? CreatedBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        Calls.Add($"{request.Method} {path}");
        string body = "{}";
        var status = HttpStatusCode.OK;

        if (request.Method == HttpMethod.Get && path.EndsWith("/richmenu/list"))
            body = JsonSerializer.Serialize(new { richmenus = ExistingNames.Select((n, i) => new { richMenuId = "old" + i, name = n }) });
        else if (request.Method == HttpMethod.Post && path.EndsWith("/v2/bot/richmenu"))
        {
            CreatedBody = await request.Content!.ReadAsStringAsync(ct);
            body = "{\"richMenuId\":\"new1\"}";
        }
        else if (path.EndsWith("/content")) status = UploadStatus;
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}

public class RichMenuTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "linejod-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRichMenuHandler _http = new();

    public RichMenuTests() => Directory.CreateDirectory(Path.Combine(_root, "richmenu"));
    public void Dispose() => Directory.Delete(_root, true);

    private RichMenuService Service(string liffId = "1234-abcd", bool withImage = true)
    {
        if (withImage) File.WriteAllBytes(Path.Combine(_root, "richmenu", "richmenu.png"), new byte[] { 1, 2, 3 });
        var options = Microsoft.Extensions.Options.Options.Create(new AppSettings { Liff = { Id = liffId } });
        var line = new LineClient(new HttpClient(_http), options, NullLogger<LineClient>.Instance);
        return new RichMenuService(line, options, new Env(_root));
    }

    private sealed class Env(string root) : IWebHostEnvironment
    {
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public string ApplicationName { get; set; } = "test";
        public string EnvironmentName { get; set; } = "Test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void Six_tiles_cover_the_whole_image_without_gaps_or_overlap()
    {
        var json = JsonSerializer.Serialize(RichMenuDefinition.Build("1234-abcd"));
        using var doc = JsonDocument.Parse(json);
        var areas = doc.RootElement.GetProperty("areas").EnumerateArray().ToList();
        Assert.Equal(6, areas.Count);

        var covered = new int[RichMenuDefinition.Width / 50, RichMenuDefinition.Height / 6];   // coarse grid: every cell hit exactly once
        foreach (var a in areas)
        {
            var b = a.GetProperty("bounds");
            int x = b.GetProperty("x").GetInt32(), y = b.GetProperty("y").GetInt32(), w = b.GetProperty("width").GetInt32(), h = b.GetProperty("height").GetInt32();
            Assert.InRange(x + w, 1, RichMenuDefinition.Width);
            Assert.InRange(y + h, 1, RichMenuDefinition.Height);
            for (var cx = x / 50; cx < (x + w) / 50; cx++)
                for (var cy = y / 6; cy < (y + h) / 6; cy++) covered[cx, cy]++;
        }
        foreach (var c in covered) Assert.Equal(1, c);
    }

    [Fact]
    public void Tiles_open_the_right_screens_in_the_order_of_the_image()
    {
        var json = JsonSerializer.Serialize(RichMenuDefinition.Build("1234-abcd"));
        using var doc = JsonDocument.Parse(json);
        var actions = doc.RootElement.GetProperty("areas").EnumerateArray().Select(a => a.GetProperty("action")).ToList();

        string Uri(int i) => actions[i].GetProperty("uri").GetString()!;
        Assert.Equal("https://liff.line.me/1234-abcd?tab=new", Uri(0));
        Assert.Equal("https://liff.line.me/1234-abcd?tab=new&kind=income", Uri(1));
        Assert.Equal(("message", "สรุป"), (actions[2].GetProperty("type").GetString(), actions[2].GetProperty("text").GetString()));
        Assert.Equal("https://liff.line.me/1234-abcd?tab=history", Uri(3));
        Assert.Equal("https://liff.line.me/1234-abcd?tab=subs", Uri(4));
        Assert.Equal("https://liff.line.me/1234-abcd?tab=budget", Uri(5));
        Assert.All(actions, a => Assert.True(a.GetProperty("label").GetString()!.Length <= 20));   // LINE's limit
    }

    [Fact]
    public async Task Install_creates_uploads_sets_default_and_only_then_removes_the_old_copy()
    {
        _http.ExistingNames = new[] { RichMenuDefinition.Name, "someone else's menu" };
        var r = await Service().InstallAsync();

        Assert.True(r.Ok);
        Assert.Equal("new1", r.RichMenuId);
        Assert.Equal(new[]
        {
            "GET /v2/bot/richmenu/list",
            "POST /v2/bot/richmenu",
            "POST /v2/bot/richmenu/new1/content",
            "POST /v2/bot/user/all/richmenu/new1",
            "DELETE /v2/bot/richmenu/old0",       // only the menu with our name; the other one is untouched
        }, _http.Calls);
    }

    [Fact]
    public async Task A_refused_image_removes_the_half_made_menu_and_keeps_the_old_one()
    {
        _http.ExistingNames = new[] { RichMenuDefinition.Name };
        _http.UploadStatus = HttpStatusCode.BadRequest;
        var r = await Service().InstallAsync();

        Assert.False(r.Ok);
        Assert.Contains("DELETE /v2/bot/richmenu/new1", _http.Calls);
        Assert.DoesNotContain("DELETE /v2/bot/richmenu/old0", _http.Calls);
        Assert.DoesNotContain(_http.Calls, c => c.Contains("/user/all/"));
    }

    [Fact]
    public async Task Missing_liff_id_or_image_is_reported_without_calling_LINE()
    {
        Assert.False((await Service(withImage: false).InstallAsync()).Ok);   // first: Service() writes the image file the next call would find
        Assert.False((await Service(liffId: "").InstallAsync()).Ok);
        Assert.Empty(_http.Calls);
    }
}
