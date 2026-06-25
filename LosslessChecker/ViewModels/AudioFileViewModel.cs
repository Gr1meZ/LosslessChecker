using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LosslessChecker.Models;
using LosslessChecker.Services;

namespace LosslessChecker.ViewModels;

public partial class AudioFileViewModel : ObservableObject
{
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _format = "";
    [ObservableProperty] private double _cutoffFrequency;
    [ObservableProperty] private double _dynamicRange;
    [ObservableProperty] private double _samplePeakDb;
    [ObservableProperty] private double _truePeakDb;
    [ObservableProperty] private double _clippingPercent;
    [ObservableProperty] private string _authenticity = "";
    [ObservableProperty] private double _losslessScorePercent;
    [ObservableProperty] private double _hiResScorePercent;
    [ObservableProperty] private int _qualityScore;
    [ObservableProperty] private double _qualityScorePercent;
    [ObservableProperty] private double _metricsCoverage;
    [ObservableProperty] private string _decision = "";
    [ObservableProperty] private string _verdict = "";
    [ObservableProperty] private string _artifactLevel = "None";
    [ObservableProperty] private bool _hasArtifacts;
    [ObservableProperty] private AnalysisStatus _analysisStatus = AnalysisStatus.Pending;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _bitDepthSuspicious;
    [ObservableProperty] private bool _isUpscale;
    [ObservableProperty] private double _correlation = 1.0;
    [ObservableProperty] private double _dcOffsetL;
    [ObservableProperty] private double _dcOffsetR;
    [ObservableProperty] private double _integratedLufs;
    [ObservableProperty] private double _overallRmsDb;
    [ObservableProperty] private int _sampleRate;
    [ObservableProperty] private int _bitDepth;
    [ObservableProperty] private int _channels;
    [ObservableProperty] private string _encoderMatch = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _album = "";
    [ObservableProperty] private string _genre = "";
    [ObservableProperty] private double _durationSeconds;
    [ObservableProperty] private byte[]? _coverData;
    [ObservableProperty] private WriteableBitmap? _spectrogramBitmap;
    [ObservableProperty] private int _mp3Bitrate;

    public int AacBitrate { get; private set; }
    public bool IsAac { get; private set; }
    public int ActualBitrate { get; private set; }

    public string BitrateDisplay
    {
        get
        {
            var headerBr = Mp3Bitrate > 0 ? Mp3Bitrate : AacBitrate > 0 ? AacBitrate : 0;
            if (headerBr > 0 && ActualBitrate > 0)
                return $"{headerBr}→{ActualBitrate}";
            if (ActualBitrate > 0)
                return $"{ActualBitrate}";
            if (headerBr > 0)
                return $"{headerBr}";
            return "—";
        }
    }

    public System.Windows.Media.Brush BitrateColor
    {
        get
        {
            var headerBr = Mp3Bitrate > 0 ? Mp3Bitrate : AacBitrate > 0 ? AacBitrate : 0;
            if (headerBr <= 0 || ActualBitrate <= 0)
                return System.Windows.Application.Current.TryFindResource("FgMutedBrush") as System.Windows.Media.Brush
                    ?? System.Windows.Media.Brushes.Gray;

            double ratio = (double)headerBr / ActualBitrate;
            if (ratio > 2.5)
                return System.Windows.Application.Current.TryFindResource("FakeRedBrush") as System.Windows.Media.Brush
                    ?? System.Windows.Media.Brushes.Red;
            if (ratio > 1.5)
                return System.Windows.Application.Current.TryFindResource("SuspiciousAmberBrush") as System.Windows.Media.Brush
                    ?? System.Windows.Media.Brushes.Orange;
            if (ratio > 1.1 || ratio < 0.9)
                return System.Windows.Application.Current.TryFindResource("SuspiciousAmberBrush") as System.Windows.Media.Brush
                    ?? System.Windows.Media.Brushes.Orange;
            return System.Windows.Application.Current.TryFindResource("LosslessGreenBrush") as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.Green;
        }
    }

