using System;
using System.IO;
using System.Linq;
using EasyVocal.Core.Model;
using EasyVocal.Core.Sounds;

namespace EasyVocal.Tests;

public class ItemOpsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EV_itemops_" + Guid.NewGuid().ToString("N"));

    public ItemOpsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static AppSettings Fresh()
    {
        var s = new AppSettings();
        s.Voices.AddRange(BuiltInVoices.All());
        s.Sounds.AddRange(StarterSounds.Items());
        return s;
    }

    private string Src(string name, string content = "data")
    {
        var folder = Path.Combine(_dir, "src");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ---------- voices ----------

    [Fact]
    public void DuplicateBuiltInVoice_MakesEditableCopyWithoutHotkey()
    {
        var s = Fresh();
        var monster = s.Voices.First(v => v.Id == "monster");
        monster.Hotkey = "Ctrl+1";
        int index = s.Voices.IndexOf(monster);

        var copy = ItemOps.DuplicateVoice(s, "monster");

        Assert.False(copy.BuiltIn);
        Assert.False(copy.Hidden);
        Assert.Null(copy.Hotkey);
        Assert.NotEqual("monster", copy.Id);
        Assert.StartsWith("Monster", copy.Name);
        Assert.NotEqual("Monster", copy.Name);
        Assert.Equal(monster.Emoji, copy.Emoji);
        Assert.Equal(monster.Sliders.Pitch, copy.Sliders.Pitch);
        Assert.NotSame(monster.Sliders, copy.Sliders); // editing the copy must not change the original
        Assert.Same(copy, s.Voices[index + 1]);         // right after the original
        Assert.Equal("Ctrl+1", monster.Hotkey);
        Assert.True(monster.BuiltIn);
    }

    [Fact]
    public void DuplicateCustomVoice_NumbersTheCopy_AndStaysUnique()
    {
        var s = Fresh();
        s.Voices.Add(new VoicePreset { Id = "mine", Name = "My filter" });

        var a = ItemOps.DuplicateVoice(s, "mine");
        var b = ItemOps.DuplicateVoice(s, "mine");

        Assert.Equal("My filter 2", a.Name);
        Assert.Equal("My filter 3", b.Name);
        Assert.Equal(s.Voices.Count, s.Voices.Select(v => v.Id).Distinct().Count());
    }

    [Fact]
    public void DuplicateUnknownVoice_Throws()
    {
        Assert.Throws<ArgumentException>(() => ItemOps.DuplicateVoice(Fresh(), "nope"));
    }

    [Fact]
    public void DeleteBuiltInVoice_HidesIt_DeleteCustom_RemovesIt()
    {
        var s = Fresh();
        var mine = ItemOps.DuplicateVoice(s, "robot");
        int count = s.Voices.Count;

        ItemOps.DeleteVoice(s, "robot");
        Assert.Equal(count, s.Voices.Count);
        Assert.True(s.Voices.First(v => v.Id == "robot").Hidden);

        ItemOps.DeleteVoice(s, mine.Id);
        Assert.Equal(count - 1, s.Voices.Count);
        Assert.DoesNotContain(s.Voices, v => v.Id == mine.Id);

        ItemOps.DeleteVoice(s, "no-such-voice"); // nothing happens
        Assert.Equal(count - 1, s.Voices.Count);
    }

    [Fact]
    public void DeleteActiveVoice_FallsBackToNormal()
    {
        var s = Fresh();
        var mine = ItemOps.DuplicateVoice(s, "alien");
        s.ActiveVoiceId = mine.Id;
        ItemOps.DeleteVoice(s, mine.Id);
        Assert.Equal("normal", s.ActiveVoiceId);

        s.ActiveVoiceId = "ghost";
        ItemOps.DeleteVoice(s, "ghost"); // hides the built-in, which was active
        Assert.Equal("normal", s.ActiveVoiceId);

        s.ActiveVoiceId = "cave";
        ItemOps.DeleteVoice(s, "robot"); // another voice: the active one stays
        Assert.Equal("cave", s.ActiveVoiceId);
    }

    [Fact]
    public void Normal_CanNeverBeHidden()
    {
        var s = Fresh();
        var normal = s.Voices.Single(v => v.Id == BuiltInVoices.NormalId);
        Assert.False(ItemOps.CanHide(normal));
        Assert.True(ItemOps.CanHide(s.Voices.Single(v => v.Id == "robot")));

        s.ActiveVoiceId = BuiltInVoices.NormalId;
        ItemOps.DeleteVoice(s, BuiltInVoices.NormalId);
        Assert.False(normal.Hidden);
        Assert.Contains(normal, s.Voices);
        Assert.Equal(BuiltInVoices.NormalId, s.ActiveVoiceId);
    }

    // ---------- sounds ----------

    [Fact]
    public void DuplicateSound_SharesTheFile_NoHotkey_InsertedAfter()
    {
        var s = Fresh();
        var ding = s.Sounds.First(x => x.Id == "ding");
        ding.Hotkey = "7";
        ding.Volume = 0.3;
        int index = s.Sounds.IndexOf(ding);

        var copy = ItemOps.DuplicateSound(s, "ding");

        Assert.False(copy.BuiltIn);
        Assert.Null(copy.Hotkey);
        Assert.NotEqual("ding", copy.Id);
        Assert.Equal(ding.FileName, copy.FileName);
        Assert.Equal(0.3, copy.Volume);
        Assert.StartsWith("Ding", copy.Name);
        Assert.NotEqual("Ding", copy.Name);
        Assert.Same(copy, s.Sounds[index + 1]);
    }

    [Fact]
    public void DeleteSound_RemovesFileOnlyWhenUnused()
    {
        var s = Fresh();
        var sounds = Path.Combine(_dir, "Sounds");
        Directory.CreateDirectory(sounds);
        File.WriteAllText(Path.Combine(sounds, "bruh.mp3"), "x");
        s.Sounds.Add(new SoundItem { Id = "a", Name = "Bruh", FileName = "bruh.mp3" });
        s.Sounds.Add(new SoundItem { Id = "b", Name = "Bruh 2", FileName = "bruh.mp3" });

        ItemOps.DeleteSound(s, "a", sounds);
        Assert.DoesNotContain(s.Sounds, x => x.Id == "a");
        Assert.True(File.Exists(Path.Combine(sounds, "bruh.mp3")), "another sound still uses the file");

        ItemOps.DeleteSound(s, "b", sounds);
        Assert.DoesNotContain(s.Sounds, x => x.Id == "b");
        Assert.False(File.Exists(Path.Combine(sounds, "bruh.mp3")), "nobody uses the file any more");
    }

    [Fact]
    public void DeleteSound_BuiltIn_OnlyHides_AndKeepsTheFile()
    {
        var s = Fresh();
        var sounds = Path.Combine(_dir, "Sounds");
        Directory.CreateDirectory(sounds);
        var file = Path.Combine(sounds, "ding.wav");
        File.WriteAllText(file, "x");

        ItemOps.DeleteSound(s, "ding", sounds);

        Assert.True(s.Sounds.First(x => x.Id == "ding").Hidden);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void DeleteSound_CopyOfBuiltIn_NeverDeletesTheBuiltInsFile()
    {
        var s = Fresh();
        var sounds = Path.Combine(_dir, "Sounds");
        Directory.CreateDirectory(sounds);
        var file = Path.Combine(sounds, "ding.wav");
        File.WriteAllText(file, "x");
        var copy = ItemOps.DuplicateSound(s, "ding");

        ItemOps.DeleteSound(s, copy.Id, sounds);

        Assert.DoesNotContain(s.Sounds, x => x.Id == copy.Id);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void DeleteSound_FileNameOutsideTheFolder_IsNeverDeleted()
    {
        var s = Fresh();
        var sounds = Path.Combine(_dir, "Sounds");
        Directory.CreateDirectory(sounds);
        var outside = Path.Combine(_dir, "precious.txt");
        File.WriteAllText(outside, "keep me");
        s.Sounds.Add(new SoundItem { Id = "evil", Name = "Evil", FileName = @"..\precious.txt" });

        ItemOps.DeleteSound(s, "evil", sounds);

        Assert.DoesNotContain(s.Sounds, x => x.Id == "evil");
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void DeleteFileIfUnused_AfterReplacingASoundsFile_DeletesTheOldOne()
    {
        var s = Fresh();
        var sounds = Path.Combine(_dir, "Sounds");
        Directory.CreateDirectory(sounds);
        File.WriteAllText(Path.Combine(sounds, "old.wav"), "x");
        File.WriteAllText(Path.Combine(sounds, "ding.wav"), "x");
        var mine = new SoundItem { Id = "m", Name = "Mine", FileName = "new.wav" };
        s.Sounds.Add(mine);

        ItemOps.DeleteFileIfUnused(s, "old.wav", sounds);
        ItemOps.DeleteFileIfUnused(s, "ding.wav", sounds); // a built-in still uses it
        ItemOps.DeleteFileIfUnused(s, "DING.WAV", sounds); // names compare ignoring case, like Windows

        Assert.False(File.Exists(Path.Combine(sounds, "old.wav")));
        Assert.True(File.Exists(Path.Combine(sounds, "ding.wav")));
    }

    [Fact]
    public void ImportSoundFile_SameNameTwice_GetsUniqueNames()
    {
        var sounds = Path.Combine(_dir, "Sounds"); // does not exist yet: the import creates it
        var first = ItemOps.ImportSoundFile(Src("bruh.mp3", "one"), sounds);
        var second = ItemOps.ImportSoundFile(Src("bruh.mp3", "two"), sounds);
        var third = ItemOps.ImportSoundFile(Src("bruh.mp3", "three"), sounds);

        Assert.Equal("bruh.mp3", first);
        Assert.Equal("bruh (2).mp3", second);
        Assert.Equal("bruh (3).mp3", third);
        Assert.Equal("one", File.ReadAllText(Path.Combine(sounds, first)));
        Assert.Equal("two", File.ReadAllText(Path.Combine(sounds, second)));
    }

    [Fact]
    public void ImportSoundFile_AlreadyInTheFolder_IsNotCopiedAgain()
    {
        var sounds = Path.Combine(_dir, "Sounds");
        Directory.CreateDirectory(sounds);
        var path = Path.Combine(sounds, "mine.ogg");
        File.WriteAllText(path, "x");

        Assert.Equal("mine.ogg", ItemOps.ImportSoundFile(path, sounds));
        Assert.Equal(new[] { path }, Directory.GetFiles(sounds));
    }

    [Fact]
    public void ImportSoundFile_ResultIsAPlainFileName()
    {
        var sounds = Path.Combine(_dir, "Sounds");
        var name = ItemOps.ImportSoundFile(Src("weird name.WAV"), sounds);
        Assert.Equal(Path.GetFileName(name), name);
        Assert.True(File.Exists(Path.Combine(sounds, name)));
    }

    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("Clip.MOV")]
    [InlineData("clip.wmv")]
    [InlineData("clip.m4v")]
    public void ImportSound_Video_KeepsOnlyItsSoundAsAWav(string video)
    {
        var sounds = Path.Combine(_dir, "Sounds");
        var decoded = Tone(4800);
        var name = ItemOps.ImportSound(Src(video, new string('v', 100_000)), sounds, decoded);
        Assert.Equal(Path.GetFileNameWithoutExtension(video) + ".wav", name);
        Assert.Equal(new[] { name }, Directory.GetFiles(sounds).Select(Path.GetFileName)); // the video isn't copied
        var back = SoundLoader.Load(Path.Combine(sounds, name));
        Assert.Equal(decoded.Length, back.Length);
        Assert.Equal(decoded[100], back[100], 1e-3f);
    }

    [Theory]
    [InlineData("bruh.mp3")]
    [InlineData("song.m4a")]
    [InlineData("tune.flac")]
    public void ImportSound_AudioFile_IsCopiedAsItIs(string file)
    {
        var sounds = Path.Combine(_dir, "Sounds");
        var name = ItemOps.ImportSound(Src(file, "the file"), sounds, Tone(10));
        Assert.Equal(file, name);
        Assert.Equal("the file", File.ReadAllText(Path.Combine(sounds, name)));
    }

    // ---------- the sound editor ----------

    private static float[] Tone(int n)
    {
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)(0.5 * Math.Sin(i * 0.05));
        return x;
    }

    [Fact]
    public void WriteSoundFile_WritesAReadableWav_UnderAFreeName()
    {
        var sounds = Path.Combine(_dir, "Sounds");
        var a = ItemOps.WriteSoundFile(sounds, "ding (edited)", Tone(4800));
        var b = ItemOps.WriteSoundFile(sounds, "ding (edited)", Tone(2400));
        Assert.Equal("ding (edited).wav", a);
        Assert.Equal("ding (edited) (2).wav", b);
        var back = EasyVocal.Core.Sounds.SoundLoader.Load(Path.Combine(sounds, a));
        Assert.Equal(4800, back.Length);
        Assert.InRange(back[100], Tone(4800)[100] - 0.001f, Tone(4800)[100] + 0.001f);
    }

    [Theory]
    [InlineData("a/b:c?", "a_b_c_")]
    [InlineData("   ", "sound")]
    [InlineData(null, "sound")]
    [InlineData("dots...", "dots")]
    public void SafeStem_MakesAFileName(string? name, string want) => Assert.Equal(want, ItemOps.SafeStem(name));

    [Fact]
    public void UseEditedFile_RemembersTheOriginalOnce_AndDropsOldEditedCopies()
    {
        var s = Fresh();
        var sounds = Path.Combine(_dir, "Sounds");
        Directory.CreateDirectory(sounds);
        File.WriteAllText(Path.Combine(sounds, "mine.mp3"), "original");
        var item = new SoundItem { Id = "my-1", Name = "Mine", FileName = "mine.mp3" };
        s.Sounds.Add(item);

        var first = ItemOps.WriteSoundFile(sounds, ItemOps.EditedStem(item), Tone(100));
        ItemOps.UseEditedFile(s, item, first, sounds);
        Assert.Equal("mine (edited).wav", item.FileName);
        Assert.Equal("mine.mp3", item.OriginalFileName);

        var second = ItemOps.WriteSoundFile(sounds, ItemOps.EditedStem(item), Tone(100));
        ItemOps.UseEditedFile(s, item, second, sounds);
        Assert.Equal("mine.mp3", item.OriginalFileName); // still the very first file
        Assert.False(File.Exists(Path.Combine(sounds, first)));  // the older edit is gone
        Assert.Equal("original", File.ReadAllText(Path.Combine(sounds, "mine.mp3"))); // never modified

        Assert.True(ItemOps.BackToOriginal(s, item, sounds));
        Assert.Equal("mine.mp3", item.FileName);
        Assert.Null(item.OriginalFileName);
        Assert.False(File.Exists(Path.Combine(sounds, second)));
        Assert.False(ItemOps.BackToOriginal(s, item, sounds)); // nothing to go back to
    }

    [Fact]
    public void EditedCopyAndOriginal_StayWhileACopyOfTheSoundUsesThem()
    {
        var s = Fresh();
        var sounds = Path.Combine(_dir, "Sounds");
        Directory.CreateDirectory(sounds);
        File.WriteAllText(Path.Combine(sounds, "mine.mp3"), "original");
        File.WriteAllText(Path.Combine(sounds, "mine (edited).wav"), "edited");
        var item = new SoundItem { Id = "my-1", Name = "Mine", FileName = "mine (edited).wav", OriginalFileName = "mine.mp3" };
        s.Sounds.Add(item);
        var copy = ItemOps.DuplicateSound(s, "my-1");
        Assert.Equal("mine.mp3", copy.OriginalFileName);

        ItemOps.BackToOriginal(s, item, sounds);
        Assert.True(File.Exists(Path.Combine(sounds, "mine (edited).wav"))); // the copy still plays it

        ItemOps.DeleteSound(s, copy.Id, sounds);
        Assert.False(File.Exists(Path.Combine(sounds, "mine (edited).wav")));
        Assert.True(File.Exists(Path.Combine(sounds, "mine.mp3"))); // the first sound plays it again

        ItemOps.DeleteSound(s, item.Id, sounds);
        Assert.False(File.Exists(Path.Combine(sounds, "mine.mp3")));
    }

    [Fact]
    public void DeleteSound_RemovesTheEditedCopyAndTheOriginal()
    {
        var s = Fresh();
        var sounds = Path.Combine(_dir, "Sounds");
        Directory.CreateDirectory(sounds);
        File.WriteAllText(Path.Combine(sounds, "a.mp3"), "o");
        File.WriteAllText(Path.Combine(sounds, "a (edited).wav"), "e");
        s.Sounds.Add(new SoundItem { Id = "my-1", Name = "A", FileName = "a (edited).wav", OriginalFileName = "a.mp3" });
        ItemOps.DeleteSound(s, "my-1", sounds);
        Assert.Empty(Directory.GetFiles(sounds));
    }

    [Fact]
    public void EditedBuiltIn_KeepsItsStarterFile()
    {
        var s = Fresh();
        var sounds = Path.Combine(_dir, "Sounds");
        Directory.CreateDirectory(sounds);
        File.WriteAllText(Path.Combine(sounds, "ding.wav"), "starter");
        var ding = s.Sounds.First(x => x.Id == "ding");
        var edited = ItemOps.WriteSoundFile(sounds, ItemOps.EditedStem(ding), Tone(10));
        ItemOps.UseEditedFile(s, ding, edited, sounds);
        Assert.Equal("ding (edited).wav", ding.FileName);
        ItemOps.DeleteFileIfUnused(s, "ding.wav", sounds);
        Assert.True(File.Exists(Path.Combine(sounds, "ding.wav"))); // it is the original of an edited sound
    }

    [Fact]
    public void AddSplitSounds_AddsTwoNumberedSoundsAfterTheOriginal()
    {
        var s = Fresh();
        var ding = s.Sounds.First(x => x.Id == "ding");
        int index = s.Sounds.IndexOf(ding);
        var (a, b) = ItemOps.AddSplitSounds(s, "ding", "Ding", "🔔", 0.6, "Ding 1.wav", "Ding 2.wav");
        Assert.Equal("Ding 1", a.Name);
        Assert.Equal("Ding 2", b.Name);
        Assert.Same(a, s.Sounds[index + 1]);
        Assert.Same(b, s.Sounds[index + 2]);
        Assert.False(a.BuiltIn || b.BuiltIn);
        Assert.Null(a.Hotkey);
        Assert.Equal(0.6, b.Volume);
        Assert.Equal("🔔", a.Emoji);
        Assert.NotEqual(a.Id, b.Id);

        var (c, d) = ItemOps.AddSplitSounds(s, null, "Ding", "🔔", 0.6, "x.wav", "y.wav");
        Assert.NotEqual("Ding 1", c.Name);
        Assert.NotEqual(c.Name, d.Name);
        Assert.Same(d, s.Sounds[^1]);
    }
}
