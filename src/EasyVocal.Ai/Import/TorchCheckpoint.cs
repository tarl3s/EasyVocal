using System.IO.Compression;
using System.Text;

namespace EasyVocal.Ai.Import;

/// <summary>A tensor read from a checkpoint, as fp32 (row-major).</summary>
public sealed record Weight(float[] Data, long[] Shape)
{
    public long Count => Data.LongLength;
}

/// <summary>
/// Reads a PyTorch checkpoint (<c>torch.save</c>'s zip: <c>*/data.pkl</c> + <c>*/data/N</c> storages) with no PyTorch:
/// a small pickle machine that understands only what torch.save writes (dicts, lists, tuples, numbers, strings,
/// OrderedDict, <c>torch._utils._rebuild_tensor_v2</c> / <c>_rebuild_parameter</c>, persistent storage ids). Nothing in
/// the file is ever run: unknown classes become inert placeholders. An RVC <c>.pth</c> is such a file (state dict in
/// <c>weight</c>, plus <c>config</c>, <c>sr</c>, <c>f0</c>, <c>version</c>, <c>info</c>); so is Transformers'
/// <c>pytorch_model.bin</c>.
/// </summary>
public sealed class TorchCheckpoint
{
    /// <summary>Largest tensor accepted (elements); a real RVC/HuBERT tensor is far below.</summary>
    private const long MaxElements = 64L * 1024 * 1024;

    public Dictionary<string, object?> Root { get; } = new();
    public Dictionary<string, Weight> Tensors { get; } = new();

    public string? Sr => Root.GetValueOrDefault("sr") as string ?? (Root.GetValueOrDefault("sr") is long l ? l.ToString() : null);
    public string? Version => Root.GetValueOrDefault("version") as string;
    public string? Info => Root.GetValueOrDefault("info") as string;

    /// <summary>The "f0" flag: 1 = the model takes pitch (missing in some old files: assume yes).</summary>
    public bool HasPitch => Root.GetValueOrDefault("f0") switch { long v => v != 0, bool b => b, _ => true };

    /// <summary>The 18 hyper-parameters an RVC .pth keeps in "config" (numbers and lists), or null.</summary>
    public List<object?>? Config => Root.GetValueOrDefault("config") as List<object?>;

    public sealed class TensorRef
    {
        public string Storage = "", Dtype = "";
        public long Offset;
        public long[] Size = Array.Empty<long>(), Stride = Array.Empty<long>();
    }

    private sealed class Global { public string Name = ""; public override string ToString() => Name; }
    private sealed class Opaque { public object? Cls, Args, State; }
    private sealed class Mark { }

    /// <summary>Largest data.pkl read (an RVC .pth's is a few kB; HuBERT's well under 1 MB).</summary>
    private const long MaxPickleBytes = 8L * 1024 * 1024;
    /// <summary>Most bytes all tensors' storages may take together once unpacked.</summary>
    public const long MaxTotalBytes = 1536L * 1024 * 1024;
    /// <summary>Most objects / stack entries / memo entries the pickle machine keeps (a real checkpoint: thousands).</summary>
    private const int MaxObjects = 4_000_000, MaxStack = 1_000_000, MaxMemo = 1_000_000;

