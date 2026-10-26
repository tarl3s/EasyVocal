using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyVocal.Ai.Rvc;

/// <summary>
/// RVC's optional <c>.index</c>: a faiss IndexIVFFlat over the voice's training features (768-dim), read without faiss
/// (header, the flat L2 quantizer's centroids, the ArrayInvertedLists' raw vectors). Retrieval = the <c>nprobe</c>
/// nearest centroids, then the k = 8 nearest vectors in those lists; RVC blends each frame with them weighted by
/// 1/d² at the "index rate". The index is read-only once loaded: several streams may blend with it at once (each
/// thread has its own scratch; <see cref="Blend"/> allocates nothing after a thread's first call).
/// </summary>
public sealed class FeatureIndex
{
    public const int K = 8;
    private float[] centroids = Array.Empty<float>();
    private float[][] lists = Array.Empty<float[]>();
    private int nprobe = 1;

    private sealed class Scratch
    {
        public readonly float[] BestD = new float[K];
        public readonly int[] BestV = new int[K], BestL = new int[K];
        public int[] Probe = new int[1];
        public float[] ProbeD = new float[1];
        public double[] Mix = Array.Empty<double>();
    }

    [ThreadStatic] private static Scratch? scratch;

    public int D { get; private set; }
    public int NList { get; private set; }
    public long NTotal { get; private set; }

    public int NProbe
    {
        get => nprobe;
        set => nprobe = Math.Clamp(value, 1, Math.Min(MaxProbe, Math.Max(1, NList)));
    }

    /// <summary>Most lists looked at per frame (a file asking for more would only cost time on the audio worker).</summary>
    public const int MaxProbe = 8;

