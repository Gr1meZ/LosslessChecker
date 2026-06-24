using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Imaging;

namespace LosslessChecker.Views;

public partial class SpectrogramWindow : Window
{
    private readonly byte[]? _rawData;
    private readonly int _dataWidth, _dataHeight;
    private readonly double _durationSec, _sampleRate, _cutoffHz;
    private System.Windows.Point _lastMousePos;
    private bool _isPanning;

    public SpectrogramWindow(BitmapSource? bmp, string title = "Spectrogram")
    {
        InitializeComponent();
        Title = $"Спектрограмма — {title}";
        SpectrogramImage.Source = bmp;
    }

    public SpectrogramWindow(byte[] rawData, int dataWidth, int dataHeight,
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
        RenderFull();
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
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xa6, 0xad, 0xc8)),
                FontSize = 9,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
            };
            System.Windows.Controls.Canvas.SetLeft(tb, 0);
            System.Windows.Controls.Canvas.SetTop(tb, y - 7);
            OverlayCanvas.Children.Add(tb);

            var line = new Line
            {
                X1 = 0, Y1 = y, X2 = canvasW, Y2 = y,
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x45, 0x47, 0x5a)),
                StrokeThickness = 0.5,
                Opacity = 0.3
            };
            OverlayCanvas.Children.Add(line);
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
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xa6, 0xad, 0xc8)),
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
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf3, 0x8b, 0xa8)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 6, 3 }
            };
            OverlayCanvas.Children.Add(cutLine);

            var cutLabel = new TextBlock
            {
                Text = $"Срез: {_cutoffHz:F0} Гц",
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf3, 0x8b, 0xa8)),
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
        var st = SpectrogramImage.RenderTransform as ScaleTransform;
        double sx = st?.ScaleX ?? 1, sy = st?.ScaleY ?? 1;
        if (System.Windows.Input.Keyboard.Modifiers == ModifierKeys.Control) sx *= scale;
        else if (System.Windows.Input.Keyboard.Modifiers == ModifierKeys.Shift) sy *= scale;
        else { sx *= scale; sy *= scale; }
        SpectrogramImage.RenderTransform = new ScaleTransform(Math.Clamp(sx, 0.5, 10), Math.Clamp(sy, 0.5, 10));
        SpectrogramImage.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isPanning = true; _lastMousePos = e.GetPosition(this); SpectrogramImage.CaptureMouse();
    }
    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false; SpectrogramImage.ReleaseMouseCapture();
    }
    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetPosition(this);
        double dx = pos.X - _lastMousePos.X, dy = pos.Y - _lastMousePos.Y;
        var st = SpectrogramImage.RenderTransform as ScaleTransform;
        var tt = new TranslateTransform(dx, dy);
        SpectrogramImage.RenderTransform = st != null ? new TransformGroup { Children = { st, tt } } : tt;
        _lastMousePos = pos;
    }
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_rawData != null) { RenderFull(); }
    }
    private void CopyPng_Click(object sender, RoutedEventArgs e)
    {
        if (SpectrogramImage.Source is BitmapSource bmp) System.Windows.Clipboard.SetImage(bmp);
    }
    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        SpectrogramImage.RenderTransform = new ScaleTransform(1, 1);
        SpectrogramImage.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
    }
}
