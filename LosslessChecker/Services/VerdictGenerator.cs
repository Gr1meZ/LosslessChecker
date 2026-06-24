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
            "TRUE LOSSLESS" => "НАСТОЯЩИЙ LOSSLESS",
            "SUSPICIOUS" => "ПОДОЗРИТЕЛЬНЫЙ",
            "FAKE LOSSLESS" => "ФЕЙК (ПЕРЕЖАТ ИЗ LOSSY)",
            "FAKE HI-RES" => "ФЕЙК HI-RES (АПСКЕЙЛ ИЗ CD)",
            _ => r.Authenticity
        };
        sb.Append("1. СТАТУС LOSSLESS: ").Append(authRu);
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
        if (r.DynamicRange >= 13) sb.Append("АУДИОФИЛ");
        else if (r.DynamicRange >= 9) sb.Append("ХОРОШО");
        else if (r.DynamicRange >= 6) sb.Append("СЖАТО");
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
        sb.Append($"{r.QualityScore}/10");
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
        if (r.QualityScore >= 7 && r.Authenticity == "TRUE LOSSLESS")
            sb.Append(" — Отличный мастеринг, подлинный lossless");
        else if (r.Authenticity == "TRUE LOSSLESS" && r.QualityScore < 4)
            sb.Append(" — Подлинный, но плохо смастеренный");
        else if (r.Authenticity.StartsWith("FAKE"))
            sb.Append(" — Не подлинный, ищите оригинал");

        return sb.ToString();
    }
}
