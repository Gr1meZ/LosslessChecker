using System.Text;
using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class VerdictGenerator
{
    public string Generate(AnalysisResult r)
    {
        var sb = new StringBuilder();

        // Section 1
        string authRu = r.Authenticity switch
        {
            "TRUE" => "TRUE LOSSLESS",
            "UNCERTAIN" => "UNCERTAIN",
            "FALSE" => "FALSE LOSSLESS",
            "LOSSY (MP3)" => "LOSSY (MP3)",
            _ => r.Authenticity
        };
        sb.Append("1. СТАТУС: ").Append(authRu);
        sb.Append(" | срез на ").Append($"{r.CutoffFrequency:F0} Гц");
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
        sb.AppendLine(flags.Count > 0 ? "   - " + string.Join("\n   - ", flags) : "Нет");

        // Section 5
        sb.Append("5. ИТОГОВЫЙ ВЕРДИКТ: ");
        sb.Append($"{r.QualityScorePercent:F0}%");
        sb.Append(" | ");
        string decRu = r.Decision switch
        {
            "KEEP" => "ОСТАВИТЬ",
            "KEEP (poor master)" => "ОСТАВИТЬ (плохой мастеринг)",
            "INVESTIGATE" => "ПРОВЕРИТЬ",
            "REPLACE" => "ЗАМЕНИТЬ",
            _ => r.Decision
        };
        sb.Append(decRu);
        if (r.QualityScorePercent >= 70 && r.Authenticity == "TRUE")
            sb.Append(" — Отличный мастеринг, подлинный lossless");
        else if (r.Authenticity == "TRUE" && r.QualityScorePercent < 40)
            sb.Append(" — Подлинный, но плохо смастеренный");
        else if (r.Authenticity == "FALSE")
            sb.Append(" — Не подлинный, ищите оригинал");
        else if (r.Authenticity == "LOSSY (MP3)")
            sb.Append(" — Lossy-формат, оценка по качеству MP3");

        return sb.ToString();
    }

    public string GenerateWhy(AnalysisResult r)
    {
        var sb = new StringBuilder();
        var nyquist = r.SampleRate / 2.0;
        double ratio = nyquist > 0 ? r.CutoffFrequency / nyquist : 1.0;

        sb.Append("Срез ").Append($"{r.CutoffFrequency:F0} Гц");
        if (r.SampleRate < 88200)
            sb.Append($" ({ratio * 100:F0}% Найквиста)");
        if (r.ShelfType == "Brickwall") sb.Append(" — кирпичная стена, признак lossy-кодека");
        else if (r.ShelfType == "Filtered") sb.Append(" — фильтрованный спад");
        else sb.Append(" — естественный спад");

        sb.Append(". DR").Append($"{r.DynamicRange:F0}");
        if (r.DynamicRange >= 10) sb.Append(" — аудиофильская динамика");
        else if (r.DynamicRange >= 6) sb.Append(" — хорошая динамика");
        else if (r.DynamicRange >= 3) sb.Append(" — сжато");
        else sb.Append(" — пережато");

        if (r.HasArtifacts) sb.Append(". Артефакты: ").Append(r.ArtifactLevel);
        else sb.Append(". Артефакты не обнаружены");

        if (r.Authenticity == "TRUE") sb.Append(". Подлинный lossless.");
        else if (r.Authenticity == "UNCERTAIN") sb.Append(". Подозрительный — проверьте источник.");
        else if (r.Authenticity == "LOSSY (MP3)") sb.Append(". Lossy-формат (MP3).");
        else sb.Append(". Фейк — пережат из lossy.");

        return sb.ToString();
    }
}
