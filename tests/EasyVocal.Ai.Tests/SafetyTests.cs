using System.IO.Compression;
using EasyVocal.Ai.Import;
using EasyVocal.Ai.Rvc;
using EasyVocal.Ai.Store;
using EasyVocal.Ai.Tests.Helpers;
using EasyVocal.Tests.Helpers;

namespace EasyVocal.Ai.Tests;

/// <summary>
/// Files and links people bring are untrusted: sizes they claim are checked before anything is allocated, links are
/// https only and Hugging Face's host is matched exactly, a download is told by its bytes, ONNX models that would read
/// other files are refused.
/// </summary>
public class SafetyTests : IDisposable
{
    private readonly string work = TestFolders.New("EV_AiSafe_");

    public void Dispose() => TestFolders.Delete(work);

    [Theory]
    [InlineData("http://example.com/v.pth", null)]
    [InlineData("ftp://example.com/v.pth", null)]
    [InlineData("https://huggingface.co/a/b/blob/main/v.pth", "https://huggingface.co/a/b/resolve/main/v.pth")]
    [InlineData("https://nothuggingface.co/a/b/blob/main/v.pth", "https://nothuggingface.co/a/b/blob/main/v.pth")]
    [InlineData("https://huggingface.co.evil.example/a/blob/v.pth", "https://huggingface.co.evil.example/a/blob/v.pth")]
    public void Links_are_https_and_only_Hugging_Face_itself_is_rewritten(string link, string? want) =>
        Assert.Equal(want, Downloader.Normalize(link)?.AbsoluteUri);

    [Fact]
    public void A_download_is_told_by_its_bytes()
    {
        Assert.Equal(".pth", Downloader.Sniff(AiFixtures.P("tiny.pth")));
        Assert.Equal(".onnx", Downloader.Sniff(AiFixtures.P("tiny_hubert.onnx")));
        Assert.Equal(".index", Downloader.Sniff(AiFixtures.P("tiny.index")));
        var zip = Path.Combine(work, "x.bin");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) z.CreateEntry("readme.txt");
        Assert.Equal(".zip", Downloader.Sniff(zip));
        File.WriteAllText(Path.Combine(work, "t.bin"), "hello");
        Assert.Null(Downloader.Sniff(Path.Combine(work, "t.bin")));
    }

    [Fact]
    public void A_checkpoint_with_a_huge_pickle_is_refused_before_reading_it()
    {
        var path = Path.Combine(work, "big.pth");
        using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("archive/data.pkl", CompressionLevel.Fastest);
            using var s = e.Open();
            s.Write(new byte[9 * 1024 * 1024]);
        }
        var ex = Assert.Throws<InvalidDataException>(() => TorchCheckpoint.Load(path));
        Assert.Contains("too big", ex.Message);
    }

    [Fact]
    public void A_pickle_that_builds_endless_objects_is_refused()
    {
        // PROTO 2, then MARK + EMPTY_LIST forever: millions of objects from a few MB
        var b = new List<byte> { 0x80, 0x02 };
        for (int i = 0; i < 2_100_000; i++) b.Add((byte)']');
        b.Add((byte)'.');
        var ex = Assert.Throws<InvalidDataException>(() => TorchCheckpoint.Unpickle(b.ToArray()));
        Assert.Contains("too many", ex.Message);
    }

    [Fact]
    public void A_cut_index_is_refused_before_its_vectors_are_allocated()
    {
        var bytes = File.ReadAllBytes(AiFixtures.P("tiny.index"));
        var cut = Path.Combine(work, "cut.index");
        File.WriteAllBytes(cut, bytes[..(bytes.Length / 2)]);
        Assert.Throws<InvalidDataException>(() => FeatureIndex.Load(cut));
        var ix = FeatureIndex.Load(AiFixtures.P("tiny.index"));
        ix.NProbe = 1000;
        Assert.True(ix.NProbe <= FeatureIndex.MaxProbe, "nprobe is capped");
    }

    [Fact]
    public async Task A_zip_with_too_many_files_is_refused()
    {
        var zip = Path.Combine(work, "many.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            for (int i = 0; i <= VoiceImporter.MaxZipEntries; i++) z.CreateEntry("f" + i + ".txt");
        var data = Path.Combine(work, "data");
        var pack = new AiPack(data);
        var importer = new VoiceImporter(pack, new VoiceLibrary(pack));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportFilesAsync(new[] { zip }));
        Assert.Contains("too many files", ex.Message);
    }

    [Fact]
    public void An_ONNX_model_whose_weights_are_in_other_files_is_spotted()
    {
        // ModelProto { graph (7) { initializer (5) { data_location (14) = EXTERNAL } } }
        byte[] external = { 0x3A, 0x04, 0x2A, 0x02, 0x70, 0x01 };
        byte[] inline = { 0x3A, 0x04, 0x2A, 0x02, 0x70, 0x00 };
        Assert.True(OnnxRewriter.HasExternalData(external));
        Assert.False(OnnxRewriter.HasExternalData(inline));
        Assert.False(OnnxRewriter.HasExternalData(File.ReadAllBytes(AiFixtures.P("tiny_hubert.onnx"))));
    }
}
