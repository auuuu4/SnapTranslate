using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SnapTranslate;

internal readonly record struct LowLevelKeyboardHookEvent(
    int Message,
    uint VirtualKey,
    uint ScanCode,
    uint Flags,
    uint Time,
    UIntPtr ExtraInfo);

internal sealed class LowLevelKeyboardHookHost : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;
    private const int ErrorTimeout = 1460;
    private static readonly TimeSpan LifecycleTimeout = TimeSpan.FromSeconds(5);

    private readonly object _lifecycleGate = new();
    private readonly Func<LowLevelKeyboardHookEvent, bool> _handler;
    private readonly HookProc _hookProc;
    private Thread? _thread;
    private IntPtr _hookHandle;
    private uint _nativeThreadId;
    private Exception? _lastCallbackException;
    private int _messageLoopError;
    private int _stopRequested;
    private bool _disposed;

    public LowLevelKeyboardHookHost(Func<LowLevelKeyboardHookEvent, bool> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _hookProc = HookCallback;
    }

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleGate)
                return _thread is { IsAlive: true }
                       && Volatile.Read(ref _hookHandle) != IntPtr.Zero;
        }
    }

    public Exception? LastCallbackException => Volatile.Read(ref _lastCallbackException);

    public int MessageLoopError => Volatile.Read(ref _messageLoopError);

    public bool TryStart(out int win32Error)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is { IsAlive: true }
                && Volatile.Read(ref _hookHandle) != IntPtr.Zero)
            {
                win32Error = 0;
                return true;
            }

            if (_thread is not null)
            {
                if (!TryStopCore(out win32Error)) return false;
            }

            Volatile.Write(ref _messageLoopError, 0);
            Volatile.Write(ref _lastCallbackException, null);
            Volatile.Write(ref _stopRequested, 0);
            var startup = new StartupResult();
            var thread = new Thread(() => RunMessageLoop(startup))
            {
                IsBackground = true,
                Name = "SnapTranslate keyboard hook"
            };
            thread.SetApartmentState(ApartmentState.MTA);
            _thread = thread;
            thread.Start();

            if (!startup.Completed.Wait(LifecycleTimeout))
            {
                Volatile.Write(ref _stopRequested, 1);
                var threadId = Volatile.Read(ref _nativeThreadId);
                if (threadId != 0) _ = PostThreadMessage(threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
                _ = thread.Join(LifecycleTimeout);
                win32Error = ErrorTimeout;
                return false;
            }
            win32Error = startup.Win32Error;
            if (startup.Succeeded) return true;

            if (!thread.Join(LifecycleTimeout))
            {
                win32Error = ErrorTimeout;
                return false;
            }
            if (ReferenceEquals(_thread, thread)) _thread = null;
            return false;
        }
    }

    public void Start()
    {
        if (!TryStart(out var win32Error))
            throw new Win32Exception(win32Error, "Unable to install the low-level keyboard hook.");
    }

    public void Stop()
    {
        lock (_lifecycleGate)
        {
            if (!TryStopCore(out var win32Error))
                throw new Win32Exception(win32Error, "Unable to stop the low-level keyboard hook thread.");
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _ = TryStopCore(out _);
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private bool TryStopCore(out int win32Error)
    {
        win32Error = 0;
        var thread = _thread;
        if (thread is null) return true;

        if (thread.IsAlive)
        {
            Volatile.Write(ref _stopRequested, 1);
            var threadId = Volatile.Read(ref _nativeThreadId);
            if (threadId != 0)
            {
                var posted = PostThreadMessage(threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
                if (!posted && thread.IsAlive) win32Error = Marshal.GetLastWin32Error();
            }

            if (Thread.CurrentThread != thread && !thread.Join(LifecycleTimeout))
            {
                var hookHandle = Interlocked.Exchange(ref _hookHandle, IntPtr.Zero);
                if (hookHandle != IntPtr.Zero) _ = UnhookWindowsHookEx(hookHandle);
                win32Error = ErrorTimeout;
                return false;
            }
        }

        if (!thread.IsAlive && ReferenceEquals(_thread, thread)) _thread = null;
        return !thread.IsAlive || Thread.CurrentThread == thread;
    }

    private void RunMessageLoop(StartupResult startup)
    {
        IntPtr hookHandle = IntPtr.Zero;
        try
        {
            _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
            Volatile.Write(ref _nativeThreadId, GetCurrentThreadId());
            if (Volatile.Read(ref _stopRequested) != 0) return;

            hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
            if (hookHandle == IntPtr.Zero)
            {
                startup.Win32Error = Marshal.GetLastWin32Error();
                return;
            }
            if (Volatile.Read(ref _stopRequested) != 0) return;

            Volatile.Write(ref _hookHandle, hookHandle);
            startup.Succeeded = true;
            startup.Completed.Set();

            int result;
            while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                _ = TranslateMessage(in message);
                _ = DispatchMessage(in message);
            }

            if (result < 0) Volatile.Write(ref _messageLoopError, Marshal.GetLastWin32Error());
        }
        finally
        {
            if (!startup.Completed.IsSet) startup.Completed.Set();
            if (hookHandle != IntPtr.Zero) _ = UnhookWindowsHookEx(hookHandle);
            Interlocked.CompareExchange(ref _hookHandle, IntPtr.Zero, hookHandle);
            Volatile.Write(ref _nativeThreadId, 0);
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        try
        {
            var input = Marshal.PtrToStructure<KeyboardInput>(lParam);
            var keyboardEvent = new LowLevelKeyboardHookEvent(
                wParam.ToInt32(),
                input.VirtualKey,
                input.ScanCode,
                input.Flags,
                input.Time,
                input.ExtraInfo);
            if (_handler(keyboardEvent)) return new IntPtr(1);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _lastCallbackException, ex);
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private sealed class StartupResult
    {
        public ManualResetEventSlim Completed { get; } = new(false);
        public bool Succeeded { get; set; }
        public int Win32Error { get; set; }
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        HookProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(
        out NativeMessage message,
        IntPtr window,
        uint minimumMessage,
        uint maximumMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(in NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage message,
        IntPtr window,
        uint minimumMessage,
        uint maximumMessage,
        uint removeMessage);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
