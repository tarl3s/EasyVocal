using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EasyVocal.Ai;
using EasyVocal.Ai.Import;
using EasyVocal.Ai.Store;

namespace EasyVocal.E2E;

/// <summary>
/// The e2e run's AI pack, served with its release description by a small web server on 127.0.0.1, so the app
/// downloads, checks and installs it the way it does from GitHub; the server also serves a voice .pth for "Add AI
/// voice → from a link". Either the tiny test models (tests/EasyVocal.Ai.Tests/Fixtures, a few MB, the real
/// interfaces; <c>--e2e-ai &lt;fixtures&gt;</c>) zipped like a release asset, or a real release asset
/// (<c>--e2e-ai-pack &lt;EasyVocal-AI-*.zip&gt;</c>: the packaged app on the real models with the pack's own ONNX
/// Runtime; the pack's base generator is the .pth imported by file and by link).
/// </summary>
internal sealed class E2EAiPack : IDisposable
{
    public const string TinyAssetName = "EasyVocal-AI-test.zip";
    private const string TinyVoiceA = "tiny-a";

    private readonly LocalWeb web = new();

    private E2EAiPack(string zip, string assetName, string voicePth, string voiceA, bool real)
    {
        AssetName = assetName;
        VoicePth = voicePth;
        VoiceA = voiceA;
        Real = real;
        var note = zip + ".sha256";
        string sha = real && File.Exists(note) ? File.ReadAllText(note).Split(' ', '\n')[0].Trim() : Downloader.Sha256Of(zip);
        long size = new FileInfo(zip).Length;
        web.MapFile("/pack.zip", zip);
        var release = new
        {
            tag_name = PackSource.Tag,
            assets = new object[] { new { name = assetName, browser_download_url = web.Url("/pack.zip"), size, digest = "sha256:" + sha } },
        };
        web.Map("/release", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(release)));
        web.MapFile("/voices/by-link", voicePth); // no extension: told by its bytes
        PackBytes = size;
    }

    /// <summary>The release asset's name the app is told to install.</summary>
    public string AssetName { get; }

    /// <summary>The bundled voice the step plays.</summary>
    public string VoiceA { get; }

    /// <summary>The real pack: real models, so the step waits longer.</summary>
    public bool Real { get; }

    public string ReleaseUrl => web.Url("/release");
    public string VoiceLink => web.Url("/voices/by-link");
    public long PackBytes { get; }

    /// <summary>A voice .pth to import by file (and, served, by link).</summary>
    public string VoicePth { get; }

    /// <summary>
    /// The real pack when <paramref name="realZip"/> is given, else the tiny one made of <paramref name="fixtures"/>;
    /// null when neither is there (the e2e run then skips the AI steps).
    /// </summary>
    public static E2EAiPack? TryCreate(string? fixtures, string? realZip, string work)
    {
        if (realZip != null && File.Exists(realZip))
        {
            Directory.CreateDirectory(work);
            Downloader.AllowLoopbackHttp = true; // the test server is plain http on this computer
            var pth = Path.Combine(work, "voice.pth");
            using (var z = ZipFile.OpenRead(realZip))
                (z.GetEntry("engine/" + BaseModels.BaseGeneratorId + ".pth") ?? throw new InvalidDataException("The AI pack has no base generator.")).ExtractToFile(pth, true);
            return new E2EAiPack(realZip, Path.GetFileName(realZip), pth, BundledRecipe.Embedded().Voices[0].Id, real: true);
        }
        if (fixtures == null || !File.Exists(Path.Combine(fixtures, "tiny.pth"))) return null;
        Directory.CreateDirectory(work);
        Downloader.AllowLoopbackHttp = true;
        var zip = Path.Combine(work, TinyAssetName);
        BuildZip(fixtures, Path.Combine(work, "pack"), zip);
        return new E2EAiPack(zip, TinyAssetName, Path.Combine(fixtures, "tiny.pth"), TinyVoiceA, real: false);
    }

    private static void BuildZip(string fx, string dir, string zip)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var engine = Path.Combine(dir, "engine");
        Directory.CreateDirectory(Path.Combine(engine, "templates"));
        File.Copy(Path.Combine(fx, "tiny_hubert.onnx"), Path.Combine(engine, AiPack.ContentFp32));
        File.Copy(Path.Combine(fx, "tiny_hubert.onnx"), Path.Combine(engine, AiPack.ContentInt8));
        File.Copy(Path.Combine(fx, "tiny_rmvpe.onnx"), Path.Combine(engine, AiPack.Rmvpe));
        File.Copy(Path.Combine(fx, "tiny.pth"), Path.Combine(engine, BaseModels.BaseGeneratorId + ".pth"));
        File.Copy(Path.Combine(fx, "tiny_enc.onnx.gz"), Path.Combine(engine, "templates", "rvc_v2_f0_40k_enc.onnx.gz"));
        File.Copy(Path.Combine(fx, "tiny_dec.onnx.gz"), Path.Combine(engine, "templates", "rvc_v2_f0_40k_dec.onnx.gz"));
        var emb = TorchCheckpoint.Load(Path.Combine(fx, "tiny.pth")).Tensors["emb_g.weight"];
        Voice(dir, TinyVoiceA, "Tiny A", "👩", RvcTemplates.SpeakerVector(emb, 0), 220);
        Voice(dir, "tiny-b", "Tiny B", "👨", RvcTemplates.SpeakerVector(emb, 1), 110);
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(dir, zip);
        Directory.Delete(dir, true);
    }

    private static void Voice(string dir, string id, string name, string emoji, float[] speaker, double targetHz)
    {
        var vd = Path.Combine(dir, "voices", id);
        Directory.CreateDirectory(vd);
        var spk = Path.Combine(vd, "speaker.f32");
        File.WriteAllBytes(spk, MemoryMarshalBytes(speaker));
        var m = new VoiceManifest
        {
            Id = id, Name = name, Emoji = emoji, Kind = VoiceKinds.Speaker, Base = BaseModels.BaseGeneratorId, SampleRate = 40000,
            TargetHz = targetHz, BuiltIn = true, Licence = "test", Attribution = "EasyVocal's test models",
            Files = { new VoiceFile { Role = "speaker", Name = "speaker.f32", Size = new FileInfo(spk).Length, Sha256 = Downloader.Sha256Of(spk) } },
        };
        m.Write(Path.Combine(vd, VoiceManifest.FileName));
    }

    private static byte[] MemoryMarshalBytes(float[] v)
    {
        var b = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, b, 0, b.Length);
        return b;
    }

    public void Dispose() => web.Dispose();
}

