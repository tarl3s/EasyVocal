using System.Runtime.InteropServices;

namespace EasyVocal.Platform.Unix;

/// <summary>
/// The few PortAudio v19 calls EasyVocal needs (portaudio.h). The library is <c>libportaudio</c> from the
/// org.k2fsa.portaudio.runtime packages (macOS: CoreAudio; Linux: ALSA/JACK); C's <c>unsigned long</c> is
/// <see cref="CULong"/> so the same declarations also run on Windows (the tests).
/// </summary>
internal static unsafe class PortAudioNative
{
    private const string Lib = "portaudio";

    public const int NoError = 0;

    public const ulong Float32 = 0x00000001;
    public const ulong ClipOff = 0x00000001; // paClipOff (0x2 would be paDitherOff)

    public const int Continue = 0;
    public const int Complete = 1;
    public const int Abort = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfo
    {
        public int StructVersion;
        public IntPtr Name;
        public int HostApi;
        public int MaxInputChannels;
        public int MaxOutputChannels;
        public double DefaultLowInputLatency;
        public double DefaultLowOutputLatency;
        public double DefaultHighInputLatency;
        public double DefaultHighOutputLatency;
        public double DefaultSampleRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HostApiInfo
    {
        public int StructVersion;
        public int Type;
        public IntPtr Name;
        public int DeviceCount;
        public int DefaultInputDevice;
        public int DefaultOutputDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StreamParameters
    {
        public int Device;
        public int ChannelCount;
        public CULong SampleFormat;
        public double SuggestedLatency;
        public IntPtr HostApiSpecificStreamInfo;
    }

    [DllImport(Lib)] public static extern int Pa_Initialize();
    [DllImport(Lib)] public static extern int Pa_Terminate();
    [DllImport(Lib)] public static extern IntPtr Pa_GetErrorText(int error);
    [DllImport(Lib)] public static extern int Pa_GetDeviceCount();
    [DllImport(Lib)] public static extern int Pa_GetDefaultHostApi();
    [DllImport(Lib)] public static extern HostApiInfo* Pa_GetHostApiInfo(int hostApi);
    [DllImport(Lib)] public static extern DeviceInfo* Pa_GetDeviceInfo(int device);

    [DllImport(Lib)]
    public static extern int Pa_IsFormatSupported(StreamParameters* input, StreamParameters* output, double sampleRate);

    [DllImport(Lib)]
    public static extern int Pa_OpenStream(out IntPtr stream, StreamParameters* input, StreamParameters* output, double sampleRate,
        CULong framesPerBuffer, CULong flags,
        delegate* unmanaged[Cdecl]<void*, void*, CULong, void*, CULong, void*, int> callback, void* userData);

    /// <summary>
    /// Called with the stream's userData once the stream is inactive: after its callback answered paComplete and
    /// PortAudio stopped it (on macOS when the AudioUnit says it no longer runs), or after a stop call.
    /// </summary>
    [DllImport(Lib)]
    public static extern int Pa_SetStreamFinishedCallback(IntPtr stream, delegate* unmanaged[Cdecl]<void*, void> finished);

    [DllImport(Lib)] public static extern int Pa_StartStream(IntPtr stream);
    [DllImport(Lib)] public static extern int Pa_AbortStream(IntPtr stream);
    [DllImport(Lib)] public static extern int Pa_CloseStream(IntPtr stream);
    [DllImport(Lib)] public static extern int Pa_IsStreamActive(IntPtr stream);
    [DllImport(Lib)] public static extern int Pa_IsStreamStopped(IntPtr stream);

    /// <summary>PortAudio's sentence for an error code.</summary>
    public static string ErrorText(int error)
    {
        try { return Marshal.PtrToStringUTF8(Pa_GetErrorText(error)) ?? ("PortAudio error " + error); }
        catch (Exception) { return "PortAudio error " + error; }
    }
}

/// <summary>A PortAudio call failed.</summary>
public sealed class PortAudioException(string what, int error)
    : IOException(what + ": " + PortAudioNative.ErrorText(error) + " (" + error + ")")
{
    public int Error { get; } = error;
}

/// <summary>The PortAudio calls made on an open stream (the tests pass a pretend one to see when each is made).</summary>
internal interface IPaStreamCalls
{
    int Start(IntPtr stream);
    int Abort(IntPtr stream);
    int Close(IntPtr stream);
    int IsActive(IntPtr stream);
    int IsStopped(IntPtr stream);
}

internal sealed class PaStreamCalls : IPaStreamCalls
{
    public static readonly PaStreamCalls Instance = new();

    public int Start(IntPtr stream) => PortAudioNative.Pa_StartStream(stream);
    public int Abort(IntPtr stream) => PortAudioNative.Pa_AbortStream(stream);
    public int Close(IntPtr stream) => PortAudioNative.Pa_CloseStream(stream);
    public int IsActive(IntPtr stream) => PortAudioNative.Pa_IsStreamActive(stream);
    public int IsStopped(IntPtr stream) => PortAudioNative.Pa_IsStreamStopped(stream);
}
