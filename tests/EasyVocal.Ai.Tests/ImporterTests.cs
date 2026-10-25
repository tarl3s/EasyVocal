using System.IO.Compression;
using EasyVocal.Ai.Store;
using EasyVocal.Ai.Tests.Helpers;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Ai.Tests;

/// <summary>Adding voices by file and by link (a local HTTP server stands in for Hugging Face).</summary>
public class ImporterTests : IDisposable
{
    private readonly string data = AiFixtures.TinyDataFolder();
    private readonly AiPack pack;
    private readonly VoiceLibrary library;
    private readonly VoiceImporter importer;
    private readonly LocalHttp http = new();

    public ImporterTests()
    {
        pack = new AiPack(data);
        library = new VoiceLibrary(pack);
        importer = new VoiceImporter(pack, library);
    }

    public void Dispose()
    {
        http.Dispose();
        TestFolders.Delete(data);
    }

    [Fact]
    public async Task A_pth_and_its_index_become_a_checked_voice()
    {
        var m = await importer.ImportFilesAsync(new[] { AiFixtures.P("tiny.pth"), AiFixtures.P("tiny.index") }, "My robot pal", "🤖");
        Assert.Equal(VoiceKinds.Pth, m.Kind);
        Assert.Equal(40000, m.SampleRate);
        Assert.Equal("My robot pal", m.Name);
        Assert.Equal("🤖", m.Emoji);
        Assert.StartsWith("user-my-robot-pal", m.Id);
        Assert.Equal(new[] { "index", "model" }, m.Files.Select(f => f.Role).OrderBy(r => r));
        Assert.Equal("tiny test model", m.Description);
        var back = library.Find(m.Id)!;
        Assert.Null(library.Check(back));
        Assert.Equal(m.Files.Select(f => f.Sha256), back.Files.Select(f => f.Sha256));

        // and it plays
        using var service = new AiVoiceService(data) { Threads = 1 };
        using var v = service.Load(m.Id, AiQuality.Max, Runtime.AiDevice.Cpu);
        Assert.NotNull(v.Voice.Index);
        Assert.NotNull(v.Voice.Speaker);

        // a damaged file is noticed
        File.WriteAllBytes(library.PathOf(back, back.Files.First(f => f.Role == "model")), new byte[10]);
        Assert.NotNull(library.Check(back));
        Assert.True(library.Delete(m.Id));
        Assert.Null(library.Find(m.Id));
    }

    [Fact]
    public async Task An_onnx_voice_is_recognised_by_its_inputs()
    {
        var m = await importer.ImportFilesAsync(new[] { AiFixtures.P("tiny_single.onnx") });
        Assert.Equal(VoiceKinds.OnnxSingle, m.Kind);
        Assert.Equal(40000, m.SampleRate);
        Assert.Equal("Tiny single", m.Name);
        using var service = new AiVoiceService(data) { Threads = 1 };
        using var conv = await service.CreateConverterAsync(m.Id, AiQuality.Light, Runtime.AiDevice.Cpu, CancellationToken.None);
        ServiceTests.Lockstep(conv, ServiceTests.Speech48(1.2));
        Assert.True(conv.Stats.Ready, conv.Stats.ToString());
    }

