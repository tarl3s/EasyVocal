using System.IO;

namespace EasyVocal;

/// <summary>The command line: <c>--data &lt;folder&gt;</c>, <c>--smoketest</c>, <c>--e2e</c>, <c>--tray</c>.</summary>
internal sealed class AppArgs
{
    /// <summary>The folder given with <c>--data</c> (full path), or null for the normal one.</summary>
    public string? DataFolder { get; private init; }

    public bool SmokeTest { get; private init; }

    /// <summary><c>--e2e</c>: the end-to-end test (test audio devices, drives the real windows).</summary>
    public bool E2E { get; private init; }

    /// <summary><c>--e2e-relaunch</c>: the e2e test's second run, which checks what the first one saved.</summary>
    public bool E2ERelaunch { get; private init; }

    public bool StartHidden { get; private init; }

    /// <summary>A self-test run (smoke or e2e): no first-run setup, no single-instance hand-over, no real startup entry.</summary>
    public bool TestMode => SmokeTest || E2E;

    /// <summary>
    /// The tests add and remove items: they may only run on a scratch folder given with <c>--data</c>, never on
    /// the person's real settings. When this is false the app must refuse to start (exit code 2).
    /// </summary>
    public bool Allowed => !TestMode || DataFolder != null;

    public static AppArgs Parse(IReadOnlyList<string> args)
    {
        string? data = null;
        bool smoke = false, e2e = false, relaunch = false, hidden = false;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--data" && i + 1 < args.Count) data = Path.GetFullPath(args[++i]);
            else if (args[i] == "--smoketest") smoke = true;
            else if (args[i] == "--e2e") e2e = true;
            else if (args[i] == "--e2e-relaunch") e2e = relaunch = true;
            else if (args[i] == "--tray") hidden = true;
        }
        return new AppArgs { DataFolder = data, SmokeTest = smoke, E2E = e2e, E2ERelaunch = relaunch, StartHidden = hidden };
    }
}
