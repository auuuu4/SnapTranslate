using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace SnapTranslate;

public partial class MainWindow : Window
{
    private const int HotkeyInput = 1;
    private const int HotkeyTranslate = 2;
    private const int HotkeySilentOcr = 3;
    private const int HotkeyOcr = 4;
    private const int HotkeyClipboard = 5;
    private const int HotkeyScreenshot = 6;
    private const int HotkeyPinnedImage = 7;
    private const int ClipboardTabIndex = 3;
    private const int WmHotkey = 0x0312;
    private const int WmClipboardUpdate = 0x031D;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int MaximumHookEventAgeMilliseconds = 450;
    private const uint VkV = 0x56;
    private const uint VkLeftWin = 0x5B;
    private const uint VkRightWin = 0x5C;
    private const byte VkControl = 0x11;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint LlkhfInjected = 0x00000010;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly SettingsService _storage = new();
    private readonly OcrService _ocr = new();
    private readonly TranslationService _translator = new();
    private readonly ObservableCollection<HistoryEntry> _history;
    private IReadOnlyList<ClipboardItem> _clipboardItems = Array.Empty<ClipboardItem>();
    private readonly ObservableCollection<DocumentTranslationTask> _documentTasks = new();
    private readonly ICollectionView _historyView;
    private readonly ICollectionView _documentTasksView;
    private readonly ClipboardRepository _clipboardRepository;
    private readonly DocumentTranslationService _documentTranslationService;
    private readonly SemaphoreSlim _documentTaskSlots = new(2, 2);
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _foregroundTimer;
    private readonly DispatcherTimer _documentTaskSaveTimer;
    private readonly DispatcherTimer _clipboardSearchRefreshTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(140)
    };
    private readonly DispatcherTimer _settingsAutoSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(700)
    };
    private bool _suppressSettingsAutoSave = true;
    private bool _suppressQuickLanguageSave;
    private readonly LowLevelKeyboardHookHost _keyboardHookHost;
    private readonly HotkeyExecutionGate _hotkeyExecutionGate = new();
    private readonly object _hookStateGate = new();
    private AppSettings _settings;
    private HwndSource? _source;
    private ClipboardWindow? _clipboardWindow;
    private IntPtr _lastExternalWindow;
    private List<ChatApiProfile> _chatProfiles = new();
    private string _activeChatProfileId = string.Empty;
    private bool _bindingChatProfile;
    private string? _suppressedClipboardHash;
    private DateTimeOffset _suppressedClipboardUntil;
    private uint _suppressedClipboardSequence;
    private uint _lastClipboardNotificationSequence;
    private uint _lastCapturedClipboardSequence;
    private IntPtr _mainWindowHandle;
    private HotkeyRegistration _hookRegistration = new(0, new Dictionary<GlobalHotkey, int>());
    private readonly HashSet<uint> _suppressedHotkeyKeys = new();
    private readonly HashSet<uint> _pressedModifierKeys = new();
    private readonly HashSet<int> _registeredHotkeyIds = new();
    private readonly Dictionary<string, long> _clipboardFavoriteRevisions = new();
    private readonly Dictionary<string, long> _clipboardNoteRevisions = new();
    private CancellationTokenSource? _inputTranslationCancellation;
    private CancellationTokenSource? _clipboardMainRefreshCancellation;
    private int _inputTranslationRequestId;
    private uint _hookModifierState;
    private int _hotkeyRegistrationGeneration;
    private int _hotkeyEditorFocused;
    private int _clipboardUseInProgress;
    private long _clipboardUseGeneration;
    private int _clipboardSelfWritePending;
    private bool _allowClose;
    private bool _captureInProgress;
    private bool _clipboardReadInProgress;
    private bool _clipboardReadQueued;
    private bool _clipboardReadDeferred;
    private bool _clipboardListenerRegistered;
    private bool _clipboardMainDirty;
    private long _clipboardMutationRevision;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _storage.LoadSettings();
        _history = new ObservableCollection<HistoryEntry>(_storage.LoadHistory());
        _historyView = CollectionViewSource.GetDefaultView(_history);
        _historyView.Filter = FilterHistoryEntry;
        HistoryGrid.ItemsSource = _historyView;
        _clipboardRepository = new ClipboardRepository();
        _documentTranslationService = new DocumentTranslationService(_translator);
        ClipboardGrid.ItemsSource = _clipboardItems;
        foreach (var snapshot in _storage.LoadDocumentTasks())
        {
            try { AttachDocumentTask(DocumentTranslationTask.Restore(snapshot), false); }
            catch (Exception ex) { AppLog.Error("RestoreDocumentTask", ex); }
        }
        _documentTasksView = CollectionViewSource.GetDefaultView(_documentTasks);
        _documentTasksView.Filter = FilterDocumentTask;
        DocumentTaskGrid.ItemsSource = _documentTasksView;
        UpdateDocumentTaskCount();
        _clipboardSearchRefreshTimer.Tick += (_, _) =>
        {
            _clipboardSearchRefreshTimer.Stop();
            RefreshClipboardItems();
        };
        RefreshClipboardItems();
        MainTabs.SelectionChanged += MainTabs_SelectionChanged;
        // 设置页的任何改动都自动落盘：以前只有点「保存设置」才写，关掉窗口就白改了
        _settingsAutoSaveTimer.Tick += (_, _) => AutoSaveSettings();
        SettingsScrollViewer.AddHandler(
            System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => QueueSettingsAutoSave()),
            true);
        SettingsScrollViewer.AddHandler(
            System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler((_, _) => QueueSettingsAutoSave()),
            true);
        SettingsScrollViewer.AddHandler(
            System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
            new RoutedEventHandler((_, _) => QueueSettingsAutoSave()),
            true);
        SettingsScrollViewer.AddHandler(
            System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent,
            new RoutedEventHandler((_, _) => QueueSettingsAutoSave()),
            true);

        InitializeTranslationSelectors();
        LoadSettingsControls();
        ApplyWindowPreferences();
        _suppressSettingsAutoSave = false;
        _keyboardHookHost = new LowLevelKeyboardHookHost(HandleKeyboardHookEvent);
        foreach (var editor in new[]
                 {
                     InputHotkeyText,
                     ScreenshotHotkeyText,
                     ScreenshotTranslateHotkeyText,
                     SilentOcrHotkeyText,
                     ScreenshotOcrHotkeyText,
                     ClipboardHotkeyText,
                     PinnedImageHotkeyText
                 })
        {
            editor.GotKeyboardFocus += HotkeyEditor_GotKeyboardFocus;
            editor.LostKeyboardFocus += HotkeyEditor_LostKeyboardFocus;
        }

        _trayIcon = BuildTrayIcon();
        _foregroundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _foregroundTimer.Tick += (_, _) => RememberForegroundWindow();
        _foregroundTimer.Start();
        _documentTaskSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _documentTaskSaveTimer.Tick += (_, _) =>
        {
            _documentTaskSaveTimer.Stop();
            PersistDocumentTasks();
        };
        if (_documentTasks.Count > 0) ScheduleDocumentTasksSave();
        Loaded += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_storage.LastLoadWarning))
                FooterStatus.Text = _storage.LastLoadWarning;
            else if (string.IsNullOrWhiteSpace(FooterStatus.Text))
                FooterStatus.Text = "本地 OCR 已就绪";
            if (_settings.StartWithWindows) StartupService.SetEnabled(true);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _mainWindowHandle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_mainWindowHandle);
        _source.AddHook(WndProc);
        RegisterHotkeys();
        SetClipboardListener(_settings.ClipboardEnabled);
    }

    private void RegisterHotkeys()
    {
        FooterStatus.Text = TryRegisterHotkeys(_settings, out var error)
            ? $"本地 OCR 已就绪 · 全局快捷键 {_hookRegistration.Hotkeys.Count} 个"
            : error;
    }

    private bool TryRegisterHotkeys(AppSettings settings, out string error)
    {
        UnregisterAllHotkeys();
        var definitions = new[]
        {
            new HotkeyDefinition(HotkeyInput, "输入翻译", settings.InputHotkey),
            new HotkeyDefinition(HotkeyScreenshot, "截图", settings.ScreenshotHotkey),
            new HotkeyDefinition(HotkeyTranslate, "截图翻译", settings.ScreenshotTranslateHotkey),
            new HotkeyDefinition(HotkeySilentOcr, "静默 OCR", settings.SilentOcrHotkey),
            new HotkeyDefinition(HotkeyOcr, "截图 OCR", settings.ScreenshotOcrHotkey),
            new HotkeyDefinition(HotkeyClipboard, "剪贴板", settings.ClipboardHotkey),
            new HotkeyDefinition(HotkeyPinnedImage, "贴图", settings.PinnedImageHotkey)
        };
        var parsed = new List<(HotkeyDefinition Definition, GlobalHotkey Hotkey)>();
        var unique = new HashSet<(uint Modifiers, uint VirtualKey)>();

        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Value)) continue;
            if (!TryParseHotkey(definition.Value, out var hotkey))
            {
                error = $"快捷键格式无效：{definition.Name}";
                return false;
            }
            if (!unique.Add((hotkey.Modifiers, hotkey.VirtualKey)))
            {
                error = $"快捷键重复：{definition.Value}";
                return false;
            }
            parsed.Add((definition, hotkey));
        }

        var registration = new HotkeyRegistration(
            Interlocked.Increment(ref _hotkeyRegistrationGeneration),
            parsed.ToDictionary(item => item.Hotkey, item => item.Definition.Id));
        Volatile.Write(ref _hookRegistration, registration);
        if (registration.Hotkeys.Count > 0 && !SetKeyboardHook(true))
        {
            UnregisterAllHotkeys();
            error = "无法启用全局快捷键";
            return false;
        }

        RegisterSystemHotkeyFallbacks(parsed);
        error = string.Empty;
        return true;
    }

    private void RegisterSystemHotkeyFallbacks(
        IEnumerable<(HotkeyDefinition Definition, GlobalHotkey Hotkey)> hotkeys)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        foreach (var hotkey in hotkeys)
        {
            var reservedWindowsClipboard = hotkey.Definition.Id == HotkeyClipboard
                                           && hotkey.Hotkey.Modifiers == ModWin
                                           && hotkey.Hotkey.VirtualKey == VkV;
            if (!reservedWindowsClipboard
                && RegisterHotKey(
                    handle,
                    hotkey.Definition.Id,
                    hotkey.Hotkey.Modifiers | ModNoRepeat,
                    hotkey.Hotkey.VirtualKey))
            {
                _registeredHotkeyIds.Add(hotkey.Definition.Id);
            }
        }
    }

    private void UnregisterSystemHotkeyFallbacks()
    {
        var handle = new WindowInteropHelper(this).Handle;
        foreach (var id in _registeredHotkeyIds) UnregisterHotKey(handle, id);
        _registeredHotkeyIds.Clear();
    }

    private void UnregisterAllHotkeys()
    {
        UnregisterSystemHotkeyFallbacks();
        Volatile.Write(
            ref _hookRegistration,
            new HotkeyRegistration(
                Interlocked.Increment(ref _hotkeyRegistrationGeneration),
                new Dictionary<GlobalHotkey, int>()));
        SetKeyboardHook(false);
        lock (_hookStateGate)
        {
            _suppressedHotkeyKeys.Clear();
            _pressedModifierKeys.Clear();
            _hookModifierState = 0;
        }
        _hotkeyExecutionGate.Reset();
    }

    private bool SetKeyboardHook(bool enabled)
    {
        if (!enabled)
        {
            try
            {
                _keyboardHookHost.Stop();
            }
            catch (Exception ex)
            {
                AppLog.Error(nameof(SetKeyboardHook), ex);
                return false;
            }
            lock (_hookStateGate)
            {
                _suppressedHotkeyKeys.Clear();
                _pressedModifierKeys.Clear();
                _hookModifierState = 0;
            }
            return true;
        }

        return _keyboardHookHost.TryStart(out _);
    }

    private bool HandleKeyboardHookEvent(LowLevelKeyboardHookEvent input)
    {
        var ownInjected = (input.Flags & LlkhfInjected) != 0
                          && input.ExtraInfo == SyntheticKeyboardInputMarker.Value;
        var keyDown = input.Message is WmKeyDown or WmSysKeyDown;
        var keyUp = input.Message is WmKeyUp or WmSysKeyUp;
        var modifier = ModifierForVirtualKey(input.VirtualKey);

        if (modifier != 0)
        {
            if (!ownInjected)
            {
                lock (_hookStateGate)
                {
                    if (keyDown) _pressedModifierKeys.Add(input.VirtualKey);
                    if (keyUp) _pressedModifierKeys.Remove(input.VirtualKey);
                    _hookModifierState = 0;
                    foreach (var pressedKey in _pressedModifierKeys)
                        _hookModifierState |= ModifierForVirtualKey(pressedKey);
                }
            }
            return false;
        }

        if (ownInjected) return false;
        if (keyUp)
        {
            lock (_hookStateGate)
            {
                if (_suppressedHotkeyKeys.Remove(input.VirtualKey)) return true;
            }
        }
        if (keyDown && !IsHotkeyEditorActive())
        {
            uint modifierState;
            lock (_hookStateGate) modifierState = _hookModifierState;
            var hotkey = new GlobalHotkey(modifierState, input.VirtualKey);
            var registration = Volatile.Read(ref _hookRegistration);
            if (registration.Hotkeys.TryGetValue(hotkey, out var id))
            {
                var shouldSuppress = false;
                lock (_hookStateGate) shouldSuppress = _suppressedHotkeyKeys.Add(input.VirtualKey);
                if (shouldSuppress)
                {
                    var fresh = HotkeyEventTiming.IsFresh(
                        input.Time,
                        TimeSpan.FromMilliseconds(MaximumHookEventAgeMilliseconds));
                    if (fresh
                        && id == HotkeyClipboard
                        && hotkey.Modifiers == ModWin
                        && hotkey.VirtualKey == VkV)
                    {
                        keybd_event(VkControl, 0, 0, SyntheticKeyboardInputMarker.Value);
                        keybd_event(VkControl, 0, KeyeventfKeyup, SyntheticKeyboardInputMarker.Value);
                    }
                    if (fresh)
                        QueueHotkeyExecution(id, input.Time, registration.Generation, true);
                }
                return true;
            }
        }
        return false;
    }

    private static uint ModifierForVirtualKey(uint virtualKey) => virtualKey switch
    {
        0x10 or 0xA0 or 0xA1 => ModShift,
        0x11 or 0xA2 or 0xA3 => ModControl,
        0x12 or 0xA4 or 0xA5 => ModAlt,
        VkLeftWin or VkRightWin => ModWin,
        _ => 0
    };

    private bool IsHotkeyEditorActive()
    {
        return Volatile.Read(ref _hotkeyEditorFocused) != 0
               && GetForegroundWindow() == _mainWindowHandle;
    }

    private void HotkeyEditor_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => Volatile.Write(ref _hotkeyEditorFocused, 1);

    private void HotkeyEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => Volatile.Write(ref _hotkeyEditorFocused, 0);

    private void ExecuteHotkey(
        int hotkeyId,
        uint? nativeEventTime,
        int registrationGeneration,
        bool fromKeyboardHook)
    {
        switch (hotkeyId)
        {
            case HotkeyInput:
                ShowMain();
                MainTabs.SelectedIndex = 0;
                InputText.Focus();
                break;
            case HotkeyScreenshot:
                _ = RunCaptureAsync(CaptureAction.Screenshot);
                break;
            case HotkeyTranslate:
                _ = RunCaptureAsync(CaptureAction.Translate);
                break;
            case HotkeySilentOcr:
                _ = RunCaptureAsync(CaptureAction.SilentOcr);
                break;
            case HotkeyOcr:
                _ = RunCaptureAsync(CaptureAction.Ocr);
                break;
            case HotkeyClipboard:
                ShowClipboardPanel();
                break;
            case HotkeyPinnedImage:
                _ = PinClipboardImageAsync();
                break;
        }
    }

    private void QueueHotkeyExecution(
        int hotkeyId,
        uint? nativeEventTime = null,
        int? expectedRegistrationGeneration = null,
        bool fromKeyboardHook = false)
    {
        var registrationGeneration = expectedRegistrationGeneration
                                     ?? Volatile.Read(ref _hookRegistration).Generation;
        if (registrationGeneration != Volatile.Read(ref _hookRegistration).Generation) return;
        var maximumAge = TimeSpan.FromMilliseconds(MaximumHookEventAgeMilliseconds);
        if (nativeEventTime is { } eventTime
            && !HotkeyEventTiming.IsFresh(eventTime, maximumAge))
            return;
        var clipboardUseGeneration = Volatile.Read(ref _clipboardUseGeneration);
        if (hotkeyId == HotkeyClipboard
            && Volatile.Read(ref _clipboardUseInProgress) != 0)
            return;

        var debounce = fromKeyboardHook
            ? TimeSpan.Zero
            : hotkeyId == HotkeyClipboard
            ? TimeSpan.FromMilliseconds(240)
            : TimeSpan.FromMilliseconds(90);
        if (!_hotkeyExecutionGate.TryEnter(hotkeyId, debounce, out var lease)) return;

        try
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                (Action)(() =>
                {
                    try
                    {
                        if (registrationGeneration == Volatile.Read(ref _hookRegistration).Generation
                            && (nativeEventTime is not { } queuedEventTime
                                || HotkeyEventTiming.IsFresh(queuedEventTime, maximumAge))
                            && (hotkeyId != HotkeyClipboard
                                || (clipboardUseGeneration == Volatile.Read(ref _clipboardUseGeneration)
                                    && Volatile.Read(ref _clipboardUseInProgress) == 0)))
                            ExecuteHotkey(
                                hotkeyId,
                                nativeEventTime,
                                registrationGeneration,
                                fromKeyboardHook);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error(nameof(ExecuteHotkey), ex);
                    }
                    finally
                    {
                        _hotkeyExecutionGate.Exit(lease);
                    }
                }));
        }
        catch (Exception ex)
        {
            _hotkeyExecutionGate.Exit(lease);
            AppLog.Error(nameof(QueueHotkeyExecution), ex);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            handled = true;
            if (!IsHotkeyEditorActive())
                QueueHotkeyExecution(wParam.ToInt32(), unchecked((uint)GetMessageTime()));
        }
        else if (msg == WmClipboardUpdate)
        {
            var sequence = GetClipboardSequenceNumber();
            if (Volatile.Read(ref _clipboardSelfWritePending) != 0)
            {
                QueueClipboardReadAfterWrite();
                return IntPtr.Zero;
            }
            if (ShouldIgnoreClipboardNotification(sequence))
                return IntPtr.Zero;
            if (ClipboardService.IsWriteInProgress)
                QueueClipboardReadAfterWrite();
            else
                _ = CaptureClipboardUpdateAsync();
        }
        return IntPtr.Zero;
    }

    private void SetClipboardListener(bool enabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        if (enabled && !_clipboardListenerRegistered)
            _clipboardListenerRegistered = AddClipboardFormatListener(handle);
        else if (!enabled && _clipboardListenerRegistered)
        {
            RemoveClipboardFormatListener(handle);
            _clipboardListenerRegistered = false;
        }
    }

    private async Task CaptureClipboardUpdateAsync()
    {
        if (!_settings.ClipboardEnabled) return;
        if (Volatile.Read(ref _clipboardSelfWritePending) != 0)
        {
            QueueClipboardReadAfterWrite();
            return;
        }
        if (ClipboardService.IsWriteInProgress)
        {
            QueueClipboardReadAfterWrite();
            return;
        }
        if (_clipboardReadInProgress)
        {
            _clipboardReadQueued = true;
            return;
        }

        _clipboardReadInProgress = true;
        try
        {
            do
            {
                _clipboardReadQueued = false;
                var sequence = GetClipboardSequenceNumber();
                if (sequence != 0 && sequence == _lastCapturedClipboardSequence)
                    continue;
                var capture = await ClipboardCaptureService.ReadAsync(
                    _clipboardRepository.ImagesDirectory,
                    _settings.ClipboardImageOcr,
                    _ocr);
                var latestSequence = GetClipboardSequenceNumber();
                if (sequence != 0 && latestSequence != 0 && latestSequence != sequence)
                {
                    _clipboardReadQueued = true;
                    continue;
                }
                if (sequence != 0) _lastCapturedClipboardSequence = sequence;
                if (capture is null) continue;
                if (_suppressedClipboardHash == capture.ContentHash
                    && DateTimeOffset.UtcNow <= _suppressedClipboardUntil)
                {
                    _suppressedClipboardHash = null;
                    continue;
                }
                await Task.Run(() => _clipboardRepository.Upsert(
                    capture,
                    _settings.ClipboardMaxItems,
                    _settings.ClipboardRetentionDays));
                MarkClipboardViewsDirty();
            }
            while (_clipboardReadQueued);
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(CaptureClipboardUpdateAsync), ex);
            FooterStatus.Text = $"剪贴板：{ex.Message}";
        }
        finally
        {
            _clipboardReadInProgress = false;
        }
    }

    private void QueueClipboardReadAfterWrite()
    {
        _clipboardReadQueued = true;
        if (_clipboardReadDeferred) return;
        _clipboardReadDeferred = true;
        _ = ResumeClipboardReadAfterWriteAsync();
    }

    private async Task ResumeClipboardReadAfterWriteAsync()
    {
        try
        {
            await ClipboardService.WaitForWriteCompletionAsync();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        finally
        {
            _clipboardReadDeferred = false;
        }

        if (!_clipboardReadQueued) return;
        _clipboardReadQueued = false;
        if (ConsumeSuppressedClipboardSequence()) return;
        await CaptureClipboardUpdateAsync();
    }

    private bool ShouldIgnoreClipboardNotification(uint sequence)
    {
        if (sequence == 0) return false;
        if (sequence == _suppressedClipboardSequence)
        {
            _suppressedClipboardSequence = 0;
            _suppressedClipboardHash = null;
            _lastClipboardNotificationSequence = sequence;
            _lastCapturedClipboardSequence = sequence;
            return true;
        }
        if (sequence == _lastClipboardNotificationSequence) return true;
        _lastClipboardNotificationSequence = sequence;
        return false;
    }

    private bool ConsumeSuppressedClipboardSequence()
    {
        var sequence = GetClipboardSequenceNumber();
        if (sequence == 0 || sequence != _suppressedClipboardSequence) return false;
        _suppressedClipboardSequence = 0;
        _suppressedClipboardHash = null;
        _lastClipboardNotificationSequence = sequence;
        _lastCapturedClipboardSequence = sequence;
        return true;
    }

    private void ShowClipboardPanel()
    {
        if (Volatile.Read(ref _clipboardUseInProgress) != 0
            || _clipboardWindow?.IsUseInProgress == true)
            return;
        var foreground = GetForegroundWindow();
        if (IsExternalWindow(foreground)) _lastExternalWindow = foreground;
        _clipboardWindow ??= new ClipboardWindow(
            _clipboardRepository,
            UseClipboardItemAsync,
            RecognizeClipboardImageAsync,
            () => MarkClipboardViewsDirty(),
            SetClipboardFavoriteAsync,
            SetClipboardNoteAsync);
        _clipboardWindow.ShowForTarget(_lastExternalWindow, _settings.ClipboardAutoPaste);
    }

    private void RememberForegroundWindow()
    {
        var foreground = GetForegroundWindow();
        if (IsExternalWindow(foreground)) _lastExternalWindow = foreground;
    }

    private static bool IsExternalWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var processId);
        return processId != (uint)Environment.ProcessId;
    }

    private async Task UseClipboardItemAsync(
        ClipboardItem item,
        bool plain,
        bool paste,
        IntPtr targetWindow)
    {
        if (Interlocked.CompareExchange(ref _clipboardUseInProgress, 1, 0) != 0)
            throw new InvalidOperationException("正在处理上一个剪贴板条目，请稍候。");

        try
        {
            Interlocked.Increment(ref _clipboardUseGeneration);
            var plainText = item.Kind switch
            {
                ClipboardItemKind.Files => string.Join(
                    Environment.NewLine,
                    JsonSerializer.Deserialize<string[]>(item.Value) ?? Array.Empty<string>()),
                ClipboardItemKind.Text => item.Value,
                _ => item.SearchText
            };
            _suppressedClipboardHash = plain
                ? ClipboardRepository.HashContent(ClipboardItemKind.Text, plainText)
                : item.ContentHash;
            _suppressedClipboardUntil = DateTimeOffset.UtcNow.AddSeconds(2);

            Volatile.Write(ref _clipboardSelfWritePending, 1);
            await ClipboardWriteService.WriteAsync(item, plain);
            _suppressedClipboardSequence = GetClipboardSequenceNumber();
            Volatile.Write(ref _clipboardSelfWritePending, 0);
            if (paste)
                await PasteService.PasteAsync(targetWindow == IntPtr.Zero ? _lastExternalWindow : targetWindow);

            _ = UpdateClipboardUsageAsync(item.Id);
        }
        finally
        {
            if (Volatile.Read(ref _clipboardSelfWritePending) != 0)
            {
                _suppressedClipboardHash = null;
                _suppressedClipboardUntil = DateTimeOffset.MinValue;
                Volatile.Write(ref _clipboardSelfWritePending, 0);
            }
            Volatile.Write(ref _clipboardSelfWritePending, 0);
            Volatile.Write(ref _clipboardUseInProgress, 0);
            Interlocked.Increment(ref _clipboardUseGeneration);
        }
    }

    private async Task UpdateClipboardUsageAsync(string itemId)
    {
        try
        {
            await _clipboardRepository.TouchAsync(itemId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(UpdateClipboardUsageAsync), ex);
        }
    }

    private async Task TryUseClipboardItemAsync(
        ClipboardItem item,
        bool plain,
        bool paste,
        IntPtr targetWindow)
    {
        try
        {
            await UseClipboardItemAsync(item, plain, paste, targetWindow);
            FooterStatus.Text = paste ? "已粘贴剪贴板条目" : "已复制剪贴板条目";
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(TryUseClipboardItemAsync), ex);
            FooterStatus.Text = $"剪贴板：{ex.Message}";
        }
    }

    private async Task RecognizeClipboardImageAsync(ClipboardItem item)
    {
        if (!item.IsImage || !File.Exists(item.Value))
            throw new InvalidOperationException("图片文件已不存在。");

        await using var stream = File.OpenRead(item.Value);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        var text = await _ocr.RecognizeAsync(image);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("图片中没有识别到文字。");
        _clipboardRepository.UpdateOcr(item, text);
        MarkClipboardViewsDirty(item.Id);
    }

    private void MarkClipboardViewsDirty(string? selectedId = null)
    {
        _clipboardMainDirty = true;
        _clipboardMainRefreshCancellation?.Cancel();
        if (IsVisible && MainTabs.SelectedIndex == ClipboardTabIndex)
            RefreshClipboardItems(selectedId);
        _clipboardWindow?.MarkDirty(selectedId);
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs)
            || MainTabs.SelectedIndex != ClipboardTabIndex
            || !_clipboardMainDirty)
            return;
        RefreshClipboardItems();
    }

    private void RefreshClipboardItems(string? selectedId = null)
    {
        _clipboardSearchRefreshTimer.Stop();
        _ = RefreshClipboardItemsAsync(selectedId);
    }

    private async Task RefreshClipboardItemsAsync(string? selectedId = null)
    {
        if (!IsInitialized) return;
        var requestedSelectedId = selectedId;
        var keyword = ClipboardSearchText.Text.Trim();
        var filter = Enum.TryParse<ClipboardFilter>(
            SelectedTag(ClipboardFilterCombo, ClipboardFilter.All.ToString()),
            out var parsed)
            ? parsed
            : ClipboardFilter.All;
        var previous = _clipboardMainRefreshCancellation;
        var cancellation = new CancellationTokenSource();
        _clipboardMainRefreshCancellation = cancellation;
        previous?.Cancel();
        try
        {
            var result = await _clipboardRepository.SearchWithCountAsync(
                keyword,
                filter,
                300,
                cancellation.Token);
            if (cancellation.IsCancellationRequested
                || !ReferenceEquals(_clipboardMainRefreshCancellation, cancellation))
                return;

            var currentFilter = Enum.TryParse<ClipboardFilter>(
                SelectedTag(ClipboardFilterCombo, ClipboardFilter.All.ToString()),
                out var currentParsed)
                ? currentParsed
                : ClipboardFilter.All;
            if (!string.Equals(keyword, ClipboardSearchText.Text.Trim(), StringComparison.Ordinal)
                || filter != currentFilter)
            {
                _clipboardMainDirty = true;
                return;
            }

            var currentSelectedId = requestedSelectedId
                                    ?? (ClipboardGrid.SelectedItem as ClipboardItem)?.Id;
            _clipboardItems = result.Items;
            ClipboardGrid.ItemsSource = _clipboardItems;
            ClipboardGrid.SelectedItem = _clipboardItems.FirstOrDefault(item => item.Id == currentSelectedId);
            ClipboardCountText.Text = $"{result.TotalCount:N0} 条";
            _clipboardMainDirty = false;
            UpdateClipboardFavoriteButton();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_allowClose) FooterStatus.Text = $"剪贴板刷新失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_clipboardMainRefreshCancellation, cancellation))
                _clipboardMainRefreshCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task RunCaptureAsync(CaptureAction action)
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            Hide();
            await Task.Delay(180);
            var snapshot = ScreenshotService.CaptureVirtualScreen();
            var initialAction = action switch
            {
                CaptureAction.Screenshot => CaptureResultAction.Copy,
                CaptureAction.Translate => CaptureResultAction.Translate,
                CaptureAction.Ocr => CaptureResultAction.Ocr,
                _ => CaptureResultAction.SilentOcr
            };
            var overlay = new CaptureOverlayWindow(
                snapshot,
                initialAction,
                initialAction switch
                {
                    CaptureResultAction.SilentOcr => !_settings.SilentOcrShowEditor,
                    CaptureResultAction.Translate => true,
                    _ => false
                });
            if (overlay.ShowDialog() != true || overlay.ResultImage is null) return;

            var image = overlay.ResultImage;
            switch (overlay.ResultAction)
            {
                case CaptureResultAction.Copy:
                    await ClipboardService.SetImageAsync(image);
                    ShowNotification(
                        _settings.NotifyScreenshotActions,
                        "截图已复制",
                        $"{image.PixelWidth} × {image.PixelHeight}");
                    break;

                case CaptureResultAction.Save:
                    var saveDialog = new Microsoft.Win32.SaveFileDialog
                    {
                        Title = "保存截图",
                        Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg",
                        FileName = $"截图_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                        AddExtension = true
                    };
                    if (saveDialog.ShowDialog() != true) break;
                    BitmapEncoder encoder = string.Equals(
                        Path.GetExtension(saveDialog.FileName),
                        ".jpg",
                        StringComparison.OrdinalIgnoreCase)
                        ? new JpegBitmapEncoder { QualityLevel = 95 }
                        : new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    await using (var stream = File.Create(saveDialog.FileName)) encoder.Save(stream);
                    ShowNotification(
                        _settings.NotifyScreenshotActions,
                        "截图已保存",
                        saveDialog.FileName);
                    break;

                case CaptureResultAction.Pin:
                    new PinnedImageWindow(image, _settings.PinnedImageEdgeEffect).Show();
                    break;

                case CaptureResultAction.Translate:
                    var translationWindow = new ScreenshotTranslationWindow(
                        image,
                        overlay.ResultScreenBounds,
                        _ocr,
                        _translator,
                        _settings);
                    translationWindow.TranslationCompleted += (source, translation, provider) =>
                        AddHistory(source, translation, provider);
                    translationWindow.Show();
                    break;

                default:
                    var text = await _ocr.RecognizeAsync(image);
                    if (string.IsNullOrWhiteSpace(text))
                        throw new InvalidOperationException("选区中没有识别到文本。");
                    if (overlay.ResultAction == CaptureResultAction.SilentOcr)
                    {
                        await ClipboardService.SetTextAsync(text);
                        ShowNotification(
                            _settings.NotifyOcrResults,
                            "OCR 已复制",
                            text.Length > 70 ? text[..70] + "..." : text);
                    }
                    else
                    {
                        var resultWindow = new ResultWindow();
                        resultWindow.SetSource(text);
                        resultWindow.Show();
                    }
                    AddHistory(text, string.Empty, "Windows OCR");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(RunCaptureAsync), ex);
            ShowNotification(_settings.NotifyErrors, "SnapTranslate", ex.Message);
        }
        finally
        {
            _captureInProgress = false;
        }
    }

    private async Task PinClipboardImageAsync()
    {
        try
        {
            await ClipboardService.WaitForWriteCompletionAsync();
            var image = await ReadClipboardImageAsync();
            if (image is null)
                throw new InvalidOperationException("剪贴板中没有可贴出的图片。");
            new PinnedImageWindow(image, _settings.PinnedImageEdgeEffect).Show();
            FooterStatus.Text = $"已贴图 · {image.PixelWidth} × {image.PixelHeight}";
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(PinClipboardImageAsync), ex);
            FooterStatus.Text = ex.Message;
            ShowNotification(_settings.NotifyErrors, "贴图失败", ex.Message);
        }
    }

    private static async Task<BitmapSource?> ReadClipboardImageAsync()
    {
        ExternalException? lastError = null;
        const int attempts = 5;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsImage()) return null;
                var image = System.Windows.Clipboard.GetImage();
                if (image is null) return null;
                if (!image.IsFrozen && image.CanFreeze) image.Freeze();
                return image;
            }
            catch (ExternalException ex)
            {
                lastError = ex;
                if (attempt + 1 < attempts)
                    await Task.Delay(40 * (attempt + 1));
            }
        }

        throw new InvalidOperationException("剪贴板正被其他应用占用，请稍后重试。", lastError);
    }

    private async void TranslateInput_Click(object sender, RoutedEventArgs e)
    {
        await TranslateInputAsync();
    }

    private async Task TranslateInputAsync()
    {
        var text = InputText.Text.Trim();
        if (text.Length == 0) return;

        var requestId = Interlocked.Increment(ref _inputTranslationRequestId);
        var previous = _inputTranslationCancellation;
        var cancellation = new CancellationTokenSource();
        _inputTranslationCancellation = cancellation;
        previous?.Cancel();

        try
        {
            FooterStatus.Text = "正在翻译...";
            TranslateInputButton.IsEnabled = false;
            InputResultText.Text = string.Empty;
            var settings = CreateInteractiveTranslationSettingsSnapshot();
            using var streamProgress = new ThrottledUiProgress(Dispatcher, partial =>
            {
                if (requestId != Volatile.Read(ref _inputTranslationRequestId)) return;
                InputResultText.Text = partial;
                InputResultText.ScrollToEnd();
            }, TimeSpan.FromMilliseconds(40));
            var result = await _translator.TranslateAsync(
                text,
                settings,
                streamProgress,
                cancellation.Token);
            if (requestId != Volatile.Read(ref _inputTranslationRequestId)) return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (requestId != Volatile.Read(ref _inputTranslationRequestId)) return;
                InputResultText.Text = result.Text;
                InputResultText.ScrollToEnd();
                FooterStatus.Text = result.Provider;
                AddHistory(text, result.Text, result.Provider);
            }, DispatcherPriority.ContextIdle, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(TranslateInputAsync), ex);
            FooterStatus.Text = ex.Message;
            ShowNotification(_settings.NotifyErrors, "翻译失败", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_inputTranslationCancellation, cancellation))
            {
                _inputTranslationCancellation = null;
                TranslateInputButton.IsEnabled = true;
            }
            cancellation.Dispose();
        }
    }

    private AppSettings CreateInteractiveTranslationSettingsSnapshot()
    {
        var snapshot = _settings.Clone();
        snapshot.SourceLanguage = SelectedTag(QuickSourceCombo, _settings.SourceLanguage);
        snapshot.TargetLanguage = SelectedTag(QuickTargetCombo, _settings.TargetLanguage);
        return snapshot;
    }

    private void AddHistory(string source, string translation, string provider)
    {
        _history.Insert(0, new HistoryEntry
        {
            CreatedAt = DateTime.Now,
            Source = source,
            Translation = translation,
            Provider = provider
        });
        while (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
        try
        {
            _storage.SaveHistory(_history);
        }
        catch (Exception ex)
        {
            FooterStatus.Text = $"结果已生成，但历史保存失败：{ex.Message}";
        }
    }

    private Forms.NotifyIcon BuildTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("剪贴板", null, (_, _) => Dispatcher.Invoke(ShowClipboardPanel));
        menu.Items.Add("输入翻译", null, (_, _) => Dispatcher.Invoke(() => { ShowMain(); MainTabs.SelectedIndex = 0; }));
        menu.Items.Add("截图", null, (_, _) => Dispatcher.Invoke(() => _ = RunCaptureAsync(CaptureAction.Screenshot)));
        menu.Items.Add("截图翻译", null, (_, _) => Dispatcher.Invoke(() => _ = RunCaptureAsync(CaptureAction.Translate)));
        menu.Items.Add("截图 OCR", null, (_, _) => Dispatcher.Invoke(() => _ = RunCaptureAsync(CaptureAction.Ocr)));
        menu.Items.Add("静默 OCR", null, (_, _) => Dispatcher.Invoke(() => _ = RunCaptureAsync(CaptureAction.SilentOcr)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(() => { ShowMain(); MainTabs.SelectedIndex = 5; }));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        var icon = new Forms.NotifyIcon
        {
            Text = "SnapTranslate",
            Icon = LoadTrayIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMain);
        return icon;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            using var extracted = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);
            if (extracted is not null) return (System.Drawing.Icon)extracted.Clone();
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(LoadTrayIcon), ex);
        }
        return (System.Drawing.Icon)SystemIcons.Information.Clone();
    }

    private void ShowMain()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_clipboardMainDirty && MainTabs.SelectedIndex == ClipboardTabIndex)
            RefreshClipboardItems();
    }

    private void ShowNotification(bool enabled, string title, string message)
    {
        if (!enabled) return;
        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = message;
        _trayIcon.ShowBalloonTip(3500);
    }

    private void LoadSettingsControls()
    {
        SelectByTag(ProviderCombo, _settings.Provider.ToString());
        SelectByTag(SettingsSourceCombo, _settings.SourceLanguage);
        SelectByTag(SettingsTargetCombo, _settings.TargetLanguage);
        SyncQuickLanguageCombos(_settings);
        _chatProfiles = _settings.ChatApiProfiles.Select(profile => profile.Clone()).ToList();
        _activeChatProfileId = _settings.ActiveChatApiProfile?.Id ?? string.Empty;
        RefreshChatProfileList(_activeChatProfileId);
        TranslationPromptText.Text = TranslationPrompt.Resolve(_settings.TranslationPrompt);
        PopulateAutoChain(_settings.AutoChain);
        UpdateProviderPanels();
        UpdateChatApiFormatHint();
        SelectByTag(DocumentSourceLanguageCombo, _settings.DocumentSourceLanguage);
        SelectByTag(DocumentTargetLanguageCombo, _settings.DocumentTargetLanguage);
        DocumentOutputDirectoryText.Text = _settings.DocumentOutputDirectory;
        DocumentOutputSuffixText.Text = _settings.DocumentOutputSuffix;
        DocumentTranslationConcurrencyText.Text = _settings.DocumentTranslationConcurrency.ToString();
        InputHotkeyText.Text = _settings.InputHotkey;
        ScreenshotHotkeyText.Text = _settings.ScreenshotHotkey;
        ScreenshotTranslateHotkeyText.Text = _settings.ScreenshotTranslateHotkey;
        SilentOcrHotkeyText.Text = _settings.SilentOcrHotkey;
        SilentOcrShowEditorCheck.IsChecked = _settings.SilentOcrShowEditor;
        ScreenshotOcrHotkeyText.Text = _settings.ScreenshotOcrHotkey;
        ClipboardHotkeyText.Text = _settings.ClipboardHotkey;
        PinnedImageHotkeyText.Text = _settings.PinnedImageHotkey;
        SelectByTag(PinnedImageEdgeEffectCombo, _settings.PinnedImageEdgeEffect.ToString());
        ClipboardEnabledCheck.IsChecked = _settings.ClipboardEnabled;
        ClipboardImageOcrCheck.IsChecked = _settings.ClipboardImageOcr;
        ClipboardAutoPasteCheck.IsChecked = _settings.ClipboardAutoPaste;
        ClipboardMaxItemsText.Text = _settings.ClipboardMaxItems.ToString();
        ClipboardRetentionDaysText.Text = _settings.ClipboardRetentionDays.ToString();
        StartupCheck.IsChecked = _settings.StartWithWindows;
        CloseToTrayCheck.IsChecked = _settings.CloseToTray;
        KeepOnTopCheck.IsChecked = _settings.KeepWindowOnTop;
        NotifyScreenshotActionsCheck.IsChecked = _settings.NotifyScreenshotActions;
        NotifyOcrResultsCheck.IsChecked = _settings.NotifyOcrResults;
        NotifyErrorsCheck.IsChecked = _settings.NotifyErrors;
    }

    private AppSettings ReadSettingsControls()
    {
        var provider = SelectedTag(ProviderCombo, "Auto");
        return new AppSettings
        {
            Provider = Enum.TryParse<TranslationProvider>(provider, out var parsed) ? parsed : TranslationProvider.Auto,
            // Ollama 已并入「自定义 API」，这里保留旧字段只是为了回滚时配置还在
            OllamaEndpoint = _settings.OllamaEndpoint,
            OllamaModel = _settings.OllamaModel,
            ChatApiProfiles = _chatProfiles.Select(profile => profile.Clone()).ToList(),
            ActiveChatApiProfileId = _activeChatProfileId,
            AutoChain = ReadAutoChain(),
            TranslationPrompt = TranslationPrompt.NormalizeForStorage(TranslationPromptText.Text),
            SourceLanguage = SelectedTag(SettingsSourceCombo, "auto"),
            TargetLanguage = SelectedTag(SettingsTargetCombo, "zh-CN"),
            DocumentSourceLanguage = SelectedTag(DocumentSourceLanguageCombo, "auto"),
            DocumentTargetLanguage = SelectedTag(DocumentTargetLanguageCombo, "zh-CN"),
            DocumentOutputDirectory = DocumentOutputDirectoryText.Text.Trim(),
            DocumentOutputSuffix = string.IsNullOrWhiteSpace(DocumentOutputSuffixText.Text)
                ? "_translation"
                : DocumentOutputSuffixText.Text.Trim(),
            DocumentTranslationConcurrency = int.TryParse(
                DocumentTranslationConcurrencyText.Text.Trim(), out var documentConcurrency) && documentConcurrency > 0
                ? documentConcurrency
                : _settings.DocumentTranslationConcurrency,
            InputHotkey = InputHotkeyText.Text.Trim(),
            ScreenshotHotkey = ScreenshotHotkeyText.Text.Trim(),
            ScreenshotTranslateHotkey = ScreenshotTranslateHotkeyText.Text.Trim(),
            SilentOcrHotkey = SilentOcrHotkeyText.Text.Trim(),
            SilentOcrShowEditor = SilentOcrShowEditorCheck.IsChecked == true,
            ScreenshotOcrHotkey = ScreenshotOcrHotkeyText.Text.Trim(),
            ClipboardHotkey = ClipboardHotkeyText.Text.Trim(),
            PinnedImageHotkey = PinnedImageHotkeyText.Text.Trim(),
            PinnedImageEdgeEffect = Enum.TryParse<PinnedImageEdgeEffect>(
                SelectedTag(PinnedImageEdgeEffectCombo, PinnedImageEdgeEffect.Gradient.ToString()), out var edgeEffect)
                ? edgeEffect
                : PinnedImageEdgeEffect.Gradient,
            ClipboardEnabled = ClipboardEnabledCheck.IsChecked == true,
            ClipboardImageOcr = ClipboardImageOcrCheck.IsChecked == true,
            ClipboardAutoPaste = ClipboardAutoPasteCheck.IsChecked == true,
            ClipboardMaxItems = ParseNonNegative(ClipboardMaxItemsText.Text, _settings.ClipboardMaxItems),
            ClipboardRetentionDays = ParseNonNegative(ClipboardRetentionDaysText.Text, _settings.ClipboardRetentionDays),
            StartWithWindows = StartupCheck.IsChecked == true,
            CloseToTray = CloseToTrayCheck.IsChecked == true,
            KeepWindowOnTop = KeepOnTopCheck.IsChecked == true,
            NotifyScreenshotActions = NotifyScreenshotActionsCheck.IsChecked == true,
            NotifyOcrResults = NotifyOcrResultsCheck.IsChecked == true,
            NotifyErrors = NotifyErrorsCheck.IsChecked == true
        };
    }

    private static string SelectedTag(System.Windows.Controls.ComboBox combo, string fallback)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static int ParseNonNegative(string value, int fallback)
        => int.TryParse(value.Trim(), out var parsed) ? Math.Clamp(parsed, 0, 1_000_000) : fallback;

    private static void SelectByTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private void QueueSettingsAutoSave()
    {
        if (_suppressSettingsAutoSave) return;
        _settingsAutoSaveTimer.Stop();
        _settingsAutoSaveTimer.Start();
    }

    private void FlushPendingSettingsAutoSave()
    {
        if (!_settingsAutoSaveTimer.IsEnabled) return;
        _settingsAutoSaveTimer.Stop();
        AutoSaveSettings();
    }

    /// <summary>设置页改动后自动保存。快捷键只在真的改了时才重新注册，避免边打字边卸钩子。</summary>
    private void AutoSaveSettings()
    {
        _settingsAutoSaveTimer.Stop();
        if (_suppressSettingsAutoSave) return;

        if (!int.TryParse(DocumentTranslationConcurrencyText.Text.Trim(), out var documentConcurrency)
            || documentConcurrency <= 0)
        {
            SettingsStatus.Text = "文档并发请求数必须是大于 0 的整数（暂未保存）";
            return;
        }

        var candidate = ReadSettingsControls();
        var hotkeysChanged =
            candidate.InputHotkey != _settings.InputHotkey
            || candidate.ScreenshotHotkey != _settings.ScreenshotHotkey
            || candidate.ScreenshotTranslateHotkey != _settings.ScreenshotTranslateHotkey
            || candidate.SilentOcrHotkey != _settings.SilentOcrHotkey
            || candidate.ScreenshotOcrHotkey != _settings.ScreenshotOcrHotkey
            || candidate.ClipboardHotkey != _settings.ClipboardHotkey
            || candidate.PinnedImageHotkey != _settings.PinnedImageHotkey;

        if (hotkeysChanged && !TryRegisterHotkeys(candidate, out var hotkeyError))
        {
            TryRegisterHotkeys(_settings, out _);
            SettingsStatus.Text = hotkeyError + "（设置暂未保存）";
            return;
        }

        try
        {
            _storage.SaveSettings(candidate);
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = "设置保存失败：" + ex.Message;
            return;
        }

        _settings = candidate;
        ApplyWindowPreferences();
        SetClipboardListener(_settings.ClipboardEnabled);
        try
        {
            StartupService.SetEnabled(_settings.StartWithWindows);
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = "已保存，但开机启动更新失败：" + ex.Message;
            return;
        }

        SyncQuickLanguageCombos(_settings);
        SettingsStatus.Text = "已自动保存";
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DocumentTranslationConcurrencyText.Text.Trim(), out var documentConcurrency)
            || documentConcurrency <= 0)
        {
            SettingsStatus.Text = "文档并发请求数必须是大于 0 的整数";
            DocumentTranslationConcurrencyText.Focus();
            DocumentTranslationConcurrencyText.SelectAll();
            return;
        }
        var candidate = ReadSettingsControls();
        var previous = _settings;
        if (!TryRegisterHotkeys(candidate, out var error))
        {
            TryRegisterHotkeys(_settings, out _);
            LoadSettingsControls();
            SettingsStatus.Text = error;
            return;
        }
        try
        {
            _storage.SaveSettings(candidate);
        }
        catch (Exception ex)
        {
            TryRegisterHotkeys(previous, out _);
            LoadSettingsControls();
            SettingsStatus.Text = $"设置保存失败：{ex.Message}";
            return;
        }
        _settings = candidate;
        ApplyWindowPreferences();
        SetClipboardListener(_settings.ClipboardEnabled);
        string? startupWarning = null;
        try
        {
            StartupService.SetEnabled(_settings.StartWithWindows);
        }
        catch (Exception ex)
        {
            startupWarning = $"设置已保存，但开机启动更新失败：{ex.Message}";
        }
        SyncQuickLanguageCombos(_settings);
        FooterStatus.Text = "本地 OCR 已就绪";
        SettingsStatus.Text = startupWarning
            ?? $"已保存，快捷键监听正常（{_hookRegistration.Hotkeys.Count}/{_hookRegistration.Hotkeys.Count}）";
    }

    private void PositiveIntegerText_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Any(character => !char.IsDigit(character));

    private void PositiveIntegerText_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(System.Windows.DataFormats.Text)
            || e.DataObject.GetData(System.Windows.DataFormats.Text) is not string text
            || text.Length == 0
            || text.Any(character => !char.IsDigit(character)))
            e.CancelCommand();
    }

    /// <summary>下拉项统一从 TranslationProviderCatalog 生成，名字只在 Core 里定义一次。</summary>
    private void InitializeTranslationSelectors()
    {
        ProviderCombo.Items.Clear();
        foreach (var provider in TranslationProviderCatalog.SelectableProviders)
        {
            ProviderCombo.Items.Add(new ComboBoxItem
            {
                Content = TranslationProviderCatalog.DisplayName(provider),
                Tag = provider.ToString()
            });
        }

        ChatApiFormatCombo.Items.Clear();
        foreach (var format in TranslationProviderCatalog.SelectableFormats)
        {
            ChatApiFormatCombo.Items.Add(new ComboBoxItem
            {
                Content = TranslationProviderCatalog.FormatDisplayName(format),
                Tag = format.ToString()
            });
        }

        AutoChainAddCombo.Items.Clear();
        foreach (var provider in TranslationProviderCatalog.SelectableProviders)
        {
            if (provider == TranslationProvider.Auto) continue;
            AutoChainAddCombo.Items.Add(new ComboBoxItem
            {
                Content = TranslationProviderCatalog.DisplayName(provider),
                Tag = provider.ToString()
            });
        }
        AutoChainAddCombo.SelectedIndex = 0;
    }

    /// <summary>应用只影响主窗口外观的设置。目前只有「始终置顶」。</summary>
    private void ApplyWindowPreferences()
        => Topmost = _settings.KeepWindowOnTop;

    /// <summary>勾选后立刻生效，不用先点保存；保存后才会写进 settings.json。</summary>
    private void KeepOnTopCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (KeepOnTopCheck is null) return;
        Topmost = KeepOnTopCheck.IsChecked == true;
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateProviderPanels();

    private void ChatApiFormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateChatApiFormatHint();
        if (_bindingChatProfile) return;
        if (SelectedChatProfile is not { } profile) return;
        if (Enum.TryParse<ChatApiFormat>(
                SelectedTag(ChatApiFormatCombo, nameof(ChatApiFormat.OpenAi)), out var format))
            profile.Format = format;
    }

    /// <summary>把所选格式对应的请求路径和鉴权方式显示出来，替代把路径写进格式名。</summary>
    private void UpdateChatApiFormatHint()
    {
        if (ChatApiFormatHint is null) return;
        var format = Enum.TryParse<ChatApiFormat>(
            SelectedTag(ChatApiFormatCombo, nameof(ChatApiFormat.OpenAi)), out var parsed)
            ? parsed
            : ChatApiFormat.OpenAi;
        ChatApiFormatHint.Text = format switch
        {
            ChatApiFormat.Anthropic =>
                "向 {接口地址}/messages 发送 system + messages，使用 x-api-key 与 anthropic-version 头。",
            ChatApiFormat.Responses =>
                "向 {接口地址}/responses 发送 instructions + input，使用 Bearer 鉴权。",
            _ =>
                "向 {接口地址}/chat/completions 发送 messages，使用 Bearer 鉴权。"
        };
    }

    /// <summary>只显示当前翻译方式需要的配置项，避免设置页堆一排无关输入框。</summary>
    private void UpdateProviderPanels()
    {
        if (ChatApiPanel is null) return;
        var provider = SelectedTag(ProviderCombo, nameof(TranslationProvider.Auto));
        ChatApiPanel.Visibility = provider == nameof(TranslationProvider.ChatApi)
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutoChainPanel.Visibility = provider == nameof(TranslationProvider.Auto)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void FetchChatModels_Click(object sender, RoutedEventArgs e)
    {
        var profile = SelectedChatProfile;
        if (profile is null)
        {
            SettingsStatus.Text = "请先新建或选择一个渠道";
            return;
        }
        if (string.IsNullOrWhiteSpace(profile.BaseUrl))
        {
            SettingsStatus.Text = "请先填写接口地址，例如 https://api.openai.com/v1";
            ChatApiBaseUrlText.Focus();
            return;
        }

        SettingsStatus.Text = "正在获取模型列表…";
        try
        {
            var models = await _translator.ListModelsAsync(profile.Clone());
            var current = ChatApiModelText.Text.Trim();
            ChatApiModelList.Items.Clear();
            foreach (var model in models) ChatApiModelList.Items.Add(model);
            ChatApiModelList.Visibility = Visibility.Visible;
            ChatApiModelList.SelectedItem = models.FirstOrDefault(model => string.Equals(model, current, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(current)) ChatApiModelText.Text = models[0];
            ChatApiModelList.IsDropDownOpen = true;
            SettingsStatus.Text = $"接口返回 {models.Count} 个模型，从下拉列表选一个，或直接改上面的模型名";
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(FetchChatModels_Click), ex);
            SettingsStatus.Text = ex.Message;
        }
    }

    private ChatApiProfile? SelectedChatProfile
        => ChatProfileList.SelectedItem is ListBoxItem { Tag: string id }
            ? _chatProfiles.FirstOrDefault(profile => profile.Id == id)
            : null;

    private void ChatProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingChatProfile) return;
        ApplyChatProfileToFields(ChatProfileList.SelectedItem as ListBoxItem);
    }

    private void ChatProfileField_Changed(object sender, TextChangedEventArgs e)
    {
        if (_bindingChatProfile) return;
        var profile = SelectedChatProfile;
        if (profile is null) return;
        profile.Name = ChatProfileNameText.Text.Trim();
        profile.BaseUrl = ChatApiBaseUrlText.Text.Trim();
        profile.ApiKey = ChatApiKeyText.Text.Trim();
        profile.Model = ChatApiModelText.Text.Trim();
        RefreshChatProfileItemLabel(ChatProfileList.SelectedItem as ListBoxItem, profile);
    }

    private void RefreshChatProfileList(string? selectId = null)
    {
        var targetId = selectId ?? SelectedChatProfile?.Id ?? _activeChatProfileId;
        _bindingChatProfile = true;
        ChatProfileList.Items.Clear();
        foreach (var profile in _chatProfiles) ChatProfileList.Items.Add(CreateChatProfileItem(profile));
        var items = ChatProfileList.Items.OfType<ListBoxItem>().ToList();
        ChatProfileList.SelectedItem = items.FirstOrDefault(item => (string?)item.Tag == targetId) ?? items.FirstOrDefault();
        _bindingChatProfile = false;
        ApplyChatProfileToFields(ChatProfileList.SelectedItem as ListBoxItem);
    }

    private ListBoxItem CreateChatProfileItem(ChatApiProfile profile)
    {
        var item = new ListBoxItem { Tag = profile.Id };
        RefreshChatProfileItemLabel(item, profile);
        return item;
    }

    private void RefreshChatProfileItemLabel(ListBoxItem? item, ChatApiProfile profile)
    {
        if (item is null) return;
        var active = profile.Id == _activeChatProfileId ? "✓ " : string.Empty;
        var model = string.IsNullOrWhiteSpace(profile.Model) ? string.Empty : " · " + profile.Model.Trim();
        item.Content = active + profile.DisplayLabel + model;
    }

    /// <summary>把列表里选中的渠道加载到下面的输入框；空列表时清空。</summary>
    private void ApplyChatProfileToFields(ListBoxItem? item)
    {
        var profile = item?.Tag is string id
            ? _chatProfiles.FirstOrDefault(candidate => candidate.Id == id)
            : null;
        _bindingChatProfile = true;
        ChatProfileNameText.Text = profile?.Name ?? string.Empty;
        ChatApiBaseUrlText.Text = profile?.BaseUrl ?? string.Empty;
        ChatApiKeyText.Text = profile?.ApiKey ?? string.Empty;
        ChatApiModelText.Text = profile?.Model ?? string.Empty;
        SelectByTag(ChatApiFormatCombo, (profile?.Format ?? ChatApiFormat.OpenAi).ToString());
        _bindingChatProfile = false;
        ChatApiModelList.Items.Clear();
        ChatApiModelList.Visibility = Visibility.Collapsed;
        var active = _chatProfiles.FirstOrDefault(candidate => candidate.Id == _activeChatProfileId);
        ChatProfileHint.Text = active is null
            ? "还没有当前渠道，点「新建渠道」开始。"
            : "当前使用：" + active.DisplayLabel;
        UpdateChatApiFormatHint();
    }

    private void NewChatProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = new ChatApiProfile { Name = "渠道 " + (_chatProfiles.Count + 1) };
        _chatProfiles.Add(profile);
        if (string.IsNullOrEmpty(_activeChatProfileId)) _activeChatProfileId = profile.Id;
        RefreshChatProfileList(profile.Id);
        ChatProfileNameText.Focus();
        ChatProfileNameText.SelectAll();
        SettingsStatus.Text = "已新建渠道，填好参数后点「保存设置」";
    }

    private void DeleteChatProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = SelectedChatProfile;
        if (profile is null) return;
        _chatProfiles.Remove(profile);
        if (_activeChatProfileId == profile.Id)
            _activeChatProfileId = _chatProfiles.FirstOrDefault()?.Id ?? string.Empty;
        RefreshChatProfileList();
        SettingsStatus.Text = "已删除渠道「" + profile.DisplayLabel + "」，保存后生效";
    }

    private void ActivateChatProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = SelectedChatProfile;
        if (profile is null) return;
        _activeChatProfileId = profile.Id;
        RefreshChatProfileList(profile.Id);
        SettingsStatus.Text = "已把「" + profile.DisplayLabel + "」设为当前渠道，保存后生效";
    }

    private void PopulateAutoChain(IEnumerable<TranslationProvider> chain)
    {
        AutoChainList.Items.Clear();
        foreach (var provider in AppSettings.NormalizeAutoChain(chain))
            AutoChainList.Items.Add(new ListBoxItem { Tag = provider.ToString() });
        RefreshAutoChainLabels();
        if (AutoChainList.Items.Count > 0) AutoChainList.SelectedIndex = 0;
    }

    private List<TranslationProvider> ReadAutoChain()
        => AutoChainList.Items.OfType<ListBoxItem>()
            .Select(item => Enum.TryParse<TranslationProvider>(item.Tag?.ToString(), out var provider)
                ? provider
                : TranslationProvider.Auto)
            .Where(provider => provider != TranslationProvider.Auto)
            .ToList();

    private void RefreshAutoChainLabels()
    {
        var index = 0;
        foreach (var item in AutoChainList.Items.OfType<ListBoxItem>())
        {
            if (!Enum.TryParse<TranslationProvider>(item.Tag?.ToString(), out var provider)) continue;
            index++;
            item.Content = index + ". " + TranslationProviderCatalog.DisplayName(provider);
        }
    }

    private void MoveAutoChainUp_Click(object sender, RoutedEventArgs e) => MoveAutoChain(-1);

    private void MoveAutoChainDown_Click(object sender, RoutedEventArgs e) => MoveAutoChain(1);

    private void MoveAutoChain(int delta)
    {
        var index = AutoChainList.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= AutoChainList.Items.Count) return;
        var item = AutoChainList.Items[index];
        AutoChainList.Items.RemoveAt(index);
        AutoChainList.Items.Insert(target, item);
        RefreshAutoChainLabels();
        AutoChainList.SelectedIndex = target;
    }

    private void RemoveAutoChain_Click(object sender, RoutedEventArgs e)
    {
        var index = AutoChainList.SelectedIndex;
        if (index < 0) return;
        AutoChainList.Items.RemoveAt(index);
        RefreshAutoChainLabels();
        if (AutoChainList.Items.Count > 0)
            AutoChainList.SelectedIndex = Math.Min(index, AutoChainList.Items.Count - 1);
        SettingsStatus.Text = "已从自动顺序中移除，保存后生效";
    }

    private void AddAutoChain_Click(object sender, RoutedEventArgs e)
    {
        if (AutoChainAddCombo.SelectedItem is not ComboBoxItem chosen) return;
        if (!Enum.TryParse<TranslationProvider>(chosen.Tag?.ToString(), out var provider)) return;
        var name = TranslationProviderCatalog.DisplayName(provider);
        if (ReadAutoChain().Contains(provider))
        {
            SettingsStatus.Text = name + " 已经在自动顺序里了";
            return;
        }

        var added = new ListBoxItem { Tag = provider.ToString() };
        AutoChainList.Items.Add(added);
        RefreshAutoChainLabels();
        AutoChainList.SelectedItem = added;
        SettingsStatus.Text = "已把 " + name + " 加到自动顺序末尾，保存后生效";
    }

    private void ResetAutoChain_Click(object sender, RoutedEventArgs e)
    {
        PopulateAutoChain(AppSettings.DefaultAutoChain);
        SettingsStatus.Text = "自动顺序已恢复默认，保存后生效";
    }

    private void ChatApiModelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChatApiModelList.SelectedItem is string model) ChatApiModelText.Text = model;
    }

    private void ResetTranslationPrompt_Click(object sender, RoutedEventArgs e)
    {
        TranslationPromptText.Text = TranslationPrompt.Default;
        SettingsStatus.Text = "已恢复内置默认提示词，保存后生效";
    }

    private void TestTranslation_Click(object sender, RoutedEventArgs e)
    {
        var window = new TranslationTestWindow(_translator, ReadSettingsControls()) { Owner = this };
        window.ShowDialog();
    }

    private void HotkeyText_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        if (sender is not System.Windows.Controls.TextBox textBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed) key = e.ImeProcessedKey;
        if (key is Key.Delete or Key.Back)
        {
            textBox.Clear();
            SettingsStatus.Text = string.Empty;
            return;
        }
        if (IsModifierKey(key)) return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            SettingsStatus.Text = "快捷键至少需要一个修饰键";
            return;
        }
        textBox.Text = FormatHotkey(modifiers, key);
        textBox.CaretIndex = textBox.Text.Length;
        SettingsStatus.Text = string.Empty;
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string target }
            && FindName(target) is System.Windows.Controls.TextBox textBox)
            textBox.Clear();
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string FormatHotkey(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyToToken(key));
        return string.Join("+", parts);
    }

    private static string KeyToToken(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9) return ((int)key - (int)Key.D0).ToString();
        return key switch
        {
            Key.Return => "Enter",
            Key.Escape => "Esc",
            _ => key.ToString()
        };
    }

    private static bool TryParseHotkey(string value, out GlobalHotkey hotkey)
    {
        var modifiers = 0u;
        Key? key = null;
        foreach (var part in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL": modifiers |= ModControl; break;
                case "ALT": modifiers |= ModAlt; break;
                case "SHIFT": modifiers |= ModShift; break;
                case "WIN":
                case "WINDOWS": modifiers |= ModWin; break;
                default:
                    if (key is not null || !TryParseKey(part, out var parsedKey))
                    {
                        hotkey = default;
                        return false;
                    }
                    key = parsedKey;
                    break;
            }
        }
        if (modifiers == 0 || key is null || IsModifierKey(key.Value))
        {
            hotkey = default;
            return false;
        }
        var virtualKey = KeyInterop.VirtualKeyFromKey(key.Value);
        if (virtualKey <= 0)
        {
            hotkey = default;
            return false;
        }
        hotkey = new GlobalHotkey(modifiers, (uint)virtualKey);
        return true;
    }

    private static bool TryParseKey(string token, out Key key)
    {
        if (token.Length == 1 && char.IsDigit(token[0]))
        {
            key = (Key)((int)Key.D0 + (token[0] - '0'));
            return true;
        }
        if (string.Equals(token, "Esc", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Escape;
            return true;
        }
        if (string.Equals(token, "Enter", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Return;
            return true;
        }
        return Enum.TryParse(token, true, out key);
    }

    /// <summary>程序化同步两个语言下拉，不要反过来写回设置（否则会把设置页选的语言冲掉）。</summary>
    private void SyncQuickLanguageCombos(AppSettings settings)
    {
        _suppressQuickLanguageSave = true;
        try
        {
            SelectByTag(QuickSourceCombo, settings.SourceLanguage);
            SelectByTag(QuickTargetCombo, settings.TargetLanguage);
        }
        finally
        {
            _suppressQuickLanguageSave = false;
        }
    }

    private void QuickTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressQuickLanguageSave) return;
        _settings.TargetLanguage = SelectedTag(QuickTargetCombo, "auto");
        SelectByTag(SettingsTargetCombo, _settings.TargetLanguage);
        try
        {
            _storage.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            FooterStatus.Text = $"目标语言未能保存：{ex.Message}";
        }
    }

    private void QuickSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressQuickLanguageSave) return;
        _settings.SourceLanguage = SelectedTag(QuickSourceCombo, "auto");
        SelectByTag(SettingsSourceCombo, _settings.SourceLanguage);
        try
        {
            _storage.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            FooterStatus.Text = $"源语言未能保存：{ex.Message}";
        }
    }

    private void ScreenshotTranslate_Click(object sender, RoutedEventArgs e) => _ = RunCaptureAsync(CaptureAction.Translate);
    private void ScreenshotOcr_Click(object sender, RoutedEventArgs e) => _ = RunCaptureAsync(CaptureAction.Ocr);
    private void SilentOcr_Click(object sender, RoutedEventArgs e) => _ = RunCaptureAsync(CaptureAction.SilentOcr);
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMaximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeGlyph is null || RestoreGlyph is null) return;
        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void InputText_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.X || Keyboard.Modifiers != ModifierKeys.Control || sender is not System.Windows.Controls.TextBox textBox)
            return;
        e.Handled = true;
        await ClipboardSearchCutService.CutAsync(textBox, message => FooterStatus.Text = message);
    }
    private void ClearInput_Click(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _inputTranslationRequestId);
        _inputTranslationCancellation?.Cancel();
        InputText.Clear();
        InputResultText.Text = string.Empty;
    }

    private async void CopyInput_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputText.Text))
        {
            FooterStatus.Text = "没有可复制的原文";
            return;
        }

        await TrySetClipboardTextAsync(InputText.Text, "已复制原文");
    }

    private async void CopyInputResult_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputResultText.Text))
        {
            FooterStatus.Text = "还没有翻译结果";
            return;
        }

        await TrySetClipboardTextAsync(InputResultText.Text, "已复制翻译结果");
    }

    private async void CopyHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is HistoryEntry entry)
            await TrySetClipboardTextAsync(
                string.IsNullOrWhiteSpace(entry.Translation) ? entry.Source : entry.Translation,
                "已复制历史记录");
    }

    private async Task TrySetClipboardTextAsync(string text, string successMessage)
    {
        try
        {
            await ClipboardService.SetTextAsync(text);
            FooterStatus.Text = successMessage;
        }
        catch (Exception ex)
        {
            FooterStatus.Text = $"复制失败：{ex.Message}";
        }
    }

    private void HistorySearchText_TextChanged(object sender, TextChangedEventArgs e)
        => _historyView.Refresh();

    private bool FilterHistoryEntry(object item)
    {
        if (item is not HistoryEntry entry) return false;
        var keyword = HistorySearchText.Text.Trim();
        if (keyword.Length == 0) return true;
        return entry.Source.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
               || entry.Translation.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
               || entry.Provider.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);
    }

    private void ClipboardSearchText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        _clipboardMainDirty = true;
        _clipboardMainRefreshCancellation?.Cancel();
        _clipboardSearchRefreshTimer.Stop();
        _clipboardSearchRefreshTimer.Start();
    }

    private async void SearchText_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.X || Keyboard.Modifiers != ModifierKeys.Control || sender is not TextBox textBox)
            return;
        if (textBox.SelectionLength <= 0) return;
        e.Handled = true;
        await ClipboardSearchCutService.CutAsync(textBox, message => FooterStatus.Text = message);
    }

    private void ClipboardFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        _clipboardSearchRefreshTimer.Stop();
        _clipboardMainDirty = true;
        _clipboardMainRefreshCancellation?.Cancel();
        RefreshClipboardItems();
    }

    private void ClipboardGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateClipboardFavoriteButton();

    private void UpdateClipboardFavoriteButton()
    {
        if (ClipboardFavoriteButton is null) return;
        var item = ClipboardGrid.SelectedItem as ClipboardItem;
        ClipboardFavoriteButton.IsEnabled = item is not null;
        ClipboardFavoriteButton.Content = item?.IsFavorite == true ? "取消收藏" : "收藏";
        ClipboardFavoriteButton.ToolTip = item?.IsFavorite == true ? "取消收藏当前条目" : "收藏当前条目";
    }

    private async void ClipboardGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || e.OriginalSource is not DependencyObject source
            || IsClipboardInteractiveSource(source)
            || ItemsControl.ContainerFromElement(ClipboardGrid, source) is not DataGridRow)
            return;

        e.Handled = true;
        if (_settings.ClipboardAutoPaste && ClipboardGrid.SelectedItem is ClipboardItem item)
            await TryUseClipboardItemAsync(item, false, true, _lastExternalWindow);
    }

    private async void ClipboardCopy_Click(object sender, RoutedEventArgs e)
    {
        if (ClipboardGrid.SelectedItem is ClipboardItem item)
            await TryUseClipboardItemAsync(item, false, false, IntPtr.Zero);
    }

    private async void ClipboardPaste_Click(object sender, RoutedEventArgs e)
    {
        if (ClipboardGrid.SelectedItem is ClipboardItem item)
            await TryUseClipboardItemAsync(item, false, true, _lastExternalWindow);
    }

    private async void ClipboardPastePlain_Click(object sender, RoutedEventArgs e)
    {
        if (ClipboardGrid.SelectedItem is ClipboardItem item)
            await TryUseClipboardItemAsync(item, true, true, _lastExternalWindow);
    }

    private async void ClipboardFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ClipboardGrid.SelectedItem is not ClipboardItem item) return;
        await SetClipboardFavoriteAsync(item, !item.IsFavorite);
    }

    private async void ClipboardFavoriteInline_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ClipboardItem item }) return;
        e.Handled = true;
        ClipboardGrid.SelectedItem = item;
        await SetClipboardFavoriteAsync(item, !item.IsFavorite);
    }

    private async void ClipboardNote_Click(object sender, RoutedEventArgs e)
    {
        if (ClipboardGrid.SelectedItem is not ClipboardItem item) return;
        await EditClipboardNoteAsync(item);
    }

    private async void ClipboardInlineNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ClipboardItem item }) return;
        e.Handled = true;
        ClipboardGrid.SelectedItem = item;
        await EditClipboardNoteAsync(item);
    }

    private async Task EditClipboardNoteAsync(ClipboardItem item)
    {
        var dialog = new NoteWindow(item.Note) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await SetClipboardNoteAsync(item, dialog.Note, item.Note);
    }

    private async Task SetClipboardFavoriteAsync(ClipboardItem item, bool isFavorite)
    {
        var revision = ++_clipboardMutationRevision;
        _clipboardFavoriteRevisions[item.Id] = revision;
        ApplyClipboardFavoriteState(item.Id, isFavorite, item);
        var message = isFavorite ? "已收藏剪贴板条目" : "已取消收藏";
        FooterStatus.Text = message;
        _clipboardWindow?.SetStatus(isFavorite ? "已收藏" : "已取消收藏");

        try
        {
            await _clipboardRepository.SetFavoriteAsync(item.Id, isFavorite);
            if (_clipboardFavoriteRevisions.TryGetValue(item.Id, out var current) && current == revision)
                MarkClipboardViewsDirty(item.Id);
        }
        catch (Exception ex)
        {
            if (!_clipboardFavoriteRevisions.TryGetValue(item.Id, out var current) || current != revision) return;
            RefreshClipboardItems(item.Id);
            _clipboardWindow?.RefreshItems(item.Id);
            FooterStatus.Text = $"收藏失败，已恢复：{ex.Message}";
            _clipboardWindow?.SetStatus("收藏失败，已恢复");
        }
    }

    private async Task SetClipboardNoteAsync(ClipboardItem item, string note, string originalNote)
    {
        var normalized = note.Trim();
        if (string.Equals(normalized, originalNote.Trim(), StringComparison.Ordinal))
        {
            item.Note = normalized;
            return;
        }

        var revision = ++_clipboardMutationRevision;
        _clipboardNoteRevisions[item.Id] = revision;
        ApplyClipboardNoteState(item.Id, normalized, item);
        FooterStatus.Text = "备注已更新";
        _clipboardWindow?.SetStatus("备注已更新");

        try
        {
            await _clipboardRepository.SetNoteAsync(item.Id, normalized);
            if (_clipboardNoteRevisions.TryGetValue(item.Id, out var current) && current == revision)
                MarkClipboardViewsDirty(item.Id);
        }
        catch (Exception ex)
        {
            if (!_clipboardNoteRevisions.TryGetValue(item.Id, out var current) || current != revision) return;
            RefreshClipboardItems(item.Id);
            _clipboardWindow?.RefreshItems(item.Id);
            FooterStatus.Text = $"备注保存失败，已恢复：{ex.Message}";
            _clipboardWindow?.SetStatus("备注保存失败，已恢复");
        }
    }

    private void ApplyClipboardFavoriteState(string id, bool isFavorite, ClipboardItem source)
    {
        source.IsFavorite = isFavorite;
        var mainItem = _clipboardItems.FirstOrDefault(candidate => candidate.Id == id);
        if (mainItem is not null) mainItem.IsFavorite = isFavorite;
        _clipboardWindow?.ApplyFavoriteState(id, isFavorite);
        UpdateClipboardFavoriteButton();
    }

    private void ApplyClipboardNoteState(string id, string note, ClipboardItem source)
    {
        source.Note = note;
        var mainItem = _clipboardItems.FirstOrDefault(candidate => candidate.Id == id);
        if (mainItem is not null) mainItem.Note = note;
        _clipboardWindow?.ApplyNoteState(id, note);
    }

    private static bool IsClipboardInteractiveSource(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.TextBoxBase) return true;
            if (current is DataGridRow) return false;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private async void ClipboardOcr_Click(object sender, RoutedEventArgs e)
    {
        if (ClipboardGrid.SelectedItem is not ClipboardItem item || !item.IsImage) return;
        FooterStatus.Text = "正在识别剪贴板图片...";
        try
        {
            await RecognizeClipboardImageAsync(item);
            _clipboardWindow?.RefreshItems(item.Id);
            FooterStatus.Text = "图片 OCR 完成";
        }
        catch (Exception ex) { FooterStatus.Text = ex.Message; }
    }

    private void ClipboardDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ClipboardGrid.SelectedItem is not ClipboardItem item) return;
        _clipboardRepository.Delete(item);
        RefreshClipboardItems();
        _clipboardWindow?.RefreshItems();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        _storage.SaveHistory(_history);
    }

    private void Window_PreviewDragEnter(object sender, DragEventArgs e) => UpdateFileDropState(e);
    private void Window_PreviewDragOver(object sender, DragEventArgs e) => UpdateFileDropState(e);

    private void Window_PreviewDragLeave(object sender, DragEventArgs e)
    {
        FileDropOverlay.Visibility = Visibility.Collapsed;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        FileDropOverlay.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        QueueDocumentFiles(paths);
        e.Handled = true;
    }

    private void DocumentTaskSearchText_TextChanged(object sender, TextChangedEventArgs e)
        => _documentTasksView.Refresh();

    private bool FilterDocumentTask(object item)
    {
        if (item is not DocumentTranslationTask task) return false;
        var keyword = DocumentTaskSearchText.Text.Trim();
        if (keyword.Length == 0) return true;
        return task.FileName.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
               || task.SourcePath.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
               || task.OutputPath.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
               || task.Format.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
               || task.StatusDisplay.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
               || task.StatusText.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
               || task.Error.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);
    }

    private void AttachDocumentTask(DocumentTranslationTask task, bool insert)
    {
        task.PropertyChanged += DocumentTask_PropertyChanged;
        if (insert) _documentTasks.Insert(0, task);
        else _documentTasks.Add(task);
    }

    private void DetachDocumentTask(DocumentTranslationTask task)
    {
        task.PropertyChanged -= DocumentTask_PropertyChanged;
        task.Cancellation.Dispose();
        _documentTasks.Remove(task);
    }

    private void DocumentTask_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ScheduleDocumentTasksSave();
        if (e.PropertyName is nameof(DocumentTranslationTask.Status)
            or nameof(DocumentTranslationTask.StatusText)
            or nameof(DocumentTranslationTask.OutputPath)
            or nameof(DocumentTranslationTask.Error))
            _documentTasksView.Refresh();
    }

    private void ScheduleDocumentTasksSave()
    {
        if (!_documentTaskSaveTimer.IsEnabled) _documentTaskSaveTimer.Start();
    }

    private void PersistDocumentTasks()
    {
        try
        {
            _storage.SaveDocumentTasks(_documentTasks.Select(task => task.CreateSnapshot()));
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(PersistDocumentTasks), ex);
            FooterStatus.Text = $"任务记录保存失败：{ex.Message}";
        }
    }

    private void UpdateFileDropState(DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        FileDropOverlay.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void AddDocumentFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加文档翻译任务",
            Filter = DocumentTranslationService.FileDialogFilter,
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true) QueueDocumentFiles(dialog.FileNames);
    }

    private void QueueDocumentFiles(IEnumerable<string> paths)
    {
        var submittedPaths = paths.ToArray();
        var activePaths = _documentTasks
            .Where(task => task.Status is DocumentTaskStatus.Queued or DocumentTaskStatus.Processing)
            .Select(task => Path.GetFullPath(task.SourcePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicates = 0;
        var files = submittedPaths.Where(DocumentTranslationService.IsSupported)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path =>
            {
                if (!activePaths.Contains(path)) return true;
                duplicates++;
                return false;
            })
            .ToArray();
        var unsupported = submittedPaths.Count(path => File.Exists(path) && !DocumentTranslationService.IsSupported(path));
        foreach (var path in files)
        {
            var task = new DocumentTranslationTask(path);
            AttachDocumentTask(task, true);
            _ = ProcessDocumentTaskAsync(task, CreateDocumentSettingsSnapshot());
        }
        if (files.Length > 0)
        {
            MainTabs.SelectedIndex = 2;
            FooterStatus.Text = duplicates > 0
                ? $"已添加 {files.Length} 个文档翻译任务，忽略 {duplicates} 个重复任务"
                : $"已添加 {files.Length} 个文档翻译任务";
        }
        else if (duplicates > 0)
        {
            FooterStatus.Text = "相同文件已有翻译任务正在处理";
        }
        else if (unsupported > 0)
        {
            FooterStatus.Text = "未添加任务：文件类型不受支持";
        }
        UpdateDocumentTaskCount();
        ScheduleDocumentTasksSave();
    }

    private async Task ProcessDocumentTaskAsync(DocumentTranslationTask task, AppSettings settings)
    {
        var acquired = false;
        try
        {
            task.StatusText = "等待处理";
            await _documentTaskSlots.WaitAsync(task.Cancellation.Token);
            acquired = true;
            task.Status = DocumentTaskStatus.Processing;
            task.Progress = 1;
            task.StatusText = "正在准备";
            var progress = new Progress<DocumentTranslationProgress>(value =>
            {
                task.Progress = value.Percentage;
                task.StatusText = value.Stage;
            });
            task.OutputPath = await Task.Run(() => _documentTranslationService.TranslateAsync(
                task.SourcePath, settings, progress, task.Cancellation.Token), task.Cancellation.Token);
            task.Progress = 100;
            task.Status = DocumentTaskStatus.Completed;
            task.StatusText = Path.GetFileName(task.OutputPath);
        }
        catch (OperationCanceledException)
        {
            task.Status = DocumentTaskStatus.Canceled;
            task.StatusText = "任务已取消";
        }
        catch (Exception ex)
        {
            task.Status = DocumentTaskStatus.Failed;
            task.Error = ex.Message;
            task.StatusText = ex.Message;
            AppLog.Error($"DocumentTranslation: {task.SourcePath}", ex);
            ShowNotification(_settings.NotifyErrors, "文档翻译失败", $"{task.FileName}：{ex.Message}");
        }
        finally
        {
            if (acquired) _documentTaskSlots.Release();
            UpdateDocumentTaskCount();
        }
    }

    private AppSettings CreateDocumentSettingsSnapshot() => _settings.Clone();

    private void UpdateDocumentTaskCount()
    {
        var active = _documentTasks.Count(task => task.Status is DocumentTaskStatus.Queued or DocumentTaskStatus.Processing);
        DocumentTaskCountText.Text = active == 0
            ? $"{_documentTasks.Count} 个任务"
            : $"{active} 个进行中 · {_documentTasks.Count} 个任务";
    }

    private void CancelDocumentTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DocumentTranslationTask task && task.CanCancel)
            task.Cancellation.Cancel();
    }

    private void OpenDocumentOutput_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DocumentTranslationTask { CanOpen: true } task) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{task.OutputPath}\"") { UseShellExecute = true });
    }

    private void ClearCompletedDocumentTasks_Click(object sender, RoutedEventArgs e)
    {
        foreach (var task in _documentTasks
                     .Where(item => item.Status is DocumentTaskStatus.Completed or DocumentTaskStatus.Failed or DocumentTaskStatus.Canceled)
                     .ToArray())
        {
            DetachDocumentTask(task);
        }
        UpdateDocumentTaskCount();
        ScheduleDocumentTasksSave();
    }

    private void BrowseDocumentOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择文档翻译输出目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(DocumentOutputDirectoryText.Text) ? DocumentOutputDirectoryText.Text : string.Empty
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) DocumentOutputDirectoryText.Text = dialog.SelectedPath;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        FlushPendingSettingsAutoSave();
        if (_allowClose || !_settings.CloseToTray) return;
        e.Cancel = true;
        _clipboardSearchRefreshTimer.Stop();
        _clipboardMainRefreshCancellation?.Cancel();
        Hide();
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _inputTranslationCancellation?.Cancel();
        _clipboardMainRefreshCancellation?.Cancel();
        _clipboardSearchRefreshTimer.Stop();
        foreach (var task in _documentTasks.Where(item => item.CanCancel)) task.Cancellation.Cancel();
        _documentTaskSaveTimer.Stop();
        PersistDocumentTasks();
        SetClipboardListener(false);
        UnregisterAllHotkeys();
        _keyboardHookHost.Dispose();
        _source?.RemoveHook(WndProc);
        _foregroundTimer.Stop();
        _clipboardWindow?.Close();
        _clipboardRepository.Dispose();
        _trayIcon.Visible = false;
        var trayIcon = _trayIcon.Icon;
        _trayIcon.Dispose();
        trayIcon?.Dispose();
    }

    private enum CaptureAction { Screenshot, Translate, Ocr, SilentOcr }

    private readonly record struct HotkeyDefinition(int Id, string Name, string Value);
    private readonly record struct GlobalHotkey(uint Modifiers, uint VirtualKey);
    private sealed record HotkeyRegistration(
        int Generation,
        IReadOnlyDictionary<GlobalHotkey, int> Hotkeys);

    private sealed class ThrottledUiProgress : IProgress<string>, IDisposable
    {
        private readonly object _gate = new();
        private readonly Dispatcher _dispatcher;
        private readonly Action<string> _handler;
        private readonly TimeSpan _interval;
        private string? _latest;
        private bool _scheduled;
        private bool _disposed;

        public ThrottledUiProgress(
            Dispatcher dispatcher,
            Action<string> handler,
            TimeSpan interval)
        {
            _dispatcher = dispatcher;
            _handler = handler;
            _interval = interval;
        }

        public void Report(string value)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _latest = value;
                if (_scheduled) return;
                _scheduled = true;
            }
            _ = FlushLaterAsync();
        }

        private async Task FlushLaterAsync()
        {
            await Task.Delay(_interval).ConfigureAwait(false);
            string? value;
            lock (_gate)
            {
                if (_disposed) return;
                value = _latest;
                _latest = null;
                _scheduled = false;
            }
            if (value is null) return;
            try
            {
                await _dispatcher.InvokeAsync(() => _handler(value), DispatcherPriority.Background);
            }
            catch (TaskCanceledException)
            {
            }
            catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted)
            {
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _latest = null;
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetMessageTime();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
