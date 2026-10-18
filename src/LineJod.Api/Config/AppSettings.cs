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
    public string PathBase { get; set; } = "";         // "" on a site of its own (a subdomain); "/linejod" only when the app runs in a sub-folder of a site
}
