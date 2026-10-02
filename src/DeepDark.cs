// DeepDark —— Windows 10/11 屏幕调光（让屏幕比显示器最低亮度还暗）
//
// 变暗机制：
//   1) 驱动级调光（默认）：用 SetDeviceGammaRamp 改写显示器输出的 gamma 查找表。
//      该 LUT 在显卡扫描输出前生效，所以任务视图、切换虚拟桌面、开始菜单、UAC 界面
//      全都在同一亮度下，不会有覆盖窗，也不会闪动；不依赖显卡品牌。
//   2) 黑色覆盖窗（兼容模式）：每台显示器铺一层可调不透明度的置顶黑窗。
//      系统不允许改 gamma 时（例如开启 HDR）会自动回退，也可以手动强制使用。
//
// 命令行：
//   DeepDark.exe                 沿用上次的浓度
//   DeepDark.exe --level=70      指定变暗程度 0~95
//   DeepDark.exe --restore       把显示器 gamma 恢复成线性（屏幕被搞暗时救急）
//
// 编译：powershell -ExecutionPolicy Bypass -File build.ps1

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DeepDark
{
    // ------------------------------------------------------------------ Win32

    internal static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;

        public const int WM_HOTKEY = 0x0312;
        public const int WM_SETTINGCHANGE = 0x001A;
        public const int WM_DISPLAYCHANGE = 0x007E;
        public const int WM_POWERBROADCAST = 0x0218;
        public const int WM_DWMCOMPOSITIONCHANGED = 0x031E;
        public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        public const int PBT_APMRESUMESUSPEND = 0x0007;
        public const int SM_CMONITORS = 80;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public const uint VK_UP = 0x26;
        public const uint VK_DOWN = 0x28;
        public const uint VK_H = 0x48;
        public const uint VK_M = 0x4D;
        public const uint VK_P = 0x50;
        public const uint VK_Q = 0x51;
        public const uint VK_S = 0x53;
        public const uint VK_F11 = 0x7A;
        public const uint VK_F12 = 0x7B;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public static int GetExStyle(IntPtr hWnd) { return GetWindowLong(hWnd, GWL_EXSTYLE); }
        public static int SetExStyle(IntPtr hWnd, int value) { return SetWindowLong(hWnd, GWL_EXSTYLE, value); }

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        public static void PushToTopMost(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>把闲置的工作集还给系统，降低常驻内存（任务管理器显示的就是这个数）。</summary>
        public static void TrimMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                EmptyWorkingSet(GetCurrentProcess());
            }
            catch { }
        }
    }

    // ------------------------------------------------------- gamma 查找表调光

    internal static class Gamma
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDC(string driver, string device, string port, IntPtr initData);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

        private const int RampLength = 768;   // 3 通道 × 256

        // CreateDC 较贵（约 10ms），而巡检每 10 秒就要读一次 gamma，
        // 所以把每个显示器的 DC 缓存复用；显示器变化或退出时释放。
        private static readonly Dictionary<string, IntPtr> dcCache =
            new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);

        private static IntPtr GetDc(string deviceName)
        {
            IntPtr hdc;
            if (dcCache.TryGetValue(deviceName, out hdc) && hdc != IntPtr.Zero) return hdc;

            hdc = CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
            if (hdc != IntPtr.Zero) dcCache[deviceName] = hdc;
            return hdc;
        }

        public static void ReleaseDcs()
        {
            foreach (KeyValuePair<string, IntPtr> pair in dcCache)
            {
                try { if (pair.Value != IntPtr.Zero) DeleteDC(pair.Value); } catch { }
            }
            dcCache.Clear();
        }

        public static ushort[] Read(string deviceName)
        {
            ushort[] ramp = new ushort[RampLength];
            return ReadInto(deviceName, ramp) ? ramp : null;
        }

        /// <summary>读进调用方给好的缓冲区，避免巡检时反复分配。</summary>
        public static bool ReadInto(string deviceName, ushort[] buffer)
        {
            if (buffer == null || buffer.Length < RampLength) return false;
            IntPtr hdc = GetDc(deviceName);
            if (hdc == IntPtr.Zero) return false;
            try { return GetDeviceGammaRamp(hdc, buffer); }
            catch { return false; }
        }

        public static bool Write(string deviceName, ushort[] ramp)
        {
            if (ramp == null) return false;
            IntPtr hdc = GetDc(deviceName);
            if (hdc == IntPtr.Zero) return false;
            try { return SetDeviceGammaRamp(hdc, ramp); }
            catch { return false; }
        }

        /// <summary>按比例缩放基准曲线：factor=1 等于基准，0.3 表示只透出 30% 亮度。</summary>
        public static ushort[] Build(ushort[] baseline, double factor)
        {
            ushort[] ramp = new ushort[RampLength];
            for (int channel = 0; channel < 3; channel++)
            {
                for (int i = 0; i < 256; i++)
                {
                    int baseValue = baseline == null ? i * 257 : baseline[channel * 256 + i];
                    int value = (int)Math.Round(baseValue * factor);
                    if (value < 0) value = 0;
                    if (value > 65535) value = 65535;
                    ramp[channel * 256 + i] = (ushort)value;
                }
            }
            return ramp;
        }

        public static ushort[] Linear() { return Build(null, 1.0); }

        public static int MaxDifference(ushort[] a, ushort[] b)
        {
            if (a == null || b == null) return int.MaxValue;
            int max = 0;
            for (int i = 0; i < RampLength; i++)
            {
                int diff = Math.Abs(a[i] - b[i]);
                if (diff > max) max = diff;
            }
            return max;
        }

        public static bool Same(ushort[] a, ushort[] b, int tolerance)
        {
            return MaxDifference(a, b) <= tolerance;
        }
    }

    // ------------------------------------------- 高级颜色（HDR）状态查询

    /// <summary>
    /// 查询每台显示器是否启用了 HDR / 高级颜色。开启时系统会忽略 gamma 查找表，
    /// 这时必须改用黑色覆盖窗。用 DisplayConfig API，不依赖第三方库。
    /// </summary>
    internal static class DisplayConfig
    {
        private const uint QDC_ONLY_ACTIVE_PATHS = 2;
        private const int GET_SOURCE_NAME = 1;
        private const int GET_ADVANCED_COLOR_INFO = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RATIONAL { public uint Numerator; public uint Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SourceInfo { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TargetInfo
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
            public uint rotation; public uint scaling; public RATIONAL refreshRate;
            public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PathInfo { public SourceInfo sourceInfo; public TargetInfo targetInfo; public uint flags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ModeInfo
        {
            public int infoType; public uint id; public LUID adapterId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] padding;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfoHeader { public int type; public uint size; public LUID adapterId; public uint id; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SourceDeviceName
        {
            public DeviceInfoHeader header;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public ushort[] viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AdvancedColorInfo
        {
            public DeviceInfoHeader header;
            public uint value;              // bit0 支持, bit1 已启用
            public uint colorEncoding;
            public uint bitsPerColorChannel;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths,
            ref uint numModes, [Out] ModeInfo[] modes, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName info);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo info);

        /// <summary>返回 GDI 设备名（\\.\DISPLAY1）→ 是否开启 HDR。查询失败返回空表。</summary>
        public static Dictionary<string, bool> GetHdrStateByGdiName()
        {
            Dictionary<string, bool> result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                uint numPaths, numModes;
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPaths, out numModes) != 0) return result;
                if (numPaths == 0) return result;

                PathInfo[] paths = new PathInfo[numPaths];
                ModeInfo[] modes = new ModeInfo[numModes];
                for (int i = 0; i < modes.Length; i++) modes[i].padding = new byte[48];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0)
                    return result;

                for (uint i = 0; i < numPaths; i++)
                {
                    string gdiName = null;

                    SourceDeviceName source = new SourceDeviceName();
                    source.header.type = GET_SOURCE_NAME;
                    source.header.size = (uint)Marshal.SizeOf(typeof(SourceDeviceName));
                    source.header.adapterId = paths[i].sourceInfo.adapterId;
                    source.header.id = paths[i].sourceInfo.id;
                    source.viewGdiDeviceName = new ushort[32];
                    if (DisplayConfigGetDeviceInfo(ref source) == 0)
                        gdiName = new string(Array.ConvertAll(source.viewGdiDeviceName,
                            delegate(ushort c) { return (char)c; })).TrimEnd('\0');

                    AdvancedColorInfo color = new AdvancedColorInfo();
                    color.header.type = GET_ADVANCED_COLOR_INFO;
                    color.header.size = (uint)Marshal.SizeOf(typeof(AdvancedColorInfo));
                    color.header.adapterId = paths[i].targetInfo.adapterId;
                    color.header.id = paths[i].targetInfo.id;
                    int err = DisplayConfigGetDeviceInfo(ref color);

                    if (gdiName != null && gdiName.Length > 0 && err == 0)
                        result[gdiName] = (color.value & 2) != 0;
                }
            }
            catch { }
            return result;
        }

        public static bool AnyHdrEnabled()
        {
            foreach (KeyValuePair<string, bool> pair in GetHdrStateByGdiName())
            {
                if (pair.Value) return true;
            }
            return false;
        }
    }

    // ------------------------------------------------------------ 黑色覆盖窗

    internal sealed class OverlayForm : Form
    {
        private bool clickThrough;

        public OverlayForm(Rectangle bounds, double opacity, bool clickThrough)
        {
            this.clickThrough = clickThrough;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false;
            Text = "DeepDarkOverlay";
            Bounds = bounds;
            BackColor = Color.Black;
            TopMost = true;
            Opacity = ClampOpacity(opacity);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED;
                if (clickThrough) cp.ExStyle |= Native.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public static double ClampOpacity(double value)
        {
            if (value < 0.0) return 0.0;
            if (value > 0.95) return 0.95;
            return value;
        }

        public void SetOpacity(double value)
        {
            double v = ClampOpacity(value);
            if (Opacity != v) Opacity = v;
        }

        public void SetClickThrough(bool on)
        {
            clickThrough = on;
            if (!IsHandleCreated) return;
            int style = Native.GetExStyle(Handle);
            int updated = on ? (style | Native.WS_EX_TRANSPARENT) : (style & ~Native.WS_EX_TRANSPARENT);
            if (updated != style) Native.SetExStyle(Handle, updated);
        }

        public void ReassertTopMost()
        {
            if (IsHandleCreated && Visible) Native.PushToTopMost(Handle);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
        }
    }

    /// <summary>不响应滚轮的滑块：滚轮在它上面时用来滚动整个设置面板。</summary>
    internal sealed class NoWheelTrackBar : TrackBar
    {
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            MainForm form = FindForm() as MainForm;
            if (form != null) form.ScrollBy(e.Delta);
        }
    }

    // -------------------------------------------------------- 快捷键的表示与解析

    internal static class HotkeyText
    {
        public const uint NoRepeat = Native.MOD_NOREPEAT;

        public static uint ModifierBits(uint mods)
        {
            return mods & (Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_WIN);
        }

        public static string Format(uint mods, uint vk)
        {
            StringBuilder sb = new StringBuilder();
            if ((mods & Native.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((mods & Native.MOD_ALT) != 0) sb.Append("Alt+");
            if ((mods & Native.MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((mods & Native.MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(KeyName(vk));
            return sb.ToString();
        }

        public static string KeyName(uint vk)
        {
            switch (vk)
            {
                case Native.VK_UP: return "↑";
                case Native.VK_DOWN: return "↓";
                case 0x25: return "←";
                case 0x27: return "→";
                case 0x21: return "PageUp";
                case 0x22: return "PageDown";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                case 0x0D: return "Enter";
                case 0x20: return "Space";
                case 0xBD: return "OemMinus";
                case 0xBB: return "OemPlus";
                case 0xDB: return "OemOpenBrackets";
                case 0xDD: return "OemCloseBrackets";
                case 0xBA: return "OemSemicolon";
                case 0xDE: return "OemQuotes";
                case 0xBC: return "OemComma";
                case 0xBE: return "OemPeriod";
                case 0xBF: return "OemQuestion";
                case 0xC0: return "OemTilde";
                default:
                    Keys key = (Keys)vk;
                    if (vk >= 0x60 && vk <= 0x69) return "数字键盘" + (vk - 0x60);
                    return key.ToString();
            }
        }

        /// <summary>从 KeyDown 的 KeyData 里取组合键；只按修饰键、或没按修饰键，都不算有效。</summary>
        public static bool TryCapture(Keys keyData, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;

            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.None ||
                key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey ||
                key == Keys.Menu || key == Keys.LMenu || key == Keys.RMenu ||
                key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey ||
                key == Keys.LWin || key == Keys.RWin)
                return false;

            Keys modifierKeys = keyData & Keys.Modifiers;
            uint bits = 0;
            if ((modifierKeys & Keys.Control) != 0) bits |= Native.MOD_CONTROL;
            if ((modifierKeys & Keys.Alt) != 0) bits |= Native.MOD_ALT;
            if ((modifierKeys & Keys.Shift) != 0) bits |= Native.MOD_SHIFT;
            if (bits == 0) return false;      // 必须带至少一个修饰键

            mods = bits | NoRepeat;
            vk = (uint)key;
            return true;
        }

        public static bool TryParse(string text, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrEmpty(text)) return false;

            string[] parts = text.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0) return false;

                if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Control", StringComparison.OrdinalIgnoreCase)) { mods |= Native.MOD_CONTROL; continue; }
                if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase)) { mods |= Native.MOD_ALT; continue; }
                if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase)) { mods |= Native.MOD_SHIFT; continue; }
                if (string.Equals(part, "Win", StringComparison.OrdinalIgnoreCase)) { mods |= Native.MOD_WIN; continue; }

                // 最后一段是主键
                if (i != parts.Length - 1) return false;
                uint parsed;
                if (!TryParseKey(part, out parsed)) return false;
                vk = parsed;
            }

            if (vk == 0 || ModifierBits(mods) == 0) return false;
            mods |= NoRepeat;
            return true;
        }

        private static bool TryParseKey(string name, out uint vk)
        {
            vk = 0;
            switch (name)
            {
                case "↑": vk = Native.VK_UP; return true;
                case "↓": vk = Native.VK_DOWN; return true;
                case "←": vk = 0x25; return true;
                case "→": vk = 0x27; return true;
            }
            if (name.StartsWith("数字键盘", StringComparison.Ordinal) && name.Length == 5)
            {
                int digit;
                if (int.TryParse(name.Substring(4), out digit) && digit >= 0 && digit <= 9)
                {
                    vk = (uint)(0x60 + digit);
                    return true;
                }
            }
            try
            {
                object parsed = Enum.Parse(typeof(Keys), name, true);
                vk = Convert.ToUInt32(parsed);
                return vk != 0;
            }
            catch { return false; }
        }
    }

    internal sealed class HotkeyBinding
    {
        public int Id;
        public string Name;
        public uint Mods;
        public uint Vk;

        public HotkeyBinding(int id, string name, uint mods, uint vk)
        {
            Id = id;
            Name = name;
            Mods = mods;
            Vk = vk;
        }

        public HotkeyBinding Clone()
        {
            return new HotkeyBinding(Id, Name, Mods, Vk);
        }

        public string Display { get { return HotkeyText.Format(Mods, Vk); } }
    }

    // ------------------------------------------------------------ 设置面板

    internal sealed class HotkeyForm : Form
    {
        private readonly List<HotkeyBinding> items;
        private readonly TextBox[] boxes;
        private readonly MainForm owner;

        public bool Applied;

        public HotkeyForm(MainForm owner, List<HotkeyBinding> current)
        {
            this.owner = owner;
            items = new List<HotkeyBinding>();
            foreach (HotkeyBinding b in current) items.Add(b.Clone());

            float scale = owner.UiScale;
            Text = "修改快捷键";
            Font = owner.Font;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(S(430, scale), S(60 + items.Count * 46 + 70, scale));
            TopMost = true;

            Label hint = new Label();
            hint.Text = "点一行再按下想要的组合键（必须含 Ctrl / Alt / Shift 中至少一个）";
            hint.AutoSize = true;
            hint.Location = new Point(S(16, scale), S(14, scale));
            hint.ForeColor = Color.DimGray;
            Controls.Add(hint);

            boxes = new TextBox[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                Label label = new Label();
                label.Text = items[i].Name;
                label.AutoSize = false;
                label.TextAlign = ContentAlignment.MiddleLeft;
                label.Location = new Point(S(16, scale), S(46 + i * 46, scale));
                label.Size = new Size(S(110, scale), S(30, scale));
                Controls.Add(label);

                TextBox box = new TextBox();
                box.ReadOnly = true;
                box.TextAlign = HorizontalAlignment.Center;
                box.Text = items[i].Display;
                box.Location = new Point(S(130, scale), S(46 + i * 46, scale));
                box.Size = new Size(S(280, scale), S(30, scale));
                int index = i;
                box.KeyDown += delegate(object sender, KeyEventArgs e)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    uint mods, vk;
                    if (!HotkeyText.TryCapture(e.KeyData, out mods, out vk))
                    {
                        if ((e.KeyData & Keys.KeyCode) != Keys.None) box.Text = items[index].Display;
                        return;
                    }
                    items[index].Mods = mods;
                    items[index].Vk = vk;
                    box.Text = items[index].Display;
                };
                box.Enter += delegate { box.SelectAll(); };
                Controls.Add(box);
                boxes[i] = box;
            }

            int baseY = 46 + items.Count * 46 + 12;

            Button ok = new Button();
            ok.Text = "确定";
            ok.Location = new Point(S(16, scale), S(baseY, scale));
            ok.Size = new Size(S(120, scale), S(34, scale));
            ok.Click += delegate { OnOk(); };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Location = new Point(S(150, scale), S(baseY, scale));
            cancel.Size = new Size(S(120, scale), S(34, scale));
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);

            Button reset = new Button();
            reset.Text = "恢复默认";
            reset.Location = new Point(S(284, scale), S(baseY, scale));
            reset.Size = new Size(S(130, scale), S(34, scale));
            reset.Click += delegate { OnReset(); };
            Controls.Add(reset);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private static int S(int value, float scale)
        {
            return (int)Math.Round(value * scale);
        }

        private void OnReset()
        {
            List<HotkeyBinding> defaults = MainForm.CreateDefaultBindings();
            for (int i = 0; i < items.Count && i < defaults.Count; i++)
            {
                items[i].Mods = defaults[i].Mods;
                items[i].Vk = defaults[i].Vk;
                boxes[i].Text = items[i].Display;
            }
        }

        private void OnOk()
        {
            // 查重
            for (int i = 0; i < items.Count; i++)
            {
                for (int j = i + 1; j < items.Count; j++)
                {
                    if (HotkeyText.ModifierBits(items[i].Mods) == HotkeyText.ModifierBits(items[j].Mods) &&
                        items[i].Vk == items[j].Vk)
                    {
                        MessageBox.Show(this, "“" + items[i].Name + "”和“" + items[j].Name + "”用了同一个组合键，请改掉一个。",
                            "快捷键重复", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
            }

            string failed;
            if (!owner.TryApplyBindings(items, out failed))
            {
                MessageBox.Show(this, "组合键 " + failed + " 已被其它程序占用，请换一个。",
                    "无法注册", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Applied = true;
            Close();
        }
    }

    // -------------------------------------------------------------- 主窗口

    internal sealed class MainForm : Form
    {
        private const int HotkeyDarker = 1;
        private const int HotkeyLighter = 2;
        private const int HotkeyToggle = 3;
        private const int HotkeyQuit = 4;
        private const int HotkeyShowPanel = 5;

        private const int MinLevel = 0;
        private const int MaxLevel = 95;
        private const int DefaultLevel = 45;

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "DeepDark";
        private const string SettingsKeyPath = @"Software\DeepDark";
        private const string LevelValueName = "Level";

        private static readonly uint ShowPanelMessage = Native.RegisterWindowMessage("DeepDark_ShowPanel_1");

        /// <summary>--restore 用它让正在运行的实例先优雅退出（退出时它自己会还原 gamma）。</summary>
        private static readonly uint QuitForRestoreMessage = Native.RegisterWindowMessage("DeepDark_QuitForRestore_1");

        /// <summary>一台显示器的调光状态。</summary>
        private sealed class DisplayInfo
        {
            public string DeviceName;
            public Rectangle Bounds;
            public bool HdrOn;
            public bool GammaOk;
            public ushort[] OriginalRamp;
            public ushort[] TargetRamp;
            public ushort[] CurrentRamp;   // 复用的读回缓冲区
        }

        private readonly List<DisplayInfo> displays = new List<DisplayInfo>();
        private readonly Dictionary<string, OverlayForm> overlays =
            new Dictionary<string, OverlayForm>(StringComparer.OrdinalIgnoreCase);
        private readonly List<HotkeyBinding> bindings = new List<HotkeyBinding>();
        private readonly System.Windows.Forms.Timer watchdog = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer reapply = new System.Windows.Forms.Timer();

        private FlowLayoutPanel content;
        private Label levelLabel;
        private NoWheelTrackBar slider;
        private CheckBox throughBox;
        private CheckBox overlayBox;
        private CheckBox autoStartBox;
        private CheckBox trayBox;
        private Button toggleButton;
        private Label statusLabel;
        private NotifyIcon tray;

        private float uiScale = 1f;
        private int level = DefaultLevel;
        private bool maskVisible = true;
        private bool clickThrough = true;
        private bool forceOverlay;
        private bool suppressSlider;
        private bool cleaned;
        private bool balloonShown;
        private bool minimizeToTray = true;
        private int tickCount;
        private bool hdrWasOn;
        private string lastStatusText;

        private static Settings startupSettings = new Settings();

        public MainForm()
        {
            Settings settings = startupSettings;
            level = ClampLevel(settings.Level);
            minimizeToTray = settings.MinimizeToTray;

            BuildUi();
            IntPtr forceHandle = Handle;      // 快捷键需要已经存在的窗口句柄
            RegisterHotkeys(settings);

            RebuildDisplays();
            ApplyAll();

            SetupTray();
            CreateTrayMenu();

            reapply.Interval = 500;
            reapply.Tick += delegate { reapply.Stop(); ApplyAll(); };

            watchdog.Interval = PollInterval();
            watchdog.Tick += WatchdogTick;
            watchdog.Start();
        }

        public float UiScale { get { return uiScale; } }

        // ------------------------------------------------------------ 界面

        private void BuildUi()
        {
            Text = "DeepDark · 屏幕调光";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;     // 尺寸按当前字体实测，避免 DPI 二次缩放错位
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            MaximizeBox = true;
            MinimizeBox = true;
            AutoScroll = true;
            Icon = MakeAppIcon();

            uiScale = Font.Height / 15f;
            if (uiScale < 1f) uiScale = 1f;

            content = new FlowLayoutPanel();
            content.FlowDirection = FlowDirection.TopDown;
            content.WrapContents = false;
            content.AutoSize = true;
            content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            content.Dock = DockStyle.Top;
            content.Padding = new Padding(0);
            Controls.Add(content);

            int side = S(14);
            int wide = S(352);

            levelLabel = new Label();
            levelLabel.AutoSize = true;
            levelLabel.Margin = new Padding(side, S(12), side, S(2));
            content.Controls.Add(levelLabel);

            slider = new NoWheelTrackBar();
            slider.Minimum = MinLevel;
            slider.Maximum = MaxLevel;
            slider.TickFrequency = 5;
            slider.SmallChange = 1;
            slider.LargeChange = 5;
            slider.Value = level;
            slider.AutoSize = false;
            slider.Width = wide;
            slider.Height = S(45);
            slider.Margin = new Padding(side, 0, side, S(4));
            slider.ValueChanged += delegate { if (!suppressSlider) SetLevel(slider.Value); };
            content.Controls.Add(slider);

            throughBox = new CheckBox();
            throughBox.Text = "鼠标可穿透遮罩（推荐）";
            throughBox.AutoSize = true;
            throughBox.Margin = new Padding(side, S(2), side, S(2));
            throughBox.Checked = clickThrough;
            throughBox.CheckedChanged += ThroughChanged;
            content.Controls.Add(throughBox);

            overlayBox = new CheckBox();
            overlayBox.Text = "改用黑色覆盖窗（兼容模式）";
            overlayBox.AutoSize = true;
            overlayBox.Margin = new Padding(side, S(2), side, S(2));
            overlayBox.Checked = forceOverlay;
            overlayBox.CheckedChanged += OverlayModeChanged;
            content.Controls.Add(overlayBox);

            autoStartBox = new CheckBox();
            autoStartBox.Text = "开机自动启动";
            autoStartBox.AutoSize = true;
            autoStartBox.Margin = new Padding(side, S(2), side, S(2));
            autoStartBox.Checked = IsAutoStartEnabled();
            autoStartBox.CheckedChanged += AutoStartChanged;
            content.Controls.Add(autoStartBox);

            trayBox = new CheckBox();
            trayBox.Text = "最小化时收进托盘（推荐）";
            trayBox.AutoSize = true;
            trayBox.Margin = new Padding(side, S(2), side, S(2));
            trayBox.Checked = minimizeToTray;
            trayBox.CheckedChanged += TrayModeChanged;
            content.Controls.Add(trayBox);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = true;
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.Margin = new Padding(side, S(6), side, S(4));
            content.Controls.Add(buttons);

            toggleButton = MakeButton("隐藏遮罩");
            toggleButton.Click += delegate { ToggleMask(); };
            buttons.Controls.Add(toggleButton);

            Button hotkeyButton = MakeButton("修改快捷键…");
            hotkeyButton.Click += delegate { EditHotkeys(); };
            buttons.Controls.Add(hotkeyButton);

            Button quitButton = MakeButton("退出程序");
            quitButton.Click += delegate { Close(); };
            buttons.Controls.Add(quitButton);

            statusLabel = new Label();
            statusLabel.AutoSize = true;
            statusLabel.MaximumSize = new Size(wide, 0);
            statusLabel.Margin = new Padding(side, S(8), side, S(12));
            statusLabel.ForeColor = Color.DimGray;
            content.Controls.Add(statusLabel);

            content.SizeChanged += delegate { AutoScrollMinSize = new Size(0, content.Height + S(8)); };

            UpdateLevelText();
            UpdateStatus();
            FitToContent();
        }

        private Button MakeButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.Padding = new Padding(S(10), S(4), S(10), S(4));
            button.Margin = new Padding(0, 0, S(8), S(4));
            return button;
        }

        private int S(int value) { return (int)Math.Round(value * uiScale); }

        private void FitToContent()
        {
            content.PerformLayout();
            Size preferred = content.PreferredSize;

            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            int maxHeight = (int)(work.Height * 0.88);

            int width = Math.Max(S(340), preferred.Width + S(30));
            int height = preferred.Height + S(30);
            if (height > maxHeight) height = maxHeight;

            ClientSize = new Size(width, height);
            AutoScrollMinSize = new Size(0, content.Height + S(8));

            if (statusLabel != null) statusLabel.MaximumSize = new Size(width - S(40), 0);
            if (slider != null) slider.Width = width - S(40);
        }

        private void Relayout()
        {
            if (content == null) return;
            int width = ClientSize.Width;
            if (statusLabel != null) statusLabel.MaximumSize = new Size(Math.Max(S(200), width - S(46)), 0);
            if (slider != null) slider.Width = Math.Max(S(180), width - S(40));
            content.PerformLayout();
            AutoScrollMinSize = new Size(0, content.Height + S(8));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            if (WindowState == FormWindowState.Normal) Relayout();

            if (WindowState == FormWindowState.Minimized && minimizeToTray && tray != null)
            {
                Hide();
                Native.TrimMemory();

                if (!balloonShown)
                {
                    balloonShown = true;
                    tray.ShowBalloonTip(5000, "DeepDark 已最小化到托盘",
                        "调光继续生效。双击托盘图标可重新打开设置；若右下角没看到图标，" +
                        "点任务栏上的 ∧ 展开隐藏图标即可。", ToolTipIcon.Info);
                }
            }
        }

        /// <summary>滚轮滚动设置面板（滑块上的滚轮也会转到这里）。</summary>
        public void ScrollBy(int delta)
        {
            if (!AutoScroll) return;
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = 3;
            int step = S(lines * 20);
            int max = Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height + S(8));
            int target = -AutoScrollPosition.Y - (delta / 120) * step;
            if (target < 0) target = 0;
            if (target > max) target = max;
            AutoScrollPosition = new Point(0, target);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollBy(e.Delta);   // 自己处理，不调用 base，避免重复滚动
        }

        private static Icon MakeAppIcon()
        {
            try
            {
                // 自己编码标准 32 位色 ICO：不能依赖 Bitmap.GetHicon() 的临时句柄，
                // 那个句柄一销毁图标就失效（托盘里会什么都看不到）。
                return new Icon(new MemoryStream(BuildAppIconIco()));
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        internal static byte[] BuildAppIconIco()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.FromArgb(26, 26, 30));
                    using (SolidBrush ring = new SolidBrush(Color.FromArgb(80, 80, 92)))
                        g.FillEllipse(ring, 2, 2, 28, 28);
                    using (SolidBrush moon = new SolidBrush(Color.FromArgb(255, 233, 150)))
                        g.FillEllipse(moon, 9, 8, 14, 14);
                    using (SolidBrush cut = new SolidBrush(Color.FromArgb(26, 26, 30)))
                        g.FillEllipse(cut, 15, 5, 14, 14);
                }
                return EncodeIco(bmp);
            }
        }

        /// <summary>把位图编码成标准 ICO（32 位色 DIB + AND 掩码）。</summary>
        internal static byte[] EncodeIco(Bitmap bmp)
        {
            int width = bmp.Width;
            int height = bmp.Height;
            int xorSize = width * 4 * height;
            int andStride = ((width + 31) / 32) * 4;
            int andSize = andStride * height;
            int imageSize = 40 + xorSize + andSize;

            using (MemoryStream stream = new MemoryStream(22 + imageSize))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)1);

                writer.Write((byte)width);
                writer.Write((byte)height);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(imageSize);
                writer.Write(22);

                writer.Write(40);
                writer.Write(width);
                writer.Write(height * 2);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(0);
                writer.Write(xorSize + andSize);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);

                for (int y = height - 1; y >= 0; y--)
                {
                    for (int x = 0; x < width; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        writer.Write(c.B);
                        writer.Write(c.G);
                        writer.Write(c.R);
                        writer.Write(c.A);
                    }
                }

                writer.Write(new byte[andSize]);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private void SetupTray()
        {
            tray = new NotifyIcon();
            tray.Icon = Icon;
            tray.Text = "DeepDark —— 让屏幕比最低亮度还暗";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowPanel(); };
        }

        private void CreateTrayMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem showItem = new ToolStripMenuItem("显示设置面板");
            showItem.Click += delegate { ShowPanel(); };
            menu.Items.Add(showItem);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(MakeTrayItem("变暗 5%", delegate { SetLevel(level + 5); }, HotkeyDarker));
            menu.Items.Add(MakeTrayItem("变亮 5%", delegate { SetLevel(level - 5); }, HotkeyLighter));
            menu.Items.Add(MakeTrayItem("显示 / 隐藏遮罩", delegate { ToggleMask(); }, HotkeyToggle));
            menu.Items.Add(MakeTrayItem("显示设置面板", delegate { ShowPanel(); }, HotkeyShowPanel));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MakeTrayItem("退出", delegate { Close(); }, HotkeyQuit));

            tray.ContextMenuStrip = menu;
        }

        private ToolStripMenuItem MakeTrayItem(string text, EventHandler handler, int hotkeyId)
        {
            HotkeyBinding binding = FindBinding(hotkeyId);
            ToolStripMenuItem item = new ToolStripMenuItem(text + (binding == null ? "" : "   (" + binding.Display + ")"));
            item.Click += handler;
            return item;
        }

        private HotkeyBinding FindBinding(int id)
        {
            foreach (HotkeyBinding b in bindings)
            {
                if (b.Id == id) return b;
            }
            return null;
        }

        // ------------------------------------------------------- 显示器与调光

        private static int ClampLevel(int value)
        {
            if (value < MinLevel) return MinLevel;
            if (value > MaxLevel) return MaxLevel;
            return value;
        }

        private double Transmission()
        {
            if (!maskVisible) return 1.0;
            return (100.0 - level) / 100.0;
        }

        private void RebuildDisplays()
        {
            Gamma.ReleaseDcs();   // 显示器变了，缓存的 DC 作废
            Dictionary<string, bool> hdr = DisplayConfig.GetHdrStateByGdiName();
            List<DisplayInfo> fresh = new List<DisplayInfo>();
            hdrWasOn = false;

            foreach (Screen screen in Screen.AllScreens)
            {
                DisplayInfo info = new DisplayInfo();
                info.DeviceName = screen.DeviceName;
                info.Bounds = screen.Bounds;
                info.CurrentRamp = new ushort[768];

                bool hdrOn;
                info.HdrOn = hdr.TryGetValue(screen.DeviceName, out hdrOn) && hdrOn;
                if (info.HdrOn) hdrWasOn = true;

                info.OriginalRamp = LoadRampFile(info.DeviceName);
                if (info.OriginalRamp != null)
                {
                    // 上次是被强杀/崩溃退出的：先把屏幕恢复原样，再重新开始
                    Gamma.Write(info.DeviceName, info.OriginalRamp);
                }
                else
                {
                    info.OriginalRamp = Gamma.Read(info.DeviceName);
                    if (info.OriginalRamp != null) SaveRampFile(info.DeviceName, info.OriginalRamp);
                }

                info.GammaOk = info.OriginalRamp != null && Gamma.Same(info.OriginalRamp, Gamma.Read(info.DeviceName), 400);
                if (info.HdrOn) info.GammaOk = false;   // HDR 下系统会忽略 gamma

                fresh.Add(info);
            }

            displays.Clear();
            displays.AddRange(fresh);

            // 丢掉已经不存在的显示器上的覆盖窗
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, OverlayForm> pair in overlays)
            {
                bool alive = false;
                foreach (DisplayInfo d in displays)
                {
                    if (string.Equals(d.DeviceName, pair.Key, StringComparison.OrdinalIgnoreCase)) { alive = true; break; }
                }
                if (!alive) stale.Add(pair.Key);
            }
            foreach (string name in stale)
            {
                OverlayForm overlay = overlays[name];
                overlays.Remove(name);
                try { overlay.Hide(); overlay.Dispose(); } catch { }
            }
        }

        private bool ScreensChanged()
        {
            Screen[] screens = Screen.AllScreens;
            if (screens.Length != displays.Count) return true;
            for (int i = 0; i < screens.Length; i++)
            {
                bool found = false;
                foreach (DisplayInfo d in displays)
                {
                    if (string.Equals(d.DeviceName, screens[i].DeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        found = d.Bounds == screens[i].Bounds;
                        break;
                    }
                }
                if (!found) return true;
            }
            return false;
        }

        /// <summary>按当前浓度把两种机制同步成一致的状态。</summary>
        private void ApplyAll()
        {
            if (ScreensChanged()) RebuildDisplays();

            double transmission = Transmission();

            foreach (DisplayInfo d in displays)
            {
                bool useOverlay = forceOverlay || !d.GammaOk;

                if (useOverlay)
                {
                    if (d.TargetRamp != null)
                    {
                        Gamma.Write(d.DeviceName, d.OriginalRamp);   // 避免和覆盖窗双重压暗
                        d.TargetRamp = null;
                    }
                    SetOverlay(d, 1.0 - transmission);
                }
                else
                {
                    ushort[] target = Gamma.Build(d.OriginalRamp, transmission);
                    d.TargetRamp = target;
                    Gamma.Write(d.DeviceName, target);
                    SetOverlay(d, 0.0);
                }
            }

            UpdatePollInterval();
            UpdateStatus();
        }

        private void SetOverlay(DisplayInfo d, double opacity)
        {
            OverlayForm overlay;
            overlays.TryGetValue(d.DeviceName, out overlay);

            bool needed = opacity > 0.0005 && maskVisible;

            if (!needed)
            {
                if (overlay != null) overlay.Hide();
                return;
            }

            if (overlay == null)
            {
                Rectangle bounds = d.Bounds;
                bounds.Inflate(2, 2);
                overlay = new OverlayForm(bounds, opacity, clickThrough);
                overlays[d.DeviceName] = overlay;
                overlay.Show();
                overlay.SetOpacity(opacity);
            }
            else
            {
                overlay.SetClickThrough(clickThrough);
                if (!overlay.Visible) overlay.Show();
                overlay.SetOpacity(opacity);
                overlay.ReassertTopMost();
            }
        }

        private void SetLevel(int value)
        {
            level = ClampLevel(value);
            ApplyAll();

            if (slider != null && slider.Value != level)
            {
                suppressSlider = true;
                slider.Value = level;
                suppressSlider = false;
            }
            UpdateLevelText();
            SaveSettingsSoon();
        }

        private void UpdateLevelText()
        {
            if (levelLabel != null) levelLabel.Text = "变暗程度：" + level + "%（数值越大越暗）";
        }

        private void ToggleMask()
        {
            maskVisible = !maskVisible;
            ApplyAll();
            if (toggleButton != null) toggleButton.Text = maskVisible ? "隐藏遮罩" : "显示遮罩";
        }

        private void UpdateStatus()
        {
            if (statusLabel == null) return;

            StringBuilder sb = new StringBuilder();
            sb.Append(BuildHotkeySummary());

            bool anyOverlay = overlays.Count > 0;
            bool anyHdr = false;
            foreach (DisplayInfo d in displays) { if (d.HdrOn) anyHdr = true; }

            if (anyOverlay)
            {
                sb.Append("\r\n调光方式：黑色覆盖窗");
                if (forceOverlay) sb.Append("（你手动启用的兼容模式）");
                else if (anyHdr) sb.Append("（检测到 HDR / 高级颜色，系统会忽略驱动调光）");
                sb.Append("。任务视图、切换虚拟桌面时可能短暂漏出。");
            }
            else
            {
                sb.Append("\r\n调光方式：驱动级调光，任务视图 / 切换虚拟桌面 / 开始菜单同样变暗，无闪动。");
            }

            if (!maskVisible) sb.Append("\r\n遮罩当前已隐藏。");
            if (!clickThrough && anyOverlay) sb.Append("\r\n注意：鼠标穿透已关闭，覆盖窗会挡住点击，请用快捷键恢复。");
            if (minimizeToTray) sb.Append("\r\n最小化后收进右下角托盘，调光继续生效。");
            if (HasHotkeyFailure) sb.Append("\r\n有快捷键被别的程序占用，已在菜单里标出可用组合；可点“修改快捷键…”换一个。");

            string text = sb.ToString();
            if (text == lastStatusText) return;
            lastStatusText = text;

            statusLabel.Text = text;
            statusLabel.ForeColor = (!clickThrough && anyOverlay) || HasHotkeyFailure ? Color.Firebrick : Color.DimGray;

            EnsureContentFits();
        }

        private bool HasHotkeyFailure
        {
            get
            {
                foreach (HotkeyBinding b in bindings)
                {
                    if (b.Vk == 0) return true;
                }
                return false;
            }
        }

        private string BuildHotkeySummary()
        {
            StringBuilder sb = new StringBuilder();
            foreach (HotkeyBinding b in bindings)
            {
                if (sb.Length > 0) sb.Append(" · ");
                sb.Append(b.Display + " " + b.Name);
            }
            return sb.ToString();
        }

        /// <summary>状态文字变长时把窗口撑高（只涨不缩，不覆盖用户调过的尺寸）。</summary>
        private void EnsureContentFits()
        {
            if (!Visible || WindowState != FormWindowState.Normal || content == null) return;

            int needed = content.Height + S(30);
            if (needed <= ClientSize.Height) return;

            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            int maxHeight = (int)(work.Height * 0.88);
            if (needed > maxHeight) needed = maxHeight;

            ClientSize = new Size(ClientSize.Width, needed);
        }

        // ------------------------------------------------------------ 事件

        private void ThroughChanged(object sender, EventArgs e)
        {
            bool on = throughBox.Checked;
            if (!on && overlays.Count > 0)
            {
                HotkeyBinding toggle = FindBinding(HotkeyToggle);
                HotkeyBinding quit = FindBinding(HotkeyQuit);
                DialogResult answer = MessageBox.Show(this,
                    "关闭鼠标穿透后，黑色覆盖窗会挡住鼠标点击。\r\n" +
                    "你仍然可以用全局快捷键恢复：\r\n" +
                    "    " + (toggle == null ? "" : toggle.Display) + "  显示 / 隐藏遮罩\r\n" +
                    "    " + (quit == null ? "" : quit.Display) + "  退出程序\r\n\r\n" +
                    "确定要关闭鼠标穿透吗？",
                    "请确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

                if (answer != DialogResult.Yes)
                {
                    throughBox.Checked = true;
                    return;
                }
            }

            clickThrough = on;
            ApplyAll();
        }

        private void OverlayModeChanged(object sender, EventArgs e)
        {
            forceOverlay = overlayBox.Checked;
            ApplyAll();
        }

        private void TrayModeChanged(object sender, EventArgs e)
        {
            minimizeToTray = trayBox.Checked;
            SaveSettingsSoon();
            UpdateStatus();
        }

        private void AutoStartChanged(object sender, EventArgs e)
        {
            bool on = autoStartBox.Checked;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null) throw new InvalidOperationException("无法打开注册表启动项");
                    if (on) key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    else if (key.GetValue(RunValueName) != null) key.DeleteValue(RunValueName, false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "设置开机启动失败：" + ex.Message, "DeepDark",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                autoStartBox.Checked = !on;
            }
        }

        private void EditHotkeys()
        {
            using (HotkeyForm dialog = new HotkeyForm(this, bindings))
            {
                dialog.ShowDialog(this);
            }
        }

        // ------------------------------------------------------- 快捷键管理

        /// <summary>默认组合键（顺序即回退顺序：被占用时自动往后退）。</summary>
        private static List<HotkeyBinding[]> DefaultCandidates()
        {
            const uint CtrlAlt = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
            const uint CtrlShift = Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT;
            const uint AltShift = Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT;

            List<HotkeyBinding[]> list = new List<HotkeyBinding[]>();
            list.Add(new HotkeyBinding[]
            {
                new HotkeyBinding(HotkeyDarker, "变暗 5%", CtrlAlt, Native.VK_DOWN),
                new HotkeyBinding(HotkeyDarker, "变暗 5%", CtrlShift, Native.VK_DOWN),
                new HotkeyBinding(HotkeyDarker, "变暗 5%", AltShift, Native.VK_DOWN)
            });
            list.Add(new HotkeyBinding[]
            {
                new HotkeyBinding(HotkeyLighter, "变亮 5%", CtrlAlt, Native.VK_UP),
                new HotkeyBinding(HotkeyLighter, "变亮 5%", CtrlShift, Native.VK_UP),
                new HotkeyBinding(HotkeyLighter, "变亮 5%", AltShift, Native.VK_UP)
            });
            list.Add(new HotkeyBinding[]
            {
                new HotkeyBinding(HotkeyToggle, "显示/隐藏遮罩", CtrlAlt, Native.VK_M),
                new HotkeyBinding(HotkeyToggle, "显示/隐藏遮罩", CtrlAlt, Native.VK_H),
                new HotkeyBinding(HotkeyToggle, "显示/隐藏遮罩", CtrlShift, Native.VK_F11)
            });
            list.Add(new HotkeyBinding[]
            {
                new HotkeyBinding(HotkeyShowPanel, "唤出设置面板", CtrlAlt, Native.VK_S),
                new HotkeyBinding(HotkeyShowPanel, "唤出设置面板", CtrlAlt, Native.VK_P),
                new HotkeyBinding(HotkeyShowPanel, "唤出设置面板", CtrlShift, Native.VK_F12)
            });
            list.Add(new HotkeyBinding[]
            {
                new HotkeyBinding(HotkeyQuit, "退出程序", CtrlAlt, Native.VK_Q),
                new HotkeyBinding(HotkeyQuit, "退出程序", CtrlAlt, Native.VK_F12),
                new HotkeyBinding(HotkeyQuit, "退出程序", AltShift, Native.VK_Q)
            });
            return list;
        }

        /// <summary>给对话框用：默认组合键（每组取第一个）。</summary>
        public static List<HotkeyBinding> CreateDefaultBindings()
        {
            List<HotkeyBinding> result = new List<HotkeyBinding>();
            foreach (HotkeyBinding[] group in DefaultCandidates()) result.Add(group[0].Clone());
            return result;
        }

        private void RegisterHotkeys(Settings settings)
        {
            bindings.Clear();
            List<HotkeyBinding[]> groups = DefaultCandidates();
            string[] saved = new string[] { settings.Darker, settings.Lighter, settings.Toggle, settings.Show, settings.Quit };

            for (int g = 0; g < groups.Count; g++)
            {
                HotkeyBinding[] group = groups[g];

                // 用户自定义的排在最前，注册不上再退回默认方案
                List<HotkeyBinding> candidates = new List<HotkeyBinding>();
                uint mods, vk;
                if (saved[g] != null && HotkeyText.TryParse(saved[g], out mods, out vk))
                    candidates.Add(new HotkeyBinding(group[0].Id, group[0].Name, mods, vk));
                candidates.AddRange(group);

                HotkeyBinding registered = null;
                foreach (HotkeyBinding candidate in candidates)
                {
                    if (candidate.Vk != 0 && Native.RegisterHotKey(Handle, candidate.Id, candidate.Mods, candidate.Vk))
                    {
                        registered = candidate;
                        break;
                    }
                }

                bindings.Add(registered ?? new HotkeyBinding(group[0].Id, group[0].Name, group[0].Mods, 0));
            }
        }

        /// <summary>对话框点确定时调用：全部重新注册，失败则回滚到原来的组合。</summary>
        public bool TryApplyBindings(List<HotkeyBinding> candidate, out string failedName)
        {
            failedName = null;
            List<HotkeyBinding> backup = new List<HotkeyBinding>();
            foreach (HotkeyBinding b in bindings) backup.Add(b.Clone());

            UnregisterHotkeys();

            List<HotkeyBinding> applied = new List<HotkeyBinding>();
            foreach (HotkeyBinding b in candidate)
            {
                if (!Native.RegisterHotKey(Handle, b.Id, b.Mods, b.Vk))
                {
                    failedName = b.Display;
                    foreach (HotkeyBinding done in applied) Native.UnregisterHotKey(Handle, done.Id);

                    // 回滚
                    bindings.Clear();
                    bindings.AddRange(backup);
                    foreach (HotkeyBinding b2 in bindings)
                    {
                        if (b2.Vk != 0) Native.RegisterHotKey(Handle, b2.Id, b2.Mods, b2.Vk);
                    }
                    UpdateStatus();
                    return false;
                }
                applied.Add(b);
            }

            bindings.Clear();
            foreach (HotkeyBinding b in candidate) bindings.Add(b.Clone());

            foreach (Control c in Controls) RefreshHotkeyTexts(c);
            SaveSettingsSoon();
            UpdateStatus();
            return true;
        }

        private void RefreshHotkeyTexts(Control parent)
        {
            // 面板上唯一带快捷键文字的是状态标签，这里直接整体刷新
            lastStatusText = null;
            UpdateStatus();
        }

        private void UnregisterHotkeys()
        {
            foreach (HotkeyBinding b in bindings)
            {
                if (b.Vk != 0)
                {
                    try { Native.UnregisterHotKey(Handle, b.Id); } catch { }
                }
            }
        }

        protected override void WndProc(ref Message m)
        {
            if ((uint)m.Msg == ShowPanelMessage)
            {
                BeginInvoke((MethodInvoker)delegate { ShowPanel(); });
                return;
            }

            // --restore：先优雅退出，退出流程会把 gamma 还原
            if ((uint)m.Msg == QuitForRestoreMessage)
            {
                BeginInvoke((MethodInvoker)delegate { Close(); });
                return;
            }

            switch (m.Msg)
            {
                case Native.WM_HOTKEY:
                    int id = m.WParam.ToInt32();
                    if (id == HotkeyDarker) SetLevel(level + 5);
                    else if (id == HotkeyLighter) SetLevel(level - 5);
                    else if (id == HotkeyToggle) ToggleMask();
                    else if (id == HotkeyShowPanel) ShowPanel();
                    else if (id == HotkeyQuit) Close();
                    break;

                // 显示器插拔/分辨率变化、系统设置变化（含 HDR）、DWM 重开、睡眠唤醒
                case Native.WM_DISPLAYCHANGE:
                case Native.WM_SETTINGCHANGE:
                case Native.WM_DWMCOMPOSITIONCHANGED:
                    ScheduleReapply();
                    break;

                case Native.WM_POWERBROADCAST:
                    int powerEvent = m.WParam.ToInt32();
                    if (powerEvent == Native.PBT_APMRESUMEAUTOMATIC || powerEvent == Native.PBT_APMRESUMESUSPEND)
                        ScheduleReapply();
                    break;
            }
            base.WndProc(ref m);
        }

        private void ScheduleReapply()
        {
            reapply.Stop();
            reapply.Start();
        }

        /// <summary>
        /// 巡检间隔：只有用黑色覆盖窗时才需要每秒抢置顶；
        /// 驱动级调光下系统事件会通知我们，10 秒兜底一次足够（省 CPU、省唤醒）。
        /// </summary>
        private int PollInterval()
        {
            return overlays.Count > 0 ? 1000 : 10000;
        }

        private void UpdatePollInterval()
        {
            int wanted = PollInterval();
            if (watchdog.Interval != wanted) watchdog.Interval = wanted;
        }

        private void WatchdogTick(object sender, EventArgs e)
        {
            tickCount++;

            if (tickCount == 1) Native.TrimMemory();   // 启动稳定后把闲置页还给系统

            // 极便宜：只问显示器个数，不分配、不枚举设备
            if (Native.GetSystemMetrics(Native.SM_CMONITORS) != displays.Count)
            {
                ApplyAll();
                return;
            }

            if (tickCount % 6 == 0)
            {
                if (ScreensChanged() || DisplayConfig.AnyHdrEnabled() != hdrWasOn)
                {
                    RebuildDisplays();
                    ApplyAll();
                    return;
                }
            }

            if (tickCount % 60 == 0) Native.TrimMemory();

            double transmission = Transmission();
            foreach (DisplayInfo d in displays)
            {
                if (d.TargetRamp != null)
                {
                    // 被夜灯 / 游戏 / 驱动重置了就写回来
                    if (!Gamma.ReadInto(d.DeviceName, d.CurrentRamp) ||
                        !Gamma.Same(d.CurrentRamp, d.TargetRamp, 300))
                    {
                        Gamma.Write(d.DeviceName, Gamma.Build(d.OriginalRamp, transmission));
                    }
                }

                OverlayForm overlay;
                if (overlays.TryGetValue(d.DeviceName, out overlay)) overlay.ReassertTopMost();
            }
        }

        public void ShowPanel()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            Native.PushToTopMost(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Native.TrimMemory();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Cleanup();
            base.OnFormClosing(e);
        }

        private void Cleanup()
        {
            if (cleaned) return;
            cleaned = true;

            watchdog.Stop();
            reapply.Stop();
            UnregisterHotkeys();

            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }

            foreach (KeyValuePair<string, OverlayForm> pair in overlays)
            {
                try { pair.Value.Hide(); pair.Value.Dispose(); } catch { }
            }
            overlays.Clear();

            // 关键：退出时把 gamma 还原，否则屏幕会一直偏暗
            foreach (DisplayInfo d in displays)
            {
                if (d.OriginalRamp != null && Gamma.Write(d.DeviceName, d.OriginalRamp))
                    DeleteRampFile(d.DeviceName);
            }
            Gamma.ReleaseDcs();

            SaveSettings(CollectSettings());
        }

        // -------------------------------------------------- 设置 / 自启 / 备份

        private Settings CollectSettings()
        {
            Settings settings = new Settings();
            settings.Level = level;
            settings.MinimizeToTray = minimizeToTray;

            HotkeyBinding darker = FindBinding(HotkeyDarker);
            HotkeyBinding lighter = FindBinding(HotkeyLighter);
            HotkeyBinding toggle = FindBinding(HotkeyToggle);
            HotkeyBinding show = FindBinding(HotkeyShowPanel);
            HotkeyBinding quit = FindBinding(HotkeyQuit);

            if (darker != null && darker.Vk != 0) settings.Darker = darker.Display;
            if (lighter != null && lighter.Vk != 0) settings.Lighter = lighter.Display;
            if (toggle != null && toggle.Vk != 0) settings.Toggle = toggle.Display;
            if (show != null && show.Vk != 0) settings.Show = show.Display;
            if (quit != null && quit.Vk != 0) settings.Quit = quit.Display;
            return settings;
        }

        private long lastSaveTicks;

        private void SaveSettingsSoon()
        {
            long now = DateTime.Now.Ticks;
            if (now - lastSaveTicks < TimeSpan.TicksPerSecond) return;   // 拖动滑块时不要每像素都写盘
            lastSaveTicks = now;
            SaveSettings(CollectSettings());
        }

        private static string SettingsFilePath()
        {
            return Path.Combine(Application.StartupPath, "DeepDark.settings");
        }

        private static string RampFilePath(string deviceName)
        {
            string safe = deviceName.Replace("\\", "").Replace(".", "").Replace("/", "").Replace(":", "");
            return Path.Combine(Application.StartupPath, "DeepDark.ramp." + safe);
        }

        private static ushort[] LoadRampFile(string deviceName)
        {
            try
            {
                string path = RampFilePath(deviceName);
                if (!File.Exists(path)) return null;
                byte[] raw = File.ReadAllBytes(path);
                if (raw.Length != 1536) return null;
                ushort[] ramp = new ushort[768];
                for (int i = 0; i < ramp.Length; i++) ramp[i] = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));
                return ramp;
            }
            catch { return null; }
        }

        private static void SaveRampFile(string deviceName, ushort[] ramp)
        {
            if (ramp == null) return;
            try
            {
                byte[] raw = new byte[1536];
                for (int i = 0; i < ramp.Length; i++)
                {
                    raw[i * 2] = (byte)(ramp[i] & 0xFF);
                    raw[i * 2 + 1] = (byte)(ramp[i] >> 8);
                }
                File.WriteAllBytes(RampFilePath(deviceName), raw);
            }
            catch { }
        }

        private static void DeleteRampFile(string deviceName)
        {
            try
            {
                string path = RampFilePath(deviceName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private static void SaveSettings(Settings settings)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(settings.Level.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("tray=" + (settings.MinimizeToTray ? "1" : "0"));
                if (settings.Darker != null) sb.AppendLine("hk.darker=" + settings.Darker);
                if (settings.Lighter != null) sb.AppendLine("hk.lighter=" + settings.Lighter);
                if (settings.Toggle != null) sb.AppendLine("hk.toggle=" + settings.Toggle);
                if (settings.Show != null) sb.AppendLine("hk.show=" + settings.Show);
                if (settings.Quit != null) sb.AppendLine("hk.quit=" + settings.Quit);
                File.WriteAllText(SettingsFilePath(), sb.ToString(), Encoding.UTF8);
            }
            catch { }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath))
                {
                    if (key != null) key.SetValue(LevelValueName, settings.Level, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    return key.GetValue(RunValueName) != null;
                }
            }
            catch { return false; }
        }

        // -------------------------------------------------------------- 入口

        [STAThread]
        private static void Main(string[] args)
        {
            bool restore = false;
            bool levelGiven = false;
            int initialLevel = DefaultLevel;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--restore", StringComparison.OrdinalIgnoreCase)) restore = true;
                else if (arg.StartsWith("--level=", StringComparison.OrdinalIgnoreCase))
                {
                    int parsed;
                    if (int.TryParse(arg.Substring("--level=".Length), out parsed)) { initialLevel = parsed; levelGiven = true; }
                }
                else if (string.Equals(arg, "--level", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int parsed;
                    if (int.TryParse(args[i + 1], out parsed)) { initialLevel = parsed; levelGiven = true; i++; }
                }
            }

            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (restore)
            {
                // 先让正在运行的实例优雅退出——它退出时会自己把 gamma 还原；
                // 否则它 10 秒后的巡检会把调光重新写回来，看起来就像"恢复没用"。
                Native.PostMessage(Native.HWND_BROADCAST, QuitForRestoreMessage, IntPtr.Zero, IntPtr.Zero);
                System.Threading.Thread.Sleep(1500);

                StringBuilder report = new StringBuilder();
                report.AppendLine("如已有实例在运行，已请求它退出并自行还原屏幕。");
                report.AppendLine();

                foreach (Screen screen in Screen.AllScreens)
                {
                    ushort[] saved = LoadRampFile(screen.DeviceName);
                    ushort[] target = saved != null ? saved : Gamma.Linear();
                    bool ok = Gamma.Write(screen.DeviceName, target);
                    if (ok && saved != null) DeleteRampFile(screen.DeviceName);
                    report.AppendLine(screen.DeviceName + " -> " + (ok
                        ? (saved != null ? "已恢复为调光前的曲线" : "已恢复为线性调光")
                        : "恢复失败（该显示器驱动不支持）"));
                }
                Gamma.ReleaseDcs();
                MessageBox.Show(report.ToString(), "DeepDark：gamma 已重置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            startupSettings = Settings.Load();
            if (levelGiven) startupSettings.Level = initialLevel;

            bool createdNew;
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(true, "DeepDark_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // 已经有一个在跑：广播消息让它的面板弹出来
                    Native.PostMessage(Native.HWND_BROADCAST, ShowPanelMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                Application.Run(new MainForm());
                GC.KeepAlive(mutex);
            }
        }
    }

    /// <summary>DeepDark.settings 的内容。</summary>
    internal sealed class Settings
    {
        public int Level = 45;
        public bool MinimizeToTray = true;
        public string Darker;
        public string Lighter;
        public string Toggle;
        public string Show;
        public string Quit;

        private static string FilePath()
        {
            return Path.Combine(Application.StartupPath, "DeepDark.settings");
        }

        public static Settings Load()
        {
            Settings settings = new Settings();
            bool levelFound = false;

            try
            {
                string path = FilePath();
                if (File.Exists(path))
                {
                    foreach (string rawLine in File.ReadAllLines(path))
                    {
                        string line = rawLine.Trim();
                        if (line.Length == 0) continue;

                        if (line.StartsWith("tray=", StringComparison.OrdinalIgnoreCase))
                            settings.MinimizeToTray = line.Substring(5).Trim() != "0";
                        else if (line.StartsWith("hk.darker=", StringComparison.OrdinalIgnoreCase))
                            settings.Darker = line.Substring(10).Trim();
                        else if (line.StartsWith("hk.lighter=", StringComparison.OrdinalIgnoreCase))
                            settings.Lighter = line.Substring(11).Trim();
                        else if (line.StartsWith("hk.toggle=", StringComparison.OrdinalIgnoreCase))
                            settings.Toggle = line.Substring(10).Trim();
                        else if (line.StartsWith("hk.show=", StringComparison.OrdinalIgnoreCase))
                            settings.Show = line.Substring(8).Trim();
                        else if (line.StartsWith("hk.quit=", StringComparison.OrdinalIgnoreCase))
                            settings.Quit = line.Substring(8).Trim();
                        else if (!levelFound)
                        {
                            int parsed;
                            if (int.TryParse(line, out parsed)) { settings.Level = parsed; levelFound = true; }
                        }
                    }
                }
            }
            catch { }

            if (!levelFound)
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\DeepDark", false))
                    {
                        if (key != null)
                        {
                            object value = key.GetValue("Level");
                            if (value != null) settings.Level = Convert.ToInt32(value);
                        }
                    }
                }
                catch { }
            }

            return settings;
        }
    }
}
