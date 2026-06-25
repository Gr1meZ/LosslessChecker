# AGENTS.md

## Project
LosslessChecker — .NET 10 WPF desktop app that analyzes audio files to detect lossy transcodes, upscales, and quality issues.

## Build & Test
```powershell
dotnet build
dotnet test
```
- **Close the app before building** — `dotnet build` fails if `LosslessChecker.exe` is locked by a running instance.
- Single solution file: `LosslessChecker.slnx` with two projects (`LosslessChecker` app + `LosslessChecker.Tests` xUnit).
- Target: `net10.0-windows` (Windows-only, WPF + Windows Forms).

## Architecture
- **MVVM** via `CommunityToolkit.Mvvm` (source generators: `[ObservableProperty]`, `[RelayCommand]`). Rebuild after adding generators.
- **Entry**: `App.xaml.cs` → `MainWindow.xaml` → `MainViewModel`.
- **Analysis pipeline**: `MainViewModel` → `AudioAnalyzer` → `AudioPipeline` (the real orchestrator).
  - `AudioPipeline` decodes audio to a `StereoBuffer` (left/right float arrays), then runs analyzers in parallel via `Task.Run`.
  - Analyzers: `CutoffDetector`, `ArtifactDetector`, `DrMeter`, `LufsMeter`, `TruePeakDetector`, `BitDepthValidator`, `UpscaleDetector`, `VinylDetector`, `ContainerAnalyzer`, `ResamplingDetector`, `PhaseAnalyzer`, `DcOffsetDetector`.
  - Scoring: `LosslessScorer` / `QualityScorer` → `VerdictGenerator`.
- **Data**: `AnalysisResult` is a large C# `record` (~50 fields) passed through the pipeline via `with` expressions.
- **DSP**: NWaves (FFT, windows), NAudio (decoding).

## Key files
| File | Purpose |
|------|---------|
| `LosslessChecker/Services/AudioPipeline.cs` | Main analysis orchestrator (all detectors wired here) |
| `LosslessChecker/Services/AudioDecoder.cs` | Decodes any format to `StereoBuffer` |
| `LosslessChecker/Models/AnalysisResult.cs` | Central result record |
| `LosslessChecker/Models/StereoBuffer.cs` | Decoded audio (Left, Right, SampleRate) |
| `LosslessChecker/ViewModels/MainViewModel.cs` | App state, file scanning, analysis dispatch |
| `LosslessChecker.Tests/Helpers/TestSignalGenerator.cs` | Synthetic signal generators for tests |

## Conventions
- File-scoped namespaces (`namespace X;`).
- Nullable enabled, implicit usings.
- Tests: xUnit `[Fact]`, one test class per analyzer, using `TestSignalGenerator` for synthetic input.
- Settings persisted to `settings.json` in the app directory (theme preference).
- No CI / GitHub Actions configured (local-only repo).
- `.gitignore` covers `bin/`, `obj/`, `/packages/`.
- `ALGORITHM_SOURCE.txt` is a condensed reference of the original algorithm code — not compiled, keep in sync if logic changes.
- `project_full.txt` — full project dump, not compiled.

## Gotchas
- **File lock**: The exe in `bin/Debug/` must not be running during `dotnet build`.
- **Generator-first**: Adding `[ObservableProperty]` or `[RelayCommand]` fields requires a build before IntelliSense sees generated members.
- **NAudio resampling** uses `MediaFoundationResampler` (Windows Media Foundation). Tests that decode real files need Windows.
- `AudioPipeline` creates fresh detector instances per call (avoided race condition in past bugfix).
- `SpectrogramWindow` uses OxyPlot WPF for rendering — theming must match app theme.
