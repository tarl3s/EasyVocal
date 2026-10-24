namespace EasyVocal.Core.Model;

public static class BuiltInVoices
{
    private static VoicePreset V(string id, string name, string emoji, double pitch, double body,
        double robot, double echo, double radio, double dist, double whisper) => new()
    {
        Id = id, Name = name, Emoji = emoji, BuiltIn = true,
        Sliders = new VoiceSliders
        {
            Pitch = pitch, Body = body, Robot = robot, Echo = echo,
            Radio = radio, Distortion = dist, Whisper = whisper,
        },
    };

    /// <summary>Fresh copies of the built-in catalog (ids are persisted: never rename).</summary>
    public static IReadOnlyList<VoicePreset> All() => new[]
    {
        V("normal", "Normal", "🙂", 0, 0, 0, 0, 0, 0, 0),
        V("female", "Female", "👩", 0.42, 0.45, 0, 0, 0, 0, 0),
        V("male", "Male", "👨", -0.4, -0.4, 0, 0, 0, 0, 0),
        V("robot", "Robot", "🤖", 0, 0, 0.8, 0.1, 0, 0.1, 0),
        V("chipmunk", "Chipmunk", "🐿️", 0.8, 0.6, 0, 0, 0, 0, 0),
        V("monster", "Monster", "👹", -0.75, -0.6, 0, 0.15, 0, 0.35, 0),
        V("alien", "Alien", "👽", 0.3, -0.3, 0.5, 0.3, 0, 0, 0),
        V("radio", "Radio", "📻", 0, 0, 0, 0, 0.9, 0.2, 0),
        V("demon", "Demon", "😈", -1, -0.7, 0.2, 0.35, 0, 0.5, 0),
        V("cave", "Cave", "🕳️", 0, 0, 0, 0.8, 0, 0, 0),
        V("dwarf", "Dwarf", "⛏️", -0.25, 0.5, 0, 0, 0, 0, 0),
        V("giant", "Giant", "🗿", -0.6, -0.8, 0, 0.4, 0, 0, 0),
        V("ghost", "Ghost", "👻", 0.15, 0, 0, 0.6, 0, 0, 0.7),
        V("movie-robot", "Movie robot", "🦾", -0.2, 0, 1, 0.2, 0.3, 0.2, 0),
    };
}

public static class StarterSounds
{
    private static SoundItem S(string id, string emoji, string name) => new()
    {
        Id = id, Name = name, Emoji = emoji, FileName = id + ".wav", BuiltIn = true,
    };

    /// <summary>Metadata only; the WAVs are generated elsewhere.</summary>
    public static IReadOnlyList<SoundItem> Items() => new[]
    {
        S("airhorn", "📯", "Airhorn"),
        S("ding", "🔔", "Ding"),
        S("sad-trombone", "🎺", "Sad trombone"),
        S("drum-roll", "🥁", "Drum roll"),
        S("rimshot", "🥁", "Ba dum tss"),
        S("crickets", "🦗", "Crickets"),
        S("applause", "👏", "Applause"),
        S("boing", "🤸", "Boing"),
        S("buzzer", "❌", "Wrong buzzer"),
        S("tada", "🎉", "Ta-da"),
    };
}
