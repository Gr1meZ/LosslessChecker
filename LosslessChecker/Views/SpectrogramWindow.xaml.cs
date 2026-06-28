using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LosslessChecker.Views;

public partial class SpectrogramWindow : Window
{
    private readonly float[]? _rawData;
    private readonly int _dataWidth, _dataHeight;
    private readonly double _durationSec, _sampleRate, _cutoffHz;
    private System.Windows.Point _lastMousePos;
    private double _panX, _panY;
    private double _scaleX = 1, _scaleY = 1;
    private bool _isPanning;
    private readonly DispatcherTimer _resizeTimer;

    private static readonly SolidColorBrush AxisBrush =
        new(System.Windows.Media.Color.FromRgb(0x9a, 0xa0, 0xb0));
    private static readonly SolidColorBrush GridBrush =
        new(System.Windows.Media.Color.FromRgb(0x2a, 0x2e, 0x3f));
    private static readonly SolidColorBrush CutoffBrush =
        new(System.Windows.Media.Color.FromRgb(0xf8, 0x71, 0x71));

    public SpectrogramWindow(BitmapSource? bmp, string title = "Spectrogram")
    {
        InitializeComponent();
        Title = $"Спектрограмма — {title}";
        SpectrogramImage.Source = bmp;
        _resizeTimer = CreateResizeTimer();
    }

    public SpectrogramWindow(float[] rawData, int dataWidth, int dataHeight,
        double durationSec, double sampleRate, double cutoffHz, string fileName)
    {
        InitializeComponent();
        _rawData = rawData;
        _dataWidth = dataWidth;
        _dataHeight = dataHeight;
        _durationSec = durationSec;
        _sampleRate = sampleRate;
        _cutoffHz = cutoffHz;
        Title = $"Спектрограмма — {fileName}";
        DataContext = new { Title = fileName };
        _resizeTimer = CreateResizeTimer();
        RenderFull();
    }

