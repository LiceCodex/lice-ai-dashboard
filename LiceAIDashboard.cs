using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Lice AI Dashboard")]
[assembly: System.Reflection.AssemblyDescription("Codex usage and network tray dashboard")]
[assembly: System.Reflection.AssemblyCompany("Lice")]
[assembly: System.Reflection.AssemblyProduct("Lice AI Dashboard")]
[assembly: System.Reflection.AssemblyVersion("1.3.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.3.0.0")]

namespace LiceAIDashboard
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.Expect100Continue = false;
            WebRequest.DefaultWebProxy = WebRequest.GetSystemWebProxy();
            if (WebRequest.DefaultWebProxy != null)
                WebRequest.DefaultWebProxy.Credentials = CredentialCache.DefaultCredentials;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool startHidden = Environment.GetCommandLineArgs()
                .Any(arg => String.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase));
            Application.Run(new DashboardForm(startHidden));
        }
    }

    internal sealed class AppConfig
    {
        public int refresh_seconds = 60;
        public string vpn_node_label = "";
        public bool auto_start = true;
    }

    internal sealed class NodeSample
    {
        public DateTime sampled_at;
        public string node_key;
        public string label;
        public double latency_ms;
        public bool online;
    }

    internal sealed class PurityCacheEntry
    {
        public DateTime checked_at;
        public string response_json;
    }

    internal sealed class PurityResult
    {
        public int score;
        public string grade;
        public string recommendation;
        public string detail;
    }

    internal sealed class GlassPanel : Panel
    {
        public int Radius { get; set; }
        public Color GlassTop { get; set; }
        public Color GlassBottom { get; set; }
        public Color BorderColor { get; set; }

        public GlassPanel()
        {
            Radius = 10;
            GlassTop = Color.FromArgb(31, 35, 43);
            GlassBottom = Color.FromArgb(31, 35, 43);
            BorderColor = Color.FromArgb(62, 68, 80);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = GlassTop;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            using (var path = RoundedPath(new Rectangle(0, 0, Width, Height), Radius))
                Region = new Region(path);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (var path = RoundedPath(rect, Radius))
            using (var brush = new SolidBrush(GlassTop))
                e.Graphics.FillPath(brush, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), Radius))
            using (var pen = new Pen(BorderColor, 1F))
                e.Graphics.DrawPath(pen, path);
        }

        internal static GraphicsPath RoundedPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int diameter = Math.Max(2, radius * 2);
            var arc = new Rectangle(rect.X, rect.Y, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class GlassButton : Button
    {
        public int Radius { get; set; }

        public GlassButton()
        {
            Radius = 6;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = GlassPanel.RoundedPath(rect, Radius))
            using (var brush = new SolidBrush(
                Enabled ? Color.FromArgb(48, 54, 66) : Color.FromArgb(38, 42, 50)))
            using (var pen = new Pen(Color.FromArgb(72, 79, 92)))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, rect,
                Enabled ? ForeColor : Color.FromArgb(130, ForeColor),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class DashboardForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(18, 20, 24);
        private static readonly Color Card = Color.FromArgb(31, 35, 43);
        private static readonly Color TextColor = Color.FromArgb(243, 245, 248);
        private static readonly Color Muted = Color.FromArgb(155, 164, 180);
        private static readonly Color Green = Color.FromArgb(63, 201, 128);
        private static readonly Color Yellow = Color.FromArgb(246, 190, 76);
        private static readonly Color Red = Color.FromArgb(239, 93, 108);
        private static readonly Color Accent = Color.FromArgb(76, 141, 255);
        private static readonly Color Cyan = Color.FromArgb(94, 168, 255);

        private readonly string appDir;
        private readonly string configPath;
        private readonly string historyPath;
        private readonly string statusPath;
        private readonly string purityCachePath;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly NotifyIcon tray;
        private readonly Timer refreshTimer;
        private readonly Timer hoverTimer;
        private AppConfig config;
        private DateTime lastTrayHover = DateTime.MinValue;
        private bool temporaryHoverWindow;
        private bool refreshing;
        private readonly bool startHidden;

        private Label weeklyValue;
        private Label weeklyMeta;
        private Panel weeklyProgress;
        private Label vpnValue;
        private Label vpnMeta;
        private Label vpnHistory;
        private Label purityValue;
        private Label aiRecommendation;
        private Label purityMeta;
        private Label healthMeta;
        private Label updated;
        private CheckBox autoStartToggle;
        private Panel settingsPanel;
        private Button networkRefreshButton;

        public DashboardForm(bool startHidden)
        {
            this.startHidden = startHidden;
            appDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiceAIDashboard");
            configPath = Path.Combine(appDir, "config-exe.json");
            historyPath = Path.Combine(appDir, "vpn-history-exe.jsonl");
            statusPath = Path.Combine(appDir, "last-status.json");
            purityCachePath = Path.Combine(appDir, "purity-cache.json");
            Directory.CreateDirectory(appDir);
            config = LoadConfig();

            Text = "Lice AI Dashboard";
            BackColor = Bg;
            ForeColor = TextColor;
            Font = new Font("Microsoft YaHei UI", 9F);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(410, 640);
            MinimumSize = new Size(390, 570);
            TopMost = true;
            DoubleBuffered = true;
            ShowInTaskbar = false;

            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            BuildUi();

            tray = new NotifyIcon();
            tray.Icon = Icon ?? SystemIcons.Application;
            tray.Text = "Lice AI Dashboard";
            tray.Visible = true;
            tray.MouseMove += delegate
            {
                lastTrayHover = DateTime.Now;
                if (!Visible)
                {
                    temporaryHoverWindow = true;
                    ShowAtTray();
                }
            };
            tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                temporaryHoverWindow = false;
                if (Visible) HideToTray(); else ShowAtTray();
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开", null, delegate { temporaryHoverWindow = false; ShowAtTray(); });
            menu.Items.Add("立即刷新", null, async delegate { await RefreshData(); });
            menu.Items.Add("退出", null, delegate { ExitApplication(); });
            tray.ContextMenuStrip = menu;

            refreshTimer = new Timer();
            refreshTimer.Interval = Math.Max(30, config.refresh_seconds) * 1000;
            refreshTimer.Tick += async delegate { await RefreshData(); };

            hoverTimer = new Timer();
            hoverTimer.Interval = 250;
            hoverTimer.Tick += delegate
            {
                if (!Visible || !temporaryHoverWindow) return;
                bool overWindow = Bounds.Contains(Cursor.Position);
                var area = Screen.PrimaryScreen.WorkingArea;
                bool overTrayZone = Cursor.Position.Y >= area.Bottom
                    && Cursor.Position.X >= area.Right - 260;
                bool recentlyOverTray = (DateTime.Now - lastTrayHover).TotalMilliseconds < 900;
                if (!overWindow && !overTrayZone && !recentlyOverTray) HideToTray();
            };

            Shown += async delegate
            {
                ApplyAutoStart(config.auto_start);
                await RefreshData();
                refreshTimer.Start();
                hoverTimer.Start();
                if (this.startHidden) BeginInvoke(new Action(HideToTray));
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    HideToTray();
                }
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Bg);
            using (var border = new Pen(Color.FromArgb(57, 62, 72)))
                e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            base.OnPaint(e);
        }

        private void BuildUi()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Bg };
            Controls.Add(header);
            var logo = new PictureBox
            {
                Image = BuildLogo(36),
                SizeMode = PictureBoxSizeMode.StretchImage,
                Bounds = new Rectangle(16, 16, 36, 36),
                BackColor = Bg
            };
            header.Controls.Add(logo);
            header.Controls.Add(NewLabel("Lice AI Dashboard", 60, 14, 215, 26, 16, true, TextColor));
            header.Controls.Add(NewLabel("Windows 桌面状态中心", 61, 38, 190, 16, 7.5F, false, Muted));

            var settingsButton = NewButton("设置", 288, 18, 54, 32);
            settingsButton.Click += delegate { settingsPanel.Visible = !settingsPanel.Visible; };
            header.Controls.Add(settingsButton);
            var hideButton = NewButton("—", 348, 18, 42, 32);
            hideButton.Click += delegate { HideToTray(); };
            header.Controls.Add(hideButton);

            var body = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(14, 3, 14, 10),
                BackColor = Bg,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
            Controls.Add(body);
            body.BringToFront();

            settingsPanel = NewCard(108);
            settingsPanel.Visible = false;
            settingsPanel.Controls.Add(NewLabel("启动与托盘", 18, 13, 220, 24, 11, true, TextColor));
            autoStartToggle = new CheckBox
            {
                Text = "登录 Windows 后自动启动",
                ForeColor = TextColor,
                BackColor = Card,
                AutoSize = true,
                Location = new Point(20, 45),
                Checked = IsAutoStartEnabled()
            };
            autoStartToggle.CheckedChanged += delegate
            {
                config.auto_start = autoStartToggle.Checked;
                SaveConfig();
                ApplyAutoStart(autoStartToggle.Checked);
            };
            settingsPanel.Controls.Add(autoStartToggle);
            settingsPanel.Controls.Add(NewLabel("托盘悬停展开，移开后自动收起", 20, 74, 310, 20, 9, false, Muted));
            body.Controls.Add(settingsPanel);

            var weekly = NewCard(126);
            weekly.Controls.Add(NewLabel("Codex 周额度", 18, 13, 240, 24, 10.5F, true, TextColor));
            weeklyValue = NewLabel("正在读取…", 18, 42, 320, 32, 22, true, TextColor);
            weekly.Controls.Add(weeklyValue);
            var barBg = new Panel { BackColor = Color.FromArgb(62, 75, 105), Bounds = new Rectangle(18, 82, 346, 7) };
            weeklyProgress = new Panel { BackColor = Accent, Bounds = new Rectangle(0, 0, 0, 8) };
            barBg.Controls.Add(weeklyProgress);
            weekly.Controls.Add(barBg);
            weeklyMeta = NewLabel("刷新时间未知", 18, 96, 346, 20, 8.5F, false, Muted);
            weekly.Controls.Add(weeklyMeta);
            body.Controls.Add(weekly);

            var vpn = NewCard(264);
            vpn.Controls.Add(NewLabel("VPN / 网络节点", 18, 13, 230, 24, 10.5F, true, TextColor));
            networkRefreshButton = NewButton("↻  刷新节点", 268, 10, 96, 32);
            networkRefreshButton.Click += async delegate { await RefreshNetworkData(true); };
            vpn.Controls.Add(networkRefreshButton);
            vpnValue = NewLabel("正在检测…", 18, 49, 346, 31, 17, true, TextColor);
            vpn.Controls.Add(vpnValue);
            vpnMeta = NewLabel("", 18, 80, 346, 20, 9, false, Muted);
            vpn.Controls.Add(vpnMeta);
            purityValue = NewLabel("纯净度：检测中…", 18, 109, 195, 25, 12, true, Muted);
            vpn.Controls.Add(purityValue);
            aiRecommendation = NewLabel("AI 推荐：检测中…", 213, 109, 151, 25, 11, true, Muted);
            vpn.Controls.Add(aiRecommendation);
            purityMeta = NewLabel("", 18, 138, 346, 38, 9, false, Muted);
            purityMeta.AutoEllipsis = true;
            vpn.Controls.Add(purityMeta);
            vpnHistory = NewLabel("", 18, 184, 346, 67, 8.5F, false, Muted);
            vpnHistory.AutoEllipsis = true;
            vpn.Controls.Add(vpnHistory);
            body.Controls.Add(vpn);

            var health = NewCard(126);
            health.Controls.Add(NewLabel("服务状态", 18, 13, 260, 24, 10.5F, true, TextColor));
            healthMeta = NewLabel("正在检测…", 18, 43, 346, 72, 9, false, Muted);
            health.Controls.Add(healthMeta);
            body.Controls.Add(health);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 32, BackColor = Bg };
            updated = NewLabel("", 18, 7, 250, 18, 8, false, Muted);
            footer.Controls.Add(updated);
            Controls.Add(footer);
            footer.BringToFront();
        }

        private Panel NewCard(int height)
        {
            return new GlassPanel
            {
                Height = height,
                Width = 366,
                Dock = DockStyle.None,
                Margin = new Padding(0, 0, 0, 12),
                Padding = new Padding(0)
            };
        }

        private Label NewLabel(string text, int x, int y, int w, int h, float size, bool bold, Color color)
        {
            return new Label
            {
                Text = text,
                Bounds = new Rectangle(x, y, w, h),
                ForeColor = color,
                BackColor = Color.FromArgb(48, 54, 66),
                Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private Button NewButton(string text, int x, int y, int w, int h)
        {
            return new GlassButton
            {
                Text = text,
                Bounds = new Rectangle(x, y, w, h),
                BackColor = Color.Transparent,
                ForeColor = TextColor,
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular)
            };
        }

        private Bitmap BuildLogo(int size)
        {
            var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var brush = new LinearGradientBrush(
                    new Rectangle(0, 0, size, size), Accent, Color.FromArgb(52, 211, 153), 45F))
                    g.FillEllipse(brush, 1, 1, size - 2, size - 2);
                using (var pen = new Pen(Color.White, Math.Max(2, size / 11)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, size * .24F, size * .23F, size * .52F, size * .52F, 205, 235);
                    g.DrawLine(pen, size * .5F, size * .5F, size * .69F, size * .37F);
                }
                using (var brush = new SolidBrush(Color.White))
                    g.FillEllipse(brush, size * .42F, size * .42F, size * .16F, size * .16F);
            }
            return bitmap;
        }

        private AppConfig LoadConfig()
        {
            try
            {
                if (File.Exists(configPath)) return json.Deserialize<AppConfig>(File.ReadAllText(configPath));
            }
            catch { }
            var result = new AppConfig();
            try { File.WriteAllText(configPath, json.Serialize(result)); } catch { }
            return result;
        }

        private void SaveConfig()
        {
            try { File.WriteAllText(configPath, json.Serialize(config)); } catch { }
        }

        private bool IsAutoStartEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                return key != null && key.GetValue("Lice AI Dashboard") != null;
        }

        private void ApplyAutoStart(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (enabled)
                        key.SetValue("Lice AI Dashboard", "\"" + Application.ExecutablePath + "\" --tray");
                    else
                        key.DeleteValue("Lice AI Dashboard", false);
                }
                if (autoStartToggle != null && autoStartToggle.Checked != enabled)
                    autoStartToggle.Checked = enabled;
            }
            catch { }
        }

        private void ShowAtTray()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 10, area.Bottom - Height - 10);
            Show();
            BringToFront();
        }

        private void HideToTray()
        {
            temporaryHoverWindow = false;
            Hide();
        }

        private void ExitApplication()
        {
            refreshTimer.Stop();
            hoverTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            FormClosing -= null;
            Dispose();
            Application.Exit();
        }

        private async Task RefreshData()
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                var weeklyTask = Task.Run(() => ReadWeeklyUsage());
                var networkTask = Task.Run(() => ReadNetwork());
                var healthTask = Task.Run(() => ReadHealth());
                await Task.WhenAll(weeklyTask, networkTask, healthTask);

                var weekly = weeklyTask.Result;
                double remaining = weekly.Item1;
                if (remaining >= 0)
                {
                    weeklyValue.Text = Math.Round(remaining) + "% 剩余";
                    weeklyValue.ForeColor = TextColor;
                    weeklyProgress.Width = (int)(346 * remaining / 100.0);
                }
                else
                {
                    weeklyValue.Text = weekly.Item2;
                    weeklyValue.ForeColor = Yellow;
                    weeklyProgress.Width = 0;
                }
                weeklyMeta.Text = weekly.Item3;

                var network = networkTask.Result;
                vpnValue.Text = network.Item1 + "  ·  " + (network.Item3 >= 0 ? Math.Round(network.Item3) + " ms" : "离线");
                vpnValue.ForeColor = network.Item4 ? Green : Red;
                vpnMeta.Text = "公网 IP：" + (String.IsNullOrEmpty(network.Item2) ? "读取失败" : network.Item2);
                AddHistory(network.Item2, network.Item1, network.Item3, network.Item4);
                vpnHistory.Text = BuildHistorySummary();

                var healthLines = healthTask.Result;
                bool allAiServicesReachable = healthLines.All(line => line.Contains("正常"));
                var purity = await Task.Run(() => ReadPurity(network.Item2, allAiServicesReachable));
                purityValue.Text = "纯净度：" + purity.score + "/100 · " + purity.grade;
                purityValue.ForeColor = purity.score >= 80 ? Green : purity.score >= 60 ? Yellow : Red;
                aiRecommendation.Text = "AI：" + purity.recommendation;
                aiRecommendation.ForeColor = purity.recommendation == "推荐" ? Green
                    : purity.recommendation == "谨慎推荐" ? Yellow : Red;
                purityMeta.Text = purity.detail;
                healthMeta.Text = String.Join(Environment.NewLine, healthLines);
                healthMeta.ForeColor = allAiServicesReachable ? Green : Red;
                updated.Text = "更新于 " + DateTime.Now.ToString("HH:mm:ss");
                try
                {
                    File.WriteAllText(statusPath, json.Serialize(new
                    {
                        updated_at = DateTime.Now,
                        weekly_remaining = remaining,
                        weekly_status = weekly.Item2,
                        weekly_detail = weekly.Item3,
                        vpn_online = network.Item4,
                        vpn_label = network.Item1,
                        public_ip = network.Item2,
                        latency_ms = network.Item3,
                        purity_score = purity.score,
                        purity_grade = purity.grade,
                        ai_recommendation = purity.recommendation,
                        purity_detail = purity.detail,
                        services = healthLines
                    }));
                }
                catch { }
            }
            finally { refreshing = false; }
        }

        private async Task RefreshNetworkData(bool forcePurityRefresh)
        {
            if (refreshing) return;
            refreshing = true;
            networkRefreshButton.Enabled = false;
            networkRefreshButton.Text = "刷新中…";
            vpnValue.Text = "正在识别新节点…";
            vpnValue.ForeColor = Muted;
            try
            {
                var networkTask = Task.Run(() => ReadNetwork());
                var healthTask = Task.Run(() => ReadHealth());
                await Task.WhenAll(networkTask, healthTask);

                var network = networkTask.Result;
                var healthLines = healthTask.Result;
                bool allAiServicesReachable = healthLines.All(line => line.Contains("正常"));
                var purity = await Task.Run(() =>
                    ReadPurity(network.Item2, allAiServicesReachable, forcePurityRefresh));

                vpnValue.Text = network.Item1 + "  ·  " +
                    (network.Item3 >= 0 ? Math.Round(network.Item3) + " ms" : "离线");
                vpnValue.ForeColor = network.Item4 ? Green : Red;
                vpnMeta.Text = "公网 IP：" +
                    (String.IsNullOrEmpty(network.Item2) ? "读取失败" : network.Item2);
                AddHistory(network.Item2, network.Item1, network.Item3, network.Item4);
                vpnHistory.Text = BuildHistorySummary();
                purityValue.Text = "纯净度：" + purity.score + "/100 · " + purity.grade;
                purityValue.ForeColor = purity.score >= 80 ? Green : purity.score >= 60 ? Yellow : Red;
                aiRecommendation.Text = "AI：" + purity.recommendation;
                aiRecommendation.ForeColor = purity.recommendation == "推荐" ? Green
                    : purity.recommendation == "谨慎推荐" ? Yellow : Red;
                purityMeta.Text = purity.detail;
                healthMeta.Text = String.Join(Environment.NewLine, healthLines);
                healthMeta.ForeColor = allAiServicesReachable ? Green : Red;
                updated.Text = "节点更新于 " + DateTime.Now.ToString("HH:mm:ss");
            }
            finally
            {
                networkRefreshButton.Text = "↻  刷新节点";
                networkRefreshButton.Enabled = true;
                refreshing = false;
            }
        }

        private Tuple<double, string, string> ReadWeeklyUsage()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codex", "auth.json");
                if (!File.Exists(path)) return Tuple.Create(-1D, "未连接", "未找到 Codex 登录文件");
                var root = json.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                var tokens = root["tokens"] as Dictionary<string, object>;
                string access = Convert.ToString(tokens["access_token"]);
                string account = Convert.ToString(tokens["account_id"]);
                using (var client = NewWebClient())
                {
                    client.Headers[HttpRequestHeader.Authorization] = "Bearer " + access;
                    client.Headers["chatgpt-account-id"] = account;
                    client.Headers[HttpRequestHeader.Accept] = "application/json";
                    string raw = client.DownloadString("https://chatgpt.com/backend-api/wham/usage");
                    var data = json.DeserializeObject(raw) as Dictionary<string, object>;
                    var rate = data["rate_limit"] as Dictionary<string, object>;
                    foreach (string name in new[] { "primary_window", "secondary_window" })
                    {
                        var window = rate[name] as Dictionary<string, object>;
                        if (Convert.ToInt32(window["limit_window_seconds"]) != 604800) continue;
                        double used = Convert.ToDouble(window["used_percent"]);
                        long reset = Convert.ToInt64(window["reset_at"]);
                        DateTime resetAt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                            .AddSeconds(reset).ToLocalTime();
                        return Tuple.Create(Math.Max(0, Math.Min(100, 100 - used)), "已连接",
                            "刷新：" + resetAt.ToString("MM-dd HH:mm") + " · 非公开接口");
                    }
                }
                return Tuple.Create(-1D, "不可用", "接口没有返回周额度");
            }
            catch (Exception ex) { return Tuple.Create(-1D, "更新失败", ex.GetType().Name); }
        }

        private Tuple<string, string, double, bool> ReadNetwork()
        {
            double latency = WebLatency("https://chatgpt.com/");
            try
            {
                using (var client = NewWebClient())
                {
                    string trace = client.DownloadString("https://www.cloudflare.com/cdn-cgi/trace");
                    var values = trace.Split('\n')
                        .Where(x => x.Contains("="))
                        .Select(x => x.Trim().Split(new[] { '=' }, 2))
                        .ToDictionary(x => x[0], x => x[1]);
                    string location = values.ContainsKey("loc") ? values["loc"] : "当前节点";
                    string ip = values.ContainsKey("ip") ? values["ip"] : "";
                    string label = String.IsNullOrEmpty(config.vpn_node_label) ? location : config.vpn_node_label;
                    return Tuple.Create(label, ip, latency, true);
                }
            }
            catch
            {
                string label = String.IsNullOrEmpty(config.vpn_node_label) ? "当前节点" : config.vpn_node_label;
                return Tuple.Create(label, "", latency, latency >= 0);
            }
        }

        private List<string> ReadHealth()
        {
            var services = new[]
            {
                Tuple.Create("ChatGPT", "https://chatgpt.com/"),
                Tuple.Create("OpenAI API", "https://api.openai.com/v1/models"),
                Tuple.Create("GitHub", "https://github.com/")
            };
            return services.Select(service =>
            {
                double ms = WebLatency(service.Item2);
                return ms < 0 ? "● " + service.Item1 + "  不可达"
                    : "● " + service.Item1 + "  正常  " + Math.Round(ms) + "ms";
            }).ToList();
        }

        private double WebLatency(string url)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Proxy = WebRequest.DefaultWebProxy;
                request.Method = "HEAD";
                request.Timeout = 6000;
                request.AllowAutoRedirect = false;
                request.UserAgent = "Lice-AI-Dashboard/1.0";
                using (request.GetResponse()) { }
                return watch.Elapsed.TotalMilliseconds;
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                {
                    ex.Response.Close();
                    return watch.Elapsed.TotalMilliseconds;
                }
                return -1;
            }
            catch { return -1; }
            finally { watch.Stop(); }
        }

        private WebClient NewWebClient()
        {
            var client = new WebClient();
            client.Proxy = WebRequest.DefaultWebProxy;
            client.Headers[HttpRequestHeader.UserAgent] = "Lice-AI-Dashboard/1.0";
            return client;
        }

        private PurityResult ReadPurity(string ip, bool aiReachable, bool forceRefresh = false)
        {
            if (String.IsNullOrWhiteSpace(ip))
                return new PurityResult
                {
                    score = 0,
                    grade = "未知",
                    recommendation = "不推荐",
                    detail = "未读取到公网 IP，无法评估纯净度"
                };
            try
            {
                string raw = forceRefresh ? null : ReadCachedPurity(ip);
                if (String.IsNullOrEmpty(raw))
                {
                    using (var client = NewWebClient())
                        raw = client.DownloadString(
                            "https://reputation.noc.org/api/?ip=" + Uri.EscapeDataString(ip));
                    SavePurityCache(ip, raw);
                }
                var data = json.DeserializeObject(raw) as Dictionary<string, object>;
                var usage = data.ContainsKey("usage") ? data["usage"] as Dictionary<string, object> : null;
                var reputation = data.ContainsKey("reputation")
                    ? data["reputation"] as Dictionary<string, object> : null;
                var recommendations = data.ContainsKey("recommendations")
                    ? data["recommendations"] as Dictionary<string, object> : null;
                int score = 100;
                var reasons = new List<string>();
                if (Flag(usage, "is_tor")) { score -= 50; reasons.Add("Tor 出口"); }
                if (Flag(usage, "is_proxy")) { score -= 30; reasons.Add("代理特征"); }
                if (Flag(usage, "is_hosting")) { score -= 15; reasons.Add("机房 IP"); }
                if (usage != null && usage.ContainsKey("is_routable") && !Flag(usage, "is_routable"))
                { score -= 50; reasons.Add("不可路由"); }
                ApplyRisk(reputation, "web_spam", "网页垃圾", 15, ref score, reasons);
                ApplyRisk(reputation, "web_attacks", "攻击记录", 15, ref score, reasons);
                ApplyRisk(reputation, "botnet", "僵尸网络", 30, ref score, reasons);
                ApplyRisk(reputation, "email_spam", "邮件垃圾", 10, ref score, reasons);
                ApplyRisk(reputation, "brute_force", "暴力破解", 10, ref score, reasons);
                ApplyRisk(reputation, "ddos", "DDoS 记录", 20, ref score, reasons);
                if (Flag(recommendations, "block_traffic"))
                { score -= 20; reasons.Add("信誉库建议拦截"); }
                score = Math.Max(0, Math.Min(100, score));
                string grade = score >= 90 ? "优秀" : score >= 80 ? "良好"
                    : score >= 60 ? "一般" : score >= 40 ? "较差" : "高风险";
                string recommendation = !aiReachable ? "不推荐"
                    : score >= 80 ? "推荐" : score >= 60 ? "谨慎推荐" : "不推荐";
                string detail = reasons.Count == 0
                    ? "未发现公开代理、机房或滥用风险信号"
                    : "风险信号：" + String.Join("、", reasons.Distinct());
                return new PurityResult
                {
                    score = score,
                    grade = grade,
                    recommendation = recommendation,
                    detail = detail
                };
            }
            catch (Exception ex)
            {
                return new PurityResult
                {
                    score = 0,
                    grade = "未知",
                    recommendation = aiReachable ? "谨慎推荐" : "不推荐",
                    detail = "信誉接口暂不可用：" + ex.GetType().Name
                };
            }
        }

        private bool Flag(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.ContainsKey(key) || source[key] == null) return false;
            try { return Convert.ToBoolean(source[key]); } catch { return false; }
        }

        private void ApplyRisk(Dictionary<string, object> source, string key, string label,
            int penalty, ref int score, List<string> reasons)
        {
            if (!Flag(source, key)) return;
            score -= penalty;
            reasons.Add(label);
        }

        private string ReadCachedPurity(string ip)
        {
            try
            {
                if (!File.Exists(purityCachePath)) return null;
                var cache = json.Deserialize<Dictionary<string, PurityCacheEntry>>(
                    File.ReadAllText(purityCachePath));
                PurityCacheEntry entry;
                if (cache != null && cache.TryGetValue(ip, out entry)
                    && DateTime.Now - entry.checked_at < TimeSpan.FromHours(6))
                    return entry.response_json;
            }
            catch { }
            return null;
        }

        private void SavePurityCache(string ip, string raw)
        {
            try
            {
                Dictionary<string, PurityCacheEntry> cache = null;
                if (File.Exists(purityCachePath))
                    cache = json.Deserialize<Dictionary<string, PurityCacheEntry>>(
                        File.ReadAllText(purityCachePath));
                if (cache == null) cache = new Dictionary<string, PurityCacheEntry>();
                cache[ip] = new PurityCacheEntry { checked_at = DateTime.Now, response_json = raw };
                File.WriteAllText(purityCachePath, json.Serialize(cache));
            }
            catch { }
        }

        private void AddHistory(string ip, string label, double latency, bool online)
        {
            try
            {
                var sample = new NodeSample
                {
                    sampled_at = DateTime.Now,
                    node_key = String.IsNullOrEmpty(ip) ? "offline" : ip,
                    label = label,
                    latency_ms = latency,
                    online = online
                };
                File.AppendAllText(historyPath, json.Serialize(sample) + Environment.NewLine);
            }
            catch { }
        }

        private string BuildHistorySummary()
        {
            try
            {
                if (!File.Exists(historyPath)) return "暂无历史记录";
                var samples = File.ReadLines(historyPath).Reverse().Take(1000)
                    .Select(line => { try { return json.Deserialize<NodeSample>(line); } catch { return null; } })
                    .Where(item => item != null && item.node_key != "offline")
                    .GroupBy(item => item.node_key)
                    .Select(group => new
                    {
                        Label = group.First().label,
                        Count = group.Count(),
                        Online = group.Count(x => x.online) * 100.0 / group.Count(),
                        Average = group.Where(x => x.online && x.latency_ms >= 0)
                            .Select(x => x.latency_ms).DefaultIfEmpty(0).Average()
                    })
                    .OrderByDescending(x => x.Online).ThenBy(x => x.Average).Take(3).ToList();
                var lines = new List<string> { "近30天节点记录" };
                int index = 1;
                foreach (var item in samples)
                    lines.Add(index++ + ". " + item.Label + "  " + Math.Round(item.Average) +
                        "ms  在线 " + Math.Round(item.Online) + "% · " + item.Count + "次");
                return String.Join(Environment.NewLine, lines);
            }
            catch { return "历史记录读取失败"; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (tray != null) tray.Dispose();
                if (refreshTimer != null) refreshTimer.Dispose();
                if (hoverTimer != null) hoverTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
