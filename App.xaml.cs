using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using D = System.Drawing;
using WF = System.Windows.Forms;

namespace StatsOSD
{
    public partial class App : Application
    {
        private OverlayWindow _overlay;
        private readonly Sensors _sensors = new Sensors();
        private DispatcherTimer _timer;
        private WF.NotifyIcon _tray;
        private Settings _cfg;
        private string _lastWarn = "<init>";
        private bool _sawData;
        private bool _demo;
        private BgAlphaWindow _bgWin;

        private WF.ToolStripMenuItem _miVisible;
        private WF.ToolStripMenuItem _miPassthru;
        private WF.ToolStripMenuItem _miTopmost;
        private WF.ToolStripMenuItem _miAutoStart;
        private readonly WF.ToolStripMenuItem[] _miCorner = new WF.ToolStripMenuItem[4];
        private readonly WF.ToolStripMenuItem[] _miIconPlace = new WF.ToolStripMenuItem[2];

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, a) => { Log("unhandled: " + a.Exception); a.Handled = true; };

            _cfg = Settings.Load();
            _demo = HasFlag(e.Args, "--demo");
            BuildTray();

            _overlay = new OverlayWindow();
            _overlay.SetPassthrough(_cfg.ClickThrough);
            _overlay.SetBackgroundAlpha(_cfg.BackgroundAlpha);
            _overlay.IsVisibleChanged += (s, a) => { if (_miVisible != null) _miVisible.Text = _overlay.IsVisible ? "隐藏 OSD" : "显示 OSD"; };
            _overlay.Show();
            _overlay.CornerIndex = _cfg.Corner;
            _overlay.SetForceTopmost(_cfg.ForceTopmost);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;
            _timer.Start();

            if (_demo) Log("demo mode: 使用合成数据（不读取真实传感器）");
            else _sensors.Open();

            if (AutoStart.IsEnabled()) AutoStart.Sync();

            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Log("started, admin=" + IsAdmin());

            // 排版自检：--shot <png路径>  启动几秒后把面板渲染成图片并退出
            string shotPath = ParseArg(e.Args, "--shot");
            if (shotPath != null)
            {
                var shotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                shotTimer.Tick += (s, a) =>
                {
                    shotTimer.Stop();
                    try { _overlay.SaveSnapshot(shotPath); Log("snapshot saved: " + shotPath); }
                    catch (Exception ex) { Log("snapshot error: " + ex.Message); }
                    ExitApp();
                };
                shotTimer.Start();
            }

            // 传感器排查：--dump <txt路径>  导出全部硬件/传感器后退出
            string dumpPath = ParseArg(e.Args, "--dump");
            if (dumpPath != null)
            {
                var dumpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                dumpTimer.Tick += (s, a) =>
                {
                    dumpTimer.Stop();
                    try { System.IO.File.WriteAllText(dumpPath, _sensors.DumpAll(), System.Text.Encoding.UTF8); Log("dump saved: " + dumpPath); }
                    catch (Exception ex) { Log("dump error: " + ex.Message); }
                    ExitApp();
                };
                dumpTimer.Start();
            }

            // 托盘位置：--traypos promoted|overflow|status  执行后退出
            string tpArg = ParseArg(e.Args, "--traypos");
            if (tpArg != null)
            {
                if (string.Equals(tpArg, "status", StringComparison.OrdinalIgnoreCase))
                {
                    bool? cur = TrayPlacement.IsPromoted();
                    Log("traypos status -> " + (cur == null ? "未登记（图标从未出现）" : (cur.Value ? "常显区" : "溢出区")));
                }
                else
                {
                    string msg;
                    bool ok = TrayPlacement.SetPromoted(
                        string.Equals(tpArg, "promoted", StringComparison.OrdinalIgnoreCase), out msg);
                    Log("traypos " + tpArg + " -> " + (ok ? "OK" : "FAIL") + " / " + msg);
                }
                ExitApp();
                return;
            }

            // 自启管理：--autostart on|off|status  执行后退出（等价于托盘里的"开机自启"开关）
            string asArg = ParseArg(e.Args, "--autostart");
            if (asArg != null)
            {
                string how;
                if (string.Equals(asArg, "on", StringComparison.OrdinalIgnoreCase))
                {
                    bool ok = AutoStart.Enable(out how);
                    Log("autostart on -> " + (ok ? "OK" : "FAIL") + " / " + how);
                }
                else if (string.Equals(asArg, "off", StringComparison.OrdinalIgnoreCase))
                {
                    AutoStart.Disable(out how);
                    Log("autostart off -> " + how);
                }
                else
                {
                    Log("autostart status -> enabled=" + AutoStart.IsEnabled());
                }
                ExitApp();
                return;
            }

