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
    [ObservableProperty] private string _claimedType = "";
    [ObservableProperty] private string _detectedType = "";
    [ObservableProperty] private int _bitrateSpectrum;
    [ObservableProperty] private System.Windows.Media.Brush _bitrateSpectrumColor =
        System.Windows.Media.Brushes.Gray;
    [ObservableProperty] private string _fileSizeDisplay = "";
    [ObservableProperty] private System.Windows.Media.Brush _fileSizeColor =
        System.Windows.Media.Brushes.Gray;
    [ObservableProperty] private System.Windows.Media.Brush _detectedTypeColor =
        System.Windows.Media.Brushes.Gray;
    [ObservableProperty] private bool _isAlbumOutlier;

    public int AacBitrate { get; private set; }
    public bool IsAac { get; private set; }
    public int ActualBitrate { get; private set; }
    public double AverageBitrateKbps { get; private set; }

    public string ClaimedBitrate
    {
        get
        {
            var br = Mp3Bitrate > 0 ? Mp3Bitrate : AacBitrate > 0 ? AacBitrate : 0;
            return br > 0 ? $"{br}" : "—";
        }
    }

    public string DisplayBitrate
    {
        get
        {
            if (Mp3Bitrate > 0 || IsAac)
                return ActualBitrate > 0 ? $"{ActualBitrate}" : "—";
            if (ActualBitrate > 0) return $"{ActualBitrate}";
            return "—";
        }
    }

    public string ActualBitrateDisplay => ActualBitrate > 0 ? $"{ActualBitrate}" : "—";


    public string VerdictLabel => DetectedType.Length > 0 ? DetectedType : Decision switch
    {
        "KEEP (Excellent)" => SampleRate >= 88200 && HiResScorePercent >= 70 ? "HI-RES" : "LOSSLESS",
        "KEEP (Good)" => "LOSSLESS",
        "KEEP (Fair)" => "LOSSLESS",
        "INVESTIGATE" => "NOT SURE",
        "REPLACE" => Mp3Bitrate > 0
            ? $"MP3 {(ActualBitrate > 0 ? ActualBitrate : Mp3Bitrate)}"
            : IsAac && AacBitrate > 0
                ? $"AAC {(ActualBitrate > 0 ? ActualBitrate : AacBitrate)}"
                : "REPLACE",
        _ => Decision
    };

    public string VerdictDisplayText => VerdictLabel;

    public int VerdictPriority => Decision switch
    {
        "REPLACE" => 0,
        "MQA (needs decoder)" => 1,
        "INVESTIGATE" => 2,
        "SKIPPED" => 3,
        "KEEP (Fair)" => 4,
        "KEEP (Good)" => 5,
        "KEEP (Excellent)" => 6,
        _ => 3
    };

    // Detail panel: metric items collection
    [ObservableProperty] private ObservableCollection<MetricItem> _metricItems = new();

    public string FilePath { get; }

    private string? _rawSpectroKey;
    private int _spectroWidth, _spectroHeight;
    private static readonly SpectrogramCache _spectroCache = new();
    private static readonly CoverCache _coverCache = new();
    private AnalysisResult? _lastResult;
    internal AnalysisResult? LastResult => _lastResult;
    public float[]? RawSpectrogram => _rawSpectroKey != null && _spectroCache.TryGet(_rawSpectroKey, out var data) ? data : null;
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
        FileName = r.FileName;
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
        if (r.CoverData is { Length: > 0 })
        {
            string coverKey = $"cover_{r.FilePath}";
            if (!_coverCache.TryGet(coverKey, out _))
                _coverCache.Store(coverKey, r.CoverData, 150);
        }
        CoverData = r.CoverData;
        EncoderMatch = r.EncoderMatch;
        Mp3Bitrate = r.Mp3Bitrate;
        AacBitrate = r.AacBitrate;
        IsAac = r.IsAac;
        ActualBitrate = r.ActualBitrate;
        AverageBitrateKbps = r.AverageBitrateKbps;
        ClaimedType = r.ClaimedType;
        DetectedType = r.DetectedType;
        // Bitrate spectrum: real calculated average bitrate in kbps
        BitrateSpectrum = (int)Math.Round(r.AverageBitrateKbps);

        // Color: red if suspicious bitrate, green otherwise
        if (r.IsSuspiciousBitrate)
            BitrateSpectrumColor = GetBrush("SignalRedBrush");
        else if (BitrateSpectrum == 0)
            BitrateSpectrumColor = GetBrush("FgMutedBrush");
        else
            BitrateSpectrumColor = GetBrush("SignalGreenBrush");

        double mbPerMin = r.DurationSeconds > 0
            ? new System.IO.FileInfo(r.FilePath).Length / (1024.0 * 1024.0) / (r.DurationSeconds / 60.0)
            : 0;
        double fileSizeMb = new System.IO.FileInfo(r.FilePath).Length / (1024.0 * 1024.0);
        FileSizeDisplay = $"{fileSizeMb:F1} MB";

        double expectedMbPerMin = 0;
        bool isLossy = r.Mp3Bitrate > 0 || r.AacBitrate > 0;
        bool isLossless = !isLossy;
        int claimedBr = r.Mp3Bitrate > 0 ? r.Mp3Bitrate : r.AacBitrate > 0 ? r.AacBitrate : 0;

        if (isLossy && claimedBr > 0)
            expectedMbPerMin = claimedBr / 8.0 / 1024.0 * 60.0;
        else if (r.SampleRate >= 88200)
            expectedMbPerMin = 20.0;
        else if (r.BitDepth == 24)
            expectedMbPerMin = 15.0;
        else if (r.BitDepth == 16)
            expectedMbPerMin = 8.0;

        bool sizeSuspicious = false;
        if (expectedMbPerMin > 0 && r.DurationSeconds > 0)
            sizeSuspicious = mbPerMin < expectedMbPerMin * 0.4 || mbPerMin > expectedMbPerMin * 2.0;

        if (r.IsSuspiciousBitrate || sizeSuspicious)
            FileSizeColor = GetBrush("SignalRedBrush");
        else if (expectedMbPerMin > 0)
            FileSizeColor = GetBrush("SignalGreenBrush");
        else
            FileSizeColor = GetBrush("FgMutedBrush");

        bool match = string.Equals(r.ClaimedType, r.DetectedType, StringComparison.OrdinalIgnoreCase)
            || (r.DetectedType.StartsWith("LOSSLESS") && (r.ClaimedType == "FLAC" || r.ClaimedType == "ALAC" || r.ClaimedType == "WAV"))
            || (r.DetectedType.StartsWith("HI-RES") && r.ClaimedType.StartsWith("HI-RES"));
        if (r.DetectedType.StartsWith("UNCERTAIN"))
            DetectedTypeColor = GetBrush("SignalAmberBrush");
        else
            DetectedTypeColor = match ? GetBrush("SignalGreenBrush") : GetBrush("SignalRedBrush");

        if (r.SpectrogramDb is { Length: > 0 })
        {
            string cacheKey = $"{r.FilePath}|{r.SpectrogramWidth}|{r.SpectrogramHeight}";
            if (!_spectroCache.TryGet(cacheKey, out _))
                _spectroCache.Store(cacheKey, r.SpectrogramDb);
            _rawSpectroKey = cacheKey;
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
        var nyquist = r.SampleRate / 2.0;

        AddSpectralMetrics(items, r, nyquist);
        AddDynamicAndPeakMetrics(items, r);
        AddTechnicalMetrics(items, r);
        AddContainerMetrics(items, r);
        AddVerdictMetrics(items, r);

        MetricItems = items;
    }

    private static void AddSpectralMetrics(ObservableCollection<MetricItem> items, AnalysisResult r, double nyquist)
    {
        items.Add(new MetricItem { Name = "Спектральный анализ", IsHeader = true });

        bool isHiRes = r.SampleRate >= 88200;
        bool isLossy = r.Mp3Bitrate > 0 || r.AacBitrate > 0 || r.IsAac;

        double codecMax = isLossy ? 20500 : nyquist;
        var cutoffRatio = codecMax > 0 ? r.CutoffFrequency / codecMax : 1.0;
        string ratioLabel = isLossy ? "% от макс. кодека" : "% Найквиста";

        string cutoffStatus;
        string cutoffColor;
        string cutoffTypical;
        if (isHiRes)
        {
            cutoffStatus = r.CutoffFrequency > 22100 ? "✓ Отлично"
                : r.CutoffFrequency > 20000 ? "⚠ Подозрительно"
                : "✗ Плохо";
            cutoffColor = r.CutoffFrequency > 22100 ? "#34d399"
                : r.CutoffFrequency > 20000 ? "#fbbf24"
                : "#f87171";
            cutoffTypical = ">22 кГц — отлично (настоящий Hi-Res)\n20–22 кГц — подозрительно (возможен CD-апскейл)\n<20 кГц — плохо (апскейл из lossy)";
        }
        else if (isLossy)
        {
            cutoffStatus = r.CutoffFrequency >= 20000 ? "✓ MP3 256–320"
                : r.CutoffFrequency >= 18500 ? "⚠ MP3 192"
                : r.CutoffFrequency >= 16000 ? "⚠ MP3 128–160"
                : "✗ MP3 <128";
            cutoffColor = r.CutoffFrequency >= 18500 ? "#34d399"
                : r.CutoffFrequency >= 16000 ? "#fbbf24"
                : "#f87171";
            cutoffTypical = "≥20.0 кГц — MP3 256–320\n18.5–20.0 кГц — MP3 192\n16.0–18.5 кГц — MP3 128–160\n<16.0 кГц — MP3 <128";
        }
        else
        {
            cutoffStatus = cutoffRatio >= 0.95 ? "✓ Отлично" : cutoffRatio >= 0.85 ? "⚠ Подозрительно" : "✗ Плохо";
            cutoffColor = cutoffRatio >= 0.95 ? "#34d399" : cutoffRatio >= 0.85 ? "#fbbf24" : "#f87171";
            cutoffTypical = ">95% Найквиста — отлично\n85–95% — подозрительно (возможен MP3 320)\n<85% — точно сжато (MP3 128-256)";
        }
        items.Add(new MetricItem
        {
            Category = "Спектр",
            Name = "Частотный срез (Cutoff)",
            Value = isHiRes ? $"{r.CutoffFrequency:F0} Гц"
                : $"{r.CutoffFrequency:F0} Гц ({cutoffRatio * 100:F0}{ratioLabel})",
            Status = cutoffStatus,
            StatusColor = cutoffColor,
            Description = "Максимальная частота, выше которой сигнал отсутствует. Настоящий lossless сохраняет полный спектр до частоты Найквиста. Lossy-кодеки (MP3, AAC) обрезают высокие частоты для экономии места.",
            Typical = cutoffTypical
        });

        if (r.ShelfType.Length > 0)
        {
            string shelfLabel = r.ShelfType switch
            {
                "Brickwall" => isLossy ? "Кирпичная стена (норма для MP3/AAC)" : "Кирпичная стена (lossy-кодек)",
                "Filtered" => "Фильтрованный спад",
                "Natural" => "Естественный спад",
                _ => r.ShelfType
            };
            string shelfStatus;
            string shelfColor;
            if (isLossy)
            {
                if (r.ShelfType == "Brickwall")
                    (shelfStatus, shelfColor) = ("✓ Кодек", "#34d399");
                else if (r.ShelfType == "Filtered")
                    (shelfStatus, shelfColor) = ("⚠ Нехарактерно", "#fbbf24");
                else
                    (shelfStatus, shelfColor) = ("⚠ Нехарактерно", "#fbbf24");
            }
            else if (r.ShelfType == "Natural")
                (shelfStatus, shelfColor) = ("✓ Хорошо", "#34d399");
            else if (r.ShelfType == "Filtered" && isHiRes && r.CutoffFrequency > 22100)
                (shelfStatus, shelfColor) = ("✓ Ок", "#34d399");
            else if (r.ShelfType == "Filtered")
                (shelfStatus, shelfColor) = ("⚠ Подозрительно", "#fbbf24");
            else
                (shelfStatus, shelfColor) = ("✗ Плохо", "#f87171");
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

        if (r.EncoderMatch != "None" && r.EncoderMatch.Length > 0)
        {
            bool isHiResMatch = r.EncoderMatch == "None (Hi-Res)";
            string encStatus = isLossy && r.EncoderMatch.StartsWith("MP3") ? "✓ Определён"
                : isHiResMatch ? "✓ Настоящий"
                : r.EncoderMatch != "None" ? "⚠ Обнаружено"
                : "—";
            string encColor = (isLossy && r.EncoderMatch.StartsWith("MP3")) || isHiResMatch ? "#34d399"
                : r.EncoderMatch != "None" ? "#fbbf24"
                : "#6b7280";
            items.Add(new MetricItem
            {
                Category = "Спектр",
                Name = "Соответствие кодека",
                Value = r.EncoderMatch,
                Status = encStatus,
                StatusColor = encColor,
                Description = "Частота среза сопоставлена с известными кодеками. Каждый lossy-кодек имеет характерную частоту среза.",
                Typical = "MP3 128 → 16 кГц\nMP3 256 → 18 кГц\nMP3 320 / AAC → 20 кГц"
            });
        }

        var limitFreq = isLossy ? 20050 : nyquist;
        var limitName = isLossy ? "Макс. частота кодека" : "Теоретический предел (Найквист)";
        var limitDesc = isLossy
            ? "Lossy-кодеки физически не могут сохранить спектр до Найквиста. MP3 320 / AAC 256 обрезают на ~20.05 кГц."
            : "Теорема Котельникова: полезный сигнал = Sample Rate / 2. Для 44.1 кГц предел — 22.05 кГц, для 48 кГц — 24 кГц.";
        var limitTypical = isLossy
            ? "MP3 128 → 16 кГц\nMP3 256 → 19 кГц\nMP3 320 → 20.05 кГц"
            : "44.1 кГц → 22 050 Гц\n48 кГц → 24 000 Гц\n96 кГц → 48 000 Гц";
        items.Add(new MetricItem
        {
            Category = "Спектр",
            Name = limitName,
            Value = $"{limitFreq:F0} Гц",
            Status = "—",
            StatusColor = "#6b7280",
            Description = limitDesc,
            Typical = limitTypical
        });

        string artStatus = r.ArtifactLevel == "None" ? "✓ Чисто" : r.ArtifactLevel == "Weak" ? "⚠ Слабые" : "✗ Обнаружены";
        string artColor = r.ArtifactLevel == "None" ? "#34d399" : r.ArtifactLevel == "Weak" ? "#fbbf24" : "#f87171";
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
    }

    private static void AddDynamicAndPeakMetrics(ObservableCollection<MetricItem> items, AnalysisResult r)
    {
        items.Add(new MetricItem { Name = "Динамический диапазон и громкость", IsHeader = true });

        string drStatus = r.DynamicRange >= 10 ? "✓ Аудиофил" : r.DynamicRange >= 6 ? "✓ Хорошо" : r.DynamicRange >= 3 ? "⚠ Сжато" : "✗ Пережато";
        string drColor = r.DynamicRange >= 6 ? "#34d399" : r.DynamicRange >= 3 ? "#fbbf24" : "#f87171";
        items.Add(new MetricItem
        {
            Category = "Динамика",
            Name = "Динамический диапазон (DR)",
            Value = $"DR{r.DynamicRange:F0}",
            Status = drStatus,
            StatusColor = drColor,
            Description = "Разница между пиковым и средним уровнем громкости (TT DR Meter). Высокий DR — живой, дышащий звук. Низкий DR — не обязательно плохо, зависит от жанра.",
            Typical = "DR12+ — аудиофил (джаз, классика, акустика, винил)\nDR8-11 — золотая середина (рок 80-90х, инди, симфо-метал)\nDR5-7 — плотный звук (современный метал, альт-рок, пост-гранж, поп)\nDR3-4 — кирпичная стена (EDM, экстрим-метал, гиперпоп)"
        });

        string tpStatus = r.TruePeakDb <= 0 ? "✓ Чисто" : r.TruePeakDb <= 1 ? "⚠ Искажения" : "✗ Перегруз";
        string tpColor = r.TruePeakDb <= 0 ? "#34d399" : r.TruePeakDb <= 1 ? "#fbbf24" : "#f87171";
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

        items.Add(new MetricItem
        {
            Category = "Динамика",
            Name = "Sample Peak (цифровой пик)",
            Value = $"{r.SamplePeakDb:F1} dBFS",
            Status = r.SamplePeakDb <= -1.0 ? "✓ Ок" : r.SamplePeakDb < 0 ? "⚠ Потолок" : "✗ Клипп",
            StatusColor = r.SamplePeakDb <= -1.0 ? "#34d399" : r.SamplePeakDb < 0 ? "#fbbf24" : "#f87171",
            Description = "Максимальное значение амплитуды среди цифровых сэмплов. 0 dBFS — цифровой потолок. Идеальный запас: от −0.1 до −1.0 dBFS.",
            Typical = "<0 dBFS — норма\n=0 dBFS — возможен клиппинг"
        });

        string clipStatus = r.ClippingPercent <= 0 ? "✓ Нет" : r.ClippingPercent < 0.5 ? "⚠ Единично" : r.ClippingPercent < 5 ? "⚠ Заметно" : "✗ Сильный";
        string clipColor = r.ClippingPercent <= 0 ? "#34d399" : r.ClippingPercent < 0.5 ? "#fbbf24" : "#f87171";
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

        string lufsStatus = r.IntegratedLufs < -16 ? "✓ Динамично" : r.IntegratedLufs < -11 ? "✓ Норма" : r.IntegratedLufs < -7 ? "⚠ Громко" : "✗ Пережато";
        string lufsColor = r.IntegratedLufs < -11 ? "#34d399" : r.IntegratedLufs < -7 ? "#fbbf24" : "#f87171";
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

        var overallRms = r.OverallRmsDb;
        if (overallRms < 0)
        {
            items.Add(new MetricItem
            {
                Category = "Динамика",
                Name = "Общий RMS (средняя громкость)",
                Value = $"{overallRms:F1} dBFS",
                Status = overallRms > -6 ? "⚠ Громко" : overallRms > -12 ? "✓ Норма" : "✓ Тихо",
                StatusColor = overallRms > -6 ? "#fbbf24" : "#34d399",
                Description = "Среднеквадратичный уровень всего трека. Аналог колонки 'RMS' в foobar2000. Чем ближе к 0 — тем громче.",
                Typical = "−12..−6 dBFS — норма\n>−6 — очень громко\n<−12 — тихо"
            });
        }

        if (r.Plr > 0)
        {
            string plrStatus = r.Plr >= 8 ? "✓ Хорошо" : r.Plr >= 6 ? "⚠ Сжато" : "✗ Пережато";
            string plrColor = r.Plr >= 8 ? "#34d399" : r.Plr >= 6 ? "#fbbf24" : "#f87171";
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
    }

    private static void AddTechnicalMetrics(ObservableCollection<MetricItem> items, AnalysisResult r)
    {
        items.Add(new MetricItem { Name = "Технические параметры", IsHeader = true });

        items.Add(new MetricItem
        {
            Category = "Техника",
            Name = "Битовая глубина",
            Value = $"{r.BitDepth} бит (эфф. {r.EffectiveBitDepth} бит)",
            Status = r.BitDepthSuspicious ? "⚠ Подозрительно" : "✓ Честно",
            StatusColor = r.BitDepthSuspicious ? "#fbbf24" : "#34d399",
            Description = "Соответствие заявленной битности реальной. Часто 16-битный файл сохраняют в 24-битный контейнер с нулями в младших битах — размер растёт, качество нет.",
            Typical = "Совпадает — честно\nМладшие биты нули — фейк"
        });

        if (r.LsbZeroPadded)
        {
            items.Add(new MetricItem
            {
                Category = "Техника",
                Name = "Нулевые младшие биты",
                Value = "Обнаружены",
                Status = "✗ Фейк",
                StatusColor = "#f87171",
                Description = "Младшие 8 бит 24-битного файла всегда равны нулю — это 16-битный файл, сохранённый в 24-битном контейнере.",
                Typical = "Не обнаружены — честный 24 бит\nОбнаружены — фейковый 24 бит"
            });
        }

        bool hasDc = Math.Abs(r.DcOffsetL) > 0.5 || Math.Abs(r.DcOffsetR) > 0.5;
        items.Add(new MetricItem
        {
            Category = "Техника",
            Name = "DC смещение",
            Value = $"L={r.DcOffsetL:F4}% R={r.DcOffsetR:F4}%",
            Status = hasDc ? "⚠ Обнаружено" : "✓ Нет",
            StatusColor = hasDc ? "#fbbf24" : "#34d399",
            Description = "Постоянная составляющая сигнала. Должно быть близко к 0.0000%. Наличие выше 0.5% съедает динамический диапазон и вызывает щелчки.",
            Typical = "0.0000% — норма\n>0.5% — дефект оцифровки"
        });

        string phaseStatus = r.Correlation >= 0 ? "✓ Норма" : "✗ Проблема";
        string phaseColor = r.Correlation >= 0 ? "#34d399" : "#f87171";
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

        if (r.SampleRate >= 88200)
        {
            string upStatus = r.IsUpscale ? "✗ Апскейл" : "✓ Настоящий Hi-Res";
            string upColor = r.IsUpscale ? "#f87171" : "#34d399";
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

        items.Add(new MetricItem
        {
            Category = "Техника",
            Name = "Каналы",
            Value = r.Channels == 1 ? "Моно" : r.Channels == 2 ? "Стерео" : $"{r.Channels} каналов",
            Status = "—",
            StatusColor = "#6b7280",
            Description = "Количество аудиоканалов в файле.",
            Typical = "1 — моно\n2 — стерео"
        });

        if (r.IsFakeStereo)
        {
            items.Add(new MetricItem
            {
                Category = "Техника",
                Name = "Fake Stereo",
                Value = "Моно в стерео-контейнере",
                Status = "⚠ Обнаружено",
                StatusColor = "#fbbf24",
                Description = "Оба канала идентичны (корреляция >0.99, задержка <1 сэмпл). Фактически моно, сохранённое как стерео — занимает вдвое больше места без пользы.",
                Typical = "Не обнаружено — нормальное стерео\nОбнаружено — моно в стерео"
            });
        }

        if (r.HasAbruptEdges)
        {
            items.Add(new MetricItem
            {
                Category = "Техника",
                Name = "Обрезанные края",
                Value = "Обнаружены",
                Status = "⚠ Тишина на границах",
                StatusColor = "#fbbf24",
                Description = "Первые/последние 500 мс трека имеют RMS ниже −60 dBFS при общем RMS выше −30. Вероятно, трек обрезан не по нулевой точке.",
                Typical = "Не обнаружены — нормально\nОбнаружены — возможен щелчок при воспроизведении"
            });
        }

        if (r.ReplayGainMismatch)
        {
            items.Add(new MetricItem
            {
                Category = "Техника",
                Name = "ReplayGain расхождение",
                Value = $"RG: {r.ReplayGainTrackDb:F1} dB vs LUFS: {r.IntegratedLufs:F1}",
                Status = "⚠ Расхождение >3 dB",
                StatusColor = "#fbbf24",
                Description = "Тег REPLAYGAIN_TRACK_GAIN расходится с измеренным Integrated LUFS более чем на 3 dB. Возможно, тег устарел или не соответствует реальной громкости.",
                Typical = "<3 dB — норма\n>3 dB — расхождение"
            });
        }
    }

    private static void AddContainerMetrics(ObservableCollection<MetricItem> items, AnalysisResult r)
    {
        if ((r.AverageBitrateKbps > 0 || r.CompressionRatio > 0) && r.Mp3Bitrate == 0 && r.AacBitrate == 0)
        {
            items.Add(new MetricItem { Name = "Битрейт и сжатие", IsHeader = true });
            if (r.AverageBitrateKbps > 0)
            {
                items.Add(new MetricItem
                {
                    Category = "Битрейт",
                    Name = "Средний битрейт",
                    Value = $"{(int)r.AverageBitrateKbps} kbps",
                    Status = "—",
                    StatusColor = "#6b7280",
                    Description = "Реальный средний битрейт: (размер файла − метаданные) × 8 / длительность.",
                    Typical = "CD FLAC: 700–1000 kbps\nHi-Res FLAC: 2000–5000 kbps\nMP3 320: ~320 kbps\nMP3 128: ~128 kbps"
                });
            }
            if (r.CompressionRatio > 0)
            {
                string compStatus = r.CompressionRatio > 0.95 ? "⚠ Подозрительно" : r.CompressionRatio > 0.7 ? "✓ Норма" : "✓ Эффективно";
                string compColor = r.CompressionRatio > 0.95 ? "#fbbf24" : "#34d399";
                items.Add(new MetricItem
                {
                    Category = "Битрейт",
                    Name = "Степень сжатия",
                    Value = $"{r.CompressionRatio * 100:F0}%",
                    Status = compStatus,
                    StatusColor = compColor,
                    Description = "Размер файла / несжатый размер. FLAC обычно сжимает до 50–70%. >95% — файл почти не сжат (возможно, внутри уже lossy).",
                    Typical = "50–70% — норма для FLAC\n70–90% — слабое сжатие\n>95% — подозрительно"
                });
            }
            if (r.MinFrameBitrateKbps > 0 && r.MaxFrameBitrateKbps > 0)
            {
                items.Add(new MetricItem
                {
                    Category = "Битрейт",
                    Name = "Битрейт по фреймам",
                    Value = $"{r.MinFrameBitrateKbps:F0}–{r.MaxFrameBitrateKbps:F0} kbps",
                    Status = "—",
                    StatusColor = "#6b7280",
                    Description = "Мгновенный битрейт FLAC-фреймов (VBR). Показывает разброс: тихие участки vs громкие.",
                    Typical = "FLAC VBR: битрейт меняется внутри трека в 2–5 раз"
                });
            }
            if (r.IsSuspiciousBitrate)
            {
                items.Add(new MetricItem
                {
                    Category = "Битрейт",
                    Name = "Подозрительный битрейт",
                    Value = "⚠ Обнаружен",
                    Status = "✗ Подозрительно",
                    StatusColor = "#f87171",
                    Description = "Битрейт не соответствует формату: слишком низкий для lossless (возможен транскод) или аномально высокое сжатие.",
                    Typical = "Не обнаружен — норма\nОбнаружен — проверьте источник"
                });
            }
        }

        if (r.Mp3Bitrate > 0)
        {
            items.Add(new MetricItem { Name = "Характеристики MP3", IsHeader = true });
            items.Add(new MetricItem
            {
                Category = "MP3",
                Name = "Битрейт",
                Value = $"{r.Mp3Bitrate} kbps",
                Status = "—",
                StatusColor = "#6b7280",
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
                    StatusColor = "#6b7280",
                    Description = "Идентифицированный MP3-кодер (LAME, FhG, etc)."
                });
            }
            string mp3QualStatus = r.Mp3QualityScore >= 80 ? "✓ Хороший рип" : r.Mp3QualityScore >= 50 ? "⚠ Средний" : "✗ Плохой";
            string mp3QualColor = r.Mp3QualityScore >= 80 ? "#34d399" : r.Mp3QualityScore >= 50 ? "#fbbf24" : "#f87171";
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
    }

    private static void AddVerdictMetrics(ObservableCollection<MetricItem> items, AnalysisResult r)
    {
        items.Add(new MetricItem { Name = "Итоговая оценка", IsHeader = true });

        string losslessStatus = r.LosslessScore >= 85 ? "✓ Отлично" : r.LosslessScore >= 60 ? "⚠ Средне" : "✗ Плохо";
        string losslessColor = r.LosslessScore >= 85 ? "#34d399" : r.LosslessScore >= 60 ? "#fbbf24" : "#f87171";
        string losslessLabel = r.DetectedType.Length > 0 ? r.DetectedType : r.Authenticity;
        items.Add(new MetricItem
        {
            Category = "Итог",
            Name = "Итоговый тип файла",
            Value = losslessLabel,
            Status = losslessStatus,
            StatusColor = losslessColor,
            Description = "Наше определение: какой тип файла перед вами. LOSSLESS (CD) — честный CD-рип. MP3 128/192/256/320 — lossy-файл с этим битрейтом. HI-RES 96k/192k — настоящий высокочастотный файл. UPSCALE — апконверт из низкого качества.",
            Typical = "LOSSLESS (CD) — честный lossless\nMP3 128/320 — lossy-файл\nHI-RES 96k — настоящий Hi-Res\nUPSCALE — фейк"
        });

        if (r.HiResScore > 0 || r.SampleRate >= 88200)
        {
            string hrStatus = r.HiResScore >= 70 ? "✓ Настоящий Hi-Res" : r.HiResScore >= 40 ? "⚠ Сомнительно" : "✗ Апскейл";
            string hrColor = r.HiResScore >= 70 ? "#34d399" : r.HiResScore >= 40 ? "#fbbf24" : "#f87171";
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

        items.Add(new MetricItem
        {
            Category = "Итог",
            Name = "Охват метрик",
            Value = $"{r.MetricsCoverage:F0}%",
            Status = r.MetricsCoverage >= 80 ? "✓ Хорошо" : r.MetricsCoverage >= 60 ? "⚠ Средне" : "✗ Мало",
            StatusColor = r.MetricsCoverage >= 80 ? "#34d399" : r.MetricsCoverage >= 60 ? "#fbbf24" : "#f87171",
            Description = "Процент метрик, прошедших пороговые значения. Показывает, насколько 'чист' файл по всем проверкам. 100% — все метрики в норме.",
            Typical = "80–100% — отлично\n60–79% — есть замечания\n<60% — много проблем"
        });

        string qualStatus = r.QualityScorePercent >= 70 ? "✓ Отлично" : r.QualityScorePercent >= 40 ? "⚠ Нормально" : "✗ Плохо";
        string qualColor = r.QualityScorePercent >= 70 ? "#34d399" : r.QualityScorePercent >= 40 ? "#fbbf24" : "#f87171";
        items.Add(new MetricItem
        {
            Category = "Итог",
            Name = "Качество мастеринга",
            Value = $"{r.QualityScorePercent:F0}%",
            Status = qualStatus,
            StatusColor = qualColor,
            Description = "Взвешенная оценка качества мастеринга (0–100%). Учитывает клиппинг, True Peak, LUFS, DC Offset, фазу.",
            Typical = "70–100% — отличный мастеринг\n40–69% — средний мастеринг\n<40% — плохой мастеринг"
        });

        string decColor = r.Decision.StartsWith("KEEP") ? "#34d399" : r.Decision == "INVESTIGATE" ? "#fbbf24" : "#f87171";
        string decText = r.Decision switch
        {
            "KEEP (Excellent)" => "ОСТАВИТЬ (отлично)",
            "KEEP (Good)" => "ОСТАВИТЬ (хорошо)",
            "KEEP (Fair)" => "ОСТАВИТЬ (приемлемо)",
            "INVESTIGATE" => "ПРОВЕРИТЬ",
            "REPLACE" => "ЗАМЕНИТЬ",
            "MQA (needs decoder)" => "MQA (нужен декодер)",
            "CORRUPTED" => "ПОВРЕЖДЁН",
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
                StatusColor = "#6b7280",
                Description = r.WhyVerdict
            });
        }
    }

    private static readonly SpectrogramRenderer _spectroRenderer = new();

    public WriteableBitmap? GetOrBuildSpectrogram()
    {
        if (SpectrogramBitmap != null) return SpectrogramBitmap;
        if (_rawSpectroKey == null) return null;
        if (!_spectroCache.TryGet(_rawSpectroKey, out var rawSpectro) || rawSpectro == null)
        {
            _rawSpectroKey = null;
            return null;
        }

        var bmp = _spectroRenderer.Render(rawSpectro, _spectroWidth, _spectroHeight);
        SpectrogramBitmap = bmp;
        return bmp;
    }

    public void ClearSpectrogramData()
    {
        _rawSpectroKey = null;
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

    private static System.Windows.Media.Brush GetBrush(string key)
    {
        return System.Windows.Application.Current.TryFindResource(key) as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.Gray;
    }
}
