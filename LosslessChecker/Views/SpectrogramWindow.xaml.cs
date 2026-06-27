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
        new(System.Windows.Media.Color.FromRgb(0xa6, 0xad, 0xc8));
    private static readonly SolidColorBrush GridBrush =
        new(System.Windows.Media.Color.FromRgb(0x45, 0x47, 0x5a));
    private static readonly SolidColorBrush CutoffBrush =
        new(System.Windows.Media.Color.FromRgb(0xf3, 0x8b, 0xa8));

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

        double[] freqLabels = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
        foreach (var freq in freqLabels)
        {
            if (freq > nyquist) break;
            double ratio = (Math.Log10(freq) - logMin) / logRange;
            double y = canvasH - ratio * canvasH;
            var tb = new TextBlock
            {
                Text = freq >= 1000 ? $"{freq / 1000:F0}k" : $"{freq:F0}",
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

            if (Math.Abs(nyquist - 22050) < 1)
            {
                double ratio22 = (Math.Log10(22050) - logMin) / logRange;
                double y22 = canvasH - ratio22 * canvasH;
                var tb22 = new TextBlock
                {
                    Text = "22.05k",
                    Foreground = AxisBrush,
                    FontSize = 9,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
                };
                System.Windows.Controls.Canvas.SetLeft(tb22, 0);
                System.Windows.Controls.Canvas.SetTop(tb22, y22 - 7);
                OverlayCanvas.Children.Add(tb22);
            }

            double[] standardMarkers = { 1000, 5000, 10000, 16000, 20000, 22050 };
            foreach (var freq in standardMarkers)
            {
                if (freq > nyquist) continue;
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
        if (e.ChangedButton == System.Windows.Input.MouseButton.Middle ||
            (e.ChangedButton == System.Windows.Input.MouseButton.Left
             && System.Windows.Input.Keyboard.Modifiers == ModifierKeys.Shift))
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