    public string VerdictLabel => Decision switch
    {
        "KEEP" => SampleRate >= 88200 && HiResScorePercent >= 70 ? "HI-RES" : "LOSSLESS",
        "KEEP (poor master)" => "LOSSLESS",
        "INVESTIGATE" => "NOT SURE",
        "REPLACE" => Mp3Bitrate > 0
            ? $"MP3 {(ActualBitrate > 0 ? ActualBitrate : Mp3Bitrate)}"
            : IsAac && AacBitrate > 0
                ? $"AAC {(ActualBitrate > 0 ? ActualBitrate : AacBitrate)}"
                : "REPLACE",
        _ => Decision
    };

    public string VerdictDisplayText => VerdictLabel switch
    {
        "LOSSLESS" => "✅ LOSSLESS",
        "HI-RES" => "✅ HI-RES",
        "NOT SURE" => "⚠ NOT SURE",
        "REPLACE" => "❌ REPLACE",
        _ => VerdictLabel.StartsWith("MP3") || VerdictLabel.StartsWith("AAC")
            ? $"❌ {VerdictLabel}" : VerdictLabel
    };

    // Detail panel: metric items collection
    [ObservableProperty] private ObservableCollection<MetricItem> _metricItems = new();

    public string FilePath { get; }

    private float[]? _rawSpectro;
    private int _spectroWidth, _spectroHeight;
    private AnalysisResult? _lastResult;
    internal AnalysisResult? LastResult => _lastResult;
    public float[]? RawSpectrogram => _rawSpectro;
    public int SpectroWidth => _spectroWidth;
    public int SpectroHeight => _spectroHeight;

    public AudioFileViewModel(AudioFileInfo fileInfo)
    {
        FilePath = fileInfo.FilePath;
        _fileName = fileInfo.FileName;
    }

    public void ApplyResult(AnalysisResult r)
    {
        _lastResult = r with { SpectrogramDb = null };
        _rawSpectro = r.SpectrogramDb;        FileName = r.FileName;
        Format = r.Format;
        CutoffFrequency = r.CutoffFrequency;
        DynamicRange = r.DynamicRange;
        SamplePeakDb = r.SamplePeakDb;
        TruePeakDb = r.TruePeakDb;
        ClippingPercent = r.ClippingPercent;
        Authenticity = r.Authenticity;
        LosslessScorePercent = r.LosslessScore;
        HiResScorePercent = r.HiResScore;
        QualityScore = r.QualityScore;
        QualityScorePercent = r.QualityScorePercent;
        MetricsCoverage = r.MetricsCoverage;
        Decision = r.Decision;
        Verdict = r.Verdict;
        ArtifactLevel = r.ArtifactLevel;
        HasArtifacts = r.HasArtifacts;
        AnalysisStatus = r.AnalysisStatus;
        ErrorMessage = r.ErrorMessage ?? "";
        BitDepthSuspicious = r.BitDepthSuspicious;
        IsUpscale = r.IsUpscale;
        Correlation = r.Correlation;
        DcOffsetL = r.DcOffsetL;
        DcOffsetR = r.DcOffsetR;
        IntegratedLufs = r.IntegratedLufs;
        OverallRmsDb = r.OverallRmsDb;
        SampleRate = r.SampleRate;
        BitDepth = r.BitDepth;
        Channels = r.Channels;
        Artist = r.Artist;
        Album = r.Album;
        Genre = r.Genre;
        DurationSeconds = r.DurationSeconds;
        CoverData = r.CoverData;
        EncoderMatch = r.EncoderMatch;
        Mp3Bitrate = r.Mp3Bitrate;
        AacBitrate = r.AacBitrate;
        IsAac = r.IsAac;
        ActualBitrate = r.ActualBitrate;

        if (r.SpectrogramDb is { Length: > 0 })
        {
            _rawSpectro = r.SpectrogramDb;
            _spectroWidth = r.SpectrogramWidth;
            _spectroHeight = r.SpectrogramHeight;
        }

        BuildMetricItems(r);

        OnPropertyChanged(nameof(VerdictLabel));
        OnPropertyChanged(nameof(VerdictDisplayText));
    }

    partial void OnDecisionChanged(string value)
    {
        OnPropertyChanged(nameof(VerdictLabel));
        OnPropertyChanged(nameof(VerdictDisplayText));
    }

