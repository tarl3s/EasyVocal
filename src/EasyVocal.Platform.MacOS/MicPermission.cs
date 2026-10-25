using System.Runtime.InteropServices;
using EasyVocal.Platform.Unix;

namespace EasyVocal.Platform.MacOS;

/// <summary>
/// macOS' microphone permission (Privacy &amp; Security → Microphone), read through AVFoundation's
/// <c>[AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeAudio]</c>. When the person hasn't been asked yet,
/// opening the microphone makes macOS ask (the .app's Info.plist says why: NSMicrophoneUsageDescription); a "no" makes
/// CoreAudio deliver silence, so EasyVocal looks at the status before opening and while recording.
/// </summary>
public static class MicPermission
{
    public enum Status { NotDetermined = 0, Restricted = 1, Denied = 2, Authorized = 3, Unknown = -1 }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AVFoundation = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector, string arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNint(IntPtr receiver, IntPtr selector, IntPtr arg);

    private static readonly object sync = new();
    private static bool loaded, broken;
    private static IntPtr captureDevice, statusSelector, audioType;

    /// <summary>The current status; <see cref="Status.Unknown"/> when it can't be read (then nothing is blocked).</summary>
    public static Status Current()
    {
        lock (sync)
        {
            if (broken) return Status.Unknown;
            try
            {
                if (!loaded)
                {
                    NativeLibrary.Load(AVFoundation); // registers AVCaptureDevice with the Objective-C runtime
                    captureDevice = objc_getClass("AVCaptureDevice");
                    statusSelector = sel_registerName("authorizationStatusForMediaType:");
                    // AVMediaTypeAudio is the NSString "soun".
                    audioType = SendPtr(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), "soun");
                    if (audioType != IntPtr.Zero) SendNint(audioType, sel_registerName("retain"), IntPtr.Zero);
                    loaded = true;
                    if (captureDevice == IntPtr.Zero || audioType == IntPtr.Zero) throw new EntryPointNotFoundException("AVCaptureDevice");
                }
                return (Status)(int)SendNint(captureDevice, statusSelector, audioType);
            }
            catch (Exception ex)
            {
                broken = true;
                AppLog.Error("The microphone permission can't be read", ex);
                return Status.Unknown;
            }
        }
    }

    /// <summary>The person (or a policy) said no: EasyVocal can't use the microphone until it is allowed.</summary>
    public static bool IsRefused() => Current() is Status.Denied or Status.Restricted;

    /// <summary>System Settings → Privacy &amp; Security → Microphone.</summary>
    public const string SettingsUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone";

    public static void OpenSettings() => Opener.Open(SettingsUrl);
}
