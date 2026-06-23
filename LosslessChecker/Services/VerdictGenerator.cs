using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class VerdictGenerator
{
    public string Generate(AnalysisResult result)
    {
        var lines = new List<string>();
        var nyquist = result.SampleRate / 2.0;
        double cutoffRatio = nyquist > 0 ? result.CutoffFrequency / nyquist : 1.0;

        // Cutoff analysis
        if (cutoffRatio >= 0.90)
            lines.Add($"Frequency response extends to {result.CutoffFrequency:F0} Hz ({cutoffRatio * 100:F0}% of Nyquist) — full spectrum.");
        else if (cutoffRatio >= 0.82)
            lines.Add($"Gentle high-frequency rolloff at {result.CutoffFrequency:F0} Hz ({cutoffRatio * 100:F0}% Nyquist) — natural analog or mild filtering.");
        else if (cutoffRatio >= 0.72)
            lines.Add($"Moderate frequency cutoff at {result.CutoffFrequency:F0} Hz ({cutoffRatio * 100:F0}% Nyquist).");

        if (cutoffRatio < 0.72)
        {
            if (cutoffRatio >= 0.60)
                lines.Add($"Significant brickwall cutoff at {result.CutoffFrequency:F0} Hz — likely transcoded from 192-256 kbps lossy source.");
            else if (cutoffRatio >= 0.45)
                lines.Add($"Severe brickwall cutoff at {result.CutoffFrequency:F0} Hz — characteristic of 128-160 kbps MP3/AAC transcode.");
            else
                lines.Add($"Extreme lowpass at {result.CutoffFrequency:F0} Hz — very low-bitrate lossy source.");
        }

        // Artifact analysis
        if (result.ArtifactLevel != "None")
        {
            string desc = result.ArtifactLevel switch
            {
                "Strong" => "Strong block-boundary artifacts and spectral flatness above cutoff — definitive lossy encoder signature.",
                "Medium" => "Medium encoder artifacts: shelf-like noise floor and spectral structure typical of perceptual codec.",
                "Weak" => "Weak artifacts: minor spectral anomalies above cutoff that may indicate prior lossy encoding.",
                _ => ""
            };
            if (desc.Length > 0) lines.Add(desc);
        }

        // Dynamic Range
        if (result.DynamicRange >= 12)
            lines.Add($"Excellent dynamic range: DR{result.DynamicRange:F0}. Full dynamics preserved.");
        else if (result.DynamicRange >= 9)
            lines.Add($"Good dynamic range: DR{result.DynamicRange:F0}. Healthy headroom.");
        else if (result.DynamicRange >= 7)
            lines.Add($"Average dynamic range: DR{result.DynamicRange:F0}. Typical of modern mastering.");
        else if (result.DynamicRange >= 5)
            lines.Add($"Below-average dynamic range: DR{result.DynamicRange:F0}. Compressed mastering — check for clipping.");
        else
            lines.Add($"Very low dynamic range: DR{result.DynamicRange:F0}. Heavy compression/limiting (loudness war).");

        // Clipping
        if (result.ClippingPercent > 5)
        {
            lines.Add($"Severe clipping: {result.ClippingPercent:F1}% samples at 0 dBFS. Brickwall-limited. Audible distortion likely on resolving equipment.");
        }
        else if (result.ClippingPercent > 1)
        {
            lines.Add($"Noticeable clipping: {result.ClippingPercent:F1}% samples at ceiling. Intersample peaks may distort on playback.");
        }
        else if (result.ClippingPercent > 0)
        {
            lines.Add($"Minor clipping: {result.ClippingPercent:F2}% samples. Negligible.");
        }

        // Bit depth
        // if (result.BitDepthSuspicious)
        // {
        //     lines.Add(result.BitDepthVerdict);
        // }

        // Upscale
        // if (result.IsUpscale)
        // {
        //     lines.Add(result.UpscaleVerdict);
        // }

        // Material quality assessment
            // if (result.TruePeakDb > 0)
            //     lines.Add($"True Peak at +{result.TruePeakDb:F1} dBTP — intersample peaks exceed 0 dBFS. May clip on some DACs.");

        return string.Join(" ", lines);
    }
}
