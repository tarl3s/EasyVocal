using System;
using System.IO;
using System.Linq;
using EasyVocal.Core.Model;

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
}
