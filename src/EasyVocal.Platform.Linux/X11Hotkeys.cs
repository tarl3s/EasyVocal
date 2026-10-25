using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Platform;

namespace EasyVocal.Platform.Linux;

/// <summary>The saved hotkeys (Windows virtual-key codes, see <see cref="Hotkey"/>) in X11's terms: keysyms and modifier masks.</summary>
public static class X11Keys
{
    public const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16, Mod4Mask = 64;

    /// <summary>The masks a press may also carry without meaning anything: Caps Lock, Num Lock and both.</summary>
    public static readonly uint[] IgnoredVariants = { 0, LockMask, Mod2Mask, LockMask | Mod2Mask };

    private static readonly Dictionary<int, uint> Keysyms = Build();

    private static Dictionary<int, uint> Build()
    {
        var d = new Dictionary<int, uint>();
        for (int c = 'A'; c <= 'Z'; c++) d[c] = (uint)char.ToLowerInvariant((char)c); // XK_a…XK_z
        for (int c = '0'; c <= '9'; c++) d[c] = (uint)c;                             // XK_0…XK_9
        for (int i = 0; i < 24; i++) d[0x70 + i] = 0xFFBEu + (uint)i;                  // XK_F1…XK_F24
        for (int i = 0; i < 10; i++) d[0x60 + i] = 0xFFB0u + (uint)i;                  // XK_KP_0…XK_KP_9
        d[0x6A] = 0xFFAA; d[0x6B] = 0xFFAB; d[0x6D] = 0xFFAD; d[0x6F] = 0xFFAF;      // KP * + - /
        d[0x20] = 0x20;                                                              // space
        d[0x2D] = 0xFF63; d[0x2E] = 0xFFFF;                                          // Insert, Delete
        d[0x24] = 0xFF50; d[0x23] = 0xFF57; d[0x21] = 0xFF55; d[0x22] = 0xFF56;      // Home, End, Prior, Next
        d[0x25] = 0xFF51; d[0x26] = 0xFF52; d[0x27] = 0xFF53; d[0x28] = 0xFF54;      // Left, Up, Right, Down
        d[0x13] = 0xFF13; d[0x91] = 0xFF14;                                          // Pause, Scroll_Lock
        d[0xBA] = 0x3B; d[0xBB] = 0x3D; d[0xBC] = 0x2C; d[0xBD] = 0x2D; d[0xBE] = 0x2E; // ; = , - .
        d[0xBF] = 0x2F; d[0xC0] = 0x60; d[0xDB] = 0x5B; d[0xDC] = 0x5C; d[0xDD] = 0x5D; d[0xDE] = 0x27; // / ` [ \ ] '
        return d;
    }

    /// <summary>The keysym of a saved key, or null when X has none for it.</summary>
    public static uint? Keysym(int virtualKey) => Keysyms.TryGetValue(virtualKey, out var k) ? k : null;

    /// <summary>X's modifier mask: Alt is Mod1, Win (Super) is Mod4.</summary>
    public static uint Modifiers(HotkeyMods mods)
    {
        uint m = 0;
        if ((mods & HotkeyMods.Shift) != 0) m |= ShiftMask;
        if ((mods & HotkeyMods.Ctrl) != 0) m |= ControlMask;
        if ((mods & HotkeyMods.Alt) != 0) m |= Mod1Mask;
        if ((mods & HotkeyMods.Win) != 0) m |= Mod4Mask;
        return m;
    }

    /// <summary>A press's modifiers without Caps Lock and Num Lock (and the mouse buttons and other bits).</summary>
    public static uint Clean(uint state) => state & (ShiftMask | ControlMask | Mod1Mask | Mod4Mask);
}

/// <summary>Whether this desktop session lets an app have global hotkeys.</summary>
public static class DesktopSession
{
    /// <summary>Wayland doesn't let apps grab keys system-wide: one English sentence for <c>L.T</c>.</summary>
    public static readonly string WaylandReason = UiText.Mark(
        "This desktop (Wayland) doesn't let apps use global hotkeys. Click the buttons in EasyVocal instead, or log in with an X11 (Xorg) session.");

    public static readonly string NoDisplayReason = UiText.Mark("Global hotkeys need an X11 desktop, and none was found.");

