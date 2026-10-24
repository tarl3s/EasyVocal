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
    };

    public VoiceSliders Clone() => (VoiceSliders)MemberwiseClone();

    public bool IsNeutral =>
        Pitch == 0 && Body == 0 && Robot == 0 && Echo == 0 && Radio == 0 && Distortion == 0 && Whisper == 0;
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
    public VoiceSliders Sliders { get; set; } = new();
}

public class SoundItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Emoji { get; set; } = "🔊";
    /// <summary>Relative to the Sounds folder.</summary>
    public string FileName { get; set; } = "";
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
    public string ActiveVoiceId { get; set; } = "normal";
    public bool StartWithWindows { get; set; }
    public bool ConfirmQuit { get; set; } = true;
    public string Language { get; set; } = "auto";
    public bool SetupDone { get; set; }
    public List<VoicePreset> Voices { get; set; } = new();
    public List<SoundItem> Sounds { get; set; } = new();
    /// <summary>Keys: "toggleFilter", "stopSounds", "muteMic".</summary>
    public Dictionary<string, string> GeneralHotkeys { get; set; } = new();
}
