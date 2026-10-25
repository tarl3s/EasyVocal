using System.Text.RegularExpressions;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Tests;

public class PackagingTests
{
    [Fact]
    public void Csproj_HasVersionAndIcon()
    {
        var root = TestFolders.RepoRoot();
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var version = Regex.Match(props, @"<Version>([^<]+)</Version>");
        Assert.True(version.Success, "Directory.Build.props has no <Version>");
        Assert.Matches(@"^\d+\.\d+\.\d+$", version.Groups[1].Value);

        var csproj = File.ReadAllText(Path.Combine(root, "src", "EasyVocal", "EasyVocal.csproj"));
        var icon = Regex.Match(csproj, @"<ApplicationIcon>([^<]+)</ApplicationIcon>");
        Assert.True(icon.Success, "EasyVocal.csproj has no <ApplicationIcon>");
        Assert.True(File.Exists(Path.Combine(root, "src", "EasyVocal", icon.Groups[1].Value)), "the icon file is missing");
    }
}