    public static TorchCheckpoint Load(string path, CancellationToken ct = default)
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw new InvalidDataException("This isn't a PyTorch file EasyVocal can read (only the zip format of PyTorch 1.6 and later)."); }
        using (zip)
        {
            var pkl = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("data.pkl", StringComparison.Ordinal))
                      ?? throw new InvalidDataException("No data.pkl inside: not a PyTorch checkpoint.");
            string prefix = pkl.FullName[..^"data.pkl".Length];
            var root = Unpickle(ReadAll(pkl, pkl.Length, MaxPickleBytes));
            var ck = new TorchCheckpoint();
            if (root is Dictionary<object, object?> d)
                foreach (var kv in d) ck.Root[kv.Key.ToString() ?? ""] = kv.Value;
            object? sdObj = ck.Root.GetValueOrDefault("weight") ?? ck.Root.GetValueOrDefault("model") ?? ck.Root.GetValueOrDefault("state_dict") ?? root;
            if (sdObj is not Dictionary<object, object?> sd) throw new InvalidDataException("No weights (state dict) inside.");
            // What each storage must hold for the tensors that use it: only that much is ever read.
            var needed = new Dictionary<string, long>();
            foreach (var kv in sd)
                if (kv.Value is TensorRef t)
                    needed[t.Storage] = Math.Max(needed.GetValueOrDefault(t.Storage), BytesNeeded(t));
            long total = 0;
            foreach (var n in needed.Values)
            {
                total = checked(total + n);
                if (total > MaxTotalBytes) throw new InvalidDataException("The model is too big.");
            }
            var storages = new Dictionary<string, byte[]>();
            foreach (var kv in sd)
            {
                ct.ThrowIfCancellationRequested();
                if (kv.Value is not TensorRef t) continue;
                if (!storages.TryGetValue(t.Storage, out var raw))
                {
                    var e = zip.GetEntry(prefix + "data/" + t.Storage) ?? throw new InvalidDataException("Missing storage " + t.Storage);
                    storages[t.Storage] = raw = ReadAll(e, needed[t.Storage], int.MaxValue - 64);
                }
                ck.Tensors[kv.Key.ToString() ?? ""] = new Weight(Materialize(raw, t), t.Size);
            }
            return ck;
        }
    }

    /// <summary>
    /// The bytes of its storage a tensor reaches (offset + the farthest element), checked: sizes and strides must be
    /// non-negative, and a tensor at most <see cref="MaxElements"/> elements.
    /// </summary>
    private static long BytesNeeded(TensorRef t)
    {
        if (t.Size.Length != t.Stride.Length || t.Offset < 0) throw new InvalidDataException("Bad tensor layout.");
        long n = 1, far = t.Offset;
        try
        {
            for (int i = 0; i < t.Size.Length; i++)
            {
                if (t.Size[i] < 0 || t.Stride[i] < 0) throw new InvalidDataException("Bad tensor layout.");
                n = checked(n * t.Size[i]);
                if (n > MaxElements) throw new InvalidDataException("A tensor is too big.");
                if (t.Size[i] > 0) far = checked(far + (t.Size[i] - 1) * t.Stride[i]);
            }
            return n == 0 ? 0 : checked((far + 1) * ElementSize(t.Dtype));
        }
        catch (OverflowException) { throw new InvalidDataException("Bad tensor layout."); }
    }

    /// <summary>The first <paramref name="want"/> bytes of an entry (it must have them); at most <paramref name="max"/>.</summary>
    private static byte[] ReadAll(ZipArchiveEntry e, long want, long max)
    {
        if (want > max) throw new InvalidDataException("A part of the file is too big.");
        long take = Math.Min(want, e.Length);
        if (take < want) throw new InvalidDataException("A tensor points outside its storage.");
        using var s = e.Open();
        var b = new byte[take];
        int at = 0;
        while (at < b.Length)
        {
            int n = s.Read(b, at, b.Length - at);
            if (n <= 0) throw new InvalidDataException("The file is cut short.");
            at += n;
        }
        return b;
    }

    private static int ElementSize(string dtype) => dtype switch
    {
        "HalfStorage" or "BFloat16Storage" => 2,
        "FloatStorage" => 4,
        "DoubleStorage" => 8,
        _ => throw new NotSupportedException("Tensors of type " + dtype + " aren't supported."),
    };

    private static float[] Materialize(byte[] raw, TensorRef t)
    {
        long n = 1;
        foreach (var s in t.Size)
        {
            if (s < 0) throw new InvalidDataException("Bad tensor size.");
            n *= s;
            if (n > MaxElements) throw new InvalidDataException("A tensor is too big.");
        }
        int es = ElementSize(t.Dtype);
        var o = new float[n];
        if (n == 0) return o;
        // Contiguous (the usual case): one straight run of the storage.
        bool contiguous = true;
        long expect = 1;
        for (int d = t.Size.Length - 1; d >= 0; d--)
        {
            if (t.Size[d] != 1 && t.Stride[d] != expect) { contiguous = false; break; }
            expect *= t.Size[d];
        }
        if (contiguous)
        {
            long start = t.Offset * es, bytes = n * es;
            if (start < 0 || start + bytes > raw.LongLength) throw new InvalidDataException("A tensor points outside its storage.");
            var src = raw.AsSpan((int)start, (int)bytes);
            switch (t.Dtype)
            {
                case "HalfStorage":
                    var h = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(src);
                    for (int i = 0; i < h.Length; i++) o[i] = (float)h[i];
                    break;
                case "BFloat16Storage":
                    var u = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(src);
                    for (int i = 0; i < u.Length; i++) o[i] = BitConverter.Int32BitsToSingle(u[i] << 16);
                    break;
                case "FloatStorage":
                    System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(src).CopyTo(o);
                    break;
                default:
                    var dd = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(src);
                    for (int i = 0; i < dd.Length; i++) o[i] = (float)dd[i];
                    break;
            }
            return o;
        }
        var idx = new long[t.Size.Length];
        for (long i = 0; i < n; i++)
        {
            long off = t.Offset;
            for (int d = 0; d < idx.Length; d++) off += idx[d] * t.Stride[d];
            long at = off * es;
            if (at < 0 || at + es > raw.LongLength) throw new InvalidDataException("A tensor points outside its storage.");
            o[i] = t.Dtype switch
            {
                "HalfStorage" => (float)BitConverter.ToHalf(raw, (int)at),
                "BFloat16Storage" => BitConverter.Int32BitsToSingle(BitConverter.ToUInt16(raw, (int)at) << 16),
                "FloatStorage" => BitConverter.ToSingle(raw, (int)at),
                _ => (float)BitConverter.ToDouble(raw, (int)at),
            };
            for (int d = idx.Length - 1; d >= 0; d--) { if (++idx[d] < t.Size[d]) break; idx[d] = 0; }
        }
        return o;
    }

    // ---------------- pickle (protocols 2-5, the subset torch.save uses) ----------------

    public static object? Unpickle(byte[] b)
    {
        var stack = new List<object?>();
        var memo = new Dictionary<long, object?>();
        int ops = 0;
        int p = 0;
        object? Pop()
        {
            if (stack.Count == 0) throw new InvalidDataException("Damaged pickle (empty stack).");
            var v = stack[^1]; stack.RemoveAt(stack.Count - 1); return v;
        }
        object? Top() => stack.Count > 0 ? stack[^1] : throw new InvalidDataException("Damaged pickle (empty stack).");
        List<object?> PopMark()
        {
            int i = stack.FindLastIndex(x => x is Mark);
            if (i < 0) throw new InvalidDataException("Damaged pickle (no mark).");
            var items = stack.GetRange(i + 1, stack.Count - i - 1);
            stack.RemoveRange(i, stack.Count - i);
            return items;
        }
        void Need(int n) { if (p + n > b.Length || n < 0) throw new InvalidDataException("Damaged pickle (cut short)."); }
        string ReadLine()
        {
            int s = p;
            while (p < b.Length && b[p] != '\n') p++;
            Need(1);
            var r = Encoding.ASCII.GetString(b, s, p - s);
            p++;
            return r;
        }
        while (true)
        {
            Need(1);
            byte op = b[p++];
            if (++ops > MaxObjects || stack.Count > MaxStack || memo.Count > MaxMemo) throw new InvalidDataException("Damaged pickle (too many objects).");
            switch (op)
            {
                case 0x80: Need(1); p++; break;                                           // PROTO
                case 0x95: Need(8); p += 8; break;                                        // FRAME
                case (byte)'}': stack.Add(new Dictionary<object, object?>()); break;
                case (byte)']': stack.Add(new List<object?>()); break;
                case (byte)')': stack.Add(Array.Empty<object?>()); break;
                case (byte)'(': stack.Add(new Mark()); break;
                case (byte)'X': { Need(4); int l = BitConverter.ToInt32(b, p); p += 4; Need(l); stack.Add(Encoding.UTF8.GetString(b, p, l)); p += l; break; }
                case 0x8c: { Need(1); int l = b[p++]; Need(l); stack.Add(Encoding.UTF8.GetString(b, p, l)); p += l; break; }
                case 0x8d: { Need(8); long l = BitConverter.ToInt64(b, p); p += 8; Need((int)l); stack.Add(Encoding.UTF8.GetString(b, p, (int)l)); p += (int)l; break; }
                case (byte)'U': { Need(1); int l = b[p++]; Need(l); stack.Add(Encoding.Latin1.GetString(b, p, l)); p += l; break; }
                case (byte)'T': { Need(4); int l = BitConverter.ToInt32(b, p); p += 4; Need(l); stack.Add(Encoding.Latin1.GetString(b, p, l)); p += l; break; }
                case (byte)'C': { Need(1); int l = b[p++]; Need(l); stack.Add(b.AsSpan(p, l).ToArray()); p += l; break; }
                case (byte)'B': { Need(4); int l = BitConverter.ToInt32(b, p); p += 4; Need(l); stack.Add(b.AsSpan(p, l).ToArray()); p += l; break; }
                case (byte)'q': Need(1); memo[b[p++]] = Top(); break;                     // BINPUT
                case (byte)'r': Need(4); memo[BitConverter.ToUInt32(b, p)] = Top(); p += 4; break;
                case 0x94: memo[memo.Count] = Top(); break;                              // MEMOIZE
                case (byte)'h': { Need(1); stack.Add(memo.TryGetValue(b[p++], out var v) ? v : throw new InvalidDataException("Damaged pickle (memo).")); break; }
                case (byte)'j': { Need(4); stack.Add(memo.TryGetValue(BitConverter.ToUInt32(b, p), out var v) ? v : throw new InvalidDataException("Damaged pickle (memo).")); p += 4; break; }
                case (byte)'c': { var mod = ReadLine(); var name = ReadLine(); stack.Add(new Global { Name = mod + "." + name }); break; }
                case 0x93: { var name = Pop() as string; var mod = Pop() as string; stack.Add(new Global { Name = mod + "." + name }); break; }
                case (byte)'K': Need(1); stack.Add((long)b[p++]); break;
                case (byte)'M': Need(2); stack.Add((long)BitConverter.ToUInt16(b, p)); p += 2; break;
                case (byte)'J': Need(4); stack.Add((long)BitConverter.ToInt32(b, p)); p += 4; break;
                case 0x8a:
                {
                    Need(1); int l = b[p++]; Need(l);
                    if (l > 8) throw new NotSupportedException("Integer too long.");
                    long v = 0;
                    for (int i = 0; i < l; i++) v |= (long)b[p + i] << (8 * i);
                    if (l > 0 && l < 8 && (b[p + l - 1] & 0x80) != 0) v -= 1L << (8 * l);
                    p += l; stack.Add(v); break;
                }
                case (byte)'G': { Need(8); var t = b.AsSpan(p, 8).ToArray(); Array.Reverse(t); stack.Add(BitConverter.ToDouble(t)); p += 8; break; }
                case 0x88: stack.Add(true); break;
                case 0x89: stack.Add(false); break;
                case (byte)'N': stack.Add(null); break;
                case 0x85: stack.Add(new object?[] { Pop() }); break;
                case 0x86: { var y = Pop(); var x = Pop(); stack.Add(new object?[] { x, y }); break; }
                case 0x87: { var z = Pop(); var y = Pop(); var x = Pop(); stack.Add(new object?[] { x, y, z }); break; }
                case (byte)'t': stack.Add(PopMark().ToArray()); break;
                case (byte)'l': stack.Add(PopMark()); break;
                case (byte)'a': { var v = Pop(); (Top() as List<object?> ?? throw new InvalidDataException("Damaged pickle (append).")).Add(v); break; }
                case (byte)'e': { var items = PopMark(); (Top() as List<object?> ?? throw new InvalidDataException("Damaged pickle (appends).")).AddRange(items); break; }
                case (byte)'s': { var v = Pop(); var k = Pop() ?? "None"; SetItem(Top(), k, v); break; }
                case (byte)'u':
                {
                    var items = PopMark();
                    var dct = Top() as Dictionary<object, object?> ?? throw new InvalidDataException("Damaged pickle (setitems).");
                    for (int i = 0; i + 1 < items.Count; i += 2) dct[items[i] ?? "None"] = items[i + 1];
                    break;
                }
                case (byte)'Q':                                                           // BINPERSID: ('storage', type, key, location, numel)
                {
                    var pid = Pop() as object?[] ?? throw new InvalidDataException("Damaged pickle (persistent id).");
                    if (pid.Length < 3) throw new InvalidDataException("Damaged pickle (persistent id).");
                    stack.Add(new TensorRef { Dtype = (pid[1] as Global)?.Name.Split('.').Last() ?? "", Storage = pid[2]?.ToString() ?? "" });
                    break;
                }
                case (byte)'R':                                                           // REDUCE
                {
                    var args = Pop() as object?[] ?? Array.Empty<object?>();
                    var f = Pop();
                    string fn = (f as Global)?.Name ?? "";
                    if ((fn == "torch._utils._rebuild_tensor_v2" || fn == "torch._utils._rebuild_tensor") && args.Length >= 4 && args[0] is TensorRef st)
                        stack.Add(new TensorRef
                        {
                            Storage = st.Storage, Dtype = st.Dtype, Offset = args[1] as long? ?? 0,
                            Size = Longs(args[2]), Stride = Longs(args[3]),
                        });
                    else if (fn == "torch._utils._rebuild_parameter" && args.Length >= 1) stack.Add(args[0]);
                    else if (fn == "collections.OrderedDict") stack.Add(new Dictionary<object, object?>());
                    else stack.Add(new Opaque { Cls = f, Args = args });
                    break;
                }
                case 0x81: { var args = Pop(); var cls = Pop(); stack.Add(new Opaque { Cls = cls, Args = args }); break; } // NEWOBJ
                case 0x92: { Pop(); var args = Pop(); var cls = Pop(); stack.Add(new Opaque { Cls = cls, Args = args }); break; } // NEWOBJ_EX
                case (byte)'b': { var state = Pop(); if (Top() is Opaque o) o.State = state; break; }                         // BUILD
                case 0x8f: stack.Add(new List<object?>()); break;                                                             // EMPTY_SET
                case 0x90: { var items = PopMark(); (Top() as List<object?>)?.AddRange(items); break; }                         // ADDITEMS
                case 0x91: stack.Add(PopMark()); break;                                                                       // FROZENSET
                case (byte)'0': Pop(); break;                                                                                 // POP
                case (byte)'2': stack.Add(Top()); break;                                                                      // DUP
                case (byte)'.': return Pop();
                default: throw new NotSupportedException($"This file uses something EasyVocal can't read (pickle opcode 0x{op:x2}).");
            }
        }
    }

    private static void SetItem(object? target, object key, object? value)
    {
        if (target is Dictionary<object, object?> d) d[key] = value;
    }

    private static long[] Longs(object? o) => o switch
    {
        object?[] a => a.Select(x => x as long? ?? throw new InvalidDataException("Bad tensor shape.")).ToArray(),
        List<object?> l => l.Select(x => x as long? ?? throw new InvalidDataException("Bad tensor shape.")).ToArray(),
        _ => throw new InvalidDataException("Bad tensor shape."),
    };
}
