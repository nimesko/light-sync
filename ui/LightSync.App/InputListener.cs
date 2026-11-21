using System.Runtime.InteropServices;

namespace LightSync.App;

/// <summary>
/// Notices key presses and mouse clicks / movement anywhere in Windows (also while LightSync is hidden in
/// the tray), for keyboard and mouse triggers. It uses Raw Input on a hidden message-only window with
/// RIDEV_INPUTSINK, on a thread of its own with its own message loop.
/// It registers only for what <see cref="Configure"/> asks for, and unregisters as soon as nothing needs
/// it and when disposed. It keeps nothing: each key press is handed on as it happens (key repeat is
/// dropped by remembering which keys are down until they are released), and is never logged or stored.
/// </summary>
internal sealed class InputListener : IDisposable
{
    /// <summary>"key" (with its virtual-key code), "mouse_click" (with "left", "right" or "middle") or "mouse_move". Called on the listener thread.</summary>
    private readonly Action<string, int, string?> _onInput;
    private readonly object _gate = new();
    private Thread? _thread;
    private IntPtr _hwnd;
    /// <summary>Made once and kept for the life of this object: the window class points at it.</summary>
    private readonly WndProc _wndProc;
    private volatile bool _wantKeys, _wantClicks, _wantMoves;
    private bool _keysRegistered, _mouseRegistered;
    private bool _disposed;
    private readonly HashSet<ushort> _down = [];

    public InputListener(Action<string, int, string?> onInput)
    {
        _onInput = onInput;
        _wndProc = Proc;
    }

    /// <summary>What is being listened to right now (for the Triggers window).</summary>
    public bool ListeningToKeys => _keysRegistered;
    public bool ListeningToMouse => _mouseRegistered;

    /// <summary>Listen to exactly this; all false stops listening (and ends the thread).</summary>
    public void Configure(bool keys, bool clicks, bool moves)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _wantKeys = keys;
            _wantClicks = clicks;
            _wantMoves = moves;
            var any = keys || clicks || moves;
            if (_thread is null)
            {
                if (!any)
                {
                    return;
                }
                _thread = new Thread(Run) { IsBackground = true, Name = "LightSync input triggers" };
                _thread.Start();
                return; // The thread registers what is wanted once its window exists.
            }
            if (_hwnd != IntPtr.Zero)
            {
                PostMessage(_hwnd, any ? WM_APP_UPDATE : WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _wantKeys = _wantClicks = _wantMoves = false;
            thread = _thread;
            if (_hwnd != IntPtr.Zero)
            {
                PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }
        thread?.Join(TimeSpan.FromSeconds(1));
    }

    // ---------- the listener thread ----------

    private void Run()
    {
        try
        {
            var className = "LightSyncInput" + Environment.ProcessId;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                lpszClassName = className,
            };
            RegisterClassEx(ref wc); // Fails harmlessly if a previous listener thread registered it.
            var hwnd = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            lock (_gate)
            {
                _hwnd = hwnd;
            }
            if (hwnd == IntPtr.Zero)
            {
                CrashLog.Write("input triggers", $"could not create the input window (error {Marshal.GetLastWin32Error()})");
                return;
            }
            UpdateRegistration();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception e)
        {
            CrashLog.Write("input triggers", e);
        }
        finally
        {
            Unregister();
            lock (_gate)
            {
                _hwnd = IntPtr.Zero;
                _thread = null;
                _down.Clear();
            }
            // Something was asked for while this thread was ending: start again.
            if (!_disposed && (_wantKeys || _wantClicks || _wantMoves))
            {
                Configure(_wantKeys, _wantClicks, _wantMoves);
            }
        }
    }

