using EasyVocal.Core.Model;
using EasyVocal.Core.Storage;

namespace EasyVocal.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string tmp = Path.Combine(Path.GetTempPath(), "EV_Test_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
    }

    private string SettingsPath => Path.Combine(tmp, "settings.json");

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(tmp);
        File.WriteAllText(SettingsPath, json);
    }

    [Fact]
    public void Load_MissingFolder_ReturnsDefaultsWithAllBuiltIns()
    {
        var s = SettingsStore.Load(tmp);
        Assert.Equal(BuiltInVoices.All().Select(v => v.Id), s.Voices.Select(v => v.Id));
        Assert.Equal(StarterSounds.Items().Select(x => x.Id), s.Sounds.Select(x => x.Id));
        Assert.Equal("normal", s.ActiveVoiceId);
        Assert.True(s.Enabled);
        Assert.True(s.HearSounds);
        Assert.False(s.SetupDone);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsUserItemsAndHotkeys()
    {
        var s = SettingsStore.Load(tmp);
        s.Voices.Add(new VoicePreset
        {
            Id = "custom-1", Name = "My filter", Emoji = "🎤", Hotkey = "Ctrl+1",
            Sliders = new VoiceSliders { Pitch = 0.3, Echo = 0.5 },
        });
        s.Sounds.Add(new SoundItem { Id = "mine", Name = "Mine", FileName = "mine.wav", Volume = 0.5, Hotkey = "F5" });
        s.Voices.First(v => v.Id == "alien").Hidden = true;
        s.Sounds.First(x => x.Id == "airhorn").Hotkey = "7";
        s.GeneralHotkeys["muteMic"] = "Ctrl+M";
        s.HearMe = true;
        s.ActiveVoiceId = "custom-1";
        s.MicDeviceId = "mic-1";
        SettingsStore.Save(tmp, s);

        var l = SettingsStore.Load(tmp);
        var v = l.Voices.Single(x => x.Id == "custom-1");
        Assert.Equal("My filter", v.Name);
        Assert.Equal("🎤", v.Emoji);
        Assert.Equal("Ctrl+1", v.Hotkey);
        Assert.Equal(0.3, v.Sliders.Pitch);
        Assert.Equal(0.5, v.Sliders.Echo);
        Assert.False(v.BuiltIn);
        Assert.True(l.Voices.Single(x => x.Id == "alien").Hidden);
        Assert.Equal("7", l.Sounds.Single(x => x.Id == "airhorn").Hotkey);
        var snd = l.Sounds.Single(x => x.Id == "mine");
        Assert.Equal("mine.wav", snd.FileName);
        Assert.Equal(0.5, snd.Volume);
        Assert.Equal("Ctrl+M", l.GeneralHotkeys["muteMic"]);
        Assert.True(l.HearMe);
        Assert.Equal("custom-1", l.ActiveVoiceId);
        Assert.Equal("mic-1", l.MicDeviceId);
        Assert.Equal(s.Voices.Select(x => x.Id), l.Voices.Select(x => x.Id));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaultsAndKeepsBadCopy()
    {
        WriteFile("{ \"Voices\": [ {");
        var s = SettingsStore.Load(tmp);
        Assert.Equal("normal", s.ActiveVoiceId);
        Assert.True(File.Exists(Path.Combine(tmp, "settings.json.bad")));
    }

    [Fact]
    public void Load_UnknownAndMissingKeys_UseDefaults()
    {
        WriteFile("{\"Version\":1,\"Foo\":3,\"HearMe\":true}");
        var s = SettingsStore.Load(tmp);
        Assert.True(s.HearMe);
        Assert.True(s.HearSounds);
        Assert.Equal(BuiltInVoices.All().Select(v => v.Id), s.Voices.Select(v => v.Id));
        Assert.Equal(StarterSounds.Items().Select(x => x.Id), s.Sounds.Select(x => x.Id));
    }

    [Fact]
    public void Load_OldFileMissingANewBuiltIn_AddsItKeepingUserChanges()
    {
        WriteFile("{\"Voices\":[{\"Id\":\"normal\",\"Name\":\"Normal\"},{\"Id\":\"robot\",\"Name\":\"Robot\",\"Hidden\":true,\"Hotkey\":\"F1\"}]}");
        var s = SettingsStore.Load(tmp);
        Assert.Equal(BuiltInVoices.All().Select(v => v.Id), s.Voices.Select(v => v.Id));
        var robot = s.Voices.Single(v => v.Id == "robot");
        Assert.True(robot.Hidden);
        Assert.Equal("F1", robot.Hotkey);
    }

    [Fact]
    public void Load_BuiltInSlidersComeFromCode_NotFromFile()
    {
        WriteFile("{\"Voices\":[{\"Id\":\"robot\",\"Name\":\"Old name\",\"Sliders\":{\"Robot\":0.1}}]}");
        var s = SettingsStore.Load(tmp);
        var robot = s.Voices.Single(v => v.Id == "robot");
        Assert.Equal(0.8, robot.Sliders.Robot);
        Assert.Equal("Robot", robot.Name);
        Assert.True(robot.BuiltIn);
    }

    [Fact]
    public void Load_NormalHiddenInTheFile_IsVisibleAgain()
    {
        WriteFile("{\"Voices\":[{\"Id\":\"normal\",\"Hidden\":true},{\"Id\":\"robot\",\"Hidden\":true}]}");
        var s = SettingsStore.Load(tmp);
        Assert.False(s.Voices.Single(v => v.Id == BuiltInVoices.NormalId).Hidden);
        Assert.True(s.Voices.Single(v => v.Id == "robot").Hidden); // any other built-in may stay hidden
    }

    [Fact]
    public void SaveThenLoad_BuiltInSoundKeepsTheNameAndEmojiThePersonGaveIt()
    {
        var s = SettingsStore.Load(tmp);
        var ding = s.Sounds.Single(x => x.Id == "ding");
        ding.Name = "Bell";
        ding.Emoji = "🛎️";
        ding.FileName = "elsewhere.wav"; // not the person's: a built-in always plays its own file
        SettingsStore.Save(tmp, s);

        var loaded = SettingsStore.Load(tmp).Sounds.Single(x => x.Id == "ding");
        Assert.Equal("Bell", loaded.Name);
        Assert.Equal("🛎️", loaded.Emoji);
        Assert.Equal("ding.wav", loaded.FileName);
        Assert.True(loaded.BuiltIn);
    }

    [Fact]
    public void Load_BuiltInSoundWithEmptyNameOrEmoji_UsesTheCatalogOnes()
    {
        WriteFile("{\"Sounds\":[{\"Id\":\"ding\",\"Name\":\" \",\"Emoji\":\"\"}]}");
        var ding = SettingsStore.Load(tmp).Sounds.Single(x => x.Id == "ding");
        var catalog = StarterSounds.Items().Single(x => x.Id == "ding");
        Assert.Equal(catalog.Name, ding.Name);
        Assert.Equal(catalog.Emoji, ding.Emoji);
    }

    [Fact]
    public void Load_OutOfRangeSliders_AreClamped()
    {
        WriteFile("{\"Voices\":[{\"Id\":\"c1\",\"Name\":\"C\",\"Sliders\":{\"Pitch\":5,\"Echo\":-2}}]}");
        var s = SettingsStore.Load(tmp);
        var c = s.Voices.Single(v => v.Id == "c1");
        Assert.Equal(1, c.Sliders.Pitch);
        Assert.Equal(0, c.Sliders.Echo);
    }

    [Fact]
    public void Save_IsAtomic_NoTempLeftAndOldFileReplaced()
    {
        var s = SettingsStore.Load(tmp);
        s.Language = "first";
        SettingsStore.Save(tmp, s);
        Assert.Equal(new[] { SettingsPath }, Directory.GetFiles(tmp)); // the first save has nothing to back up
        s.Language = "second";
        SettingsStore.Save(tmp, s);

        Assert.Equal(new[] { SettingsPath, SettingsPath + ".bak" }, Directory.GetFiles(tmp).OrderBy(f => f, StringComparer.Ordinal));
        Assert.Equal("second", SettingsStore.Load(tmp).Language);
    }

    [Fact]
    public void Save_KeepsThePreviousFileAsBak()
    {
        var s = SettingsStore.Load(tmp);
        s.Language = "first";
        SettingsStore.Save(tmp, s);
        s.Language = "second";
        SettingsStore.Save(tmp, s);
        Assert.Contains("\"first\"", File.ReadAllText(SettingsPath + ".bak"));
        Assert.Contains("\"second\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Load_CorruptFileWithGoodBak_UsesTheBak()
    {
        var s = SettingsStore.Load(tmp);
        s.Language = "from-backup";
        SettingsStore.Save(tmp, s);
        File.Copy(SettingsPath, SettingsPath + ".bak");
        File.WriteAllText(SettingsPath, "{ \"Voices\": [ {");

        var loaded = SettingsStore.Load(tmp, out bool couldNotRead);
        Assert.False(couldNotRead);
        Assert.Equal("from-backup", loaded.Language);
        Assert.True(File.Exists(SettingsPath + ".bad"));
    }

    [Fact]
    public void SaveAfterRecoveringFromBak_KeepsTheGoodBackup()
    {
        var s = SettingsStore.Load(tmp);
        s.Language = "good";
        SettingsStore.Save(tmp, s);
        SettingsStore.Save(tmp, s); // settings.json.bak holds "good" too
        File.WriteAllText(SettingsPath, "{ \"Voices\": [ {");

        var file = new SettingsFile(tmp);
        var loaded = file.Load();
        Assert.Equal("good", loaded.Language);
        Assert.Equal("{ \"Voices\": [ {", File.ReadAllText(SettingsPath + ".bad")); // the damaged file is kept

        loaded.Language = "after";
        Assert.True(file.Save(loaded));
        // The damaged file never lands in .bak: the good backup survives the first save after the recovery.
        Assert.Equal("good", JsonLanguage(File.ReadAllText(SettingsPath + ".bak")));
        Assert.Equal("after", JsonLanguage(File.ReadAllText(SettingsPath)));

        loaded.Language = "later";
        Assert.True(file.Save(loaded));
        Assert.Equal("after", JsonLanguage(File.ReadAllText(SettingsPath + ".bak"))); // the usual rotation afterwards
    }

    private static string? JsonLanguage(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("Language").GetString();

    [Fact]
    public void Load_LockedFile_DefaultsAndReadOnlySession()
    {
        var s = SettingsStore.Load(tmp);
        s.Language = "mine";
        SettingsStore.Save(tmp, s);

        var file = new SettingsFile(tmp);
        AppSettings loaded;
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            loaded = file.Load();
            Assert.True(file.CouldNotRead);
            Assert.Equal("normal", loaded.ActiveVoiceId);
            Assert.NotEqual("mine", loaded.Language);
        }
        // Still flagged after the lock is gone: this session must never write its defaults over the real file.
        loaded.Language = "defaults";
        Assert.False(file.Save(loaded));
        Assert.Equal("mine", SettingsStore.Load(tmp).Language);
        Assert.False(File.Exists(SettingsPath + ".bad"));
    }

    [Fact]
    public void SettingsFile_ReadableFile_Saves()
    {
        var file = new SettingsFile(tmp);
        var s = file.Load();
        Assert.False(file.CouldNotRead);
        s.Language = "pt-BR";
        Assert.True(file.Save(s));
        Assert.Equal("pt-BR", SettingsStore.Load(tmp).Language);
    }

    [Fact]
    public void VoiceSliders_IsNeutral_AndClamp()
    {
        Assert.True(new VoiceSliders().IsNeutral);
        var v = new VoiceSliders { Whisper = 0.1 };
        Assert.False(v.IsNeutral);
        var c = new VoiceSliders { Pitch = -3, Robot = 4 }.Clamp();
        Assert.Equal(-1, c.Pitch);
        Assert.Equal(1, c.Robot);
        var clone = v.Clone();
        clone.Whisper = 0.9;
        Assert.Equal(0.1, v.Whisper);
    }
}
