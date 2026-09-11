using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace StatsOSD
{
    public partial class OverlayWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const int HWND_TOPMOST = -1;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        private DispatcherTimer _topmostTimer;

        private bool _passthrough = true;
        private int _corner = 1;   // 0左上 1右上 2左下 3右下
        private bool _positioning;

        private static readonly SolidColorBrush BrHot = MakeBrush("#FFFF5B5B");
        private static readonly SolidColorBrush BrWarm = MakeBrush("#FFFFB04D");
        private static readonly SolidColorBrush BrCool = MakeBrush("#FFE8E8F0");
        private static readonly SolidColorBrush BrGray = MakeBrush("#FF9A9AA8");
        private static readonly SolidColorBrush BrLabel = MakeBrush("#FF8A94A6");
        private static readonly SolidColorBrush BrUnit = MakeBrush("#FF78808F");
        private static readonly SolidColorBrush BrDrag = MakeBrush("#FF4DD2FF");

        /// <summary>文字描边：零偏移 + 小半径阴影 = 一圈均匀的深色描边（背景透明时保证可读性）</summary>
        private static readonly System.Windows.Media.Effects.DropShadowEffect OutlineEffect = CreateOutlineEffect();

        private static System.Windows.Media.Effects.DropShadowEffect CreateOutlineEffect()
        {
            var eff = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 2.5,
                ShadowDepth = 0,
                Opacity = 0.95,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            };
            eff.Freeze();
            return eff;
        }

        // 自由拖动位置（纯自由拖动，无任何额外对齐行为）
        private bool _freeMode;                 // 自由位置模式（不再自动贴角）
        private bool _dragMode;                 // 拖动模式（临时解除鼠标穿透）
        private bool _dragging;
        private Point _dragCursorStart;         // 按下时的屏幕坐标（设备像素）
        private double _dpiScaleX = 1, _dpiScaleY = 1;
        private double _dragLeft, _dragTop;
        private DispatcherTimer _dragTimeout;
        private int _dragTimeoutSeconds;

        /// <summary>拖动结束回调，参数为最终位置（DIP）</summary>
        public Action<double, double> DragFinished { get; set; }

        public bool IsDragMode { get { return _dragMode; } }

        public int CornerIndex
        {
            get { return _corner; }
            set { _corner = value; _freeMode = false; ApplyCorner(); }
        }

        public void SetPassthrough(bool on)
        {
            _passthrough = on;
            ApplyStyle();
        }

        /// <summary>强制顶层：每秒重新抢占一次 Z 序，防止被其他置顶窗口（游戏启动器、录屏工具等）压住</summary>
        public void SetForceTopmost(bool on)
        {
            if (on)
            {
                if (_topmostTimer == null)
                {
                    _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _topmostTimer.Tick += (s, e) => ReassertTopmost();
                }
                Topmost = true;
                _topmostTimer.Start();
                bool ok = ReassertTopmost();
                App.Log("force topmost ON (SetWindowPos ok=" + ok + ")");
            }
            else
            {
                if (_topmostTimer != null) _topmostTimer.Stop();
                App.Log("force topmost OFF");
            }
        }

        private bool ReassertTopmost()
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return false;
            return SetWindowPos(h, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // ---------- 自由拖动位置 ----------

        /// <summary>进入/退出拖动模式（进入时临时解除鼠标穿透并高亮提示）</summary>
        public void SetDragMode(bool on)
        {
            if (on == _dragMode) return;
            _dragMode = on;

            if (on)
            {
                _passthrough = false;               // 必须能接收鼠标才能拖
                ApplyStyle();
                DragHint.Visibility = Visibility.Visible;
                Card.BorderBrush = BrDrag;
                Card.BorderThickness = new Thickness(1.4);
                Topmost = true;
                _dragTimeoutSeconds = 0;
                if (_dragTimeout == null)
                {
                    _dragTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _dragTimeout.Tick += (s, e) =>
                    {
                        if (!_dragMode) { _dragTimeout.Stop(); return; }
                        _dragTimeoutSeconds++;
                        if (_dragTimeoutSeconds >= 20)
                        {
                            App.Log("drag mode auto-off (20s timeout)");
                            SetDragMode(false);
                        }
                    };
                }
                _dragTimeout.Start();
                App.Log("drag mode ON");
            }
            else
            {
                if (_dragging) { _dragging = false; ReleaseMouseCapture(); }
                _passthrough = true;                // 恢复穿透（App 会再按配置设一次）
                ApplyStyle();
                DragHint.Visibility = Visibility.Collapsed;
                Card.BorderBrush = null;
                Card.BorderThickness = new Thickness(0);
                if (_dragTimeout != null) _dragTimeout.Stop();
                App.Log("drag mode OFF");
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (!_dragMode) return;
            _dragging = true;
            // 用屏幕绝对坐标计算位移：不依赖窗口自身位置，避免拖动时的反馈震荡
            _dragCursorStart = PointToScreen(e.GetPosition(this));
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            _dpiScaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            _dpiScaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
            _dragLeft = Left;
            _dragTop = Top;
            _dragTimeoutSeconds = 0;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;

            // 纯自由拖动：按屏幕绝对坐标位移
            Point cur = PointToScreen(e.GetPosition(this));
            Left = _dragLeft + (cur.X - _dragCursorStart.X) / _dpiScaleX;
            Top = _dragTop + (cur.Y - _dragCursorStart.Y) / _dpiScaleY;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            e.Handled = true;

            _freeMode = true;
            App.Log("drag end -> " + Left.ToString("0") + "," + Top.ToString("0"));
            DragFinished?.Invoke(Left, Top);
            SetDragMode(false);                     // 松手即退出拖动模式，恢复穿透
        }

        /// <summary>应用保存的自由位置（带有效性检查：完全在屏幕外则回退到左上角）</summary>
        public void SetFreePosition(double x, double y)
        {
            _freeMode = true;
            double w = ActualWidth > 0 ? ActualWidth : Width;
            double h = ActualHeight > 0 ? ActualHeight : 64;

            bool visible = false;
            try
            {
                foreach (WF.Screen s in WF.Screen.AllScreens)
                {
                    System.Drawing.Rectangle b = s.Bounds;
                    if (x < b.Right - 40 && x + w > b.Left + 40 && y < b.Bottom - 20 && y + h > b.Top + 20)
                    {
                        visible = true;
                        break;
                    }
                }
            }
            catch { visible = true; }

            if (!visible)
            {
                Rect wa = SystemParameters.WorkArea;
                App.Log("free position " + x.ToString("0") + "," + y.ToString("0") + " off-screen -> fallback to top-left");
                x = wa.Left + 14;
                y = wa.Top + 14;
            }

            Left = x;
            Top = y;
            App.Log("free position applied: " + x.ToString("0") + "," + y.ToString("0"));
        }

        /// <summary>设置面板背景不透明度（0-100，只影响背景层，文字保持清晰）</summary>
        public void SetBackgroundAlpha(int percent)
        {
            int p = Math.Max(0, Math.Min(100, percent));
            var brush = new SolidColorBrush(Color.FromArgb((byte)(p * 255 / 100), 0x13, 0x19, 0x1E));
            brush.Freeze();
            Card.Background = brush;
        }

        public OverlayWindow()
        {
            InitializeComponent();
            SizeChanged += (s, e) => ApplyCorner();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyStyle();
        }

        private void ApplyStyle()
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (_passthrough) ex |= WS_EX_TRANSPARENT;
            else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLong(h, GWL_EXSTYLE, ex);
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            ApplyCorner();
        }

        public void ApplyCorner()
        {
            if (_freeMode) return;          // 自由位置模式：不自动贴角
            if (_positioning) return;
            _positioning = true;
            try
            {
                double w = ActualWidth, h = ActualHeight;
                if (double.IsNaN(w) || w <= 0 || double.IsNaN(h) || h <= 0) return;
                Rect wa = SystemParameters.WorkArea;
                const double margin = 14;
                switch (_corner)
                {
                    case 0: Left = wa.Left + margin; Top = wa.Top + margin; break;
                    case 1: Left = wa.Right - w - margin; Top = wa.Top + margin; break;
                    case 2: Left = wa.Left + margin; Top = wa.Bottom - h - margin; break;
                    default: Left = wa.Right - w - margin; Top = wa.Bottom - h - margin; break;
                }
            }
            finally { _positioning = false; }
        }

        // ---------- 配置驱动的渲染（显示项 / 布局预设 / 全局字体） ----------

        private sealed class Cell
        {
            public MetricDef Def;
            public TextBlock Value;
            public bool ShowGroupLabel;      // mini 布局下把组标签画进同一格
        }

        private Settings _cfg;
        private readonly List<Cell> _cells = new List<Cell>();

        /// <summary>按配置返回描边效果（关闭时返回 null）</summary>
        private System.Windows.Media.Effects.Effect OutlineOrNull
        {
            get { return (_cfg != null && _cfg.TextOutline) ? OutlineEffect : null; }
        }

        /// <summary>应用配置并重建面板内容</summary>
        public void ApplyConfig(Settings cfg)
        {
            _cfg = cfg;
            Warn.Effect = OutlineOrNull;
            DragHint.Effect = OutlineOrNull;
            BuildContent();
        }

        private double Scale
        {
            get { return (_cfg == null || _cfg.FontScale <= 0) ? 1.0 : _cfg.FontScale; }
        }

        private string FontName
        {
            get { return (_cfg == null || string.IsNullOrWhiteSpace(_cfg.FontFamily)) ? "Consolas" : _cfg.FontFamily; }
        }

        private void BuildContent()
        {
            RowsPanel.Children.Clear();
            _cells.Clear();
            if (_cfg == null) return;

            List<MetricDef> metrics = Metrics.Resolve(_cfg.Metrics);
            string preset = (_cfg.LayoutPreset ?? "standard").ToLowerInvariant();
            double baseSize = 19 * Scale;

            if (preset == "mini") BuildMini(metrics, baseSize);
            else if (preset == "detailed") BuildDetailed(metrics, baseSize);
            else BuildStandard(metrics, baseSize);
        }

        /// <summary>按出现顺序把指标分组（同组共用一个行标签）</summary>
        private static List<List<MetricDef>> GroupByOrder(List<MetricDef> metrics)
        {
            var groups = new List<List<MetricDef>>();
            foreach (MetricDef d in metrics)
            {
                List<MetricDef> g = null;
                foreach (List<MetricDef> x in groups) { if (x[0].Group == d.Group) { g = x; break; } }
                if (g == null) { g = new List<MetricDef>(); groups.Add(g); }
                g.Add(d);
            }
            return groups;
        }

        /// <summary>标准布局：每组一行，行首是组标签，行内依次是各项数值</summary>
        private void BuildStandard(List<MetricDef> metrics, double baseSize)
        {
            int rowIndex = 0;
            foreach (List<MetricDef> group in GroupByOrder(metrics))
            {
                var grid = new Grid { Margin = new Thickness(0, rowIndex == 0 ? 0 : 2 * Scale, 0, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30 * Scale) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var label = new TextBlock
                {
                    Text = group[0].Group,
                    FontFamily = new FontFamily(FontName),
                    FontSize = 11.5 * Scale,
                    FontWeight = FontWeights.Bold,
                    Foreground = BrLabel,
                    Effect = OutlineOrNull,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(label, 0);

                var values = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(values, 1);

                bool first = true;
                foreach (MetricDef def in group)
                {
                    bool primary = def.Style == MetricStyle.Primary;
                    var tb = new TextBlock
                    {
                        FontFamily = new FontFamily(FontName),
                        FontSize = primary ? baseSize : baseSize * 0.63,
                        FontWeight = primary ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = BrCool,
                        Effect = OutlineOrNull,
                        TextAlignment = TextAlignment.Right,
                        MinWidth = (primary ? 44 : 32) * Scale,
                        Margin = new Thickness(first ? 0 : 8 * Scale, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    _cells.Add(new Cell { Def = def, Value = tb });
                    values.Children.Add(tb);
                    first = false;
                }

                grid.Children.Add(label);
                grid.Children.Add(values);
                RowsPanel.Children.Add(grid);
                rowIndex++;
            }
        }

        /// <summary>迷你布局：单行，每组只显示主指标（温度），带组标签</summary>
        private void BuildMini(List<MetricDef> metrics, double baseSize)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            bool first = true;
            foreach (List<MetricDef> group in GroupByOrder(metrics))
            {
                MetricDef def = null;
                foreach (MetricDef d in group) { if (d.Style == MetricStyle.Primary) { def = d; break; } }
                if (def == null) def = group[0];

                var tb = new TextBlock
                {
                    FontFamily = new FontFamily(FontName),
                    FontSize = baseSize * 0.95,
                    FontWeight = FontWeights.Bold,
                    Foreground = BrCool,
                    Effect = OutlineOrNull,
                    Margin = new Thickness(first ? 0 : 12 * Scale, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                _cells.Add(new Cell { Def = def, Value = tb, ShowGroupLabel = true });
                row.Children.Add(tb);
                first = false;
            }
            RowsPanel.Children.Add(row);
        }

        /// <summary>详细布局：每个指标一行，左侧完整名称、右侧数值</summary>
        private void BuildDetailed(List<MetricDef> metrics, double baseSize)
        {
            int rowIndex = 0;
            foreach (MetricDef def in metrics)
            {
                var grid = new Grid { Margin = new Thickness(0, rowIndex == 0 ? 0 : 3 * Scale, 0, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84 * Scale) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var name = new TextBlock
                {
                    Text = def.Name,
                    FontFamily = new FontFamily(FontName),
                    FontSize = 11.5 * Scale,
                    Foreground = BrLabel,
                    Effect = OutlineOrNull,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(name, 0);

                bool primary = def.Style == MetricStyle.Primary;
                var val = new TextBlock
                {
                    FontFamily = new FontFamily(FontName),
                    FontSize = primary ? baseSize : baseSize * 0.78,
                    FontWeight = primary ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = BrCool,
                    Effect = OutlineOrNull,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(val, 1);

                _cells.Add(new Cell { Def = def, Value = val });
                grid.Children.Add(name);
                grid.Children.Add(val);
                RowsPanel.Children.Add(grid);
                rowIndex++;
            }
        }

        public void Update(Sample s)
        {
            foreach (Cell c in _cells)
            {
                double? v = SafeGet(c.Def, s);
                bool primary = c.Def.Style == MetricStyle.Primary;
                double valueSize = (primary ? 19 : 19 * 0.63) * Scale;
                if (_cfg != null && (_cfg.LayoutPreset ?? "").ToLowerInvariant() == "detailed" && !primary)
                    valueSize = 19 * 0.78 * Scale;

                c.Value.Inlines.Clear();
                if (c.ShowGroupLabel)
                {
                    c.Value.Inlines.Add(new Run(c.Def.Group + " ")
                    {
                        FontSize = valueSize * 0.62,
                        Foreground = BrLabel,
                        FontWeight = FontWeights.Bold
                    });
                }
                c.Value.Inlines.Add(new Run(Metrics.Format(c.Def, v))
                {
                    FontSize = valueSize,
                    Foreground = ColorFor(c.Def, v)
                });
                if (v.HasValue && !string.IsNullOrEmpty(c.Def.Suffix))
                {
                    c.Value.Inlines.Add(new Run(c.Def.Suffix)
                    {
                        FontSize = valueSize * (primary ? 0.58 : 0.8),
                        Foreground = BrUnit
                    });
                }
            }

            // 提示
            if (!string.IsNullOrEmpty(s.Warn))
            {
                Warn.Visibility = Visibility.Visible;
                Warn.Text = s.Warn;
            }
            else
            {
                Warn.Visibility = Visibility.Collapsed;
            }
        }

        private static double? SafeGet(MetricDef def, Sample s)
        {
            try { return def.Get(s); }
            catch { return null; }
        }

        /// <summary>着色：温度按阈值分级，其余用指标自带颜色</summary>
        private static SolidColorBrush ColorFor(MetricDef def, double? v)
        {
            if (!v.HasValue) return BrGray;
            if (def.Kind == MetricKind.CpuTemp) return Severity(v, false);
            if (def.Kind == MetricKind.GpuTemp) return Severity(v, true);
            return MakeBrush(def.ColorHex);
        }

        /// <summary>把面板自身渲染成 PNG（排版自检用；叠加在中性底色上以便看清半透明效果）</summary>
        public void SaveSnapshot(string path)
        {
            double w = ActualWidth, h = ActualHeight;
            if (double.IsNaN(w) || w <= 0 || double.IsNaN(h) || h <= 0) return;

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x35, 0x38, 0x40)), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(Card), null, new Rect(0, 0, w, h));
            }

            var rtb = new RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(path))
            {
                encoder.Save(fs);
            }
        }

        /// <summary>温度着色：CPU 80-89 橙 / ≥90 红；GPU 75-82 橙 / ≥83 红</summary>
        private static SolidColorBrush Severity(double? t, bool isGpu)
        {
            if (!t.HasValue) return BrGray;
            double v = t.Value;
            if (isGpu)
            {
                if (v >= 83) return BrHot;
                if (v >= 75) return BrWarm;
            }
            else
            {
                if (v >= 90) return BrHot;
                if (v >= 80) return BrWarm;
            }
            return BrCool;
        }

        private static SolidColorBrush MakeBrush(string argb)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(argb));
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    }
}
