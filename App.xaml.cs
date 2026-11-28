using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
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
        private TaskbarReadings _taskbarReadings;
        private Settings _cfg;
        private string _lastWarn = "<init>";
        private bool _sawData;
        private bool _demo;
        private BgAlphaWindow _bgWin;

        // 后台采样：采样跑在独立线程，界面只读缓存 —— 底层驱动卡住时界面不会跟着冻结
        private System.Threading.Timer _sampler;
        private volatile Sample _latest;
        private long _lastSampleTicksUtc;
        private readonly object _sensorGate = new object();
        private long _nextOpenAttempt;
        private volatile bool _stopping;
        private bool _dumping;
        private bool _systemMenuBusy;
        private bool _vendorsApplied;
        private string _vendorArgCpu, _vendorArgGpu;
        private string _menuDumpPath;
        private bool _stallLogged;
        private System.Threading.Mutex _singleInstance;

        private WF.ToolStripMenuItem _miVisible;
        private WF.ToolStripMenuItem _miDrag;
        private WF.ToolStripMenuItem _miOpacity;
        private WF.ToolStripMenuItem _miFreePosition;
        private WF.ToolStripMenuItem _miPassthru;
        private WF.ToolStripMenuItem _miTopmost;
        private WF.ToolStripMenuItem _miAutoStart;
        private readonly WF.ToolStripMenuItem[] _miCorner = new WF.ToolStripMenuItem[4];
        private readonly List<WF.ToolStripMenuItem> _miMetrics = new List<WF.ToolStripMenuItem>();
        private readonly WF.ToolStripMenuItem[] _miLayout = new WF.ToolStripMenuItem[3];
        private WF.ToolStripMenuItem _miHorizontalGroups;
        private WF.ToolStripMenuItem _miVerticalGroups;
        private readonly WF.ToolStripMenuItem[] _miFont = new WF.ToolStripMenuItem[4];
        private WF.ToolStripMenuItem _miOutline;
        private readonly WF.ToolStripMenuItem[] _miIconPlace = new WF.ToolStripMenuItem[2];
        private static readonly string[] LayoutIds = { "mini", "standard", "detailed" };
        private static readonly double[] FontScales = { 0.85, 1.0, 1.2, 1.4 };

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, a) => { Log("unhandled: " + a.Exception); a.Handled = true; };

            // 单实例保护：避免出现两个面板/两个托盘图标，以及重复加载传感器驱动
            // （自检类参数 --shot/--dump/--menu/--iconshot/--autostart/--traypos 不参与，方便诊断截图）
            bool toolMode = ParseArg(e.Args, "--shot") != null || ParseArg(e.Args, "--iconshot") != null || ParseArg(e.Args, "--dump") != null
                         || ParseArg(e.Args, "--menu") != null || ParseArg(e.Args, "--autostart") != null
                         || ParseArg(e.Args, "--traypos") != null;
            if (!toolMode)
            {
                var mutex = new Mutex(false, "StatsOSD_SingleInstance");
                if (!AcquireInstance(mutex, HasFlag(e.Args, "--restart")))
                {
                    mutex.Dispose();
                    WF.MessageBox.Show("StatsOSD 已经在运行（请查看任务栏托盘图标）。", "StatsOSD",
                        WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Information);
                    Shutdown();
                    return;
                }
                _singleInstance = mutex;
            }

            RotateLogIfNeeded();

            _cfg = Settings.Load();
            _cfg.SaveFailed += OnSettingsSaveFailed;
            _demo = HasFlag(e.Args, "--demo");
            _menuDumpPath = ParseArg(e.Args, "--menu");

            // 演示/截图用：--vendors AMD,NVIDIA 覆盖厂商识别（影响分组配色）
            string vendors = ParseArg(e.Args, "--vendors");
            if (!string.IsNullOrEmpty(vendors))
            {
                string[] parts = vendors.Split(',');
                if (parts.Length > 0) _vendorArgCpu = parts[0].Trim();
                if (parts.Length > 1) _vendorArgGpu = parts[1].Trim();
                Log("vendor override: cpu=" + _vendorArgCpu + " gpu=" + _vendorArgGpu);
            }
            BuildTray();
            if (!toolMode) _ = UpdateSystemMenuAsync(syncStartup: true);

            _overlay = new OverlayWindow();
            ApplyTextSettings();
            _overlay.DoubleClicked = OpenTaskManager;
            _overlay.SetPassthrough(_cfg.ClickThrough);
            SetBackgroundAlpha(_cfg.BackgroundAlpha);
            _taskbarReadings.SetEnabled(_cfg.ShowTaskbarReadings);
            _overlay.IsVisibleChanged += (s, a) =>
            {
                _miVisible.Checked = _overlay.IsVisible;
                _miDrag.Enabled = _overlay.IsVisible;
                _miFreePosition.Enabled = _overlay.IsVisible;
            };
            if (_cfg.ShowDesktopOverlay || ParseArg(e.Args, "--shot") != null)
                ShowDesktopOverlay();

            // 拖动模式演示/自检：--drag  启动后自动进入拖动模式
            if (HasFlag(e.Args, "--drag"))
            {
                var dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                dragTimer.Tick += (s, a) => { dragTimer.Stop(); StartDragMode(); };
                dragTimer.Start();
            }

            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
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
                dumpTimer.Tick += async (s, a) =>
                {
                    dumpTimer.Stop();
                    await DumpSensorsAsync(dumpPath, false);
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
                    Log("traypos status -> " + (cur == null ? "未知（没有一致的可读位置记录）" : (cur.Value ? "常显区" : "溢出区")));
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
                    bool? enabled = AutoStart.ReadState(out string error);
                    Log("autostart status -> " + (enabled.HasValue ? "enabled=" + enabled.Value : "未知：" + error));
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

            if (_menuDumpPath != null && shotPath == null && dumpPath == null)
            {
                ExitApp();
                return;
            }

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;                  // UI 线程只负责把缓存的数据画出来
            _timer.Start();

            if (_demo) Log("demo mode: 使用合成数据（不读取真实传感器）");

            // 采样在后台线程进行（每秒一次），界面永不因驱动调用而卡死
            _sampler = new System.Threading.Timer(DoSample, null, 0, 1000);
            Log("background sampler started");
        }

        private static bool AcquireInstance(Mutex mutex, bool restart)
        {
            try { return mutex.WaitOne(restart ? 15000 : 0); }
            catch (AbandonedMutexException) { return true; }
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
                CpuClock = 4380,
                GpuTemp = 90,
                GpuLoad = 76,
                GpuPower = 165,
                GpuHotspot = 97,
                GpuClock = 2415,
                GpuVram = 6144,
                GpuFan = 68,
                MemPercent = 63,
                MemUsedGb = 19.7,
                MemTotalGb = 32.0
            };
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            _overlay?.ApplyCorner();
        }

        /// <summary>系统睡眠/休眠期间采样本就不会进行，恢复时重置时间戳，避免看门狗误报"传感器无响应"</summary>
        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            Interlocked.Exchange(ref _lastSampleTicksUtc, DateTime.UtcNow.Ticks);
            _stallLogged = false;
            Log("system resumed from sleep (watchdog timer reset)");
            _overlay?.ApplyCorner();
        }

        private void OnTick(object sender, EventArgs e)
        {
            // 硬件厂商拿到后应用一次（用于分组配色）
            if (!_vendorsApplied)
            {
                string cpuV = !string.IsNullOrEmpty(_vendorArgCpu) ? _vendorArgCpu : (_demo ? "Intel" : null);
                string gpuV = !string.IsNullOrEmpty(_vendorArgGpu) ? _vendorArgGpu : (_demo ? "NVIDIA" : null);
                if (_demo || _sensors.IsOpen)
                {
                    _vendorsApplied = true;
                    string cpuF = cpuV ?? _sensors.CpuVendor;
                    string gpuF = gpuV ?? _sensors.GpuVendor;
                    _overlay.SetVendors(cpuF, gpuF);
                    _taskbarReadings.SetBackgrounds(_overlay.GetGroupBackground);
                    _overlay.SetModels(
                        Sensors.ShortModel(_demo ? DemoModel(cpuF) : _sensors.CpuName, false),
                        Sensors.ShortModel(_demo ? DemoModel(gpuF) : _sensors.GpuName, true),
                        _demo ? "DDR5 5600" : _sensors.MemType);
                }
            }

            Sample s = _latest;
            if (s == null)
            {
                _taskbarReadings.Update(null);
                _overlay.Update(new Sample { Warn = "正在初始化传感器…" });
                return;
            }

            // 看门狗：采样停滞超过 5 秒时明确提示，而不是让界面看起来"卡死"
            double ageSec = (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastSampleTicksUtc), DateTimeKind.Utc)).TotalSeconds;
            if (ageSec > 5)
            {
                if (!_stallLogged)
                {
                    _stallLogged = true;
                    Log("watchdog: sampling stalled for " + ageSec.ToString("0") + "s");
                }
                s = s.Clone();
                s.Warn = "传感器无响应（显示为最后已知值）";
            }
            else if (_stallLogged)
            {
                _stallLogged = false;
                Log("watchdog: sampling recovered");
            }

            _overlay.Update(s);

            if (s.Warn != _lastWarn)
            {
                _lastWarn = s.Warn;
                App.Log("warn: " + (s.Warn ?? "(none)"));
            }
            if (s.HasAny && !_sawData)
            {
                _sawData = true;
                App.Log(string.Format("sample ok: cpuTemp={0} cpuLoad={1} cpuPower={2} gpuTemp={3} gpuLoad={4} gpuPower={5}",
                    s.CpuTemp, s.CpuLoad, s.CpuPower, s.GpuTemp, s.GpuLoad, s.GpuPower));
            }

            _taskbarReadings.Update(ageSec > 5 ? null : s);
            string tip = s.CpuTemp.HasValue ? "CPU " + s.CpuTemp.Value.ToString("0") + "\u00B0C" : "CPU --";
            tip += s.GpuTemp.HasValue ? "  GPU " + s.GpuTemp.Value.ToString("0") + "\u00B0C" : "  GPU --";
            try { if (tip.Length > 60) tip = tip.Substring(0, 60); _tray.Text = tip; } catch { }
        }

        /// <summary>后台采样线程：绝不阻塞 UI；上一次没返回（可能卡在驱动里）就跳过本次</summary>
        private void DoSample(object state)
        {
            if (_stopping || !Monitor.TryEnter(_sensorGate)) return;
            try
            {
                if (_stopping) return;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                if (_demo)
                {
                    _latest = MakeDemoSample();
                }
                else
                {
                    if (!_sensors.IsOpen && Environment.TickCount64 >= _nextOpenAttempt)
                    {
                        _sensors.Open();
                        _nextOpenAttempt = Environment.TickCount64 + 10000;
                    }
                    _latest = _sensors.Sample();
                }

                Interlocked.Exchange(ref _lastSampleTicksUtc, DateTime.UtcNow.Ticks);
                sw.Stop();
                if (sw.ElapsedMilliseconds > 2000) Log("slow sample: " + sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex)
            {
                Log("sample thread error: " + ex.Message);
            }
            finally
            {
                Monitor.Exit(_sensorGate);
            }
        }

        /// <summary>日志轮转：超过 512KB 归档为 .old，避免无限增长</summary>
        private static void RotateLogIfNeeded()
        {
            try
            {
                string logPath = Path.Combine(Path.GetTempPath(), "statsosd.log");
                if (!File.Exists(logPath)) return;
                if (new FileInfo(logPath).Length < 512 * 1024) return;
                string old = logPath + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(logPath, old);
            }
            catch { }
        }

        // ---------- tray ----------

        private void BuildTray()
        {
            _tray = new WF.NotifyIcon { Icon = LoadAppIcon(), Text = "StatsOSD", Visible = true };
            var menu = new WF.ContextMenuStrip { Renderer = new TrayMenuRenderer() };

            _miVisible = AddToggle(menu.Items, "显示为悬浮窗", (s, e) => ToggleVisible(), _cfg.ShowDesktopOverlay);
            AddToggle(menu.Items, "显示在任务栏", (s, e) =>
            {
                _cfg.ShowTaskbarReadings = ((WF.ToolStripMenuItem)s).Checked;
                _taskbarReadings.SetEnabled(_cfg.ShowTaskbarReadings);
                _cfg.Save();
            }, _cfg.ShowTaskbarReadings);
            _miDrag = Add(menu.Items, "拖动调整位置", (s, e) => StartDragMode());
            _miDrag.Enabled = _cfg.ShowDesktopOverlay;
            menu.Items.Add(new WF.ToolStripSeparator());

            BuildDesktopMenu(Add(menu.Items, "桌面面板"));
            BuildAppearanceMenu(Add(menu.Items, "共用外观"));

            var system = Add(menu.Items, "启动与托盘");
            _miAutoStart = AddToggle(system.DropDownItems, "开机自启", (s, e) => ToggleAutoStart(), false);
            var iconPosition = Add(system.DropDownItems, "托盘图标位置");
            string[] places = { "托盘常显区", "托盘溢出区" };
            for (int i = 0; i < places.Length; i++)
            {
                int index = i;
                _miIconPlace[i] = AddChoice(iconPosition, places[i], (s, e) => SetIconPlacement(index));
            }
            system.DropDownOpening += (s, e) => RefreshSystemChecks();

            var tools = Add(menu.Items, "工具").DropDownItems;
            Add(tools, "导出传感器", async (s, e) =>
                await DumpSensorsAsync(Path.Combine(Path.GetTempPath(), "statsosd-sensors.txt"), true));
            Add(tools, "重启资源管理器", async (s, e) =>
            {
                var item = (WF.ToolStripMenuItem)s;
                item.Enabled = false;
                try { await RestartExplorer(); }
                finally { if (!item.IsDisposed) item.Enabled = true; }
            });
            Add(tools, "以管理员重启", (s, e) => RestartAsAdmin());

            menu.Items.Add(new WF.ToolStripSeparator());
            Add(menu.Items, "退出 StatsOSD", (s, e) => ExitApp());

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => ToggleVisible();
            _taskbarReadings = new TaskbarReadings();
            if (_menuDumpPath != null)
            {
                bool? enabled = AutoStart.ReadState(out string error);
                SetAutoStartCheck(enabled, error);
                ApplyIconPlaceChecks(TrayPlacement.IsPromoted());
            }
            else SetAutoStartCheck(null, "等待读取系统状态");
            SyncCornerChecks();
            SyncMetricChecks();
            SyncLayoutFontChecks();

            // 菜单结构自检：--menu <文件> 导出菜单树
            if (!string.IsNullOrEmpty(_menuDumpPath))
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (WF.ToolStripItem it in menu.Items) DumpMenuItem(it, 0, sb);
                    System.IO.File.WriteAllText(_menuDumpPath, sb.ToString(), System.Text.Encoding.UTF8);
                    Log("menu dumped: " + _menuDumpPath);
                }
                catch (Exception ex) { Log("menu dump error: " + ex.Message); }
            }
        }

        private void BuildDesktopMenu(WF.ToolStripMenuItem desktop)
        {
            var items = desktop.DropDownItems;
            var metrics = Add(items, "显示指标");
            foreach (var group in Metrics.All.GroupBy(metric => metric.Group))
            {
                var submenu = Add(metrics.DropDownItems, group.Key == "Mem" ? "内存" : group.Key);
                submenu.DropDown.Closing += (s, e) =>
                {
                    if (e.CloseReason == WF.ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
                };
                foreach (MetricDef metric in group)
                {
                    var item = new WF.ToolStripMenuItem { Text = metric.Name, Tag = metric.Id, CheckOnClick = true };
                    item.Click += (s, e) => ToggleMetric((WF.ToolStripMenuItem)s);
                    submenu.DropDownItems.Add(item);
                    _miMetrics.Add(item);
                }
            }

            var layout = Add(items, "布局");
            string[] names = { "迷你", "标准", "详细" };
            for (int i = 0; i < names.Length; i++)
            {
                string id = LayoutIds[i];
                _miLayout[i] = AddChoice(layout, names[i], (s, e) => SetLayout(id));
            }

            var direction = Add(items, "排列方向");
            _miHorizontalGroups = AddChoice(direction, "横向排列", (s, e) => SetGroupDirection(true));
            _miVerticalGroups = AddChoice(direction, "竖向排列", (s, e) => SetGroupDirection(false));

            var position = Add(items, "停靠位置");
            string[] corners = { "左上角", "右上角", "左下角", "右下角" };
            for (int i = 0; i < corners.Length; i++)
            {
                int index = i;
                _miCorner[i] = AddChoice(position, corners[i], (s, e) => SetCorner(index));
            }
            _miFreePosition = AddChoice(position, "自由位置", (s, e) => StartDragMode());
            _miFreePosition.ToolTipText = "拖动桌面面板并松开以保存位置";
            _miFreePosition.Enabled = _miDrag.Enabled;
            _miPassthru = AddToggle(items, "鼠标穿透", (s, e) => TogglePassthrough(), _cfg.ClickThrough);
            _miTopmost = AddToggle(items, "强制置顶", (s, e) => ToggleForceTopmost(), _cfg.ForceTopmost);
        }

        private void BuildAppearanceMenu(WF.ToolStripMenuItem appearance)
        {
            var items = appearance.DropDownItems;
            var size = Add(items, "字体大小");
            string[] names = { "小", "中", "大", "特大" };
            for (int i = 0; i < names.Length; i++)
            {
                double scale = FontScales[i];
                _miFont[i] = AddChoice(size, names[i], (s, e) => SetFontScale(scale));
            }
            AddToggle(items, "粗体文本", (s, e) =>
            {
                _cfg.TextBold = ((WF.ToolStripMenuItem)s).Checked;
                _cfg.Save();
                ApplyTextSettings();
            }, _cfg.TextBold);
            _miOutline = AddToggle(items, "字体描边", (s, e) => ToggleOutline(), _cfg.TextOutline);
            _miOpacity = Add(items, "", (s, e) => OpenBgWindow());
            UpdateAlphaMenuText(_cfg.BackgroundAlpha);
        }

        private void UpdateAlphaMenuText(int percent)
        {
            _miOpacity.Text = $"背景不透明度（{Math.Clamp(percent, 0, 100)}%）…";
        }

        private static void DumpMenuItem(WF.ToolStripItem it, int depth, System.Text.StringBuilder sb)
        {
            var mi = it as WF.ToolStripMenuItem;
            sb.Append(new string(' ', depth * 2)).Append(it is WF.ToolStripSeparator ? "──" : it.Text);
            if (mi != null && mi.CheckOnClick) sb.Append("   [勾选]");
            if (mi?.AccessibleRole == WF.AccessibleRole.RadioButton) sb.Append("   [单选]");
            if (mi?.CheckState == WF.CheckState.Indeterminate) sb.Append("  [状态未知]");
            else if (mi?.Checked == true) sb.Append(mi.AccessibleRole == WF.AccessibleRole.RadioButton ? "  ●" : "  ✓");
            if (mi != null && !mi.Enabled) sb.Append("  [禁用]");
            sb.AppendLine();
            if (mi != null)
            {
                foreach (WF.ToolStripItem c in mi.DropDownItems) DumpMenuItem(c, depth + 1, sb);
            }
        }

        /// <summary>已启用指标（配置为空时按默认项）</summary>
        private List<string> CurrentMetrics()
        {
            return Metrics.Resolve(_cfg.Metrics).Select(metric => metric.Id).ToList();
        }

        private void SyncMetricChecks()
        {
            List<string> cur = CurrentMetrics();
            foreach (WF.ToolStripMenuItem item in _miMetrics)
            {
                string id = item.Tag as string;
                item.Checked = cur.Exists(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
                bool last = item.Checked && cur.Count == 1;
                item.Enabled = !last;
                string name = Metrics.ById(id).Name;
                item.Text = last ? name + "（至少保留一项）" : name;
                item.ToolTipText = last ? "先勾选另一项，再取消此项" : "";
            }
        }

        private void SyncLayoutFontChecks()
        {
            string preset = (_cfg.LayoutPreset ?? "standard").ToLowerInvariant();
            for (int i = 0; i < LayoutIds.Length; i++) _miLayout[i].Checked = preset == LayoutIds[i];
            _miHorizontalGroups.Checked = _cfg.ArrangeGroupsHorizontally;
            _miVerticalGroups.Checked = !_miHorizontalGroups.Checked;

            for (int i = 0; i < FontScales.Length; i++) _miFont[i].Checked = Math.Abs(_cfg.FontScale - FontScales[i]) < 0.01;
        }

        private void ToggleMetric(WF.ToolStripMenuItem item)
        {
            string id = item.Tag as string;
            if (string.IsNullOrEmpty(id)) return;

            var list = CurrentMetrics();
            if (item.Checked)
            {
                if (!list.Exists(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase))) list.Add(id);
            }
            else
            {
                list.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            }
            if (list.Count == 0) { SyncMetricChecks(); return; }

            _cfg.Metrics = list;
            _cfg.Save();
            _overlay.ApplyConfig(_cfg);
            SyncMetricChecks();
            Log("metrics -> " + string.Join(",", list));
        }

        private void SetLayout(string preset)
        {
            _cfg.LayoutPreset = preset;
            ApplyDesktopLayout();
        }

        private void SetGroupDirection(bool horizontal)
        {
            _cfg.HorizontalGroups = horizontal;
            ApplyDesktopLayout();
        }

        private void ApplyDesktopLayout()
        {
            _cfg.Save();
            _overlay.ApplyConfig(_cfg);
            SyncLayoutFontChecks();
            Log("layout -> " + _cfg.LayoutPreset + ", horizontal groups=" + _cfg.ArrangeGroupsHorizontally);
        }

        private void SetFontScale(double scale)
        {
            _cfg.FontScale = scale;
            _cfg.Save();
            ApplyTextSettings();
            SyncLayoutFontChecks();
            Log("font scale -> " + scale);
        }

        private void ToggleOutline()
        {
            _cfg.TextOutline = _miOutline.Checked;
            _cfg.Save();
            ApplyTextSettings();
            Log("text outline -> " + _cfg.TextOutline);
        }

        private void ApplyTextSettings()
        {
            _overlay.ApplyConfig(_cfg);
            _taskbarReadings.SetTypography(_overlay);
        }

        private static WF.ToolStripMenuItem Add(WF.ToolStripItemCollection items, string text, EventHandler handler = null)
        {
            var item = new WF.ToolStripMenuItem(text, null, handler);
            items.Add(item);
            return item;
        }

        private static WF.ToolStripMenuItem AddToggle(WF.ToolStripItemCollection items, string text, EventHandler handler, bool value)
        {
            var item = Add(items, text, handler);
            item.CheckOnClick = true;
            item.Checked = value;
            return item;
        }

        private static WF.ToolStripMenuItem AddChoice(WF.ToolStripMenuItem group, string text, EventHandler handler)
        {
            var item = Add(group.DropDownItems, text, handler);
            item.AccessibleRole = WF.AccessibleRole.RadioButton;
            return item;
        }

        private sealed class TrayMenuRenderer : WF.ToolStripProfessionalRenderer
        {
            protected override void OnRenderItemCheck(WF.ToolStripItemImageRenderEventArgs e)
            {
                if (e.Item.AccessibleRole != WF.AccessibleRole.RadioButton)
                {
                    base.OnRenderItemCheck(e);
                    return;
                }
                var bounds = e.ImageRectangle;
                int size = Math.Max(3, Math.Min(bounds.Width, bounds.Height) / 2);
                using var brush = new D.SolidBrush(e.Item.Enabled ? e.Item.ForeColor : D.SystemColors.GrayText);
                e.Graphics.FillEllipse(brush, bounds.X + (bounds.Width - size) / 2,
                    bounds.Y + (bounds.Height - size) / 2, size, size);
            }
        }

        private void SyncCornerChecks()
        {
            bool free = string.Equals(_cfg.PositionMode, "free", StringComparison.OrdinalIgnoreCase);
            for (int i = 0; i < 4; i++) _miCorner[i].Checked = !free && _cfg.Corner == i;
            _miFreePosition.Checked = free;
        }

        /// <summary>进入拖动模式：临时解除鼠标穿透，自由拖动面板调整位置</summary>
        private void StartDragMode()
        {
            if (_overlay?.IsVisible != true) return;
            _overlay.DragFinished = (x, y) =>
            {
                _cfg.PositionMode = "free";
                _cfg.FreeX = (int)Math.Round(x);
                _cfg.FreeY = (int)Math.Round(y);
                _cfg.Save();
                SyncCornerChecks();
                Log("position saved (free): " + _cfg.FreeX + "," + _cfg.FreeY);
            };
            _overlay.SetDragMode(true);
        }

        private void ApplyIconPlaceChecks(bool? promoted)
        {
            _miIconPlace[0].Checked = promoted == true;
            _miIconPlace[1].Checked = promoted == false;
            foreach (var item in _miIconPlace)
                item.ToolTipText = promoted.HasValue ? "" : "Windows 尚未提供确定的图标位置";
        }

        private void SetAutoStartCheck(bool? enabled, string error = null)
        {
            _miAutoStart.CheckState = !enabled.HasValue ? WF.CheckState.Indeterminate
                : enabled.Value ? WF.CheckState.Checked : WF.CheckState.Unchecked;
            _miAutoStart.Enabled = enabled.HasValue;
            _miAutoStart.ToolTipText = enabled.HasValue ? "" : error ?? "无法读取开机自启状态";
        }

        private void RefreshSystemChecks() => _ = UpdateSystemMenuAsync();

        private void SetIconPlacement(int k) => _ = UpdateSystemMenuAsync(promoted: k == 0);

        private async Task UpdateSystemMenuAsync(bool? autoStart = null, bool? promoted = null, bool syncStartup = false)
        {
            if (_systemMenuBusy || _stopping) return;
            _systemMenuBusy = true;
            _miAutoStart.Enabled = false;
            foreach (var item in _miIconPlace) item.Enabled = false;
            try
            {
                var result = await Task.Run(() =>
                {
                    string detail = "";
                    bool ok = true;
                    if (syncStartup && AutoStart.ReadState(out _) == true)
                        AutoStart.Sync();
                    if (autoStart.HasValue)
                        ok = autoStart.Value ? AutoStart.Enable(out detail) : AutoStart.Disable(out detail);
                    else if (promoted.HasValue)
                        ok = TrayPlacement.SetPromoted(promoted.Value, out detail);
                    bool? startup = AutoStart.ReadState(out string readError);
                    return (ok, detail, startup, readError, placement: TrayPlacement.IsPromoted());
                });
                if (_stopping) return;
                SetAutoStartCheck(result.startup, result.readError);
                ApplyIconPlaceChecks(result.placement);
                if (!autoStart.HasValue && !promoted.HasValue) return;

                bool matches = autoStart.HasValue ? result.startup == autoStart : result.placement == promoted;
                bool success = result.ok && matches;
                string message = result.detail;
                if (result.ok && !matches)
                    message = (autoStart.HasValue ? result.readError : null) ?? "Windows 回读的状态与请求不一致，请重新打开菜单检查。";
                if (success && promoted.HasValue) ReRegisterTrayIcon();
                string setting = autoStart.HasValue ? "开机自启" : "托盘图标位置";
                Log(setting + (success ? " OK: " : " FAIL: ") + message);
                _tray.ShowBalloonTip(4000, setting + (success ? "已更新" : "设置失败"), message,
                    success ? WF.ToolTipIcon.Info : WF.ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
                Log("system settings: " + ex.Message);
                if (_stopping) return;
                SetAutoStartCheck(null, ex.Message);
                ApplyIconPlaceChecks(null);
                if (autoStart.HasValue || promoted.HasValue)
                    _tray.ShowBalloonTip(4000, "无法完成系统设置", ex.Message, WF.ToolTipIcon.Warning);
            }
            finally
            {
                _systemMenuBusy = false;
                if (!_stopping)
                    foreach (var item in _miIconPlace) item.Enabled = true;
            }
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

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        /// <summary>只重启当前桌面的 Explorer shell。</summary>
        private async Task RestartExplorer()
        {
            try
            {
                string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                IntPtr shell = GetShellWindow();
                if (shell != IntPtr.Zero)
                {
                    if (GetWindowThreadProcessId(shell, out uint pid) == 0) return;
                    using var process = Process.GetProcessById((int)pid);
                    if (!string.Equals(process.MainModule?.FileName, explorerPath, StringComparison.OrdinalIgnoreCase)) return;
                    process.Kill();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(timeout.Token);
                    await Task.Delay(900);
                }
                if (GetShellWindow() == IntPtr.Zero)
                    Process.Start(new ProcessStartInfo(explorerPath) { UseShellExecute = true })?.Dispose();
                Log("explorer restarted (tray placement)");
            }
            catch (Exception ex) { Log("restart explorer error: " + ex.Message); }
        }

        private void ToggleVisible()
        {
            if (_overlay == null) return;
            if (_overlay.IsVisible)
            {
                _overlay.SetDragMode(false);
                _overlay.SetForceTopmost(false);
                _overlay.Hide();
            }
            else ShowDesktopOverlay();
            _cfg.ShowDesktopOverlay = _overlay.IsVisible;
            _cfg.Save();
        }

        private void ShowDesktopOverlay()
        {
            _overlay.Show();
            if (string.Equals(_cfg.PositionMode, "free", StringComparison.OrdinalIgnoreCase))
                _overlay.SetFreePosition(_cfg.FreeX, _cfg.FreeY);
            else
                _overlay.CornerIndex = _cfg.Corner;
            _overlay.SetForceTopmost(_cfg.ForceTopmost);
        }

        private void OnSettingsSaveFailed(string error)
        {
            if (_stopping || _tray?.Visible != true) return;
            try
            {
                _tray.ShowBalloonTip(4500, "设置已生效，保存失败",
                    "重启后可能无法恢复本次设置。\n" + error, WF.ToolTipIcon.Warning);
            }
            catch (Exception ex) { Log("settings notification error: " + ex.Message); }
        }

        private void SetCorner(int k)
        {
            _cfg.PositionMode = "corner";
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
            if (_overlay?.IsVisible == true) _overlay.SetForceTopmost(_cfg.ForceTopmost);
        }

        private void ToggleAutoStart() => _ = UpdateSystemMenuAsync(autoStart: _miAutoStart.Checked);

        private void SetBackgroundAlpha(int percent)
        {
            UpdateAlphaMenuText(percent);
            _overlay.SetBackgroundAlpha(percent);
            _taskbarReadings.SetBackgrounds(_overlay.GetGroupBackground);
        }

        private void OpenBgWindow()
        {
            if (_bgWin != null)
            {
                try { _bgWin.Activate(); } catch { }
                return;
            }
            _bgWin = new BgAlphaWindow(_cfg, SetBackgroundAlpha);
            try { _bgWin.Icon = LoadAppIconSource(); } catch { }
            _bgWin.Closed += (s, e) => _bgWin = null;
            _bgWin.Show();
        }

        private void RestartAsAdmin()
        {
            bool wasAdmin = IsAdmin();
            Log("restart as admin requested (currently admin=" + wasAdmin + ")");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    Arguments = "--restart",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using var child = Process.Start(psi);
                if (child == null) return;
                ExitApp();
            }
            catch (Exception ex)
            {
                Log("restart as admin failed/cancelled: " + ex.Message);
                try
                {
                    _tray.ShowBalloonTip(2500, "StatsOSD",
                        wasAdmin ? "重启未完成：" + ex.Message : "已取消（未获得管理员权限）",
                        WF.ToolTipIcon.Info);
                }
                catch { }
            }
        }

        /// <summary>演示模式下的示例型号</summary>
        private static string DemoModel(string vendor)
        {
            if (vendor == "Intel") return "Intel Core Ultra 9 275HX";
            if (vendor == "AMD") return "AMD Ryzen 9 9950X3D";
            if (vendor == "NVIDIA") return "NVIDIA GeForce RTX 5080 Laptop GPU";
            return "";
        }

        /// <summary>双击面板：打开任务管理器</summary>
        private void OpenTaskManager()
        {
            try
            {
                string path = Path.Combine(Environment.SystemDirectory, "taskmgr.exe");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
                Log("double click -> task manager");
            }
            catch (Exception ex) { Log("open task manager error: " + ex.Message); }
        }

        private async Task DumpSensorsAsync(string path, bool notify)
        {
            if (_stopping || _dumping) return;
            _dumping = true;
            try
            {
                await Task.Run(() =>
                {
                    string result;
                    lock (_sensorGate)
                    {
                        if (_stopping) return;
                        if (!_sensors.IsOpen) _sensors.Open();
                        result = _sensors.DumpAll();
                    }
                    File.WriteAllText(path, result, System.Text.Encoding.UTF8);
                });
                if (_stopping) return;
                Log("sensors dumped: " + path);
                if (notify) _tray.ShowBalloonTip(2500, "StatsOSD", "传感器清单已导出：\n" + path, WF.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log("dump error: " + ex.Message);
                if (notify && !_stopping) _tray.ShowBalloonTip(2500, "StatsOSD", "导出失败：" + ex.Message, WF.ToolTipIcon.Warning);
            }
            finally { _dumping = false; }
        }

        private void ExitApp()
        {
            if (_stopping) return;
            _stopping = true;
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _stopping = true;
            _timer?.Stop();
            _sampler?.Dispose();
            if (_sampler != null)
            {
                // A stalled hardware call must not block shutdown or race a concurrent Close().
                var close = Task.Run(() =>
                {
                    lock (_sensorGate) _sensors.Close();
                });
                if (!close.Wait(1500)) Log("sensor operation still busy during exit");
            }
            _taskbarReadings?.Dispose();
            _tray?.Dispose();
            try { SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; } catch { }
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            _singleInstance?.ReleaseMutex();
            _singleInstance?.Dispose();
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
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity)
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
