using System.Text.Json.Nodes;
using XilousProbe.Update;

namespace XilousProbe.Tests;

public class UpdaterTests
{
    private static JsonObject Release(string tag, string? url = null, bool prerelease = false, string? digest = "sha256:ABC123") =>
        new()
        {
            ["tag_name"] = tag,
            ["draft"] = false,
            ["prerelease"] = prerelease,
            ["assets"] = new JsonArray(
                new JsonObject { ["name"] = "notes.txt", ["browser_download_url"] = "https://example.com/notes.txt" },
                new JsonObject
                {
                    ["name"] = "XilousProbe.exe",
                    ["size"] = 76_000_000,
                    ["digest"] = digest,
                    ["browser_download_url"] = url ?? $"https://github.com/Xilous/xilous-ui-probe/releases/download/{tag}/XilousProbe.exe",
                }),
        };

    [Theory]
    [InlineData("v0.1.12", "0.1.12")]
    [InlineData("0.2.3", "0.2.3")]
    [InlineData("V1.0.0", "1.0.0")]
    [InlineData("v1.2", null)]
    [InlineData("nightly", null)]
    [InlineData(null, null)]
    public void Parses_release_tags(string? tag, string? expected) =>
        Assert.Equal(expected is null ? null : Version.Parse(expected), Updater.ParseTag(tag));

    [Fact]
    public void Offers_a_newer_release_with_its_exe_and_digest()
    {
        var update = Updater.FromRelease(Release("v0.1.7"), new Version(0, 1, 5));
        Assert.NotNull(update);
        Assert.Equal(new Version(0, 1, 7), update.Version);
        Assert.Equal(76_000_000, update.Size);
        Assert.Equal("abc123", update.Sha256);
        Assert.EndsWith("/v0.1.7/XilousProbe.exe", update.DownloadUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData("v0.1.5")] // same
    [InlineData("v0.1.4")] // older
    public void Ignores_releases_that_are_not_newer(string tag) =>
        Assert.Null(Updater.FromRelease(Release(tag), new Version(0, 1, 5)));

    [Fact]
    public void Ignores_prereleases() =>
        Assert.Null(Updater.FromRelease(Release("v0.2.0", prerelease: true), new Version(0, 1, 5)));

    [Fact]
    public void Refuses_a_download_from_anywhere_but_this_repo() =>
        Assert.Null(Updater.FromRelease(Release("v0.2.0", url: "https://evil.example/XilousProbe.exe"), new Version(0, 1, 5)));

    [Fact]
    public void Works_without_a_digest()
    {
        var update = Updater.FromRelease(Release("v0.2.0", digest: null), new Version(0, 1, 5));
        Assert.NotNull(update);
        Assert.Null(update.Sha256);
    }

    [Fact]
    public void Local_builds_never_update_themselves() => Assert.False(Updater.IsReleaseBuild);
}