            // 图标自检：--iconshot <png路径>  导出实际使用的托盘图标后退出
            string iconPath = ParseArg(e.Args, "--iconshot");
            if (iconPath != null)
            {
                try
                {
                    using (var ic = LoadAppIcon())
                    {
                        Log("tray icon resolved: " + ic.Width + "x" + ic.Height);
                        using (var bmp = ic.ToBitmap()) bmp.Save(iconPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Log("icon shot saved: " + iconPath);
                }
                catch (Exception ex) { Log("icon shot error: " + ex.Message); }
                ExitApp();
                return;
            }
        }

        private static string ParseArg(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }

        private static bool HasFlag(string[] args, string name)
        {
            if (args == null) return false;
            foreach (string a in args)
            {
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>演示/自检用合成数据：CPU 85（应为橙）、GPU 90（应为红）</summary>
        private Sample MakeDemoSample()
        {
            return new Sample
            {
                CpuTemp = 85,
                CpuLoad = 42,
                CpuPower = 88,
                GpuTemp = 90,
                GpuLoad = 76,
                GpuPower = 165
            };
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            _overlay?.ApplyCorner();
        }

        private void OnTick(object sender, EventArgs e)
        {
            Sample s = _demo ? MakeDemoSample() : _sensors.Sample();
            _overlay.Update(s);

            if (s.Warn != _lastWarn)
            {
                _lastWarn = s.Warn;
                App.Log("warn: " + (s.Warn ?? "(none)"));
            }
            if (s.HasAny && !_sawData)
            {
                _sawData = true;
                App.Log(string.Format("sample ok: cpuTemp={0} cpuLoad={1} gpuTemp={2} gpuLoad={3} cores={4}",
                    s.CpuTemp, s.CpuLoad, s.GpuTemp, s.GpuLoad));
            }

            string tip = s.CpuTemp.HasValue ? "CPU " + s.CpuTemp.Value.ToString("0") + "\u00B0C" : "CPU --";
            tip += s.GpuTemp.HasValue ? "  GPU " + s.GpuTemp.Value.ToString("0") + "\u00B0C" : "  GPU --";
            try { if (tip.Length > 60) tip = tip.Substring(0, 60); _tray.Text = tip; } catch { }
        }

        // ---------- tray ----------

        private void BuildTray()
        {
            _tray = new WF.NotifyIcon { Icon = LoadAppIcon(), Text = "StatsOSD", Visible = true };
            var menu = new WF.ContextMenuStrip();

            // ── 组 1：主操作 ──
            _miVisible = Add(menu, "隐藏 OSD", (s, e) => ToggleVisible());

            menu.Items.Add(new WF.ToolStripSeparator());

            // ── 组 2：带二级菜单 / 二级窗口的设置 ──
            var miPos = new WF.ToolStripMenuItem("面板位置");
            string[] names = { "左上角", "右上角", "左下角", "右下角" };
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                _miCorner[i] = new WF.ToolStripMenuItem(names[i], null, (s, e) => SetCorner(k));
                miPos.DropDownItems.Add(_miCorner[i]);
            }
            menu.Items.Add(miPos);

            // 小图标位置（悬停二级菜单）：托盘常显区 / 溢出区
            var miIcon = new WF.ToolStripMenuItem("图标位置");
            string[] places = { "托盘常显区", "托盘溢出区（默认）" };
            for (int i = 0; i < 2; i++)
            {
                int k = i;
                _miIconPlace[i] = new WF.ToolStripMenuItem(places[i], null, (s, e) => SetIconPlacement(k));
                miIcon.DropDownItems.Add(_miIconPlace[i]);
            }
            menu.Items.Add(miIcon);

            Add(menu, "透明度…", (s, e) => OpenBgWindow());

            menu.Items.Add(new WF.ToolStripSeparator());

            // ── 组 3：单纯开关（开机自启固定放这一组最后）──
            _miPassthru = Add(menu, "鼠标穿透", (s, e) => TogglePassthrough());
            _miPassthru.CheckOnClick = true;
            _miTopmost = Add(menu, "强制置顶", (s, e) => ToggleForceTopmost());
            _miTopmost.CheckOnClick = true;
            _miAutoStart = Add(menu, "开机自启", (s, e) => ToggleAutoStart());
            _miAutoStart.CheckOnClick = true;

            menu.Items.Add(new WF.ToolStripSeparator());

            // ── 组 4：工具 ──
            Add(menu, "导出传感器", (s, e) => DumpSensorsToFile());
            Add(menu, "重启资源管理器", (s, e) => RestartExplorer());

            menu.Items.Add(new WF.ToolStripSeparator());

            // ── 组 5：权限与退出 ──
            if (IsAdmin())
            {
                var miAdmin = Add(menu, "已以管理员运行", null);
                miAdmin.Enabled = false;
            }
            else
            {
                Add(menu, "以管理员重启", (s, e) => RestartAsAdmin());
            }

            Add(menu, "退出", (s, e) => ExitApp());

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => ToggleVisible();

            _miPassthru.Checked = _cfg.ClickThrough;
            _miTopmost.Checked = _cfg.ForceTopmost;
            _miAutoStart.Checked = AutoStart.IsEnabled();
            SyncCornerChecks();
            SyncIconPlaceChecks();
        }

        private WF.ToolStripMenuItem Add(WF.ContextMenuStrip m, string text, EventHandler handler)
        {
            var item = new WF.ToolStripMenuItem(text, null, handler);
            m.Items.Add(item);
            return item;
        }

        private void SyncCornerChecks()
        {
            for (int i = 0; i < 4; i++) _miCorner[i].Checked = _cfg.Corner == i;
        }

        private void SyncIconPlaceChecks()
        {
            bool promoted = TrayPlacement.IsPromoted() == true;
            _miIconPlace[0].Checked = promoted;
            _miIconPlace[1].Checked = !promoted;
        }

        private void SetIconPlacement(int k)
        {
            bool wantPromoted = k == 0;
            string msg;
            bool ok = TrayPlacement.SetPromoted(wantPromoted, out msg);
            if (ok) ReRegisterTrayIcon();
            SyncIconPlaceChecks();
            Log("icon placement: wantPromoted=" + wantPromoted + " ok=" + ok);
            try
            {
                _tray.ShowBalloonTip(4000, "StatsOSD",
                    msg + (ok ? "\n若未立即生效：重开本程序，或用菜单里的「重启资源管理器」" : ""),
                    ok ? WF.ToolTipIcon.Info : WF.ToolTipIcon.Warning);
            }
            catch { }
        }

        /// <summary>注销并重新注册托盘图标，促使资源管理器重新读取托盘位置设置</summary>
        private void ReRegisterTrayIcon()
        {
            try
            {
                _tray.Visible = false;
                System.Threading.Thread.Sleep(150);
                _tray.Visible = true;
            }
            catch (Exception ex) { Log("re-register tray icon error: " + ex.Message); }
        }

        /// <summary>重启资源管理器（任务栏会闪一下），让托盘位置设置确实生效</summary>
        private void RestartExplorer()
        {
            try
            {
                foreach (Process pr in Process.GetProcessesByName("explorer"))
                {
                    try { pr.Kill(); } catch { }
                }
                System.Threading.Thread.Sleep(900);
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                Log("explorer restarted (tray placement)");
            }
            catch (Exception ex) { Log("restart explorer error: " + ex.Message); }
        }

        private void ToggleVisible()
        {
            if (_overlay == null) return;
            if (_overlay.IsVisible) _overlay.Hide(); else _overlay.Show();
        }

        private void SetCorner(int k)
        {
            _cfg.Corner = k;
            _cfg.Save();
            if (_overlay != null) _overlay.CornerIndex = k;
            SyncCornerChecks();
        }

        private void TogglePassthrough()
        {
            _cfg.ClickThrough = _miPassthru.Checked;
            _cfg.Save();
            _overlay?.SetPassthrough(_cfg.ClickThrough);
        }

        private void ToggleForceTopmost()
        {
            _cfg.ForceTopmost = _miTopmost.Checked;
            _cfg.Save();
            _overlay?.SetForceTopmost(_cfg.ForceTopmost);
        }

        private void ToggleAutoStart()
        {
            bool want = _miAutoStart.Checked;
            string how;
            bool ok = want ? AutoStart.Enable(out how) : AutoStart.Disable(out how);
            if (want && !ok) _miAutoStart.Checked = false;
            Log("autostart toggle: want=" + want + " ok=" + ok + " how=" + how);
            try
            {
                _tray.ShowBalloonTip(3500, "StatsOSD",
                    (want ? (ok ? "已开启开机自启：" : "开启失败：") : "已关闭开机自启：") + how,
                    ok ? WF.ToolTipIcon.Info : WF.ToolTipIcon.Warning);
            }
            catch { }
        }

        private void OpenBgWindow()
        {
            if (_bgWin != null)
            {
                try { _bgWin.Activate(); } catch { }
                return;
            }
            _bgWin = new BgAlphaWindow(_cfg, v => _overlay?.SetBackgroundAlpha(v));
            try { _bgWin.Icon = LoadAppIconSource(); } catch { }
            _bgWin.Closed += (s, e) => _bgWin = null;
            _bgWin.Show();
        }

        private void RestartAsAdmin()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                ExitApp();
            }
            catch (Exception)
            {
                try { _tray.ShowBalloonTip(2000, "StatsOSD", "已取消（未获得管理员权限）", WF.ToolTipIcon.Info); } catch { }
            }
        }

