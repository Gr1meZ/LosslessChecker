using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LosslessChecker.Services;

namespace LosslessChecker.Views;

public partial class SpectrogramWindow : Window
{
    private readonly SpectrogramRenderer _renderer = new();
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
        RenderFull();
    }

    private void RenderFull()
    {
        if (_rawData == null) return;
        var bmp = _renderer.Render(_rawData, _dataWidth, _dataHeight,
            _durationSec, _sampleRate, _cutoffHz);
        SpectrogramImage.Source = bmp;
    }

    private void Window_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var scale = e.Delta > 0 ? 1.1 : 0.9;
        var st = SpectrogramImage.RenderTransform as ScaleTransform;
        double sx = st?.ScaleX ?? 1, sy = st?.ScaleY ?? 1;
        if (Keyboard.Modifiers == ModifierKeys.Control)
            sx *= scale;
        else if (Keyboard.Modifiers == ModifierKeys.Shift)
            sy *= scale;
        else { sx *= scale; sy *= scale; }

        SpectrogramImage.RenderTransform = new ScaleTransform(
            Math.Clamp(sx, 0.5, 10), Math.Clamp(sy, 0.5, 10));
        SpectrogramImage.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _lastMousePos = e.GetPosition(this);
        SpectrogramImage.CaptureMouse();
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        SpectrogramImage.ReleaseMouseCapture();
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetPosition(this);
        double dx = pos.X - _lastMousePos.X, dy = pos.Y - _lastMousePos.Y;

        var st = SpectrogramImage.RenderTransform as ScaleTransform;
        var tt = new TranslateTransform(dx, dy);
        if (st != null)
            SpectrogramImage.RenderTransform = new TransformGroup { Children = { st, tt } };
        else
            SpectrogramImage.RenderTransform = tt;

        _lastMousePos = pos;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_rawData != null) RenderFull();
    }

    private void CopyPng_Click(object sender, RoutedEventArgs e)
    {
        if (SpectrogramImage.Source is BitmapSource bmp)
            System.Windows.Clipboard.SetImage(bmp);
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        SpectrogramImage.RenderTransform = new ScaleTransform(1, 1);
        SpectrogramImage.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
    }
}