    /// <summary>Null when X11 hotkeys can be used, else why not.</summary>
    public static string? HotkeysUnavailable(Func<string, string?> env)
    {
        // On Wayland an X11 grab only sees keys while an X11 window has the focus: not global, so not offered.
        if (string.Equals(env("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(env("WAYLAND_DISPLAY")))
            return WaylandReason;
        if (string.IsNullOrEmpty(env("DISPLAY"))) return NoDisplayReason;
        return null;
    }
}

/// <summary>
/// System-wide hotkeys through X11's XGrabKey on the root window, on a thread of its own with its own display
/// connection (every Xlib call happens there; <see cref="Register"/> waits for it). Each combination is grabbed four
/// times (with and without Caps Lock / Num Lock). Auto-repeat is ignored: one press, one <see cref="Pressed"/>, raised on
/// that thread. A combination another app grabbed is refused (X reports BadAccess to XGrabKey). The thread sleeps in
/// poll() on the X connection and a wake pipe (no busy loop); a keyboard layout change (MappingNotify) grabs every
/// hotkey again with its key's new code.
/// </summary>
public sealed unsafe class X11Hotkeys : IGlobalHotkeys, IHotkeyTestHooks
{
    private const string Lib = "libX11.so.6";
    private const string LibC = "libc";
    private const int KeyPress = 2, KeyRelease = 3, MappingNotify = 34, GrabModeAsync = 1;
    /// <summary>X's request code of XGrabKey: only its errors say a combination is taken (BadAccess).</summary>
    private const byte GrabKeyRequest = 33;
    private const short PollIn = 1;

    [DllImport(Lib)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(Lib)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(Lib)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(Lib)] private static extern int XConnectionNumber(IntPtr display);
    [DllImport(Lib)] private static extern byte XKeysymToKeycode(IntPtr display, nuint keysym);
    [DllImport(Lib)] private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(Lib)] private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window);
    [DllImport(Lib)] private static extern int XSync(IntPtr display, int discard);
    [DllImport(Lib)] private static extern int XFlush(IntPtr display);
    [DllImport(Lib)] private static extern int XPending(IntPtr display);
    [DllImport(Lib)] private static extern int XNextEvent(IntPtr display, void* ev);
    [DllImport(Lib)] private static extern int XRefreshKeyboardMapping(void* mappingEvent);
    [DllImport(Lib)] private static extern IntPtr XSetErrorHandler(delegate* unmanaged[Cdecl]<IntPtr, XErrorEvent*, int> handler);
    [DllImport(Lib)] private static extern int XkbSetDetectableAutoRepeat(IntPtr display, int detectable, int* supported);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport(LibC, SetLastError = true)] private static extern int pipe(int* fds);
    [DllImport(LibC, SetLastError = true)] private static extern int poll(PollFd* fds, nuint count, int timeoutMs);
    [DllImport(LibC, SetLastError = true)] private static extern nint read(int fd, void* buffer, nint count);
    [DllImport(LibC, SetLastError = true)] private static extern nint write(int fd, void* buffer, nint count);
    [DllImport(LibC)] private static extern int close(int fd);
    [DllImport(LibC)] private static extern int fcntl(int fd, int command, int argument);

    [StructLayout(LayoutKind.Sequential)]
    private struct XErrorEvent
    {
        public int Type;
        public IntPtr Display;
        public nuint ResourceId;
        public nuint Serial;
        public byte ErrorCode;
        public byte RequestCode;
        public byte MinorCode;
    }

