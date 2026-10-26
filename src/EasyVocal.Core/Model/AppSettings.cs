using System.Text.Json.Serialization;

namespace EasyVocal.Core.Model;

public enum VoiceKind { Classic, Ai }

public class VoiceSliders
{
    /// <summary>-1..1, mapped to -12..+12 semitones.</summary>
    public double Pitch { get; set; }
    /// <summary>-1..1.</summary>
    public double Body { get; set; }
    public double Robot { get; set; }
    public double Echo { get; set; }
    public double Radio { get; set; }
    public double Distortion { get; set; }
    public double Whisper { get; set; }
    /// <summary>0..1, the filter editor's "Growl": a rough, rattling throat.</summary>
    public double Growl { get; set; }
    /// <summary>0..1, the filter editor's "Big room": a reverb from a small room to a huge hall.</summary>
    public double Room { get; set; }

    // Not in the filter editor: they give the built-in voices their character (a copy of a built-in keeps them).
    // Missing in an older settings.json = 0 = not used.

    /// <summary>0..1: a second voice an octave below the shifted one is mixed in (Demon).</summary>
    public double Octave { get; set; }
    /// <summary>-1..1: less or more chest (low shelf).</summary>
    public double Bass { get; set; }
    /// <summary>-1..1: darker or airier (high shelf).</summary>
    public double Treble { get; set; }
    /// <summary>0..1: the pitch sways up and down.</summary>
    public double Wobble { get; set; }
    /// <summary>0..1: the melody is flattened towards one note.</summary>
    public double Monotone { get; set; }
    /// <summary>0..1: the voice becomes a synthesizer buzz at one pitch that says the words (vocoder).</summary>
    public double Vocoder { get; set; }

    private static double C(double v, double lo, double hi) =>
        double.IsNaN(v) ? 0 : Math.Min(hi, Math.Max(lo, v));

    public VoiceSliders Clamp() => new()
    {
        Pitch = C(Pitch, -1, 1),
        Body = C(Body, -1, 1),
        Robot = C(Robot, 0, 1),
        Echo = C(Echo, 0, 1),
        Radio = C(Radio, 0, 1),
        Distortion = C(Distortion, 0, 1),
        Whisper = C(Whisper, 0, 1),
        Growl = C(Growl, 0, 1),
        Room = C(Room, 0, 1),
        Octave = C(Octave, 0, 1),
        Bass = C(Bass, -1, 1),
        Treble = C(Treble, -1, 1),
        Wobble = C(Wobble, 0, 1),
        Monotone = C(Monotone, 0, 1),
        Vocoder = C(Vocoder, 0, 1),
    };

    public VoiceSliders Clone() => (VoiceSliders)MemberwiseClone();

    [JsonIgnore]
    public bool IsNeutral =>
        Pitch == 0 && Body == 0 && Robot == 0 && Echo == 0 && Radio == 0 && Distortion == 0 && Whisper == 0 &&
        Growl == 0 && Room == 0 && Octave == 0 && Bass == 0 && Treble == 0 && Wobble == 0 && Monotone == 0 && Vocoder == 0;

    /// <summary>The pitch shifter (and its fixed delay) is needed: Pitch, Voice body, Octave, Wobble or Monotone is used.</summary>
    [JsonIgnore]
    public bool Shifts => Pitch != 0 || Body != 0 || Octave != 0 || Wobble != 0 || Monotone != 0;
}

public class VoicePreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Emoji { get; set; } = "🙂";
    public bool BuiltIn { get; set; }
    public bool Hidden { get; set; }
    public string? Hotkey { get; set; }
    public VoiceKind Kind { get; set; } = VoiceKind.Classic;
    /// <summary>An AI voice (<see cref="VoiceKind.Ai"/>): the id of its folder in the AI pack's voices. Null on classic voices.</summary>
    public string? AiVoiceId { get; set; }
    /// <summary>An AI voice's own quality ("light", "balanced", "max"); null = the one in <see cref="AiSettings.Quality"/>.</summary>
    public string? AiQuality { get; set; }
    public VoiceSliders Sliders { get; set; } = new();
}

