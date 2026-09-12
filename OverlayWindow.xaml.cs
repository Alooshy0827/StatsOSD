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
        private static readonly SolidColorBrush BrLabel = MakeBrush("#FFA3ADBE");
        private static readonly SolidColorBrush BrUnit = MakeBrush("#FF78808F");
        private static readonly SolidColorBrush BrDrag = MakeBrush("#FF4DD2FF");

        // 厂商分组配色：Intel 蓝 / AMD 橙红 / NVIDIA 绿
        private static readonly SolidColorBrush BrIntel = MakeBrush("#FF0071C5");
        private static readonly SolidColorBrush BrAmd = MakeBrush("#FFE8452C");
        private static readonly SolidColorBrush BrNvidia = MakeBrush("#FF76B900");
        private static readonly SolidColorBrush BrGroupNeutral = MakeBrush("#FF3C4657");
        private static readonly SolidColorBrush BrModel = MakeBrush("#FFFFFFFF");   // 型号：白
        private static readonly SolidColorBrush BrGroupName = MakeBrush("#D9FFFFFF"); // 分组名（CPU/GPU/Mem）：浅灰

        /// <summary>文字描边：零偏移 + 小半径阴影 = 一圈均匀的深色描边（背景透明时保证可读性）</summary>
        private static readonly System.Windows.Media.Effects.DropShadowEffect OutlineEffect = CreateOutlineEffect();

        private static System.Windows.Media.Effects.DropShadowEffect CreateOutlineEffect()
        {
            var eff = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 1.5,      // 收紧：更硬的描边，而不是摊薄的柔光
                ShadowDepth = 0,
                Opacity = 1.0,         // 全不透明
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

        /// <summary>双击面板时的动作（由 App 注入）</summary>
        public Action DoubleClicked { get; set; }

        private string _cpuVendor = "";
        private string _gpuVendor = "";
        private string _cpuModel = "";
        private string _gpuModel = "";
        private string _memModel = "";
        private int _blockAlpha = 50;

        /// <summary>硬件型号（显示在色块内）</summary>
        public void SetModels(string cpu, string gpu, string mem = null)
        {
            if (_cpuModel == cpu && _gpuModel == gpu && _memModel == mem) return;
            _cpuModel = cpu ?? "";
            _gpuModel = gpu ?? "";
            _memModel = mem ?? "";
            BuildContent();
        }

        private string ModelFor(string group)
        {
            if (group == "CPU") return _cpuModel;
            if (group == "GPU") return _gpuModel;
            if (group == "Mem") return _memModel;
            return "";
        }

        private OutlinedText MakeModelText(string model, double baseSize)
        {
            return new OutlinedText
            {
                Text = model,
                FontName = FontName,
                FontSize = baseSize * 0.70,     // 型号是这张卡的标题，略大于数值（0.68）
                TextBrush = BrModel,
                StrokeBrush = OutlineBrush,
                StrokeThickness = 1.6 * Scale,
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }

        /// <summary>详细布局显示的指标名：去掉分组前缀（分组已由色块表达）</summary>
        private static string DisplayName(MetricDef def)
        {
            string n = def.Name ?? "";
            string g = def.Group ?? "";
            if (g.Length > 0 && n.StartsWith(g + " ", StringComparison.Ordinal)) return n.Substring(g.Length + 1);
            if (n.StartsWith("内存 ", StringComparison.Ordinal)) return n.Substring(3);
            return n;
        }

        /// <summary>硬件厂商（用于分组配色）</summary>
        public void SetVendors(string cpu, string gpu)
        {
            if (_cpuVendor == cpu && _gpuVendor == gpu) return;
            _cpuVendor = cpu ?? "";
            _gpuVendor = gpu ?? "";
            BuildContent();
        }

        private Brush GroupColor(string group)
        {
            if (group == "CPU") return VendorBrush(_cpuVendor);
            if (group == "GPU") return VendorBrush(_gpuVendor);
            return BrGroupNeutral;
        }

        private static Brush VendorBrush(string vendor)
        {
            if (vendor == "Intel") return BrIntel;
            if (vendor == "AMD") return BrAmd;
            if (vendor == "NVIDIA") return BrNvidia;
            return BrGroupNeutral;
        }

        /// <summary>分组标签色块（半透明底色，包住该硬件的全部信息）</summary>
        private Border MakeGroupBlock(string group, double fontSize)
        {
            return new Border
            {
                Background = Tint(GroupColor(group), 0x66),
                CornerRadius = new CornerRadius(4 * Scale),
                Padding = new Thickness(6 * Scale, 2 * Scale, 6 * Scale, 2 * Scale),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = new OutlinedText
                {
                    Text = group,
                    FontName = FontName,
                    FontSize = fontSize,
                    FontWeight = FontWeights.Bold,
                    TextBrush = Brushes.White
                }
            };
        }

        private static readonly Dictionary<uint, SolidColorBrush> _tintCache = new Dictionary<uint, SolidColorBrush>();

        /// <summary>把颜色按指定 alpha 变淡（用于行底色）</summary>
        private static SolidColorBrush Tint(Brush baseBrush, byte alpha)
        {
            var scb = baseBrush as SolidColorBrush;
            Color c = scb != null ? scb.Color : Color.FromRgb(0x3C, 0x46, 0x57);
            uint key = ((uint)c.R << 24) | ((uint)c.G << 16) | ((uint)c.B << 8) | alpha;
            SolidColorBrush cached;
            if (_tintCache.TryGetValue(key, out cached)) return cached;
            var nb = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            nb.Freeze();
            _tintCache[key] = nb;
            return nb;
        }

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

            // 双击动作（鼠标穿透关闭时可用）
            if (e.ClickCount == 2 && DoubleClicked != null)
            {
                DoubleClicked();
                e.Handled = true;
                return;
            }

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

        /// <summary>色块不透明度（0-100，由设置里的"透明度"控制；面板本身无底色）</summary>
        public void SetBackgroundAlpha(int percent)
        {
            int p = Math.Max(0, Math.Min(100, percent));
            if (p == _blockAlpha) return;
            _blockAlpha = p;
            Card.Background = Brushes.Transparent;
            BuildContent();
        }

        private byte BlockAlpha
        {
            get { return (byte)(Math.Max(0, Math.Min(100, _blockAlpha)) * 255 / 100); }
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
            public OutlinedText Value;
            public bool ShowGroupLabel;      // mini 布局下把组标签画进同一格
        }

        private Settings _cfg;
        private readonly List<Cell> _cells = new List<Cell>();

        // 动态列宽（按当前实际数值宽度实时计算）
        private readonly List<List<OutlinedText>> _stdCards = new List<List<OutlinedText>>();
        private readonly List<ColumnDefinition> _detailCols = new List<ColumnDefinition>();
        private readonly List<OutlinedText> _detailVals = new List<OutlinedText>();
        private readonly Dictionary<OutlinedText, double> _appliedWidth = new Dictionary<OutlinedText, double>();

        /// <summary>文字描边画笔（关闭时返回 null）——由 OutlinedText 用几何轮廓描线实现</summary>
        private Brush OutlineBrush
        {
            get { return (_cfg != null && _cfg.TextOutline) ? Brushes.Black : null; }
        }

        /// <summary>提示类文字（警告/拖动提示）仍用柔和阴影，避免与主数据抢眼</summary>
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
            _stdCards.Clear();
            _detailCols.Clear();
            _detailVals.Clear();
            _appliedWidth.Clear();
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

        /// <summary>文本宽度实测（列宽估算用）</summary>
        private double MeasureText(string text, double fontSize, bool bold)
        {
            try
            {
                var tf = new Typeface(new FontFamily(FontName), FontStyles.Normal,
                    bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                var ft = new FormattedText(text ?? "", System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, tf, fontSize, Brushes.White, 1.0);
                return ft.Width;
            }
            catch { return fontSize * 3; }
        }

        /// <summary>最宽数值文本（含单位）的预估宽度</summary>
        private double ValueWidth(MetricDef def, double valueSize, double smallScale)
        {
            string sample = string.IsNullOrEmpty(def.WidthSample) ? "888" : def.WidthSample;
            double w = MeasureText(sample, valueSize, false);
            if (!string.IsNullOrEmpty(def.Suffix))
            {
                string sfx = def.Suffix.Length >= 2 && char.IsLetter(def.Suffix[0]) ? " " + def.Suffix : def.Suffix;
                w += MeasureText(sfx, valueSize * smallScale, false);
            }
            return w;
        }

        /// <summary>标准布局：每组一块（卡片），块内是型号 + 各项数值；列宽按本卡最坏情况计算</summary>
        private void BuildStandard(List<MetricDef> metrics, double baseSize)
        {
            List<List<MetricDef>> groups = GroupByOrder(metrics);
            double valueSize = baseSize * 0.68;

            int rowIndex = 0;
            foreach (List<MetricDef> group in groups)
            {
                // 本卡的列宽（不跨卡共享，避免被别行的长值撑出空隙）
                var colWidth = new double[group.Count];
                for (int i = 0; i < group.Count; i++)
                    colWidth[i] = Math.Max(ValueWidth(group[i], valueSize, 0.78) + 3 * Scale, 22 * Scale);
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal
                };
                // 色块内容 = 第一行（硬件型号）+ 第二行（标签与数值）
                var content = new StackPanel();
                string model = ModelFor(group[0].Group);
                if (!string.IsNullOrEmpty(model))
                {
                    OutlinedText mt = MakeModelText(model, baseSize);
                    mt.Margin = new Thickness(0, 0, 0, 2 * Scale);
                    content.Children.Add(mt);
                }
                content.Children.Add(row);

                var block = new Border
                {
                    Background = Tint(GroupColor(group[0].Group), BlockAlpha),
                    Padding = new Thickness(7 * Scale, 4 * Scale, 7 * Scale, 4 * Scale),
                    CornerRadius = new CornerRadius(0),
                    Child = content
                };

                bool first = true;
                var cardCells = new List<OutlinedText>();
                for (int i = 0; i < group.Count; i++)
                {
                    MetricDef def = group[i];
                    var tb = new OutlinedText
                    {
                        FontName = FontName,
                        FontSize = valueSize,
                        FontWeight = FontWeights.Normal,
                        TextBrush = BrCool,
                        SuffixBrush = BrUnit,
                        StrokeBrush = OutlineBrush,
                        StrokeThickness = 2.0 * Scale,
                        SmallScale = 0.78,
                        Alignment = TextAlignment.Left,
                        Width = colWidth[i],
                        Margin = new Thickness(first ? 0 : 6 * Scale, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    _appliedWidth[tb] = colWidth[i];
                    cardCells.Add(tb);
                    _cells.Add(new Cell { Def = def, Value = tb });
                    row.Children.Add(tb);
                    first = false;
                }
                _stdCards.Add(cardCells);

                RowsPanel.Children.Add(block);
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

                var cell = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                // 迷你布局不再显示分组名（色块本身即分组标识）
                var tb = new OutlinedText
                {
                    FontName = FontName,
                    FontSize = baseSize * 0.68,
                    FontWeight = FontWeights.Normal,
                    TextBrush = BrCool,
                    SuffixBrush = BrUnit,
                    StrokeBrush = OutlineBrush,
                    StrokeThickness = 2.0 * Scale,
                    SmallScale = 0.78,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _cells.Add(new Cell { Def = def, Value = tb });
                cell.Children.Add(tb);
                row.Children.Add(new Border
                {
                    Background = Tint(GroupColor(def.Group), BlockAlpha),
                    CornerRadius = new CornerRadius(0),
                    Padding = new Thickness(6 * Scale, 3 * Scale, 6 * Scale, 3 * Scale),
                    Child = cell
                });
                first = false;
            }
            RowsPanel.Children.Add(row);
        }

        /// <summary>详细布局名称列宽：按最长名称实测（上限 200*缩放）</summary>
        private double MeasureNameWidth(List<MetricDef> metrics, double baseSize)
        {
            double max = 60 * Scale;
            var typeface = new Typeface(new FontFamily(FontName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            foreach (MetricDef def in metrics)
            {
                try
                {
                    var ft = new FormattedText(DisplayName(def), System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, typeface, 11.5 * Scale, Brushes.White, 1.0);
                    if (ft.Width > max) max = ft.Width;
                }
                catch { }
            }
            return Math.Min(max + 12 * Scale, 200 * Scale);
        }

        /// <summary>详细布局：每个指标一行，左侧完整名称、右侧数值（名称列宽按最长名称自动计算）</summary>
        private void BuildDetailed(List<MetricDef> metrics, double baseSize)
        {
            double nameWidth = MeasureNameWidth(metrics, baseSize);
            // 数值列用实测最坏宽度（星号列在 SizeToContent 下会塌成 0 宽，导致数值压到名称上）
            double detailedValueSize = baseSize * 0.68;
            double valWidth = 0;
            foreach (MetricDef d in metrics) valWidth = Math.Max(valWidth, ValueWidth(d, detailedValueSize, 0.78));
            valWidth = Math.Max(valWidth + 3 * Scale, 40 * Scale);
            int rowIndex = 0;
            string lastGroup = null;
            foreach (MetricDef def in metrics)
            {
                // 换组时先插入一条"硬件型号"行（归属于该组的色块）
                if (def.Group != lastGroup)
                {
                    lastGroup = def.Group;
                    string model = ModelFor(def.Group);
                    if (!string.IsNullOrEmpty(model))
                    {
                        OutlinedText mt = MakeModelText(model, baseSize);
                        mt.Margin = new Thickness(6 * Scale, 2 * Scale, 6 * Scale, 2 * Scale);
                        RowsPanel.Children.Add(new Border
                        {
                            Background = Tint(GroupColor(def.Group), BlockAlpha),
                            CornerRadius = new CornerRadius(0),
                            Child = mt
                        });
                    }
                }

                var grid = new Grid
                {
                    Background = Tint(GroupColor(def.Group), BlockAlpha)
                };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(nameWidth) });
                // 数值列宽 = 最坏数值宽度 + 右侧留白（留白必须计入列宽，否则内容会溢出被裁）
                var valCol = new ColumnDefinition { Width = new GridLength(valWidth + 14 * Scale) };
                grid.ColumnDefinitions.Add(valCol);
                _detailCols.Add(valCol);

                var nameRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6 * Scale, 2 * Scale, 0, 2 * Scale) };
                nameRow.Children.Add(new OutlinedText
                {
                    Text = DisplayName(def),
                    FontName = FontName,
                    FontSize = 11.5 * Scale,
                    TextBrush = BrGroupName,
                    StrokeBrush = OutlineBrush,
                    StrokeThickness = 2.0 * Scale,
                    VerticalAlignment = VerticalAlignment.Center
                });
                Grid.SetColumn(nameRow, 0);

                var val = new OutlinedText
                {
                    FontName = FontName,
                    FontSize = detailedValueSize,
                    FontWeight = FontWeights.Normal,
                    TextBrush = BrCool,
                    SuffixBrush = BrUnit,
                    StrokeBrush = OutlineBrush,
                    StrokeThickness = 2.0 * Scale,
                    SmallScale = 0.78,
                    Alignment = TextAlignment.Left,
                    Width = valWidth,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 2 * Scale, 14 * Scale, 2 * Scale),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(val, 1);
                _detailVals.Add(val);
                _appliedWidth[val] = valWidth;

                _cells.Add(new Cell { Def = def, Value = val });
                grid.Children.Add(nameRow);
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
                OutlinedText ot = c.Value;
                ot.Prefix = c.ShowGroupLabel ? c.Def.Group + " " : "";
                ot.Text = c.Def.Text != null ? c.Def.Text(s) : Metrics.Format(c.Def, v);
                // 多字母单位（GB/MB/MHz/RPM）前留一个空格，符号单位（°C %）保持紧凑
                string sfx = c.Def.Suffix ?? "";
                if (sfx.Length >= 2 && char.IsLetter(sfx[0])) sfx = " " + sfx;
                ot.Suffix = v.HasValue ? sfx : "";
                ot.TextBrush = ColorFor(c.Def, v);
                ot.SuffixBrush = BrUnit;
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

            ApplyDynamicWidths();
        }

        /// <summary>按当前实际数值宽度重算列宽：加宽立即生效，明显变窄才收缩（避免位数变化时来回跳）</summary>
        private void ApplyDynamicWidths()
        {
            foreach (List<OutlinedText> card in _stdCards)
            {
                // 每个数值各按自身实际宽度（不取卡内最大值，否则短值会被撑出空隙）
                foreach (OutlinedText t in card) SetCellWidth(t, TextWidthOf(t) + 3 * Scale);
            }

            if (_detailVals.Count > 0)
            {
                double max = 0;
                foreach (OutlinedText t in _detailVals) max = Math.Max(max, TextWidthOf(t));
                double want = max + 3 * Scale;
                foreach (OutlinedText t in _detailVals) SetCellWidth(t, want);
                foreach (ColumnDefinition cd in _detailCols)
                {
                    double colWant = want + 14 * Scale;
                    if (Math.Abs(cd.Width.Value - colWant) > 1) cd.Width = new GridLength(colWant);
                }
            }
        }

        private double TextWidthOf(OutlinedText t)
        {
            double w = MeasureText(t.Text, t.FontSize, t.FontWeight == FontWeights.Bold);
            if (!string.IsNullOrEmpty(t.Suffix)) w += MeasureText(t.Suffix, t.FontSize * t.SmallScale, false);
            return w;
        }

        private void SetCellWidth(OutlinedText t, double want)
        {
            double cur;
            if (_appliedWidth.TryGetValue(t, out cur))
            {
                bool grow = want > cur + 0.5;
                bool shrink = want < cur - 2 * Scale;   // 略微变窄就收紧，避免出现明显空隙
                if (!grow && !shrink) return;
            }
            t.Width = want;
            _appliedWidth[t] = want;
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