    /// <summary>XKeyEvent's fields EasyVocal reads (the start of the 192-byte XEvent union, 64-bit layout).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct XKeyEvent
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public IntPtr Display;
        public IntPtr Window, Root, Subwindow;
        public nuint Time;
        public int X, Y, XRoot, YRoot;
        public uint State;
        public uint Keycode;
        public int SameScreen;
    }

    // X's error handler is one per process: ours only answers for our own displays, others go to the one before it.
    // (Set once per process; every X11Hotkeys display is in GrabErrors, the error of its last XGrabKey there.)
    private static readonly ConcurrentDictionary<IntPtr, int> GrabErrors = new();
    private static readonly object HandlerSync = new();
    private static bool handlerSet;
    private static IntPtr previousHandler;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int OnXError(IntPtr display, XErrorEvent* error)
    {
        if (GrabErrors.ContainsKey(display))
        {
            // Only a refused grab means "taken"; other errors on our connection (an ungrab of nothing) mean nothing.
            if (error->RequestCode == GrabKeyRequest) GrabErrors[display] = error->ErrorCode;
            return 0;
        }
        if (previousHandler != IntPtr.Zero)
            return ((delegate* unmanaged[Cdecl]<IntPtr, XErrorEvent*, int>)previousHandler)(display, error);
        return 0;
    }

    private string? unavailable;
    private readonly Thread? thread;
    private readonly ConcurrentQueue<Action> work = new();
    private readonly ManualResetEventSlim ready = new();
    private readonly Dictionary<int, (string Action, Hotkey Key, int Keycode, uint Mods)> registered = new(); // only on the X thread
    private readonly HashSet<(int Keycode, uint Mods)> down = new();
    private readonly int[] wakePipe = { -1, -1 };
    private IntPtr display, root;
    private int nextId = 1, count, highest, regrabs;
    private volatile bool stopping;

    public event Action<string>? Pressed;

    /// <param name="environment">Reads an environment variable (the tests pass their own); null = the real one.</param>
    public X11Hotkeys(Func<string, string?>? environment = null)
    {
        unavailable = DesktopSession.HotkeysUnavailable(environment ?? Environment.GetEnvironmentVariable);
        if (unavailable != null) return;
        thread = new Thread(Run) { IsBackground = true, Name = "EasyVocal X11 hotkeys" };
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5)) || display == IntPtr.Zero) unavailable ??= DesktopSession.NoDisplayReason;
    }

    public bool Available => unavailable == null;

    public string? UnavailableReason => unavailable;

    /// <summary>How many times the keyboard's layout changed and every grab was made again (for the tests).</summary>
    internal int Regrabs => Volatile.Read(ref regrabs);

    private void Run()
    {
        try
        {
            display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                unavailable = DesktopSession.NoDisplayReason;
                return;
            }
            root = XDefaultRootWindow(display);
            int supported = 0;
            XkbSetDetectableAutoRepeat(display, 1, &supported); // held keys repeat as presses only: told apart below
            GrabErrors[display] = 0;
            lock (HandlerSync)
            {
                if (!handlerSet)
                {
                    previousHandler = XSetErrorHandler(&OnXError);
                    handlerSet = true;
                }
            }
            fixed (int* fds = wakePipe)
                if (pipe(fds) != 0) throw new IOException("pipe failed: " + Marshal.GetLastPInvokeError());
            const int SetFlags = 4, NonBlocking = 0x800; // F_SETFL, O_NONBLOCK (Linux)
            fcntl(wakePipe[0], SetFlags, NonBlocking);
            fcntl(wakePipe[1], SetFlags, NonBlocking);
        }
        catch (Exception ex)
        {
            AppLog.Error("X11 can't be used for hotkeys", ex);
            unavailable = DesktopSession.NoDisplayReason;
            display = IntPtr.Zero;
            return;
        }
        finally { ready.Set(); }

        var ev = stackalloc byte[192];
        var fds2 = stackalloc PollFd[2];
        var drain = stackalloc byte[64];
        int xfd = XConnectionNumber(display);
        while (!stopping)
        {
            while (work.TryDequeue(out var job)) // requests from the UI thread first
            {
                try { job(); } catch (Exception ex) { AppLog.Error("A hotkey request failed", ex); }
            }
            try
            {
                while (!stopping && XPending(display) > 0) // also flushes what we asked for
                {
                    XNextEvent(display, ev);
                    int type = ((XKeyEvent*)ev)->Type;
                    if (type == KeyPress) OnKey((int)((XKeyEvent*)ev)->Keycode, ((XKeyEvent*)ev)->State, pressed: true);
                    else if (type == KeyRelease) OnKey((int)((XKeyEvent*)ev)->Keycode, ((XKeyEvent*)ev)->State, pressed: false);
                    else if (type == MappingNotify)
                    {
                        XRefreshKeyboardMapping(ev); // the layout changed: the keys may have other codes now
                        GrabAgain();
                    }
                }
            }
            catch (Exception ex) { AppLog.Error("Reading X11 events failed", ex); }
            if (stopping || !work.IsEmpty) continue;
            // Sleep until X has something for us or a request wakes us (no busy loop).
            XFlush(display);
            fds2[0] = new PollFd { Fd = xfd, Events = PollIn };
            fds2[1] = new PollFd { Fd = wakePipe[0], Events = PollIn };
            poll(fds2, 2, 1000);
            if ((fds2[1].Revents & PollIn) != 0)
                while (read(wakePipe[0], drain, 64) > 0) { }
        }
        try
        {
            UngrabAll();
            XSync(display, 0);
            XCloseDisplay(display);
            GrabErrors.TryRemove(display, out _);
        }
        catch (Exception) { /* closing anyway */ }
        display = IntPtr.Zero;
        close(wakePipe[0]);
        close(wakePipe[1]);
    }

    /// <summary>Wakes the X thread (a request is waiting).</summary>
    private void Wake()
    {
        if (wakePipe[1] < 0) return;
        byte b = 1;
        write(wakePipe[1], &b, 1);
    }

    private void OnKey(int keycode, uint state, bool pressed)
    {
        var combo = (keycode, X11Keys.Clean(state));
        if (!pressed)
        {
            down.Remove(combo);
            down.RemoveWhere(d => d.Keycode == keycode); // released with other modifiers
            return;
        }
        if (!down.Add(combo)) return; // auto-repeat while held
        foreach (var (_, r) in registered)
        {
            if (r.Keycode != keycode || r.Mods != combo.Item2) continue;
            try { Pressed?.Invoke(r.Action); }
            catch (Exception ex) { AppLog.Error("A hotkey press failed", ex); }
            return;
        }
    }

    /// <summary>X thread, after the keyboard mapping changed: every hotkey is grabbed again with its key's code now.</summary>
    private void GrabAgain()
    {
        if (registered.Count == 0) return;
        foreach (var id in registered.Keys.ToList())
        {
            var r = registered[id];
            Ungrab(r.Keycode, r.Mods);
            int keycode = KeycodeOf(r.Key);
            if (keycode != 0 && Grab(keycode, r.Mods)) registered[id] = r with { Keycode = keycode };
            else
            {
                registered.Remove(id);
                AppLog.Info("After a keyboard layout change the hotkey " + r.Key + " couldn't be taken again");
            }
        }
        down.Clear();
        Volatile.Write(ref count, registered.Count);
        Interlocked.Increment(ref regrabs);
    }

    /// <summary>Runs on the X thread and waits for the answer (at most 2 s).</summary>
    private T OnXThread<T>(Func<T> job, T fallback)
    {
        if (!Available || stopping) return fallback;
        if (Environment.CurrentManagedThreadId == thread!.ManagedThreadId) return job();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Enqueue(() =>
        {
            try { done.SetResult(job()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        Wake();
        return done.Task.Wait(TimeSpan.FromSeconds(2)) ? done.Task.Result : fallback;
    }

    /// <summary>X thread: grabs the combination in its four variants; false (and nothing grabbed) when X refuses one.</summary>
    private bool Grab(int keycode, uint mods)
    {
        GrabErrors[display] = 0;
        foreach (var extra in X11Keys.IgnoredVariants) XGrabKey(display, keycode, mods | extra, root, 0, GrabModeAsync, GrabModeAsync);
        XSync(display, 0); // the errors of those requests arrive now
        if (GrabErrors[display] == 0) return true;
        foreach (var extra in X11Keys.IgnoredVariants) XUngrabKey(display, keycode, mods | extra, root);
        XSync(display, 0);
        GrabErrors[display] = 0;
        return false;
    }

    private void Ungrab(int keycode, uint mods)
    {
        foreach (var extra in X11Keys.IgnoredVariants) XUngrabKey(display, keycode, mods | extra, root);
    }

    private int KeycodeOf(Hotkey key) => X11Keys.Keysym(key.VirtualKey) is { } sym ? XKeysymToKeycode(display, sym) : 0;

    public bool Register(string actionId, Hotkey key) => OnXThread(() =>
    {
        int keycode = KeycodeOf(key);
        if (keycode == 0) return false; // no such key on this keyboard
        uint mods = X11Keys.Modifiers(key.Mods);
        foreach (var (_, r) in registered)
            if (r.Keycode == keycode && r.Mods == mods) return false; // ours already
        if (!Grab(keycode, mods)) return false;
        int id = nextId++;
        registered[id] = (actionId, key, keycode, mods);
        Volatile.Write(ref count, registered.Count);
        Volatile.Write(ref highest, id);
        return true;
    }, false);

    public HotkeyCheck Check(Hotkey key) => OnXThread(() =>
    {
        int keycode = KeycodeOf(key);
        if (keycode == 0) return HotkeyCheck.Unknown;
        uint mods = X11Keys.Modifiers(key.Mods);
        foreach (var (_, r) in registered)
            if (r.Keycode == keycode && r.Mods == mods) return HotkeyCheck.Taken;
        if (!Grab(keycode, mods)) return HotkeyCheck.Taken;
        Ungrab(keycode, mods);
        XSync(display, 0);
        return HotkeyCheck.Free;
    }, HotkeyCheck.Unknown);

    public void UnregisterAll() => OnXThread(() =>
    {
        UngrabAll();
        XSync(display, 0);
        return true;
    }, true);

    private void UngrabAll()
    {
        foreach (var (_, r) in registered) Ungrab(r.Keycode, r.Mods);
        registered.Clear();
        down.Clear();
        nextId = 1;
        Volatile.Write(ref count, 0);
        Volatile.Write(ref highest, 0);
    }

    public int Count => Volatile.Read(ref count);

    public int HighestId => Volatile.Read(ref highest);

    /// <summary>For the tests: a press through the same path a real X event takes.</summary>
    public bool SimulatePress(string actionId) => OnXThread(() =>
    {
        foreach (var (_, r) in registered)
        {
            if (r.Action != actionId) continue;
            OnKey(r.Keycode, r.Mods, pressed: true);
            OnKey(r.Keycode, r.Mods, pressed: false);
            return true;
        }
        return false;
    }, false);

    /// <summary>For the tests: what a MappingNotify does (every grab made again).</summary>
    internal bool SimulateLayoutChange() => OnXThread(() =>
    {
        GrabAgain();
        return true;
    }, false);

    public void Dispose()
    {
        if (thread == null || stopping) return;
        stopping = true;
        Wake();
        thread.Join(TimeSpan.FromSeconds(2));
    }
}
