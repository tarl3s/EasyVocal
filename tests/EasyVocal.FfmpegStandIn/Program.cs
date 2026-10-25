using System.Buffers.Binary;

// Stands in for ffmpeg in DecoderTests. It takes ffmpeg's arguments, reads the input file (after "-i file:") as an
// instruction and answers on stdout in f32le, like `ffmpeg … -f f32le pipe:1`:
//   tone <seconds>   a 440 Hz tone at half scale, 48 kHz mono, exit 0
//   partial-fail     a tenth of a second of tone, then exit 3
//   empty            nothing, exit 0
//   hang             writes its process id to "<input>.pid", a little tone, then waits two minutes
// Without "-i file:<path>" it exits 9.
int i = Array.IndexOf(args, "-i");
if (i < 0 || i + 1 >= args.Length || !args[i + 1].StartsWith("file:", StringComparison.Ordinal)) return 9;
string input = args[i + 1]["file:".Length..];
var words = File.ReadAllText(input).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
using var stdout = Console.OpenStandardOutput();

void Tone(double seconds)
{
    int n = (int)(48000 * seconds);
    var buffer = new byte[4];
    for (int k = 0; k < n; k++)
    {
        BinaryPrimitives.WriteSingleLittleEndian(buffer, (float)(0.5 * Math.Sin(2 * Math.PI * 440 * k / 48000.0)));
        stdout.Write(buffer);
    }
    stdout.Flush();
}

switch (words.FirstOrDefault())
{
    case "tone":
        Tone(double.Parse(words[1], System.Globalization.CultureInfo.InvariantCulture));
        return 0;
    case "partial-fail":
        Tone(0.1);
        return 3;
    case "empty":
        return 0;
    case "hang":
        File.WriteAllText(input + ".pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Tone(0.1);
        Thread.Sleep(TimeSpan.FromMinutes(2));
        return 0;
    default:
        return 8;
}
