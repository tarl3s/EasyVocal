using System;
using System.Collections.Generic;
using System.IO;

namespace EasyVocal.Tests.Helpers;

/// <summary>
/// Writes a real (if big) FLAC file: 16-bit mono at 44.1 kHz in "verbatim" frames, the simplest valid FLAC.
/// Windows has a FLAC decoder but not always an encoder, so the tests make their own file.
/// </summary>
public static class MiniFlac
{
    private const int Rate = 44100;
    private const int Block = 4096;

    public static void Write(string path, float[] samples)
    {
        var o = new List<byte>();
        o.AddRange("fLaC"u8.ToArray());
        // STREAMINFO, the last metadata block, 34 bytes.
        o.Add(0x80);
        o.AddRange(new byte[] { 0, 0, 34 });
        var info = new BitWriter();
        info.Put(Block, 16); info.Put(Block, 16);
        info.Put(0, 24); info.Put(0, 24);
        info.Put(Rate, 20); info.Put(0, 3); info.Put(15, 5);
        info.Put((long)samples.Length, 36);
        for (int i = 0; i < 16; i++) info.Put(0, 8); // no MD5
        o.AddRange(info.Bytes);

        int frame = 0;
        for (int start = 0; start < samples.Length; start += Block, frame++)
        {
            int n = Math.Min(Block, samples.Length - start);
            var f = new List<byte>
            {
                0xFF, 0xF8,                 // sync, fixed block size
                0x79,                       // block size: 16 bits at the end of the header; 44.1 kHz
                0x08,                       // mono, 16 bits per sample
            };
            if (frame >= 128) throw new ArgumentException("Too long for this little writer.");
            f.Add((byte)frame);
            f.Add((byte)((n - 1) >> 8));
            f.Add((byte)(n - 1));
            f.Add(Crc8(f));
            f.Add(0x02);                    // subframe: verbatim
            for (int i = 0; i < n; i++)
            {
                short s = (short)Math.Round(Math.Clamp(samples[start + i], -1f, 1f) * 32767f);
                f.Add((byte)(s >> 8));
                f.Add((byte)s);
            }
            ushort crc = Crc16(f);
            f.Add((byte)(crc >> 8));
            f.Add((byte)crc);
            o.AddRange(f);
        }
        File.WriteAllBytes(path, o.ToArray());
    }

    private static byte Crc8(List<byte> data)
    {
        int crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
        }
        return (byte)crc;
    }

    private static ushort Crc16(List<byte> data)
    {
        int crc = 0;
        foreach (var b in data)
        {
            crc ^= b << 8;
            for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
        }
        return (ushort)crc;
    }

    private sealed class BitWriter
    {
        private readonly List<byte> bytes = new();
        private int acc, count;

        public byte[] Bytes => bytes.ToArray();

        public void Put(long value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                acc = (acc << 1) | (int)((value >> i) & 1);
                if (++count == 8) { bytes.Add((byte)acc); acc = 0; count = 0; }
            }
        }
    }
}
