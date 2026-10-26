using System.IO.Compression;
using System.Runtime.InteropServices;
using EasyVocal.Ai.Store;

/// <summary>A NuGet package the AI pack takes ONNX Runtime's native files from (SHA-256 of the .nupkg pinned).</summary>
internal sealed record NativePackage(string Id, string Version, string Sha256)
{
    public string FileName => $"{Id}.{Version}.nupkg";
    public Uri Url => new($"https://api.nuget.org/v3-flatcontainer/{Id}/{Version}/{FileName}");
}

/// <summary>
/// The release assets of the AI pack (tag <see cref="PackSource.Tag"/>): one zip per OS with the same engine models and
/// bundled voices and that OS's ONNX Runtime (DirectML on Windows), plus a <c>.sha256</c> next to each.
/// </summary>
internal static class PackRelease
{
    // ONNX Runtime 1.23.x: the last line whose CPU package still has osx-x64. DirectML's build is 1.23.0 (its last is 1.24).
    private static readonly NativePackage OrtCpu = new("microsoft.ml.onnxruntime", "1.23.2", "25fe172f7fcdf34f2b5b02c8b997f6b88abc282956849d4af4335af23d0c4e4a");
    private static readonly NativePackage OrtDml = new("microsoft.ml.onnxruntime.directml", "1.23.0", "a33ec2382b3c440bab74042a135733bb6e5085f293b908d3997688a58fe307e7");
    private static readonly NativePackage DirectML = new("microsoft.ai.directml", "1.15.4", "4e7cb7ddce8cf837a7a75dc029209b520ca0101470fcdf275c1f49736a3615b9");

    public static readonly string[] Rids = { "win-x64", "osx-arm64", "osx-x64", "linux-x64" };

    /// <summary>The release asset's name for a .NET runtime id ("osx-arm64" → EasyVocal-AI-macos-arm64.zip).</summary>
    public static string AssetName(string rid) => rid switch
    {
        "win-x64" => PackSource.AssetName("win", Architecture.X64)!,
        "osx-arm64" => PackSource.AssetName("macos", Architecture.Arm64)!,
        "osx-x64" => PackSource.AssetName("macos", Architecture.X64)!,
        "linux-x64" => PackSource.AssetName("linux", Architecture.X64)!,
        _ => throw new ArgumentException("unknown runtime id " + rid + " (" + string.Join(", ", Rids) + ")"),
    };

    /// <summary>
    /// What goes into <c>runtime/</c> for an OS: (path inside the package, name in the pack). Licences go into
    /// <c>runtime/licenses/</c>.
    /// </summary>
    private static IEnumerable<(NativePackage Package, string Entry, string Name)> RuntimeFiles(string rid)
    {
        if (rid == "win-x64")
        {
            yield return (OrtDml, "runtimes/win-x64/native/onnxruntime.dll", "onnxruntime.dll");
            yield return (OrtDml, "runtimes/win-x64/native/onnxruntime_providers_shared.dll", "onnxruntime_providers_shared.dll");
            yield return (OrtDml, "LICENSE", "licenses/ONNX-Runtime-LICENSE.txt");
            yield return (OrtDml, "ThirdPartyNotices.txt", "licenses/ONNX-Runtime-ThirdPartyNotices.txt");
            yield return (DirectML, "bin/x64-win/DirectML.dll", "DirectML.dll");
            yield return (DirectML, "LICENSE.txt", "licenses/DirectML-LICENSE.txt");
            yield return (DirectML, "ThirdPartyNotices.txt", "licenses/DirectML-ThirdPartyNotices.txt");
            yield break;
        }
        if (rid == "linux-x64")
        {
            yield return (OrtCpu, "runtimes/linux-x64/native/libonnxruntime.so", "libonnxruntime.so");
            yield return (OrtCpu, "runtimes/linux-x64/native/libonnxruntime_providers_shared.so", "libonnxruntime_providers_shared.so");
        }
        else yield return (OrtCpu, $"runtimes/{rid}/native/libonnxruntime.dylib", "libonnxruntime.dylib");
        yield return (OrtCpu, "LICENSE", "licenses/ONNX-Runtime-LICENSE.txt");
        yield return (OrtCpu, "ThirdPartyNotices.txt", "licenses/ONNX-Runtime-ThirdPartyNotices.txt");
    }

