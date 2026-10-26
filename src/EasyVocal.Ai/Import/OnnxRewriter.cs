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
    public sealed record Initializer(string Name, long[] Dims, int DataType, bool HasData, bool External = false);

    private const int Float = 1, Float16 = 10;

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
    /// <summary>The model keeps (some of) its weights in other files: never opened (ONNX Runtime would read whatever the paths say).</summary>
    public static bool HasExternalData(byte[] model) => Initializers(model).Any(i => i.External);

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
    public static byte[] Fill(byte[] template, Func<string, Weight?> lookup, ISet<string>? used = null) =>
        Rewrite(template, (b, start, end, head, into) =>
        {
            var w = head.DataType == Float ? lookup(head.Name) : null;
            if (w == null)
            {
                if (!head.HasData) throw new InvalidDataException($"The voice has no weight '{head.Name}' this model type needs.");
                return false;
            }
            if (!head.Dims.SequenceEqual(w.Shape))
                throw new InvalidDataException($"'{head.Name}' has the shape [{string.Join(",", w.Shape)}], this model type needs [{string.Join(",", head.Dims)}].");
            used?.Add(head.Name);
            AddTensor(into, head, Float, Part.Floats(w.Data));
            return true;
        });

    /// <summary>
    /// The model with its big float weights stored as fp16 (half the bytes; the AI pack's pitch model is kept this way).
    /// The result is not meant to be run as it is: <see cref="ExpandHalf"/> turns it back into the fp32 model (the same
    /// graph, each weight rounded to fp16) before a session is made. Weights with fewer than
    /// <paramref name="minElements"/> values (biases, normalisation statistics) and any value fp16 can't hold stay fp32.
    /// A model that already has fp16 weights is refused (expanding it would change them).
    /// </summary>
    public static byte[] StoreHalf(byte[] model, int minElements = 4096) =>
        Rewrite(model, (b, start, end, head, into) =>
        {
            if (head.DataType == Float16) throw new InvalidDataException($"'{head.Name}' is fp16 already: the model can't be stored this way.");
            if (head.DataType != Float || !head.HasData || head.External) return false;
            long n = 1;
            foreach (var d in head.Dims) n *= d;
            if (n < minElements) return false;
            if (DataBytes(b, start, end) is not var (at, count) || count != n * 4) return false;
            var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(b.AsSpan(at, count));
            foreach (var v in src) if (!float.IsFinite(v) || Math.Abs(v) > 65504f) return false;
            AddTensor(into, head, Float16, Part.ToHalf(b, at, count));
            return true;
        });

    /// <summary>
    /// A model stored by <see cref="StoreHalf"/> made runnable again: every fp16 initializer becomes fp32 (the values
    /// exactly as stored), written once into an array of the right size.
    /// </summary>
    public static byte[] ExpandHalf(byte[] model) =>
        Rewrite(model, (b, start, end, head, into) =>
        {
            if (head.DataType != Float16 || !head.HasData || head.External) return false;
            long n = 1;
            foreach (var d in head.Dims) n *= d;
            if (DataBytes(b, start, end) is not var (at, count) || count != n * 2)
                throw new InvalidDataException($"'{head.Name}': its fp16 data isn't stored as raw bytes.");
            AddTensor(into, head, Float, Part.FromHalf(b, at, count));
            return true;
        });

    /// <summary>
    /// Where a TensorProto's values are, as one run of little-endian bytes: <c>raw_data</c>, or a packed
    /// <c>float_data</c> (null when they are stored any other way).
    /// </summary>
    private static (int At, int Count)? DataBytes(byte[] b, int q, int e)
    {
        (int, int)? found = null;
        while (q < e)
        {
            var (f, w) = Key(b, ref q);
            if (w == 2)
            {
                int l = Len(b, ref q);
                if ((f == 9 || f == 4) && l > 0) found = (q, l);
                q += l;
            }
            else
            {
                if (f == 4) return null; // float_data one value at a time
                Skip(b, ref q, w);
            }
        }
        return found;
    }

    /// <summary>Called for each initializer of the main graph (its TensorProto is <c>b[start..end)</c>); true = it was written into <c>into</c>, false = keep it as it is.</summary>
    private delegate bool TensorMap(byte[] b, int start, int end, Initializer head, List<Part> into);

    /// <summary>The model with some initializers rewritten. Two passes so a big model (HuBERT: ~380 MB) is written once into an array of the right size.</summary>
    private static byte[] Rewrite(byte[] model, TensorMap map)
    {
        var parts = new List<Part>();
        long total = 0;
        int p = 0;
        while (p < model.Length)
        {
            int start = p;
            var (field, wt) = Key(model, ref p);
            if (field == 7 && wt == 2)
            {
                int len = Len(model, ref p);
                var graph = GraphParts(model, p, p + len, map);
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
                Skip(model, ref p, wt);
                parts.Add(Part.Range(model, start, p - start));
                total += p - start;
            }
        }
        if (total > int.MaxValue - 64) throw new InvalidDataException("The model is too big.");
        var o = new byte[total];
        int at = 0;
        foreach (var part in parts) at += part.CopyTo(o, at);
        return o;
    }

    /// <summary>A piece of the output: a run of bytes, a weight's float data, or source bytes converted fp32 ↔ fp16 on the way.</summary>
    private readonly struct Part
    {
        private enum Kind : byte { Copy, Floats, ToHalf, FromHalf }

        private readonly byte[]? bytes;
        private readonly int offset, count;
        private readonly float[]? floats;
        private readonly Kind kind;

        private Part(Kind k, byte[]? b, int off, int n, float[]? f) { kind = k; bytes = b; offset = off; count = n; floats = f; }

        public static Part Range(byte[] b, int off, int n) => new(Kind.Copy, b, off, n, null);
        public static Part Bytes(byte[] b) => new(Kind.Copy, b, 0, b.Length, null);
        public static Part Floats(float[] f) => new(Kind.Floats, null, 0, f.Length * 4, f);
        /// <summary>fp32 bytes <c>b[off..off+n)</c> written as fp16 (n / 2 bytes).</summary>
        public static Part ToHalf(byte[] b, int off, int n) => new(Kind.ToHalf, b, off, n, null);
        /// <summary>fp16 bytes <c>b[off..off+n)</c> written as fp32 (n × 2 bytes).</summary>
        public static Part FromHalf(byte[] b, int off, int n) => new(Kind.FromHalf, b, off, n, null);

        public long Length => kind switch { Kind.ToHalf => count / 2, Kind.FromHalf => (long)count * 2, _ => count };

        public int CopyTo(byte[] dst, int at)
        {
            switch (kind)
            {
                case Kind.Floats:
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(floats.AsSpan()).CopyTo(dst.AsSpan(at));
                    break;
                case Kind.ToHalf:
                {
                    var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan(offset, count));
                    var o = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(dst.AsSpan(at, count / 2));
                    for (int i = 0; i < src.Length; i++) o[i] = (Half)src[i];
                    break;
                }
                case Kind.FromHalf:
                {
                    var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bytes.AsSpan(offset, count));
                    var o = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(dst.AsSpan(at, count * 2));
                    for (int i = 0; i < src.Length; i++) o[i] = (float)src[i];
                    break;
                }
                default:
                    Buffer.BlockCopy(bytes!, offset, dst, at, count);
                    break;
            }
            return (int)Length;
        }
    }

    /// <summary>A TensorProto (dims, data_type, name, raw_data = <paramref name="data"/>) as an initializer of the graph.</summary>
    private static void AddTensor(List<Part> into, Initializer head, int dataType, Part data)
    {
        var t = new MemoryStream(head.Name.Length + 64);
        foreach (var d in head.Dims) { WriteVarint(t, 1 << 3); WriteVarint(t, (ulong)d); }
        WriteVarint(t, 2 << 3); WriteVarint(t, (ulong)dataType);
        var nb = Encoding.UTF8.GetBytes(head.Name);
        WriteVarint(t, (8 << 3) | 2); WriteVarint(t, (ulong)nb.Length); t.Write(nb);
        WriteVarint(t, (9 << 3) | 2); WriteVarint(t, (ulong)data.Length);
        var header = new MemoryStream();
        WriteVarint(header, (5 << 3) | 2); WriteVarint(header, (ulong)(t.Length + data.Length));
        into.Add(Part.Bytes(header.ToArray()));
        into.Add(Part.Bytes(t.ToArray()));
        into.Add(data);
    }

    private static Initializer ReadTensorHeader(byte[] b, int q, int e)
    {
        string name = "";
        var dims = new List<long>();
        int type = 0;
        bool data = false, external = false;
        while (q < e)
        {
            var (f, w) = Key(b, ref q);
            if (f == 8 && w == 2) { int l = Len(b, ref q); name = Encoding.UTF8.GetString(b, q, l); q += l; }
            else if (f == 1 && w == 0) dims.Add((long)Varint(b, ref q));
            else if (f == 1 && w == 2) { int l = Len(b, ref q), qe = q + l; while (q < qe) dims.Add((long)Varint(b, ref q)); }
            else if (f == 2 && w == 0) type = (int)Varint(b, ref q);
            else if (f == 14 && w == 0) external |= Varint(b, ref q) == 1; // data_location = EXTERNAL
            else
            {
                if (f is 4 or 5 or 6 or 7 or 9 or 10 or 11 or 13) data = true;
                if (f == 13) external = true; // external_data: the weights would be read from other files
                if (w == 2 && (f is 4 or 5 or 7 or 9 or 10 or 11))
                {
                    int l = Len(b, ref q);
                    if (l == 0) data = false; // an empty packed field
                    q += l;
                }
                else Skip(b, ref q, w);
            }
        }
        return new Initializer(name, dims.ToArray(), type, data, external);
    }


    private static List<Part> GraphParts(byte[] b, int p, int end, TensorMap map)
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
                bool rewritten = map(b, p, p + len, head, o);
                p += len;
                if (!rewritten) o.Add(Part.Range(b, start, p - start));
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