        private void DumpSensorsToFile()
        {
            string path = Path.Combine(Path.GetTempPath(), "statsosd-sensors.txt");
            try
            {
                File.WriteAllText(path, _sensors.DumpAll(), System.Text.Encoding.UTF8);
                Log("sensors dumped: " + path);
                try { _tray.ShowBalloonTip(2500, "StatsOSD", "传感器清单已导出：\n" + path, WF.ToolTipIcon.Info); } catch { }
            }
            catch (Exception ex)
            {
                Log("dump error: " + ex.Message);
                try { _tray.ShowBalloonTip(2500, "StatsOSD", "导出失败：" + ex.Message, WF.ToolTipIcon.Warning); } catch { }
            }
        }

        private void ExitApp()
        {
            try { _cfg.Save(); } catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            try { _timer?.Stop(); } catch { }
            try { _sensors.Close(); } catch { }
            try { _overlay?.Close(); } catch { }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; } catch { }
            base.OnExit(e);
        }

        // ---------- helpers ----------

        /// <summary>托盘图标：用内嵌的多尺寸 app 图标，按系统托盘推荐尺寸取帧；失败则退回程序绘制的圆点</summary>
        private static D.Icon LoadAppIcon()
        {
            try
            {
                var res = Application.GetResourceStream(new Uri("assets/StatsOSD.ico", UriKind.Relative));
                if (res != null)
                {
                    using (var s = res.Stream)
                    {
                        var ms = new MemoryStream();
                        s.CopyTo(ms);
                        ms.Position = 0;
                        int sz = WF.SystemInformation.SmallIconSize.Width;
                        if (sz <= 0) sz = 16;
                        return new D.Icon(ms, new D.Size(sz, sz));
                    }
                }
            }
            catch (Exception ex) { Log("tray icon load error: " + ex.Message); }
            return IconFactory.Make();
        }

        /// <summary>WPF 窗口图标（设置对话框标题栏用）</summary>
        private static System.Windows.Media.ImageSource LoadAppIconSource()
        {
            var res = Application.GetResourceStream(new Uri("assets/StatsOSD.ico", UriKind.Relative));
            if (res == null) return null;
            using (var s = res.Stream)
            {
                var ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;
                return System.Windows.Media.Imaging.BitmapFrame.Create(
                    ms,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            }
        }

        public static bool IsAdmin()
        {
            try
            {
                return new WindowsPrincipal(WindowsIdentity.GetCurrent())
                    .IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static void Log(string msg)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "statsosd.log"), DateTime.Now.ToString("HH:mm:ss") + " " + msg + Environment.NewLine); } catch { }
        }
    }

    internal static class IconFactory
    {
        public static D.Icon Make()
        {
            using var bmp = new D.Bitmap(16, 16);
            using (var g = D.Graphics.FromImage(bmp))
            {
                g.Clear(D.Color.Transparent);
                using var fill = new D.SolidBrush(D.Color.FromArgb(255, 255, 138, 0));
                g.FillEllipse(fill, 1, 1, 14, 14);
                using var pen = new D.Pen(D.Color.White, 1.8f);
                g.DrawLine(pen, 8, 9, 12, 5);
            }
            return D.Icon.FromHandle(bmp.GetHicon());
        }
    }
}
