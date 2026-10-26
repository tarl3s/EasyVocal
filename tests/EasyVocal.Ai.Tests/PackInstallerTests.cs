using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EasyVocal.Ai.Store;
using EasyVocal.Ai.Tests.Helpers;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Ai.Tests;

/// <summary>
/// The AI pack's download and install (<see cref="PackInstaller"/>) against a local server: the release's asset and
/// SHA-256 (digest or .sha256 file), a download that stops goes on from where it stopped (HTTP range), a wrong file is
/// refused and nothing is installed, an incomplete pack is refused, the install keeps the person's imported voices, and
/// Remove takes the pack (and, when asked, the imported voices) away.
/// </summary>
public class PackInstallerTests : IDisposable
{
    private readonly string work = TestFolders.New("EV_AiPack_");

    public void Dispose() => TestFolders.Delete(work);

    /// <summary>The tiny pack zipped as a release asset (engine + two bundled voices).</summary>
    private static byte[] PackZip(bool withInt8 = true)
    {
        var data = AiFixtures.TinyDataFolder();
        try
        {
            var pack = new AiPack(data);
            if (withInt8) File.Copy(Path.Combine(pack.EngineFolder, AiPack.ContentFp32), Path.Combine(pack.EngineFolder, AiPack.ContentInt8));
            var zip = Path.Combine(data, "pack.zip");
            ZipFile.CreateFromDirectory(pack.Root, zip);
            return File.ReadAllBytes(zip);
        }
        finally { TestFolders.Delete(data); }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();

    private static byte[] Release(LocalHttp http, string asset, long size, string? digest, bool sidecar = false)
    {
        var assets = new List<object> { new { name = asset, browser_download_url = http.Url("/" + asset), size, digest } };
        if (sidecar) assets.Add(new { name = asset + ".sha256", browser_download_url = http.Url("/" + asset + ".sha256"), size = 64, digest = (string?)null });
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { tag_name = PackSource.Tag, assets }));
    }

    [Fact]
    public void The_release_names_the_asset_its_size_and_SHA_256()
    {
        Assert.Equal("EasyVocal-AI-win-x64.zip", PackSource.AssetName("win", System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal("EasyVocal-AI-macos-arm64.zip", PackSource.AssetName("macos", System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Equal("EasyVocal-AI-linux-x64.zip", PackSource.AssetName("linux", System.Runtime.InteropServices.Architecture.X64));
        Assert.Null(PackSource.AssetName("linux", System.Runtime.InteropServices.Architecture.Arm64));

        string sha = new('a', 64);
        string json = $$"""{"assets":[{"name":"X.zip","browser_download_url":"https://e.invalid/X.zip","size":1000,"digest":"sha256:{{sha}}"}]}""";
        var (url, size, digest, side) = PackSource.Pick(json, "X.zip");
        Assert.Equal(("https://e.invalid/X.zip", 1000L, sha), (url.ToString(), size, digest));
        Assert.Null(side);
        Assert.Throws<PackUnavailableException>(() => PackSource.Pick(json, "Y.zip"));
        string noSha = """{"assets":[{"name":"X.zip","browser_download_url":"https://e.invalid/X.zip","size":1000}]}""";
        Assert.Contains("SHA-256", Assert.Throws<PackUnavailableException>(() => PackSource.Pick(noSha, "X.zip")).Message);
    }

    [Fact]
    public async Task Downloads_checks_and_installs_keeping_imported_voices_then_removes()
    {
        var data = Path.Combine(work, "data");
        var pack = new AiPack(data);
        // a voice the person imported earlier (the pack must never touch it)
        Directory.CreateDirectory(pack.VoiceFolder("user-mine"));
        new VoiceManifest { Id = "user-mine", Name = "Mine", Kind = VoiceKinds.Pth }.Write(Path.Combine(pack.VoiceFolder("user-mine"), VoiceManifest.FileName));

        var zip = PackZip();
        using var http = new LocalHttp();
        http.Map("/A.zip", new LocalHttp.Route(200, zip, Ranges: true));
        http.Map("/A.zip.sha256", new LocalHttp.Route(200, Encoding.ASCII.GetBytes(Sha(zip) + "  A.zip\n")));
        http.Map("/release", new LocalHttp.Route(200, Release(http, "A.zip", zip.Length, null, sidecar: true)));
        var installer = new PackInstaller(pack, releaseApi: http.Url("/release"), assetName: "A.zip");

        var offer = await installer.FindAsync();
        Assert.Equal(Sha(zip), offer.Sha256); // from the .sha256 file next to it
        var stages = new List<PackStage>();
        await installer.InstallAsync(offer, new SyncProgress<PackProgress>(p => stages.Add(p.Stage)));
        Assert.True(pack.Installed);
        Assert.Contains(PackStage.Downloading, stages);
        Assert.Contains(PackStage.Installing, stages);
        Assert.True(File.Exists(Path.Combine(pack.EngineFolder, AiPack.ContentInt8)));
        Assert.True(File.Exists(Path.Combine(pack.VoiceFolder(AiFixtures.VoiceA), VoiceManifest.FileName)));
        Assert.True(File.Exists(Path.Combine(pack.VoiceFolder("user-mine"), VoiceManifest.FileName)), "the imported voice is kept");
        Assert.False(Directory.Exists(installer.DownloadFolder), "the download is cleaned up");
        Assert.Empty(Directory.GetDirectories(data, "AI.staging-*"));

        installer.Remove(alsoImported: false);
        Assert.False(pack.Installed);
        Assert.False(Directory.Exists(pack.VoiceFolder(AiFixtures.VoiceA)));
        Assert.True(Directory.Exists(pack.VoiceFolder("user-mine")), "Remove keeps the imported voices unless asked");
        installer.Remove(alsoImported: true);
        Assert.False(Directory.Exists(pack.VoiceFolder("user-mine")));
    }

    [Fact]
    public async Task A_stopped_download_goes_on_from_where_it_stopped()
    {
        var data = Path.Combine(work, "data");
        var pack = new AiPack(data);
        var zip = PackZip();
        using var http = new LocalHttp();
        http.Map("/A.zip", new LocalHttp.Route(200, zip, PauseMs: 30, Ranges: true));
        http.Map("/release", new LocalHttp.Route(200, Release(http, "A.zip", zip.Length, "sha256:" + Sha(zip))));
        var installer = new PackInstaller(pack, releaseApi: http.Url("/release"), assetName: "A.zip");
        var offer = await installer.FindAsync();

        using (var cts = new CancellationTokenSource())
        {
            var p = new SyncProgress<PackProgress>(x => { if (x.Done > zip.Length / 3) cts.Cancel(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(offer, p, cts.Token));
        }
        long kept = installer.Downloaded(offer);
        Assert.InRange(kept, 1, zip.Length - 1);
        Assert.False(pack.Installed);

        http.Map("/A.zip", new LocalHttp.Route(200, zip, Ranges: true));
        await installer.InstallAsync(offer);
        Assert.True(pack.Installed);
        Assert.Equal(1, http.RangedRequests); // the second try asked only for the rest
    }

    [Fact]
    public async Task A_wrong_or_incomplete_pack_is_refused_and_nothing_is_installed()
    {
        var data = Path.Combine(work, "data");
        var pack = new AiPack(data);
        var zip = PackZip();
        using var http = new LocalHttp();
        http.Map("/A.zip", new LocalHttp.Route(200, zip, Ranges: true));
        http.Map("/release", new LocalHttp.Route(200, Release(http, "A.zip", zip.Length, "sha256:" + new string('0', 64))));
        var installer = new PackInstaller(pack, releaseApi: http.Url("/release"), assetName: "A.zip");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await installer.InstallAsync(await installer.FindAsync()));
        Assert.False(pack.Installed);
        Assert.Equal(0, installer.Downloaded(await installer.FindAsync())); // wrong bytes are never resumed from

        var noInt8 = PackZip(withInt8: false);
        http.Map("/B.zip", new LocalHttp.Route(200, noInt8));
        http.Map("/release2", new LocalHttp.Route(200, Release(http, "B.zip", noInt8.Length, "sha256:" + Sha(noInt8))));
        var second = new PackInstaller(pack, releaseApi: http.Url("/release2"), assetName: "B.zip");
        var e = await Assert.ThrowsAsync<InvalidDataException>(async () => await second.InstallAsync(await second.FindAsync()));
        Assert.Contains("incomplete", e.Message);
        Assert.False(pack.Installed);
        Assert.Empty(Directory.GetDirectories(data, "AI.staging-*"));

        http.Map("/release3", new LocalHttp.Route(404, Encoding.ASCII.GetBytes("{}")));
        var none = new PackInstaller(pack, releaseApi: http.Url("/release3"), assetName: "B.zip");
        Assert.Contains("isn't available", (await Assert.ThrowsAsync<PackUnavailableException>(() => none.FindAsync())).Message);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
