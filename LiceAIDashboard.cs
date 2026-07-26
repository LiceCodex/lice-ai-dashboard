using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Lice AI Dashboard")]
[assembly: System.Reflection.AssemblyDescription("Codex usage and network tray dashboard")]
[assembly: System.Reflection.AssemblyCompany("Lice")]
[assembly: System.Reflection.AssemblyProduct("Lice AI Dashboard")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

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

    internal sealed class DashboardForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(11, 13, 18);
        private static readonly Color Card = Color.FromArgb(21, 24, 33);
        private static readonly Color TextColor = Color.FromArgb(244, 245, 247);
        private static readonly Color Muted = Color.FromArgb(141, 148, 163);
        private static readonly Color Green = Color.FromArgb(67, 209, 123);
        private static readonly Color Yellow = Color.FromArgb(255, 202, 92);
        private static readonly Color Red = Color.FromArgb(255, 105, 120);
        private static readonly Color Accent = Color.FromArgb(124, 140, 255);

        private readonly string appDir;
        private readonly string configPath;
        private readonly string historyPath;
        private readonly string statusPath;
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
        private Label healthMeta;
        private Label updated;
        private CheckBox autoStartToggle;
        private Panel settingsPanel;

        public DashboardForm(bool startHidden)
        {
            this.startHidden = startHidden;
            appDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiceAIDashboard");
            configPath = Path.Combine(appDir, "config-exe.json");
            historyPath = Path.Combine(appDir, "vpn-history-exe.jsonl");
            statusPath = Path.Combine(appDir, "last-status.json");
            Directory.CreateDirectory(appDir);
            config = LoadConfig();

            Text = "Lice AI Dashboard";
            BackColor = Bg;
            ForeColor = TextColor;
            Font = new Font("Microsoft YaHei UI", 9F);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(396, 610);
            MinimumSize = new Size(370, 540);
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
            base.OnPaint(e);
            using (var pen = new Pen(Color.FromArgb(45, 49, 62)))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        private void BuildUi()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Bg };
            Controls.Add(header);
            var logo = new PictureBox
            {
                Image = BuildLogo(32),
                SizeMode = PictureBoxSizeMode.StretchImage,
                Bounds = new Rectangle(14, 13, 32, 32)
            };
            header.Controls.Add(logo);
            header.Controls.Add(NewLabel("Lice AI Dashboard", 52, 16, 210, 28, 16, true, TextColor));

            var settingsButton = NewButton("设置", 278, 14, 50, 30);
            settingsButton.Click += delegate { settingsPanel.Visible = !settingsPanel.Visible; };
            header.Controls.Add(settingsButton);
            var hideButton = NewButton("—", 334, 14, 42, 30);
            hideButton.Click += delegate { HideToTray(); };
            header.Controls.Add(hideButton);

            var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12, 2, 12, 8) };
            Controls.Add(body);
            body.BringToFront();

            settingsPanel = NewCard(104);
            settingsPanel.Visible = false;
            settingsPanel.Controls.Add(NewLabel("启动与托盘", 14, 10, 220, 24, 11, true, TextColor));
            autoStartToggle = new CheckBox
            {
                Text = "登录 Windows 后自动启动",
                ForeColor = TextColor,
                BackColor = Card,
                AutoSize = true,
                Location = new Point(16, 43),
                Checked = IsAutoStartEnabled()
            };
            autoStartToggle.CheckedChanged += delegate
            {
                config.auto_start = autoStartToggle.Checked;
                SaveConfig();
                ApplyAutoStart(autoStartToggle.Checked);
            };
            settingsPanel.Controls.Add(autoStartToggle);
            settingsPanel.Controls.Add(NewLabel("托盘悬停展开，移开后自动收起", 16, 70, 300, 20, 9, false, Muted));
            body.Controls.Add(settingsPanel);
            settingsPanel.Dock = DockStyle.Top;

            var weekly = NewCard(122);
            weekly.Controls.Add(NewLabel("Codex 周额度", 14, 10, 220, 24, 11, true, TextColor));
            weeklyValue = NewLabel("正在读取…", 14, 39, 320, 32, 21, true, TextColor);
            weekly.Controls.Add(weeklyValue);
            var barBg = new Panel { BackColor = Color.FromArgb(39, 43, 55), Bounds = new Rectangle(14, 78, 336, 8) };
            weeklyProgress = new Panel { BackColor = Accent, Bounds = new Rectangle(0, 0, 0, 8) };
            barBg.Controls.Add(weeklyProgress);
            weekly.Controls.Add(barBg);
            weeklyMeta = NewLabel("刷新时间未知", 14, 92, 338, 20, 9, false, Muted);
            weekly.Controls.Add(weeklyMeta);
            body.Controls.Add(weekly);
            weekly.Dock = DockStyle.Top;

            var vpn = NewCard(174);
            vpn.Controls.Add(NewLabel("VPN / 网络节点", 14, 10, 240, 24, 11, true, TextColor));
            vpnValue = NewLabel("正在检测…", 14, 40, 330, 30, 17, true, TextColor);
            vpn.Controls.Add(vpnValue);
            vpnMeta = NewLabel("", 14, 73, 336, 20, 9, false, Muted);
            vpn.Controls.Add(vpnMeta);
            vpnHistory = NewLabel("", 14, 98, 336, 66, 9, false, Muted);
            vpnHistory.AutoEllipsis = true;
            vpn.Controls.Add(vpnHistory);
            body.Controls.Add(vpn);
            vpn.Dock = DockStyle.Top;

            var health = NewCard(122);
            health.Controls.Add(NewLabel("服务状态", 14, 10, 240, 24, 11, true, TextColor));
            healthMeta = NewLabel("正在检测…", 14, 39, 336, 72, 9, false, Muted);
            health.Controls.Add(healthMeta);
            body.Controls.Add(health);
            health.Dock = DockStyle.Top;
            body.Controls.SetChildIndex(settingsPanel, 0);
            body.Controls.SetChildIndex(weekly, 1);
            body.Controls.SetChildIndex(vpn, 2);
            body.Controls.SetChildIndex(health, 3);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Bg };
            updated = NewLabel("", 16, 7, 220, 18, 8, false, Muted);
            footer.Controls.Add(updated);
            Controls.Add(footer);
            footer.BringToFront();
        }

        private Panel NewCard(int height)
        {
            return new Panel
            {
                BackColor = Card,
                Height = height,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 10),
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
                BackColor = Color.Transparent,
                Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private Button NewButton(string text, int x, int y, int w, int h)
        {
            return new Button
            {
                Text = text,
                Bounds = new Rectangle(x, y, w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(34, 38, 51),
                ForeColor = TextColor,
                Cursor = Cursors.Hand,
                TabStop = false
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
                    weeklyProgress.Width = (int)(336 * remaining / 100.0);
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
                healthMeta.Text = String.Join(Environment.NewLine, healthLines);
                healthMeta.ForeColor = healthLines.All(line => line.Contains("正常")) ? Green : Red;
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
                        services = healthLines
                    }));
                }
                catch { }
            }
            finally { refreshing = false; }
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
