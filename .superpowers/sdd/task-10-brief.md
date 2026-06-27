# Task 10: Spectrogram — Grid Lines, Pan/Zoom Rework, Label Fix

**Files:**
- Modify: `LosslessChecker/Views/SpectrogramWindow.xaml`
- Modify: `LosslessChecker/Views/SpectrogramWindow.xaml.cs`

## Step 1: Add frequency grid lines in DrawAxes

In `SpectrogramWindow.xaml.cs`, in the `DrawAxes()` method, after the existing `freqLabels` loop (after line 110, the line `OverlayCanvas.Children.Add(line);` inside the loop), add standard frequency markers as dashed lines:

```csharp
            // Standard frequency markers with dashed lines
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
```

## Step 2: Change pan trigger from LMB to MMB/Shift+LMB

Replace the `Window_MouseLeftButtonDown` handler. Change from plain left button to middle button or Shift+left button:

```csharp
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
```

## Step 3: Fix frequency label positioning

In `DrawAxes()`, for each `tb` TextBlock created in the freqLabels loop, change:

```csharp
            System.Windows.Controls.Canvas.SetLeft(tb, 0);
```

To:

```csharp
            System.Windows.Controls.Canvas.SetLeft(tb, -42);
```

Then in `SpectrogramWindow.xaml`, change the Grid margin from:

```xml
        <Grid Grid.Row="1" Margin="50,10,50,30">
```

To:

```xml
        <Grid Grid.Row="1" Margin="65,10,50,30">
```

This pushes labels into the left margin so they don't overlap the spectrogram image.

## Step 4: Build and test

Run: `dotnet build`
Expected: Build succeeds.

Run: `dotnet test`
Expected: All tests pass.

## Step 5: Commit

```bash
git add LosslessChecker/Views/SpectrogramWindow.xaml LosslessChecker/Views/SpectrogramWindow.xaml.cs
git commit -m "feat: add frequency grid lines, rework pan to MMB/Shift+LMB, fix label positioning"
```
