using System.Text.RegularExpressions;
using EasyVocal.Core.Update;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests;

/// <summary>The portable rules of the updater: versions, GitHub's answer, which file, when to ask, source builds, notes.</summary>
public class UpdateRulesTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "EV_Test_UpdateRules_" + Guid.NewGuid().ToString("N"));

    public UpdateRulesTests() => Directory.CreateDirectory(temp);

    public void Dispose()
    {
        TestFolders.Delete(temp);
    }

    // ---------- versions ----------

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3, null)]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData(" V0.10.10 ", 0, 10, 10, null)]
    [InlineData("v1.2", 1, 2, 0, null)]
    [InlineData("2", 2, 0, 0, null)]
    [InlineData("0.2.0+4f3a2b1c", 0, 2, 0, null)]
    [InlineData("v1.0.0-beta.1", 1, 0, 0, "beta.1")]
    [InlineData("1.0.0-rc-2+build.5", 1, 0, 0, "rc-2")]
    public void Version_Parses(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(AppVersion.TryParse(text, out var v));
        Assert.Equal(new AppVersion(major, minor, patch, pre), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("latest")]
    [InlineData("1.2.3.4")]
    [InlineData("1..2")]
    [InlineData("1.x")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-be ta")]
    [InlineData("v 1.2.3")]
    [InlineData("99999999999.0.0")]
    [InlineData("１.２.３")]
    public void Version_RefusesGarbage(string? text)
    {
        Assert.False(AppVersion.TryParse(text, out _));
        Assert.Null(AppVersion.Parse(text));
    }

    [Fact]
    public void Version_ComparesLikeSemver()
    {
        string[] ascending =
        {
            "0.9", "0.10.9", "v0.10.10", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1", "2.0.0",
        };
        for (int i = 1; i < ascending.Length; i++)
        {
            var lo = AppVersion.Parse(ascending[i - 1])!.Value;
            var hi = AppVersion.Parse(ascending[i])!.Value;
            Assert.True(hi > lo, ascending[i] + " > " + ascending[i - 1]);
            Assert.True(lo < hi && lo.CompareTo(hi) < 0 && hi.CompareTo(lo) > 0);
        }
        Assert.Equal(AppVersion.Parse("v1.2"), AppVersion.Parse("1.2.0"));
        Assert.True(AppVersion.Parse("1.2")!.Value >= AppVersion.Parse("1.2.0+x")!.Value);
        Assert.Equal("1.0.0-beta.1", AppVersion.Parse("v1.0.0-beta.1+abc").ToString());
    }

    [Fact]
    public void RunningVersion_IsTheVersionInDirectoryBuildProps()
    {
        var props = File.ReadAllText(Path.Combine(TestFolders.RepoRoot(), "Directory.Build.props"));
        var expected = AppVersion.Parse(Regex.Match(props, @"<Version>([^<]+)</Version>").Groups[1].Value);
        Assert.Equal(expected, UpdateRules.RunningVersion(typeof(UpdateRulesTests).Assembly)); // informational "0.2.0+commit" included
    }

    // ---------- GitHub's answer ----------

    private static string ReleaseJson(string tag = "v0.3.0", string extra = "", string? assets = null) =>
        "{ \"tag_name\": \"" + tag + "\", \"name\": \"EasyVocal " + tag + "\", \"body\": \"## New\\n- Updates itself\"," +
        " \"html_url\": \"https://github.com/tarl3s/EasyVocal/releases/tag/" + tag + "\"," + extra +
        " \"assets\": " + (assets ?? "[{ \"name\": \"EasyVocal-win-x64.zip\", \"size\": 70000000, \"browser_download_url\": \"https://github.com/tarl3s/EasyVocal/releases/download/" + tag + "/EasyVocal-win-x64.zip\" }]") + " }";

    [Fact]
    public void ParseRelease_ReadsGitHubsAnswer()
    {
        var r = UpdateRules.ParseRelease(ReleaseJson())!;
        Assert.Equal(new AppVersion(0, 3, 0), r.Version);
        Assert.Equal("v0.3.0", r.Tag);
        Assert.Equal("## New\n- Updates itself", r.Notes);
        Assert.False(r.Prerelease);
        Assert.False(r.Draft);
        Assert.Equal("https://github.com/tarl3s/EasyVocal/releases/tag/v0.3.0", r.PageUrl);
        var a = Assert.Single(r.Assets);
        Assert.Equal("EasyVocal-win-x64.zip", a.Name);
        Assert.Equal(70000000, a.Size);
    }

    [Fact]
    public void ParseRelease_IsTolerant()
    {
        // comments, trailing commas, a size in quotes, no body, a page that isn't https, null and odd assets
        var json = """
            {
              // GitHub never sends comments, but a proxy might mangle things
              "tag_name": "0.4",
              "body": null,
              "html_url": "javascript:alert(1)",
              "prerelease": "false",
              "assets": [
                null, 42, { "name": "no url" },
                { "name": "EasyVocal-win-x64.zip", "size": "123", "browser_download_url": "https://example.com/e.zip", },
                { "name": "plain-http.zip", "browser_download_url": "http://example.com/e.zip" },
              ],
            }
            """;
        var r = UpdateRules.ParseRelease(json)!;
        Assert.Equal(new AppVersion(0, 4, 0), r.Version);
        Assert.Equal("", r.Notes);
        Assert.Equal(UpdateRules.PageUrl, r.PageUrl);
        Assert.False(r.Prerelease);
        var a = Assert.Single(r.Assets);
        Assert.Equal(123, a.Size);

        var noAssets = UpdateRules.ParseRelease("{\"tag_name\":\"v1.0.0\",\"assets\":\"none\"}")!;
        Assert.Empty(noAssets.Assets);
        Assert.Equal(-1, UpdateRules.ParseRelease("{\"tag_name\":\"v1.0.0\",\"assets\":[{\"name\":\"x.zip\",\"browser_download_url\":\"https://e.com/x\"}]}")!.Assets[0].Size);
        Assert.True(UpdateRules.ParseRelease(ReleaseJson(extra: " \"prerelease\": true, \"draft\": true,"))! is { Prerelease: true, Draft: true });

        var huge = UpdateRules.ParseRelease("{\"tag_name\":\"v1.0.0\",\"body\":\"" + new string('a', 20000) + "\"}")!;
        Assert.True(huge.Notes.Length <= UpdateRules.MaxNotesChars + 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"v1.0.0\"")]
    [InlineData("{\"message\": \"API rate limit exceeded\"}")]
    [InlineData("{\"tag_name\": \"nightly\", \"assets\": []}")]
    [InlineData("{\"tag_name\": 3}")]
    [InlineData("{\"tag_name\": \"v1.0.0\"")]
    public void ParseRelease_RefusesWhatIsNotARelease(string? json) => Assert.Null(UpdateRules.ParseRelease(json));

    // ---------- which file ----------

    private static ReleaseInfo WithAssets(params string[] names) => new(new AppVersion(0, 3, 0), "v0.3.0", "",
        names.Select(n => new ReleaseAsset(n, "https://example.com/" + n, 10)).ToList(), false, false, UpdateRules.PageUrl);

    [Fact]
    public void PickAsset_TakesTheFirstNameTheReleaseHas_IgnoringCase()
    {
        var r = WithAssets("EasyVocal-win-x64.zip.sha256", "Source code.zip", "easyvocal-WIN-x64.ZIP", "EasyVocal-linux-x64.tar.gz");
        Assert.Equal("easyvocal-WIN-x64.ZIP", UpdateRules.PickAsset(r, new[] { "EasyVocal-win-x64.zip" })!.Name);
        Assert.Equal("EasyVocal-linux-x64.tar.gz", UpdateRules.PickAsset(r, new[] { "EasyVocal-macos-arm64.zip", "EasyVocal-linux-x64.tar.gz" })!.Name);
        Assert.Null(UpdateRules.PickAsset(r, Array.Empty<string>()));
        Assert.Null(UpdateRules.PickAsset(WithAssets("EasyVocal-win-x64.zip.sha256", "EasyVocal.exe"), new[] { "EasyVocal-win-x64.zip" }));
    }

    // ---------- when to ask ----------

    private static ReleaseInfo Release(string tag, bool prerelease = false, bool draft = false) =>
        new(AppVersion.Parse(tag)!.Value, tag, "", Array.Empty<ReleaseAsset>(), prerelease, draft, UpdateRules.PageUrl);

    [Fact]
    public void ShouldOffer_OnlyANewerFinishedRelease()
    {
        var current = new AppVersion(0, 2, 0);
        Assert.True(UpdateRules.ShouldOffer(Release("v0.3.0"), current, null, manual: false));
        Assert.True(UpdateRules.ShouldOffer(Release("v0.2.1"), current, null, manual: false));
        Assert.False(UpdateRules.ShouldOffer(Release("v0.2.0"), current, null, manual: true));
        Assert.False(UpdateRules.ShouldOffer(Release("v0.1.2"), current, null, manual: true));
        Assert.False(UpdateRules.ShouldOffer(Release("v0.3.0", prerelease: true), current, null, manual: true));
        Assert.False(UpdateRules.ShouldOffer(Release("v0.3.0", draft: true), current, null, manual: true));
        Assert.False(UpdateRules.ShouldOffer(Release("v0.3.0-beta.1"), current, null, manual: true));
        // a pre-release build is offered its final version
        Assert.True(UpdateRules.ShouldOffer(Release("v0.3.0"), new AppVersion(0, 3, 0, "beta.1"), null, manual: false));
    }

    [Fact]
    public void ShouldOffer_SkippedVersionIsOnlyOfferedWhenAsked()
    {
        var current = new AppVersion(0, 2, 0);
        Assert.False(UpdateRules.ShouldOffer(Release("v0.3.0"), current, "0.3.0", manual: false));
        Assert.False(UpdateRules.ShouldOffer(Release("v0.3.0"), current, "v0.3", manual: false));
        Assert.True(UpdateRules.ShouldOffer(Release("v0.3.0"), current, "0.3.0", manual: true));   // "Check now" shows it anyway
        Assert.True(UpdateRules.ShouldOffer(Release("v0.3.1"), current, "0.3.0", manual: false));  // a newer one comes back
        Assert.True(UpdateRules.ShouldOffer(Release("v0.3.0"), current, "garbage", manual: false));
        Assert.True(UpdateRules.ShouldOffer(Release("v0.3.0"), current, "", manual: false));
    }

    // ---------- source builds ----------

    [Fact]
    public void IsSourceBuild_LooksForTheSolutionAbove()
    {
        var withSln = Path.Combine(temp, "repo");
        var bin = Path.Combine(withSln, "src", "EasyVocal", "bin", "Debug", "net8.0-windows");
        Directory.CreateDirectory(bin);
        Assert.False(UpdateRules.IsSourceBuild(bin));
        File.WriteAllText(Path.Combine(withSln, "EasyVocal.sln"), "");
        Assert.True(UpdateRules.IsSourceBuild(bin));
        Assert.True(UpdateRules.IsSourceBuild(Path.Combine(withSln, "publish", "win-x64")));

        var withProject = Path.Combine(temp, "checkout");
        Directory.CreateDirectory(Path.Combine(withProject, "src", "EasyVocal"));
        File.WriteAllText(Path.Combine(withProject, "src", "EasyVocal", "EasyVocal.csproj"), "");
        Assert.True(UpdateRules.IsSourceBuild(Path.Combine(withProject, "publish")));

        var downloaded = Path.Combine(temp, "Downloads", "EasyVocal");
        Directory.CreateDirectory(downloaded);
        Assert.False(UpdateRules.IsSourceBuild(downloaded));
        Assert.False(UpdateRules.IsSourceBuild(Path.Combine(temp, "does not exist")));
        Assert.True(UpdateRules.IsSourceBuild(AppContext.BaseDirectory)); // these tests run inside the repo
    }

    // ---------- release notes ----------

    [Fact]
    public void Notes_BecomePlainLines()
    {
        var lines = ReleaseNotes.ToLines("## What's new\r\n\r\n\r\n- **Updates** itself — see [the page](https://x.y)\n* `code` &amp; <b>tags</b>\n---\nPlain text ![img](a.png)\n\n");
        Assert.Equal(new[]
        {
            new NoteLine(NoteKind.Heading, "What's new"),
            new NoteLine(NoteKind.Blank, ""),
            new NoteLine(NoteKind.Bullet, "Updates itself — see the page"),
            new NoteLine(NoteKind.Bullet, "code & tags"),
            new NoteLine(NoteKind.Blank, ""),
            new NoteLine(NoteKind.Text, "Plain text"),
        }, lines);
        Assert.Empty(ReleaseNotes.ToLines(null));
        Assert.Empty(ReleaseNotes.ToLines("\n\n  \n"));
        Assert.True(ReleaseNotes.ToLines(string.Join("\n", Enumerable.Repeat("- x", 1000))).Count <= 200);
    }
}