/// <summary>A tiny HTTP/1.1 server on 127.0.0.1 that answers GETs with fixed bytes or a file (no HttpListener: it needs URL reservations on Windows).</summary>
internal sealed class LocalWeb : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Dictionary<string, (byte[]? Bytes, string? File)> routes = new();
    private readonly CancellationTokenSource stop = new();

    public LocalWeb()
    {
        listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{path}";

    public void Map(string path, byte[] body)
    {
        lock (routes) routes[path] = (body, null);
    }

    /// <summary>Serves a file from disk (read as it is sent: the real AI pack is ~700 MB).</summary>
    public void MapFile(string path, string file)
    {
        lock (routes) routes[path] = (null, file);
    }

    private async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient c)
    {
        using (c)
        {
            try
            {
                var s = c.GetStream();
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await s.ReadAsync(one, stop.Token) <= 0) return;
                    head.Append((char)one[0]);
                }
                var path = head.ToString().Split(' ')[1];
                (byte[]? Bytes, string? File) route;
                bool found;
                lock (routes) found = routes.TryGetValue(path, out route);
                using Stream body = !found ? new MemoryStream(Encoding.ASCII.GetBytes("not found"))
                    : route.File != null ? File.OpenRead(route.File) : new MemoryStream(route.Bytes!);
                var status = found ? "200 OK" : "404 Not Found";
                var h = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Type: application/octet-stream\r\nContent-Length: {body.Length}\r\n\r\n");
                await s.WriteAsync(h, stop.Token);
                await body.CopyToAsync(s, 1 << 20, stop.Token);
                await s.FlushAsync(stop.Token);
            }
            catch (Exception) { /* the client went away */ }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
    }
}
