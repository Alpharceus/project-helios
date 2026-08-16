// TempMonitor - lightweight always-on-top CPU/GPU temperature and fan overlay.
// Uses LibreHardwareMonitorLib (MPL-2.0) for sensor access. Requires admin
// rights (kernel driver) to read CPU temperature sensors.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

namespace TempMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            Mutex mutex = new Mutex(true, "Local\\TempMonitor_SingleInstance", out createdNew);
            if (!createdNew)
                return;
            GC.KeepAlive(mutex);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new OverlayForm());
        }
    }

    // ---------------------------------------------------------------- config
    internal sealed class Config
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly string _path;

        public Config()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "config.txt");
            try
            {
                if (File.Exists(_path))
                {
                    foreach (string line in File.ReadAllLines(_path))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0)
                            _values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in _values)
                    sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(_path, sb.ToString());
            }
            catch { }
        }

        public int GetInt(string key, int fallback)
        {
            string s;
            int v;
            if (_values.TryGetValue(key, out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                return v;
            return fallback;
        }

        public string GetString(string key, string fallback)
        {
            string s;
            return _values.TryGetValue(key, out s) ? s : fallback;
        }

        public void Set(string key, object value)
        {
            _values[key] = Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    // ------------------------------------------------------------- snapshot
    internal sealed class Snapshot
    {
        public float? CpuTemp;
        public float? GpuTemp;
        public float? CpuLoad;   // percent
        public float? GpuLoad;   // percent
        public float? RamLoad;   // percent
        public float? RamUsedGb;
        public List<KeyValuePair<string, int>> Fans = new List<KeyValuePair<string, int>>();
        public string Error;
    }

    // ---------------------------------------------------------- sensor loop
    internal sealed class SensorReader : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        public Action<Snapshot> OnSnapshot; // invoked on reader thread

        // The Nuvoton NCT6701D returns 0 rpm for every fan after an LHM
        // instance has been open for a few seconds (library bug, a fresh
        // instance always reads correctly). Workaround: motherboard fans are
        // read through a short-lived instance recreated on a slower cadence.
        private volatile List<KeyValuePair<string, int>> _mbFans = new List<KeyValuePair<string, int>>();

        public SensorReader()
        {
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.BelowNormal;
        }

        // Started explicitly after OnSnapshot is wired up, so an init error
        // raised in the first milliseconds isn't reported into a null handler
        // and silently lost.
        public void Start()
        {
            _thread.Start();
        }

        private void Run()
        {
            try { RunCore(); }
            catch (Exception ex)
            {
                LogError(ex);
                Snapshot err = new Snapshot();
                err.Error = "Sensor thread died: " + ex.Message;
                Report(err);
            }
        }

        private void RunCore()
        {
            Computer computer = null;
            try
            {
                WriteMarker("initializing sensors " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                computer = new Computer();
                computer.IsCpuEnabled = true;
                computer.IsGpuEnabled = true;
                computer.IsMemoryEnabled = true;
                computer.IsControllerEnabled = true;
                computer.Open();
            }
            catch (Exception ex)
            {
                LogError(ex);
                Snapshot err = new Snapshot();
                err.Error = "Sensor init failed: " + ex.Message;
                Report(err);
                return;
            }

            int cycle = 0;
            while (!_stop)
            {
                if (cycle % 5 == 0) // every 10 s
                    RefreshMotherboardFans();

                Snapshot snap = new Snapshot();
                try
                {
                    snap.Fans.AddRange(_mbFans);
                    Read(computer, snap);
                }
                catch (Exception ex)
                {
                    snap.Error = ex.Message;
                }
                Report(snap);

                if (cycle % 15 == 0)
                    Dump(computer, snap);
                cycle++;

                for (int i = 0; i < 20 && !_stop; i++)
                    Thread.Sleep(100);
            }

            try { computer.Close(); }
            catch { }
        }

        // Overwrites sensors.txt with a one-line stage marker so a hang during
        // library init is distinguishable from a throw (error.txt) after the fact.
        private static void WriteMarker(string text)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor");
                File.WriteAllText(Path.Combine(dir, "sensors.txt"), text + Environment.NewLine);
            }
            catch { }
        }

        private static void LogError(Exception ex)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor");
                File.AppendAllText(Path.Combine(dir, "error.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + Environment.NewLine);
            }
            catch { }
        }

        private void Report(Snapshot snap)
        {
            Action<Snapshot> h = OnSnapshot;
            if (h != null)
                h(snap);
        }

        private static void Read(Computer computer, Snapshot snap)
        {
            IHardware bestGpu = null;
            int bestScore = -1;

            foreach (IHardware hw in computer.Hardware)
            {
                hw.Update();
                foreach (IHardware sub in hw.SubHardware)
                    sub.Update();

                if (hw.HardwareType == HardwareType.Cpu)
                {
                    snap.CpuTemp = PickTemp(hw, snap.CpuTemp,
                        new string[] { "Core (Tctl/Tdie)", "CPU Package", "Package", "Core Average" });
                    snap.CpuLoad = PickSensor(hw, SensorType.Load, "CPU Total", snap.CpuLoad);
                }

                if (hw.HardwareType == HardwareType.Memory)
                {
                    snap.RamLoad = PickSensor(hw, SensorType.Load, "Memory", snap.RamLoad);
                    snap.RamUsedGb = PickSensor(hw, SensorType.Data, "Memory Used", snap.RamUsedGb);
                }

                bool isGpu = hw.HardwareType == HardwareType.GpuNvidia
                          || hw.HardwareType == HardwareType.GpuAmd
                          || hw.HardwareType == HardwareType.GpuIntel;
                if (isGpu)
                {
                    // Prefer the discrete card over an integrated GPU: a "GPU Core"
                    // temperature sensor and physical fans are strong indicators.
                    int score = 0;
                    foreach (ISensor s in hw.Sensors)
                    {
                        if (s.SensorType == SensorType.Temperature && s.Value.HasValue &&
                            string.Equals(s.Name, "GPU Core", StringComparison.OrdinalIgnoreCase))
                            score |= 2;
                        if (s.SensorType == SensorType.Fan)
                            score |= 1;
                    }
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestGpu = hw;
                    }
                }
                else
                {
                    CollectFans(hw, snap, "", false);
                    foreach (IHardware sub in hw.SubHardware)
                        CollectFans(sub, snap, "", false);
                }
            }

            if (bestGpu != null)
            {
                snap.GpuTemp = PickTemp(bestGpu, null, new string[] { "GPU Core" });
                snap.GpuLoad = PickSensor(bestGpu, SensorType.Load, "GPU Core", null);
                CollectFans(bestGpu, snap, "", true); // sensor names already start with "GPU"
            }
        }

        private static float? PickSensor(IHardware hw, SensorType type, string name, float? current)
        {
            foreach (ISensor s in hw.Sensors)
            {
                if (s.SensorType == type && s.Value.HasValue &&
                    string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    return s.Value.Value;
            }
            return current;
        }

        private void RefreshMotherboardFans()
        {
            Computer mini = null;
            try
            {
                mini = new Computer();
                mini.IsMotherboardEnabled = true;
                mini.Open();
                List<KeyValuePair<string, int>> fans = new List<KeyValuePair<string, int>>();
                foreach (IHardware hw in mini.Hardware)
                {
                    hw.Update();
                    CollectFansInto(hw, fans);
                    foreach (IHardware sub in hw.SubHardware)
                    {
                        sub.Update();
                        CollectFansInto(sub, fans);
                    }
                }
                _mbFans = fans;
            }
            catch { }
            // Deliberately NOT calling mini.Close(): the library's kernel-driver
            // layer is a process-wide singleton and Close() tears it down for the
            // long-lived main instance too (CPU reads start throwing). Abandoned
            // instances hold no unmanaged per-instance state and are GC'd.
            GC.KeepAlive(mini);
        }

        private static void CollectFansInto(IHardware hw, List<KeyValuePair<string, int>> fans)
        {
            foreach (ISensor s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Fan || !s.Value.HasValue)
                    continue;
                int rpm = (int)s.Value.Value;
                if (rpm > 0 && fans.Count < 10)
                    fans.Add(new KeyValuePair<string, int>(FriendlyFanName(s.Name), rpm));
            }
        }

        // Header names for this board (ASUS X870 MAX GAMING WIFI7): the Nuvoton
        // chip only reports generic indices; #7 was identified as the AIO pump
        // by its rpm range. Adjust here if fans get replugged.
        private static string FriendlyFanName(string name)
        {
            if (name == "Fan #7")
                return "AIO Pump";
            return name.Replace(" #", " ");
        }

        private static float? PickTemp(IHardware hw, float? current, string[] preferredNames)
        {
            float? preferred = null;
            float? max = null;
            foreach (ISensor s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Temperature || !s.Value.HasValue)
                    continue;
                float v = s.Value.Value;
                if (v <= 0f || v >= 128f)
                    continue;
                if (!max.HasValue || v > max.Value)
                    max = v;
                for (int i = 0; i < preferredNames.Length; i++)
                {
                    if (string.Equals(s.Name, preferredNames[i], StringComparison.OrdinalIgnoreCase))
                    {
                        if (!preferred.HasValue || v > preferred.Value)
                            preferred = v;
                    }
                }
            }
            float? result = preferred.HasValue ? preferred : max;
            if (!result.HasValue)
                return current;
            if (current.HasValue && current.Value > result.Value)
                return current;
            return result;
        }

        private static void CollectFans(IHardware hw, Snapshot snap, string prefix, bool includeZero)
        {
            foreach (ISensor s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Fan || !s.Value.HasValue)
                    continue;
                int rpm = (int)s.Value.Value;
                if (rpm <= 0 && !includeZero)
                    continue; // unpopulated motherboard fan header
                if (snap.Fans.Count < 10)
                    snap.Fans.Add(new KeyValuePair<string, int>(prefix + s.Name, rpm));
            }
        }

        // Diagnostic snapshot of everything the sensor library sees, written to
        // %APPDATA%\TempMonitor\sensors.txt every ~30 s.
        private static void Dump(Computer computer, Snapshot snap)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("dump " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                foreach (IHardware hw in computer.Hardware)
                    DumpHardware(sb, hw, "");
                sb.AppendLine(string.Format("snapshot: cpu={0} gpu={1} cpuload={2} gpuload={3} ramload={4} ramused={5} fans={6} err={7}",
                    snap.CpuTemp, snap.GpuTemp, snap.CpuLoad, snap.GpuLoad,
                    snap.RamLoad, snap.RamUsedGb, snap.Fans.Count,
                    snap.Error == null ? "none" : snap.Error));
                foreach (KeyValuePair<string, int> f in snap.Fans)
                    sb.AppendLine("  shown: " + f.Key + " = " + f.Value + " rpm");
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor");
                File.WriteAllText(Path.Combine(dir, "sensors.txt"), sb.ToString());
            }
            catch { }
        }

        private static void DumpHardware(StringBuilder sb, IHardware hw, string indent)
        {
            sb.AppendLine(indent + "[" + hw.HardwareType + "] " + hw.Name);
            foreach (ISensor s in hw.Sensors)
            {
                if (s.SensorType == SensorType.Temperature || s.SensorType == SensorType.Fan ||
                    s.SensorType == SensorType.Load || s.SensorType == SensorType.Data)
                    sb.AppendLine(indent + "  " + s.SensorType + " \"" + s.Name + "\" = " +
                        (s.Value.HasValue ? s.Value.Value.ToString("0.0", CultureInfo.InvariantCulture) : "n/a"));
            }
            foreach (IHardware sub in hw.SubHardware)
                DumpHardware(sb, sub, indent + "  ");
        }

        public void Dispose()
        {
            _stop = true;
        }
    }

    // ------------------------------------------------------------- rgb link
    // Optional OpenRGB tie-in: LEDs turn red while a temperature alert is
    // active, and the user's saved OpenRGB profile is restored when it clears
    // (also applied once at startup). Runs the OpenRGB CLI hidden on state
    // transitions only - nothing stays resident.
    internal static class RgbControl
    {
        private static Process _proc;

        public static string ExePath = "C:\\Program Files\\OpenRGB\\OpenRGB.exe";

        public static bool Available()
        {
            return File.Exists(ExePath);
        }

        public static void ApplyProfile(string name)
        {
            string resolved = ResolveProfile(name);
            if (resolved != null)
                Run("--profile \"" + resolved + "\"");
        }

        public static void ApplyColor(string hexRrggbb)
        {
            Run("--mode static --color " + hexRrggbb);
        }

        // Falls back to the most recently saved OpenRGB profile when the
        // configured name doesn't exist.
        private static string ResolveProfile(string name)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRGB");
                if (File.Exists(Path.Combine(dir, name + ".orp")))
                    return name;
                string best = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (string f in Directory.GetFiles(dir, "*.orp"))
                {
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (t > bestTime) { bestTime = t; best = f; }
                }
                return best == null ? null : Path.GetFileNameWithoutExtension(best);
            }
            catch { return null; }
        }

        private static void Run(string args)
        {
            try
            {
                // A newer state supersedes an in-flight apply; hysteresis keeps
                // transitions many seconds apart so this is rare.
                if (_proc != null && !_proc.HasExited)
                {
                    try { _proc.Kill(); } catch { }
                }
                ProcessStartInfo psi = new ProcessStartInfo(ExePath, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                _proc = Process.Start(psi);
            }
            catch { }
        }
    }

    // -------------------------------------------------------------- overlay
    internal sealed class OverlayForm : Form
    {
        private const int WarnRearmDelta = 5; // deg C below limit before popup re-arms

        private readonly Config _config;
        private readonly SensorReader _reader;
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _monitorMenu;
        private readonly ToolStripMenuItem _popupItem;
        private readonly ToolStripMenuItem _startupItem;
        private readonly Font _bigFont = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        private readonly Font _smallFont = new Font("Segoe UI", 8.25f, FontStyle.Regular);

        private Snapshot _snap;
        private bool _cpuAlertArmed = true;
        private bool _gpuAlertArmed = true;
        private AlertForm _alert;

        // Fan % is current rpm vs the highest rpm ever observed for that fan
        // (the SuperIO chip doesn't report a rated max). Learned maxes are
        // persisted as "fanmax.<name>" config keys; saves are throttled so
        // slow upward creep during the first ramp doesn't hammer the disk.
        private bool _fanMaxDirty;
        private DateTime _fanMaxSaved = DateTime.MinValue;

        private ToolStripMenuItem _rgbItem; // null when OpenRGB isn't installed

        // LEDs as a 4-level temperature gauge: blue / green / yellow / red.
        // Band boundaries come from config (rgbcpubands / rgbgpubands, three
        // rising values each); the higher of the CPU and GPU bands wins. A
        // band change only steps down after the temp falls RgbHyst below the
        // boundary, so a temp sitting on a boundary can't flap the color.
        private int _rgbBand = -1; // -1 = nothing applied yet
        private const int RgbHyst = 3;
        private static readonly string[] RgbBandColors = { "0000FF", "00FF00", "FFFF00", "FF0000" };

        private static readonly Color ColOk = Color.FromArgb(235, 235, 235);
        private static readonly Color ColHot = Color.FromArgb(255, 72, 72);
        private static readonly Color ColDim = Color.FromArgb(160, 165, 175);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

        public OverlayForm()
        {
            _config = new Config();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(16, 17, 21);
            DoubleBuffered = true;
            Text = "TempMonitor";
            ClientSize = new Size(150, 64);

            // tray icon + menu ------------------------------------------------
            _menu = new ContextMenuStrip();
            _monitorMenu = new ToolStripMenuItem("Monitor");
            _menu.Items.Add(_monitorMenu);
            _menu.Items.Add(new ToolStripMenuItem("CPU alert threshold…", null, OnEditCpuLimit));
            _menu.Items.Add(new ToolStripMenuItem("GPU alert threshold…", null, OnEditGpuLimit));
            _popupItem = new ToolStripMenuItem("Popup warning", null, OnTogglePopup);
            _popupItem.Checked = _config.GetInt("popup", 1) != 0;
            _menu.Items.Add(_popupItem);
            RgbControl.ExePath = _config.GetString("rgbexe", RgbControl.ExePath);
            if (RgbControl.Available())
            {
                _rgbItem = new ToolStripMenuItem("RGB alert lighting", null, OnToggleRgb);
                _rgbItem.Checked = _config.GetInt("rgb", 1) != 0;
                _menu.Items.Add(_rgbItem);
            }
            _startupItem = new ToolStripMenuItem("Start with Windows", null, OnToggleStartup);
            _menu.Items.Add(_startupItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { Close(); }));
            _menu.Opening += delegate { RebuildMonitorMenu(); };
            ContextMenuStrip = _menu;

            _tray = new NotifyIcon();
            _tray.Icon = MakeTrayIcon();
            _tray.Text = "TempMonitor";
            _tray.Visible = true;
            _tray.ContextMenuStrip = _menu;
            _tray.DoubleClick += delegate { Visible = true; AssertTopMost(); };

            _startupItem.Checked = StartupTask.Exists();

            // First run: enable start-with-Windows by default (user can untick it).
            if (_config.GetInt("startupset", 0) == 0)
            {
                if (!_startupItem.Checked && StartupTask.Create())
                    _startupItem.Checked = true;
                _config.Set("startupset", 1);
                _config.Save();
            }

            RestorePosition();

            _reader = new SensorReader();
            _reader.OnSnapshot = delegate(Snapshot s)
            {
                try
                {
                    if (IsHandleCreated && !IsDisposed)
                        BeginInvoke(new Action<Snapshot>(ApplySnapshot), s);
                }
                catch { }
            };
            _reader.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW: keep out of Alt-Tab
                cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE: never steal focus
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        // ------------------------------------------------------------ layout
        private void ApplySnapshot(Snapshot s)
        {
            _snap = s;

            foreach (KeyValuePair<string, int> fan in s.Fans)
            {
                if (fan.Value > _config.GetInt("fanmax." + fan.Key, 0))
                {
                    _config.Set("fanmax." + fan.Key, fan.Value);
                    _fanMaxDirty = true;
                }
            }
            if (_fanMaxDirty && (DateTime.UtcNow - _fanMaxSaved).TotalSeconds > 60)
            {
                _config.Save();
                _fanMaxDirty = false;
                _fanMaxSaved = DateTime.UtcNow;
            }

            int cpuMax = _config.GetInt("cpumax", 85);
            int gpuMax = _config.GetInt("gpumax", 80);
            bool cpuHot = s.CpuTemp.HasValue && s.CpuTemp.Value >= cpuMax;
            bool gpuHot = s.GpuTemp.HasValue && s.GpuTemp.Value >= gpuMax;

            // popup warning with hysteresis so it fires once per over-temp episode
            if (s.CpuTemp.HasValue && s.CpuTemp.Value <= cpuMax - WarnRearmDelta) _cpuAlertArmed = true;
            if (s.GpuTemp.HasValue && s.GpuTemp.Value <= gpuMax - WarnRearmDelta) _gpuAlertArmed = true;

            if (_popupItem.Checked && ((cpuHot && _cpuAlertArmed) || (gpuHot && _gpuAlertArmed)))
            {
                StringBuilder msg = new StringBuilder();
                if (cpuHot && _cpuAlertArmed)
                {
                    msg.AppendLine(string.Format("CPU temperature {0:0}°C exceeds the {1}°C limit!", s.CpuTemp.Value, cpuMax));
                    _cpuAlertArmed = false;
                }
                if (gpuHot && _gpuAlertArmed)
                {
                    msg.AppendLine(string.Format("GPU temperature {0:0}°C exceeds the {1}°C limit!", s.GpuTemp.Value, gpuMax));
                    _gpuAlertArmed = false;
                }
                ShowAlert(msg.ToString().TrimEnd());
            }

            if (_rgbItem != null && _rgbItem.Checked && s.Error == null &&
                (s.CpuTemp.HasValue || s.GpuTemp.HasValue))
            {
                int[] cb = ParseBands("rgbcpubands", "50,60,72");
                int[] gb = ParseBands("rgbgpubands", "45,55,70");
                int candUp = Math.Max(BandOf(s.CpuTemp, cb, 0), BandOf(s.GpuTemp, gb, 0));
                int candDown = Math.Max(BandOf(s.CpuTemp, cb, RgbHyst), BandOf(s.GpuTemp, gb, RgbHyst));
                int band = _rgbBand;
                if (_rgbBand < 0)
                    band = candUp;          // first reading after start/enable
                else if (candUp > _rgbBand)
                    band = candUp;          // heating: react immediately
                else if (candDown < _rgbBand)
                    band = candDown;        // cooling: only after clearing hysteresis
                if (band != _rgbBand)
                {
                    RgbControl.ApplyColor(RgbBandColors[band]);
                    _rgbBand = band;
                }
            }

            _tray.Text = BuildTrayText(s);
            Resize2Content();
            Invalidate();
            AssertTopMost();
        }

        private static string BuildTrayText(Snapshot s)
        {
            string t = "TempMonitor";
            if (s.CpuTemp.HasValue) t += string.Format("\nCPU {0:0}°C", s.CpuTemp.Value);
            if (s.GpuTemp.HasValue) t += string.Format("  GPU {0:0}°C", s.GpuTemp.Value);
            if (t.Length > 63) t = t.Substring(0, 63);
            return t;
        }

        private List<object[]> BuildRows() // { name, value, color, font }
        {
            List<object[]> rows = new List<object[]>();
            Snapshot s = _snap;
            int cpuMax = _config.GetInt("cpumax", 85);
            int gpuMax = _config.GetInt("gpumax", 80);

            if (s == null)
            {
                rows.Add(new object[] { "Starting", "…", ColDim, _bigFont });
                return rows;
            }
            if (s.Error != null)
            {
                rows.Add(new object[] { "Error", "", ColHot, _smallFont });
                rows.Add(new object[] { s.Error.Length > 40 ? s.Error.Substring(0, 40) : s.Error, "", ColDim, _smallFont });
                return rows;
            }

            rows.Add(new object[] { "CPU",
                TempLoadText(s.CpuTemp, s.CpuLoad),
                s.CpuTemp.HasValue && s.CpuTemp.Value >= cpuMax ? ColHot : ColOk, _bigFont });
            rows.Add(new object[] { "GPU",
                TempLoadText(s.GpuTemp, s.GpuLoad),
                s.GpuTemp.HasValue && s.GpuTemp.Value >= gpuMax ? ColHot : ColOk, _bigFont });

            if (s.RamLoad.HasValue || s.RamUsedGb.HasValue)
            {
                string ram = "";
                if (s.RamUsedGb.HasValue)
                    ram = string.Format("{0:0.0} GB", s.RamUsedGb.Value);
                if (s.RamLoad.HasValue)
                    ram = string.Format("{0:0}%", s.RamLoad.Value) + (ram.Length > 0 ? "  " + ram : "");
                rows.Add(new object[] { "RAM", ram, ColOk, _bigFont });
            }

            foreach (KeyValuePair<string, int> fan in s.Fans)
            {
                string val = string.Format("{0} rpm", fan.Value);
                int max = _config.GetInt("fanmax." + fan.Key, 0);
                if (max > 0 && fan.Value > 0)
                    val += string.Format("  {0:0}%", Math.Min(100, fan.Value * 100 / max));
                rows.Add(new object[] { fan.Key, val, ColDim, _smallFont });
            }

            return rows;
        }

        private static string TempLoadText(float? temp, float? load)
        {
            string t = temp.HasValue ? string.Format("{0:0}°C", temp.Value) : "--";
            if (load.HasValue)
                t += string.Format("  {0:0}%", load.Value);
            return t;
        }

        private void Resize2Content()
        {
            List<object[]> rows = BuildRows();
            int pad = 9, gap = 14;
            int w = 0, h = pad;
            foreach (object[] r in rows)
            {
                Font f = (Font)r[3];
                Size a = TextRenderer.MeasureText((string)r[0], f);
                Size b = TextRenderer.MeasureText((string)r[1], f);
                if (a.Width + gap + b.Width > w) w = a.Width + gap + b.Width;
                h += Math.Max(a.Height, b.Height) + 1;
            }
            h += pad - 1;
            w += pad * 2;
            if (w < 132) w = 132;
            Size wanted = new Size(w, h);
            if (ClientSize != wanted)
            {
                ClientSize = wanted;
                Region = new Region(); // replaced below in OnResize
                ApplyRoundCorners();
            }
        }

        private void ApplyRoundCorners()
        {
            IntPtr rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 12, 12);
            Region = System.Drawing.Region.FromHrgn(rgn);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyRoundCorners();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            List<object[]> rows = BuildRows();
            int pad = 9;
            int y = pad;
            foreach (object[] r in rows)
            {
                Font f = (Font)r[3];
                Color c = (Color)r[2];
                string name = (string)r[0];
                string val = (string)r[1];
                Size a = TextRenderer.MeasureText(name, f);
                Size b = TextRenderer.MeasureText(val, f);
                TextRenderer.DrawText(e.Graphics, name, f, new Point(pad, y), c == ColHot ? ColHot : (f == _bigFont ? ColOk : ColDim));
                TextRenderer.DrawText(e.Graphics, val, f, new Point(ClientSize.Width - pad - b.Width, y), c);
                y += Math.Max(a.Height, b.Height) + 1;
            }
        }

        // ------------------------------------------------- drag + persistence
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, 2 /*HTCAPTION*/, 0);
                SavePosition();
            }
        }

        private void RestorePosition()
        {
            Screen target = FindConfiguredScreen();
            int x = _config.GetInt("x", int.MinValue);
            int y = _config.GetInt("y", int.MinValue);
            Rectangle wa = target.WorkingArea;
            if (x == int.MinValue || y == int.MinValue ||
                x < wa.Left - 20 || x > wa.Right - 40 || y < wa.Top - 20 || y > wa.Bottom - 20)
                Location = new Point(wa.Right - Width - 14, wa.Top + 14);
            else
                Location = new Point(x, y);
        }

        private Screen FindConfiguredScreen()
        {
            string name = _config.GetString("screen", "");
            foreach (Screen sc in Screen.AllScreens)
                if (sc.DeviceName == name)
                    return sc;
            return Screen.PrimaryScreen;
        }

        private void SavePosition()
        {
            _config.Set("x", Location.X);
            _config.Set("y", Location.Y);
            _config.Set("screen", Screen.FromControl(this).DeviceName);
            _config.Save();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x232 /*WM_EXITSIZEMOVE*/)
                SavePosition();
        }

        private void AssertTopMost()
        {
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // ------------------------------------------------------------- menu
        private void RebuildMonitorMenu()
        {
            _monitorMenu.DropDownItems.Clear();
            Screen current = Screen.FromControl(this);
            List<Screen> screens = new List<Screen>(Screen.AllScreens);
            screens.Sort(delegate(Screen a, Screen b)
            {
                return string.CompareOrdinal(a.DeviceName, b.DeviceName);
            });
            foreach (Screen sc in screens)
            {
                // "\\.\DISPLAY2" -> "Display 2" (matches Windows display numbering)
                string label = string.Format("{0}  ({1}×{2}){3}",
                    sc.DeviceName.Replace("\\\\.\\DISPLAY", "Display "),
                    sc.Bounds.Width, sc.Bounds.Height, sc.Primary ? " – primary" : "");
                ToolStripMenuItem item = new ToolStripMenuItem(label);
                item.Checked = sc.DeviceName == current.DeviceName;
                Screen captured = sc;
                item.Click += delegate { MoveToScreen(captured); };
                _monitorMenu.DropDownItems.Add(item);
            }
        }

        private void MoveToScreen(Screen sc)
        {
            Rectangle wa = sc.WorkingArea;
            Location = new Point(wa.Right - Width - 14, wa.Top + 14);
            SavePosition();
            AssertTopMost();
        }

        private void OnEditCpuLimit(object sender, EventArgs e)
        {
            int v = ThresholdDialog.Ask("CPU alert threshold", _config.GetInt("cpumax", 85));
            if (v > 0) { _config.Set("cpumax", v); _config.Save(); Invalidate(); }
        }

        private void OnEditGpuLimit(object sender, EventArgs e)
        {
            int v = ThresholdDialog.Ask("GPU alert threshold", _config.GetInt("gpumax", 80));
            if (v > 0) { _config.Set("gpumax", v); _config.Save(); Invalidate(); }
        }

        private void OnToggleRgb(object sender, EventArgs e)
        {
            _rgbItem.Checked = !_rgbItem.Checked;
            _config.Set("rgb", _rgbItem.Checked ? 1 : 0);
            _config.Save();
            if (!_rgbItem.Checked)
                RgbControl.ApplyProfile(_config.GetString("rgbprofile", "normal")); // hand the LEDs back to the saved look
            _rgbBand = -1;
        }

        private int[] ParseBands(string key, string fallback)
        {
            string[] parts = _config.GetString(key, fallback).Split(',');
            int[] b = new int[3];
            if (parts.Length != 3)
                parts = fallback.Split(',');
            for (int i = 0; i < 3; i++)
            {
                int v;
                b[i] = int.TryParse(parts[i].Trim(), out v) ? v : int.Parse(fallback.Split(',')[i]);
            }
            return b;
        }

        private static int BandOf(float? t, int[] bounds, int shift)
        {
            if (!t.HasValue)
                return 0;
            int band = 0;
            for (int i = 0; i < bounds.Length; i++)
                if (t.Value >= bounds[i] - shift)
                    band = i + 1;
            return band;
        }

        private void OnTogglePopup(object sender, EventArgs e)
        {
            _popupItem.Checked = !_popupItem.Checked;
            _config.Set("popup", _popupItem.Checked ? 1 : 0);
            _config.Save();
        }

        private void OnToggleStartup(object sender, EventArgs e)
        {
            if (_startupItem.Checked)
            {
                if (StartupTask.Remove())
                    _startupItem.Checked = false;
            }
            else
            {
                if (StartupTask.Create())
                    _startupItem.Checked = true;
                else
                    MessageBox.Show("Could not create the startup task.", "TempMonitor",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ShowAlert(string message)
        {
            if (_alert == null || _alert.IsDisposed)
                _alert = new AlertForm();
            _alert.Present(message, Screen.FromControl(this));
            SystemSounds.Exclamation.Play();
        }

        private static Icon MakeTrayIcon()
        {
            Bitmap b = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush stem = new SolidBrush(Color.FromArgb(230, 90, 60)))
                {
                    g.FillRectangle(stem, 6, 1, 4, 9);
                    g.FillEllipse(stem, 3, 8, 10, 7);
                }
                using (Pen p = new Pen(Color.White, 1f))
                    g.DrawEllipse(p, 3, 8, 10, 7);
            }
            return Icon.FromHandle(b.GetHicon());
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_fanMaxDirty)
                _config.Save();
            _reader.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            base.OnFormClosed(e);
        }
    }

    // -------------------------------------------------------------- dialogs
    internal sealed class ThresholdDialog : Form
    {
        private readonly NumericUpDown _num;

        private ThresholdDialog(string title, int value)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(230, 92);

            Label lbl = new Label();
            lbl.Text = "Warn when temperature reaches (°C):";
            lbl.SetBounds(12, 12, 210, 18);
            Controls.Add(lbl);

            _num = new NumericUpDown();
            _num.Minimum = 40;
            _num.Maximum = 110;
            _num.Value = Math.Max(40, Math.Min(110, value));
            _num.SetBounds(12, 34, 70, 24);
            Controls.Add(_num);

            Button ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(64, 62, 75, 24);
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(145, 62, 75, 24);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        public static int Ask(string title, int current)
        {
            using (ThresholdDialog d = new ThresholdDialog(title, current))
                return d.ShowDialog() == DialogResult.OK ? (int)d._num.Value : -1;
        }
    }

    internal sealed class AlertForm : Form
    {
        private readonly Label _label;
        private readonly System.Windows.Forms.Timer _autoClose;

        public AlertForm()
        {
            Text = "Temperature warning";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(46, 12, 12);
            Size = new Size(360, 120);

            Panel border = new Panel();
            border.Dock = DockStyle.Fill;
            border.Padding = new Padding(2);
            border.BackColor = Color.FromArgb(220, 40, 40);
            Controls.Add(border);

            Panel inner = new Panel();
            inner.Dock = DockStyle.Fill;
            inner.BackColor = Color.FromArgb(46, 12, 12);
            border.Controls.Add(inner);

            Label title = new Label();
            title.Text = "⚠  TEMPERATURE WARNING";
            title.ForeColor = Color.FromArgb(255, 96, 96);
            title.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            title.SetBounds(14, 10, 320, 22);
            inner.Controls.Add(title);

            _label = new Label();
            _label.ForeColor = Color.White;
            _label.Font = new Font("Segoe UI", 9.5f);
            _label.SetBounds(14, 36, 330, 48);
            inner.Controls.Add(_label);

            Button ok = new Button();
            ok.Text = "Dismiss";
            ok.ForeColor = Color.White;
            ok.FlatStyle = FlatStyle.Flat;
            ok.SetBounds(262, 88, 82, 24);
            ok.Click += delegate { Hide(); };
            inner.Controls.Add(ok);

            _autoClose = new System.Windows.Forms.Timer();
            _autoClose.Interval = 20000;
            _autoClose.Tick += delegate { _autoClose.Stop(); Hide(); };
        }

        public void Present(string message, Screen screen)
        {
            _label.Text = message;
            Rectangle wa = screen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + wa.Height / 5);
            _autoClose.Stop();
            _autoClose.Start();
            Show();
            SetForegroundSafe();
        }

        private void SetForegroundSafe()
        {
            try { Activate(); }
            catch { }
        }
    }

    // ------------------------------------------------------- startup task
    // Uses a Task Scheduler logon task (highest privileges) so the app starts
    // elevated at logon without a UAC prompt each boot.
    internal static class StartupTask
    {
        private const string TaskName = "TempMonitor";

        private static int RunSchtasks(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(15000);
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch
            {
                return -1;
            }
        }

        public static bool Exists()
        {
            return RunSchtasks("/Query /TN \"" + TaskName + "\"") == 0;
        }

        public static bool Create()
        {
            string exe = Application.ExecutablePath;
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            return RunSchtasks("/Create /F /TN \"" + TaskName + "\" /SC ONLOGON /RL HIGHEST /IT" +
                               " /RU \"" + user + "\"" +
                               " /TR \"\\\"" + exe + "\\\"\"") == 0;
        }

        public static bool Remove()
        {
            return RunSchtasks("/Delete /F /TN \"" + TaskName + "\"") == 0;
        }
    }
}
