using System;
using System.Threading;

namespace EasyVocal.Core.Dsp;

/// <summary>
/// "Deeper ↔ Higher" (pitch) and "Voice body" (formants) in one TD-PSOLA stage.
/// <para>
/// Analysis: every 10 ms hop a YIN pitch tracker (70–600 Hz, on a 12 kHz low-passed copy, refined at the full
/// rate) decides voiced/unvoiced. Voiced stretches get one pitch mark per period, on the waveform peak; unvoiced
/// stretches get plain place-holder marks.
/// </para>
/// <para>
/// Synthesis: two-period Hann grains are cut around the mark nearest each synthesis point, resampled by
/// <see cref="FormantFactor"/> (the formants move, the spacing — the pitch — does not) and overlap-added
/// <c>period / pitchRatio</c> apart; <see cref="Monotone"/> and <see cref="Wobble"/> change that ratio grain by
/// grain. Unvoiced sound ("s", "f") uses 10 ms grains at a fixed 10 ms spacing that play the input on at the
/// formant speed and splice back now and then (WSOLA-style), so noise never picks up a tone (or is left out with
/// <see cref="VoicedOnly"/>).
/// </para>
/// With no pitch, body, wobble or monotone the clean input is heard, only delayed (bit-exact); going in or out of
/// that state crossfades over 20 ms. Every buffer is allocated in the constructor; <see cref="Process"/> never
/// allocates or locks and reads the parameters once per call.
/// </summary>
public sealed class VoiceShifter : IAudioEffect
{
    public const double MinSemitones = -12, MaxSemitones = 12;
    public const double MinFormant = 0.75, MaxFormant = 1.33;

    // Tracker tuning (YIN cumulative-mean-normalised difference).
    private const double YinThreshold = 0.15;   // the first dip below this is the period
    private const double VoicedEnter = 0.25;    // aperiodicity needed to start a voiced stretch...
    private const double VoicedStay = 0.35;     // ...and to stay in one (hysteresis)
    private const double SilenceRms = 5e-4;     // about -66 dBFS: quieter windows are never voiced
    private const double MarkSearch = 0.2;      // a mark may sit ±20 % of a period from where it is expected

    private readonly int latency;
    private readonly int hop;            // tracker hop (10 ms)
    private readonly int grainMax;       // longest voiced half-grain (10 ms); longer periods get a shorter window
    private readonly int lookAhead;      // how far past a synthesis point its analysis mark may be
    private readonly int uvHalf;         // unvoiced half-grain = unvoiced grain spacing (10 ms)
    private readonly int uvMarkStep;     // place-holder mark spacing in unvoiced stretches
    private readonly int uvLag;          // unvoiced marking stays this far behind the newest sample
    private readonly int uvJump;         // how far unvoiced reading may drift behind "now" before it splices
    private readonly int spliceSearch;   // ± search for the best-matching splice point
    private readonly int minPeriod, maxPeriod;
    private readonly int fade;           // dry/wet crossfade length

    // Input history (also the dry delay line) and the overlap-add output, both indexed by absolute input time.
    private readonly float[] input;
    private readonly float[] output;
    private readonly int ringMask;
    private long written;                // input samples received so far
    private long readPos;                // next output sample to hand out (= written - latency between calls)

    // Pitch tracker on a decimated, low-passed copy.
    private readonly int dec;
    private readonly Biquad lp1, lp2;
    private readonly float[] decRing;
    private readonly int decMask;
    private long decCount;
    private int decPhase;
    private readonly int decMinLag, decMaxLag, decWin;
    private readonly double[] yinBuf, yinCmnd;
    private readonly float[] fullBuf;
    private readonly double[] fullDiff;
    private readonly int fullWin;
    private bool trackVoiced;
    private double trackPeriod;

    // Analysis marks, sorted by position: voiced ones carry their period, unvoiced ones are place-holders.
    private const int MarkCap = 512;
    private readonly long[] markPos = new long[MarkCap];
    private readonly float[] markPer = new float[MarkCap];
    private readonly bool[] markVoiced = new bool[MarkCap];
    private readonly long[] backfill = new long[16];
    private long markCount;
    private long frontier;               // no mark will ever be added at or before this position
    private long lastMark;               // phase reference of the current voiced stretch
    private bool voicedRun;
    private long uvNext;

    // Synthesis.
    private double synthPos;             // centre of the next grain, in input time
    private long cursor;                 // index of the mark nearest synthPos
    private double uvSrc;                // where the last unvoiced grain read (its centre, input time)
    private int uvRun;                   // unvoiced grains in a row so far
    private double wet;                  // 0 = clean delayed input, 1 = shifted

