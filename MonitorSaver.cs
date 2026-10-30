// MonitorSaver - turns off individual monitors in a multi-monitor setup when they are not in use.
//
// Idle time, content-change waking/sensitivity, power-off method and "never turn off" can be
// set per monitor from the tray menu; anything not overridden uses the global defaults.
//
// A monitor counts as "in use" when:
//   * the mouse cursor moves on it,
//   * you type / click while the foreground window is on it, or
//   * its screen content changes noticeably (video, scrolling, new windows...).
// After a configurable idle time it is switched off via DDC/CI (real power-off / standby),
// or covered with a black overlay for monitors that do not support DDC/CI.
// Moving the mouse onto it (or content changing on it, if enabled) wakes it again.
//
// Build (no SDK needed, uses the C# compiler shipped with Windows):
//   build.cmd

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public int cbSize; public uint dwTime; }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rc, IntPtr data);

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITORINFOF_PRIMARY = 1;

    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO lii);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int xd, int yd, int wd, int hd, IntPtr src, int xs, int ys, int ws, int hs, uint rop);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr prev);

    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] public static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint value);
}

// DDC/CI power control (VCP code 0xD6: 1 = on, 4 = off/standby).
static class Ddc
{
    const byte VCP_POWER_MODE = 0xD6;

    public static bool SetPower(IntPtr hMonitor, bool on)
    {
        uint count;
        if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out count) || count == 0) return false;
        var arr = new Native.PHYSICAL_MONITOR[count];
        if (!Native.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, arr)) return false;
        bool ok = false;
        try
        {
            foreach (var pm in arr)
                ok |= Native.SetVCPFeature(pm.hPhysicalMonitor, VCP_POWER_MODE, on ? 1u : 4u);
        }
        finally { Native.DestroyPhysicalMonitors(count, arr); }
        return ok;
    }
}

// Click-through, capture-excluded black window used when DDC/CI is unavailable.
class Overlay : Form
{
    Rectangle target;

    public Overlay(Rectangle bounds)
    {
        target = bounds;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.Black;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
    }

    public void MoveTo(Rectangle bounds)
    {
        target = bounds;
        Native.SetWindowPos(Handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x10);
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE | TOPMOST
            cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetLayeredWindowAttributes(Handle, 0, 255, 2);
        // WDA_EXCLUDEFROMCAPTURE: keeps the overlay out of our own screen captures,
        // so content changes underneath can still be detected.
        try { Native.SetWindowDisplayAffinity(Handle, 0x11); } catch { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        MoveTo(target);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x02E0) return; // WM_DPICHANGED: keep exact physical bounds
        base.WndProc(ref m);
    }
}

class Mon
{
    public string Device;
    public string Name;
    public string Key;   // stable per physical monitor + port, used for per-monitor settings
    public IntPtr HMon;
    public Rectangle Bounds;
    public bool Primary;
    public DateTime LastActive = DateTime.Now;
    public bool IsOff;
    public bool OffViaDdc;
    public Overlay Overlay;
    public int[] Thumb;
}

