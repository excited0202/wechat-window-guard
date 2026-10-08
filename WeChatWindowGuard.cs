using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Local, current-user window-position repair. No messages, files, or account APIs.
internal static class Program
{
    internal static string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeChatWindowGuard");
    internal const string StartupName = "WeChatWindowGuard.lnk";
    private static readonly string SessionSuffix = Process.GetCurrentProcess().SessionId.ToString();
    private static readonly string MutexName = @"Local\WeChatWindowGuard.v1." + SessionSuffix;
    private static readonly string StopName = @"Local\WeChatWindowGuard.Stop.v1." + SessionSuffix;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    [STAThread]
    private static int Main(string[] args)
    {
        string mode = args.Length == 0 ? "--watch" : args[0];
        // Tests have their own scratch directory and never share installed guard logs.
        if (mode == "--self-test") DataDir = Path.Combine(Path.GetTempPath(), "WeChatWindowGuard-tests-" + Guid.NewGuid().ToString("N"));
        string report = args.Length > 1 ? args[1] : Path.Combine(DataDir, "last-result.json");
        try
        {
            Directory.CreateDirectory(DataDir);
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            Native.SetThreadDpiAwarenessContext(new IntPtr(-4));
            if (mode == "--stop" || mode == "--disable")
            {
                EventWaitHandle stop;
                if (EventWaitHandle.TryOpenExisting(StopName, out stop)) { using (stop) stop.Set(); }
                if (mode == "--disable")
                {
                    string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupName);
                    if (File.Exists(link)) File.Move(link, Path.Combine(DataDir, "startup-disabled-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".lnk"));
                }
                Write(report, new { Mode = mode, Success = true });
                return 0;
            }
            if (mode == "--inspect")
            {
                Write(report, new { Time = DateTime.Now, Monitors = Native.Monitors(), Windows = Native.WeChatWindows().Select(w => w.Snapshot()).ToArray() });
                return 0;
            }
            if (mode == "--self-test")
            {
                try { Write(report, Tests.Run()); return 0; }
                finally { if (args.Length > 1) Directory.Delete(DataDir, true); }
            }
            if (mode == "--recover")
            {
                var monitors = Native.Monitors();
                var windows = Native.WeChatWindows();
                var results = new List<object>();
                foreach (var w in windows)
                {
                    object before = w.Snapshot();
                    bool changed = Repair(w, monitors);
                    Native.ShowWindowAsync(w.Handle, 9);
                    Thread.Sleep(250);
                    Native.SetForegroundWindow(w.Handle);
                    var after = Native.Read(w.Handle);
                    if (after != null && !after.Minimized && !after.Maximized) Repair(after, Native.Monitors());
                    Thread.Sleep(200);
                    var finalState = Native.Read(w.Handle);
                    results.Add(new { Before = before, PositionChanged = changed, After = finalState == null ? null : finalState.Snapshot() });
                }
                Write(report, new { Time = DateTime.Now, Mode = mode, Count = windows.Count, Results = results });
                return windows.Count == 0 ? 2 : 0;
            }
            if (mode != "--watch") throw new ArgumentException("Unknown mode");
            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created) return 0;
                using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName))
                using (var timer = new System.Windows.Forms.Timer())
                {
                    var pending = new Dictionary<long, string>();
                    var attempts = new Dictionary<long, DateTime>();
                    string lastTopology = null;
                    DateTime lastHealth = DateTime.MinValue;
                    timer.Interval = 2000;
                    timer.Tick += delegate
                    {
                        try
                        {
                            if (stop.WaitOne(0)) { Application.ExitThread(); return; }
                            var monitors = Native.Monitors();
                            string topology = string.Join(";", monitors.Select(m => m.Bounds + "/" + m.Work).ToArray());
                            // Wait for a stable monitor layout; do not interfere with a manual drag.
                            if (monitors.Count == 0 || topology != lastTopology || Native.MouseButtonDown())
                            { lastTopology = topology; pending.Clear(); return; }
                            var windows = Native.WeChatWindows();
                            var live = new HashSet<long>(windows.Select(w => w.Handle.ToInt64()));
                            foreach (long id in pending.Keys.Where(k => !live.Contains(k)).ToArray()) pending.Remove(id);
                            foreach (long id in attempts.Keys.Where(k => !live.Contains(k)).ToArray()) attempts.Remove(id);
                            foreach (var w in windows)
                            {
                                long id = w.Handle.ToInt64();
                                // Windows belonging to another virtual desktop are not disturbed.
                                if (w.Cloaked || w.Maximized || Geometry.Accessible(w.EffectiveRect(monitors), monitors))
                                { pending.Remove(id); continue; }
                                string current = w.ProcessId + ":" + w.EffectiveRect(monitors) + ":" + w.Minimized;
                                string previous;
                                DateTime lastAttempt;
                                if (pending.TryGetValue(id, out previous) && previous == current &&
                                    (!attempts.TryGetValue(id, out lastAttempt) || DateTime.UtcNow - lastAttempt > TimeSpan.FromSeconds(15)))
                                {
                                    attempts[id] = DateTime.UtcNow;
                                    Repair(w, monitors);
                                    pending.Remove(id);
                                }
                                else pending[id] = current;
                            }
                            if (DateTime.UtcNow - lastHealth > TimeSpan.FromSeconds(60))
                            {
                                Write(Path.Combine(DataDir, "health.json"), new { Time = DateTime.Now, ProcessId = Process.GetCurrentProcess().Id, Version = "1.0", CandidateWindows = windows.Count, MonitorCount = monitors.Count });
                                lastHealth = DateTime.UtcNow;
                            }
                        }
                        catch (Exception ex) { Log("watch-error", new { Error = ex.Message }); }
                    };
                    Log("started", new { ProcessId = Process.GetCurrentProcess().Id, IntervalMilliseconds = timer.Interval });
                    timer.Start();
                    Application.Run();
                    timer.Stop();
                    Log("stopped", new { ProcessId = Process.GetCurrentProcess().Id });
                }
                mutex.ReleaseMutex();
            }
            return 0;
        }
        catch (Exception ex)
        {
            Write(report, new { Success = false, Error = ex.ToString() });
            return 1;
        }
    }

    internal static bool Repair(WindowState w, List<MonitorState> monitors)
    {
        if (monitors.Count == 0 || w.Cloaked || w.Maximized) return false;
        Rect current = w.EffectiveRect(monitors);
        if (Geometry.Accessible(current, monitors)) return false;
        // Recheck handle ownership immediately before a write, guarding against reused HWNDs.
        uint pid;
        Native.GetWindowThreadProcessId(w.Handle, out pid);
        if (pid != w.ProcessId) return false;
        MonitorState monitor = monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];
        Rect target = Geometry.Fit(current, monitor.Work);
        bool result;
        if (w.Minimized)
        {
            Placement p = w.Placement;
            p.Normal = Geometry.ToWorkspace(target, monitor);
            p.Flags |= 4; // WPF_ASYNCWINDOWPLACEMENT: do not hang behind a busy application.
            p.Show = w.Visible ? 7 : 0; // Keep minimized (without activation), or hidden.
            result = Native.SetWindowPlacement(w.Handle, ref p);
        }
        else
        {
            // Do not show hidden windows, steal focus, or change stacking order.
            result = Native.SetWindowPos(w.Handle, IntPtr.Zero, target.L, target.T, target.Width, target.Height, 0x0010 | 0x0004 | 0x0200 | 0x4000);
        }
        Log("repair", new { ProcessId = w.ProcessId, Window = w.Handle.ToInt64(), Before = current, Target = target, WasMinimized = w.Minimized, WasVisible = w.Visible, RequestSucceeded = result });
        return result;
    }

    internal static void Write(string path, object value) { File.WriteAllText(path, Json.Serialize(value), new UTF8Encoding(false)); }
    private static void Log(string action, object data)
    {
        string path = Path.Combine(DataDir, "guard.log");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 512000) File.Move(path, Path.Combine(DataDir, "guard-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log"));
            File.AppendAllText(path, Json.Serialize(new { Time = DateTime.Now, Action = action, Data = data }) + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { /* A log lock must not affect WeChat. */ }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int L, T, R, B;
    public Rect(int l, int t, int r, int b) { L = l; T = t; R = r; B = b; }
    public int Width { get { return R - L; } }
    public int Height { get { return B - T; } }
    public override string ToString() { return L + "," + T + "," + R + "," + B; }
}
[StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
[StructLayout(LayoutKind.Sequential)] internal struct Placement { public int Size, Flags, Show; public Point Min, Max; public Rect Normal; }
[StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public Rect Bounds, Work; public uint Flags; }
internal sealed class MonitorState { public Rect Bounds, Work; public bool Primary; }
internal sealed class WindowState
{
    public IntPtr Handle;
    public uint ProcessId;
    public bool Visible, Minimized, Maximized, Cloaked;
    public Rect Current;
    public Placement Placement;
    public Rect EffectiveRect(List<MonitorState> monitors)
    {
        if (!Minimized) return Current;
        // WINDOWPLACEMENT stores workspace coordinates, not screen coordinates.
        Rect normal = Placement.Normal;
        var nearest = monitors.OrderBy(m => Geometry.DistanceSquared(normal, m.Bounds)).FirstOrDefault();
        return nearest == null ? normal : Geometry.FromWorkspace(normal, nearest);
    }
    public object Snapshot() { return new { Window = Handle.ToInt64(), ProcessId, Visible, Minimized, Maximized, Cloaked, Current, Normal = Placement.Normal, Show = Placement.Show }; }
}

internal static class Geometry
{
    internal static bool Accessible(Rect r, List<MonitorState> monitors)
    {
        if (r.Width <= 0 || r.Height <= 0) return false;
        // Require a usable section of the title bar on a real monitor, not just one corner.
        return monitors.Any(m => Math.Min(r.R, m.Work.R) - Math.Max(r.L, m.Work.L) >= Math.Min(160, r.Width) &&
            r.T >= m.Work.T - 12 && r.T + 40 <= m.Work.B);
    }
    internal static Rect Fit(Rect r, Rect work)
    {
        int pad = Math.Min(24, Math.Min(work.Width, work.Height) / 20);
        int width = Math.Min(Math.Max(300, r.Width), work.Width - 2 * pad);
        int height = Math.Min(Math.Max(300, r.Height), work.Height - 2 * pad);
        int x = work.L + (work.Width - width) / 2;
        int y = work.T + (work.Height - height) / 2;
        return new Rect(x, y, x + width, y + height);
    }
    internal static Rect ToWorkspace(Rect r, MonitorState m) { int x = m.Work.L - m.Bounds.L, y = m.Work.T - m.Bounds.T; return new Rect(r.L - x, r.T - y, r.R - x, r.B - y); }
    internal static Rect FromWorkspace(Rect r, MonitorState m) { int x = m.Work.L - m.Bounds.L, y = m.Work.T - m.Bounds.T; return new Rect(r.L + x, r.T + y, r.R + x, r.B + y); }
    internal static double DistanceSquared(Rect r, Rect m)
    {
        double x = Math.Max(0, Math.Max((double)m.L - r.R, (double)r.L - m.R));
        double y = Math.Max(0, Math.Max((double)m.T - r.B, (double)r.T - m.B));
        return x * x + y * y;
    }
}

internal static class Native
{
    internal delegate bool WindowCallback(IntPtr h, IntPtr l);
    internal delegate bool MonitorCallback(IntPtr h, IntPtr dc, ref Rect r, IntPtr l);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback c, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr h, WindowCallback c, IntPtr l);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr h, ref Placement p);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPlacement(IntPtr h, ref Placement p);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr h, int command);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint c);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr a, IntPtr b, MonitorCallback c, IntPtr d);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr h, ref MonitorInfo m);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int k);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int n);
    internal static bool MouseButtonDown() { return (GetAsyncKeyState(1) & 0x8000) != 0; }

    internal static List<MonitorState> Monitors()
    {
        var all = new List<MonitorState>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr dc, ref Rect r, IntPtr l)
        {
            var mi = new MonitorInfo(); mi.Size = Marshal.SizeOf(mi);
            if (GetMonitorInfo(h, ref mi) && mi.Work.Width > 100 && mi.Work.Height > 100)
                all.Add(new MonitorState { Bounds = mi.Bounds, Work = mi.Work, Primary = (mi.Flags & 1) != 0 });
            return true;
        }, IntPtr.Zero);
        return all;
    }
    internal static WindowState Read(IntPtr h)
    {
        uint pid; GetWindowThreadProcessId(h, out pid);
        Rect r; var p = new Placement(); p.Size = Marshal.SizeOf(p);
        if (!GetWindowRect(h, out r) || !GetWindowPlacement(h, ref p)) return null;
        int cloak; DwmGetWindowAttribute(h, 14, out cloak, 4);
        return new WindowState { Handle = h, ProcessId = pid, Visible = IsWindowVisible(h), Minimized = IsIconic(h), Maximized = IsZoomed(h), Cloaked = cloak != 0, Current = r, Placement = p };
    }
    internal static List<WindowState> WeChatWindows()
    {
        var pids = new HashSet<int>();
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Tencent\Weixin\Weixin.exe");
        foreach (Process process in Process.GetProcessesByName("Weixin"))
        {
            using (process)
            {
                try { if (process.SessionId == Process.GetCurrentProcess().SessionId && string.Equals(process.MainModule.FileName, expected, StringComparison.OrdinalIgnoreCase)) pids.Add(process.Id); }
                catch { }
            }
        }
        var all = new List<WindowState>();
        if (pids.Count == 0) return all;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (!pids.Contains((int)pid) || GetWindow(h, 4) != IntPtr.Zero) return true;
            var title = new StringBuilder(128); var cls = new StringBuilder(128);
            GetWindowText(h, title, title.Capacity); GetClassName(h, cls, cls.Capacity);
            if (title.ToString() != "微信" && title.ToString() != "Weixin" && title.ToString() != "WeChat") return true;
            if (!cls.ToString().StartsWith("Qt", StringComparison.Ordinal) || !cls.ToString().EndsWith("QWindowIcon", StringComparison.Ordinal)) return true;
            long style = GetWindowLong(h, -16).ToInt64(), exStyle = GetWindowLong(h, -20).ToInt64();
            // Main chat frame is resizable/maximizable; ignore tray, tools, login shells and children.
            if ((style & 0x40000) == 0 || (style & 0x10000) == 0 || (style & 0x40000000) != 0 || (exStyle & 0x80) != 0) return true;
            bool renderChild = false;
            EnumChildWindows(h, delegate(IntPtr child, IntPtr ignored)
            {
                var c = new StringBuilder(128); GetClassName(child, c, c.Capacity);
                if (c.ToString() == "MMUIRenderSubWindowHW") renderChild = true;
                return !renderChild;
            }, IntPtr.Zero);
            if (!renderChild) return true;
            WindowState state = Read(h);
            if (state != null && state.Placement.Normal.Width >= 450 && state.Placement.Normal.Height >= 350) all.Add(state);
            return true;
        }, IntPtr.Zero);
        return all;
    }
}