    private DispatcherTimer CreateResizeTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            if (_rawData != null) RenderFull();
        };
        return timer;
    }

    private void RenderFull()
    {
        if (_rawData == null) return;
        var renderer = new LosslessChecker.Services.SpectrogramRenderer();
        var bmp = renderer.Render(_rawData, _dataWidth, _dataHeight);
        SpectrogramImage.Source = bmp;
        DrawAxes();
        DrawLegend();
    }

    private void DrawAxes()
    {
        OverlayCanvas.Children.Clear();
        double canvasW = OverlayCanvas.ActualWidth;
        double canvasH = OverlayCanvas.ActualHeight;
        if (canvasW < 10 || canvasH < 10) return;

        double nyquist = _sampleRate / 2.0;
        double logMin = Math.Log10(20.0);
        double logMax = Math.Log10(nyquist);
        double logRange = logMax - logMin;

        var freqLabelList = new List<double>();
        {
            double v = 20;
            int step = 0;
            while (v <= nyquist + 0.5)
            {
                freqLabelList.Add(v);
                v *= step == 0 ? 2.5 : 2.0;
                step = (step + 1) % 3;
            }
            if (freqLabelList.Count > 0 && Math.Abs(freqLabelList[^1] - nyquist) > 1)
                freqLabelList.Add(Math.Round(nyquist));
        }
        double[] freqLabels = freqLabelList.ToArray();

        foreach (var freq in freqLabels)
        {
            double ratio = (Math.Log10(freq) - logMin) / logRange;
            double y = canvasH - ratio * canvasH;
            var tb = new TextBlock
            {
                Text = freq >= 1000 ? $"{freq / 1000:0.##}k" : $"{freq:F0}",
                Foreground = AxisBrush,
                FontSize = 9,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
            };
            System.Windows.Controls.Canvas.SetLeft(tb, -42);
            System.Windows.Controls.Canvas.SetTop(tb, y - 7);
            OverlayCanvas.Children.Add(tb);

            var line = new Line
            {
                X1 = 0, Y1 = y, X2 = canvasW, Y2 = y,
                Stroke = GridBrush,
                StrokeThickness = 0.5,
                Opacity = 0.3
            };
            OverlayCanvas.Children.Add(line);
        }

        var markerList = new List<double>();
        {
            double v = 500;
            int step = 0;
            while (v <= nyquist + 0.5)
            {
                markerList.Add(v);
                v *= step == 0 ? 2.5 : 2.0;
                step = (step + 1) % 3;
            }
            if (markerList.Count > 0 && Math.Abs(markerList[^1] - nyquist) > 1)
                markerList.Add(Math.Round(nyquist));
        }
        double[] standardMarkers = markerList.ToArray();

        foreach (var freq in standardMarkers)
        {
            double ratio2 = (Math.Log10(freq) - logMin) / logRange;
            double y2 = canvasH - ratio2 * canvasH;

            var dashLine = new Line
            {
                X1 = 0, Y1 = y2, X2 = canvasW, Y2 = y2,
                Stroke = GridBrush,
                StrokeThickness = 0.8,
                Opacity = 0.5,
                StrokeDashArray = new DoubleCollection { 4, 2 }
            };
            OverlayCanvas.Children.Add(dashLine);
        }

        if (_durationSec > 0)
        {
            double interval = _durationSec <= 300 ? 30 : 60;
            for (double t = 0; t <= _durationSec; t += interval)
            {
                double x = t / _durationSec * canvasW;
                var ts = System.TimeSpan.FromSeconds(t);
                string label = ts.TotalHours >= 1
                    ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}"
                    : $"{ts.Minutes}:{ts.Seconds:D2}";
                var tb = new TextBlock
                {
                    Text = label,
                    Foreground = AxisBrush,
                    FontSize = 9,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
                };
                System.Windows.Controls.Canvas.SetLeft(tb, x - 12);
                System.Windows.Controls.Canvas.SetTop(tb, canvasH + 4);
                OverlayCanvas.Children.Add(tb);
            }
        }

        if (_cutoffHz > 0)
        {
            double cutRatio = (Math.Log10(Math.Max(_cutoffHz, 20.0)) - logMin) / logRange;
            double cutY = canvasH - cutRatio * canvasH;
            var cutLine = new Line
            {
                X1 = 0, Y1 = cutY, X2 = canvasW, Y2 = cutY,
                Stroke = CutoffBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 6, 3 }
            };
            OverlayCanvas.Children.Add(cutLine);

            var cutLabel = new TextBlock
            {
                Text = $"Срез: {_cutoffHz:F0} Гц",
                Foreground = CutoffBrush,
                FontSize = 9,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
            };
            System.Windows.Controls.Canvas.SetLeft(cutLabel, canvasW - 100);
            System.Windows.Controls.Canvas.SetTop(cutLabel, cutY - 12);
            OverlayCanvas.Children.Add(cutLabel);
        }
    }

    private void DrawLegend()
    {
        LegendCanvas.Children.Clear();
        double canvasW = LegendCanvas.ActualWidth;
        double canvasH = OverlayCanvas.ActualHeight;
        if (canvasH < 10 || canvasW < 10) return;

        int barWidth = 12;
        double barLeft = (canvasW - barWidth) / 2;

        int steps = Math.Min(256, (int)canvasH);
        var pixels = new byte[steps * barWidth * 4];
        for (int row = 0; row < steps; row++)
        {
            double t = 1.0 - (double)row / (steps - 1);
            var (r, g, b) = SpectrogramColormap(t);
            for (int col = 0; col < barWidth; col++)
            {
                int idx = (row * barWidth + col) * 4;
                pixels[idx] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = 0xFF;
            }
        }

        var bmp = new WriteableBitmap(barWidth, steps, 96, 96, PixelFormats.Bgra32, null);
        bmp.Lock();
        Marshal.Copy(pixels, 0, bmp.BackBuffer, pixels.Length);
        bmp.AddDirtyRect(new Int32Rect(0, 0, barWidth, steps));
        bmp.Unlock();

        var img = new System.Windows.Controls.Image { Source = bmp, Width = barWidth, Height = canvasH, Stretch = Stretch.Fill };
        LegendCanvas.Children.Add(img);
        Canvas.SetLeft(img, barLeft);
        Canvas.SetTop(img, 0);

        double[] dbLabels = { 0, -12, -24, -36, -48, -60, -72, -84, -96 };
        foreach (double db in dbLabels)
        {
            double t = db / -96.0;
            double y = t * canvasH;
            var tb = new TextBlock
            {
                Text = $"{db:F0}",
                Foreground = AxisBrush,
                FontSize = 9,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
            };
            Canvas.SetLeft(tb, barLeft + barWidth + 4);
            Canvas.SetTop(tb, y - 7);
            LegendCanvas.Children.Add(tb);
        }
    }

    private static (byte r, byte g, byte b) SpectrogramColormap(double t)
    {
        if (t <= 0) return (0, 0, 0);
        if (t < 1.0/8.0) { double s = t * 8; return ((byte)(0), (byte)(0), (byte)(25 + 110 * s)); }
        if (t < 2.0/8.0) { double s = (t - 1.0/8.0) * 8; return ((byte)(40 * s), (byte)(0), (byte)(135 + 65 * s)); }
        if (t < 3.0/8.0) { double s = (t - 2.0/8.0) * 8; return ((byte)(40 + 70 * s), (byte)(0), (byte)(200)); }
        if (t < 4.0/8.0) { double s = (t - 3.0/8.0) * 8; return ((byte)(110 + 120 * s), (byte)(40 * s), (byte)(200 - 20 * s)); }
        if (t < 5.0/8.0) { double s = (t - 4.0/8.0) * 8; return ((byte)(230 + 25 * s), (byte)(40 + 20 * s), (byte)(180 * (1 - s))); }
        if (t < 6.0/8.0) { double s = (t - 5.0/8.0) * 8; return ((byte)(255), (byte)(60 + 70 * s), (byte)(0)); }
        if (t < 7.0/8.0) { double s = (t - 6.0/8.0) * 8; return ((byte)(255), (byte)(130 + 125 * s), (byte)(0)); }
        double s2 = (t - 7.0/8.0) * 8;
        return ((byte)(255), (byte)(255), (byte)(180 * s2));
    }

    private void Window_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var scale = e.Delta > 0 ? 1.1 : 0.9;
        if (System.Windows.Input.Keyboard.Modifiers == ModifierKeys.Control) _scaleX = Math.Clamp(_scaleX * scale, 0.5, 10);
        else if (System.Windows.Input.Keyboard.Modifiers == ModifierKeys.Shift) _scaleY = Math.Clamp(_scaleY * scale, 0.5, 10);
        else { _scaleX = Math.Clamp(_scaleX * scale, 0.5, 10); _scaleY = Math.Clamp(_scaleY * scale, 0.5, 10); }
        ApplyTransform();
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left
            && System.Windows.Input.Keyboard.Modifiers == ModifierKeys.Shift)
        {
            _isPanning = true;
            _lastMousePos = e.GetPosition(this);
            SpectrogramImage.CaptureMouse();
        }
    }

    private void Window_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isPanning = false;
        SpectrogramImage.ReleaseMouseCapture();
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetPosition(this);
        _panX += pos.X - _lastMousePos.X;
        _panY += pos.Y - _lastMousePos.Y;
        _lastMousePos = pos;
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        SpectrogramImage.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(_scaleX, _scaleY),
                new TranslateTransform(_panX, _panY)
            }
        };
        SpectrogramImage.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void CopyPng_Click(object sender, RoutedEventArgs e)
    {
        if (SpectrogramImage.Source is BitmapSource bmp)
            System.Windows.Clipboard.SetImage(bmp);
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        _scaleX = _scaleY = 1;
        _panX = _panY = 0;
        ApplyTransform();
    }
}
