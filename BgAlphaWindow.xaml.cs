using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace CpuHud
{
    public partial class BgAlphaWindow : Window
    {
        private readonly Settings _cfg;
        private readonly Action<int> _liveApply;
        private readonly DispatcherTimer _saveTimer;
        private bool _internal;

        public BgAlphaWindow(Settings cfg, Action<int> liveApply)
        {
            InitializeComponent();
            _cfg = cfg;
            _liveApply = liveApply;

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (s, e) =>
            {
                _saveTimer.Stop();
                _cfg.BackgroundAlpha = (int)Math.Round(Slider.Value);
                _cfg.Save();
            };

            _internal = true;
            Slider.Value = _cfg.BackgroundAlpha;
            _internal = false;
            UpdateText();

            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Closed += (s, e) =>
            {
                _saveTimer.Stop();
                _cfg.BackgroundAlpha = (int)Math.Round(Slider.Value);
                _cfg.Save();
            };
        }

        private void OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_internal) return;
            UpdateText();
            _liveApply?.Invoke((int)Math.Round(Slider.Value));
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void UpdateText()
        {
            ValueText.Text = (int)Math.Round(Slider.Value) + "%";
        }
    }
}