    private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_INPUT:
                    OnRawInput(lParam);
                    break; // DefWindowProc must still run for WM_INPUT.
                case WM_APP_UPDATE:
                    UpdateRegistration();
                    return IntPtr.Zero;
                case WM_CLOSE:
                    Unregister();
                    DestroyWindow(hwnd);
                    return IntPtr.Zero;
                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception e)
        {
            // Never what was pressed: only that handling it failed.
            CrashLog.Write("input triggers", e.GetType().Name + ": " + e.Message);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Register for keyboard and / or mouse raw input as wanted; unregister what isn't.</summary>
    private void UpdateRegistration()
    {
        var keys = _wantKeys && !_disposed;
        var mouse = (_wantClicks || _wantMoves) && !_disposed;
        if (keys != _keysRegistered && Register(UsageKeyboard, keys))
        {
            _keysRegistered = keys;
        }
        if (!keys)
        {
            _down.Clear();
        }
        if (mouse != _mouseRegistered && Register(UsageMouse, mouse))
        {
            _mouseRegistered = mouse;
        }
        if (!keys && !mouse && _hwnd != IntPtr.Zero)
        {
            // Nothing needed any more: close the window, which ends this thread.
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void Unregister()
    {
        if (_keysRegistered)
        {
            Register(UsageKeyboard, false);
            _keysRegistered = false;
        }
        if (_mouseRegistered)
        {
            Register(UsageMouse, false);
            _mouseRegistered = false;
        }
    }

    private bool Register(ushort usage, bool on)
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = 0x01,
            usUsage = usage,
            dwFlags = on ? RIDEV_INPUTSINK : RIDEV_REMOVE,
            hwndTarget = on ? _hwnd : IntPtr.Zero,
        };
        var ok = RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        if (!ok)
        {
            CrashLog.Write("input triggers", $"RegisterRawInputDevices({(usage == UsageKeyboard ? "keyboard" : "mouse")}, {(on ? "on" : "off")}) failed: error {Marshal.GetLastWin32Error()}");
        }
        return ok;
    }

    private long _lastMove;
    private byte[] _buffer = new byte[64];

    private void OnRawInput(IntPtr handle)
    {
        uint size = 0;
        var header = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(handle, RID_INPUT, IntPtr.Zero, ref size, header) != 0 || size == 0 || size > 4096)
        {
            return;
        }
        if (_buffer.Length < size)
        {
            _buffer = new byte[size];
        }
        var pin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        try
        {
            var p = pin.AddrOfPinnedObject();
            if (GetRawInputData(handle, RID_INPUT, p, ref size, header) != size)
            {
                return;
            }
            var type = Marshal.ReadInt32(p, 0);
            var data = p + (int)header;
            if (type == RIM_TYPEKEYBOARD && _wantKeys)
            {
                // RAWKEYBOARD: MakeCode, Flags, Reserved, VKey (ushort each), Message (uint), ExtraInformation.
                var vk = (ushort)Marshal.ReadInt16(data, 6);
                var message = (uint)Marshal.ReadInt32(data, 8);
                if (vk is 0 or >= 255)
                {
                    return;
                }
                if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    if (_down.Add(vk)) // held keys repeat: only the first press counts
                    {
                        _onInput("key", vk, null);
                    }
                }
                else if (message is WM_KEYUP or WM_SYSKEYUP)
                {
                    _down.Remove(vk);
                }
            }
            else if (type == RIM_TYPEMOUSE)
            {
                // RAWMOUSE: usFlags (ushort, padded to 4), usButtonFlags, usButtonData, ulRawButtons, lLastX, lLastY.
                var buttons = (ushort)Marshal.ReadInt16(data, 4);
                var dx = Marshal.ReadInt32(data, 12);
                var dy = Marshal.ReadInt32(data, 16);
                if (_wantClicks && buttons != 0)
                {
                    if ((buttons & RI_MOUSE_LEFT_BUTTON_DOWN) != 0)
                    {
                        _onInput("mouse_click", 0, "left");
                    }
                    if ((buttons & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0)
                    {
                        _onInput("mouse_click", 0, "right");
                    }
                    if ((buttons & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0)
                    {
                        _onInput("mouse_click", 0, "middle");
                    }
                }
                if (_wantMoves && (dx != 0 || dy != 0))
                {
                    // At most 30 a second.
                    var now = Environment.TickCount64;
                    if (now - _lastMove >= 33)
                    {
                        _lastMove = now;
                        _onInput("mouse_move", 0, null);
                    }
                }
            }
        }
        finally
        {
            pin.Free();
        }
    }

    // ---------- Win32 ----------

    private const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_INPUT = 0x00FF, WM_APP_UPDATE = 0x8001;
    private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint RIDEV_REMOVE = 0x00000001, RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private const int RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;
    private const ushort UsageMouse = 0x02, UsageKeyboard = 0x06;
    private const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001, RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004, RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