    private void BuildMetricItems(AnalysisResult r)
    {
        var items = new ObservableCollection<MetricItem>();
        var isLossless = r.Authenticity == "TRUE";
        var nyquist = r.SampleRate / 2.0;

        // === Group: Спектральный анализ ===
        items.Add(new MetricItem { Name = "Спектральный анализ", IsHeader = true });

        // Cutoff
        var cutoffRatio = nyquist > 0 ? r.CutoffFrequency / nyquist : 1.0;
        bool isHiRes = r.SampleRate >= 88200;
        string cutoffStatus;
        string cutoffColor;
        string cutoffTypical;
        if (isHiRes)
        {
            cutoffStatus = r.CutoffFrequency > 22100 ? "✓ Отлично"
                : r.CutoffFrequency > 20000 ? "⚠ Подозрительно"
                : "✗ Плохо";
            cutoffColor = r.CutoffFrequency > 22100 ? "#2EA043"
                : r.CutoffFrequency > 20000 ? "#D29922"
                : "#CF222E";
            cutoffTypical = ">22 кГц — отлично (настоящий Hi-Res)\n20–22 кГц — подозрительно (возможен CD-апскейл)\n<20 кГц — плохо (апскейл из lossy)";
        }
        else
        {
            cutoffStatus = cutoffRatio >= 0.95 ? "✓ Отлично" : cutoffRatio >= 0.85 ? "⚠ Подозрительно" : "✗ Плохо";
            cutoffColor = cutoffRatio >= 0.95 ? "#2EA043" : cutoffRatio >= 0.85 ? "#D29922" : "#CF222E";
            cutoffTypical = ">95% Найквиста — отлично\n85–95% — подозрительно (возможен MP3 320)\n<85% — точно сжато (MP3 128-256)";
        }
        items.Add(new MetricItem
        {
            Category = "Спектр",
            Name = "Частотный срез (Cutoff)",
            Value = $"{r.CutoffFrequency:F0} Гц ({cutoffRatio * 100:F0}% Найквиста)",
            Status = cutoffStatus,
            StatusColor = cutoffColor,
            Description = "Максимальная частота, выше которой сигнал отсутствует. Настоящий lossless сохраняет полный спектр до частоты Найквиста. Lossy-кодеки (MP3, AAC) обрезают высокие частоты для экономии места.",
            Typical = cutoffTypical
        });

        // Shelf type
        if (r.ShelfType.Length > 0)
        {
            string shelfLabel = r.ShelfType switch
            {
                "Brickwall" => "Кирпичная стена (lossy-кодек)",
                "Filtered" => "Фильтрованный спад",
                "Natural" => "Естественный спад",
                _ => r.ShelfType
            };
            // Hi-Res: "Filtered" above 22kHz is normal mastering LP, not suspicious
            string shelfStatus;
            string shelfColor;
            if (r.ShelfType == "Natural")
                (shelfStatus, shelfColor) = ("✓ Хорошо", "#2EA043");
            else if (r.ShelfType == "Filtered" && isHiRes && r.CutoffFrequency > 22100)
                (shelfStatus, shelfColor) = ("✓ Ок", "#2EA043");
            else if (r.ShelfType == "Filtered")
                (shelfStatus, shelfColor) = ("⚠ Подозрительно", "#D29922");
            else
                (shelfStatus, shelfColor) = ("✗ Плохо", "#CF222E");
            items.Add(new MetricItem
            {
                Category = "Спектр",
                Name = "Характер спада",
                Value = shelfLabel,
                Status = shelfStatus,
                StatusColor = shelfColor,
                Description = "Форма спектра выше частоты среза. Резкая 'кирпичная стена' — признак lossy-кодека. Плавный естественный спад — аналоговая запись или качественный цифровой трансфер.",
                Typical = "Естественный — отлично\nФильтрованный — подозрительно\nКирпичная стена — сжато"
            });
        }

        // Encoder match
        if (r.EncoderMatch != "None" && r.EncoderMatch.Length > 0)
        {
            bool isHiResMatch = r.EncoderMatch == "None (Hi-Res)";
            items.Add(new MetricItem
            {
                Category = "Спектр",
                Name = "Соответствие кодека",
                Value = r.EncoderMatch,
                Status = isHiResMatch ? "✓ Настоящий" : "⚠ Обнаружено",
                StatusColor = isHiResMatch ? "#2EA043" : "#D29922",
                Description = "Частота среза сопоставлена с известными кодеками. Каждый lossy-кодек имеет характерную частоту среза.",
                Typical = "MP3 128 → 16 кГц\nMP3 256 → 18 кГц\nMP3 320 / AAC → 20 кГц"
            });
        }

        // Nyquist
        items.Add(new MetricItem
        {
            Category = "Спектр",
            Name = "Теоретический предел (Найквист)",
            Value = $"{nyquist:F0} Гц",
            Status = "—",
            StatusColor = "#585b70",
            Description = "Теорема Котельникова: полезный сигнал = Sample Rate / 2. Для 44.1 кГц предел — 22.05 кГц, для 48 кГц — 24 кГц.",
            Typical = "44.1 кГц → 22 050 Гц\n48 кГц → 24 000 Гц\n96 кГц → 48 000 Гц"
        });

        // Artifacts
        string artStatus = r.ArtifactLevel == "None" ? "✓ Чисто" : r.ArtifactLevel == "Weak" ? "⚠ Слабые" : "✗ Обнаружены";
        string artColor = r.ArtifactLevel == "None" ? "#2EA043" : r.ArtifactLevel == "Weak" ? "#D29922" : "#CF222E";
        string artValue = r.ArtifactLevel;
        if (r.ArtifactType != "None" && r.ArtifactType.Length > 0)
            artValue += $" ({r.ArtifactType})";
        items.Add(new MetricItem
        {
            Category = "Спектр",
            Name = "Артефакты сжатия",
            Value = artValue,
            Status = artStatus,
            StatusColor = artColor,
            Description = "Характерные искажения lossy-кодеков: спектральная 'полка' шума, блоковые артефакты на границах MP3-гранул, аномальная плоскостность спектра выше среза.",
            Typical = "Нет — чисто\nСлабые — возможно, был сжат\nСредние/Сильные — пережат из lossy"
        });

        // === Group: Динамика ===
        items.Add(new MetricItem { Name = "Динамический диапазон и громкость", IsHeader = true });

        // DR
        string drStatus = r.DynamicRange >= 10 ? "✓ Аудиофил" : r.DynamicRange >= 6 ? "✓ Хорошо" : r.DynamicRange >= 3 ? "⚠ Сжато" : "✗ Пережато";
        string drColor = r.DynamicRange >= 6 ? "#2EA043" : r.DynamicRange >= 3 ? "#D29922" : "#CF222E";
        items.Add(new MetricItem
        {
            Category = "Динамика",
            Name = "Динамический диапазон (DR)",
            Value = $"DR{r.DynamicRange:F0}",
            Status = drStatus,
            StatusColor = drColor,
            Description = "Разница между пиковым и средним уровнем громкости (TT DR Meter). Высокий DR — живой, дышащий звук. Низкий DR — 'кирпичная стена' лимитера, утомляет слух (Loudness War). Откалибровано под foobar2000.",
            Typical = "DR10+ — аудиофил (джаз, классика)\nDR6–9 — хорошо (рок, качественный поп)\nDR3–5 — сжато (современный поп, EDM)\nDR<3 — пережато (громко, плоско)"
        });

        // True Peak
        string tpStatus = r.TruePeakDb <= 0 ? "✓ Чисто" : r.TruePeakDb <= 1 ? "⚠ Искажения" : "✗ Перегруз";
        string tpColor = r.TruePeakDb <= 0 ? "#2EA043" : r.TruePeakDb <= 1 ? "#D29922" : "#CF222E";
        items.Add(new MetricItem
        {
            Category = "Динамика",
            Name = "True Peak (межсэмпловый пик)",
            Value = $"{r.TruePeakDb:F1} dBTP",
            Status = tpStatus,
            StatusColor = tpColor,
            Description = "Реальный пик аналогового сигнала между цифровыми сэмплами (ITU-R BS.1770-4). Если >0 dBTP — искажения при конвертации в MP3/AAC или на ЦАП. Стриминговый стандарт: не выше −1.0 dBTP.",
            Typical = "<0 dBTP — отлично\n0..+1 dBTP — искажения\n>+1 dBTP — сильный перегруз"
        });

        // Sample Peak
        items.Add(new MetricItem
        {
            Category = "Динамика",
            Name = "Sample Peak (цифровой пик)",
            Value = $"{r.SamplePeakDb:F1} dBFS",
            Status = r.SamplePeakDb < 0 ? "✓ Ок" : r.SamplePeakDb < 0.1 ? "⚠ Потолок" : "✗ Клипп",
            StatusColor = r.SamplePeakDb < 0 ? "#2EA043" : r.SamplePeakDb < 0.1 ? "#D29922" : "#CF222E",
            Description = "Максимальное значение амплитуды среди цифровых сэмплов. 0 dBFS — цифровой потолок. Идеальный запас: от −0.1 до −1.0 dBFS.",
            Typical = "<0 dBFS — норма\n=0 dBFS — возможен клиппинг"
        });

        // Clipping
        string clipStatus = r.ClippingPercent <= 0 ? "✓ Нет" : r.ClippingPercent < 0.5 ? "⚠ Единично" : r.ClippingPercent < 5 ? "⚠ Заметно" : "✗ Сильный";
        string clipColor = r.ClippingPercent <= 0 ? "#2EA043" : r.ClippingPercent < 0.5 ? "#D29922" : "#CF222E";
        items.Add(new MetricItem
        {
            Category = "Динамика",
            Name = "Клиппинг (срезанные пики)",
            Value = $"{r.ClippingPercent:F2}%",
            Status = clipStatus,
            StatusColor = clipColor,
            Description = "Процент сэмплов, достигших цифрового потолка. Три и более подряд — цифровой брак мастеринга (Brickwall Limiting с перегрузом).",
            Typical = "0% — чисто\n<0.5% — пренебрежимо\n0.5–5% — заметно\n>5% — сильные искажения"
        });

        // LUFS
        string lufsStatus = r.IntegratedLufs < -16 ? "✓ Динамично" : r.IntegratedLufs < -11 ? "✓ Норма" : r.IntegratedLufs < -7 ? "⚠ Громко" : "✗ Пережато";
        string lufsColor = r.IntegratedLufs < -11 ? "#2EA043" : r.IntegratedLufs < -7 ? "#D29922" : "#CF222E";
        string lufsLabel = r.IntegratedLufs < -100 ? "—" : $"{r.IntegratedLufs:F1} LUFS";
        items.Add(new MetricItem
        {
            Category = "Динамика",
            Name = "Интегрированная громкость (LUFS)",
            Value = lufsLabel,
            Status = lufsStatus,
            StatusColor = lufsColor,
            Description = "Средняя воспринимаемая громкость по стандарту ITU-R BS.1770-4. Показывает участие в 'войне громкости': чем громче → тем сильнее сжат звук. Стриминги приводят к −14 LUFS.",
            Typical = "<−16 LUFS — динамично (аудиофил)\n−14 LUFS — стриминг-цель\n−8..−11 — коммерческий стандарт\n>−7 LUFS — экстремально громко"
        });

        // Overall RMS
        var overallRms = r.OverallRmsDb;
        if (overallRms < 0)
        {
            items.Add(new MetricItem
            {
                Category = "Динамика",
                Name = "Общий RMS (средняя громкость)",
                Value = $"{overallRms:F1} dBFS",
                Status = overallRms > -6 ? "⚠ Громко" : overallRms > -12 ? "✓ Норма" : "✓ Тихо",
                StatusColor = overallRms > -6 ? "#D29922" : "#2EA043",
                Description = "Среднеквадратичный уровень всего трека. Аналог колонки 'RMS' в foobar2000. Чем ближе к 0 — тем громче.",
                Typical = "−12..−6 dBFS — норма\n>−6 — очень громко\n<−12 — тихо"
            });
        }

        // PLR
        if (r.Plr > 0)
        {
            string plrStatus = r.Plr >= 8 ? "✓ Хорошо" : r.Plr >= 6 ? "⚠ Сжато" : "✗ Пережато";
            string plrColor = r.Plr >= 8 ? "#2EA043" : r.Plr >= 6 ? "#D29922" : "#CF222E";
            items.Add(new MetricItem
            {
                Category = "Динамика",
                Name = "Peak-to-Loudness Ratio (PLR)",
                Value = $"{r.Plr:F1} дБ",
                Status = plrStatus,
                StatusColor = plrColor,
                Description = "Разница между True Peak и Integrated LUFS. Показывает сохранность макродинамики. PLR < 6-7 дБ — трек лишён динамических перепадов.",
                Typical = ">8 дБ — отлично\n6–8 дБ — сжато\n<6 дБ — пережато"
            });
        }

        // === Group: Технические параметры ===
        items.Add(new MetricItem { Name = "Технические параметры", IsHeader = true });

        // Bit Depth
        items.Add(new MetricItem
        {
            Category = "Техника",
            Name = "Битовая глубина",
            Value = $"{r.BitDepth} бит (эфф. {r.EffectiveBitDepth} бит)",
            Status = r.BitDepthSuspicious ? "⚠ Подозрительно" : "✓ Честно",
            StatusColor = r.BitDepthSuspicious ? "#D29922" : "#2EA043",
            Description = "Соответствие заявленной битности реальной. Часто 16-битный файл сохраняют в 24-битный контейнер с нулями в младших битах — размер растёт, качество нет.",
            Typical = "Совпадает — честно\nМладшие биты нули — фейк"
        });

        // LSB zero-pad
        if (r.LsbZeroPadded)
        {
            items.Add(new MetricItem
            {
                Category = "Техника",
                Name = "Нулевые младшие биты",
                Value = "Обнаружены",
                Status = "✗ Фейк",
                StatusColor = "#CF222E",
                Description = "Младшие 8 бит 24-битного файла всегда равны нулю — это 16-битный файл, сохранённый в 24-битном контейнере.",
                Typical = "Не обнаружены — честный 24 бит\nОбнаружены — фейковый 24 бит"
            });
        }

        // DC Offset
        bool hasDc = Math.Abs(r.DcOffsetL) > 0.01 || Math.Abs(r.DcOffsetR) > 0.01;
        items.Add(new MetricItem
        {
            Category = "Техника",
            Name = "DC смещение",
            Value = $"L={r.DcOffsetL:F4}% R={r.DcOffsetR:F4}%",
            Status = hasDc ? "⚠ Обнаружено" : "✓ Нет",
            StatusColor = hasDc ? "#D29922" : "#2EA043",
            Description = "Постоянная составляющая сигнала. Должно быть близко к 0.0000%. Наличие выше 0.01% съедает динамический диапазон и вызывает щелчки.",
            Typical = "0.0000% — норма\n>0.01% — дефект оцифровки"
        });

        // Phase
        string phaseStatus = r.Correlation >= 0 ? "✓ Норма" : "✗ Проблема";
        string phaseColor = r.Correlation >= 0 ? "#2EA043" : "#CF222E";
        string phaseValue = $"{r.Correlation:F2}";
        if (!r.IsMonoCompatible) phaseValue += " (несовместимо с моно)";
        items.Add(new MetricItem
        {
            Category = "Техника",
            Name = "Фаза / Стереокорреляция",
            Value = phaseValue,
            Status = phaseStatus,
            StatusColor = phaseColor,
            Description = "Степень синфазности каналов. +1.0 = идеальное моно, 0..+0.9 = нормальное стерео, <0 = противофаза (звук пропадёт при моно-воспроизведении).",
            Typical = "0..+1.0 — норма\n<0 — проблема с фазой"
        });

        // Upscale
        if (r.SampleRate >= 88200)
        {
            string upStatus = r.IsUpscale ? "✗ Апскейл" : "✓ Настоящий Hi-Res";
            string upColor = r.IsUpscale ? "#CF222E" : "#2EA043";
            string upValue = r.IsUpscale ? $"Апскейл (HF макс {r.MaxHfDb:F0} dB)" : $"Контент выше 22 кГц ({r.MaxHfDb:F0} dB)";
            items.Add(new MetricItem
            {
                Category = "Техника",
                Name = "Hi-Res аутентичность",
                Value = upValue,
                Status = upStatus,
                StatusColor = upColor,
                Description = "Наличие реального ультразвукового контента выше 22 кГц. Если заявлен 96 кГц, а ВЧ отсутствуют — это апскейл из CD-качества (44.1/48 кГц).",
                Typical = ">−30 dB — настоящий Hi-Res\n−30..−50 dB — сомнительно\n<−50 dB — апскейл из CD"
            });
        }

        // Channels
        items.Add(new MetricItem
        {
            Category = "Техника",
            Name = "Каналы",
            Value = r.Channels == 1 ? "Моно" : r.Channels == 2 ? "Стерео" : $"{r.Channels} каналов",
            Status = "—",
            StatusColor = "#585b70",
            Description = "Количество аудиоканалов в файле.",
            Typical = "1 — моно\n2 — стерео"
        });

        if (r.Mp3Bitrate > 0)
        {
            items.Add(new MetricItem { Name = "Характеристики MP3", IsHeader = true });
            items.Add(new MetricItem
            {
                Category = "MP3",
                Name = "Битрейт",
                Value = $"{r.Mp3Bitrate} kbps",
                Status = "—",
                StatusColor = "#585b70",
                Description = "Заявленный битрейт MP3-файла из заголовка."
            });
            if (r.Mp3Encoder.Length > 0 && r.Mp3Encoder != "Error")
            {
                items.Add(new MetricItem
                {
                    Category = "MP3",
                    Name = "Кодер",
                    Value = r.Mp3Encoder,
                    Status = "—",
                    StatusColor = "#585b70",
                    Description = "Идентифицированный MP3-кодер (LAME, FhG, etc)."
                });
            }
            string mp3QualStatus = r.Mp3QualityScore >= 80 ? "✓ Хороший рип" : r.Mp3QualityScore >= 50 ? "⚠ Средний" : "✗ Плохой";
            string mp3QualColor = r.Mp3QualityScore >= 80 ? "#2EA043" : r.Mp3QualityScore >= 50 ? "#D29922" : "#CF222E";
            items.Add(new MetricItem
            {
                Category = "MP3",
                Name = "Качество MP3",
                Value = $"{r.Mp3QualityScore:F0}%",
                Status = mp3QualStatus,
                StatusColor = mp3QualColor,
                Description = "Оценка качества MP3-рипа: соответствие среза битрейту, артефакты, спектральные дыры."
            });
        }

        // === Group: Итоговая оценка ===
        items.Add(new MetricItem { Name = "Итоговая оценка", IsHeader = true });

        // Lossless Score
        string losslessStatus = r.LosslessScore >= 85 ? "✓ Отлично" : r.LosslessScore >= 60 ? "⚠ Средне" : "✗ Плохо";
        string losslessColor = r.LosslessScore >= 85 ? "#2EA043" : r.LosslessScore >= 60 ? "#D29922" : "#CF222E";
        string losslessLabel = r.Authenticity == "LOSSY (MP3)" ? "LOSSY (MP3)" : r.Authenticity;
        items.Add(new MetricItem
        {
            Category = "Итог",
            Name = "Подлинность",
            Value = losslessLabel,
            Status = losslessStatus,
            StatusColor = losslessColor,
            Description = "Оценка подлинности аудиофайла. TRUE — настоящий lossless. FALSE — фейк, пережат из lossy. UNCERTAIN — подозрительный. LOSSY (MP3) — легальный lossy-формат.",
            Typical = "TRUE — настоящий lossless\nUNCERTAIN — подозрительный\nFALSE — фейк\nLOSSY (MP3) — MP3-файл"
        });

        // Hi-Res Score (only for Hi-Res files)
        if (r.HiResScore > 0 || r.SampleRate >= 88200)
        {
            string hrStatus = r.HiResScore >= 70 ? "✓ Настоящий Hi-Res" : r.HiResScore >= 40 ? "⚠ Сомнительно" : "✗ Апскейл";
            string hrColor = r.HiResScore >= 70 ? "#2EA043" : r.HiResScore >= 40 ? "#D29922" : "#CF222E";
            items.Add(new MetricItem
            {
                Category = "Итог",
                Name = "Подлинность Hi-Res",
                Value = r.SampleRate >= 88200 ? $"{r.HiResScore:F0}%" : "—",
                Status = hrStatus,
                StatusColor = hrColor,
                Description = "Оценка подлинности Hi-Res (0–100%). Проверяет наличие реального ультразвукового контента выше 22 кГц. Только для файлов ≥88.2 кГц.",
                Typical = "70–100% — настоящий Hi-Res\n40–69% — сомнительно\n<40% — апскейл из CD"
            });
        }

        // Metrics Coverage
        items.Add(new MetricItem
        {
            Category = "Итог",
            Name = "Охват метрик",
            Value = $"{r.MetricsCoverage:F0}%",
            Status = r.MetricsCoverage >= 80 ? "✓ Хорошо" : r.MetricsCoverage >= 60 ? "⚠ Средне" : "✗ Мало",
            StatusColor = r.MetricsCoverage >= 80 ? "#2EA043" : r.MetricsCoverage >= 60 ? "#D29922" : "#CF222E",
            Description = "Процент метрик, прошедших пороговые значения. Показывает, насколько 'чист' файл по всем проверкам. 100% — все метрики в норме.",
            Typical = "80–100% — отлично\n60–79% — есть замечания\n<60% — много проблем"
        });

        // Quality
        string qualStatus = r.QualityScorePercent >= 70 ? "✓ Отлично" : r.QualityScorePercent >= 40 ? "⚠ Нормально" : "✗ Плохо";
        string qualColor = r.QualityScorePercent >= 70 ? "#2EA043" : r.QualityScorePercent >= 40 ? "#D29922" : "#CF222E";
        items.Add(new MetricItem
        {
            Category = "Итог",
            Name = "Качество мастеринга",
            Value = $"{r.QualityScorePercent:F0}%",
            Status = qualStatus,
            StatusColor = qualColor,
            Description = "Взвешенная оценка качества мастеринга (0–100%). DR (вес 25), клиппинг (20), True Peak (13), LUFS (15), DC Offset (8), фаза (12), битность (5).",
            Typical = "70–100% — отличный мастеринг\n40–69% — средний мастеринг\n<40% — плохой мастеринг"
        });

        // Decision
        string decColor = r.Decision.StartsWith("KEEP") ? "#2EA043" : r.Decision == "INVESTIGATE" ? "#D29922" : "#CF222E";
        string decText = r.Decision switch
        {
            "KEEP" => "ОСТАВИТЬ",
            "KEEP (poor master)" => "ОСТАВИТЬ (плохой мастеринг)",
            "INVESTIGATE" => "ПРОВЕРИТЬ",
            "REPLACE" => "ЗАМЕНИТЬ",
            _ => r.Decision
        };
        items.Add(new MetricItem
        {
            Category = "Итог",
            Name = "Решение",
            Value = decText,
            Status = decText,
            StatusColor = decColor,
            Description = "Рекомендация: ОСТАВИТЬ — файл подлинный, качество приемлемо. ПРОВЕРИТЬ — подозрительный. ЗАМЕНИТЬ — фейк. Настоящий lossless НИКОГДА не получит 'ЗАМЕНИТЬ'.",
            Typical = "ОСТАВИТЬ / ПРОВЕРИТЬ / ЗАМЕНИТЬ"
        });

        if (!string.IsNullOrEmpty(r.WhyVerdict))
        {
            items.Add(new MetricItem { Name = "Обоснование", IsHeader = true });
            items.Add(new MetricItem
            {
                Category = "Итог",
                Name = "Почему?",
                Value = r.WhyVerdict,
                Status = "—",
                StatusColor = "#585b70",
                Description = r.WhyVerdict
            });
        }

        MetricItems = items;
    }

