using System.IO.Compression;
using System.Text;

namespace EasyVocal.Ai.Import;

/// <summary>
/// Reads and rewrites ONNX models at the protobuf level (no generated code, no ONNX library): lists a graph's
/// initializers and replaces the weights of a <em>template</em> (a graph exported once per architecture, possibly with
/// its weights stripped) with a voice's tensors, so a <c>.pth</c> becomes a runnable model without PyTorch.
/// <para>
/// <c>SessionOptions.AddInitializer</c> is not an option: ONNX Runtime's optimizer has already used the file's
/// values by then (phase 1 research).
/// </para>
/// </summary>
public static class OnnxRewriter
{
    public sealed record Initializer(string Name, long[] Dims, int DataType, bool HasData);

    private const int Float = 1;

    /// <summary>The bytes of a model file, gunzipped when it is <c>.gz</c> (the templates are stored that way).</summary>
    public static byte[] ReadModel(string path) => Unzip(File.ReadAllBytes(path));

    public static byte[] Unzip(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0x1f || bytes[1] != 0x8b) return bytes;
        using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        var o = new MemoryStream(bytes.Length * 4);
        gz.CopyTo(o);
        return o.ToArray();
    }

    /// <summary>Every initializer of the main graph.</summary>
    public static List<Initializer> Initializers(byte[] model)
    {
        var list = new List<Initializer>();
        int p = 0;
        while (p < model.Length)
        {
            var (field, wt) = Key(model, ref p);
            if (field == 7 && wt == 2)
            {
                int len = Len(model, ref p);
                int q = p, end = p + len;
                while (q < end)
                {
                    var (f2, w2) = Key(model, ref q);
                    if (f2 == 5 && w2 == 2) { int l = Len(model, ref q); list.Add(ReadTensorHeader(model, q, q + l)); q += l; }
                    else Skip(model, ref q, w2);
                }
                p = end;
            }
            else Skip(model, ref p, wt);
        }
        return list;
    }

    /// <summary>The names of the graph's inputs (not counting initializers listed as inputs).</summary>
    public static List<string> InputNames(byte[] model)
    {
        var names = new List<string>();
        int p = 0;
        while (p < model.Length)
        {
            var (field, wt) = Key(model, ref p);
            if (field == 7 && wt == 2)
            {
                int len = Len(model, ref p);
                int q = p, end = p + len;
                while (q < end)
                {
                    var (f2, w2) = Key(model, ref q);
                    if (f2 == 11 && w2 == 2)
                    {
                        int l = Len(model, ref q), r = q, e = q + l;
                        while (r < e)
                        {
                            var (f3, w3) = Key(model, ref r);
                            if (f3 == 1 && w3 == 2) { int sl = Len(model, ref r); names.Add(Encoding.UTF8.GetString(model, r, sl)); r += sl; }
                            else Skip(model, ref r, w3);
                        }
                        q = e;
                    }
                    else Skip(model, ref q, w2);
                }
                p = end;
            }
            else Skip(model, ref p, wt);
        }
        return names;
    }

    /// <summary>
    /// The template with every float initializer that <paramref name="lookup"/> knows replaced by that tensor (shape
    /// checked). An initializer it doesn't know keeps the template's data; one without data (a weight-less template)
    /// that it doesn't know is an error. <paramref name="used"/> collects the names that were filled.
    /// </summary>
    public static byte[] Fill(byte[] template, Func<string, Weight?> lookup, ISet<string>? used = null)
    {
        // Two passes so a big model (HuBERT: ~380 MB) is written once into an array of the right size.
        var parts = new List<Part>();
        long total = 0;
        int p = 0;
        while (p < template.Length)
        {
            int start = p;
            var (field, wt) = Key(template, ref p);
            if (field == 7 && wt == 2)
            {
                int len = Len(template, ref p);
                var graph = GraphParts(template, p, p + len, lookup, used);
                long glen = 0;
                foreach (var gp in graph) glen += gp.Length;
                var head = new MemoryStream();
                WriteVarint(head, (7 << 3) | 2);
                WriteVarint(head, (ulong)glen);
                parts.Add(Part.Bytes(head.ToArray()));
                parts.AddRange(graph);
                total += head.Length + glen;
                p += len;
            }
            else
            {
                Skip(template, ref p, wt);
                parts.Add(Part.Range(template, start, p - start));
                total += p - start;
            }
        }
        if (total > int.MaxValue - 64) throw new InvalidDataException("The model is too big.");
        var o = new byte[total];
        int at = 0;
        foreach (var part in parts) at += part.CopyTo(o, at);
        return o;
    }

    /// <summary>A piece of the output: a run of bytes, or a weight's float data.</summary>
    private readonly struct Part
    {
        private readonly byte[]? bytes;
        private readonly int offset, count;
        private readonly float[]? floats;

        private Part(byte[]? b, int off, int n, float[]? f) { bytes = b; offset = off; count = n; floats = f; }

        public static Part Range(byte[] b, int off, int n) => new(b, off, n, null);
        public static Part Bytes(byte[] b) => new(b, 0, b.Length, null);
        public static Part Floats(float[] f) => new(null, 0, f.Length * 4, f);

        public long Length => count;

        public int CopyTo(byte[] dst, int at)
        {
            if (floats != null) System.Runtime.InteropServices.MemoryMarshal.AsBytes(floats.AsSpan()).CopyTo(dst.AsSpan(at));
            else Buffer.BlockCopy(bytes!, offset, dst, at, count);
            return count;
        }
    }

    private static Initializer ReadTensorHeader(byte[] b, int q, int e)
    {
        string name = "";
        var dims = new List<long>();
        int type = 0;
        bool data = false;
        while (q < e)
        {
            var (f, w) = Key(b, ref q);
            if (f == 8 && w == 2) { int l = Len(b, ref q); name = Encoding.UTF8.GetString(b, q, l); q += l; }
            else if (f == 1 && w == 0) dims.Add((long)Varint(b, ref q));
            else if (f == 1 && w == 2) { int l = Len(b, ref q), qe = q + l; while (q < qe) dims.Add((long)Varint(b, ref q)); }
            else if (f == 2 && w == 0) type = (int)Varint(b, ref q);
            else
            {
                if (f is 4 or 5 or 6 or 7 or 9 or 10 or 11 or 13) data = true;
                if (w == 2 && (f is 4 or 5 or 7 or 9 or 10 or 11))
                {
                    int l = Len(b, ref q);
                    if (l == 0) data = false; // an empty packed field
                    q += l;
                }
                else Skip(b, ref q, w);
            }
        }
        return new Initializer(name, dims.ToArray(), type, data);
    }

    private static List<Part> GraphParts(byte[] b, int p, int end, Func<string, Weight?> lookup, ISet<string>? used)
    {
        var o = new List<Part>();
        while (p < end)
        {
            int start = p;
            var (field, wt) = Key(b, ref p);
            if (field == 5 && wt == 2)
            {
                int len = Len(b, ref p);
                var head = ReadTensorHeader(b, p, p + len);
                var w = head.DataType == Float ? lookup(head.Name) : null;
                if (w == null)
                {
                    if (!head.HasData) throw new InvalidDataException($"The voice has no weight '{head.Name}' this model type needs.");
                    p += len;
                    o.Add(Part.Range(b, start, p - start));
                    continue;
                }
                if (!head.Dims.SequenceEqual(w.Shape))
                    throw new InvalidDataException($"'{head.Name}' has the shape [{string.Join(",", w.Shape)}], this model type needs [{string.Join(",", head.Dims)}].");
                used?.Add(head.Name);
                p += len;
                // TensorProto: dims, data_type = FLOAT, name, raw_data
                var t = new MemoryStream(head.Name.Length + 64);
                foreach (var d in head.Dims) { WriteVarint(t, 1 << 3); WriteVarint(t, (ulong)d); }
                WriteVarint(t, 2 << 3); WriteVarint(t, Float);
                var nb = Encoding.UTF8.GetBytes(head.Name);
                WriteVarint(t, (8 << 3) | 2); WriteVarint(t, (ulong)nb.Length); t.Write(nb);
                long raw = (long)w.Data.Length * 4;
                WriteVarint(t, (9 << 3) | 2); WriteVarint(t, (ulong)raw);
                var header = new MemoryStream();
                WriteVarint(header, (5 << 3) | 2); WriteVarint(header, (ulong)(t.Length + raw));
                o.Add(Part.Bytes(header.ToArray()));
                o.Add(Part.Bytes(t.ToArray()));
                o.Add(Part.Floats(w.Data));
            }
            else
            {
                Skip(b, ref p, wt);
                o.Add(Part.Range(b, start, p - start));
            }
        }
        return o;
    }

    private static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    private static (int, int) Key(byte[] b, ref int p) { ulong k = Varint(b, ref p); return ((int)(k >> 3), (int)(k & 7)); }

    private static int Len(byte[] b, ref int p)
    {
        ulong l = Varint(b, ref p);
        if (l > (ulong)(b.Length - p)) throw new InvalidDataException("Damaged ONNX file (a length runs past the end).");
        return (int)l;
    }

    private static ulong Varint(byte[] b, ref int p)
    {
        ulong r = 0;
        for (int s = 0; s < 64; s += 7)
        {
            if (p >= b.Length) throw new InvalidDataException("Damaged ONNX file (cut short).");
            byte x = b[p++];
            r |= (ulong)(x & 0x7F) << s;
            if (x < 0x80) return r;
        }
        throw new InvalidDataException("Damaged ONNX file (bad number).");
    }

    private static void Skip(byte[] b, ref int p, int wt)
    {
        switch (wt)
        {
            case 0: Varint(b, ref p); break;
            case 1: p += 8; break;
            case 2: { int l = Len(b, ref p); p += l; break; }
            case 5: p += 4; break;
            default: throw new InvalidDataException("Damaged ONNX file (wire type " + wt + ").");
        }
        if (p > b.Length) throw new InvalidDataException("Damaged ONNX file (cut short).");
    }
}
