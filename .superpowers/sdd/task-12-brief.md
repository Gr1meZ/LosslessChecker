# Task 12: Spectrogram 22.05k Label for CD

**Files:**
- Modify: `LosslessChecker/Views/SpectrogramWindow.xaml.cs`

## Step 1: Add 22.05k label when nyquist = 22050

In `DrawAxes()`, after the existing freqLabels loop (after the closing `}` of the foreach loop), add:

```csharp
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
```

This adds a "22.05k" label at the CD Nyquist frequency, making it easy to identify full-range CD-quality spectrum.

## Step 2: Build and test

Run: `dotnet build`
Expected: Build succeeds.

Run: `dotnet test`
Expected: All tests pass.

## Step 3: Commit

```bash
git add LosslessChecker/Views/SpectrogramWindow.xaml.cs
git commit -m "feat: add 22.05k label for CD-spectrum spectrograms"
```
