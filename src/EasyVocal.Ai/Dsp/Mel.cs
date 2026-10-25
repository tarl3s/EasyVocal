namespace EasyVocal.Ai.Dsp;

/// <summary>
/// RMVPE's input: the log-mel spectrogram of 16 kHz audio exactly as RVC computes it (torch.stft n_fft 1024, hop
/// 160, periodic Hann, centred with reflect padding; librosa mel filters, 128 bands 30-8000 Hz, HTK scale, Slaney
/// norm; log(max(mel, 1e-5))). Layout [128, frames], frames = 1 + length/160. No allocation per call.
/// </summary>
public sealed class Mel
{
    public const int NFft = 1024, Hop = 160, NMels = 128, Bins = NFft / 2 + 1;
    private readonly float[] window = new float[NFft];
    private readonly float[] fb = new float[NMels * Bins];
    private readonly int[] lo = new int[NMels], hi = new int[NMels];
    private readonly Fft fft = new(NFft);
    private readonly float[] re = new float[NFft], im = new float[NFft], mag = new float[Bins];

    public Mel()
    {
        for (int i = 0; i < NFft; i++) window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / NFft));
        static double HzToMel(double f) => 2595.0 * Math.Log10(1 + f / 700.0);
        static double MelToHz(double m) => 700.0 * (Math.Pow(10, m / 2595.0) - 1);
        double m0 = HzToMel(30), m1 = HzToMel(8000);
        var hz = new double[NMels + 2];
        for (int i = 0; i < hz.Length; i++) hz[i] = MelToHz(m0 + (m1 - m0) * i / (NMels + 1));
        for (int m = 0; m < NMels; m++)
        {
            double enorm = 2.0 / (hz[m + 2] - hz[m]);
            lo[m] = Bins;
            hi[m] = 0;
            for (int k = 0; k < Bins; k++)
            {
                double f = k * 16000.0 / NFft;
                double lower = (f - hz[m]) / (hz[m + 1] - hz[m]);
                double upper = (hz[m + 2] - f) / (hz[m + 2] - hz[m + 1]);
                double w = Math.Max(0, Math.Min(lower, upper)) * enorm;
                fb[m * Bins + k] = (float)w;
                if (w > 0) { lo[m] = Math.Min(lo[m], k); hi[m] = Math.Max(hi[m], k + 1); }
            }
        }
    }

    public static int Frames(int samples) => 1 + samples / Hop;

    /// <summary>Writes [128, stride] (frame t of band m at m·stride + t) for the first <see cref="Frames"/> frames.</summary>
    public void Compute(ReadOnlySpan<float> x, Span<float> output, int stride)
    {
        int pad = NFft / 2, n = x.Length;
        int frames = Frames(n);
        for (int t = 0; t < frames; t++)
        {
            int start = t * Hop - pad;
            for (int i = 0; i < NFft; i++)
            {
                int j = start + i;
                if (j < 0) j = -j;
                else if (j >= n) j = 2 * (n - 1) - j;   // reflect
                j = Math.Clamp(j, 0, n - 1);
                re[i] = x[j] * window[i];
                im[i] = 0;
            }
            fft.Forward(re, im);
            for (int k = 0; k < Bins; k++) mag[k] = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
            for (int m = 0; m < NMels; m++)
            {
                float s = 0;
                int b = m * Bins;
                for (int k = lo[m]; k < hi[m]; k++) s += fb[b + k] * mag[k];
                output[m * stride + t] = MathF.Log(MathF.Max(s, 1e-5f));
            }
        }
    }
}

/// <summary>In-place radix-2 complex FFT.</summary>
public sealed class Fft
{
    private readonly int n;
    private readonly float[] cos, sin;
    private readonly int[] rev;

    public Fft(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(n));
        this.n = n;
        cos = new float[n / 2];
        sin = new float[n / 2];
        rev = new int[n];
        for (int i = 0; i < n / 2; i++) { cos[i] = (float)Math.Cos(-2 * Math.PI * i / n); sin[i] = (float)Math.Sin(-2 * Math.PI * i / n); }
        int bits = (int)Math.Log2(n);
        for (int i = 0; i < n; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            rev[i] = r;
        }
    }

    public void Forward(float[] re, float[] im)
    {
        for (int i = 0; i < n; i++)
        {
            int j = rev[i];
            if (j > i) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size / 2, step = n / size;
            for (int s = 0; s < n; s += size)
                for (int k = 0; k < half; k++)
                {
                    float wr = cos[k * step], wi = sin[k * step];
                    int a = s + k, b = a + half;
                    float tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
        }
    }
}