internal static class Tests
{
    private static void Check(bool pass, string name, List<string> results) { if (!pass) throw new Exception("Test failed: " + name); results.Add(name); }
    internal static object Run()
    {
        var passed = new List<string>();
        var main = new MonitorState { Bounds = new Rect(0, 0, 1920, 1200), Work = new Rect(0, 0, 1920, 1128), Primary = true };
        var monitors = new List<MonitorState> { main };
        // Synthetic fixtures only: no real user monitor/window coordinates.
        Rect old = new Rect(200, 1600, 1200, 2400);
        Check(!Geometry.Accessible(old, monitors), "detached-display-coordinates-detected", passed);
        Rect fit = Geometry.Fit(old, main.Work);
        Check(Geometry.Accessible(fit, monitors) && fit.L >= 0 && fit.T >= 0 && fit.R <= 1920 && fit.B <= 1128, "repair-fits-current-work-area", passed);
        Check(Geometry.Accessible(new Rect(200, 100, 1000, 900), monitors), "valid-current-screen-window-left-alone", passed);
        var left = new MonitorState { Bounds = new Rect(-2560, 0, 0, 1440), Work = new Rect(-2560, 0, 0, 1392) };
        var two = new List<MonitorState> { main, left };
        Rect onLeft = new Rect(-2200, 100, -1200, 900);
        Check(Geometry.Accessible(onLeft, two), "negative-coordinate-attached-monitor-left-alone", passed);
        Check(!Geometry.Accessible(onLeft, monitors), "same-window-detected-after-monitor-disconnect", passed);
        var topTaskbar = new MonitorState { Bounds = main.Bounds, Work = new Rect(0, 48, 1920, 1200) };
        Rect taskbarRect = Geometry.Fit(old, topTaskbar.Work);
        Check(Geometry.FromWorkspace(Geometry.ToWorkspace(taskbarRect, topTaskbar), topTaskbar).ToString() == taskbarRect.ToString(), "workspace-screen-coordinate-round-trip", passed);
        Rect huge = Geometry.Fit(new Rect(4000, 4000, 7840, 6160), main.Work);
        Check(huge.Width <= main.Work.Width && huge.Height <= main.Work.Height, "large-dpi-window-fitted", passed);
        Check(!Geometry.Accessible(new Rect(100, -400, 1000, 600), monitors), "inaccessible-titlebar-detected", passed);

