using System.Globalization;
using EasyVocal.Packager;

// EasyVocal.Packager <rid> <publish folder> <output folder> <version> <icon PNG folder>
// Packs one `dotnet publish -r <rid>` folder into its release download and prints "<file>|<bytes>|<note>".
if (args.Length != 5 || !Release.Rids.Contains(args[0]))
{
    Console.Error.WriteLine("usage: EasyVocal.Packager <" + string.Join("|", Release.Rids) + "> <publish folder> <output folder> <version> <icon PNG folder>");
    return 2;
}
try
{
    var pngs = Directory.GetFiles(args[4], "*.png").Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToList();
    var packed = Release.Pack(args[0], args[1], args[2], args[3], pngs);
    Console.WriteLine(string.Join("|", packed.Archive, packed.Bytes.ToString(CultureInfo.InvariantCulture), packed.Note));
    return 0;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("EasyVocal.Packager: " + ex.Message);
    return 1;
}
