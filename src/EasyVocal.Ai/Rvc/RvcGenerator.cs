using EasyVocal.Ai.Runtime;
using Microsoft.ML.OnnxRuntime;

namespace EasyVocal.Ai.Rvc;

/// <summary>
/// An RVC v2 generator (with pitch). Two layouts:
/// <list type="bullet">
/// <item>EasyVocal's two graphs (a .pth through a template, or a pair imported as is): <c>enc</c> = the text encoder
/// over every context frame; <c>dec</c> = flow + NSF-HiFiGAN over the newest frames only, with the speaker vector
/// <c>g</c> as an input (so the base model's speakers, or blends of them, are voices with no extra weights).</item>
/// <item>One graph in RVC WebUI's "Export Onnx" layout (<c>phone, phone_lengths, pitch, pitchf, ds, rnd</c>) or
/// w-okada's (<c>feats, p_len, pitch, pitchf, sid</c>): it decodes all the frames it gets, so it costs more.</item>
/// </list>
/// The models are shared; <see cref="NewRun"/> makes a stream's buffers.
/// </summary>
public sealed class RvcGenerator : IDisposable
{
    public const int Inter = 192, Gin = 256;
    private readonly OnnxModel? enc, dec, single;
    private readonly SingleNames? names;

    public RvcGenerator(int sampleRate, OnnxModel enc, OnnxModel dec)
    {
        SampleRate = sampleRate;
        this.enc = enc;
        this.dec = dec;
    }

    public RvcGenerator(int sampleRate, OnnxModel single)
    {
        SampleRate = sampleRate;
        this.single = single;
        names = SingleNames.Of(single.InputNames) ?? throw new InvalidDataException(
            "This ONNX voice has inputs EasyVocal doesn't know (" + string.Join(", ", single.InputNames) + ").");
    }

    public int SampleRate { get; }
    public int Hop => SampleRate / 100;
    public bool IsSingleGraph => single != null;
    public string Provider => (dec ?? single)!.Provider;
    public bool OnGpu => (dec ?? single)!.OnGpu;

    /// <summary>Buffers for <paramref name="frames"/> context frames of which the last <paramref name="decoded"/> are turned into audio.</summary>
    public Run NewRun(int frames, int decoded, int seed = 1234) => new(this, frames, decoded, seed);

    public void Dispose()
    {
        enc?.Dispose();
        dec?.Dispose();
        single?.Dispose();
    }

    private sealed record SingleNames(string Phone, string Lengths, string Pitch, string PitchF, string Speaker, string? Noise)
    {
        public static SingleNames? Of(IReadOnlyList<string> inputs)
        {
            string? Pick(params string[] options) => options.FirstOrDefault(inputs.Contains);
            var phone = Pick("phone", "feats");
            var len = Pick("phone_lengths", "p_len");
            var pitch = Pick("pitch");
            var pf = Pick("pitchf", "nsff0");
            var sid = Pick("ds", "sid");
            if (phone == null || len == null || pitch == null || pf == null || sid == null) return null;
            return new SingleNames(phone, len, pitch, pf, sid, Pick("rnd"));
        }
    }

    /// <summary>One stream's tensors (fixed shapes: nothing is allocated per block for the two-graph layout).</summary>
    public sealed class Run : IDisposable
    {
        private readonly RvcGenerator g;
        private readonly Random rng;
        private readonly int frames, decoded, flowFrames;
        private readonly OrtValue[] encIn, encOut, decIn, decOut;
        private readonly OrtValue[]? singleIn;
        private readonly float[] mp, logsP, zp, f0Tail, noise;
        private readonly long[] lengths, speakerId = { 0 };
        private static readonly string[] EncIn = { "phone", "pitch", "lengths" }, EncOutNames = { "m_p", "logs_p" };
        private static readonly string[] DecIn = { "z_p", "nsff0", "g" }, DecOutNames = { "audio" };
        private readonly string[]? singleInNames, singleOutNames;

        /// <summary>[frames, 768] content features (write them before <see cref="Generate"/>).</summary>
        public float[] Phone { get; }
        /// <summary>[frames] coarse pitch (1..255).</summary>
        public long[] Coarse { get; }
        /// <summary>[frames] pitch in Hz (0 = unvoiced).</summary>
        public float[] F0 { get; }
        /// <summary>The speaker vector [256] (two-graph layout).</summary>
        public float[] Speaker { get; }
        /// <summary>The newest <c>decoded</c> frames as audio at the model's rate.</summary>
        public float[] Audio { get; }
        public float NoiseScale { get; set; } = 0.66666f;
        public int SpeakerId { get => (int)speakerId[0]; set => speakerId[0] = value; }

