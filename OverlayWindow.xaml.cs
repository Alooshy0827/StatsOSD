using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

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
        private readonly List<TextBlock> _coreCells = new List<TextBlock>();

        private static readonly SolidColorBrush BrHot = MakeBrush("#FFFF5B5B");
        private static readonly SolidColorBrush BrWarm = MakeBrush("#FFFFB04D");
        private static readonly SolidColorBrush BrCool = MakeBrush("#FFE8E8F0");
        private static readonly SolidColorBrush BrGray = MakeBrush("#FF9A9AA8");

        public bool ShowCores { get; set; } = true;

        public int CornerIndex
        {
            get { return _corner; }
            set { _corner = value; ApplyCorner(); }
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

        public void Update(Sample s)
        {
            // CPU
            CpuTemp.Text = s.CpuTemp.HasValue ? s.CpuTemp.Value.ToString("0") : "--";
            CpuTemp.Foreground = Severity(s.CpuTemp, false);
            CpuLoad.Text = s.CpuLoad.HasValue ? "负载 " + s.CpuLoad.Value.ToString("0") + "%" : "负载 --";
            CpuPower.Text = s.CpuPower.HasValue ? s.CpuPower.Value.ToString("0") + "W" : "--";

            // GPU
            GpuTemp.Text = s.GpuTemp.HasValue ? s.GpuTemp.Value.ToString("0") : "--";
            GpuTemp.Foreground = Severity(s.GpuTemp, true);
            GpuLoad.Text = s.GpuLoad.HasValue ? "负载 " + s.GpuLoad.Value.ToString("0") + "%" : "负载 --";
            GpuPower.Text = s.GpuPower.HasValue ? s.GpuPower.Value.ToString("0") + "W" : "--";

            // 每核心
            UpdateCores(s);

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

        private void UpdateCores(Sample s)
        {
            if (!ShowCores || s.Cores.Count == 0)
            {
                if (_coreCells.Count > 0)
                {
                    _coreCells.Clear();
                    CoresPanel.Children.Clear();
                }
                CoresPanel.Visibility = Visibility.Collapsed;
                return;
            }

            CoresPanel.Visibility = Visibility.Visible;

            if (_coreCells.Count != s.Cores.Count)
            {
                _coreCells.Clear();
                CoresPanel.Children.Clear();
                for (int i = 0; i < s.Cores.Count; i++)
                {
                    var cell = new TextBlock();
                    _coreCells.Add(cell);
                    CoresPanel.Children.Add(cell);
                }
            }

            for (int i = 0; i < s.Cores.Count; i++)
            {
                CoreTemp c = s.Cores[i];
                TextBlock cell = _coreCells[i];
                cell.Text = c.Name + " " + c.Temp.ToString("0") + "\u00B0";
                cell.Foreground = Severity(c.Temp, false);
            }
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
