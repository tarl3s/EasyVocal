namespace EasyVocal.Core.Model;

public static class BuiltInVoices
{
    /// <summary>The clean voice. It can never be hidden: it is what every missing or hidden active voice falls back to.</summary>
    public const string NormalId = "normal";

    private static VoicePreset V(string id, string name, string emoji, VoiceSliders sliders) => new()
    {
        Id = id, Name = name, Emoji = emoji, BuiltIn = true, Sliders = sliders,
    };

    /// <summary>
    /// Fresh copies of the built-in catalog (ids are persisted: never rename). Each voice has its own signature
    /// (README "Voices"), checked by measurement in VoiceCharacterTests: every pair is clearly apart.
    /// </summary>
    public static IReadOnlyList<VoicePreset> All() => new[]
    {
        V(NormalId, "Normal", "🙂", new()),
        // Pitch and body tuned apart, like real voices: up ~4.5 semitones with a smaller body and less chest...
        V("female", "Female", "👩", new() { Pitch = 0.375, Body = 0.5, Bass = -0.35, Treble = 0.15 }),
        // ...or down 4 semitones with a bigger body and more chest.
        V("male", "Male", "👨", new() { Pitch = -0.333, Body = -0.48, Bass = 0.35, Treble = -0.1 }),
        // The real voice flattened to one note, ring-modulated through a metallic comb.
        V("robot", "Robot", "🤖", new() { Robot = 0.8, Monotone = 0.9, Echo = 0.1, Distortion = 0.1 }),
        V("chipmunk", "Chipmunk", "🐿️", new() { Pitch = 0.8, Body = 0.6, Bass = -0.4, Treble = 0.2 }),
        // Rough and rattling (growl), deep and full of bass, almost no room.
        V("monster", "Monster", "👹", new() { Pitch = -0.6, Body = -0.6, Growl = 1, Bass = 0.45, Distortion = 0.1, Room = 0.12 }),
        // Wavering pitch, a little ring modulation and a spacey echo.
        V("alien", "Alien", "👽", new() { Pitch = 0.3, Body = -0.3, Robot = 0.25, Wobble = 0.8, Echo = 0.3, Treble = 0.3 }),
        V("radio", "Radio", "📻", new() { Radio = 0.9, Distortion = 0.2 }),
        // The voice plus a second one an octave below it, distorted, in a dark cave.
        V("demon", "Demon", "😈", new() { Pitch = -0.45, Body = -0.7, Octave = 1, Distortion = 0.3, Room = 0.4, Bass = 0.1, Treble = -0.5 }),
        // Your own voice with echoes off rock walls.
        V("cave", "Cave", "🕳️", new() { Echo = 0.7, Room = 0.45 }),
        // A bit deeper but a small, gruff body.
        V("dwarf", "Dwarf", "⛏️", new() { Pitch = -0.3, Body = 0.45, Growl = 0.12, Treble = 0.2 }),
        // A huge body, moderately deeper, in an enormous hall; clean (no distortion).
        V("giant", "Giant", "🗿", new() { Pitch = -0.4, Body = -1, Room = 0.95, Bass = 0.5 }),
        // A wavering whisper floating in an airy room.
        V("ghost", "Ghost", "👻", new() { Pitch = 0.15, Whisper = 0.75, Room = 0.6, Wobble = 0.35, Echo = 0.2, Bass = -0.6, Treble = 0.4 }),
        // A vocoder: a synthesizer buzz at one pitch says your words (no delay: no pitch shift).
        V("movie-robot", "Movie robot", "🦾", new() { Vocoder = 1, Robot = 0.15, Radio = 0.2, Echo = 0.15, Distortion = 0.1 }),
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
