using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    private const int HWND_BROADCAST = 0xFFFF;
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_MONITORPOWER = 0xF170;
    private const int MONITOR_OFF = 2;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_O = 0x4F;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    private const string MutexName = "Local\\TurnOffScreenHotkeySingleton";
    private const string TriggerEventName = "Local\\TurnOffScreenTriggerEvent";
    private const string PingEventName = "Local\\TurnOffScreenPingEvent";

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string lpString);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TurnOffScreen", "config.txt");

    private static int _deepSleepMinutes = 10;
    private static bool _dimHardwareBrightness = true;

    private static void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            foreach (var raw in File.ReadAllLines(ConfigPath))
            {
                var line = raw.Trim();
                int idx = line.IndexOf('=');
                if (idx <= 0) continue;
                string k = line.Substring(0, idx).Trim();
                string v = line.Substring(idx + 1).Trim();
                if (k == "DeepSleepMinutes")
                {
                    int mins;
                    if (int.TryParse(v, out mins) && mins >= 0 && mins <= 1440)
                        _deepSleepMinutes = mins;
                }
                else if (k == "DimHardwareBrightness")
                {
                    bool b;
                    if (bool.TryParse(v, out b))
                        _dimHardwareBrightness = b;
                }
            }
        }
        catch { }
    }

    private static void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllLines(ConfigPath, new[]
            {
                "DeepSleepMinutes=" + _deepSleepMinutes,
                "DimHardwareBrightness=" + _dimHardwareBrightness
            });
        }
        catch { }
    }

    // Handles both external monitors (DDC/CI via dxva2.dll) and built-in laptop panels (WMI)
    private sealed class BrightnessController
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, uint> _savedDdc = new Dictionary<string, uint>();
        private readonly Dictionary<string, byte> _savedWmi = new Dictionary<string, byte>();
        private readonly Dictionary<string, byte> _minWmi = new Dictionary<string, byte>();
        private bool _isDdcDimmed = false;
        private bool _isWmiDimmed = false;

        private static List<Tuple<string, uint, uint>> ReadAllDdcMonitors()
        {
            var list = new List<Tuple<string, uint, uint>>();
            int index = 0;
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
                {
                    uint count = 0;
                    if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) && count > 0)
                    {
                        var arr = new PHYSICAL_MONITOR[count];
                        if (GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
                        {
                            for (int i = 0; i < count; i++)
                            {
                                uint min = 0, cur = 0, max = 0;
                                if (GetMonitorBrightness(arr[i].hPhysicalMonitor, out min, out cur, out max) && max > min)
                                {
                                    string key = index + ":" + arr[i].szPhysicalMonitorDescription;
                                    list.Add(Tuple.Create(key, min, cur));
                                }
                                index++;
                            }
                            DestroyPhysicalMonitors(count, arr);
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return list;
        }

        private static bool ApplyDdcBrightness(Func<string, uint, uint, uint?> targetSelector)
        {
            bool allApplied = true;
            int index = 0;
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
                {
                    uint count = 0;
                    if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) && count > 0)
                    {
                        var arr = new PHYSICAL_MONITOR[count];
                        if (GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
                        {
                            for (int i = 0; i < count; i++)
                            {
                                uint min = 0, cur = 0, max = 0;
                                if (GetMonitorBrightness(arr[i].hPhysicalMonitor, out min, out cur, out max) && max > min)
                                {
                                    string key = index + ":" + arr[i].szPhysicalMonitorDescription;
                                    uint? target = targetSelector(key, min, cur);
                                    if (target.HasValue && target.Value != cur)
                                    {
                                        if (!SetMonitorBrightness(arr[i].hPhysicalMonitor, target.Value))
                                            allApplied = false;
                                    }
                                }
                                index++;
                            }
                            DestroyPhysicalMonitors(count, arr);
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                allApplied = false;
            }
            return allApplied;
        }

        private static List<Tuple<string, byte, byte>> ReadAllWmiPanels()
        {
            var list = new List<Tuple<string, byte, byte>>();
            try
            {
                var scope = new ManagementScope(@"\\.\root\wmi");
                var query = new ObjectQuery("SELECT InstanceName, CurrentBrightness, Level FROM WmiMonitorBrightness");
                using (var searcher = new ManagementObjectSearcher(scope, query))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            string name = obj["InstanceName"] as string;
                            if (string.IsNullOrEmpty(name)) continue;
                            byte cur = Convert.ToByte(obj["CurrentBrightness"]);
                            byte min = 0;
                            byte[] levels = obj["Level"] as byte[];
                            if (levels != null && levels.Length > 0) min = levels[0];
                            list.Add(Tuple.Create(name, min, cur));
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        private static bool ApplyWmiBrightness(Func<string, byte?> targetSelector)
        {
            bool allApplied = true;
            try
            {
                var scope = new ManagementScope(@"\\.\root\wmi");
                var query = new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods");
                using (var searcher = new ManagementObjectSearcher(scope, query))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            string name = obj["InstanceName"] as string;
                            if (string.IsNullOrEmpty(name)) continue;
                            byte? target = targetSelector(name);
                            if (target.HasValue)
                            {
                                try
                                {
                                    obj.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, target.Value });
                                }
                                catch
                                {
                                    allApplied = false;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                allApplied = false;
            }
            return allApplied;
        }

        public void DimToMinimum(Func<bool> shouldAbort)
        {
            lock (_lock)
            {
                if (shouldAbort()) return;

                // 1. External monitors via DDC/CI
                if (!_isDdcDimmed)
                {
                    _savedDdc.Clear();
                    foreach (var item in ReadAllDdcMonitors())
                    {
                        _savedDdc[item.Item1] = item.Item3;
                    }
                }
                if (_savedDdc.Count > 0 && !shouldAbort())
                {
                    _isDdcDimmed = true;
                    ApplyDdcBrightness((key, min, cur) => _savedDdc.ContainsKey(key) ? (uint?)min : null);
                }

                if (shouldAbort()) return;

                // 2. Built-in laptop panels via WMI
                if (!_isWmiDimmed)
                {
                    _savedWmi.Clear();
                    _minWmi.Clear();
                    foreach (var item in ReadAllWmiPanels())
                    {
                        _minWmi[item.Item1] = item.Item2;
                        _savedWmi[item.Item1] = item.Item3;
                    }
                }
                if (_savedWmi.Count > 0 && !shouldAbort())
                {
                    _isWmiDimmed = true;
                    ApplyWmiBrightness(name =>
                    {
                        byte min;
                        return _minWmi.TryGetValue(name, out min) ? (byte?)min : null;
                    });
                }
            }
        }

        public bool RestoreIfDimmed()
        {
            lock (_lock)
            {
                bool ddcOk = true;
                if (_isDdcDimmed && _savedDdc.Count > 0)
                {
                    ddcOk = ApplyDdcBrightness((key, min, cur) =>
                    {
                        uint orig;
                        return _savedDdc.TryGetValue(key, out orig) ? (uint?)orig : null;
                    });
                    if (ddcOk)
                    {
                        _isDdcDimmed = false;
                        _savedDdc.Clear();
                    }
                }

                bool wmiOk = true;
                if (_isWmiDimmed && _savedWmi.Count > 0)
                {
                    wmiOk = ApplyWmiBrightness(name =>
                    {
                        byte orig;
                        return _savedWmi.TryGetValue(name, out orig) ? (byte?)orig : null;
                    });
                    if (wmiOk)
                    {
                        _isWmiDimmed = false;
                        _savedWmi.Clear();
                        _minWmi.Clear();
                    }
                }

                return ddcOk && wmiOk;
            }
        }

        public void RestoreWithRetryAsync(int maxAttempts, int delayMs)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    if (RestoreIfDimmed()) return;
                    Thread.Sleep(delayMs);
                }
            });
        }
    }

    private sealed class BlankForm : Form
    {
        private readonly Action _onUserActivity;
        private static readonly Cursor InvisibleCursor = CreateInvisibleCursor();

        public BlankForm(Rectangle bounds, Action onUserActivity)
        {
            _onUserActivity = onUserActivity;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.Black;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            Cursor = InvisibleCursor;

            KeyDown += (s, e) => _onUserActivity();
            MouseDown += (s, e) => _onUserActivity();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            SetWindowPos(Handle, HWND_TOPMOST, Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, SWP_SHOWWINDOW);
        }

        private static Cursor CreateInvisibleCursor()
        {
            using (var bmp = new Bitmap(1, 1))
            {
                bmp.SetPixel(0, 0, Color.FromArgb(0, 0, 0, 0));
                IntPtr hIcon = bmp.GetHicon();
                return new Cursor(hIcon);
            }
        }
    }

    private sealed class ScreenOffController : IDisposable
    {
        private readonly BrightnessController _brightness = new BrightnessController();
        private readonly List<BlankForm> _overlays = new List<BlankForm>();
        private readonly System.Windows.Forms.Timer _pollTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _deepSleepTimer = new System.Windows.Forms.Timer();

        private bool _isBlankActive = false;
        private bool _armedForWake = false;
        private bool _deepSleepSent = false;
        private bool _waitingForHotkeyRelease = false;
        private int _sessionSeq = 0;
        private long _enteredTicks = 0;
        private long _lastExitTicks = 0;
        private uint _baseInputTime = 0;
        private Point _baseCursorPos = Point.Empty;

        public ScreenOffController()
        {
            _pollTimer.Interval = 40;
            _pollTimer.Tick += OnPollTick;
            _deepSleepTimer.Tick += OnDeepSleepTick;
        }

        private static uint GetLastInputTime()
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            return GetLastInputInfo(ref lii) ? lii.dwTime : 0;
        }

        private static bool IsHotkeyHeld()
        {
            return (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 ||
                   (GetAsyncKeyState((int)VK_O) & 0x8000) != 0;
        }

        public void EnterSoftBlackout(bool fromHotkey)
        {
            if (_isBlankActive) return;
            if (Math.Abs(Environment.TickCount - _lastExitTicks) < 400) return;

            LoadConfig();

            _isBlankActive = true;
            _armedForWake = false;
            _deepSleepSent = false;
            _waitingForHotkeyRelease = fromHotkey;
            _enteredTicks = Environment.TickCount;
            int seq = Interlocked.Increment(ref _sessionSeq);

            foreach (var scr in Screen.AllScreens)
            {
                var form = new BlankForm(scr.Bounds, OnDirectOverlayActivity);
                _overlays.Add(form);
                form.Show();
            }

            if (_overlays.Count > 0)
            {
                _overlays[0].Activate();
            }

            if (_dimHardwareBrightness)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    _brightness.DimToMinimum(() => Volatile.Read(ref _sessionSeq) != seq || !_isBlankActive);
                });
            }

            _pollTimer.Start();

            if (_deepSleepMinutes > 0)
            {
                _deepSleepTimer.Interval = _deepSleepMinutes * 60 * 1000;
                _deepSleepTimer.Start();
            }
        }

        private void OnDirectOverlayActivity()
        {
            if (_isBlankActive && _armedForWake)
            {
                ExitSoftBlackout();
            }
        }

        private void OnPollTick(object sender, EventArgs e)
        {
            if (!_isBlankActive)
            {
                _pollTimer.Stop();
                return;
            }

            long elapsed = Math.Abs(Environment.TickCount - _enteredTicks);

            if (!_armedForWake)
            {
                if (_waitingForHotkeyRelease)
                {
                    if (IsHotkeyHeld() && elapsed < 2500) return;
                    if (elapsed < 220) return;
                }
                else
                {
                    if (elapsed < 380) return;
                }

                _baseInputTime = GetLastInputTime();
                _baseCursorPos = Cursor.Position;
                _armedForWake = true;
                return;
            }

            uint nowInput = GetLastInputTime();
            Point pos = Cursor.Position;
            int dx = Math.Abs(pos.X - _baseCursorPos.X);
            int dy = Math.Abs(pos.Y - _baseCursorPos.Y);

            if (nowInput != _baseInputTime || dx > 8 || dy > 8)
            {
                ExitSoftBlackout();
            }
        }

        private void OnDeepSleepTick(object sender, EventArgs e)
        {
            _deepSleepTimer.Stop();
            if (!_isBlankActive || _deepSleepSent) return;

            _deepSleepSent = true;
            int seq = Volatile.Read(ref _sessionSeq);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (Volatile.Read(ref _sessionSeq) != seq) return;
                _brightness.RestoreIfDimmed();
                Thread.Sleep(150);
                if (Volatile.Read(ref _sessionSeq) != seq) return;
                SendHardwareMonitorOff();
            });
        }

        public void ExitSoftBlackout()
        {
            if (!_isBlankActive) return;

            _isBlankActive = false;
            _armedForWake = false;
            _pollTimer.Stop();
            _deepSleepTimer.Stop();
            Interlocked.Increment(ref _sessionSeq);
            _lastExitTicks = Environment.TickCount;

            foreach (var form in _overlays)
            {
                try { form.Close(); form.Dispose(); } catch { }
            }
            _overlays.Clear();

            _brightness.RestoreWithRetryAsync(_deepSleepSent ? 20 : 4, _deepSleepSent ? 600 : 120);
            _deepSleepSent = false;
        }

        public void TriggerImmediateDeepSleep()
        {
            if (_isBlankActive)
            {
                ExitSoftBlackout();
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                _brightness.RestoreIfDimmed();
                Thread.Sleep(350);
                SendHardwareMonitorOff();
            });
        }

        private static void SendHardwareMonitorOff()
        {
            IntPtr result;
            SendMessageTimeout(
                (IntPtr)HWND_BROADCAST,
                WM_SYSCOMMAND,
                (IntPtr)SC_MONITORPOWER,
                (IntPtr)MONITOR_OFF,
                SMTO_ABORTIFHUNG,
                1000,
                out result);
        }

        public void Dispose()
        {
            ExitSoftBlackout();
            _brightness.RestoreIfDimmed();
            _pollTimer.Dispose();
            _deepSleepTimer.Dispose();
        }
    }

    private static int? PromptCustomMinutes(int currentMinutes)
    {
        using (var dlg = new Form())
        {
            dlg.Text = "自定义自动深度关屏时间";
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MaximizeBox = false;
            dlg.MinimizeBox = false;
            dlg.StartPosition = FormStartPosition.CenterScreen;
            dlg.TopMost = true;
            dlg.AutoScaleMode = AutoScaleMode.Font;
            dlg.ClientSize = new Size(340, 130);

            var lbl = new Label
            {
                Text = "黑屏后多少分钟自动深度关屏（1～1440，0 为关闭）：",
                Location = new Point(16, 16),
                AutoSize = true
            };

            var num = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 1440,
                Value = Math.Max(0, Math.Min(1440, currentMinutes)),
                Location = new Point(16, 46),
                Width = 140
            };

            var unitLbl = new Label
            {
                Text = "分钟",
                Location = new Point(164, 49),
                AutoSize = true
            };

            var okBtn = new Button
            {
                Text = "确定",
                DialogResult = DialogResult.OK,
                Location = new Point(160, 88),
                Width = 75
            };

            var cancelBtn = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(245, 88),
                Width = 75
            };

            dlg.Controls.AddRange(new Control[] { lbl, num, unitLbl, okBtn, cancelBtn });
            dlg.AcceptButton = okBtn;
            dlg.CancelButton = cancelBtn;

            if (dlg.ShowDialog() == DialogResult.OK)
            {
                return (int)num.Value;
            }
            return null;
        }
    }

    private static Icon CreatePowerIcon()
    {
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var bg = new SolidBrush(Color.FromArgb(224, 62, 54)))
            {
                g.FillEllipse(bg, 1, 1, 30, 30);
            }

            using (var pen = new Pen(Color.White, 3.0f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, 8, 8, 16, 16, -55, 290);
                g.DrawLine(pen, 16, 6, 16, 15);
            }

            IntPtr hIcon = bmp.GetHicon();
            try
            {
                return (Icon)Icon.FromHandle(hIcon).Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
    }

    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        public const int HotkeyId = 1;
        public bool Registered { get; private set; }
        private readonly Action _onHotkey;
        private readonly Action _onTaskbarCreated;
        private readonly uint _taskbarCreatedMsg;

        public HotkeyWindow(Action onHotkey, Action onTaskbarCreated)
        {
            _onHotkey = onHotkey;
            _onTaskbarCreated = onTaskbarCreated;
            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
            CreateHandle(new CreateParams());
            Registered = RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_O);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                _onHotkey();
            }
            else if (_taskbarCreatedMsg != 0 && (uint)m.Msg == _taskbarCreatedMsg)
            {
                _onTaskbarCreated();
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Registered)
            {
                UnregisterHotKey(Handle, HotkeyId);
                Registered = false;
            }
            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }
    }

    private static void EnablePerMonitorDpi()
    {
        try
        {
            if (!SetProcessDpiAwarenessContext(new IntPtr(-4)))
                SetProcessDPIAware();
        }
        catch
        {
            try { SetProcessDPIAware(); } catch { }
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        EnablePerMonitorDpi();

        bool triggerOnLaunch = args.Length > 0 &&
            (string.Equals(args[0], "--trigger", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(args[0], "--now", StringComparison.OrdinalIgnoreCase));

        bool createdNew;
        using (var mutex = new Mutex(true, MutexName, out createdNew))
        {
            if (!createdNew)
            {
                try
                {
                    using (var ev = EventWaitHandle.OpenExisting(triggerOnLaunch ? TriggerEventName : PingEventName))
                    {
                        ev.Set();
                    }
                }
                catch { }
                return;
            }

            LoadConfig();
            Application.EnableVisualStyles();

            using (var triggerEvent = new EventWaitHandle(false, EventResetMode.AutoReset, TriggerEventName))
            using (var pingEvent = new EventWaitHandle(false, EventResetMode.AutoReset, PingEventName))
            using (var controller = new ScreenOffController())
            using (var uiContext = new Control())
            using (var appIcon = CreatePowerIcon())
            using (var tray = new NotifyIcon())
            using (var menu = new ContextMenuStrip())
            using (var hotkeyWin = new HotkeyWindow(
                () => controller.EnterSoftBlackout(true),
                () => { tray.Visible = false; tray.Visible = true; }))
            {
                var _ = uiContext.Handle;

                Action updateTrayText = () =>
                {
                    string delayDesc = _deepSleepMinutes > 0
                        ? (_deepSleepMinutes + "分钟后深度关屏")
                        : "仅极速黑屏";
                    string txt = "一键关屏 (Ctrl+Alt+O) - " + delayDesc;
                    tray.Text = txt.Length > 63 ? txt.Substring(0, 63) : txt;
                };

                menu.Items.Add("极速黑屏 / 秒醒 (Ctrl+Alt+O)", null, (s, e) => controller.EnterSoftBlackout(false));
                menu.Items.Add("立即深度关屏 (真断电)", null, (s, e) => controller.TriggerImmediateDeepSleep());
                menu.Items.Add(new ToolStripSeparator());

                var delayMenu = new ToolStripMenuItem("自动转深度关屏时间");
                int[] presets = { 5, 10, 15, 30, 60, 120, 180, 0 };
                var presetItems = new Dictionary<int, ToolStripMenuItem>();

                var customActiveItem = new ToolStripMenuItem { Visible = false, Checked = true, Enabled = false };
                var dimItem = new ToolStripMenuItem("黑屏时自动压低显示器背光亮度");

                Action syncUiFromConfig = () =>
                {
                    bool matchedPreset = false;
                    foreach (var kv in presetItems)
                    {
                        bool isMatch = (kv.Key == _deepSleepMinutes);
                        kv.Value.Checked = isMatch;
                        if (isMatch) matchedPreset = true;
                    }

                    if (!matchedPreset && _deepSleepMinutes > 0)
                    {
                        customActiveItem.Text = "当前自定义：" + _deepSleepMinutes + " 分钟后";
                        customActiveItem.Checked = true;
                        customActiveItem.Visible = true;
                    }
                    else
                    {
                        customActiveItem.Visible = false;
                    }

                    dimItem.Checked = _dimHardwareBrightness;
                    updateTrayText();
                };

                foreach (int mins in presets)
                {
                    int captured = mins;
                    string label;
                    if (mins == 0) label = "关闭（仅极速黑屏）";
                    else if (mins >= 60 && mins % 60 == 0) label = mins + " 分钟后（" + (mins / 60) + " 小时）";
                    else label = mins + " 分钟后";

                    var item = new ToolStripMenuItem(label);
                    item.Click += (s, e) =>
                    {
                        _deepSleepMinutes = captured;
                        SaveConfig();
                        syncUiFromConfig();
                    };
                    presetItems[captured] = item;
                    delayMenu.DropDownItems.Add(item);
                }

                delayMenu.DropDownItems.Add(customActiveItem);
                delayMenu.DropDownItems.Add(new ToolStripSeparator());
                var customPromptItem = new ToolStripMenuItem("自定义分钟数...");
                customPromptItem.Click += (s, e) =>
                {
                    int? chosen = PromptCustomMinutes(_deepSleepMinutes);
                    if (chosen.HasValue)
                    {
                        _deepSleepMinutes = chosen.Value;
                        SaveConfig();
                        syncUiFromConfig();
                    }
                };
                delayMenu.DropDownItems.Add(customPromptItem);
                menu.Items.Add(delayMenu);

                dimItem.Click += (s, e) =>
                {
                    _dimHardwareBrightness = !_dimHardwareBrightness;
                    SaveConfig();
                    syncUiFromConfig();
                };
                menu.Items.Add(dimItem);

                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("退出", null, (s, e) => Application.Exit());

                menu.Opening += (s, e) =>
                {
                    LoadConfig();
                    syncUiFromConfig();
                };

                // Set Icon, ContextMenuStrip, and Text BEFORE making tray icon visible
                tray.Icon = appIcon;
                tray.ContextMenuStrip = menu;
                syncUiFromConfig();
                tray.MouseClick += (s, e) =>
                {
                    if (e.Button == MouseButtons.Left)
                    {
                        controller.EnterSoftBlackout(false);
                    }
                };
                tray.Visible = true;

                var stopSignal = new ManualResetEvent(false);
                var listenerThread = new Thread(() =>
                {
                    WaitHandle[] handles = { stopSignal, triggerEvent, pingEvent };
                    while (true)
                    {
                        int signaled = WaitHandle.WaitAny(handles);
                        if (signaled == 0) break;
                        if (signaled == 1)
                        {
                            try { uiContext.BeginInvoke(new Action(() => controller.EnterSoftBlackout(false))); }
                            catch { break; }
                        }
                        else if (signaled == 2)
                        {
                            try
                            {
                                uiContext.BeginInvoke(new Action(() =>
                                {
                                    LoadConfig();
                                    syncUiFromConfig();
                                    tray.Visible = false;
                                    tray.Visible = true;
                                    string status = _deepSleepMinutes > 0
                                        ? ("当前设置：" + _deepSleepMinutes + " 分钟后自动深度关屏")
                                        : "当前设置：仅极速黑屏";
                                    tray.ShowBalloonTip(2500, "一键关屏已在运行 (Ctrl+Alt+O)", status, ToolTipIcon.Info);
                                }));
                            }
                            catch { break; }
                        }
                    }
                }) { IsBackground = true };
                listenerThread.Start();

                if (triggerOnLaunch)
                {
                    uiContext.BeginInvoke(new Action(() => controller.EnterSoftBlackout(false)));
                }

                Application.Run();
                stopSignal.Set();
                tray.Visible = false;
            }
        }
    }
}
