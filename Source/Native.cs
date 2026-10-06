using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FloatTranslate
{
    public static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr handle, int id);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] input, int size);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out RECT rect);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr handle, ref MARGINS margins);
        [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int Left, Right, Top, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION data; }
        [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT keyboard; [FieldOffset(0)] public MOUSEINPUT mouse; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, flags, time; public IntPtr extra; }
        public static void RoundWindow(Window window)
        {
            try { int value = 2; DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 33, ref value, 4); } catch { }
        }
        public static double DisplayScale(IntPtr handle, POINT mouse)
        {
            try { if (handle != IntPtr.Zero) { uint dpi = GetDpiForWindow(handle); if (dpi > 0) return dpi / 96.0; }
                uint x, y; if (GetDpiForMonitor(MonitorFromPoint(mouse, 2), 0, out x, out y) == 0 && x > 0) return x / 96.0; } catch { }
            return 1;
        }
        public static bool SetAcrylic(Window window, bool enabled)
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;
            try
            {
                int mode = enabled ? 3 : 1;
                int result = DwmSetWindowAttribute(handle, 38, ref mode, 4);
                bool active = enabled && result == 0;
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
                if (chrome != null) chrome.GlassFrameThickness = new Thickness(active ? -1 : 0);
                MARGINS margins = new MARGINS { Left = active ? -1 : 0, Right = active ? -1 : 0, Top = active ? -1 : 0, Bottom = active ? -1 : 0 };
                if (DwmExtendFrameIntoClientArea(handle, ref margins) != 0) active = false;
                var source = HwndSource.FromHwnd(handle);
                if (source != null && source.CompositionTarget != null) source.CompositionTarget.BackgroundColor = active ? System.Windows.Media.Colors.Transparent : System.Windows.Media.Color.FromRgb(248, 250, 252);
                window.Background = active ? System.Windows.Media.Brushes.Transparent : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252));
                int dark = 0; DwmSetWindowAttribute(handle, 20, ref dark, 4);
                return active;
            }
            catch { return false; }
        }
        public static bool CopyKeys()
        {
            INPUT[] inputs = new INPUT[4];
            ushort[] keys = { 0x11, 0x43, 0x43, 0x11 };
            for (int i = 0; i < inputs.Length; i++) { inputs[i].type = 1; inputs[i].data.keyboard.vk = keys[i]; inputs[i].data.keyboard.flags = i >= 2 ? 2u : 0u; }
            return SendInput(4, inputs, Marshal.SizeOf(typeof(INPUT))) == 4;
        }
        public static bool ModifiersDown()
        {
            return GetAsyncKeyState(0x11) < 0 || GetAsyncKeyState(0x12) < 0 || GetAsyncKeyState(0x10) < 0 || GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0;
        }
    }
    public sealed class Hotkey
    {
        public uint Modifiers; public uint KeyCode;
        public static Hotkey Parse(string value)
        {
            uint modifiers = 0; uint key = 0;
            foreach (string raw in (value ?? "").Split('+'))
            {
                string part = raw.Trim();
                switch (part.ToLowerInvariant())
                {
                    case "ctrl": case "control": modifiers |= 2; break;
                    case "alt": modifiers |= 1; break;
                    case "shift": modifiers |= 4; break;
                    case "win": modifiers |= 8; break;
                    default:
                        if (key != 0 || part.Length == 0) throw new Exception("快捷键格式无效，请重新按下组合键。");
                        Key parsed;
                        if (part.Length == 1 && char.IsDigit(part[0])) key = (uint)part[0];
                        else if (Enum.TryParse<Key>(part, true, out parsed) && parsed != Key.None) key = (uint)KeyInterop.VirtualKeyFromKey(parsed);
                        else throw new Exception("快捷键包含不支持的按键。");
                        break;
                }
            }
            if ((modifiers & 11) == 0 || key == 0 || key == 0x10 || key == 0x11 || key == 0x12 || key == 0x5B || key == 0x5C || (key >= 0xA0 && key <= 0xA5))
                throw new Exception("快捷键需要包含 Ctrl、Alt 或 Win，以及一个普通按键。");
            return new Hotkey { Modifiers = modifiers, KeyCode = key };
        }
        public override bool Equals(object obj) { Hotkey other = obj as Hotkey; return other != null && Modifiers == other.Modifiers && KeyCode == other.KeyCode; }
        public override int GetHashCode() { return (int)(Modifiers * 1024 + KeyCode); }
        public static string FromEvent(KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt || key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin || key == Key.None) return null;
            ModifierKeys mods = Keyboard.Modifiers;
            if ((mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0) return null;
            var names = new List<string>();
            if ((mods & ModifierKeys.Control) != 0) names.Add("Ctrl");
            if ((mods & ModifierKeys.Alt) != 0) names.Add("Alt");
            if ((mods & ModifierKeys.Shift) != 0) names.Add("Shift");
            if ((mods & ModifierKeys.Windows) != 0) names.Add("Win");
            string name = key.ToString(); if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name.Substring(1);
            names.Add(name); return string.Join(" + ", names);
        }
    }
    public sealed class HotkeyManager : IDisposable
    {
        private readonly IntPtr handle; private readonly HwndSource source;
        private string selection, capture;
        public event Action<bool> Triggered;
        private const int SelectionId = 4101, CaptureId = 4102;
        public HotkeyManager(Window window)
        {
            handle = new WindowInteropHelper(window).Handle;
            source = HwndSource.FromHwnd(handle); source.AddHook(Hook);
        }
        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && (wParam.ToInt32() == SelectionId || wParam.ToInt32() == CaptureId))
            {
                handled = true; if (Triggered != null) Triggered(wParam.ToInt32() == CaptureId);
            }
            return IntPtr.Zero;
        }
        private bool Register(int id, string value)
        {
            Hotkey shortcut = Hotkey.Parse(value);
            return Native.RegisterHotKey(handle, id, shortcut.Modifiers | 0x4000, shortcut.KeyCode);
        }
        public void Apply(string nextSelection, string nextCapture)
        {
            if (Hotkey.Parse(nextSelection).Equals(Hotkey.Parse(nextCapture))) throw new Exception("两个快捷键不能相同。");
            string oldSelection = selection, oldCapture = capture;
            Native.UnregisterHotKey(handle, SelectionId); Native.UnregisterHotKey(handle, CaptureId);
            string conflict = null;
            if (!Register(SelectionId, nextSelection)) conflict = nextSelection;
            else if (!Register(CaptureId, nextCapture)) conflict = nextCapture;
            if (conflict != null)
            {
                Native.UnregisterHotKey(handle, SelectionId); Native.UnregisterHotKey(handle, CaptureId);
                bool restored = true;
                if (oldSelection != null) restored &= Register(SelectionId, oldSelection);
                if (oldCapture != null) restored &= Register(CaptureId, oldCapture);
                throw new Exception("快捷键 " + conflict + " 已被系统或其他软件占用，请换一个组合。" + (restored ? "" : "旧快捷键也无法恢复，请重新设置。"));
            }
            selection = nextSelection; capture = nextCapture;
        }
        public void Suspend() { Native.UnregisterHotKey(handle, SelectionId); Native.UnregisterHotKey(handle, CaptureId); }
        public void Resume() { if (selection != null && capture != null) Apply(selection, capture); }
        public void Dispose()
        {
            Native.UnregisterHotKey(handle, SelectionId); Native.UnregisterHotKey(handle, CaptureId); source.RemoveHook(Hook);
        }
    }
    public sealed class ClipboardSnapshot
    {
        private readonly System.Windows.DataObject saved = new System.Windows.DataObject();
        private bool hasData;
        public ClipboardSnapshot()
        {
            IDataObject current = Clipboard.GetDataObject();
            if (current == null) return;
            foreach (string format in current.GetFormats(false))
            {
                try
                {
                    object data = current.GetData(format, false);
                    var stream = data as System.IO.MemoryStream;
                    if (stream != null) data = new System.IO.MemoryStream(stream.ToArray());
                    if (data != null) { saved.SetData(format, data); hasData = true; }
                }
                catch { }
            }
        }
        public void Restore() { if (hasData) Clipboard.SetDataObject(saved, true); else Clipboard.Clear(); }
    }
    public static class SelectionReader
    {
        public static async Task<string> ReadAsync()
        {
            IntPtr foreground = Native.GetForegroundWindow();
            for (int i = 0; Native.ModifiersDown() && i < 50; i++) await Task.Delay(20);
            if (Native.ModifiersDown()) throw new Exception("请松开快捷键后重试。");
            if (Native.GetForegroundWindow() != foreground) throw new Exception("当前窗口已切换，请重新选中文字并按快捷键。");
            ClipboardSnapshot snapshot = null;
            for (int i = 0; i < 5 && snapshot == null; i++)
            {
                try { snapshot = new ClipboardSnapshot(); } catch { }
                if (snapshot == null) await Task.Delay(40);
            }
            if (snapshot == null) throw new Exception("剪贴板正被其他程序使用，请稍后重试。");
            uint before = Native.GetClipboardSequenceNumber();
            if (!Native.CopyKeys()) throw new Exception("无法读取选区。请确认目标程序没有以更高权限运行，或使用框选截图翻译。");
            uint copied = before;
            string text = null;
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(25);
                copied = Native.GetClipboardSequenceNumber();
                if (copied == before) continue;
                try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); break; } catch { }
            }
            if (copied != before && Native.GetClipboardSequenceNumber() == copied)
            {
                for (int i = 0; i < 5 && Native.GetClipboardSequenceNumber() == copied; i++) { try { snapshot.Restore(); break; } catch { } await Task.Delay(30); }
            }
            if (string.IsNullOrWhiteSpace(text)) throw new Exception("没有读到选中文字。请先选中可复制的文本；图片、受保护内容或高权限窗口可尝试框选截图。");
            return text;
        }
    }
}
