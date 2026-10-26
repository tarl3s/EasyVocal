using System.Reflection;
using System.Runtime.InteropServices;
using EasyVocal.Core.Logging;
using Microsoft.ML.OnnxRuntime;

namespace EasyVocal.Ai.Runtime;

/// <summary>Where the models run: "auto" = a GPU when one works here, else the processor.</summary>
public enum AiDevice { Auto, Cpu, Gpu }

/// <summary>
/// ONNX Runtime's native library and sessions. The native library is looked for in the AI pack's runtime folder
/// first (<see cref="UseNativeFolder"/>), then where .NET normally finds it (the tests and Debug builds carry it).
/// Sessions try this system's GPU provider (DirectML on Windows, CoreML on macOS, CUDA on Linux) and fall back to
/// the CPU when it is missing or refuses the model; every choice is logged.
/// </summary>
public static class OrtRuntime
{
    private static readonly object Gate = new();
    private static string? nativeFolder;
    private static bool resolverSet;
    private static string? loadError;
    private static bool loaded;

    /// <summary>Looks for the native library in this folder first (call before anything else touches ONNX Runtime).</summary>
    public static void UseNativeFolder(string? folder)
    {
        lock (Gate)
        {
            nativeFolder = folder;
            if (resolverSet) return;
            resolverSet = true;
            try { NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, Resolve); }
            catch (InvalidOperationException) { /* someone set one already: .NET's own probing stays */ }
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        string? dir = nativeFolder;
        if (dir == null || !name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        foreach (var file in new[] { "onnxruntime.dll", "libonnxruntime.dylib", "libonnxruntime.so" })
        {
            var full = Path.Combine(dir, file);
            if (File.Exists(full) && NativeLibrary.TryLoad(full, out var h)) return h;
        }
        return IntPtr.Zero;
    }

    /// <summary>Forget a failed load (a runtime folder was just installed): the next <see cref="TryLoad"/> tries again.</summary>
    public static void ProbeAgain()
    {
        lock (Gate) if (!loaded) loadError = null;
    }

    /// <summary>True when ONNX Runtime's native library loads here; else <paramref name="error"/> says why.</summary>
    public static bool TryLoad(out string? error)
    {
        lock (Gate)
        {
            if (!loaded && loadError == null)
            {
                try
                {
                    var v = OrtEnv.Instance().GetVersionString();
                    AppLog.Info("AI: ONNX Runtime " + v + " (" + string.Join(", ", OrtEnv.Instance().GetAvailableProviders()) + ")");
                    loaded = true;
                }
                catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException or OnnxRuntimeException)
                {
                    loadError = "The AI runtime (ONNX Runtime) couldn't be loaded: " + (ex.InnerException ?? ex).Message;
                }
            }
            error = loadError;
            return loaded;
        }
    }