    private double pitchSemitones;
    private double formantFactor = 1;
    private double wobble, monotone;
    private volatile bool voicedOnly;

    // Wobble and Monotone (decided per voiced grain).
    private readonly int sampleRate;
    private readonly double lowestSemitones;
    private double usualLogHz;           // the speaker's usual pitch (log Hz), followed slowly; 0 = none yet
    private double wobblePhase;
    private double wobbleAt;             // synthPos when the wobble phase last moved on

    /// <param name="lowestSemitones">How deep <see cref="PitchSemitones"/> may go (a voice's octave layer goes below -12).</param>
    public VoiceShifter(int sampleRate, double lowestSemitones = MinSemitones)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        this.sampleRate = sampleRate;
        this.lowestSemitones = Math.Min(MinSemitones, lowestSemitones);
        hop = Math.Max(1, sampleRate / 100);
        grainMax = DspUtil.Samples(sampleRate, 0.010);
        uvHalf = DspUtil.Samples(sampleRate, 0.010);
        uvMarkStep = Math.Max(1, uvHalf / 2);
        lookAhead = DspUtil.Samples(sampleRate, 0.0048);
        minPeriod = Math.Max(2, sampleRate / 600);
        maxPeriod = (int)Math.Ceiling(sampleRate / 70.0);
        uvLag = (int)Math.Round(0.4 * maxPeriod);
        uvJump = uvHalf;
        spliceSearch = DspUtil.Samples(sampleRate, 0.0025);
        fade = DspUtil.Samples(sampleRate, 0.020);

        // A grain centred at s is due once s - halfOut reaches the output. It needs its mark (at most lookAhead
        // after s) and the input up to that mark + halfIn (+3 for interpolation); halfIn and halfOut are at most
        // grainMax. Unvoiced grains (mark = s) need uvHalf + uvHalf * MaxFormant. Marks trail the newest
        // sample by at most uvLag (0.4 of the longest period).
        int voicedNeed = 2 * grainMax + lookAhead + 4;
        int unvoicedNeed = uvHalf + (int)Math.Ceiling(uvHalf * MaxFormant) + 4;
        int markNeed = grainMax + lookAhead + uvLag + 4;
        latency = Math.Max(voicedNeed, Math.Max(unvoicedNeed, markNeed)) + DspUtil.Samples(sampleRate, 0.0001);

        int ring = 1;
        while (ring < 4 * (latency + 2 * maxPeriod + hop)) ring <<= 1;
        input = new float[ring];
        output = new float[ring];
        ringMask = ring - 1;

        dec = Math.Max(1, (int)Math.Round(sampleRate / 12000.0));
        double decRate = (double)sampleRate / dec;
        lp1 = Biquad.LowPass(sampleRate, 2000);
        lp2 = Biquad.LowPass(sampleRate, 2000);
        decMinLag = Math.Max(2, (int)Math.Floor(decRate / 600));
        decMaxLag = (int)Math.Ceiling(decRate / 70);
        decWin = decMaxLag;
        int decRingSize = 1;
        while (decRingSize < 2 * (decWin + decMaxLag + 2)) decRingSize <<= 1;
        decRing = new float[decRingSize];
        decMask = decRingSize - 1;
        yinBuf = new double[decWin + decMaxLag + 2];
        yinCmnd = new double[decMaxLag + 2];
        fullWin = maxPeriod;
        fullBuf = new float[fullWin + maxPeriod + 2 * dec + 8];
        fullDiff = new double[2 * dec + 8];

