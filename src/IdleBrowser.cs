// Idle Browser - an always-on-top, resizable, see-through browser window
// meant to sit over a game running in borderless/windowed-fullscreen mode.
//
//   Ctrl+B        show / hide (does not steal keyboard focus from the game)
//   Ctrl+Shift+B  toggle click-through (mouse clicks pass to the game)
//
// Written for the C# 5 compiler that ships with Windows (.NET Framework 4.x).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace IdleBrowser
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);

        public static readonly int ShowMessage = RegisterWindowMessage("IdleBrowser.Show");

        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "IdleBrowser.SingleInstance", out created))
            {
                if (!created)
                {
                    // Already running: ask the existing instance to show itself.
                    PostMessage(new IntPtr(0xFFFF), ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new IdleBrowserForm());
            }
        }
    }

    class IdleBrowserForm : Form
    {
        // ---- Win32 -------------------------------------------------------
        const int WM_HOTKEY = 0x0312, WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1;
        const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                  HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        const int GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20,
                  WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80;
        const uint LWA_ALPHA = 0x2, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const int HOTKEY_TOGGLE = 1, HOTKEY_GHOST = 2;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // ---- Look --------------------------------------------------------
        static readonly Color FrameColor = Color.FromArgb(22, 18, 14);
        static readonly Color BarColor = Color.FromArgb(34, 28, 20);
        static readonly Color HoverColor = Color.FromArgb(70, 55, 30);
        static readonly Color GoldColor = Color.FromArgb(170, 135, 60);
        static readonly Color GhostColor = Color.FromArgb(150, 90, 200);
        static readonly Color TextColor = Color.FromArgb(255, 214, 120);
        static readonly Color UrlBackColor = Color.FromArgb(12, 10, 8);
        const int Grip = 6;           // resize border thickness
        const int MinOpacity = 15;

        // ---- State -------------------------------------------------------
        readonly string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IdleBrowser");
        string SettingsPath { get { return Path.Combine(dataDir, "settings.ini"); } }

        string homeUrl = "https://www.youtube.com";
        string startUrl;
        int opacityPct = 90;
        bool clickThrough;
        bool reallyExit;
        bool videoFullscreen;
        Rectangle restoreBounds;
        IntPtr gameWindow = IntPtr.Zero;
        float scale = 1f;

        readonly ToolTip tips = new ToolTip();
        WebView2 web;
        Panel titleBar;
        TextBox urlBox;
        TrackBar opacityBar;
        CheckBox ghostBtn;
        Button backBtn, forwardBtn;
        NotifyIcon tray;
        ToolStripMenuItem trayGhost;

        public IdleBrowserForm()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            // Carry settings and logins over from the app's old name.
            string oldDataDir = Path.Combine(Path.GetDirectoryName(dataDir), "OverlayBrowser");
            if (Directory.Exists(oldDataDir) && !Directory.Exists(dataDir))
            {
                try { Directory.Move(oldDataDir, dataDir); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Directory.CreateDirectory(dataDir);

            Text = "Idle Browser";
            Icon = MakeIcon();
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = FrameColor;
            Padding = new Padding(S(Grip));
            MinimumSize = new Size(S(320), S(180));
            Bounds = DefaultOverlayBounds();
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            LoadSettings();

            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
            BuildTitleBar();
            Controls.Add(web);       // fill: added first so it docks last
            Controls.Add(titleBar);  // top

            BuildTray();
            ResizeEnd += (s, e) => SaveSettings();
        }

        int S(int v) { return (int)Math.Round(v * scale); }

        Rectangle DefaultOverlayBounds()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int w = S(720), h = S(460);
            return new Rectangle(wa.Right - w - S(40), wa.Top + S(120), w, h);
        }

        // Layered (for opacity), topmost, and hidden from Alt+Tab.
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        // Showing the overlay must not take keyboard focus away from the game.
        protected override bool ShowWithoutActivation { get { return true; } }

        // ---- UI construction ----------------------------------------------
        void BuildTitleBar()
        {
            titleBar = new Panel { Dock = DockStyle.Top, Height = S(32), BackColor = BarColor };
            titleBar.MouseDown += DragWindow;

            // Docking: the Fill control is added first; for Left/Right docks the
            // control added last ends up outermost.
            var urlHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(4), S(5), S(4), 0), BackColor = BarColor };
            urlHost.MouseDown += DragWindow;
            urlBox = new TextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = UrlBackColor,
                ForeColor = TextColor,
                Font = new Font("Segoe UI", 9f)
            };
            urlBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Navigate(urlBox.Text); web.Focus(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; urlBox.Text = CurrentUrl(); web.Focus(); }
            };
            urlBox.Enter += (s, e) => BeginInvoke(new Action(urlBox.SelectAll));
            urlHost.Controls.Add(urlBox);
            titleBar.Controls.Add(urlHost);

            // Right side, inner -> outer
            var opacityLabel = new Label
            {
                Dock = DockStyle.Right, Width = S(22), Text = "◐", TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = TextColor, Font = new Font("Segoe UI Symbol", 10f)
            };
            opacityLabel.MouseDown += DragWindow;
            tips.SetToolTip(opacityLabel, "Opacity");
            titleBar.Controls.Add(opacityLabel);

            var sliderHost = new Panel { Dock = DockStyle.Right, Width = S(100), Padding = new Padding(0, S(6), S(4), 0), BackColor = BarColor };
            opacityBar = new TrackBar
            {
                Dock = DockStyle.Fill, AutoSize = false, Minimum = MinOpacity, Maximum = 100,
                TickStyle = TickStyle.None, Value = opacityPct, BackColor = BarColor, TabStop = false
            };
            opacityBar.ValueChanged += (s, e) => { opacityPct = opacityBar.Value; ApplyOpacity(); };
            tips.SetToolTip(opacityBar, "Opacity");
            sliderHost.Controls.Add(opacityBar);
            titleBar.Controls.Add(sliderHost);

            ghostBtn = new CheckBox
            {
                Appearance = Appearance.Button, Dock = DockStyle.Right, Width = S(30), Text = "👻",
                TextAlign = ContentAlignment.MiddleCenter, FlatStyle = FlatStyle.Flat, ForeColor = TextColor,
                Font = new Font("Segoe UI Emoji", 9f), TabStop = false
            };
            ghostBtn.FlatAppearance.BorderSize = 0;
            ghostBtn.FlatAppearance.MouseOverBackColor = HoverColor;
            ghostBtn.FlatAppearance.CheckedBackColor = GhostColor;
            ghostBtn.CheckedChanged += (s, e) => SetClickThrough(ghostBtn.Checked);
            tips.SetToolTip(ghostBtn, "Click-through: mouse goes to the game (Ctrl+Shift+B to turn off)");
            titleBar.Controls.Add(ghostBtn);

            titleBar.Controls.Add(MakeButton("✕", "Hide  (Ctrl+B)", DockStyle.Right, (s, e) => HideOverlay()));

            // Left side, inner -> outer
            Button homeBtn = MakeButton("⌂", "Home  (right-click: make this page home)", DockStyle.Left, (s, e) => Navigate(homeUrl));
            var homeMenu = new ContextMenuStrip();
            homeMenu.Items.Add("Set current page as home", null, (s, e) => { homeUrl = CurrentUrl(); SaveSettings(); });
            homeBtn.ContextMenuStrip = homeMenu;
            titleBar.Controls.Add(homeBtn);
            titleBar.Controls.Add(MakeButton("↻", "Reload", DockStyle.Left, (s, e) => { if (web.CoreWebView2 != null) web.Reload(); }));
            forwardBtn = MakeButton("▶", "Forward", DockStyle.Left, (s, e) => { if (web.CanGoForward) web.GoForward(); });
            titleBar.Controls.Add(forwardBtn);
            backBtn = MakeButton("◀", "Back", DockStyle.Left, (s, e) => { if (web.CanGoBack) web.GoBack(); });
            titleBar.Controls.Add(backBtn);
            backBtn.Enabled = forwardBtn.Enabled = false;
        }

        Button MakeButton(string text, string tip, DockStyle dock, EventHandler click)
        {
            var b = new Button
            {
                Text = text, Dock = dock, Width = S(30), FlatStyle = FlatStyle.Flat, ForeColor = TextColor,
                BackColor = BarColor, Font = new Font("Segoe UI Symbol", 10f), TabStop = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = HoverColor;
            b.Click += click;
            tips.SetToolTip(b, tip);
            return b;
        }

        void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show / Hide   (Ctrl+B)", null, (s, e) => ToggleOverlay(false));
            trayGhost = new ToolStripMenuItem("Click-through   (Ctrl+Shift+B)", null, (s, e) => SetClickThrough(!clickThrough));
            menu.Items.Add(trayGhost);
            menu.Items.Add("Reset position && size", null, (s, e) =>
            {
                if (videoFullscreen) SetVideoFullscreen(false);
                Bounds = DefaultOverlayBounds();
                ShowOverlay();
                SaveSettings();
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => { reallyExit = true; Close(); });

            tray = new NotifyIcon { Icon = Icon, Text = "Idle Browser", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleOverlay(false); };
        }

        static Icon MakeIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            using (var fill = new SolidBrush(Color.FromArgb(45, 34, 18)))
            using (var ring = new Pen(GoldColor, 2.5f))
            using (var text = new SolidBrush(TextColor))
            using (var font = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(fill, 1, 1, 30, 30);
                g.DrawEllipse(ring, 2, 2, 28, 28);
                g.DrawString("B", font, text, new RectangleF(0, 1, 32, 32), fmt);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (videoFullscreen) return;
            int w = S(2);
            using (var pen = new Pen(clickThrough ? GhostColor : GoldColor, w))
                e.Graphics.DrawRectangle(pen, w / 2, w / 2, ClientSize.Width - w, ClientSize.Height - w);
        }

        // ---- Window behavior ----------------------------------------------
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyOpacity();
            bool ok = RegisterHotKey(Handle, HOTKEY_TOGGLE, MOD_CONTROL | MOD_NOREPEAT, (uint)Keys.B);
            ok &= RegisterHotKey(Handle, HOTKEY_GHOST, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.B);
            if (!ok)
                BeginInvoke(new Action(() => tray.ShowBalloonTip(5000, "Idle Browser",
                    "Ctrl+B / Ctrl+Shift+B is already used by another program. Use the tray icon instead.", ToolTipIcon.Warning)));
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            InitWebView();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HOTKEY_TOGGLE) ToggleOverlay(true);
                else if (id == HOTKEY_GHOST) SetClickThrough(!clickThrough);
                return;
            }
            if (m.Msg == Program.ShowMessage && m.Msg != 0) { ShowOverlay(); return; }

            base.WndProc(ref m);

            if (m.Msg == WM_NCHITTEST && m.Result.ToInt64() == HTCLIENT)
                m.Result = new IntPtr(HitTestEdges(m.LParam));
        }

        // The border strip around the browser is the form itself, so it can act as resize handles.
        int HitTestEdges(IntPtr lParam)
        {
            if (videoFullscreen) return HTCLIENT;
            long lp = lParam.ToInt64();
            Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            int g = Padding.Left, c = g * 4;
            int w = ClientSize.Width, h = ClientSize.Height;
            bool l = p.X < g, r = p.X >= w - g, t = p.Y < g, b = p.Y >= h - g;
            if ((t && p.X < c) || (l && p.Y < c)) return HTTOPLEFT;
            if ((t && p.X >= w - c) || (r && p.Y < c)) return HTTOPRIGHT;
            if ((b && p.X < c) || (l && p.Y >= h - c)) return HTBOTTOMLEFT;
            if ((b && p.X >= w - c) || (r && p.Y >= h - c)) return HTBOTTOMRIGHT;
            if (l) return HTLEFT;
            if (r) return HTRIGHT;
            if (t) return HTTOP;
            if (b) return HTBOTTOM;
            return HTCLIENT;
        }

        void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
        }

        void ToggleOverlay(bool fromHotkey)
        {
            if (Visible) { HideOverlay(); return; }
            if (fromHotkey)
            {
                // Remember the game window so focus can be handed back on hide.
                IntPtr fg = GetForegroundWindow();
                if (fg != Handle) gameWindow = fg;
            }
            ShowOverlay();
        }

        void ShowOverlay()
        {
            if (!Visible) Show();
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        void HideOverlay()
        {
            bool hadFocus = GetForegroundWindow() == Handle;
            SaveSettings();
            Hide();
            if (hadFocus) ReturnFocusToGame();
        }

        void ReturnFocusToGame()
        {
            if (gameWindow != IntPtr.Zero && IsWindow(gameWindow)) SetForegroundWindow(gameWindow);
        }

        void ApplyOpacity()
        {
            if (!IsHandleCreated) return;
            SetLayeredWindowAttributes(Handle, 0, (byte)(opacityPct * 255 / 100), LWA_ALPHA);
        }

        void SetClickThrough(bool on)
        {
            if (on == clickThrough && ghostBtn.Checked == on) return;
            clickThrough = on;
            if (IsHandleCreated)
            {
                int ex = GetWindowLong(Handle, GWL_EXSTYLE);
                ex = on ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
                SetWindowLong(Handle, GWL_EXSTYLE, ex);
                if (on && GetForegroundWindow() == Handle) ReturnFocusToGame();
            }
            ghostBtn.Checked = on;
            trayGhost.Checked = on;
            Invalidate();
            if (on && Visible)
                tray.ShowBalloonTip(2500, "Click-through on", "Clicks now go to the game. Ctrl+Shift+B to turn off.", ToolTipIcon.None);
        }

        void SetVideoFullscreen(bool on)
        {
            if (on == videoFullscreen) return;
            videoFullscreen = on;
            if (on)
            {
                restoreBounds = Bounds;
                titleBar.Visible = false;
                Padding = new Padding(0);
                Bounds = Screen.FromControl(this).Bounds;
            }
            else
            {
                Padding = new Padding(S(Grip));
                titleBar.Visible = true;
                Bounds = restoreBounds;
            }
            Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Alt+F4 or the ✕ button just hides; "Exit" in the tray really quits.
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideOverlay();
                return;
            }
            SaveSettings();
            tray.Visible = false;
            UnregisterHotKey(Handle, HOTKEY_TOGGLE);
            UnregisterHotKey(Handle, HOTKEY_GHOST);
            base.OnFormClosing(e);
        }

        // ---- Browser ------------------------------------------------------
        async void InitWebView()
        {
            try
            {
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(
                    null, Path.Combine(dataDir, "WebView2"), null);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start the browser engine (Microsoft Edge WebView2).\n\n" + ex.Message,
                    "Idle Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            CoreWebView2 core = web.CoreWebView2;
            core.SourceChanged += (s, e) => { if (!urlBox.Focused) urlBox.Text = core.Source; };
            core.HistoryChanged += (s, e) => { backBtn.Enabled = core.CanGoBack; forwardBtn.Enabled = core.CanGoForward; };
            core.DocumentTitleChanged += (s, e) =>
            {
                string t = "Idle Browser - " + core.DocumentTitle;
                tray.Text = t.Length > 63 ? t.Substring(0, 60) + "..." : t;
            };
            // Keep popups / target=_blank links inside the overlay.
            core.NewWindowRequested += (s, e) => { e.Handled = true; core.Navigate(e.Uri); };
            // Video fullscreen button fills the screen (still see-through at your opacity).
            core.ContainsFullScreenElementChanged += (s, e) => SetVideoFullscreen(core.ContainsFullScreenElement);

            Navigate(startUrl);
        }

        string CurrentUrl()
        {
            return web.CoreWebView2 != null ? web.CoreWebView2.Source : startUrl;
        }

        void Navigate(string input)
        {
            if (web.CoreWebView2 == null || input == null) return;
            string s = input.Trim();
            if (s.Length == 0) return;

            string url;
            if (s.Contains("://")) url = s;
            else if (!s.Contains(" ") && (s.Contains(".") || s.StartsWith("localhost"))) url = "https://" + s;
            else url = SearchUrl(s);

            try { web.CoreWebView2.Navigate(url); }
            catch (ArgumentException) { web.CoreWebView2.Navigate(SearchUrl(s)); }
        }

        static string SearchUrl(string q)
        {
            return "https://www.google.com/search?q=" + Uri.EscapeDataString(q);
        }

        // ---- Settings -----------------------------------------------------
        void LoadSettings()
        {
            startUrl = homeUrl;
            if (!File.Exists(SettingsPath)) return;

            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch (IOException) { return; }

            int x, y, w, h, o;
            if (ReadInt(d, "x", out x) && ReadInt(d, "y", out y) && ReadInt(d, "width", out w) && ReadInt(d, "height", out h))
            {
                var r = new Rectangle(x, y, Math.Max(w, MinimumSize.Width), Math.Max(h, MinimumSize.Height));
                if (IsOnScreen(r)) Bounds = r;
            }
            if (ReadInt(d, "opacity", out o)) opacityPct = Math.Max(MinOpacity, Math.Min(100, o));

            string v;
            if (d.TryGetValue("home", out v) && v.Length > 0) homeUrl = v;
            startUrl = d.TryGetValue("url", out v) && v.Length > 0 ? v : homeUrl;
        }

        void SaveSettings()
        {
            Rectangle r = videoFullscreen ? restoreBounds : Bounds;
            string[] lines =
            {
                "x=" + r.X, "y=" + r.Y, "width=" + r.Width, "height=" + r.Height,
                "opacity=" + opacityPct,
                "home=" + homeUrl,
                "url=" + CurrentUrl()
            };
            try { File.WriteAllLines(SettingsPath, lines); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static bool ReadInt(Dictionary<string, string> d, string key, out int value)
        {
            string s;
            value = 0;
            return d.TryGetValue(key, out s) && int.TryParse(s, out value);
        }

        static bool IsOnScreen(Rectangle r)
        {
            foreach (Screen s in Screen.AllScreens)
            {
                Rectangle overlap = Rectangle.Intersect(s.WorkingArea, r);
                if (overlap.Width >= 100 && overlap.Height >= 40) return true;
            }
            return false;
        }
    }
}
