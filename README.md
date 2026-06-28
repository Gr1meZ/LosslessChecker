# LosslessChecker

Анализатор аутентичности аудиофайлов — определяет lossy-транскоды, апскейлы и проблемы качества.

## Возможности

- **Обнаружение lossy-транскодов** — выявляет MP3/AAC/etc., перекодированные в lossless-форматы (FLAC, WAV, ALAC)
- **Детекция апскейлов** — находит файлы с искусственно завышенной частотой дискретизации/битрейтом
- **Оценка качества** — DR (Dynamic Range), LUFS, True Peak, битовая глубина
- **Спектральный анализ** — частота среза, спектрограммы, артефакты low-pass фильтрации
- **Анализ контейнера** — проверка формата, кодеков, метаданных
- **Группировка** — артист → альбом → трек с вердиктами по альбомам
- **Экспорт результатов**

## Скриншоты

_Добавь скриншоты в `docs/screenshots/` и раскомментируй:_

<!-- ![Main window](docs/screenshots/main.png) -->
<!-- ![Spectrogram](docs/screenshots/spectrogram.png) -->

## Установка

### Требования
- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Сборка

```powershell
git clone https://github.com/USER/LosslessChecker.git
cd LosslessChecker
dotnet build
dotnet run --project LosslessChecker
```

## Технологии

| Компонент | Технология |
|-----------|-----------|
| UI | WPF (.NET 10) |
| Архитектура | MVVM (CommunityToolkit.Mvvm) |
| Декодинг аудио | NAudio + MediaFoundation |
| DSP/FFT | NWaves |
| Метаданные | TagLib# |
| Графики | OxyPlot |
| Тесты | xUnit |

## Архитектура

```
MainWindow.xaml → MainViewModel
       ↓
AudioAnalyzer → AudioPipeline
       ↓
Декодер → StereoBuffer (float[])
       ↓
Анализаторы (параллельно):
  CutoffDetector      ArtifactDetector
  DrMeter             LufsMeter
  TruePeakDetector    BitDepthValidator
  UpscaleDetector     VinylDetector
  ContainerAnalyzer   ResamplingDetector
  PhaseAnalyzer       DcOffsetDetector
       ↓
LosslessScorer / QualityScorer → VerdictGenerator
       ↓
AnalysisResult (record, ~50 полей)
```

## Запуск тестов

```powershell
dotnet test
```

## Структура проекта

```
LosslessChecker/
├── LosslessChecker/          # WPF приложение
│   ├── Models/               # AnalysisResult, StereoBuffer, ...
│   ├── Services/             # AudioPipeline, анализаторы, декодер
│   │   └── Analyzers/        # LufsMeter, TruePeakDetector, ...
│   ├── ViewModels/           # MainViewModel, AudioFileViewModel
│   ├── Views/                # MainWindow, SpectrogramWindow
│   └── Converters/           # WPF value converters
├── LosslessChecker.Tests/    # xUnit тесты
│   ├── Analyzers/
│   ├── Helpers/              # TestSignalGenerator
│   └── Services/
└── LosslessChecker.slnx      # Solution file
```

## Поддерживаемые форматы

| Формат | Расширения |
|--------|-----------|
| FLAC | `.flac` |
| WAV | `.wav` |
| MP3 | `.mp3` |
| AAC | `.m4a`, `.aac` |
| ALAC | `.m4a`, `.alac` |
| WMA | `.wma` |
| OGG Vorbis | `.ogg` |
| Opus | `.opus` |
| AIFF | `.aiff`, `.aif` |

## Лицензия

MIT
