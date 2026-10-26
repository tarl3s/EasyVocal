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
/// The e2e run's AI voices: an AI pack made of the tiny test models (tests/EasyVocal.Ai.Tests/Fixtures, a few MB, the
/// real interfaces), zipped like a release asset and served with its release description by a small web server on
/// 127.0.0.1, so the app downloads, checks and installs it the way it does from GitHub. The server also serves a voice
/// .pth for "Add AI voice → from a link".
/// </summary>
internal sealed class TinyAiPack : IDisposable
{
    public const string AssetName = "EasyVocal-AI-test.zip";
    public const string VoiceA = "tiny-a", VoiceB = "tiny-b";

    private readonly LocalWeb web = new();

    private TinyAiPack(string fixtures, string work)
    {
        Fixtures = fixtures;
        var zip = Path.Combine(work, AssetName);
        BuildZip(fixtures, Path.Combine(work, "pack"), zip);
        var bytes = File.ReadAllBytes(zip);
        string sha = Downloader.Sha256Of(zip);
        web.Map("/pack.zip", bytes);
        var release = new
        {
            tag_name = PackSource.Tag,
            assets = new object[] { new { name = AssetName, browser_download_url = web.Url("/pack.zip"), size = bytes.Length, digest = "sha256:" + sha } },
        };
        web.Map("/release", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(release)));
        web.Map("/voices/by-link", File.ReadAllBytes(Path.Combine(fixtures, "tiny.pth"))); // no extension: told by its bytes
        PackBytes = bytes.Length;
    }

    public string Fixtures { get; }
    public string ReleaseUrl => web.Url("/release");
    public string VoiceLink => web.Url("/voices/by-link");
    public long PackBytes { get; }
    public string VoicePth => Path.Combine(Fixtures, "tiny.pth");

    /// <summary>Null when the fixtures aren't there (the e2e run then skips the AI steps).</summary>
    public static TinyAiPack? TryCreate(string? fixtures, string work)
    {
        if (fixtures == null || !File.Exists(Path.Combine(fixtures, "tiny.pth"))) return null;
        Directory.CreateDirectory(work);
        Downloader.AllowLoopbackHttp = true; // the test server is plain http on this computer
        return new TinyAiPack(fixtures, work);
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
        Voice(dir, VoiceA, "Tiny A", "👩", RvcTemplates.SpeakerVector(emb, 0), 220);
        Voice(dir, VoiceB, "Tiny B", "👨", RvcTemplates.SpeakerVector(emb, 1), 110);
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

/// <summary>A tiny HTTP/1.1 server on 127.0.0.1 that answers GETs with fixed bytes (no HttpListener: it needs URL reservations on Windows).</summary>
internal sealed class LocalWeb : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Dictionary<string, byte[]> routes = new();
    private readonly CancellationTokenSource stop = new();

    public LocalWeb()
    {
        listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{path}";

    public void Map(string path, byte[] body)
    {
        lock (routes) routes[path] = body;
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
                byte[]? body;
                lock (routes) routes.TryGetValue(path, out body);
                var status = body == null ? "404 Not Found" : "200 OK";
                body ??= Encoding.ASCII.GetBytes("not found");
                var h = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Type: application/octet-stream\r\nContent-Length: {body.Length}\r\n\r\n");
                await s.WriteAsync(h, stop.Token);
                await s.WriteAsync(body, stop.Token);
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