    public static FeatureIndex Load(string path, long maxVectors = 4_000_000)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        var ix = new FeatureIndex();
        Expect(br, "IwFl");
        ix.D = br.ReadInt32();
        ix.NTotal = br.ReadInt64();
        br.ReadInt64(); br.ReadInt64(); br.ReadByte();
        int metric = br.ReadInt32();
        if (metric > 1) br.ReadSingle();
        if (metric != 1) throw new NotSupportedException("Only L2 indexes are supported.");
        if (ix.D <= 0 || ix.D > 4096 || ix.NTotal < 0 || ix.NTotal > maxVectors) throw new InvalidDataException("This index is too big or damaged.");
        long nlist = br.ReadInt64();
        long nprobe = br.ReadInt64();
        if (nlist <= 0 || nlist > 1_000_000) throw new InvalidDataException("Damaged index (lists).");
        ix.NList = (int)nlist;
        Expect(br, "IxF2");                                       // quantizer: IndexFlatL2
        br.ReadInt32(); br.ReadInt64(); br.ReadInt64(); br.ReadInt64(); br.ReadByte();
        int qm = br.ReadInt32();
        if (qm > 1) br.ReadSingle();
        long nc = br.ReadInt64();
        if (nc != nlist * ix.D) throw new InvalidDataException("Damaged index (centroids).");
        Fits(br, nc * 4);
        ix.centroids = ReadFloats(br, nc);
        byte dmType = br.ReadByte();                              // direct map
        long dmN = br.ReadInt64();
        if (dmN < 0 || dmN > maxVectors) throw new InvalidDataException("Damaged index (direct map).");
        br.BaseStream.Seek(dmN * 8, SeekOrigin.Current);
        if (dmType == 2) throw new NotSupportedException("Indexes with a hashtable direct map aren't supported.");
        Expect(br, "ilar");
        long nl2 = br.ReadInt64(), codeSize = br.ReadInt64();
        if (nl2 != nlist || codeSize != ix.D * 4L) throw new NotSupportedException("Only flat (IVF-Flat) indexes are supported.");
        var sizes = new long[nlist];
        string kind = System.Text.Encoding.ASCII.GetString(br.ReadBytes(4));
        if (kind == "full")
        {
            long n = br.ReadInt64();
            if (n != nlist) throw new InvalidDataException("Damaged index (list sizes).");
            for (int i = 0; i < n; i++) sizes[i] = br.ReadInt64();
        }
        else if (kind == "sprs")
        {
            long n = br.ReadInt64();
            for (long i = 0; i < n; i += 2)
            {
                long li = br.ReadInt64(), sz = br.ReadInt64();
                if (li < 0 || li >= nlist) throw new InvalidDataException("Damaged index (list sizes).");
                sizes[li] = sz;
            }
        }
        else throw new NotSupportedException("Index list sizes stored as " + kind + " aren't supported.");
        long total = 0;
        foreach (var s in sizes) { if (s < 0) throw new InvalidDataException("Damaged index."); total += s; }
        if (total > maxVectors) throw new InvalidDataException("This index is too big.");
        Fits(br, total * (ix.D * 4L + 8)); // the vectors and their ids must really be in the file before anything is allocated
        ix.lists = new float[nlist][];
        for (int l = 0; l < nlist; l++)
        {
            ix.lists[l] = ReadFloats(br, sizes[l] * ix.D);
            br.BaseStream.Seek(sizes[l] * 8, SeekOrigin.Current); // ids: not needed (the codes are the vectors)
        }
        ix.NProbe = (int)Math.Max(1, nprobe);
        return ix;
    }

    /// <summary>The rest of the file holds at least <paramref name="bytes"/> (else it is cut short or lies about its sizes).</summary>
    private static void Fits(BinaryReader br, long bytes)
    {
        var s = br.BaseStream;
        if (bytes < 0 || s.Length - s.Position < bytes) throw new InvalidDataException("Damaged index (cut short).");
    }

    private static float[] ReadFloats(BinaryReader br, long n)
    {
        var v = new float[n];
        var bytes = MemoryMarshal.AsBytes(v.AsSpan());
        int at = 0;
        while (at < bytes.Length)
        {
            int got = br.BaseStream.Read(bytes[at..]);
            if (got <= 0) throw new InvalidDataException("The index file is cut short.");
            at += got;
        }
        return v;
    }

    private static void Expect(BinaryReader br, string fourcc)
    {
        string s = System.Text.Encoding.ASCII.GetString(br.ReadBytes(4));
        if (s != fourcc) throw new InvalidDataException($"Not an index EasyVocal can read (expected {fourcc}, got {s}).");
    }

    /// <summary>RVC's retrieval on frames [from, to) of [frames, D] features, in place.</summary>
    public void Blend(float[] feats, int from, int to, float rate)
    {
        if (rate <= 0) return;
        int d = D;
        var s = scratch ??= new Scratch();
        if (s.Probe.Length != nprobe) { s.Probe = new int[nprobe]; s.ProbeD = new float[nprobe]; }
        if (s.Mix.Length != d) s.Mix = new double[d];
        float[] probeD = s.ProbeD, bestD = s.BestD;
        int[] probe = s.Probe, bestV = s.BestV, bestL = s.BestL;
        double[] mix = s.Mix;
        for (int f = from; f < to; f++)
        {
            var q = feats.AsSpan(f * d, d);
            Array.Fill(probeD, float.MaxValue);
            for (int c = 0; c < NList; c++) Insert(probeD, probe, null, L2(q, centroids.AsSpan(c * d, d)), c, 0);
            Array.Fill(bestD, float.MaxValue);
            foreach (int l in probe)
            {
                if (l < 0 || l >= lists.Length) continue;
                var list = lists[l];
                int n = list.Length / d;
                for (int i = 0; i < n; i++) Insert(bestD, bestV, bestL, L2(q, list.AsSpan(i * d, d)), i, l);
            }
            Array.Clear(mix);
            double wsum = 0;
            for (int j = 0; j < K; j++)
            {
                if (bestD[j] == float.MaxValue) continue;
                double w = 1.0 / Math.Max(1e-12, (double)bestD[j] * bestD[j]);
                wsum += w;
                var v = lists[bestL[j]].AsSpan(bestV[j] * d, d);
                for (int i = 0; i < d; i++) mix[i] += w * v[i];
            }
            if (wsum <= 0) continue;
            for (int i = 0; i < d; i++) q[i] = (float)(mix[i] / wsum * rate + q[i] * (1 - rate));
        }
    }

    private static void Insert(float[] ds, int[] vs, int[]? ls, float dist, int v, int l)
    {
        int n = ds.Length;
        if (dist >= ds[n - 1]) return;
        int p = n - 1;
        while (p > 0 && ds[p - 1] > dist)
        {
            ds[p] = ds[p - 1];
            vs[p] = vs[p - 1];
            if (ls != null) ls[p] = ls[p - 1];
            p--;
        }
        ds[p] = dist;
        vs[p] = v;
        if (ls != null) ls[p] = l;
    }

    private static float L2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var acc = Vector<float>.Zero;
        for (int i = 0; i < va.Length; i++) { var t = va[i] - vb[i]; acc += t * t; }
        float s = Vector.Dot(acc, Vector<float>.One);
        for (int i = va.Length * Vector<float>.Count; i < a.Length; i++) { float t = a[i] - b[i]; s += t * t; }
        return s;
    }
}