    private static readonly SpectrogramRenderer _spectroRenderer = new();

    public WriteableBitmap? GetOrBuildSpectrogram()
    {
        if (SpectrogramBitmap != null) return SpectrogramBitmap;
        if (_rawSpectro == null || _spectroWidth < 1 || _spectroHeight < 1) return null;

        var bmp = _spectroRenderer.Render(_rawSpectro, _spectroWidth, _spectroHeight);
        SpectrogramBitmap = bmp;
        return bmp;
    }

    public void ClearSpectrogramData()
    {
        _rawSpectro = null;
        SpectrogramBitmap = null;
    }

    [RelayCommand]
    private void CopyMetrics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"══════════════════════════════════════════");
        sb.AppendLine($"  LosslessChecker — Анализ: {FileName}");
        sb.AppendLine($"  {Format} | {SampleRate} Гц | {BitDepth} бит | {Channels} кан.");
        sb.AppendLine($"══════════════════════════════════════════");
        sb.AppendLine();

        foreach (var m in MetricItems)
        {
            if (m.IsHeader)
            {
                sb.AppendLine();
                sb.AppendLine($"▸ {m.Name}");
                sb.AppendLine(new string('─', 60));
                continue;
            }

            sb.AppendLine($"  {m.Name}");
            sb.AppendLine($"    Значение : {m.Value}");
            sb.AppendLine($"    Статус   : {m.Status}");
            sb.AppendLine($"    Нормы    : {m.Typical.Replace("\n", "\n              ")}");
            sb.AppendLine();
        }

        sb.AppendLine("══════════════════════════════════════════");

        try
        {
            System.Windows.Clipboard.SetText(sb.ToString());
        }
        catch { }
    }
}
