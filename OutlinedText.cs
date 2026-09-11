using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace StatsOSD
{
    /// <summary>
    /// 带真实描边的文字元素：用字体几何轮廓 + 画笔描线实现（不是模糊阴影），
    /// 因此描边是**实心、粗细可控**的，背景全透明时也能保持可读性。
    ///
    /// 支持 前缀（小字标签）+ 数值 + 后缀（小字单位）三段混排。
    /// </summary>
    public sealed class OutlinedText : FrameworkElement
    {
        private string _text = "";
        private string _suffix = "";
        private string _prefix = "";
        private Brush _textBrush = Brushes.White;
        private Brush _suffixBrush = Brushes.Gray;
        private TextAlignment _alignment = TextAlignment.Left;

        public string Text
        {
            get { return _text; }
            set { if (_text != value) { _text = value; InvalidateVisual(); } }
        }

        public string Suffix
        {
            get { return _suffix; }
            set { if (_suffix != value) { _suffix = value; InvalidateVisual(); } }
        }

        public string Prefix
        {
            get { return _prefix; }
            set { if (_prefix != value) { _prefix = value; InvalidateVisual(); } }
        }

        public Brush TextBrush
        {
            get { return _textBrush; }
            set { _textBrush = value ?? Brushes.White; InvalidateVisual(); }
        }

        public Brush SuffixBrush
        {
            get { return _suffixBrush; }
            set { _suffixBrush = value ?? Brushes.Gray; InvalidateVisual(); }
        }

        public TextAlignment Alignment
        {
            get { return _alignment; }
            set { if (_alignment != value) { _alignment = value; InvalidateVisual(); } }
        }

        public string FontName { get; set; } = "Consolas";
        public double FontSize { get; set; } = 14;
        public FontWeight FontWeight { get; set; } = FontWeights.Normal;

        /// <summary>前缀/后缀相对主字号的缩放</summary>
        public double SmallScale { get; set; } = 0.6;

        /// <summary>描边颜色；null 或粗细 ≤0 时不描边</summary>
        public Brush StrokeBrush { get; set; }

        public double StrokeThickness { get; set; } = 2.2;

        private double PixelsPerDip
        {
            get
            {
                try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; }
                catch { return 1.0; }
            }
        }

        private Typeface Face
        {
            get { return new Typeface(new FontFamily(FontName), FontStyles.Normal, FontWeight, FontStretches.Normal); }
        }

        private FormattedText Ft(string s, double size, Brush brush)
        {
            return new FormattedText(
                s ?? "",
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                Face,
                size,
                brush ?? Brushes.White,
                PixelsPerDip);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double small = FontSize * SmallScale;
            FormattedText main = Ft(string.IsNullOrEmpty(_text) ? "0" : _text, FontSize, Brushes.White);
            double w = main.Width;
            double h = main.Height;
            if (!string.IsNullOrEmpty(_prefix))
            {
                FormattedText ft = Ft(_prefix, small, Brushes.White);
                w += ft.Width;
                h = Math.Max(h, ft.Height);
            }
            if (!string.IsNullOrEmpty(_suffix))
            {
                FormattedText ft = Ft(_suffix, small, Brushes.White);
                w += ft.Width;
                h = Math.Max(h, ft.Height + FontSize * 0.22);   // 后缀贴基线
            }
            double stroke = (StrokeBrush != null && StrokeThickness > 0) ? StrokeThickness : 0;
            return new Size(w + stroke, h + stroke);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double small = FontSize * SmallScale;
            double sw = (StrokeBrush != null && StrokeThickness > 0) ? StrokeThickness : 0;
            Pen pen = null;
            if (sw > 0)
            {
                pen = new Pen(StrokeBrush, sw) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();
            }

            double total = 0;
            FormattedText prefixFt = null, mainFt = null, suffixFt = null;
            if (!string.IsNullOrEmpty(_prefix)) { prefixFt = Ft(_prefix, small, Brushes.White); total += prefixFt.Width; }
            mainFt = Ft(string.IsNullOrEmpty(_text) ? "--" : _text, FontSize, Brushes.White);
            total += mainFt.Width;
            if (!string.IsNullOrEmpty(_suffix)) { suffixFt = Ft(_suffix, small, Brushes.White); total += suffixFt.Width; }

            double x0;
            switch (_alignment)
            {
                case TextAlignment.Right: x0 = ActualWidth - total - sw / 2; break;
                case TextAlignment.Center: x0 = (ActualWidth - total) / 2; break;
                default: x0 = sw / 2; break;
            }
            double y0 = sw / 2;

            double x = x0;
            if (prefixFt != null)
            {
                Draw(dc, prefixFt, x, y0 + mainFt.Baseline - prefixFt.Baseline, SuffixBrush, pen);
                x += prefixFt.Width;
            }
            Draw(dc, mainFt, x, y0, TextBrush, pen);
            x += mainFt.Width;
            if (suffixFt != null)
            {
                Draw(dc, suffixFt, x, y0 + mainFt.Baseline - suffixFt.Baseline, SuffixBrush, pen);
            }
        }

        private static void Draw(DrawingContext dc, FormattedText ft, double x, double y, Brush fill, Pen pen)
        {
            Geometry geo = null;
            if (pen != null)
            {
                try { geo = ft.BuildGeometry(new Point(x, y)); } catch { geo = null; }
            }

            if (geo == null)
            {
                dc.DrawText(ft, new Point(x, y));
                return;
            }

            // 先描边（画笔居中，外扩一半形成光晕），再填充字形 —— 这样字形不会被描边吃掉
            if (pen != null) dc.DrawGeometry(null, pen, geo);
            dc.DrawGeometry(fill, null, geo);
        }
    }
}
