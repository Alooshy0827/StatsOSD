using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CpuHud
{
    public partial class OverlayWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private bool _passthrough = true;
        private int _corner = 1;   // 0左上 1右上 2左下 3右下
        private bool _positioning;

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
            CpuTemp.Text = s.CpuTemp.HasValue ? s.CpuTemp.Value.ToString("0") + "\u00B0C" : "--";
            CpuTemp.Foreground = Severity(s.CpuTemp);
            CpuLoad.Text = s.CpuLoad.HasValue ? "负载 " + s.CpuLoad.Value.ToString("0") + "%" : "负载 --";

            // GPU
            GpuTemp.Text = s.GpuTemp.HasValue ? s.GpuTemp.Value.ToString("0") + "\u00B0C" : "--";
            GpuTemp.Foreground = Severity(s.GpuTemp);
            GpuLoad.Text = s.GpuLoad.HasValue ? "负载 " + s.GpuLoad.Value.ToString("0") + "%" : "负载 --";

            // 每核心
            if (ShowCores && s.Cores.Count > 0)
            {
                Cores.Visibility = Visibility.Visible;
                Cores.Text = string.Join("    ", s.Cores.Select(c => c.Name + " " + c.Temp.ToString("0") + "\u00B0"));
                double max = s.Cores.Max(c => c.Temp);
                Cores.Foreground = Severity(max);
            }
            else
            {
                Cores.Visibility = Visibility.Collapsed;
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

        private static SolidColorBrush Severity(double? t)
        {
            if (!t.HasValue) return BrGray;
            double v = t.Value;
            if (v >= 90) return BrHot;
            if (v >= 75) return BrWarm;
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
    }
}
