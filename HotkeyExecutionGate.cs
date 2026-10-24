using System.Diagnostics;

namespace SnapTranslate;

internal sealed class HotkeyExecutionGate
{
    private readonly object _gate = new();
    private readonly Dictionary<int, long> _pending = new();
    private readonly Dictionary<int, long> _lastAccepted = new();
    private long _nextToken;

    public bool TryEnter(int hotkeyId, TimeSpan debounce, out HotkeyExecutionLease lease)
    {
        lease = default;
        var now = Stopwatch.GetTimestamp();
        var debounceTicks = (long)(debounce.TotalSeconds * Stopwatch.Frequency);
        lock (_gate)
        {
            if (_pending.ContainsKey(hotkeyId)) return false;
            if (_lastAccepted.TryGetValue(hotkeyId, out var previous)
                && now - previous < debounceTicks)
                return false;

            var token = ++_nextToken;
            _pending[hotkeyId] = token;
            _lastAccepted[hotkeyId] = now;
            lease = new HotkeyExecutionLease(hotkeyId, token);
            return true;
        }
    }

    public void Exit(HotkeyExecutionLease lease)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(lease.HotkeyId, out var token) && token == lease.Token)
                _pending.Remove(lease.HotkeyId);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _pending.Clear();
            _lastAccepted.Clear();
        }
    }
}

internal readonly record struct HotkeyExecutionLease(int HotkeyId, long Token);

internal static class HotkeyEventTiming
{
    public static bool IsFresh(uint eventTime, uint currentTick, TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero) return false;
        var maximumMilliseconds = (uint)Math.Clamp(
            Math.Ceiling(maximumAge.TotalMilliseconds),
            1,
            uint.MaxValue);
        return unchecked(currentTick - eventTime) <= maximumMilliseconds;
    }

    public static bool IsFresh(uint eventTime, TimeSpan maximumAge)
        => IsFresh(eventTime, unchecked((uint)Environment.TickCount), maximumAge);
}

internal static class SyntheticKeyboardInputMarker
{
    public static readonly UIntPtr Value = UIntPtr.Size == 8
        ? new UIntPtr(0x53544B4559424455UL)
        : new UIntPtr(0x59424455U);
}
