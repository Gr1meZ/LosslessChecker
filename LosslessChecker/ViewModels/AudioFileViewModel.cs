using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LosslessChecker.Models;

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
    [ObservableProperty] private int _qualityScore;
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
    [ObservableProperty] private WriteableBitmap? _spectrogramBitmap;

    // Detail panel: metric items collection
    [ObservableProperty] private ObservableCollection<MetricItem> _metricItems = new();

    public string FilePath { get; }

    private byte[]? _rawSpectro;
    private int _spectroWidth, _spectroHeight;
    private AnalysisResult? _lastResult;

    public AudioFileViewModel(AudioFileInfo fileInfo)
    {
        FilePath = fileInfo.FilePath;
        _fileName = fileInfo.FileName;
    }

    public void ApplyResult(AnalysisResult r)
    {
        _lastResult = r;
        FileName = r.FileName;
        Format = r.Format;
        CutoffFrequency = r.CutoffFrequency;
        DynamicRange = r.DynamicRange;
        SamplePeakDb = r.SamplePeakDb;
        TruePeakDb = r.TruePeakDb;
        ClippingPercent = r.ClippingPercent;
        Authenticity = r.Authenticity;
        QualityScore = r.QualityScore;
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
        EncoderMatch = r.EncoderMatch;

        if (r.SpectrogramFlat is { Length: > 0 })
        {
            _rawSpectro = r.SpectrogramFlat;
            _spectroWidth = r.SpectrogramWidth;
            _spectroHeight = r.SpectrogramHeight;
        }

        BuildMetricItems(r);
    }

    private void BuildMetricItems(AnalysisResult r)
    {
        var items = new ObservableCollection<MetricItem>();
        var isLossless = r.Authenticity == "TRUE LOSSLESS";
        var nyquist = r.SampleRate / 2.0;

        // === Group: Спектральный анализ ===
        items.Add(new MetricItem { Name = "Спектральный анализ", IsHeader = true });

        // Cutoff
        var cutoffRatio = nyquist > 0 ? r.CutoffFrequency / nyquist : 1.0;
        string cutoffStatus = cutoffRatio >= 0.95 ? "✓ Отлично" : cutoffRatio >= 0.85 ? "⚠ Подозрительно" : "✗ Плохо";
        string cutoffColor = cutoffRatio >= 0.95 ? "#2EA043" : cutoffRatio >= 0.85 ? "#D29922" : "#CF222E";
        items.Add(new MetricItem
        {
            Category = "Спектр",
            Name = "Частотный срез (Cutoff)",
            Value = $"{r.CutoffFrequency:F0} Гц ({cutoffRatio * 100:F0}% Найквиста)",
            Status = cutoffStatus,
            StatusColor = cutoffColor,
            Description = "Максимальная частота, выше которой сигнал отсутствует. Настоящий lossless сохраняет полный спектр до частоты Найквиста. Lossy-кодеки (MP3, AAC) обрезают высокие частоты для экономии места.",
            Typical = ">95% Найквиста — отлично\n85–95% — подозрительно (возможен MP3 320)\n<85% — точно сжато (MP3 128-256)"
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
            string shelfColor = r.ShelfType == "Natural" ? "#2EA043" : r.ShelfType == "Filtered" ? "#D29922" : "#CF222E";
            items.Add(new MetricItem
            {
                Category = "Спектр",
                Name = "Характер спада",
                Value = shelfLabel,
                Status = r.ShelfType == "Natural" ? "✓ Хорошо" : r.ShelfType == "Filtered" ? "⚠ Подозрительно" : "✗ Плохо",
                StatusColor = shelfColor,
                Description = "Форма спектра выше частоты среза. Резкая 'кирпичная стена' — признак lossy-кодека. Плавный естественный спад — аналоговая запись или качественный цифровой трансфер.",
                Typical = "Естественный — отлично\nФильтрованный — подозрительно\nКирпичная стена — сжато"
            });
        }

        // Encoder match
        if (r.EncoderMatch != "None" && r.EncoderMatch.Length > 0)
        {
            items.Add(new MetricItem
            {
                Category = "Спектр",
                Name = "Соответствие кодека",
                Value = r.EncoderMatch,
                Status = "⚠ Обнаружено",
                StatusColor = "#D29922",
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

        // === Group: Итоговая оценка ===
        items.Add(new MetricItem { Name = "Итоговая оценка", IsHeader = true });

        // Authenticity
        string authStatus = r.Authenticity switch
        {
            "TRUE LOSSLESS" => "✓ Настоящий lossless",
            "SUSPICIOUS" => "⚠ Подозрительный",
            "FAKE LOSSLESS" => "✗ Фейк (пережат из lossy)",
            "FAKE HI-RES" => "✗ Фейк Hi-Res (апскейл из CD)",
            _ => r.Authenticity
        };
        string authColor = r.Authenticity.StartsWith("TRUE") ? "#2EA043" : r.Authenticity.StartsWith("SUSPICIOUS") ? "#D29922" : "#CF222E";
        items.Add(new MetricItem
        {
            Category = "Итог",
            Name = "Аутентичность",
            Value = authStatus,
            Status = r.Authenticity switch { "TRUE LOSSLESS" => "✓ Подлинный", "SUSPICIOUS" => "⚠ Проверить", _ => "✗ Заменить" },
            StatusColor = authColor,
            Description = "Определяет, является ли файл настоящим lossless (рип с CD/винила/студии) или пережат из lossy-источника (MP3, AAC). Основано на частотном срезе, артефактах, битовой глубине.",
            Typical = "TRUE LOSSLESS — можно оставлять\nSUSPICIOUS — проверьте вручную\nFAKE LOSSLESS — ищите оригинал"
        });

        // Quality
        string qualStatus = r.QualityScore >= 7 ? "✓ Отлично" : r.QualityScore >= 4 ? "⚠ Нормально" : "✗ Плохо";
        string qualColor = r.QualityScore >= 7 ? "#2EA043" : r.QualityScore >= 4 ? "#D29922" : "#CF222E";
        items.Add(new MetricItem
        {
            Category = "Итог",
            Name = "Качество мастеринга",
            Value = $"{r.QualityScore}/10",
            Status = qualStatus,
            StatusColor = qualColor,
            Description = "Оценка качества мастеринга от 1 до 10. Учитывает DR, клиппинг, True Peak, LUFS, DC Offset, фазу. НЕ влияет на аутентичность: даже плохо смастеренный файл может быть настоящим lossless.",
            Typical = "7–10 — отличный мастеринг\n4–6 — средний мастеринг\n1–3 — плохой мастеринг"
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
            Description = "Рекомендация: ОСТАВИТЬ — файл подлинный, качество приемлемо. ПРОВЕРИТЬ — подозрительный, проверьте вручную. ЗАМЕНИТЬ — фейк, ищите оригинал. Настоящий lossless НИКОГДА не получит 'ЗАМЕНИТЬ', даже с плохим мастерингом.",
            Typical = "ОСТАВИТЬ / ПРОВЕРИТЬ / ЗАМЕНИТЬ"
        });

        MetricItems = items;
    }

    public WriteableBitmap? GetOrBuildSpectrogram()
    {
        if (SpectrogramBitmap != null) return SpectrogramBitmap;
        if (_rawSpectro == null || _spectroWidth < 1 || _spectroHeight < 1) return null;

        int w = _spectroWidth, h = _spectroHeight;
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[w * h * 4];

        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                byte dbByte = _rawSpectro[x * h + y];
                double t = dbByte / 255.0;
                int py = h - 1 - y;
                int idx = (py * w + x) * 4;
                var (r, g, b) = HotColormap(t);
                pixels[idx] = b; pixels[idx + 1] = g; pixels[idx + 2] = r; pixels[idx + 3] = 255;
            }
        }

        bmp.Lock();
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        bmp.Unlock();

        SpectrogramBitmap = bmp;
        _rawSpectro = null;
        return bmp;
    }

    private static (byte r, byte g, byte b) HotColormap(double t)
    {
        if (t <= 0) return (0, 0, 0);
        if (t < 0.25) { double s = t / 0.25; return ((byte)(255 * s), 0, 0); }
        if (t < 0.5) { double s = (t - 0.25) / 0.25; return (255, (byte)(255 * s), 0); }
        if (t < 0.85) { double s = (t - 0.5) / 0.35; return (255, (byte)(128 + 127 * s), (byte)(255 * s)); }
        double s2 = (t - 0.85) / 0.15;
        return (255, 255, (byte)(128 + 127 * s2));
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
