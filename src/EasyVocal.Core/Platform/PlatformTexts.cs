namespace EasyVocal.Core.Platform;

/// <summary>
/// The sentences that name the operating system ("Start with Windows", "Windows default", "Restart your PC…"), in
/// English for <c>L.T</c>; each platform hands out its own (<see cref="PlatformServices.Texts"/>). Every one is written
/// with <see cref="UiText.Mark"/> so the translation test finds it.
/// </summary>
/// <param name="StartAtLogin">The check box in Settings.</param>
/// <param name="StartAtLoginRecommended">The setup's check box.</param>
/// <param name="StartAtLoginRefused">Settings: the system refused the entry.</param>
/// <param name="StartAtLoginRefusedTryAgain">The setup: the system refused the entry (it can be tried again in Settings).</param>
/// <param name="AutomaticMic">The microphone list's "Automatic" (the system's default mic).</param>
/// <param name="DefaultHeadphones">The headphones list's "no choice" (the system's default output).</param>
/// <param name="WaitingForInstall">The virtual mic's installer closed; its device isn't listed yet.</param>
/// <param name="RestartToFinish">The device never showed up after its install.</param>
/// <param name="SoundGoesToCable">The banner when the system's default playback device is the virtual cable.</param>
/// <param name="FixRefused">The banner's Fix couldn't change the default device.</param>
/// <param name="AppMenuQuestion">"Add EasyVocal to the Start menu…?" (where the platform has an app menu entry to make).</param>
/// <param name="AppMenuYes">That question's yes button.</param>
/// <param name="MicBlocked">The banner when the system refuses EasyVocal the microphone (privacy settings).</param>
public sealed record PlatformTexts(
    string StartAtLogin,
    string StartAtLoginRecommended,
    string StartAtLoginRefused,
    string StartAtLoginRefusedTryAgain,
    string AutomaticMic,
    string DefaultHeadphones,
    string WaitingForInstall,
    string RestartToFinish,
    string SoundGoesToCable,
    string FixRefused,
    string AppMenuQuestion,
    string AppMenuYes,
    string MicBlocked)
{
    public static PlatformTexts Windows { get; } = new(
        StartAtLogin: UiText.Mark("Start with Windows"),
        StartAtLoginRecommended: UiText.Mark("Start with Windows (recommended)"),
        StartAtLoginRefused: UiText.Mark("Windows would not let EasyVocal start with it."),
        StartAtLoginRefusedTryAgain: UiText.Mark("Windows would not let EasyVocal start with it. You can try again in Settings."),
        AutomaticMic: UiText.Mark("Automatic (Windows default)"),
        DefaultHeadphones: UiText.Mark("Windows default"),
        WaitingForInstall: UiText.Mark("Waiting for Windows to finish installing…"),
        RestartToFinish: UiText.Mark("Restart your PC to finish installing, then open EasyVocal again."),
        SoundGoesToCable: UiText.Mark("Your PC's sound is going into the virtual cable, so you can't hear it."),
        FixRefused: UiText.Mark("Windows didn't let EasyVocal change it. Pick your headphones or speakers in the Windows sound settings."),
        AppMenuQuestion: UiText.Mark("Add EasyVocal to the Start menu, so you can open it from there?"),
        AppMenuYes: UiText.Mark("Add to Start menu"),
        MicBlocked: UiText.Mark("EasyVocal can't use the microphone — allow it in Settings → Privacy & security → Microphone."));

    public static PlatformTexts MacOS { get; } = new(
        StartAtLogin: UiText.Mark("Open at login"),
        StartAtLoginRecommended: UiText.Mark("Open at login (recommended)"),
        StartAtLoginRefused: UiText.Mark("EasyVocal couldn't be set to open at login."),
        StartAtLoginRefusedTryAgain: UiText.Mark("EasyVocal couldn't be set to open at login. You can try again in Settings."),
        AutomaticMic: UiText.Mark("Automatic (system default)"),
        DefaultHeadphones: UiText.Mark("System default"),
        WaitingForInstall: UiText.Mark("Waiting for macOS to finish installing…"),
        RestartToFinish: UiText.Mark("Restart your Mac to finish installing, then open EasyVocal again."),
        SoundGoesToCable: UiText.Mark("Your Mac's sound is going into the virtual cable, so you can't hear it."),
        FixRefused: UiText.Mark("EasyVocal couldn't change it. Pick your headphones or speakers in System Settings → Sound."),
        AppMenuQuestion: UiText.Mark("Add EasyVocal to your applications menu, so you can open it from there?"),
        AppMenuYes: UiText.Mark("Add to the menu"),
        MicBlocked: UiText.Mark("EasyVocal can't use the microphone — allow it in System Settings → Privacy & Security → Microphone."));

    public static PlatformTexts Linux { get; } = new(
        StartAtLogin: UiText.Mark("Start at login"),
        StartAtLoginRecommended: UiText.Mark("Start at login (recommended)"),
        StartAtLoginRefused: UiText.Mark("EasyVocal couldn't be set to start at login."),
        StartAtLoginRefusedTryAgain: UiText.Mark("EasyVocal couldn't be set to start at login. You can try again in Settings."),
        AutomaticMic: UiText.Mark("Automatic (system default)"),
        DefaultHeadphones: UiText.Mark("System default"),
        WaitingForInstall: UiText.Mark("Waiting for the system to finish installing…"),
        RestartToFinish: UiText.Mark("Restart your computer to finish installing, then open EasyVocal again."),
        SoundGoesToCable: UiText.Mark("Your computer's sound is going into the virtual cable, so you can't hear it."),
        FixRefused: UiText.Mark("EasyVocal couldn't change it. Pick your headphones or speakers in the system's sound settings."),
        AppMenuQuestion: UiText.Mark("Add EasyVocal to your applications menu, so you can open it from there?"),
        AppMenuYes: UiText.Mark("Add to the menu"),
        MicBlocked: UiText.Mark("EasyVocal can't use the microphone — allow it in your system's privacy settings."));

    /// <summary>The texts for the computer this runs on.</summary>
    public static PlatformTexts ForThisComputer =>
        OperatingSystem.IsMacOS() ? MacOS : OperatingSystem.IsWindows() ? Windows : Linux;
}