        internal Run(RvcGenerator g, int frames, int decoded, int seed)
        {
            if (decoded > frames || decoded <= 0) throw new ArgumentOutOfRangeException(nameof(decoded));
            this.g = g;
            this.frames = frames;
            this.decoded = decoded;
            rng = new Random(seed);
            Phone = new float[frames * ContentEncoder.Dim];
            Coarse = new long[frames];
            F0 = new float[frames];
            Speaker = new float[Gin];
            Audio = new float[decoded * g.Hop];
            lengths = new long[] { frames };
            flowFrames = Math.Min(frames, decoded + 24);
            if (g.single == null)
            {
                mp = new float[Inter * frames];
                logsP = new float[Inter * frames];
                zp = new float[Inter * flowFrames];
                f0Tail = new float[decoded];
                noise = Array.Empty<float>();
                encIn = new[]
                {
                    OrtValue.CreateTensorValueFromMemory(Phone, new long[] { 1, frames, ContentEncoder.Dim }),
                    OrtValue.CreateTensorValueFromMemory(Coarse, new long[] { 1, frames }),
                    OrtValue.CreateTensorValueFromMemory(lengths, new long[] { 1 }),
                };
                encOut = new[]
                {
                    OrtValue.CreateTensorValueFromMemory(mp, new long[] { 1, Inter, frames }),
                    OrtValue.CreateTensorValueFromMemory(logsP, new long[] { 1, Inter, frames }),
                };
                decIn = new[]
                {
                    OrtValue.CreateTensorValueFromMemory(zp, new long[] { 1, Inter, flowFrames }),
                    OrtValue.CreateTensorValueFromMemory(f0Tail, new long[] { 1, decoded }),
                    OrtValue.CreateTensorValueFromMemory(Speaker, new long[] { 1, Gin, 1 }),
                };
                decOut = new[] { OrtValue.CreateTensorValueFromMemory(Audio, new long[] { 1, 1, decoded * g.Hop }) };
            }
            else
            {
                var n = g.names!;
                mp = logsP = zp = f0Tail = Array.Empty<float>();
                encIn = encOut = decIn = decOut = Array.Empty<OrtValue>();
                noise = new float[Inter * frames];
                var ins = new List<OrtValue>
                {
                    OrtValue.CreateTensorValueFromMemory(Phone, new long[] { 1, frames, ContentEncoder.Dim }),
                    OrtValue.CreateTensorValueFromMemory(lengths, new long[] { 1 }),
                    OrtValue.CreateTensorValueFromMemory(Coarse, new long[] { 1, frames }),
                    OrtValue.CreateTensorValueFromMemory(F0, new long[] { 1, frames }),
                    OrtValue.CreateTensorValueFromMemory(speakerId, new long[] { 1 }),
                };
                var names = new List<string> { n.Phone, n.Lengths, n.Pitch, n.PitchF, n.Speaker };
                if (n.Noise != null)
                {
                    ins.Add(OrtValue.CreateTensorValueFromMemory(noise, new long[] { 1, Inter, frames }));
                    names.Add(n.Noise);
                }
                singleIn = ins.ToArray();
                singleInNames = names.ToArray();
                singleOutNames = new[] { g.single.OutputNames[0] };
            }
        }

        /// <summary>Phone, Coarse and F0 → <see cref="Audio"/> (the newest <c>decoded</c> frames).</summary>
        public void Generate()
        {
            if (g.single != null) { GenerateSingle(); return; }
            g.enc!.Run(EncIn, encIn, EncOutNames, encOut);
            int s0 = frames - flowFrames;
            float scale = NoiseScale;
            for (int c = 0; c < Inter; c++)
            {
                int src = c * frames + s0, dst = c * flowFrames;
                for (int i = 0; i < flowFrames; i++)
                    zp[dst + i] = mp[src + i] + MathF.Exp(logsP[src + i]) * Gauss() * scale;
            }
            Array.Copy(F0, frames - decoded, f0Tail, 0, decoded);
            g.dec!.Run(DecIn, decIn, DecOutNames, decOut);
        }

        private void GenerateSingle()
        {
            for (int i = 0; i < noise.Length; i++) noise[i] = Gauss() * NoiseScale;
            using var r = g.single!.Run(singleInNames!, singleIn!, singleOutNames!);
            var y = r.First().GetTensorDataAsSpan<float>();
            int want = Audio.Length;
            if (y.Length >= want) y.Slice(y.Length - want, want).CopyTo(Audio);
            else
            {
                Array.Clear(Audio);
                y.CopyTo(Audio.AsSpan(want - y.Length));
            }
        }

        private float Gauss()
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        public void Dispose()
        {
            foreach (var v in encIn) v.Dispose();
            foreach (var v in encOut) v.Dispose();
            foreach (var v in decIn) v.Dispose();
            foreach (var v in decOut) v.Dispose();
            if (singleIn != null) foreach (var v in singleIn) v.Dispose();
        }
    }
}
