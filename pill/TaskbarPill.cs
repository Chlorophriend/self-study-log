// TaskbarPill: the Self-Study Log companion for Windows.
//  1. Serves the web app from the "web" folder at http://localhost:47613, so the log opens offline with no install.
//  2. While a session runs, draws a small clock on the taskbar, just left of the tray icons. Only the time: no
//     window frame, never takes focus, hidden when no session runs or a full-screen app is in front.
// The app sends the session state to /api/state; the pill counts the time itself between updates.
// Build with build.cmd (it uses the C# compiler that ships with Windows). Right-click the pill to quit.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Program {
    public const int Port = 47613;

    [STAThread]
    static void Main(string[] args) {
        bool first;
        using (var m = new Mutex(true, "SelfStudyLog.TaskbarPill", out first)) {
            bool quiet = args.Length > 0 && args[0] == "--quiet";
            if (!first) { if (!quiet) OpenApp(); return; }   // already running: just open the log
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            var pill = new Pill(FindWebRoot());
            if (!pill.StartServer()) {
                MessageBox.Show("Port " + Port + " is already in use, so the Self-Study Log companion cannot start.", "Self-Study Log");
                return;
            }
            if (!quiet) OpenApp();
            Application.Run(pill);
        }
    }

    public static void OpenApp() {
        try { System.Diagnostics.Process.Start("http://localhost:" + Port + "/"); } catch { }
    }

    // The web folder sits next to the pill folder: <root>\pill\TaskbarPill.exe and <root>\web\index.html.
    static string FindWebRoot() {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (string c in new[] { Path.Combine(exeDir, "web"), Path.Combine(exeDir, "..", "web") }) {
            string f = Path.GetFullPath(c);
            if (Directory.Exists(f)) return f.TrimEnd(Path.DirectorySeparatorChar);
        }
        return Path.GetFullPath(Path.Combine(exeDir, "..", "web")).TrimEnd(Path.DirectorySeparatorChar);
    }
}

static class Native {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
    public static readonly IntPtr TOPMOST = new IntPtr(-1);
    public const uint NOSIZE = 0x1, NOMOVE = 0x2, NOACTIVATE = 0x10, SHOWWINDOW = 0x40;
}

class Pill : Form {
    readonly string webRoot;
    readonly object gate = new object();
    bool on; long start, pausedAt, pausedMs; double planMin = -1; string mode = "track";
    string localMode;                         // set by clicking the pill; cleared when the app switches mode
    readonly float scale;
    readonly Font timeFont = new Font("Consolas", 11f, FontStyle.Bold);
    readonly Font labelFont = new Font("Segoe UI", 7.5f);
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
    readonly List<string> origins = new List<string>();
    int placeCount;
    TcpListener l4, l6;

    static readonly Color Orange = Color.FromArgb(227, 165, 72), Red = Color.FromArgb(238, 123, 110), Grey = Color.FromArgb(147, 163, 155);