        Reset();
    }

    /// <summary>Semitones, -12..12 (0 = unchanged).</summary>
    public double PitchSemitones
    {
        get => Volatile.Read(ref pitchSemitones);
        set => Volatile.Write(ref pitchSemitones, double.IsNaN(value) ? 0 : Math.Clamp(value, lowestSemitones, MaxSemitones));
    }

    /// <summary>0..1: the pitch sways up and down (up to ±1.2 semitones, 4–7 times a second), like a wavering ghost or alien.</summary>
    public double Wobble
    {
        get => Volatile.Read(ref wobble);
        set => Volatile.Write(ref wobble, DspUtil.Clamp01(value));
    }

    /// <summary>0..1: the melody of the voice is flattened towards the speaker's usual pitch (1 = one robotic note).</summary>
    public double Monotone
    {
        get => Volatile.Read(ref monotone);
        set => Volatile.Write(ref monotone, DspUtil.Clamp01(value));
    }

    /// <summary>Only voiced sound comes out ("s", "f" and the like are left out): for a layer added on top of another voice.</summary>
    public bool VoicedOnly { get => voicedOnly; set => voicedOnly = value; }

    /// <summary>Formant scale, 0.75..1.33 (1 = unchanged; above 1 = smaller, brighter voice body).</summary>
    public double FormantFactor
    {
        get => Volatile.Read(ref formantFactor);
        set => Volatile.Write(ref formantFactor, double.IsNaN(value) ? 1 : Math.Clamp(value, MinFormant, MaxFormant));
    }

    /// <summary>Fixed delay from input to output, whatever the parameters (25 ms at 48 kHz).</summary>
    public int LatencySamples => latency;

    public void Process(Span<float> mono)
    {
        double semis = PitchSemitones, formant = FormantFactor, wob = Wobble, flat = Monotone;
        double ratio = Math.Pow(2, semis / 12);
        double wetTarget = IsNeutral(semis, formant, wob, flat) ? 0 : 1;
        double wetStep = 1.0 / fade;

        int i = 0;
        while (i < mono.Length)
        {
            // Chunks never cross a tracker hop (so never exceed one), whatever block size the caller uses.
            int len = Math.Min(mono.Length - i, hop - (int)(written % hop));
            Span<float> chunk = mono.Slice(i, len);
            for (int k = 0; k < len; k++) Write(chunk[k]);
            if (written % hop == 0) Track();
            Mark();
            Synthesize(ratio, formant, wob, flat, readPos + len - 1);

            for (int k = 0; k < len; k++)
            {
                int idx = (int)(readPos & ringMask);
                readPos++;
                float dry = input[idx];
                float shifted = output[idx];
                output[idx] = 0;
                if (wet != wetTarget) wet = wet < wetTarget ? Math.Min(wetTarget, wet + wetStep) : Math.Max(wetTarget, wet - wetStep);
                chunk[k] = wet == 0 ? dry : wet == 1 ? shifted : (float)(dry + (shifted - dry) * wet);
            }
            i += len;
        }
    }

    public void Reset()
    {
        Array.Clear(input);
        Array.Clear(output);
        Array.Clear(decRing);
        lp1.Reset();
        lp2.Reset();
        written = 0;
        readPos = -latency;
        decCount = 0;
        decPhase = 0;
        trackVoiced = false;
        trackPeriod = 0;
        markCount = 0;
        frontier = -1;
        lastMark = 0;
        voicedRun = false;
        uvNext = 0;
        synthPos = 0;
        cursor = 0;
        uvSrc = 0;
        uvRun = 0;
        usualLogHz = 0;
        wobblePhase = 0;
        wobbleAt = 0;
        wet = IsNeutral(PitchSemitones, FormantFactor, Wobble, Monotone) ? 0 : 1;
    }

    private static bool IsNeutral(double semis, double formant, double wobble, double monotone) =>
        Math.Abs(semis) < 1e-3 && Math.Abs(formant - 1) < 1e-4 && wobble <= 0 && monotone <= 0;

    // ---------------------------------------------------------------- input

    private void Write(float x)
    {
        if (!float.IsFinite(x)) x = 0;
        input[(int)(written & ringMask)] = x;
        written++;
        float lp = lp2.Process(lp1.Process(x));
        if (++decPhase == dec)
        {
            decPhase = 0;
            decRing[(int)(decCount & decMask)] = lp;
            decCount++;
        }
    }

    private float At(long n) => input[(int)(n & ringMask)];

    /// <summary>Input at a fractional time (4-point cubic Hermite).</summary>
    private float Interp(double pos)
    {
        double fl = Math.Floor(pos);
        long n = (long)fl;
        float t = (float)(pos - fl);
        float xm1 = At(n - 1), x0 = At(n), x1 = At(n + 1), x2 = At(n + 2);
        float c1 = 0.5f * (x1 - xm1);
        float c2 = xm1 - 2.5f * x0 + 2f * x1 - 0.5f * x2;
        float c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
        return ((c3 * t + c2) * t + c1) * t + x0;
    }

    // ---------------------------------------------------------------- pitch tracker

    /// <summary>YIN over the newest decWin + decMaxLag decimated samples (about 29 ms at 48 kHz).</summary>
    private void Track()
    {
        int span = decWin + decMaxLag;
        if (decCount < span) { trackVoiced = false; return; }
        long s0 = decCount - span;
        double energy = 0;
        for (int j = 0; j < span; j++)
        {
            double v = decRing[(int)((s0 + j) & decMask)];
            yinBuf[j] = v;
            energy += v * v;
        }
        if (energy / span < SilenceRms * SilenceRms) { trackVoiced = false; return; }

        double cum = 0;
        yinCmnd[0] = 1;
        for (int tau = 1; tau <= decMaxLag; tau++)
        {
            double d = 0;
            for (int j = 0; j < decWin; j++)
            {
                double e = yinBuf[j] - yinBuf[j + tau];
                d += e * e;
            }
            cum += d;
            yinCmnd[tau] = cum > 1e-20 ? d * tau / cum : 1;
        }

        int best = -1;
        for (int tau = decMinLag; tau <= decMaxLag; tau++)
        {
            if (yinCmnd[tau] < YinThreshold)
            {
                while (tau + 1 <= decMaxLag && yinCmnd[tau + 1] < yinCmnd[tau]) tau++;
                best = tau;
                break;
            }
        }
        if (best < 0)
        {
            best = decMinLag;
            for (int tau = decMinLag + 1; tau <= decMaxLag; tau++)
                if (yinCmnd[tau] < yinCmnd[best]) best = tau;
        }
        if (yinCmnd[best] >= (trackVoiced ? VoicedStay : VoicedEnter)) { trackVoiced = false; return; }

        double lag = best;
        if (best > decMinLag && best < decMaxLag) lag += Parabola(yinCmnd[best - 1], yinCmnd[best], yinCmnd[best + 1]);
        trackPeriod = Math.Clamp(Refine(lag * dec), minPeriod, maxPeriod);
        trackVoiced = true;
    }

    /// <summary>Sharpens a coarse period with the full-rate difference function around it.</summary>
    private double Refine(double coarse)
    {
        int pc = (int)Math.Round(coarse);
        int lo = Math.Max(minPeriod, pc - dec - 1) - 1;
        int hi = Math.Min(maxPeriod + dec, pc + dec + 1) + 1;
        int count = hi - lo + 1;
        int span = fullWin + hi;
        if (count < 3 || count > fullDiff.Length || span > fullBuf.Length || written < span) return coarse;
        long s0 = written - span;
        for (int j = 0; j < span; j++) fullBuf[j] = At(s0 + j);
        int bestK = 0;
        for (int k = 0; k < count; k++)
        {
            int p = lo + k;
            double d = 0;
            for (int j = 0; j < fullWin; j++)
            {
                double e = fullBuf[j] - fullBuf[j + p];
                d += e * e;
            }
            fullDiff[k] = d;
            if (d < fullDiff[bestK]) bestK = k;
        }
        if (bestK == 0 || bestK == count - 1) return coarse;
        return lo + bestK + Parabola(fullDiff[bestK - 1], fullDiff[bestK], fullDiff[bestK + 1]);
    }

    /// <summary>Offset (-0.5..0.5) of the minimum of the parabola through three equally spaced values.</summary>
    private static double Parabola(double a, double b, double c)
    {
        double den = a - 2 * b + c;
        if (den <= 1e-30) return 0;
        return Math.Clamp(0.5 * (a - c) / den, -0.5, 0.5);
    }

    // ---------------------------------------------------------------- pitch marks

    private void Mark()
    {
        long newest = written - 1;
        if (!trackVoiced)
        {
            voicedRun = false;
            if (uvNext <= frontier) uvNext = frontier + 1;
            long limit = newest - uvLag;
            while (uvNext <= limit) { AddMark(uvNext, 0, false); uvNext += uvMarkStep; }
            return;
        }

        double p = trackPeriod;
        int search = (int)Math.Round(MarkSearch * p);
        if (!voicedRun)
        {
            // A voiced stretch starts: lock onto the strongest peak of the last full period, then walk back one
            // period at a time to fill in the marks between it and what is already marked.
            long hi = newest - search;
            long m0 = PeakIn(hi - (long)Math.Round(p), hi);
            int nb = 0;
            long m = m0;
            while (nb < backfill.Length)
            {
                long c = m - (long)Math.Round(p);
                long a = Math.Max(c - search, frontier + 1), b = c + search;
                if (a > b) break;
                m = PeakIn(a, b);
                backfill[nb++] = m;
            }
            for (int k = nb - 1; k >= 0; k--) AddMark(backfill[k], (float)p, true);
            if (m0 > frontier) AddMark(m0, (float)p, true);
            lastMark = m0;
            voicedRun = true;
            frontier = Math.Max(frontier, m0 + (long)(p - search));
        }

        while (true)
        {
            p = trackPeriod;
            search = (int)Math.Round(MarkSearch * p);
            long expect = lastMark + (long)Math.Round(p);
            long b = expect + search;
            if (b > newest) break;
            long a = Math.Max(expect - search, frontier + 1);
            if (a > b) { lastMark = expect; continue; }
            long m = PeakIn(a, b);
            AddMark(m, (float)p, true);
            lastMark = m;
            // The next search starts at least 0.8 period later: nothing can be marked before that.
            frontier = Math.Max(frontier, m + (long)(p - search));
        }
    }

    private long PeakIn(long a, long b)
    {
        long best = a;
        float bestV = At(a);
        for (long n = a + 1; n <= b; n++)
        {
            float v = At(n);
            if (v > bestV) { bestV = v; best = n; }
        }
        return best;
    }

    private void AddMark(long pos, float period, bool voiced)
    {
        int slot = (int)(markCount & (MarkCap - 1));
        markPos[slot] = pos;
        markPer[slot] = period;
        markVoiced[slot] = voiced;
        markCount++;
        if (pos > frontier) frontier = pos;
        if (markCount - cursor > MarkCap - 8) cursor = markCount - (MarkCap - 8); // never point at a reused slot
    }

    // ---------------------------------------------------------------- synthesis

    /// <param name="due">The newest output sample handed out after this call: every grain reaching it must be in.</param>
    private void Synthesize(double ratio, double formant, double wob, double flat, long due)
    {
        while (true)
        {
            bool late = synthPos - grainMax <= due;
            // Wait until every mark that could be the nearest one is known, unless this grain is already due
            // (then use the marks we have: a slightly worse grain beats a gap).
            if (!late && synthPos + lookAhead > frontier) break;
            if (markCount > 0) AdvanceCursor();
            else if (!late) break;

            int slot = (int)(cursor & (MarkCap - 1));
            bool voiced = markCount > 0 && markVoiced[slot] && Math.Abs(markPos[slot] - synthPos) <= markPer[slot];
            if (voiced)
            {
                double period = markPer[slot];
                double g = Math.Min(period, grainMax);
                double halfOut = formant >= 1 ? g / formant : g;
                long centre = markPos[slot];
                if (centre + halfOut * formant + 3 <= written - 1)
                {
                    double r = GrainRatio(ratio, period, wob, flat);
                    // Same loudness: r times more periods per second add power, a grain squeezed by formant carries less.
                    AddGrain(synthPos, centre, halfOut, formant, Math.Sqrt(formant / r));
                    uvRun = 0;
                    synthPos += period / r;
                    continue;
                }
                if (!late) break;
            }

            // Unvoiced (or a late voiced grain whose input is not in yet): a 10 ms grain.
            if (synthPos + uvHalf * formant + 3 > written - 1) break;
            AddUnvoicedGrain(formant);
            synthPos += uvHalf;
        }
    }

    /// <summary>
    /// The pitch ratio of one voiced grain: <paramref name="ratio"/>, with the melody pulled towards the speaker's
    /// usual pitch by Monotone (followed over about 2.5 s, so the voice keeps its register) and swayed by Wobble.
    /// </summary>
    private double GrainRatio(double ratio, double period, double wob, double flat)
    {
        double logHz = Math.Log(sampleRate / period);
        usualLogHz = usualLogHz == 0 ? logHz : usualLogHz + (logHz - usualLogHz) * Math.Min(1, period / (2.5 * sampleRate));
        double r = ratio;
        if (flat > 0) r *= Math.Exp(flat * (usualLogHz - logHz));
        if (wob > 0)
        {
            wobblePhase += 2 * Math.PI * (4 + 3 * wob) * (synthPos - wobbleAt) / sampleRate;
            if (wobblePhase > 2 * Math.PI) wobblePhase %= 2 * Math.PI;
            r *= Math.Pow(2, 1.2 * wob * Math.Sin(wobblePhase) / 12);
        }
        wobbleAt = synthPos;
        return Math.Clamp(r, 0.2, 5);
    }

    /// <summary>
    /// Unvoiced grains play the input on at the formant speed, each one continuing exactly where the last one
    /// read (so neighbours overlap coherently: no repeated noise, hence no comb tone). That reading point drifts
    /// from "now" by uvHalf * (formant - 1) per grain; once it leaves [synthPos - uvJump, synthPos] it jumps
    /// back by uvJump, to the best-matching spot nearby, and the crossfade is levelled for what does not match.
    /// </summary>
    private void AddUnvoicedGrain(double formant)
    {
        double src;
        bool splice = false;
        double rho = 1;
        if (uvRun == 0) src = synthPos;
        else
        {
            src = uvSrc + uvHalf * formant;
            if (src > synthPos || src < synthPos - uvJump)
            {
                src = SpliceTarget(src, src > synthPos ? src - uvJump : src + uvJump, formant, out rho);
                splice = true;
            }
        }
        if (!voicedOnly)
        {
            AddGrain(synthPos, src, uvHalf, formant, 1);
            if (splice && uvRun >= 2) LevelCrossfade(rho);
        }
        uvSrc = src;
        if (uvRun < 2) uvRun++;
    }

    /// <summary>
    /// WSOLA-style search around <paramref name="target"/> (never past synthPos) for the reading point whose first
    /// half looks most like the natural continuation <paramref name="natural"/>; <paramref name="rho"/> is how alike.
    /// </summary>
    private double SpliceTarget(double natural, double target, double formant, out double rho)
    {
        int len = (int)(uvHalf * formant);
        long natStart = (long)Math.Round(natural) - len;
        double xx = 0;
        for (int j = 0; j < len; j += 2) { double a = At(natStart + j); xx += a * a; }
        double best = target, bestScore = double.NegativeInfinity, bestXy = 0, bestYy = 0;
        long baseStart = (long)Math.Round(target) - len;
        for (int off = -spliceSearch; off <= spliceSearch; off++)
        {
            if (target + off > synthPos) break;
            long cs = baseStart + off;
            double xy = 0, yy = 0;
            for (int j = 0; j < len; j += 2)
            {
                double b = At(cs + j);
                xy += At(natStart + j) * b;
                yy += b * b;
            }
            double score = xy / Math.Sqrt(yy + 1e-12);
            if (score > bestScore) { bestScore = score; best = target + off; bestXy = xy; bestYy = yy; }
        }
        rho = xx > 1e-12 && bestYy > 1e-12 ? Math.Clamp(bestXy / Math.Sqrt(xx * bestYy), 0, 1) : 1;
        return best;
    }

    /// <summary>
    /// After a splice the two overlapping grains are only <paramref name="rho"/> alike, so the Hann crossfade
    /// would dip in power; scale the overlap back up (only the two grains can be there: the run is 2+ grains long).
    /// </summary>
    private void LevelCrossfade(double rho)
    {
        if (rho >= 0.999) return;
        long first = Math.Max((long)Math.Floor(synthPos - uvHalf) + 1, readPos);
        long last = (long)Math.Ceiling(synthPos) - 1;
        double k = Math.PI / uvHalf;
        for (long n = first; n <= last; n++)
        {
            double w2 = 0.5 + 0.5 * Math.Cos(k * (n - synthPos));
            double w1 = 1 - w2;
            double power = w1 * w1 + w2 * w2 + 2 * rho * w1 * w2;
            output[(int)(n & ringMask)] *= (float)(1 / Math.Sqrt(Math.Max(power, 0.5)));
        }
    }

    private void AdvanceCursor()
    {
        double reach = synthPos + lookAhead;
        while (cursor + 1 < markCount)
        {
            long next = markPos[(int)((cursor + 1) & (MarkCap - 1))];
            if (next > reach) break;
            long cur = markPos[(int)(cursor & (MarkCap - 1))];
            if (Math.Abs(next - synthPos) > Math.Abs(cur - synthPos)) break;
            cursor++;
        }
    }

    /// <summary>
    /// Overlap-adds one Hann grain centred at <paramref name="at"/> (output time) that reads the input around
    /// <paramref name="from"/> at <paramref name="formant"/> times the speed.
    /// </summary>
    private void AddGrain(double at, double from, double halfOut, double formant, double gain)
    {
        long first = Math.Max((long)Math.Floor(at - halfOut) + 1, readPos); // earlier output is already gone
        long last = (long)Math.Ceiling(at + halfOut) - 1;
        double k = Math.PI / halfOut;
        for (long n = first; n <= last; n++)
        {
            double u = n - at;
            double w = 0.5 + 0.5 * Math.Cos(k * u);
            output[(int)(n & ringMask)] += (float)(gain * w * Interp(from + u * formant));
        }
    }
}
