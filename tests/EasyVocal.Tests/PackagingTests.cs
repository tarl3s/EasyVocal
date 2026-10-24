using System.Text.RegularExpressions;

namespace EasyVocal.Tests;

public class PackagingTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Directory.Build.props not found above the test folder");
    }

    [Fact]
    public void Csproj_HasVersionAndIcon()
    {
        var root = RepoRoot();
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