    /// <summary>The providers this native library has ("CPUExecutionProvider", "DmlExecutionProvider", …).</summary>
    public static IReadOnlyList<string> Providers()
    {
        try { return OrtEnv.Instance().GetAvailableProviders(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>This system's GPU provider name, or null when the library has none ("DirectML", "CoreML", "CUDA").</summary>
    public static string? GpuProvider()
    {
        var p = Providers();
        if (OperatingSystem.IsWindows() && p.Contains("DmlExecutionProvider")) return "DirectML";
        if (OperatingSystem.IsMacOS() && p.Contains("CoreMLExecutionProvider")) return "CoreML";
        if (OperatingSystem.IsLinux() && p.Contains("CUDAExecutionProvider")) return "CUDA";
        return null;
    }

    /// <summary>Physical cores (logical ÷ 2 when there are at least 4: SMT), at most 6, at least 1.</summary>
    public static int DefaultThreads
    {
        get
        {
            int logical = Environment.ProcessorCount;
            int physical = logical >= 4 ? logical / 2 : logical;
            return Math.Clamp(physical, 1, 6);
        }
    }

    /// <summary>
    /// A session for <paramref name="model"/> (ONNX bytes) on the wanted device. Gpu/Auto try the GPU provider and fall
    /// back to the CPU when it can't be added or refuses the model. Spinning is off: idle thread pools must not burn the CPU.
    /// </summary>
    public static OnnxModel Create(string what, byte[] model, AiDevice device, int threads) =>
        Create(what, o => new InferenceSession(model, o), device, threads);

    /// <summary>The same for a model file on disk (gunzipped first when it is <c>.gz</c>).</summary>
    public static OnnxModel CreateFromFile(string what, string path, AiDevice device, int threads) =>
        path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? Create(what, Import.OnnxRewriter.ReadModel(path), device, threads)
            : Create(what, o => new InferenceSession(path, o), device, threads);

    private static OnnxModel Create(string what, Func<SessionOptions, InferenceSession> open, AiDevice device, int threads)
    {
        if (!TryLoad(out var error)) throw new InvalidOperationException(error);
        string? gpu = device == AiDevice.Cpu ? null : GpuProvider();
        if (gpu != null)
        {
            SessionOptions? o = null;
            try
            {
                o = Options(threads, gpu);
                var s = open(o);
                AppLog.Info($"AI: {what} runs on {gpu}");
                return new OnnxModel(what, s, o, gpu);
            }
            catch (Exception ex)
            {
                o?.Dispose();
                AppLog.Info($"AI: {what} can't use {gpu} ({First(ex.Message)}); using the CPU");
            }
        }
        var co = Options(threads, null);
        try
        {
            var cs = open(co);
            AppLog.Info($"AI: {what} runs on the CPU ({threads} threads)");
            return new OnnxModel(what, cs, co, "CPU");
        }
        catch
        {
            co.Dispose();
            throw;
        }
    }

    private static string First(string s)
    {
        int nl = s.IndexOf('\n');
        return nl > 0 ? s[..nl].Trim() : s.Trim();
    }

    private static SessionOptions Options(int threads, string? gpu)
    {
        var o = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, threads),
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        o.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        o.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        switch (gpu)
        {
            case "DirectML":
                o.EnableMemoryPattern = false;
                o.AppendExecutionProvider_DML(0);
                break;
            case "CoreML":
                o.AppendExecutionProvider_CoreML(CoreMLFlags.COREML_FLAG_USE_NONE);
                break;
            case "CUDA":
                o.AppendExecutionProvider_CUDA(0);
                break;
        }
        return o;
    }
}

/// <summary>
/// One loaded model. <see cref="Run"/> may be called from several workers (the voice and the sounds through it):
/// runs are serialised per model (DirectML doesn't take concurrent runs on one session).
/// </summary>
public sealed class OnnxModel : IDisposable
{
    private readonly InferenceSession session;
    private readonly SessionOptions options;
    private readonly RunOptions run = new();
    private readonly object gate = new();
    private bool disposed;

    internal OnnxModel(string what, InferenceSession session, SessionOptions options, string provider)
    {
        What = what;
        this.session = session;
        this.options = options;
        Provider = provider;
    }

    public string What { get; }

    /// <summary>"CPU", "DirectML", "CoreML" or "CUDA".</summary>
    public string Provider { get; }

    public bool OnGpu => Provider != "CPU";

    public IReadOnlyList<string> InputNames => session.InputNames;

    public IReadOnlyList<string> OutputNames => session.OutputNames;

    public IReadOnlyDictionary<string, NodeMetadata> InputMetadata => session.InputMetadata;

    /// <summary>Runs with caller-owned inputs and preallocated outputs (shapes fixed by the caller: no allocation for them).</summary>
    public void Run(string[] inputNames, OrtValue[] inputs, string[] outputNames, OrtValue[] outputs)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            session.Run(run, inputNames, inputs, outputNames, outputs);
        }
    }

    /// <summary>Runs and lets ONNX Runtime allocate the outputs (shapes not known in advance).</summary>
    public IDisposableReadOnlyCollection<OrtValue> Run(string[] inputNames, OrtValue[] inputs, string[] outputNames)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return session.Run(run, inputNames, inputs, outputNames);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            session.Dispose();
            options.Dispose();
            run.Dispose();
        }
    }
}
