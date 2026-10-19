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

// Local, current-user window repair. No chat, account, injection, or network APIs.
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
        bool manualBinary = string.Equals(Path.GetFileNameWithoutExtension(Application.ExecutablePath), "WeChatWindowFrameRepair", StringComparison.OrdinalIgnoreCase);
        string mode = args.Length == 0 ? (manualBinary ? "--repair-frame-ui" : "--watch") : args[0];
        // Tests have their own scratch directory and never share installed guard logs.
        if (mode == "--self-test") DataDir = Path.Combine(Path.GetTempPath(), "CodexTasks", "wechat-window-guard-tests-" + Guid.NewGuid().ToString("N"));
        if (mode == "--repair-frame" || mode == "--repair-frame-ui" || mode == "--inspect-frame")
            DataDir = Path.Combine(DataDir, "FrameRepair");
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
            if (mode == "--inspect-frame")
            {
                Write(report, new { Mode = mode, ReadOnly = true, Windows = Native.WeChatWindows(true).Select(w => Native.ReadFrame(w.Handle)).Where(f => f != null).Select(f => f.Snapshot()).ToArray() });
                return 0;
            }
            if (mode == "--repair-frame" || mode == "--repair-frame-ui")
            {
                FrameResult result;
                using (var repairMutex = new Mutex(false, @"Local\WeChatWindowGuard.FrameRepair.v1." + SessionSuffix))
                {
                    bool acquired;
                    try { acquired = repairMutex.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    try { result = acquired ? FrameRepair.Run() : new FrameResult { Code = "window-busy" }; }
                    finally { if (acquired) repairMutex.ReleaseMutex(); }
                }
                Write(report, result);
                if (mode == "--repair-frame-ui")
                    MessageBox.Show(FrameRepair.Describe(result), "微信白边与最大化修复", MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                return result.Success ? 0 : 2;
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
            if (mode == "--repair-frame-ui") MessageBox.Show("修复未完成，错误记录仅保存在本机。请检查微信窗口；不要连续重复点击。", "微信白边与最大化修复", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLong(IntPtr h, int i, IntPtr value);
    [DllImport("kernel32.dll", EntryPoint = "SetLastError")] private static extern void ClearLastError(uint error);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref Point p);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr h, uint message, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr a, IntPtr b, MonitorCallback c, IntPtr d);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr h, ref MonitorInfo m);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int k);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int n);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowRect(IntPtr h, int a, out Rect v, int n);
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
    internal static List<WindowState> WeChatWindows(bool includeDamagedFrame = false)
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
            bool standardFrame = (style & 0x40000) != 0 && (style & 0x10000) != 0;
            if ((!standardFrame && !(includeDamagedFrame && FrameRules.Damaged(unchecked((uint)style), unchecked((uint)exStyle)))) ||
                (style & 0x40000000) != 0 || (exStyle & 0x80) != 0) return true;
            bool renderChild = false;
            EnumChildWindows(h, delegate(IntPtr child, IntPtr ignored)
            {
                var c = new StringBuilder(128); GetClassName(child, c, c.Capacity);
                if (RenderChildMatches(c.ToString(), includeDamagedFrame)) renderChild = true;
                return !renderChild;
            }, IntPtr.Zero);
            if (!renderChild) return true;
            WindowState state = Read(h);
            if (state != null && state.Placement.Normal.Width >= 450 && state.Placement.Normal.Height >= 350) all.Add(state);
            return true;
        }, IntPtr.Zero);
        return all;
    }

    internal static bool RenderChildMatches(string className, bool frameMode)
    {
        // The same verified client can expose either native render-child class.
        // Keep automatic-watch identification unchanged; frame repair still applies
        // its exact executable/module version, main-class, style and DWM gates.
        return className == "MMUIRenderSubWindowHW" || (frameMode && className == "MMUIRenderSubWindow");
    }
    internal static uint Style(IntPtr h, int index) { return unchecked((uint)GetWindowLong(h, index).ToInt64()); }
    internal static bool Owns(IntPtr h, uint expected) { uint actual; GetWindowThreadProcessId(h, out actual); return actual != 0 && actual == expected; }
    internal static bool Responsive(IntPtr h) { IntPtr result; return SendMessageTimeout(h, 0, IntPtr.Zero, IntPtr.Zero, 3, 1000, out result) != IntPtr.Zero; }
    internal static bool ChangeStyle(IntPtr h, int index, uint expected, uint value)
    {
        if (Style(h, index) != expected) return false;
        ClearLastError(0);
        uint previous = unchecked((uint)SetWindowLong(h, index, new IntPtr((long)value)).ToInt64());
        int error = Marshal.GetLastWin32Error();
        if (previous == 0 && error != 0) return false;
        if (previous == expected) return Style(h, index) == value;
        // A concurrent writer won the race: restore its value, never our stale snapshot.
        if (Style(h, index) == value) SetWindowLong(h, index, new IntPtr((long)previous));
        return false;
    }
    internal static bool EnsureStyle(IntPtr h, int index, uint expected, uint value)
    {
        // Windows may update WS_EX_WINDOWEDGE itself when the caption/frame changes.
        return Style(h, index) == value || ChangeStyle(h, index, expected, value);
    }
    internal static bool RefreshFrame(IntPtr h)
    {
        return SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020 | 0x0200 | 0x4000);
    }
    internal static string WeChatVersion(uint pid)
    {
        try
        {
            using (var process = Process.GetProcessById((int)pid))
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Tencent\Weixin");
                if (process.SessionId != Process.GetCurrentProcess().SessionId ||
                    !string.Equals(process.MainModule.FileName, Path.Combine(root, "Weixin.exe"), StringComparison.OrdinalIgnoreCase)) return null;
                string version = process.MainModule.FileVersionInfo.FileVersion;
                // Refuse mixed-version sessions during/after an in-place client update.
                foreach (ProcessModule module in process.Modules)
                    if (string.Equals(module.ModuleName, "Weixin.dll", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(module.FileName, Path.Combine(root, version, "Weixin.dll"), StringComparison.OrdinalIgnoreCase) &&
                        module.FileVersionInfo.FileVersion == version) return version;
            }
        }
        catch { }
        return null;
    }
    internal static FrameState ReadFrame(IntPtr h)
    {
        WindowState window = Read(h);
        Rect client, dwm;
        var point = new Point();
        if (window == null || !GetClientRect(h, out client) || !ClientToScreen(h, ref point)) return null;
        var cls = new StringBuilder(128); GetClassName(h, cls, cls.Capacity);
        int nc; if (DwmGetWindowAttribute(h, 1, out nc, 4) != 0) nc = -1;
        bool hasDwm = DwmGetWindowRect(h, 9, out dwm, Marshal.SizeOf(typeof(Rect))) == 0;
        return new FrameState { Window = window, Style = Style(h, -16), ExStyle = Style(h, -20), ClassName = cls.ToString(),
            Version = WeChatVersion(window.ProcessId), Dpi = GetDpiForWindow(h), NcRendering = nc,
            Client = new Rect(point.X, point.Y, point.X + client.Width, point.Y + client.Height), Dwm = hasDwm ? (Rect?)dwm : null };
    }
}

