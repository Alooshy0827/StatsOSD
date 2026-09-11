using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using D = System.Drawing;
using WF = System.Windows.Forms;

namespace CpuHud
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
        private BgAlphaWindow _bgWin;

        private WF.ToolStripMenuItem _miVisible;
        private WF.ToolStripMenuItem _miPassthru;
        private WF.ToolStripMenuItem _miCores;
        private readonly WF.ToolStripMenuItem[] _miCorner = new WF.ToolStripMenuItem[4];

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, a) => { Log("unhandled: " + a.Exception); a.Handled = true; };

            _cfg = Settings.Load();
            BuildTray();

            _overlay = new OverlayWindow { ShowCores = _cfg.ShowCores };
            _overlay.SetPassthrough(_cfg.ClickThrough);
            _overlay.SetBackgroundAlpha(_cfg.BackgroundAlpha);
            _overlay.IsVisibleChanged += (s, a) => { if (_miVisible != null) _miVisible.Text = _overlay.IsVisible ? "隐藏 OSD" : "显示 OSD"; };
            _overlay.Show();
            _overlay.CornerIndex = _cfg.Corner;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;
            _timer.Start();

            _sensors.Open();

            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Log("started, admin=" + IsAdmin());

            // 排版自检：--shot <png路径>  启动几秒后把面板渲染成图片并退出
            string shotPath = ParseShotArg(e.Args);
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
        }

        private static string ParseShotArg(string[] args)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--shot", StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            _overlay?.ApplyCorner();
        }

        private void OnTick(object sender, EventArgs e)
        {
            Sample s = _sensors.Sample();
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
                    s.CpuTemp, s.CpuLoad, s.GpuTemp, s.GpuLoad, s.Cores.Count));
            }

            string tip = s.CpuTemp.HasValue ? "CPU " + s.CpuTemp.Value.ToString("0") + "\u00B0C" : "CPU --";
            tip += s.GpuTemp.HasValue ? "  GPU " + s.GpuTemp.Value.ToString("0") + "\u00B0C" : "  GPU --";
            try { if (tip.Length > 60) tip = tip.Substring(0, 60); _tray.Text = tip; } catch { }
        }

        // ---------- tray ----------

        private void BuildTray()
        {
            _tray = new WF.NotifyIcon { Icon = IconFactory.Make(), Text = "CpuHud", Visible = true };
            var menu = new WF.ContextMenuStrip();

            _miVisible = Add(menu, "隐藏 OSD", (s, e) => ToggleVisible());

            menu.Items.Add(new WF.ToolStripSeparator());

            var miPos = new WF.ToolStripMenuItem("位置");
            string[] names = { "左上角", "右上角", "左下角", "右下角" };
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                _miCorner[i] = new WF.ToolStripMenuItem(names[i], null, (s, e) => SetCorner(k));
                miPos.DropDownItems.Add(_miCorner[i]);
            }
            menu.Items.Add(miPos);

            _miPassthru = Add(menu, "鼠标穿透（不挡游戏操作）", (s, e) => TogglePassthrough());
            _miPassthru.CheckOnClick = true;
            _miCores = Add(menu, "显示每核心温度", (s, e) => ToggleCores());
            _miCores.CheckOnClick = true;

            Add(menu, "背景透明度…", (s, e) => OpenBgWindow());

            menu.Items.Add(new WF.ToolStripSeparator());

            if (IsAdmin())
            {
                var miAdmin = Add(menu, "已以管理员身份运行（完整传感器）", null);
                miAdmin.Enabled = false;
            }
            else
            {
                Add(menu, "以管理员身份重新启动（读取温度需要）", (s, e) => RestartAsAdmin());
            }

            Add(menu, "退出", (s, e) => ExitApp());

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => ToggleVisible();

            _miPassthru.Checked = _cfg.ClickThrough;
            _miCores.Checked = _cfg.ShowCores;
            SyncCornerChecks();
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

        private void ToggleCores()
        {
            _cfg.ShowCores = _miCores.Checked;
            _cfg.Save();
            if (_overlay != null) _overlay.ShowCores = _cfg.ShowCores;
        }

        private void OpenBgWindow()
        {
            if (_bgWin != null)
            {
                try { _bgWin.Activate(); } catch { }
                return;
            }
            _bgWin = new BgAlphaWindow(_cfg, v => _overlay?.SetBackgroundAlpha(v));
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
                try { _tray.ShowBalloonTip(2000, "CpuHud", "已取消（未获得管理员权限）", WF.ToolTipIcon.Info); } catch { }
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
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "cpuhud.log"), DateTime.Now.ToString("HH:mm:ss") + " " + msg + Environment.NewLine); } catch { }
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
