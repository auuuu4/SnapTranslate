using System.Runtime.InteropServices;

namespace SnapTranslate.Desktop.Platform;

/// <summary>
/// macOS 全局快捷键：走 Carbon 的 RegisterEventHotKey。
/// 相比 CGEventTap 它不需要「辅助功能」权限，快捷键只在本进程收到。
/// 非 macOS 平台上 IsSupported 为 false，注册直接失败并给出原因。
/// </summary>
public sealed class CarbonHotkeyService : IGlobalHotkeyService
{
    private const string CarbonLibrary = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const uint EventClassKeyboard = 0x6B657962;   // 'keyb'
    private const uint EventHotKeyPressed = 5;
    private const uint ParamDirectObject = 0x2D2D2D2D;   // '----'
    private const uint TypeEventHotKeyId = 0x686B6964;   // 'hkid'
    private const uint Signature = 0x534E4150;           // 'SNAP'

    private readonly object _gate = new();
    private readonly List<IntPtr> _hotkeyRefs = [];
    private readonly Dictionary<uint, GlobalHotkey> _registered = new();
    private EventHandlerProc? _handler;
    private IntPtr _handlerRef;
    private uint _nextId = 1;
    private bool _disposed;

    public bool IsSupported => OperatingSystem.IsMacOS();

    public IReadOnlyList<GlobalHotkey> Active { get; private set; } = Array.Empty<GlobalHotkey>();

    public event EventHandler<string>? Pressed;

    public bool TryRegister(IEnumerable<GlobalHotkey> hotkeys, out string error)
    {
        error = string.Empty;
        if (!IsSupported)
        {
            error = "当前平台不支持全局快捷键";
            return false;
        }

        var list = hotkeys.Where(item => !string.IsNullOrWhiteSpace(item.Gesture)).ToArray();
        if (list.Length == 0)
        {
            UnregisterAll();
            return true;
        }

        lock (_gate)
        {
            UnregisterAllLocked();
            if (!TryInstallHandler(out error)) return false;

            var active = new List<GlobalHotkey>(list.Length);
            foreach (var hotkey in list)
            {
                if (!HotkeyGesture.TryParse(hotkey.Gesture, out var gesture, out var parseError))
                {
                    error = parseError;
                    UnregisterAllLocked();
                    return false;
                }

                var id = _nextId++;
                var hotKeyId = new EventHotKeyId { Signature = Signature, Id = id };
                var target = GetApplicationEventTarget();
                var status = RegisterEventHotKey(
                    gesture.KeyCode,
                    gesture.Modifiers,
                    hotKeyId,
                    target,
                    0,
                    out var reference);
                if (status != 0 || reference == IntPtr.Zero)
                {
                    error = $"快捷键「{hotkey.Gesture}」注册失败（系统可能已占用），错误码 {status}";
                    UnregisterAllLocked();
                    return false;
                }

                _hotkeyRefs.Add(reference);
                _registered[id] = hotkey with { Gesture = gesture.CanonicalText };
                active.Add(_registered[id]);
            }

            Active = active;
            return true;
        }
    }

    public void UnregisterAll()
    {
        lock (_gate)
        {
            UnregisterAllLocked();
        }
    }

    private void UnregisterAllLocked()
    {
        foreach (var reference in _hotkeyRefs)
        {
            try
            {
                UnregisterEventHotKey(reference);
            }
            catch (Exception)
            {
                // 退出阶段系统可能已经回收，忽略即可。
            }
        }

        _hotkeyRefs.Clear();
        _registered.Clear();
        Active = Array.Empty<GlobalHotkey>();
    }

    private bool TryInstallHandler(out string error)
    {
        error = string.Empty;
        if (_handlerRef != IntPtr.Zero) return true;

        _handler = HandleEvent;
        var spec = new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed };
        var status = InstallEventHandler(
            GetApplicationEventTarget(),
            _handler,
            1,
            [spec],
            IntPtr.Zero,
            out _handlerRef);
        if (status != 0 || _handlerRef == IntPtr.Zero)
        {
            _handlerRef = IntPtr.Zero;
            error = $"无法安装快捷键事件处理器，错误码 {status}";
            return false;
        }

        return true;
    }

    private int HandleEvent(IntPtr nextHandler, IntPtr theEvent, IntPtr userData)
    {
        try
        {
            var hotKeyId = default(EventHotKeyId);
            var status = GetEventParameter(
                theEvent,
                ParamDirectObject,
                TypeEventHotKeyId,
                IntPtr.Zero,
                (uint)Marshal.SizeOf<EventHotKeyId>(),
                out _,
                ref hotKeyId);
            if (status != 0 || hotKeyId.Signature != Signature) return 0;

            GlobalHotkey? hotkey = null;
            lock (_gate)
            {
                if (_registered.TryGetValue(hotKeyId.Id, out var value)) hotkey = value;
            }

            if (hotkey is not null) Pressed?.Invoke(this, hotkey.Id);
        }
        catch (Exception)
        {
            // 事件回调里绝不能让异常穿回原生栈。
        }

        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        if (_handlerRef != IntPtr.Zero)
        {
            try
            {
                RemoveEventHandler(_handlerRef);
            }
            catch (Exception)
            {
            }

            _handlerRef = IntPtr.Zero;
        }

        _handler = null;
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    private delegate int EventHandlerProc(IntPtr nextHandler, IntPtr theEvent, IntPtr userData);

    [DllImport(CarbonLibrary)]
    private static extern IntPtr GetApplicationEventTarget();

    [DllImport(CarbonLibrary)]
    private static extern int RegisterEventHotKey(
        ushort inHotKeyCode,
        uint inHotKeyModifiers,
        EventHotKeyId inHotKeyId,
        IntPtr inTarget,
        uint inOptions,
        out IntPtr outRef);

    [DllImport(CarbonLibrary)]
    private static extern int UnregisterEventHotKey(IntPtr inHotKeyRef);

    [DllImport(CarbonLibrary)]
    private static extern int InstallEventHandler(
        IntPtr inTarget,
        EventHandlerProc inHandler,
        uint inNumTypes,
        [In] EventTypeSpec[] inList,
        IntPtr inUserData,
        out IntPtr outRef);

    [DllImport(CarbonLibrary)]
    private static extern int RemoveEventHandler(IntPtr inHandlerRef);

    [DllImport(CarbonLibrary)]
    private static extern int GetEventParameter(
        IntPtr inEvent,
        uint inName,
        uint inDesiredType,
        IntPtr outActualType,
        uint inBufferSize,
        out uint outActualSize,
        ref EventHotKeyId outData);
}