    /// <summary>A package from the local NuGet cache or nuget.org, checked against its pinned SHA-256.</summary>
    private static async Task<string> Fetch(NativePackage p, string cache)
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", p.Id, p.Version, p.FileName);
        if (File.Exists(local) && Downloader.Sha256Of(local) == p.Sha256) return local;
        var path = Path.Combine(cache, p.FileName);
        if (File.Exists(path) && Downloader.Sha256Of(path) == p.Sha256) return path;
        Console.WriteLine("  downloading " + p.Url);
        await Downloader.DownloadAsync(p.Url, path, 400L * 1048576, p.Sha256, null, CancellationToken.None);
        return path;
    }

    /// <summary>Writes <c>runtime/</c> for an OS into <paramref name="folder"/>.</summary>
    public static async Task WriteRuntime(string rid, string folder, string cache)
    {
        Directory.CreateDirectory(folder);
        foreach (var group in RuntimeFiles(rid).GroupBy(f => f.Package))
        {
            var nupkg = await Fetch(group.Key, cache);
            using var zip = ZipFile.OpenRead(nupkg);
            foreach (var (_, entry, name) in group)
            {
                var e = zip.GetEntry(entry) ?? throw new InvalidDataException($"{group.Key.FileName} has no {entry}");
                var target = Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, overwrite: true);
            }
        }
        File.WriteAllText(Path.Combine(folder, "licenses", "README.txt"),
            $"ONNX Runtime {(rid == "win-x64" ? OrtDml.Version + " with DirectML " + DirectML.Version : OrtCpu.Version)} (Microsoft), the native library EasyVocal's AI voices run on.\n" +
            "ONNX Runtime: MIT licence (ONNX-Runtime-LICENSE.txt), with the notices of what it includes (ONNX-Runtime-ThirdPartyNotices.txt).\n" +
            (rid == "win-x64" ? "DirectML.dll: Microsoft's DirectML redistributable, under its own licence terms (DirectML-LICENSE.txt, DirectML-ThirdPartyNotices.txt).\n" : ""));
    }

    /// <summary>
    /// Makes <c>EasyVocal-AI-&lt;os&gt;.zip</c> (+ <c>.sha256</c>) in <paramref name="outFolder"/> from a built pack
    /// (<paramref name="packRoot"/> = <c>&lt;data&gt;/AI</c> with engine/ and voices/) and this OS's runtime.
    /// </summary>
    public static async Task<string> Make(string packRoot, string rid, string outFolder, string cache)
    {
        string engine = Path.Combine(packRoot, "engine"), voices = Path.Combine(packRoot, "voices");
        foreach (var need in new[] { AiPack.ContentFp32, AiPack.ContentInt8, AiPack.RmvpeF16, BaseModels.BaseGeneratorId + ".pth" })
            if (!File.Exists(Path.Combine(engine, need))) throw new InvalidOperationException("the pack has no engine/" + need + " (build it with: build --data <dir> --int8 <hubert_int8.onnx>)");
        var recipe = BundledRecipe.Embedded();
        foreach (var v in recipe.Voices)
            if (!File.Exists(Path.Combine(voices, v.Id, VoiceManifest.FileName))) throw new InvalidOperationException("the pack has no bundled voice " + v.Id);

        var runtime = Path.Combine(cache, "runtime-" + rid);
        if (Directory.Exists(runtime)) Directory.Delete(runtime, true);
        await WriteRuntime(rid, runtime, cache);

        Directory.CreateDirectory(outFolder);
        var asset = AssetName(rid);
        var zipPath = Path.Combine(outFolder, asset);
        var tmp = zipPath + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            void Add(string file, string name) => zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
            foreach (var f in Directory.EnumerateFiles(engine, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                if (!f.EndsWith(".tmp", StringComparison.Ordinal)) Add(f, "engine/" + Rel(engine, f));
            zip.CreateEntry("engine/NOTICES.txt").Open().Using(s => s.Write(System.Text.Encoding.UTF8.GetBytes(EngineNotices(recipe))));
            foreach (var v in recipe.Voices) // only the bundled voices (never anything imported into the build folder)
                foreach (var f in Directory.EnumerateFiles(Path.Combine(voices, v.Id)).Order(StringComparer.Ordinal))
                    Add(f, $"voices/{v.Id}/{Path.GetFileName(f)}");
            foreach (var f in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                Add(f, "runtime/" + Rel(runtime, f));
        }
        File.Move(tmp, zipPath, overwrite: true);
        var sha = Downloader.Sha256Of(zipPath);
        File.WriteAllText(zipPath + ".sha256", sha + "  " + asset + "\n");
        return zipPath;
    }

    private static string Rel(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static void Using(this Stream s, Action<Stream> write)
    {
        using (s) write(s);
    }

    private static string EngineNotices(BundledRecipe recipe) =>
        "EasyVocal AI pack (" + PackSource.Tag + ")\n\n" +
        "engine/hubert.onnx, engine/hubert_int8.onnx: the content encoder, HuBERT base as RVC uses it (RVC-Project,\n" +
        "  lj1995/VoiceConversionWebUI, MIT; HuBERT: fairseq, MIT, trained on LibriSpeech, CC BY 4.0). The fp32 model is built\n" +
        "  from the published weights; the int8 one is the same model quantised (dynamic int8).\n" +
        "engine/rmvpe_f16.onnx: the RMVPE pitch model (lj1995/VoiceConversionWebUI rmvpe.onnx, MIT; RMVPE: Apache-2.0), its\n" +
        "  weights stored as fp16 (EasyVocal turns them back into fp32 when it loads the model).\n" +
        "engine/" + BaseModels.BaseGeneratorId + ".pth: RVC v2 40k pretrained generator f0G40k.pth (RVC-Project, MIT), trained on the\n" +
        "  CSTR VCTK Corpus (CC BY 4.0).\n" +
        "voices/: " + recipe.Source + ".\n  " + recipe.Licence + "\n  " + recipe.Attribution + "\n" +
        "runtime/: ONNX Runtime (Microsoft, MIT) and, on Windows, DirectML (Microsoft's redistributable licence): see runtime/licenses.\n";

    /// <summary>
    /// The real install from GitHub (what the app does): the release looked up, this OS's asset downloaded, checked
    /// against the release's SHA-256 and installed into <c>&lt;data&gt;/AI</c>. <paramref name="keepFolder"/> = also keep the
    /// checked zip in that folder under its asset name, with its .sha256 (the packaged e2e run serves it).
    /// </summary>
    public static async Task Install(string data, string? asset, string? keepFolder)
    {
        var pack = new AiPack(data);
        var installer = new PackInstaller(pack, assetName: asset);
        var offer = await installer.FindAsync();
        Console.WriteLine($"{offer.AssetName}: {offer.Size / 1048576.0:0.0} MB, sha256 {offer.Sha256} ({offer.Url})");
        long last = 0;
        var progress = new Prog<PackProgress>(p =>
        {
            if (p.Stage == PackStage.Downloading && p.Done - last < 100L * 1048576 && p.Done != p.Total) return;
            last = p.Done;
            Console.WriteLine($"  {p.Stage}: {p.Done / 1048576} / {(p.Total ?? 0) / 1048576} MB");
        });
        if (keepFolder == null)
        {
            await installer.InstallAsync(offer, progress);
        }
        else
        {
            Directory.CreateDirectory(keepFolder);
            var keep = Path.Combine(keepFolder, offer.AssetName);
            await Downloader.DownloadResumableAsync(offer.Url, keep, offer.Size, offer.Sha256,
                new Prog<DownloadProgress>(d => progress.Report(new PackProgress(PackStage.Downloading, d.Done, d.Total ?? offer.Size))), CancellationToken.None);
            installer.Install(keep, offer, progress, CancellationToken.None);
            File.WriteAllText(keep + ".sha256", offer.Sha256 + "  " + offer.AssetName + "\n");
        }
        var lib = new VoiceLibrary(pack);
        Console.WriteLine($"installed into {pack.Root}: {lib.All().Count} voices, runtime {string.Join(", ", Directory.EnumerateFiles(pack.RuntimeFolder).Select(Path.GetFileName))}");
    }
}

/// <summary>Reports on the caller's thread (console output in order).</summary>
internal sealed class Prog<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