    public Pill(string webRoot) {
        this.webRoot = webRoot;
        scale = Native.GetDpiForSystem() / 96f;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(42, 35, 21);
        Size = new Size(S(100), S(30));
        Location = new Point(-32000, -32000);
        Text = "Self-Study Log timer";
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Self-Study Log", null, delegate { Program.OpenApp(); });
        menu.Items.Add("Show time passed", null, delegate { localMode = "track"; Invalidate(); });
        menu.Items.Add("Show time left", null, delegate { localMode = "press"; Invalidate(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit taskbar timer", null, delegate { Close(); });
        ContextMenuStrip = menu;
        tick.Interval = 1000;
        tick.Tick += delegate { OnTick(); };
        tick.Start();
        LoadOrigins();
    }

    int S(float v) { return (int)Math.Round(v * scale); }

    // A tool window that never takes focus and stays above other windows.
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000 | 0x8; return cp; }
    }
    protected override void OnShown(EventArgs e) { base.OnShown(e); bool show; lock (gate) show = on; if (!show) Hide(); }

    // ---------- what the clock shows ----------
    string CurrentMode() { lock (gate) return localMode ?? mode; }
    void Compute(out string txt, out string label, out Color col, out bool paused) {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long st, pa, pm; double plan; string m;
        lock (gate) { st = start; pa = pausedAt; pm = pausedMs; plan = planMin; m = localMode ?? mode; }
        long el = (pa > 0 ? pa : now) - st - pm; if (el < 0) el = 0;
        bool hasPlan = plan >= 0;
        long left = hasPlan ? (long)(plan * 60000) - el : 0;
        bool over = hasPlan && left < 0;
        paused = pa > 0;
        if (m == "press" && hasPlan) { txt = (over ? "+" : "") + Clock(over ? -left : left); label = over ? "over" : "left"; }
        else { txt = Clock(el); label = ""; }
        col = paused ? Grey : over ? Red : Orange;
    }
    static string Clock(long ms) {
        long t = ms / 1000, h = t / 3600, mi = (t / 60) % 60, s = t % 60;
        return (h > 0 ? h + ":" : "") + mi.ToString("00") + ":" + s.ToString("00");
    }

    // ---------- every second: show or hide, size to the text, keep on top ----------
    void OnTick() {
        bool show; lock (gate) show = on;
        int qs;
        bool fullScreen = Native.SHQueryUserNotificationState(out qs) == 0 && (qs == 2 || qs == 3 || qs == 4); // busy, full-screen game, presentation
        if (!show || fullScreen) { if (Visible) Hide(); return; }
        if (!Visible) { Show(); placeCount = 0; }
        string t, l; Color c; bool p;
        Compute(out t, out l, out c, out p);
        using (Graphics g = CreateGraphics()) {
            float w = S(7) + S(6) + g.MeasureString(t, timeFont, PointF.Empty, StringFormat.GenericTypographic).Width
                    + (l.Length > 0 ? S(4) + g.MeasureString(l, labelFont, PointF.Empty, StringFormat.GenericTypographic).Width : 0) + S(24);
            int want = Math.Max(S(80), (int)Math.Ceiling(w));
            if (Math.Abs(want - Width) > S(3)) { Width = want; placeCount = 0; }
        }
        if (placeCount++ % 5 == 0) Place();
        else Native.SetWindowPos(Handle, Native.TOPMOST, 0, 0, 0, 0, Native.NOMOVE | Native.NOSIZE | Native.NOACTIVATE);
        Invalidate();
    }

    // On the taskbar, just left of the tray icons and clock; with a side or hidden taskbar, the bottom-right corner.
    void Place() {
        Screen scr = Screen.PrimaryScreen;
        IntPtr tb = Native.FindWindow("Shell_TrayWnd", null);
        Native.RECT tr;
        int x, y;
        if (tb != IntPtr.Zero && Native.GetWindowRect(tb, out tr) && tr.Bottom >= scr.Bounds.Bottom && tr.Top < scr.Bounds.Bottom
            && (tr.Right - tr.Left) > (tr.Bottom - tr.Top)) {
            int trayLeft = tr.Right - S(400);
            IntPtr tray = Native.FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
            Native.RECT nr;
            if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out nr)) trayLeft = nr.Left;
            x = trayLeft - Width - S(10);
            y = tr.Top + (tr.Bottom - tr.Top - Height) / 2;
        } else {
            Rectangle wa = scr.WorkingArea;
            x = wa.Right - Width - S(12); y = wa.Bottom - Height - S(12);
        }
        Native.SetWindowPos(Handle, Native.TOPMOST, x, y, 0, 0, Native.NOSIZE | Native.NOACTIVATE | Native.SHOWWINDOW);
    }

    // ---------- drawing ----------
    static GraphicsPath RoundPath(RectangleF r, float rad) {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }
    protected override void OnResize(EventArgs e) {
        base.OnResize(e);
        using (GraphicsPath p = RoundPath(new RectangleF(0, 0, Width, Height), Height / 2f)) Region = new Region(p);
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        string t, l; Color c; bool paused;
        Compute(out t, out l, out c, out paused);
        using (GraphicsPath path = RoundPath(new RectangleF(1, 1, Width - 2.5f, Height - 2.5f), (Height - 2.5f) / 2f))
        using (var pen = new Pen(paused ? Grey : Orange, Math.Max(1.5f, 1.5f * scale))) g.DrawPath(pen, path);
        float dot = S(7), gap = S(6);
        SizeF ts = g.MeasureString(t, timeFont, PointF.Empty, StringFormat.GenericTypographic);
        SizeF ls = l.Length > 0 ? g.MeasureString(l, labelFont, PointF.Empty, StringFormat.GenericTypographic) : SizeF.Empty;
        float total = dot + gap + ts.Width + (l.Length > 0 ? S(4) + ls.Width : 0);
        float x = (Width - total) / 2f, cy = Height / 2f;
        using (var b = new SolidBrush(paused ? Grey : Orange)) g.FillEllipse(b, x, cy - dot / 2f, dot, dot);
        x += dot + gap;
        using (var b = new SolidBrush(c)) g.DrawString(t, timeFont, b, x, cy - ts.Height / 2f, StringFormat.GenericTypographic);
        if (l.Length > 0) using (var b = new SolidBrush(Grey)) g.DrawString(l, labelFont, b, x + ts.Width + S(4), cy - ls.Height / 2f + S(1), StringFormat.GenericTypographic);
    }
    protected override void OnMouseUp(MouseEventArgs e) {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        lock (gate) { if (planMin < 0) return; localMode = (localMode ?? mode) == "press" ? "track" : "press"; }
        Invalidate();
    }
    protected override void OnFormClosed(FormClosedEventArgs e) {
        try { l4.Stop(); } catch { }
        try { if (l6 != null) l6.Stop(); } catch { }
        base.OnFormClosed(e);
    }

    // ---------- the local web server (this computer only) ----------
    // Other web addresses allowed to send the session state, one per line, in pill-origins.txt next to the exe
    // (for example your deployed copy: https://your-project.web.app).
    void LoadOrigins() {
        try {
            string f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pill-origins.txt");
            if (!File.Exists(f)) return;
            foreach (string line in File.ReadAllLines(f)) { string o = line.Trim().TrimEnd('/'); if (o.Length > 0 && !o.StartsWith("#")) origins.Add(o); }
        } catch { }
    }
    bool AllowedOrigin(string o) {
        if (o == null) return true;
        return o.StartsWith("http://localhost:") || o.StartsWith("http://127.0.0.1:") || origins.Contains(o);
    }

    public bool StartServer() {
        try { l4 = new TcpListener(IPAddress.Loopback, Program.Port); l4.Start(); } catch { return false; }
        try { l6 = new TcpListener(IPAddress.IPv6Loopback, Program.Port); l6.Start(); } catch { l6 = null; }
        Accept(l4); if (l6 != null) Accept(l6);
        return true;
    }
    void Accept(TcpListener l) {
        var t = new Thread(delegate () {
            while (true) {
                TcpClient c;
                try { c = l.AcceptTcpClient(); } catch { return; }
                ThreadPool.QueueUserWorkItem(delegate { Serve(c); });
            }
        });
        t.IsBackground = true; t.Start();
    }
    static int Find(byte[] b, int len, byte[] pat) {
        for (int i = 0; i + pat.Length <= len; i++) { int j = 0; while (j < pat.Length && b[i + j] == pat[j]) j++; if (j == pat.Length) return i; }
        return -1;
    }
    void Serve(TcpClient client) {
        using (client) {
            try {
                client.ReceiveTimeout = 5000;
                NetworkStream ns = client.GetStream();
                var buf = new MemoryStream(); var tmp = new byte[8192]; int end = -1; byte[] crlf2 = Encoding.ASCII.GetBytes("\r\n\r\n");
                while (end < 0 && buf.Length < 65536) { int n = ns.Read(tmp, 0, tmp.Length); if (n <= 0) return; buf.Write(tmp, 0, n); end = Find(buf.GetBuffer(), (int)buf.Length, crlf2); }
                if (end < 0) return;
                string[] lines = Encoding.ASCII.GetString(buf.GetBuffer(), 0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] req = lines[0].Split(' ');
                if (req.Length < 2) return;
                var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++) { int c = lines[i].IndexOf(':'); if (c > 0) h[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim(); }
                int len = 0; string v;
                if (h.TryGetValue("Content-Length", out v)) int.TryParse(v, out len);
                if (len < 0 || len > 65536) return;
                var body = new byte[len]; int have = Math.Min((int)buf.Length - (end + 4), len);
                Array.Copy(buf.GetBuffer(), end + 4, body, 0, have);
                while (have < len) { int n = ns.Read(body, have, len - have); if (n <= 0) break; have += n; }
                string origin, host;
                h.TryGetValue("Origin", out origin);
                h.TryGetValue("Host", out host);
                Respond(ns, req[0], req[1], host, origin, Encoding.UTF8.GetString(body, 0, have));
            } catch { }
        }
    }
    void Respond(Stream ns, string method, string path, string host, string origin, string body) {
        // Only answer requests addressed to this computer by name, which blocks DNS-rebinding tricks.
        string p = ":" + Program.Port;
        if (host != "localhost" + p && host != "127.0.0.1" + p && host != "[::1]" + p) { Send(ns, 403, "text/plain", Encoding.UTF8.GetBytes("forbidden"), ""); return; }
        int q = path.IndexOf('?'); if (q >= 0) path = path.Substring(0, q);
        if (path.StartsWith("/api/")) {
            if (!AllowedOrigin(origin)) { Send(ns, 403, "text/plain", Encoding.UTF8.GetBytes("forbidden"), ""); return; }
            string cors = origin == null ? "" : "Access-Control-Allow-Origin: " + origin + "\r\nVary: Origin\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type\r\nAccess-Control-Allow-Private-Network: true\r\n";
            byte[] ok = Encoding.UTF8.GetBytes("{\"ok\":true}");
            if (method == "OPTIONS") { Send(ns, 204, "text/plain", new byte[0], cors); return; }
            if (path == "/api/ping") { Send(ns, 200, "application/json", ok, cors + "Cache-Control: no-store\r\n"); return; }
            if (path == "/api/state" && method == "POST") { ApplyState(body); Send(ns, 200, "application/json", ok, cors); return; }
            Send(ns, 404, "text/plain", Encoding.UTF8.GetBytes("not found"), cors); return;
        }
        if (method != "GET") { Send(ns, 405, "text/plain", Encoding.UTF8.GetBytes("method not allowed"), ""); return; }
        string rel = Uri.UnescapeDataString(path).TrimStart('/');
        if (rel.Length == 0) rel = "index.html";
        string full;
        try { full = Path.GetFullPath(Path.Combine(webRoot, rel.Replace('/', Path.DirectorySeparatorChar))); } catch { full = ""; }
        if (!full.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) {
            Send(ns, 404, "text/plain", Encoding.UTF8.GetBytes("not found"), ""); return;
        }
        Send(ns, 200, Mime(full), File.ReadAllBytes(full), "Cache-Control: no-cache\r\n");
    }
    static string Mime(string f) {
        switch (Path.GetExtension(f).ToLowerInvariant()) {
            case ".html": return "text/html; charset=utf-8";
            case ".js": return "text/javascript; charset=utf-8";
            case ".css": return "text/css; charset=utf-8";
            case ".json": return "application/json";
            case ".webmanifest": return "application/manifest+json";
            case ".svg": return "image/svg+xml";
            case ".png": return "image/png";
            case ".ico": return "image/x-icon";
            default: return "application/octet-stream";
        }
    }
    static void Send(Stream ns, int code, string type, byte[] data, string extra) {
        string reason = code == 200 ? "OK" : code == 204 ? "No Content" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : "Method Not Allowed";
        string head = "HTTP/1.1 " + code + " " + reason + "\r\nContent-Type: " + type + "\r\nContent-Length: " + data.Length + "\r\n" + extra
                    + "X-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        byte[] hb = Encoding.ASCII.GetBytes(head);
        ns.Write(hb, 0, hb.Length);
        if (data.Length > 0) ns.Write(data, 0, data.Length);
        ns.Flush();
    }

    // The app sends {on, start, pausedAt, pausedMs, planMin, mode}; times are milliseconds since 1970.
    void ApplyState(string json) {
        Dictionary<string, object> d;
        try { d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); } catch { return; }
        if (d == null) return;
        lock (gate) {
            object v;
            on = d.TryGetValue("on", out v) && v is bool && (bool)v;
            start = Long(d, "start"); pausedAt = Long(d, "pausedAt"); pausedMs = Long(d, "pausedMs");
            planMin = d.TryGetValue("planMin", out v) && v != null ? Convert.ToDouble(v) : -1;
            string nm = d.TryGetValue("mode", out v) && v != null ? v.ToString() : "track";
            if (nm != mode) localMode = null;
            mode = nm;
        }
        try { if (IsHandleCreated) BeginInvoke((MethodInvoker)OnTick); } catch { }
    }
    static long Long(Dictionary<string, object> d, string k) {
        object v;
        if (!d.TryGetValue(k, out v) || v == null) return 0;
        try { return Convert.ToInt64(v); } catch { return 0; }
    }
}