internal sealed class FrameState
{
    internal WindowState Window;
    internal uint Style, ExStyle, Dpi;
    internal string Version, ClassName;
    internal int NcRendering;
    internal Rect Client;
    internal Rect? Dwm;
    internal object Snapshot() { return new { Window = Window.Snapshot(), Style = "0x" + Style.ToString("X8"), ExStyle = "0x" + ExStyle.ToString("X8"), Version, ClassName, Dpi, NcRendering, Client, Dwm }; }
}
internal static class FrameRules
{
    internal const string VerifiedVersion = "4.1.15.13";
    internal const string VerifiedClass = "Qt51514QWindowIcon";
    internal const uint FrameBits = 0x00C70000, EdgeBit = 0x100, StateBits = 0x31000000;
    internal static bool Profile(FrameState f) { return f != null && f.Version == VerifiedVersion && f.ClassName == VerifiedClass; }
    internal static bool Damaged(uint style, uint ex) { return (style & ~StateBits) == 0x86000000 && ex == 0 && (style & 0x21000000) != 0x21000000; }
    internal static bool Healthy(uint style, uint ex) { return (style & ~StateBits) == 0x86C70000 && ex == EdgeBit && (style & 0x21000000) != 0x21000000; }
    internal static bool KnownDamage(FrameState f)
    {
        return Profile(f) && Damaged(f.Style, f.ExStyle) && f.NcRendering == 0 && f.Dwm.HasValue &&
            f.Dwm.Value.ToString() == f.Window.Current.ToString();
    }
    internal static bool Inside(Rect r, Rect work, int tolerance)
    {
        return r.Width > 0 && r.Height > 0 && r.L >= work.L - tolerance && r.T >= work.T - tolerance && r.R <= work.R + tolerance && r.B <= work.B + tolerance;
    }
    internal static bool AlignedMaximizedClient(Rect r, Rect work)
    {
        return Inside(r, work, 2) && Math.Abs(r.L - work.L) <= 2 && Math.Abs(r.T - work.T) <= 2 &&
            Math.Abs(r.R - work.R) <= 2 && Math.Abs(r.B - work.B) <= 2;
    }
    internal static bool ThinVisibleEdge(FrameState f)
    {
        if (!f.Dwm.HasValue || f.NcRendering != 1) return false;
        Rect d = f.Dwm.Value, c = f.Client;
        return Math.Abs(c.L - d.L) <= 2 && Math.Abs(c.T - d.T) <= 2 && Math.Abs(d.R - c.R) <= 2 && Math.Abs(d.B - c.B) <= 2;
    }
    internal static bool NormalFrameMetrics(FrameState f, Rect work)
    {
        // Intact style bits alone do not prove a consistent window state. A
        // maximized flag can disagree with the actual visible bounds.
        return Profile(f) && f.Window != null && work.Width > 0 && work.Height > 0 &&
            f.Window.Visible && !f.Window.Minimized && !f.Window.Cloaked &&
            Healthy(f.Style, f.ExStyle) && ThinVisibleEdge(f) &&
            (!f.Window.Maximized || AlignedMaximizedClient(f.Client, work));
    }
    internal static bool FitMaximizedClient(Rect outer, Rect client, Rect work, out Rect target)
    {
        int l = client.L - outer.L, t = client.T - outer.T, r = outer.R - client.R, b = outer.B - client.B;
        target = outer;
        if (work.Width <= 0 || work.Height <= 0 || l < 0 || t < 0 || r < 0 || b < 0 || l > 128 || t > 128 || r > 128 || b > 128) return false;
        target = new Rect(work.L - l, work.T - t, work.R + r, work.B + b);
        return true;
    }
}
internal sealed class FrameResult
{
    public bool Success, Changed, RollbackSucceeded;
    public string Code;
    public object Before, After;
}
internal static class FrameRepair
{
    internal static FrameResult Run()
    {
        var candidates = Native.WeChatWindows(true);
        if (candidates.Count != 1) return new FrameResult { Code = candidates.Count == 0 ? "no-unique-chat-window" : "ambiguous-chat-windows" };
        FrameState before = Native.ReadFrame(candidates[0].Handle);
        var result = new FrameResult { Before = before == null ? null : before.Snapshot() };
        if (!FrameRules.Profile(before)) { result.Code = "unsupported-version-or-class"; return result; }
        if (!before.Window.Visible || before.Window.Minimized || before.Window.Cloaked)
        { result.Code = "open-chat-window-first"; return result; }
        bool intactFrame = FrameRules.Healthy(before.Style, before.ExStyle) && FrameRules.ThinVisibleEdge(before);
        if (!intactFrame && !FrameRules.KnownDamage(before)) { result.Code = "unrecognized-style"; return result; }
        if (Native.MouseButtonDown() || !Native.Responsive(before.Window.Handle))
        { result.Code = "window-busy"; return result; }
        var monitors = Native.Monitors();
        MonitorState monitor = monitors.OrderBy(m => Geometry.DistanceSquared(before.Window.Current, m.Bounds))
            .ThenByDescending(m => Intersection(before.Window.Current, m.Bounds)).FirstOrDefault();
        if (monitor == null || monitor.Work.Width <= 0 || monitor.Work.Height <= 0)
        { result.Code = "no-monitor"; return result; }
        if (intactFrame)
        {
            if (!FrameRules.NormalFrameMetrics(before, monitor.Work))
            { result.Code = "maximized-state-size-mismatch"; return result; }
            result.Success = true; result.Code = "already-normal"; return result;
        }
        // Refresh identity and state immediately before changing anything.
        var live = Native.WeChatWindows(true);
        FrameState fresh = Native.ReadFrame(before.Window.Handle);
        if (live.Count != 1 || live[0].Handle != before.Window.Handle || !FrameRules.Profile(fresh) ||
            fresh.Window.ProcessId != before.Window.ProcessId || fresh.Style != before.Style || fresh.ExStyle != before.ExStyle ||
            fresh.Window.Current.ToString() != before.Window.Current.ToString() || !FrameRules.KnownDamage(fresh) ||
            !fresh.Window.Visible || fresh.Window.Minimized || fresh.Window.Cloaked || Native.MouseButtonDown() ||
            !SameMonitors(monitors, Native.Monitors()))
        { result.Code = "window-changed-during-check"; return result; }
        uint restoredStyle = before.Style | FrameRules.FrameBits, restoredEx = before.ExStyle | FrameRules.EdgeBit;
        bool started = false;
        Rect? fittedRect = null;
        result.Code = "operation-interrupted";
        try
        {
            if (!Native.Owns(before.Window.Handle, before.Window.ProcessId))
            { result.Code = "window-owner-changed"; return result; }
            // Include partial writes in rollback, even when immediate readback fails.
            started = true;
            if (!Native.ChangeStyle(before.Window.Handle, -16, before.Style, restoredStyle))
            { result.Code = "style-write-refused"; return result; }
            if (!Native.Owns(before.Window.Handle, before.Window.ProcessId) ||
                !Native.EnsureStyle(before.Window.Handle, -20, before.ExStyle, restoredEx) || !Native.RefreshFrame(before.Window.Handle))
            { result.Code = "frame-refresh-failed"; return result; }
            FrameState after = Settle(before.Window.Handle, restoredStyle, restoredEx);
            if (after == null || after.Window.ProcessId != before.Window.ProcessId ||
                after.Window.Maximized != before.Window.Maximized || !after.Window.Visible || after.Window.Minimized || after.Window.Cloaked)
            { result.Code = "frame-readback-failed"; return result; }
            if (before.Window.Maximized && !FrameRules.AlignedMaximizedClient(after.Client, monitor.Work))
            {
                Rect target;
                if (!FrameRules.FitMaximizedClient(after.Window.Current, after.Client, monitor.Work, out target) ||
                    Native.MouseButtonDown() || !SameMonitors(monitors, Native.Monitors()) ||
                    !Native.Owns(before.Window.Handle, before.Window.ProcessId) ||
                    Native.Style(before.Window.Handle, -16) != restoredStyle || Native.Style(before.Window.Handle, -20) != restoredEx)
                { result.Code = "maximized-fit-failed"; return result; }
                fittedRect = target;
                if (!Native.SetWindowPos(before.Window.Handle, IntPtr.Zero, target.L, target.T, target.Width, target.Height, 0x0010 | 0x0004 | 0x0200 | 0x4000))
                { result.Code = "maximized-fit-failed"; return result; }
                Thread.Sleep(150);
                after = Settle(before.Window.Handle, restoredStyle, restoredEx);
            }
            if (after == null || !FrameRules.ThinVisibleEdge(after) ||
                after.Window.ProcessId != before.Window.ProcessId || after.Window.Maximized != before.Window.Maximized ||
                !after.Window.Visible || after.Window.Minimized || after.Window.Cloaked ||
                !SameMonitors(monitors, Native.Monitors()) ||
                (before.Window.Maximized && !FrameRules.AlignedMaximizedClient(after.Client, monitor.Work)))
            { result.Code = "geometry-readback-failed"; return result; }
            result.Success = true; result.Changed = true; result.Code = "frame-restored-and-verified"; result.After = after.Snapshot();
            return result;
        }
        finally
        {
            if (started && !result.Success)
            {
                result.RollbackSucceeded = Rollback(before, restoredStyle, restoredEx, fittedRect);
                result.Changed = !result.RollbackSucceeded;
                var final = Native.ReadFrame(before.Window.Handle);
                result.After = final == null ? null : final.Snapshot();
            }
        }
    }
    private static double Intersection(Rect a, Rect b) { return (double)Math.Max(0, Math.Min(a.R, b.R) - Math.Max(a.L, b.L)) * Math.Max(0, Math.Min(a.B, b.B) - Math.Max(a.T, b.T)); }
    private static bool SameMonitors(List<MonitorState> a, List<MonitorState> b)
    {
        return a.Select(m => m.Bounds + "/" + m.Work).OrderBy(s => s).SequenceEqual(b.Select(m => m.Bounds + "/" + m.Work).OrderBy(s => s));
    }
    private static FrameState Settle(IntPtr handle, uint style, uint ex)
    {
        FrameState state = null;
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(50);
            state = Native.ReadFrame(handle);
            if (state == null) return null;
            if (state.Style != style || state.ExStyle != ex) return null;
            // Read usable frame insets before fitting a maximized window. DWM's visible
            // edge can still be clipped until the incorrect maximized bounds are fixed.
            if (state.NcRendering == 1 && state.Dwm.HasValue) return state;
        }
        return null;
    }
    private static bool Rollback(FrameState before, uint style, uint ex, Rect? fittedRect)
    {
        IntPtr h = before.Window.Handle;
        if (!Native.Owns(h, before.Window.ProcessId)) return false;
        uint currentStyle = Native.Style(h, -16);
        uint currentEx = Native.Style(h, -20);
        if ((currentStyle != before.Style && currentStyle != style) || (currentEx != before.ExStyle && currentEx != ex)) return false;
        WindowState current = Native.Read(h);
        if (current == null || current.Minimized != before.Window.Minimized || current.Maximized != before.Window.Maximized ||
            current.Visible != before.Window.Visible || current.Cloaked != before.Window.Cloaked || Native.MouseButtonDown()) return false;
        // Do not overwrite a concurrent manual move. Only undo our own explicit fit.
        if (fittedRect.HasValue && current.Current.ToString() != fittedRect.Value.ToString() &&
            current.Current.ToString() != before.Window.Current.ToString()) return false;
        bool restored = currentStyle == before.Style || Native.ChangeStyle(h, -16, style, before.Style);
        restored = restored && Native.EnsureStyle(h, -20, ex, before.ExStyle) && Native.RefreshFrame(h);
        if (!restored) return false;
        Rect r = before.Window.Current;
        if (fittedRect.HasValue && Native.Owns(h, before.Window.ProcessId))
            Native.SetWindowPos(h, IntPtr.Zero, r.L, r.T, r.Width, r.Height, 0x0010 | 0x0004 | 0x0200 | 0x4000);
        Thread.Sleep(150);
        WindowState after = Native.Read(h);
        return Native.Owns(h, before.Window.ProcessId) && Native.Style(h, -16) == before.Style && Native.Style(h, -20) == before.ExStyle &&
            after != null && after.Current.ToString() == r.ToString() && after.Maximized == before.Window.Maximized &&
            after.Minimized == before.Window.Minimized && after.Visible == before.Window.Visible && after.Cloaked == before.Window.Cloaked;
    }
    internal static string Describe(FrameResult r)
    {
        if (r.Code == "already-normal") return "未检测到本工具识别的边框或最大化边界异常，本次未修改窗口。\n\n这只是技术参数检查，不代表实际屏幕上一定没有白边；窗口截图也不能代替实际显示检查。";
        if (r.Code == "maximized-state-size-mismatch") return "微信仍标记为最大化，但实际显示尺寸与所在屏幕工作区不一致。\n\n请点一次右上角的“还原”，先恢复为正常小窗，再查看实际屏幕上的边框是否消失。本次未修改边框或尺寸，也没有退出微信。";
        if (r.Success) return "已恢复微信窗口边框，并通过样式和边界回读检查。\n\n没有退出微信或清除登录。请再检查小窗和最大化显示；这不是对故障根因的永久修复。";
        if (r.Code == "open-chat-window-first") return "请先打开已登录的微信聊天主窗口，再使用此入口。不要停留在最小化或其他虚拟桌面的状态。";
        if (r.Code == "window-busy") return "微信窗口正忙，或鼠标正在拖动窗口。本次没有修改，请稍后再试。";
        string message = "未完成修复。此入口仅处理已验证的微信 4.1.15.13 窗口样式丢失，不会猜测其他版本或未知状态。\n\n状态：" + r.Code;
        if (r.RollbackSucceeded) message += "\n已恢复操作前的窗口状态。";
        else if (r.Changed) message += "\n未能完整恢复操作前状态，请正常重启微信；不要重复点击。";
        return message + "\n\n本地记录没有上传；不要把包含窗口位置的诊断文件直接公开。";
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
        Check(Native.RenderChildMatches("MMUIRenderSubWindowHW", false) && Native.RenderChildMatches("MMUIRenderSubWindowHW", true), "original-render-child-identification-preserved", passed);
        Check(Native.RenderChildMatches("MMUIRenderSubWindow", true), "verified-alternate-render-child-recognized-in-frame-mode", passed);
        Check(!Native.RenderChildMatches("MMUIRenderSubWindow", false), "alternate-render-child-does-not-expand-auto-watch", passed);
        Check(!Native.RenderChildMatches("UnknownRenderWindow", true) && !Native.RenderChildMatches(null, true), "unknown-render-child-remains-refused", passed);

        var damaged = new FrameState { Version = FrameRules.VerifiedVersion, ClassName = FrameRules.VerifiedClass,
            Window = new WindowState { Current = new Rect(100, 100, 774, 900) }, Style = 0x96000000, ExStyle = 0,
            NcRendering = 0, Client = new Rect(108, 100, 766, 892), Dwm = new Rect(100, 100, 774, 900) };
        Check(FrameRules.KnownDamage(damaged), "verified-damaged-frame-recognized", passed);
        damaged.Version = "0.0.0.0";
        Check(!FrameRules.KnownDamage(damaged), "unknown-version-refused", passed);
        damaged.Version = FrameRules.VerifiedVersion; damaged.ClassName = "OtherWindow";
        Check(!FrameRules.KnownDamage(damaged), "unknown-window-class-refused", passed);
        damaged.ClassName = FrameRules.VerifiedClass; damaged.NcRendering = 1;
        Check(!FrameRules.KnownDamage(damaged), "unrelated-rendering-state-refused", passed);
        damaged.NcRendering = 0; damaged.Dwm = null;
        Check(!FrameRules.KnownDamage(damaged), "missing-dwm-evidence-refused", passed);
        damaged.Dwm = new Rect(107, 100, 767, 893);
        Check(!FrameRules.KnownDamage(damaged), "different-visible-border-refused", passed);
        Check(FrameRules.Damaged(0x97000000, 0), "maximized-damaged-frame-recognized", passed);
        Check(FrameRules.Damaged(0xB6000000, 0), "minimized-state-recognized-for-safe-refusal", passed);
        Check(!FrameRules.Damaged(0xD6000000, 0) && !FrameRules.Damaged(0x96000000, 0x80) && !FrameRules.Damaged(0xB7000000, 0), "children-tools-invalid-state-refused", passed);
        foreach (uint style in new uint[] { 0x86000000, 0x96000000, 0x97000000, 0xB6000000 })
            Check(((style | FrameRules.FrameBits) & FrameRules.StateBits) == (style & FrameRules.StateBits), "frame-restore-preserves-state-" + style.ToString("X8"), passed);
        Check(FrameRules.Healthy(0x96C70000, 0x100) && !FrameRules.Damaged(0x96C70000, 0x100), "normal-frame-is-idempotent", passed);
        Check(!FrameRules.Healthy(0x96CF0000, 0x100) && !FrameRules.Damaged(0x96800000, 0), "unexpected-style-bits-refused", passed);
        var normalFrame = new FrameState { Client = new Rect(108, 100, 766, 892), Dwm = new Rect(107, 100, 767, 893), NcRendering = 1 };
        Check(FrameRules.ThinVisibleEdge(normalFrame), "normal-one-pixel-edge-accepted", passed);
        normalFrame.Dwm = new Rect(100, 100, 774, 900);
        Check(!FrameRules.ThinVisibleEdge(normalFrame), "eight-pixel-white-edge-rejected", passed);
        Rect maxTarget;
        Check(FrameRules.FitMaximizedClient(new Rect(-8, 0, 1928, 1328), new Rect(0, 0, 1920, 1320), main.Work, out maxTarget) &&
            maxTarget.ToString() == "-8,0,1928,1136", "maximized-overflow-fit-accounts-for-frame-insets", passed);
        Check(FrameRules.FitMaximizedClient(new Rect(-2568, -8, 8, 1488), new Rect(-2560, 0, 0, 1480), left.Work, out maxTarget) &&
            maxTarget.ToString() == "-2568,-8,8,1400", "maximized-negative-monitor-fit", passed);
        Check(!FrameRules.FitMaximizedClient(new Rect(0, 0, 800, 800), new Rect(-1, 0, 800, 800), main.Work, out maxTarget), "invalid-client-insets-refused", passed);
        Check(!FrameRules.Inside(new Rect(0, 0, 1920, 1320), main.Work, 2) &&
            FrameRules.AlignedMaximizedClient(new Rect(0, 0, 1920, 1128), main.Work) &&
            !FrameRules.AlignedMaximizedClient(new Rect(100, 100, 900, 900), main.Work), "maximized-client-work-area-validation", passed);

        var intact = new FrameState { Version = FrameRules.VerifiedVersion, ClassName = FrameRules.VerifiedClass,
            Window = new WindowState { Visible = true }, Style = 0x96C70000, ExStyle = 0x100, NcRendering = 1,
            Client = new Rect(200, 100, 1200, 800), Dwm = new Rect(198, 100, 1202, 802) };
        Check(FrameRules.NormalFrameMetrics(intact, main.Work), "intact-restored-window-metrics-accepted", passed);
        intact.Window.Maximized = true; intact.Style = 0x97C70000;
        Check(!FrameRules.NormalFrameMetrics(intact, main.Work), "small-window-with-maximized-flag-not-reported-normal", passed);
        intact.Client = main.Work; intact.Dwm = main.Work;
        Check(FrameRules.NormalFrameMetrics(intact, main.Work), "intact-maximized-window-must-align-with-work-area", passed);
        intact.Client = new Rect(0, 0, 1920, 1320); intact.Dwm = intact.Client;
        Check(!FrameRules.NormalFrameMetrics(intact, main.Work), "intact-style-with-maximized-overflow-not-reported-normal", passed);
        intact.Client = main.Work; intact.Dwm = main.Work; intact.Window.Visible = false;
        Check(!FrameRules.NormalFrameMetrics(intact, main.Work), "hidden-intact-window-not-reported-normal", passed);
        intact.Window.Visible = true; intact.Window.Minimized = true;
        Check(!FrameRules.NormalFrameMetrics(intact, main.Work), "minimized-intact-window-not-reported-normal", passed);
        intact.Window.Minimized = false; intact.Window.Cloaked = true;
        Check(!FrameRules.NormalFrameMetrics(intact, main.Work), "cloaked-intact-window-not-reported-normal", passed);
        intact.Window.Cloaked = false; intact.Version = "0.0.0.0";
        Check(!FrameRules.NormalFrameMetrics(intact, main.Work), "unknown-profile-not-reported-normal", passed);
        intact.Version = FrameRules.VerifiedVersion;
        Check(!FrameRules.NormalFrameMetrics(null, main.Work) && !FrameRules.NormalFrameMetrics(intact, new Rect()), "missing-metadata-or-work-area-not-reported-normal", passed);
        Check(FrameRepair.Describe(new FrameResult { Code = "maximized-state-size-mismatch" }).Contains("还原"), "state-size-mismatch-has-explicit-manual-restore-guidance", passed);

        // Exercise the actual Win32 repair path on our own disposable window, never on user windows.
        using (var form = new Form())
        {
            form.Text = "WeChatWindowGuard self-test";
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            IntPtr handle = form.Handle;
            uint originalStyle = Native.Style(handle, -16), originalEx = Native.Style(handle, -20);
            uint testStyle = originalStyle & ~FrameRules.FrameBits, testEx = originalEx & ~FrameRules.EdgeBit;
            IntPtr originalForeground = Native.GetForegroundWindow();
            try
            {
                bool styleChanged = Native.ChangeStyle(handle, -16, originalStyle, testStyle);
                bool exChanged = Native.EnsureStyle(handle, -20, originalEx, testEx);
                bool refreshed = Native.RefreshFrame(handle);
                if (!styleChanged || !exChanged || !refreshed)
                    throw new Exception("Disposable frame test: style=" + styleChanged + ", ex=" + exChanged + ", refresh=" + refreshed +
                        ", before=" + originalStyle.ToString("X8") + "/" + originalEx.ToString("X8") +
                        ", target=" + testStyle.ToString("X8") + "/" + testEx.ToString("X8") +
                        ", current=" + Native.Style(handle, -16).ToString("X8") + "/" + Native.Style(handle, -20).ToString("X8"));
                Check(true, "own-window-frame-change-succeeds", passed);
                Check(!Native.Read(handle).Visible, "frame-change-does-not-show-hidden-test-window", passed);
                Check(!Native.ChangeStyle(handle, -16, originalStyle, testStyle) && Native.Style(handle, -16) == testStyle, "stale-style-write-refused", passed);
                Check(Native.ChangeStyle(handle, -16, testStyle, originalStyle) && Native.EnsureStyle(handle, -20, testEx, originalEx) && Native.RefreshFrame(handle), "own-window-frame-restoration-succeeds", passed);
                Check(Native.Style(handle, -16) == originalStyle && Native.Style(handle, -20) == originalEx, "own-window-original-style-readback", passed);
                Check(!Native.Read(handle).Visible && Native.GetForegroundWindow() == originalForeground, "frame-restoration-does-not-show-or-activate-test-window", passed);
                Check(Native.WeChatVersion((uint)Process.GetCurrentProcess().Id) == null, "test-process-cannot-pass-wechat-identity-gate", passed);
            }
            finally
            {
                Native.ChangeStyle(handle, -16, Native.Style(handle, -16), originalStyle);
                Native.EnsureStyle(handle, -20, Native.Style(handle, -20), originalEx);
                Native.RefreshFrame(handle);
            }
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
