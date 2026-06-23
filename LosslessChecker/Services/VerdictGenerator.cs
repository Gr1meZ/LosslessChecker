using System.Text;
using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class VerdictGenerator
{
    public string Generate(AnalysisResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.FileName);
        sb.AppendLine();

        sb.Append("1. LOSSLESS STATUS: ");
        sb.Append(r.Authenticity);
        sb.Append(" | ");
        sb.Append($"cutoff at {r.CutoffFrequency:F0} Hz");
        if (r.ShelfType.Length > 0) sb.Append($", {r.ShelfType.ToLower()} rolloff");
        if (r.EncoderMatch != "None") sb.Append($", matches {r.EncoderMatch}");
        sb.AppendLine();
        sb.AppendLine();

        sb.Append("2. CLIPPING & PEAK: ");
        if (r.HasIsp || r.ClippingPercent > 0)
            sb.Append("CLIPPED | ");
        else if (r.TruePeakDb > -0.5)
            sb.Append("HOT | ");
        else
            sb.Append("CLEAN | ");
        sb.Append($"Sample Peak {r.SamplePeakDb:F1} dBFS, True Peak {r.TruePeakDb:F1} dBTP");
        if (r.HasIsp) sb.Append(", ISP DISTORTION");
        sb.AppendLine();
        sb.AppendLine();

        sb.Append("3. DYNAMICS: ");
        if (r.DynamicRange >= 13) sb.Append("AUDIOPHILE");
        else if (r.DynamicRange >= 9) sb.Append("GOOD");
        else if (r.DynamicRange >= 6) sb.Append("COMPRESSED");
        else sb.Append("CATASTROPHIC");
        sb.Append($" | DR{r.DynamicRange:F0}");
        if (r.IntegratedLufs < -1)
            sb.Append($", Integrated {r.IntegratedLufs:F1} LUFS");
        if (r.Plr > 0)
            sb.Append($", PLR {r.Plr:F1} dB");
        sb.AppendLine();
        sb.AppendLine();

        sb.Append("4. TECHNICAL RED FLAGS: ");
        var flags = new List<string>();
        if (Math.Abs(r.DcOffsetL) > 0.001 || Math.Abs(r.DcOffsetR) > 0.001)
            flags.Add($"DC Offset: L={r.DcOffsetL:F4}%, R={r.DcOffsetR:F4}%");
        if (r.Correlation < 0)
            flags.Add($"Phase correlation: {r.Correlation:F2} (mono incompatible)");
        if (r.LsbZeroPadded)
            flags.Add($"24-bit file has zero-padded LSBs (effective {r.EffectiveBitDepth}-bit)");
        if (r.BitDepthSuspicious)
            flags.Add($"Bit depth suspicious");
        if (r.IsUpscale)
            flags.Add($"Hi-Res upscale suspected (max HF {r.MaxHfDb:F0} dB)");
        sb.AppendLine(flags.Count > 0 ? "   - " + string.Join("\n   - ", flags) : "None");
        sb.AppendLine();

        sb.Append("5. OVERALL VERDICT: ");
        sb.Append($"{r.QualityScore}/10");
        sb.Append(" | ");
        sb.Append(r.Decision);
        if (r.QualityScore >= 7 && r.Authenticity == "TRUE LOSSLESS")
            sb.Append(" — Excellent, genuine lossless");
        else if (r.Authenticity == "TRUE LOSSLESS" && r.QualityScore < 4)
            sb.Append(" — Genuine but poorly mastered");
        else if (r.Authenticity.StartsWith("FAKE"))
            sb.Append(" — Not genuine, find original source");

        return sb.ToString();
    }
}