class Settings
{
    readonly string path;
    readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public Settings()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorSaver");
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "settings.ini");
        if (File.Exists(path))
            foreach (var line in File.ReadAllLines(path))
            {
                int i = line.IndexOf('=');
                if (i > 0) values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
    }

    public string Get(string key, string def) { string v; return values.TryGetValue(key, out v) ? v : def; }
    public int GetInt(string key, int def) { int v; return int.TryParse(Get(key, ""), out v) ? v : def; }
    public double GetDouble(string key, double def)
    {
        double v;
        return double.TryParse(Get(key, ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : def;
    }
    public bool GetBool(string key, bool def) { return Get(key, def ? "1" : "0") == "1"; }

    public bool Has(string key) { return values.ContainsKey(key); }

    // A null value removes the key (per-monitor settings then fall back to the defaults).
    public void Set(string key, object value)
    {
        if (value == null) values.Remove(key);
        else values[key] = value is bool ? ((bool)value ? "1" : "0") : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllLines(path, values.Select(kv => kv.Key + "=" + kv.Value).ToArray());
    }

    // Defaults for all monitors.
    public bool Enabled { get { return GetBool("Enabled", true); } set { Set("Enabled", value); } }
    public int TimeoutSec { get { return GetInt("TimeoutSec", 300); } set { Set("TimeoutSec", value); } }
    public bool WakeOnContent { get { return GetBool("WakeOnContent", true); } set { Set("WakeOnContent", value); } }
    public double ChangePct { get { return GetDouble("ChangePct", 0.5); } set { Set("ChangePct", value); } }

    // Per-monitor values, falling back to the defaults above.
    public bool Excluded(Mon m) { return GetBool(m.Key + ".Exclude", false); }
    public string Method(Mon m) { return Get(m.Key + ".Method", "ddc"); }
    public int TimeoutFor(Mon m) { return GetInt(m.Key + ".TimeoutSec", TimeoutSec); }
    public bool WakeOnContentFor(Mon m) { return GetBool(m.Key + ".WakeOnContent", WakeOnContent); }
    public double ChangePctFor(Mon m) { return GetDouble(m.Key + ".ChangePct", ChangePct); }
}

class App : ApplicationContext
{
    const int TickMs = 500;
    const int ContentEveryTicks = 4;   // content check every 2 s
    const int ThumbW = 160, ThumbH = 90;
    const int PixelDiffThreshold = 24;

    readonly Settings settings = new Settings();
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer;
    List<Mon> monitors = new List<Mon>();

    volatile bool monitorsDirty = true;
    volatile bool paused;
    volatile bool resetActivity;
    Native.POINT lastCursor;
    uint lastInputTick;
    int tickCount;

    public App()
    {
        tray = new NotifyIcon { Icon = MakeIcon(), Visible = true, Text = "Monitor Saver" };
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Opening += (s, e) => BuildMenu();

        SystemEvents.DisplaySettingsChanged += (s, e) => monitorsDirty = true;
        SystemEvents.SessionSwitch += (s, e) =>
        {
            if (e.Reason == SessionSwitchReason.SessionLock) paused = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock) { paused = false; resetActivity = true; }
        };
        SystemEvents.PowerModeChanged += (s, e) =>
        {
            if (e.Mode == PowerModes.Resume) { monitorsDirty = true; resetActivity = true; }
        };

        Native.GetCursorPos(out lastCursor);
        timer = new System.Windows.Forms.Timer { Interval = TickMs };
        timer.Tick += (s, e) => Tick();
        timer.Start();
    }

    // ---------------------------------------------------------------- monitor list

    void RefreshMonitors()
    {
        monitorsDirty = false;
        var found = new List<Mon>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Native.RECT rc, IntPtr d) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX)) };
            if (Native.GetMonitorInfo(h, ref mi))
            {
                var r = mi.rcMonitor;
                string name, key;
                Identify(mi.szDevice, out name, out key);
                found.Add(new Mon
                {
                    HMon = h,
                    Device = mi.szDevice,
                    Name = name,
                    Key = key,
                    Bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom),
                    Primary = (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
                });
            }
            return true;
        }, IntPtr.Zero);

        foreach (var n in found)
        {
            var old = monitors.FirstOrDefault(o => o.Device == n.Device);
            if (old == null) continue;
            n.LastActive = old.LastActive;
            n.IsOff = old.IsOff;
            n.OffViaDdc = old.OffViaDdc;
            n.Overlay = old.Overlay;
            if (n.Overlay != null) n.Overlay.MoveTo(n.Bounds);
        }
        foreach (var gone in monitors.Where(o => !found.Any(n => n.Device == o.Device)))
            if (gone.Overlay != null) gone.Overlay.Close();

        monitors = found.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
    }

    // Display name, and a settings key that survives DISPLAYn renumbering after reboots or
    // driver updates: model code + port instance from the monitor's device interface path,
    // e.g. \\?\DISPLAY#DELD06F#5&10c0ae1&0&UID4629#{...} -> DELD06F_5_10c0ae1_0_UID4629.
    static void Identify(string device, out string name, out string key)
    {
        const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;
        string shortDev = device.Replace("\\\\.\\", "");
        var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE)) };
        bool ok = Native.EnumDisplayDevices(device, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME);
        name = ok && !string.IsNullOrEmpty(dd.DeviceString) ? dd.DeviceString + " (" + shortDev + ")" : shortDev;

        string[] parts = ok && dd.DeviceID != null ? dd.DeviceID.Split('#') : new string[0];
        string raw = parts.Length >= 3 ? parts[1] + "_" + parts[2] : shortDev;
        key = new string(raw.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    }

    Mon Find(IntPtr hMon)
    {
        return monitors.FirstOrDefault(m => m.HMon == hMon);
    }

    // ---------------------------------------------------------------- main loop

    void Tick()
    {
        if (monitorsDirty) RefreshMonitors();
        DateTime now = DateTime.Now;

        if (resetActivity)
        {
            resetActivity = false;
            foreach (var m in monitors) { m.LastActive = now; m.Thumb = null; }
        }

        // Mouse movement -> monitor under the cursor is in use.
        Native.POINT p;
        Native.GetCursorPos(out p);
        bool moved = p.X != lastCursor.X || p.Y != lastCursor.Y;
        lastCursor = p;
        if (moved)
        {
            var m = Find(Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST));
            if (m == null) monitorsDirty = true; else MarkActive(m);
        }

        // Keyboard / click without movement -> monitor of the foreground window is in use.
        var lii = new Native.LASTINPUTINFO { cbSize = Marshal.SizeOf(typeof(Native.LASTINPUTINFO)) };
        if (Native.GetLastInputInfo(ref lii))
        {
            if (lii.dwTime != lastInputTick && !moved)
            {
                var m = Find(Native.MonitorFromWindow(Native.GetForegroundWindow(), Native.MONITOR_DEFAULTTONEAREST));
                if (m != null) MarkActive(m);
            }
            lastInputTick = lii.dwTime;
        }

        // Screen content changes.
        if (++tickCount % ContentEveryTicks == 0)
        {
            foreach (var m in monitors)
            {
                if (m.IsOff && !settings.WakeOnContentFor(m)) { m.Thumb = null; continue; }
                int[] thumb;
                try { thumb = Capture(m.Bounds); } catch { continue; }
                if (m.Thumb != null && ChangedPercent(m.Thumb, thumb) >= settings.ChangePctFor(m))
                    MarkActive(m);
                m.Thumb = thumb;
            }
        }

        // Idle -> off.
        if (settings.Enabled && !paused)
        {
            foreach (var m in monitors)
                if (!m.IsOff && !settings.Excluded(m) && now - m.LastActive >= TimeSpan.FromSeconds(settings.TimeoutFor(m)))
                    TurnOff(m);
        }

        int off = monitors.Count(m => m.IsOff);
        tray.Text = !settings.Enabled ? "Monitor Saver (disabled)"
                  : "Monitor Saver - " + off + " of " + monitors.Count + " off";
    }

    void MarkActive(Mon m)
    {
        m.LastActive = DateTime.Now;
        if (m.IsOff) Wake(m);
    }

    void TurnOff(Mon m)
    {
        bool viaDdc = settings.Method(m) == "ddc" && Ddc.SetPower(m.HMon, false);
        if (!viaDdc)
        {
            m.Overlay = new Overlay(m.Bounds);
            m.Overlay.Show();
        }
        m.OffViaDdc = viaDdc;
        m.IsOff = true;
        m.Thumb = null;
    }

    void Wake(Mon m)
    {
        if (m.OffViaDdc) Ddc.SetPower(m.HMon, true);
        if (m.Overlay != null) { m.Overlay.Close(); m.Overlay = null; }
        m.IsOff = false;
        m.OffViaDdc = false;
        m.LastActive = DateTime.Now;
        m.Thumb = null;
    }

    void WakeAll()
    {
        foreach (var m in monitors) if (m.IsOff) Wake(m); else m.LastActive = DateTime.Now;
    }

    // ---------------------------------------------------------------- content detection

    static int[] Capture(Rectangle r)
    {
        using (var bmp = new Bitmap(ThumbW, ThumbH, PixelFormat.Format32bppRgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr dst = g.GetHdc();
                IntPtr src = Native.GetDC(IntPtr.Zero);
                try
                {
                    Native.SetStretchBltMode(dst, 4); // HALFTONE: averages pixels, filters out tiny changes
                    Native.SetBrushOrgEx(dst, 0, 0, IntPtr.Zero);
                    Native.StretchBlt(dst, 0, 0, ThumbW, ThumbH, src, r.X, r.Y, r.Width, r.Height, 0x00CC0020);
                }
                finally
                {
                    Native.ReleaseDC(IntPtr.Zero, src);
                    g.ReleaseHdc(dst);
                }
            }
            var data = bmp.LockBits(new Rectangle(0, 0, ThumbW, ThumbH), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            var px = new int[ThumbW * ThumbH];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            bmp.UnlockBits(data);
            return px;
        }
    }

    static double ChangedPercent(int[] a, int[] b)
    {
        int changed = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int x = a[i], y = b[i];
            if (x == y) continue;
            int dr = Math.Abs(((x >> 16) & 0xFF) - ((y >> 16) & 0xFF));
            int dg = Math.Abs(((x >> 8) & 0xFF) - ((y >> 8) & 0xFF));
            int db = Math.Abs((x & 0xFF) - (y & 0xFF));
            if (Math.Max(dr, Math.Max(dg, db)) > PixelDiffThreshold) changed++;
        }
        return changed * 100.0 / a.Length;
    }

    // ---------------------------------------------------------------- tray menu

    void BuildMenu()
    {
        var menu = tray.ContextMenuStrip;
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem("Monitor Saver") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        foreach (var m in monitors)
        {
            var mon = m;
            string state = m.IsOff ? "off" : settings.Excluded(m) ? "always on" : "idle " + FormatSpan(DateTime.Now - m.LastActive);
            var item = new ToolStripMenuItem(m.Name + (m.Primary ? " [primary]" : "") + "  -  " + state);
            item.DropDownItems.Add("Turn off now", null, (s, e) => TurnOff(mon)).Enabled = !m.IsOff;
            item.DropDownItems.Add("Wake now", null, (s, e) => Wake(mon)).Enabled = m.IsOff;
            item.DropDownItems.Add(new ToolStripSeparator());
            item.DropDownItems.Add(Check("Never turn off", settings.Excluded(m), () => settings.Set(mon.Key + ".Exclude", !settings.Excluded(mon))));
            item.DropDownItems.Add(Check("Method: DDC/CI power off", settings.Method(m) == "ddc", () => settings.Set(mon.Key + ".Method", "ddc")));
            item.DropDownItems.Add(Check("Method: black overlay", settings.Method(m) == "overlay", () => settings.Set(mon.Key + ".Method", "overlay")));
            item.DropDownItems.Add(new ToolStripSeparator());
            item.DropDownItems.Add(Choice("Turn off after", mon.Key + ".TimeoutSec", settings.TimeoutFor(m), settings.TimeoutSec, TimeoutOptions));
            item.DropDownItems.Add(Choice("Wake when content changes", mon.Key + ".WakeOnContent", settings.WakeOnContentFor(m), settings.WakeOnContent, OnOffOptions));
            item.DropDownItems.Add(Choice("Content change sensitivity", mon.Key + ".ChangePct", settings.ChangePctFor(m), settings.ChangePct, SensitivityOptions));
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Check("Enabled", settings.Enabled, () =>
        {
            settings.Enabled = !settings.Enabled;
            if (!settings.Enabled) WakeAll();
        }));

        var defaults = new ToolStripMenuItem("Defaults for all monitors");
        defaults.DropDownItems.Add(Choice("Turn off after", "TimeoutSec", settings.TimeoutSec, TimeoutOptions));
        defaults.DropDownItems.Add(Choice("Wake when content changes", "WakeOnContent", settings.WakeOnContent, OnOffOptions));
        defaults.DropDownItems.Add(Choice("Content change sensitivity", "ChangePct", settings.ChangePct, SensitivityOptions));
        menu.Items.Add(defaults);

        menu.Items.Add(Check("Start with Windows", IsAutostart(), () => SetAutostart(!IsAutostart())));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Wake all monitors", null, (s, e) => WakeAll());
        menu.Items.Add("Exit", null, (s, e) => ExitThread());
    }

    static readonly Tuple<string, int>[] TimeoutOptions = new[] { 1, 2, 5, 10, 15, 30, 60 }
        .Select(min => Tuple.Create(min + " min", min * 60)).ToArray();
    static readonly Tuple<string, bool>[] OnOffOptions = { Tuple.Create("On", true), Tuple.Create("Off", false) };
    static readonly Tuple<string, double>[] SensitivityOptions =
    {
        Tuple.Create("High (0.2% of screen)", 0.2), Tuple.Create("Normal (0.5%)", 0.5),
        Tuple.Create("Low (2%)", 2.0), Tuple.Create("Very low (10%)", 10.0),
    };

    static string LabelOf<T>(Tuple<string, T>[] options, T value)
    {
        var opt = options.FirstOrDefault(o => Equals(o.Item2, value));
        return opt != null ? opt.Item1 : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    // Submenu for a global default setting.
    ToolStripMenuItem Choice<T>(string title, string key, T current, Tuple<string, T>[] options)
    {
        var item = new ToolStripMenuItem(title + ": " + LabelOf(options, current));
        foreach (var opt in options)
        {
            T value = opt.Item2;
            item.DropDownItems.Add(Check(opt.Item1, Equals(current, value), () => settings.Set(key, value)));
        }
        return item;
    }

    // Submenu for a per-monitor setting, with a "Use default" entry that removes the override.
    ToolStripMenuItem Choice<T>(string title, string key, T current, T defaultValue, Tuple<string, T>[] options)
    {
        bool overridden = settings.Has(key);
        var item = new ToolStripMenuItem(title + ": " + LabelOf(options, current) + (overridden ? "" : " (default)"));
        item.DropDownItems.Add(Check("Use default (" + LabelOf(options, defaultValue) + ")", !overridden, () => settings.Set(key, null)));
        item.DropDownItems.Add(new ToolStripSeparator());
        foreach (var opt in options)
        {
            T value = opt.Item2;
            item.DropDownItems.Add(Check(opt.Item1, overridden && Equals(current, value), () => settings.Set(key, value)));
        }
        return item;
    }

    static ToolStripMenuItem Check(string text, bool isChecked, Action onClick)
    {
        var item = new ToolStripMenuItem(text) { Checked = isChecked };
        item.Click += (s, e) => onClick();
        return item;
    }

    static string FormatSpan(TimeSpan t)
    {
        return ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static bool IsAutostart()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue("MonitorSaver") != null;
    }

    static void SetAutostart(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue("MonitorSaver", "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue("MonitorSaver", false);
        }
    }

    static Icon MakeIcon()
    {
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillRectangle(Brushes.DimGray, 2, 4, 28, 19);
            g.FillRectangle(Brushes.Black, 5, 7, 22, 13);
            g.FillEllipse(Brushes.Gold, 18, 9, 7, 7);
            g.FillRectangle(Brushes.DimGray, 13, 23, 6, 4);
            g.FillRectangle(Brushes.DimGray, 8, 27, 16, 3);
            IntPtr h = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(h).Clone();
            Native.DestroyIcon(h);
            return icon;
        }
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        WakeAll();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }

    [STAThread]
    static void Main()
    {
        bool created;
        using (new Mutex(true, "MonitorSaver_SingleInstance", out created))
        {
            if (!created) return;
            // Per-monitor DPI aware v2 so all coordinates are physical pixels.
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { Native.SetProcessDPIAware(); }
            Application.EnableVisualStyles();
            var app = new App();
            Application.ApplicationExit += (s, e) => app.WakeAll();
            Application.Run(app);
        }
    }
}
