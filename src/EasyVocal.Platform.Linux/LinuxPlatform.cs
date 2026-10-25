using System.Runtime.InteropServices;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;
using EasyVocal.Core.Sounds;
using EasyVocal.Platform.Unix;

namespace EasyVocal.Platform.Linux;

/// <summary>
/// Linux' sound devices, chosen without waiting on the UI thread: PulseAudio / PipeWire (<see cref="PulseBackend"/>)
/// when pactl and libpulse-simple are there; if its first look finds no server answering, PortAudio (ALSA) takes over
/// and <see cref="Changed"/> tells the app to look again. Without pactl or libpulse-simple, PortAudio from the start.
/// </summary>
public sealed class LinuxAudio : IAudioBackend, IDisposable
{
    private readonly object sync = new();
    private IAudioBackend inner;
    private bool disposed;

    /// <param name="pulse">The PulseAudio backend, or null when it can't be used here at all.</param>
    /// <param name="fallback">Makes the PortAudio backend (only when it is needed).</param>
    public LinuxAudio(PulseBackend? pulse, Func<IAudioBackend> fallback)
    {
        if (pulse == null)
        {
            inner = fallback();
            AppLog.Info("Sound: PortAudio (ALSA): pactl or libpulse-simple is missing; install pipewire-pulse for the virtual microphone");
        }
        else
        {
            inner = pulse;
            AppLog.Info("Sound: PulseAudio / PipeWire (pactl + libpulse-simple)");
            pulse.FirstLook.ContinueWith(t =>
            {
                if (t.Result) return;
                AppLog.Info("Sound: no PulseAudio / PipeWire server answered: PortAudio (ALSA) instead");
                Switch(fallback());
            }, TaskScheduler.Default);
        }
        inner.Changed += Relay;
    }

    /// <summary>The backend in use now.</summary>
    public IAudioBackend Inner
    {
        get { lock (sync) return inner; }
    }

    /// <summary>The PulseAudio backend while it is the one in use, else null.</summary>
    public PulseBackend? Pulse => Inner as PulseBackend;

    private void Switch(IAudioBackend next)
    {
        IAudioBackend old;
        lock (sync)
        {
            if (disposed) { (next as IDisposable)?.Dispose(); return; }
            old = inner;
            inner = next;
        }
        old.Changed -= Relay;
        next.Changed += Relay;
        (old as IDisposable)?.Dispose();
        Changed?.Invoke();
    }

    private void Relay() => Changed?.Invoke();

    public event Action? Changed;

    public IReadOnlyList<DeviceInfo> Inputs() => Inner.Inputs();
    public IReadOnlyList<DeviceInfo> Outputs() => Inner.Outputs();
    public IAudioInput OpenMic(string id) => Inner.OpenMic(id);
    public IAudioOutput OpenSpeaker(string id) => Inner.OpenSpeaker(id);
    public DefaultIds Defaults(bool playback) => Inner.Defaults(playback);
    public bool SetDefault(string id) => false;

    public void Dispose()
    {
        IAudioBackend now;
        lock (sync)
        {
            disposed = true;
            now = inner;
        }
        (now as IDisposable)?.Dispose();
    }
}

/// <summary>The Linux implementations of EasyVocal.Core.Platform, in one place.</summary>
public static class LinuxPlatform
{
    private static readonly object sync = new();
    private static LinuxAudio? audio;

    /// <summary>libpulse-simple can be loaded (it is part of libpulse0, on every PulseAudio / PipeWire desktop).</summary>
    public static bool PulseLibraryPresent()
    {
        if (!NativeLibrary.TryLoad(PulseSimple.Lib, out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }

    /// <summary>The sound devices (made once; nothing here waits for a sound server).</summary>
    public static LinuxAudio Audio()
    {
        lock (sync)
        {
            if (audio != null) return audio;
            bool pulse = Pactl.Default.Locate() != null && PulseLibraryPresent();
            audio = new LinuxAudio(pulse ? new PulseBackend() : null, () => new PortAudioBackend(systemFingerprint: AlsaDevices.Fingerprint));
            return audio;
        }
    }

    /// <param name="appIcon">EasyVocal's icon (.ico bytes) for the applications menu entry; null = none.</param>
    public static PlatformServices Services(Func<byte[]?>? appIcon = null)
    {
        var backend = Audio();
        return new PlatformServices(
            backend,
            AutostartEntry.Default,
            new AppMenuEntry(icon: appIcon),
            // After making or removing its devices the list is read again right there (a background thread), so the audio
            // finds EasyVocal-Output at once.
            new PactlVirtualMic(devicesChanged: () => backend.Pulse?.Refresh()),
            UnixUpdaterPlatform.Linux,
            SoundDecoders.Default, // WAV/AIFF, OGG, MP3 (NLayer), then ffmpeg when it is installed
            null, // no MP3 encoder: the sound editor saves WAV
            _ => new X11Hotkeys())
        {
            Texts = PlatformTexts.Linux,
            OpenMicSettings = null, // no microphone permission to open on a plain Linux desktop
            HasTray = () => TrayHost.Present(),
        };
    }
}

/// <summary>
/// What ALSA has, cheaply: the sound cards (<c>/proc/asound/cards</c>) and the device files (<c>/dev/snd</c>). PortAudio
/// is started again only when this changes (a card plugged in or out), never on a timer.
/// </summary>
public static class AlsaDevices
{
    public static string Fingerprint() => Fingerprint("/proc/asound/cards", "/dev/snd");

    public static string Fingerprint(string cardsFile, string deviceFolder)
    {
        string cards = "", files = "";
        try { if (File.Exists(cardsFile)) cards = File.ReadAllText(cardsFile); } catch (Exception) { /* unreadable: no change seen */ }
        try
        {
            if (Directory.Exists(deviceFolder))
                files = string.Join(",", Directory.EnumerateFileSystemEntries(deviceFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        catch (Exception) { /* unreadable */ }
        return cards + "#" + files;
    }
}
