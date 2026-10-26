using EasyVocal.Ai.Runtime;
using Microsoft.ML.OnnxRuntime;

namespace EasyVocal.Ai.Rvc;

/// <summary>
/// HuBERT base (RVC v2's content features: 768 values per 20 ms, layer 12) on 16 kHz audio, returned at RVC's
/// 100 frames/s ([frames, 768], frames = length/160: each HuBERT frame twice, the last one repeated as RVC does).
/// One instance per stream when the length is fixed (its buffers are reused); the model is shared.
/// </summary>
public sealed class ContentEncoder : IDisposable
{
    public const int Dim = 768;
    private readonly OnnxModel model;
    private readonly string[] inNames = { "source" }, outNames;
    private int cachedSamples = -1;
    private float[] input = Array.Empty<float>(), feats = Array.Empty<float>();
    private OrtValue? inVal, outVal;
    private readonly OrtValue[] ins = new OrtValue[1], outs = new OrtValue[1]; // reused: no allocation per block

    public ContentEncoder(OnnxModel model)
    {
        this.model = model;
        outNames = new[] { model.OutputNames[0] };
    }

    /// <summary>The optional .index blend (applied to HuBERT's own frames from <c>blendFrom</c> on).</summary>
    public FeatureIndex? Index { get; set; }
    public float IndexRate { get; set; }

    /// <summary>HuBERT's frame count for a number of 16 kHz samples (its convolution stack).</summary>
    public static int HubertFrames(int samples)
    {
        int l = (samples - 10) / 5 + 1;
        for (int i = 0; i < 4; i++) l = (l - 3) / 2 + 1;
        for (int i = 0; i < 2; i++) l = (l - 2) / 2 + 1;
        return Math.Max(0, l);
    }

    /// <summary>
    /// Features of <paramref name="x16"/> at 100 frames/s into <paramref name="output"/> ([length/160, 768]).
    /// <paramref name="blendFrom"/>: the first 100 fps frame the .index blend touches (the newest part only).
    /// </summary>
    public void Run(ReadOnlySpan<float> x16, Span<float> output, int blendFrom = 0)
    {
        int s = x16.Length;
        int f = HubertFrames(s);
        if (f <= 0) throw new ArgumentException("Too little audio for the content encoder.");
        if (s != cachedSamples)
        {
            inVal?.Dispose();
            outVal?.Dispose();
            input = new float[s];
            feats = new float[f * Dim];
            inVal = OrtValue.CreateTensorValueFromMemory(input, new long[] { 1, s });
            outVal = OrtValue.CreateTensorValueFromMemory(feats, new long[] { 1, f, Dim });
            cachedSamples = s;
        }
        x16.CopyTo(input);
        ins[0] = inVal!;
        outs[0] = outVal!;
        model.Run(inNames, ins, outNames, outs);
        if (Index != null && IndexRate > 0) Index.Blend(feats, Math.Min(f, blendFrom / 2), f, IndexRate);
        int frames = s / 160;
        for (int i = 0; i < frames; i++)
        {
            int j = Math.Min(i / 2, f - 1);
            feats.AsSpan(j * Dim, Dim).CopyTo(output.Slice(i * Dim, Dim));
        }
    }

    public void Dispose()
    {
        inVal?.Dispose();
        outVal?.Dispose();
        inVal = outVal = null;
    }
}
