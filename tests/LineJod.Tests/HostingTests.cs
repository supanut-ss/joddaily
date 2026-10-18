using LineJod.Config;

namespace LineJod.Tests;

public class HostingTests
{
    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("https://web.example", new[] { "https://web.example" })]
    [InlineData("https://web.example/", new[] { "https://web.example" })]                                  // a trailing slash is not part of an origin
    [InlineData("https://web.example/liff/index.html", new[] { "https://web.example" })]                   // nor is a path
    [InlineData("https://web.example:8443", new[] { "https://web.example:8443" })]
    [InlineData(" https://a.example , https://b.example/x ", new[] { "https://a.example", "https://b.example" })]
    [InlineData("https://a.example,https://a.example/", new[] { "https://a.example" })]                    // duplicates collapse
    [InlineData("not a url, ftp://files.example, https://ok.example", new[] { "https://ok.example" })]     // anything that is not http(s) is dropped
    [InlineData("*", new string[0])]                                                                        // no wildcard: only named sites may call the API
    public void Web_origins_are_read_from_the_setting(string? setting, string[] expected) =>
        Assert.Equal(expected, WebOrigins.Parse(setting));
}