/// <summary>How the AI voices run on this computer. Missing in an older settings.json = defaults.</summary>
public class AiSettings
{
    /// <summary>"auto" (the benchmark's suggestion, else "balanced"), "light", "balanced" or "max".</summary>
    public string Quality { get; set; } = "auto";
    /// <summary>"auto" (the processor, or the GPU once the benchmark found it better), "cpu" or "gpu".</summary>
    public string Device { get; set; } = "auto";
    /// <summary>What the one-time benchmark suggested ("light", "balanced", "max"); null = not measured yet.</summary>
    public string? SuggestedQuality { get; set; }
    /// <summary>The device the benchmark found best ("cpu" or "gpu"); null = not measured yet.</summary>
    public string? SuggestedDevice { get; set; }
    /// <summary>When the benchmark ran (UTC); null = never.</summary>
    public DateTime? BenchmarkedUtc { get; set; }
    /// <summary>Per level the time one block took (95th percentile, ms) when measured; for the prime of the stream.</summary>
    public Dictionary<string, double> MeasuredBlockMs { get; set; } = new();
    /// <summary>Hear me plays the AI voice itself (true) or the person's own voice as spoken (false: no delay to talk over).</summary>
    public bool HearConvertedVoice { get; set; }
}

public class SoundItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Emoji { get; set; } = "🔊";
    /// <summary>Relative to the Sounds folder.</summary>
    public string FileName { get; set; } = "";
    /// <summary>
    /// Set once the sound was changed in the sound editor: <see cref="FileName"/> is then the edited copy and this
    /// is the file it came from (never modified), which "Back to original" goes back to. Null = never edited.
    /// </summary>
    public string? OriginalFileName { get; set; }
    public double Volume { get; set; } = 0.8;
    public string? Hotkey { get; set; }
    public bool BuiltIn { get; set; }
    public bool Hidden { get; set; }
}

public class AppSettings
{
    public int Version { get; set; } = 1;
    public string? MicDeviceId { get; set; }
    /// <summary>null = auto (CABLE Input).</summary>
    public string? OutputDeviceId { get; set; }
    /// <summary>null = Windows default.</summary>
    public string? MonitorDeviceId { get; set; }
    public bool Enabled { get; set; } = true;
    public bool MicMuted { get; set; }
    public bool HearMe { get; set; }
    public bool HearSounds { get; set; } = true;
    /// <summary>The soundboard goes through the voice filter too (switching voices changes a sound that is playing).</summary>
    public bool SoundsUseVoice { get; set; }
    public string ActiveVoiceId { get; set; } = BuiltInVoices.NormalId;
    public bool StartWithWindows { get; set; }
    public bool ConfirmQuit { get; set; } = true;
    public string Language { get; set; } = "auto";
    public bool SetupDone { get; set; }
    /// <summary>The "keeps running in the tray" tip was shown once.</summary>
    public bool TrayTipShown { get; set; }
    /// <summary>The "Add EasyVocal to the Start menu?" question was answered (it is asked once).</summary>
    public bool StartMenuAsked { get; set; }
    /// <summary>The person wants the Start menu shortcut (it is kept pointing at this EasyVocal.exe).</summary>
    public bool StartMenuShortcut { get; set; }
    /// <summary>Look for a newer release on GitHub each time EasyVocal opens.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>The release version (e.g. "0.3.0") the person chose to skip: it is not offered again when EasyVocal opens.</summary>
    public string? SkippedUpdateVersion { get; set; }
    /// <summary>
    /// Windows' default playback device when the VB-CABLE install started (VB-CABLE may take its place): put back
    /// once the cable shows up, then forgotten. Null = nothing to put back.
    /// </summary>
    public string? PreCableOutputId { get; set; }
    /// <summary>The same for Windows' default microphone.</summary>
    public string? PreCableInputId { get; set; }
    public List<VoicePreset> Voices { get; set; } = new();
    public List<SoundItem> Sounds { get; set; } = new();
    /// <summary>Keys: the ids of <see cref="Hotkeys.HotkeyMap.General"/> ("toggleFilter", "stopSounds", "muteMic").</summary>
    public Dictionary<string, string> GeneralHotkeys { get; set; } = new();
    /// <summary>Where each window was last (keys: "main", "filterEditor", "soundEditor", "audioEditor", "settings", "setup").</summary>
    public Dictionary<string, WindowBox> WindowPlacements { get; set; } = new();
    /// <summary>The AI voices' engine settings (quality, device, the benchmark's suggestion).</summary>
    public AiSettings Ai { get; set; } = new();
}
