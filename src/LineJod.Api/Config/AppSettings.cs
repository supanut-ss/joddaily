namespace LineJod.Config;

/// <summary>Bound from the root of appsettings / environment variables.</summary>
public class AppSettings
{
    public LineSection Line { get; set; } = new();
    public LiffSection Liff { get; set; } = new();
    public JobsSection Jobs { get; set; } = new();
    public AppSection App { get; set; } = new();
}

public class LineSection
{
    public string ChannelSecret { get; set; } = "";
    public string ChannelAccessToken { get; set; } = "";
    public string LoginChannelId { get; set; } = "";   // LINE Login channel that owns the LIFF app
}

public class LiffSection
{
    public string Id { get; set; } = "";
}

public class JobsSection
{
    public string Key { get; set; } = "";
}

public class AppSection
{
    public string PublicBaseUrl { get; set; } = "";   // https://<this host>[/path base], used in links sent to users (CSV download)
    public string WebBaseUrl { get; set; } = "";      // the frontend (LIFF page) site, e.g. https://linejod.drivetodev.online ; several separated by commas. Only these origins may call the API from a browser.
    public string PathBase { get; set; } = "";         // "" on a site of its own (a subdomain); "/linejod" only when the app runs in a sub-folder of a site
}

public static class WebOrigins
{
    /// <summary>"https://a.example/x, https://b.example:8443/" → ["https://a.example", "https://b.example:8443"]; anything that is not an absolute http(s) URL is dropped.</summary>
    public static string[] Parse(string? setting) =>
        (setting ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(o => Uri.TryCreate(o, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.GetLeftPart(UriPartial.Authority) : null)
            .Where(o => o != null).Select(o => o!).Distinct().ToArray();
}
