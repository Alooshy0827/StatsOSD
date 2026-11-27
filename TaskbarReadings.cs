using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace StatsOSD
{
    internal sealed class TaskbarReadings : IDisposable
    {
        private const double PanelCornerRadius = 4; // Windows 11 control radius in DIPs
        private readonly UniformGrid _grid = new UniformGrid();
        private readonly OutlinedText[] _labels = new OutlinedText[3];
        private readonly OutlinedText[] _values = new OutlinedText[3];
        private readonly MetricDef[] _metrics = { Metrics.ById("cpu.temp"), Metrics.ById("gpu.temp"), Metrics.ById("mem.percent") };
        private readonly Border _panel;
        private HwndSource _window;
        private IntPtr _host;
        private bool _enabled, _locating;
        private int _generation;
        private string _lastError;
        private double _fontScale = 1;

        public TaskbarReadings()
        {
            string[] labels = { "CPU", "GPU", "MEM" };
            Grid.SetIsSharedSizeScope(_grid, true);
            for (int i = 0; i < labels.Length; i++)
            {
                // Give each Viewbox the same content size even when one reading is wider or missing.
                var cell = new Grid();
                cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ReadingWidth" });
                cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, SharedSizeGroup = "LabelHeight" });
                cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, SharedSizeGroup = "ValueHeight" });
                _labels[i] = new OutlinedText { Text = labels[i], Alignment = TextAlignment.Center };
                _values[i] = new OutlinedText { Text = "--", Alignment = TextAlignment.Center };
                Grid.SetRow(_values[i], 1);
                cell.Children.Add(_labels[i]);
                cell.Children.Add(_values[i]);
                _grid.Children.Add(new Border
                {
                    Tag = _metrics[i].Group, Padding = new Thickness(1, 0, 1, 0),
                    Child = new Viewbox { Child = cell, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly }
                });
            }
            _panel = new Border
            {
                Background = Brushes.Transparent, Child = _grid,
                UseLayoutRounding = true, SnapsToDevicePixels = true
            };
            // Clip the whole strip so adjoining hardware groups share a straight edge.
            _panel.SizeChanged += (_, e) => _panel.Clip = new RectangleGeometry(
                new Rect(e.NewSize), PanelCornerRadius, PanelCornerRadius);
            TextOptions.SetTextFormattingMode(_panel, TextFormattingMode.Display);
        }

        public void SetTypography(OverlayWindow osd)
        {
            for (int i = 0; i < _metrics.Length; i++)
            {
                osd.StyleMetricText(_labels[i], label: true);
                osd.StyleMetricText(_values[i], label: false);
            }
            if (_fontScale == osd.Scale) return;
            _fontScale = osd.Scale;
            _generation++;
            if (_enabled && !_locating) RefreshPosition();
        }

        public void SetBackgrounds(Func<string, Brush> backgroundForGroup)
        {
            foreach (Border block in _grid.Children)
                block.Background = backgroundForGroup((string)block.Tag);
        }

        public void SetEnabled(bool enabled)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            _generation++;
            if (enabled) Update(null);
            else ClosePanel();
        }

        public void Update(Sample sample)
        {
            if (!_enabled) return;
            for (int i = 0; i < _metrics.Length; i++)
            {
                var metric = _metrics[i];
                double? value = sample == null ? null : metric.Get(sample);
                if (value.HasValue && !double.IsFinite(value.Value)) value = null;
                _values[i].Text = Metrics.Format(metric, value);
                _values[i].Suffix = value.HasValue ? metric.Suffix : "";
                _values[i].TextBrush = OverlayWindow.ColorFor(metric, value);
            }
            if (!_locating) RefreshPosition();
        }

        private async void RefreshPosition()
        {
            int generation = _generation;
            _locating = true;
            try
            {
                // UI Automation may call back into WPF. Keep the UI thread available while querying Explorer.
                double fontScale = _fontScale;
                var slot = await Task.Run(() => TaskbarLayout.Locate(fontScale));
                if (!_enabled || generation != _generation) return;
                if (slot == null || slot.Host != TaskbarLayout.CurrentTaskbar())
                {
                    ClosePanel();
                    return;
                }
                if (_window == null || _window.IsDisposed || _host != slot.Host)
                {
                    ClosePanel();
                    var parameters = new HwndSourceParameters("StatsOSD.TaskbarPanel")
                    {
                        ParentWindow = slot.Host, WindowStyle = 0x40000000, // WS_CHILD
                        ExtendedWindowStyle = 0x08000000, // WS_EX_NOACTIVATE
                        PositionX = slot.Area.X, PositionY = slot.Area.Y,
                        Width = slot.Area.Width, Height = slot.Area.Height,
                        TreatAsInputRoot = false,
                        // A layered child also paints above the Windows 11 taskbar's composition surface.
                        UsesPerPixelTransparency = true
                    };
                    _window = new HwndSource(parameters);
                    _window.CompositionTarget.BackgroundColor = Colors.Transparent;
                    _host = slot.Host;
                    _window.RootVisual = _panel;
                }
                _grid.Columns = slot.Vertical ? 1 : 3;
                _grid.Rows = slot.Vertical ? 3 : 1;
                TaskbarLayout.ShowAt(_window.Handle, slot.Area);
                _lastError = null;
            }
            catch (Exception ex)
            {
                if (generation != _generation) return;
                ClosePanel();
                if (_lastError != ex.Message) App.Log("taskbar panel: " + ex.Message);
                _lastError = ex.Message;
            }
            finally
            {
                _locating = false;
                // A re-enable or size change during the query needs a fresh slot, even without a new sample.
                if (_enabled && generation != _generation) RefreshPosition();
            }
        }

        private void ClosePanel()
        {
            if (_window != null && !_window.IsDisposed) _window.Dispose();
            _window = null;
            _host = IntPtr.Zero;
        }

        public void Dispose() => SetEnabled(false);
    }
}
