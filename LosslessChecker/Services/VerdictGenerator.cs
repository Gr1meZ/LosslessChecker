using System.Text;
using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class VerdictGenerator
{
    public string Generate(AnalysisResult r)
    {
        var sb = new StringBuilder();
        var nyquist = r.SampleRate / 2.0;
        double ratio = nyquist > 0 ? r.CutoffFrequency / nyquist : 1.0;

        // Section 1
        string authRu = ResolveAuthenticityLabel(r);
        sb.Append("1. СТАТУС: ").Append(authRu);
        sb.Append(" | срез на ").Append($"{r.CutoffFrequency:F0} Гц");
        if (!r.IsMqa && !r.IsHdcd)
            sb.Append($" ({ratio * 100:F0}% Найквиста)");
        if (r.ShelfType.Length > 0)
        {
            string shelf = r.ShelfType switch
            {
                "Brickwall" => "кирпичная стена",
                "Filtered" => "фильтрованный спад",
                "Natural" => "естественный спад",
                _ => r.ShelfType.ToLower()
            };
            sb.Append(", ").Append(shelf);
        }
        if (r.EncoderMatch != "None" && r.EncoderMatch.Length > 0)
            sb.Append(", совпадает с ").Append(r.EncoderMatch);
        if (r.IsMqa) sb.Append(", MQA");
        if (r.IsHdcd) sb.Append(", HDCD");
        sb.AppendLine();

        // Section 2
        sb.Append("2. ПИКИ И КЛИППИНГ: ");
        if (r.HasIsp || r.ClippingPercent > 0)
            sb.Append("КЛИППИНГ | ");
        else if (r.TruePeakDb > -0.5)
            sb.Append("ГОРЯЧИЙ СИГНАЛ | ");
        else
            sb.Append("ЧИСТО | ");
        sb.Append($"Sample Peak {r.SamplePeakDb:F1} dBFS, True Peak {r.TruePeakDb:F1} dBTP");
        if (r.HasIsp) sb.Append(", МЕЖСЭМПЛОВЫЕ ИСКАЖЕНИЯ");
        sb.AppendLine();

        // Section 3
        sb.Append("3. ДИНАМИКА: ");
        if (r.DynamicRange >= 10) sb.Append("АУДИОФИЛ");
        else if (r.DynamicRange >= 6) sb.Append("ХОРОШО");
        else if (r.DynamicRange >= 3) sb.Append("СЖАТО");
        else sb.Append("ПЕРЕЖАТО");
        sb.Append($" | DR{r.DynamicRange:F0}");
        if (r.IntegratedLufs < -1)
            sb.Append($", LUFS {r.IntegratedLufs:F1}");
        if (r.Plr > 0)
            sb.Append($", PLR {r.Plr:F1} дБ");
        sb.AppendLine();

        // Section 4
        sb.Append("4. ТЕХНИЧЕСКИЕ ПРОБЛЕМЫ: ");
        var flags = new List<string>();
        if (Math.Abs(r.DcOffsetL) > 0.01 || Math.Abs(r.DcOffsetR) > 0.01)
            flags.Add($"DC смещение: L={r.DcOffsetL:F4}%, R={r.DcOffsetR:F4}%");
        if (r.Correlation < 0)
            flags.Add($"Корреляция фазы: {r.Correlation:F2} (несовместимо с моно)");
        if (r.LsbZeroPadded)
            flags.Add($"24-бит с нулевыми младшими битами (эфф. {r.EffectiveBitDepth}-бит)");
        if (r.BitDepthSuspicious)
            flags.Add($"Битовая глубина подозрительна");
        if (r.IsUpscale)
            flags.Add($"Hi-Res апскейл (макс ВЧ {r.MaxHfDb:F0} дБ)");
        if (r.IsFakeStereo)
            flags.Add("Fake Stereo (моно в стерео-контейнере)");
        if (r.HasAbruptEdges)
            flags.Add("Обрезанные края (тишина на границах)");
        if (r.ReplayGainMismatch)
            flags.Add($"ReplayGain расхождение ({r.ReplayGainTrackDb:F1} dB vs LUFS {r.IntegratedLufs:F1})");
        sb.AppendLine(flags.Count > 0 ? "   - " + string.Join("\n   - ", flags) : "Нет");

        // Section 5
        sb.Append("5. ИТОГОВЫЙ ВЕРДИКТ: ");
        sb.Append($"{r.AuthenticityScore:F0}%");
        sb.Append(" | ");
        string decRu = r.Decision switch
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
        sb.Append(decRu);

        string summary = r.Authenticity switch
        {
            "TRUE" => r.QualityScorePercent >= 70
                ? " — Отличный мастеринг, подлинный lossless"
                : " — Подлинный, но плохо смастеренный",
            "UNCERTAIN" => " — Подозрительный, проверьте источник",
            "FALSE" => " — Не подлинный, ищите оригинал в lossless",
            string s when s.StartsWith("LOSSY (MP3)") => " — Lossy-формат MP3, оценка по качеству рипа",
            string s when s.StartsWith("LOSSY (AAC)") => " — Lossy-формат AAC, оценка по качеству рипа",
            string s when s.StartsWith("TRANSCODE") => " — Транскод из lossy в lossless-контейнер",
            "MQA" => " — MQA-контейнер, требуется MQA-декодер",
            string s when s.EndsWith("[HDCD]") => " — HDCD-кодирование в lossless-контейнере",
            "CORRUPTED" => " — Файл повреждён (битовые ошибки)",
            _ => ""
        };
        sb.Append(summary);

        return sb.ToString();
    }

    public string GenerateWhy(AnalysisResult r)
    {
        var sb = new StringBuilder();
        var nyquist = r.SampleRate / 2.0;
        double ratio = nyquist > 0 ? r.CutoffFrequency / nyquist : 1.0;
        bool isHiRes = r.SampleRate >= 88200;

        sb.Append("Срез ").Append($"{r.CutoffFrequency:F0} Гц");
        if (r.SampleRate < 88200)
            sb.Append($" ({ratio * 100:F0}% Найквиста)");

        if (r.ShelfType == "Brickwall")
        {
            if (isHiRes)
                sb.Append(" — кирпичная стена на Hi-Res, признак апскейла");
            else if (ratio >= 0.95)
                sb.Append(" — кирпичная стена вблизи Найквиста, вероятно ADC-фильтр");
            else
                sb.Append(" — кирпичная стена, признак lossy-кодека");
        }
        else if (r.ShelfType == "Filtered")
        {
            if (ratio >= 0.95)
                sb.Append(" — фильтрованный спад вблизи Найквиста (LP-фильтр мастеринга, не кодек)");
            else if (ratio >= 0.85)
                sb.Append(" — фильтрованный спад, возможен мягкий LP-фильтр");
            else
                sb.Append(" — фильтрованный спад, подозрительно низкий");
        }
        else
        {
            sb.Append(" — естественный спад");
        }

        sb.Append(". DR").Append($"{r.DynamicRange:F0}");
        if (r.DynamicRange >= 10) sb.Append(" — аудиофильская динамика");
        else if (r.DynamicRange >= 6) sb.Append(" — хорошая динамика");
        else if (r.DynamicRange >= 3) sb.Append(" — сжато");
        else sb.Append(" — пережато");

        if (r.HasArtifacts) sb.Append(". Артефакты: ").Append(r.ArtifactLevel);
        else sb.Append(". Артефакты не обнаружены");

        if (r.IsFakeStereo) sb.Append(". Моно в стерео-контейнере");
        if (r.HasAbruptEdges) sb.Append(". Обрезанные края");
        if (r.ReplayGainMismatch) sb.Append(". Расхождение ReplayGain");

        int br = r.AverageBitrateKbps > 0 ? (int)r.AverageBitrateKbps : r.ActualBitrate;
        if (br > 0)
        {
            sb.Append(". Битрейт: ").Append(br).Append(" kbps");
            if (r.CompressionRatio > 0)
                sb.Append(" (сжатие ").Append($"{r.CompressionRatio * 100:F0}%").Append(")");
        }

        if (r.IsSuspiciousBitrate)
            sb.Append(". ПОДОЗРИТЕЛЬНЫЙ БИТРЕЙТ!");

        if (r.IsMqa) sb.Append(". MQA-контейнер, требуется специальный декодер");
        if (r.IsHdcd) sb.Append(". HDCD-кодирование, может потребовать HDCD-декодер");

        sb.Append(". ").Append(ResolveWhy(r));

        return sb.ToString();
    }

    private static string ResolveAuthenticityLabel(AnalysisResult r)
    {
        var label = r.Authenticity switch
        {
            "TRUE" => "TRUE LOSSLESS",
            "UNCERTAIN" => "UNCERTAIN",
            "FALSE" => "FALSE LOSSLESS",
            "LOSSY (MP3)" => "LOSSY (MP3)",
            "LOSSY (AAC)" => "LOSSY (AAC)",
            string s when s.StartsWith("TRANSCODE") => s,
            "MQA" => "MQA CONTAINER",
            "CORRUPTED" => "CORRUPTED",
            _ => r.Authenticity
        };
        if (r.IsHdcd && !label.Contains("HDCD"))
            label += " [HDCD]";
        return label;
    }

    private static string ResolveWhy(AnalysisResult r)
    {
        return r.Authenticity switch
        {
            "TRUE" => "Подлинный lossless.",
            "UNCERTAIN" => "Подозрительный — проверьте источник.",
            "FALSE" => "Не подлинный — ищите оригинал в lossless.",
            string s when s.StartsWith("LOSSY (MP3)") => "Lossy-формат MP3.",
            string s when s.StartsWith("LOSSY (AAC)") => "Lossy-формат AAC.",
            string s when s.StartsWith("TRANSCODE") => "Транскод из lossy в lossless-контейнер.",
            "MQA" => "MQA-контейнер, требуется специальный декодер.",
            "CORRUPTED" => "Файл повреждён — битовые ошибки в потоке.",
            _ => "Статус не определён."
        };
    }
}