        // Exercise the actual Win32 repair path on our own disposable window, never on user windows.
        using (var form = new Form())
        {
            form.Text = "WeChatWindowGuard self-test";
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            IntPtr handle = form.Handle;
            var actualMonitors = Native.Monitors();
            Rect work = actualMonitors.First(m => m.Primary).Work;
            Native.SetWindowPos(handle, IntPtr.Zero, work.R + 600, work.B + 600, 600, 500, 0x0010 | 0x0004);
            var hidden = Native.Read(handle);
            Check(!hidden.Visible, "test-window-starts-hidden", passed);
            Check(Program.Repair(hidden, actualMonitors), "hidden-window-repair-request-succeeds", passed);
            Application.DoEvents(); Thread.Sleep(100);
            hidden = Native.Read(handle);
            Check(!hidden.Visible && Geometry.Accessible(hidden.EffectiveRect(actualMonitors), actualMonitors), "hidden-window-repaired-without-opening", passed);
            Check(!Program.Repair(hidden, actualMonitors), "repair-is-idempotent", passed);
            Native.SetWindowPos(handle, IntPtr.Zero, work.R + 600, work.B + 600, 600, 500, 0x0010 | 0x0004);
            Native.ShowWindowAsync(handle, 7);
            Application.DoEvents(); Thread.Sleep(100);
            var minimized = Native.Read(handle);
            Check(minimized.Minimized, "test-window-minimized", passed);
            Check(Program.Repair(minimized, actualMonitors), "minimized-window-repair-request-succeeds", passed);
            Application.DoEvents(); Thread.Sleep(100);
            minimized = Native.Read(handle);
            Check(minimized.Minimized && Geometry.Accessible(minimized.EffectiveRect(actualMonitors), actualMonitors), "minimized-window-position-repaired-without-restoring", passed);
        }
        return new { Success = true, Count = passed.Count, Passed = passed };
    }
}
