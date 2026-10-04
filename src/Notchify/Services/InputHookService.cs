using System.Runtime.InteropServices;
using System.Windows.Input;
using Notchify.Core;

namespace Notchify.Services;

public sealed class KeyEvent
{
    public int VirtualKey { get; init; }
    public bool IsDown { get; init; }
    public bool Injected { get; init; }
    /// <summary>Set by a handler to swallow the key so no other app sees it.</summary>
    public bool Handled { get; set; }
    public Key Key => KeyInterop.KeyFromVirtualKey(VirtualKey);
}

/// <summary>
/// One shared low-level keyboard hook. Installed lazily — only while a feature that needs it is enabled
/// (Caps Lock HUD, Keystroke HUD, cleaning lock, volume-key replacement).
/// </summary>
public sealed class InputHookService : IDisposable
{
    private IntPtr _hook;
    private readonly Native.HookProc _proc;
    private readonly List<Func<KeyEvent, bool>> _handlers = new();

    public InputHookService() => _proc = HookCallback;

    /// <summary>Register a handler; returns an IDisposable that unregisters it.</summary>
    public IDisposable Subscribe(Func<KeyEvent, bool> handler)
    {
        _handlers.Add(handler);
        EnsureHook();
        return new Unsubscriber(() =>
        {
            _handlers.Remove(handler);
            if (_handlers.Count == 0) RemoveHook();
        });
    }

    private void EnsureHook()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Error("keyboard hook failed: " + Marshal.GetLastWin32Error());
    }

    private void RemoveHook()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;
            var ev = new KeyEvent
            {
                VirtualKey = (int)data.vkCode,
                IsDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN,
                Injected = (data.flags & Native.LLKHF_INJECTED) != 0,
            };
            // Copy so handlers may unsubscribe while we iterate. Keep handlers fast — Windows drops slow hooks.
            foreach (var h in _handlers.ToArray())
            {
                try { if (h(ev)) ev.Handled = true; }
                catch (Exception ex) { Log.Error("key handler", ex); }
            }
            if (ev.Handled) return (IntPtr)1;
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => RemoveHook();

    private sealed class Unsubscriber(Action action) : IDisposable
    {
        private Action? _action = action;
        public void Dispose() { _action?.Invoke(); _action = null; }
    }

    public static bool IsDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
    public static bool CapsLockOn => (Native.GetKeyState(Native.VK_CAPITAL) & 1) != 0;

    /// <summary>True when the focused control in the foreground app is a password box (never log those keys).</summary>
    public static bool FocusIsPassword()
    {
        try
        {
            var fg = Native.GetForegroundWindow();
            var tid = Native.GetWindowThreadProcessId(fg, out _);
            var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
            if (!Native.GetGUIThreadInfo(tid, ref info) || info.hwndFocus == IntPtr.Zero) return false;
            return (Native.GetWindowLong(info.hwndFocus, Native.GWL_STYLE) & Native.ES_PASSWORD) != 0;
        }
        catch { return false; }
    }
}

/// <summary>Global hotkeys via RegisterHotKey, parsed from strings like "Ctrl+Shift+Space".</summary>
public sealed class HotkeyService
{
    private IntPtr _hwnd;
    private int _nextId = 0xB000;
    private readonly Dictionary<int, Action> _actions = new();

    public void Attach(IntPtr hwnd) => _hwnd = hwnd;

    public bool Register(string gesture, Action action)
    {
        if (_hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(gesture)) return false;
        if (!TryParse(gesture, out var mods, out var vk)) return false;
        var id = _nextId++;
        if (!Native.RegisterHotKey(_hwnd, id, mods | 0x4000 /* MOD_NOREPEAT */, vk))
        {
            Log.Info($"Hotkey {gesture} is taken by another app");
            return false;
        }
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }

    public bool Handle(int id)
    {
        if (!_actions.TryGetValue(id, out var a)) return false;
        a();
        return true;
    }

    public static bool TryParse(string gesture, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var part in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= 0x2; break;
                case "alt": mods |= 0x1; break;
                case "shift": mods |= 0x4; break;
                case "win": case "windows": mods |= 0x8; break;
                default:
                    if (Enum.TryParse<Key>(part, true, out var key))
                        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    else if (part.Length == 1)
                        vk = char.ToUpperInvariant(part[0]);
                    break;
            }
        }
        return vk != 0;
    }
}
