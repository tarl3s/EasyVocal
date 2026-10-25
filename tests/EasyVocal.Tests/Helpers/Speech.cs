using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using EasyVocal.Core.Dsp;

namespace EasyVocal.Tests.Helpers;

/// <summary>
/// Real speech to measure the voices on, made once per test run with Windows' own text-to-speech (System.Speech,
/// through Windows PowerShell: nothing to install or download): an English male, an English female and a Brazilian
/// Portuguese voice when they are installed, each saying a sentence, with short pauses. Without any of them (or
/// without PowerShell) a synthetic vowel stands in, gliding in pitch like speech does.
/// </summary>
public static class Speech
{
    private const int Sr = AudioFormat.SampleRate;

    /// <summary>One spoken line: which installed voice (culture + gender) says what.</summary>
    private sealed record Line(string Culture, string Gender, string Text);

    private static readonly Line[] Lines =
    {
        new("en-US", "Male", "Hello there! How are you doing today? Let's go and win this game."),
        new("en-US", "Female", "Welcome back, everyone. I think we can do this together."),
        new("pt-BR", "Female", "Olá, pessoal! Tudo bem com vocês? Vamos jogar mais uma partida."),
    };

    private static readonly Lazy<(float[] Signal, string Source)> Cached = new(Make);

    /// <summary>The speech (mono 48 kHz, peak about 0.5) followed by 1.5 s of silence for the tails.</summary>
    public static float[] Signal => Cached.Value.Signal;

    /// <summary>"tts: …" with the voices used, or "synthetic" when text-to-speech wasn't available.</summary>
    public static string Source => Cached.Value.Source;

    /// <summary>Seconds of silence after the last word.</summary>
    public const double TailSeconds = 1.5;

    private static (float[], string) Make()
    {
        var parts = new List<float[]>();
        var used = new List<string>();
        string dir = Path.Combine(Path.GetTempPath(), "EV_tts_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var names = Synthesize(dir);
            for (int i = 0; i < Lines.Length; i++)
            {
                string wav = Path.Combine(dir, "line" + i + ".wav");
                if (!File.Exists(wav) || new FileInfo(wav).Length < 10000) continue;
                parts.Add(Trim(WavFile.ReadMono(wav)));
                used.Add(names.TryGetValue(i, out var n) ? n : "?");
            }
        }
        catch (Exception)
        {
            // no PowerShell / System.Speech: fall back below
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* best effort */ }
        }

        string source = "tts: " + string.Join(", ", used);
        if (parts.Count == 0)
        {
            parts.Add(Synthetic());
            source = "synthetic";
        }

        var all = new List<float>();
        foreach (var p in parts)
        {
            all.AddRange(new float[Sr * 3 / 10]);
            all.AddRange(p);
        }
        all.AddRange(new float[(int)(TailSeconds * Sr)]);
        var x = all.ToArray();
        Normalize(x, 0.5f);
        return (x, source);
    }

    /// <summary>Runs System.Speech in Windows PowerShell; returns line index → the voice that said it.</summary>
    private static Dictionary<int, string> Synthesize(string dir)
    {
        var ps = new StringBuilder();
        ps.AppendLine("$ErrorActionPreference = 'Stop'");
        ps.AppendLine("Add-Type -AssemblyName System.Speech");
        ps.AppendLine("$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(48000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)");
        ps.AppendLine("$voices = (New-Object System.Speech.Synthesis.SpeechSynthesizer).GetInstalledVoices() | Where-Object { $_.Enabled } | ForEach-Object { $_.VoiceInfo }");
        for (int i = 0; i < Lines.Length; i++)
        {
            var l = Lines[i];
            string text = l.Text.Replace("'", "''");
            ps.AppendLine($"$v = $voices | Where-Object {{ $_.Culture.Name -eq '{l.Culture}' -and $_.Gender -eq '{l.Gender}' }} | Select-Object -First 1");
            ps.AppendLine("if ($v) {");
            ps.AppendLine("  $s = New-Object System.Speech.Synthesis.SpeechSynthesizer");
            ps.AppendLine("  $s.SelectVoice($v.Name)");
            ps.AppendLine($"  $s.SetOutputToWaveFile('{Path.Combine(dir, "line" + i + ".wav")}', $fmt)");
            ps.AppendLine($"  $s.Speak('{text}')");
            ps.AppendLine("  $s.Dispose()");
            ps.AppendLine($"  Write-Output ('{i}|' + $v.Name)");
            ps.AppendLine("}");
        }
        string script = Path.Combine(dir, "speak.ps1");
        File.WriteAllText(script, ps.ToString(), new UTF8Encoding(true)); // BOM: Windows PowerShell reads the accents right

        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var names = new Dictionary<int, string>();
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60000)) { try { p.Kill(); } catch (Exception) { } }
        foreach (var line in output.Split('\n'))
        {
            var bits = line.Trim().Split('|');
            if (bits.Length == 2 && int.TryParse(bits[0], out int i)) names[i] = bits[1];
        }
        return names;
    }

    /// <summary>Cuts the near-silence TTS leaves at both ends.</summary>
    private static float[] Trim(float[] x)
    {
        int a = 0, b = x.Length;
        while (a < b && Math.Abs(x[a]) < 1e-3f) a++;
        while (b > a && Math.Abs(x[b - 1]) < 1e-3f) b--;
        var o = new float[b - a];
        Array.Copy(x, a, o, 0, o.Length);
        return o;
    }

    private static void Normalize(float[] x, float peak)
    {
        float m = 0;
        foreach (float v in x) m = Math.Max(m, Math.Abs(v));
        if (m <= 0) return;
        for (int i = 0; i < x.Length; i++) x[i] *= peak / m;
    }

    /// <summary>"Syllables" of a vowel whose pitch glides between 110 and 190 Hz, with pauses.</summary>
    private static float[] Synthetic()
    {
        var all = new List<float>();
        double[][] vowels = { new[] { 700.0, 1200, 2600 }, new[] { 300.0, 2300, 3000 }, new[] { 500.0, 900, 2500 } };
        for (int s = 0; s < 9; s++)
        {
            double f0 = 110 + 80 * ((s * 37) % 9) / 8.0;
            var syllable = Signals.PulseVowel(f0, vowels[s % 3], 0.35);
            for (int n = 0; n < syllable.Length; n++)
                syllable[n] *= (float)Math.Sin(Math.PI * n / syllable.Length); // fade in and out like a syllable
            all.AddRange(syllable);
            if (s % 3 == 2) all.AddRange(new float[Sr / 5]);
        }
        return all.ToArray();
    }
}
