using System.ComponentModel;

namespace Dictee;

/// <summary>
/// Global low-level keyboard hook running on its own thread, so a busy UI can never make Windows drop it.
/// A short tap of the hotkey is replayed untouched; holding it past <see cref="HoldDelayMs"/> starts a dictation.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    const uint WM_QUIT = 0x0012;
    const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10;
    const int VK_ESCAPE = 0x1B;
    const int HoldDelayMs = 250;

    enum State { Idle, Pending, Holding, Passthrough, Cancelled }

    readonly object gate = new();
    readonly SynchronizationContext ui;
    readonly Native.LowLevelKeyboardProc proc;
    readonly System.Threading.Timer holdTimer;
    readonly Thread thread;
    uint threadId;
    IntPtr handle;

    State state;
    long pressedAt;
    Hotkey hotkey;
    bool enabled = true;
    bool disposed;
    Action<Hotkey>? capture;
    int swallowVk;

    /// <summary>Hotkey went down: start recording right away so the first words are kept.</summary>
    public event Action? Pressed;
    /// <summary>Hotkey held long enough: this is a dictation.</summary>
    public event Action? Held;
    /// <summary>Dictation finished: transcribe.</summary>
    public event Action? Released;
    /// <summary>Short tap, key combination or Escape: drop the recording.</summary>
    public event Action? Aborted;

    public KeyboardHook()
    {
        ui = SynchronizationContext.Current ?? throw new InvalidOperationException("A UI synchronization context is required.");
        proc = Callback;
        holdTimer = new System.Threading.Timer(_ => OnHoldElapsed());

        using var ready = new ManualResetEventSlim();
        thread = new Thread(() =>
        {
            threadId = Native.GetCurrentThreadId();
            handle = Native.SetWindowsHookEx(WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
            ready.Set();
            if (handle == IntPtr.Zero) return;
            while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            Native.UnhookWindowsHookEx(handle);
        })
        { IsBackground = true, Name = "Dictee keyboard hook", Priority = ThreadPriority.Highest };
        thread.Start();
        ready.Wait();

        if (handle == IntPtr.Zero) throw new Win32Exception("Impossible d'installer le hook clavier.");
    }

    public Hotkey Hotkey
    {
        get { lock (gate) return hotkey; }
        set { lock (gate) { hotkey = value; state = State.Idle; } }
    }

    public bool Enabled
    {
        set
        {
            lock (gate)
            {
                enabled = value;
                if (value || state == State.Idle) return;
                // The key-up may never reach us while disabled: don't stay stuck mid-press.
                if (state is State.Pending or State.Holding) Raise(Aborted);
                state = State.Idle;
                StopHoldTimer();
            }
        }
    }

    /// <summary>The next key pressed anywhere is swallowed and handed to <paramref name="onCaptured"/> on the UI thread.</summary>
    public void BeginCapture(Action<Hotkey> onCaptured)
    {
        lock (gate) capture = onCaptured;
    }

    public void CancelCapture()
    {
        lock (gate) capture = null;
    }

    unsafe IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            bool swallow;
            lock (gate) swallow = Handle((int)wParam, (Native.KBDLLHOOKSTRUCT*)lParam);
            if (swallow) return 1;
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    unsafe bool Handle(int message, Native.KBDLLHOOKSTRUCT* key)
    {
        if (disposed || (key->flags & LLKHF_INJECTED) != 0) return false;

        bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        int vk = (int)key->vkCode;

        if (vk == swallowVk)
        {
            if (!down) swallowVk = 0;
            return true;
        }

        if (capture is { } onCaptured)
        {
            if (!down) return false;
            capture = null;
            swallowVk = vk;
            var captured = new Hotkey(vk, (int)key->scanCode, (key->flags & LLKHF_EXTENDED) != 0);
            ui.Post(_ => onCaptured(captured), null);
            return true;
        }

        if (!enabled) return false;

        if (vk == hotkey.Vk) return down ? OnHotkeyDown() : OnHotkeyUp();
        if (!down) return false;

        if (state == State.Pending)
        {
            // Hotkey used as part of a combination (e.g. Ctrl+C): give the hotkey back, then this key.
            state = State.Passthrough;
            StopHoldTimer();
            Raise(Aborted);
            InputSender.Send(hotkey.Stroke(up: false), new KeyStroke(vk, false, (int)key->scanCode, (key->flags & LLKHF_EXTENDED) != 0));
            return true;
        }

        if (state == State.Holding && vk == VK_ESCAPE)
        {
            state = State.Cancelled;
            swallowVk = VK_ESCAPE;
            Raise(Aborted);
            return true;
        }

        return false;
    }

    bool OnHotkeyDown()
    {
        switch (state)
        {
            case State.Passthrough:
                return false;
            case State.Idle:
                state = State.Pending;
                pressedAt = Environment.TickCount64;
                holdTimer.Change(HoldDelayMs, Timeout.Infinite);
                Raise(Pressed);
                return true;
            default:
                return true; // auto-repeat while held
        }
    }

    bool OnHotkeyUp()
    {
        var previous = state;
        state = State.Idle;
        StopHoldTimer();

        switch (previous)
        {
            case State.Pending:
                Raise(Aborted);
                InputSender.Send(hotkey.Stroke(up: false), hotkey.Stroke(up: true));
                return true;
            case State.Holding:
                Raise(Released);
                return true;
            case State.Cancelled:
                return true;
            default:
                return false;
        }
    }

    void OnHoldElapsed()
    {
        lock (gate)
        {
            // A late tick from a previous press must not promote the current one early.
            if (disposed || state != State.Pending || Environment.TickCount64 - pressedAt < HoldDelayMs - 20) return;
            state = State.Holding;
            Raise(Held);
        }
    }

    void StopHoldTimer() => holdTimer.Change(Timeout.Infinite, Timeout.Infinite);

    void Raise(Action? handler)
    {
        if (handler != null) ui.Post(_ => handler(), null);
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            holdTimer.Dispose();
        }
        Native.PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