    [Fact]
    public async Task What_isnt_a_usable_voice_is_refused_and_nothing_is_left()
    {
        var dir = TestFolders.New("EV_AiImp_");
        try
        {
            var txt = Path.Combine(dir, "notes.pth");
            File.WriteAllText(txt, "hello");
            await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportFilesAsync(new[] { txt }));
            await Assert.ThrowsAnyAsync<Exception>(() => importer.ImportFilesAsync(new[] { AiFixtures.P("tiny_hubert.onnx") }));
            var zip = Path.Combine(dir, "empty.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) z.CreateEntry("readme.txt");
            await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportFilesAsync(new[] { zip }));
            Assert.Empty(library.All().Where(v => !v.BuiltIn));
            Assert.False(Directory.Exists(Path.Combine(pack.Root, "tmp")) && Directory.EnumerateFileSystemEntries(Path.Combine(pack.Root, "tmp")).Any());
        }
        finally { TestFolders.Delete(dir); }
    }

    private static byte[] Zip(params (string Name, string From)[] files)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, from) in files)
            {
                var e = z.CreateEntry(name);
                using var s = e.Open();
                s.Write(File.ReadAllBytes(from));
            }
        return ms.ToArray();
    }

    [Fact]
    public async Task A_link_to_a_zip_is_downloaded_unpacked_and_imported_with_progress()
    {
        var zip = Zip(("weights/MyVoice.pth", AiFixtures.P("tiny.pth")),
                      ("logs/added_IVF4_Flat_nprobe_1_v2.index", AiFixtures.P("tiny.index")),
                      ("G_2333333.pth", AiFixtures.P("tiny.pth")), // a training checkpoint: skipped
                      ("../../evil.pth", AiFixtures.P("tiny.pth")));
        http.Map("/voice.zip", new LocalHttp.Route(200, zip));
        var seen = new List<ImportProgress>();
        var m = await importer.ImportUrlAsync(http.Url("/voice.zip"), progress: new SyncProgress<ImportProgress>(seen.Add));
        Assert.Equal(VoiceKinds.Pth, m.Kind);
        Assert.Equal(http.Url("/voice.zip"), m.Source);
        Assert.Contains(seen, p => p.Stage == ImportStage.Downloading && p.Done == zip.Length && p.Total == zip.Length);
        Assert.Contains(seen, p => p.Stage == ImportStage.Unpacking);
        Assert.NotNull(m.Index);
        Assert.False(File.Exists(Path.Combine(pack.Root, "..", "evil.pth")));
        Assert.False(Directory.EnumerateFileSystemEntries(Path.Combine(pack.Root, "tmp")).Any(), "the download's leftovers are gone");
    }

    [Fact]
    public async Task Downloads_refuse_what_is_too_big_missing_or_different_and_can_be_cancelled()
    {
        var dir = TestFolders.New("EV_AiDl_");
        try
        {
            var body = new byte[200_000];
            new Random(1).NextBytes(body);
            http.Map("/big", new LocalHttp.Route(200, body));
            http.Map("/nolength", new LocalHttp.Route(200, body, SendLength: false));
            http.Map("/liar", new LocalHttp.Route(200, body, FakeLength: 10L * 1024 * 1024 * 1024));
            http.Map("/slow", new LocalHttp.Route(200, body, PauseMs: 200));
            var dst = Path.Combine(dir, "x.bin");

            var e1 = await Assert.ThrowsAsync<IOException>(() => Downloader.DownloadAsync(new Uri(http.Url("/big")), dst, 100_000));
            Assert.Contains("too big", e1.Message);
            await Assert.ThrowsAsync<IOException>(() => Downloader.DownloadAsync(new Uri(http.Url("/nolength")), dst, 100_000));
            await Assert.ThrowsAsync<IOException>(() => Downloader.DownloadAsync(new Uri(http.Url("/liar")), dst, 100_000));
            var e404 = await Assert.ThrowsAsync<IOException>(() => Downloader.DownloadAsync(new Uri(http.Url("/nothing")), dst, 100_000));
            Assert.Contains("404", e404.Message);
            await Assert.ThrowsAsync<InvalidDataException>(() => Downloader.DownloadAsync(new Uri(http.Url("/big")), dst, 1_000_000, expectedSha256: new string('0', 64)));
            using var cts = new CancellationTokenSource(300);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Downloader.DownloadAsync(new Uri(http.Url("/slow")), dst, 1_000_000, ct: cts.Token));
            Assert.Empty(Directory.GetFiles(dir)); // nothing half-written stays

            var sha = await Downloader.DownloadAsync(new Uri(http.Url("/nolength")), dst, 1_000_000);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant(), sha);
            Assert.Equal(body, File.ReadAllBytes(dst));
        }
        finally { TestFolders.Delete(dir); }
    }

    [Theory]
    [InlineData("https://huggingface.co/someone/voices/blob/main/Calm%20Voice.zip", "https://huggingface.co/someone/voices/resolve/main/Calm%20Voice.zip")]
    [InlineData("https://example.org/a/b.pth", "https://example.org/a/b.pth")]
    [InlineData("http://example.org/a/b.pth", null)]
    [InlineData("file:///C:/x.pth", null)]
    [InlineData("not a link", null)]
    public void Links_are_made_downloadable(string text, string? want)
    {
        Assert.Equal(want, Downloader.Normalize(text)?.AbsoluteUri);
    }

    [Fact]
    public void New_ids_are_plain_and_unique()
    {
        var a = library.NewId("Deep  calm man!");
        Assert.Equal("user-deep-calm-man", a);
        Directory.CreateDirectory(pack.VoiceFolder(a));
        Assert.Equal("user-deep-calm-man-2", library.NewId("Deep calm man"));
        Assert.Equal("user-voice", library.NewId("🙂🙂"));
    }

    [Fact]
    public void A_broken_voice_json_is_skipped()
    {
        var dir = pack.VoiceFolder("broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, VoiceManifest.FileName), "{ not json");
        var dir2 = pack.VoiceFolder("escape");
        Directory.CreateDirectory(dir2);
        File.WriteAllText(Path.Combine(dir2, VoiceManifest.FileName), "{\"id\":\"escape\",\"files\":[{\"role\":\"model\",\"name\":\"../../x.pth\"}]}");
        var ids = library.All().Select(v => v.Id).ToList();
        Assert.DoesNotContain("broken", ids);
        Assert.DoesNotContain("escape", ids);
        Assert.Contains(AiFixtures.VoiceA, ids);
    }

    /// <summary>A progress that reports on the calling thread (Progress&lt;T&gt; posts, so the test could miss the last ones).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> report;
        public SyncProgress(Action<T> report) => this.report = report;
        public void Report(T value) { lock (this) report(value); }
    }
}
