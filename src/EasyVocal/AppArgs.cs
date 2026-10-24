using System.IO;

namespace EasyVocal;

/// <summary>The command line: <c>--data &lt;folder&gt;</c>, <c>--smoketest</c>, <c>--tray</c>.</summary>
internal sealed class AppArgs
{
    /// <summary>The folder given with <c>--data</c> (full path), or null for the normal one.</summary>
    public string? DataFolder { get; private init; }

    public bool SmokeTest { get; private init; }

    public bool StartHidden { get; private init; }

    /// <summary>
    /// The smoke test adds and removes items: it may only run on a scratch folder given with <c>--data</c>, never on
    /// the person's real settings. When this is false the app must refuse to start (exit code 2).
    /// </summary>
    public bool Allowed => !SmokeTest || DataFolder != null;

    public static AppArgs Parse(IReadOnlyList<string> args)
    {
        string? data = null;
        bool smoke = false, hidden = false;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--data" && i + 1 < args.Count) data = Path.GetFullPath(args[++i]);
            else if (args[i] == "--smoketest") smoke = true;
            else if (args[i] == "--tray") hidden = true;
        }
        return new AppArgs { DataFolder = data, SmokeTest = smoke, StartHidden = hidden };
    }
}
