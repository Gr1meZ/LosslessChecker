namespace LosslessChecker.Services.Analyzers;

public sealed class KWeightingFilter
{
    private readonly Biquad _stage1;
    private readonly Biquad _stage2;

    public KWeightingFilter(int sampleRate)
    {
        _stage1 = Biquad.HighShelf(1681.97429648, 0.3589044658, 4.0, sampleRate);
        _stage2 = Biquad.HighPass(38.13547, 0.5003, sampleRate);
    }

    public double Process(double sample) => _stage2.Process(_stage1.Process(sample));

    public void Reset()
    {
        _stage1.Reset();
        _stage2.Reset();
    }
}

public sealed class Biquad
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    private Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        _b0 = b0; _b1 = b1; _b2 = b2;
        _a1 = a1; _a2 = a2;
    }

    public static Biquad HighPass(double f0, double q, int fs)
    {
        double w0 = 2.0 * Math.PI * f0 / fs;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2.0 * q);

        double b0 = (1 + cosW0) / 2;
        double b1 = -(1 + cosW0);
        double b2 = (1 + cosW0) / 2;
        double a0 = 1 + alpha;
        double a1 = -2 * cosW0;
        double a2 = 1 - alpha;

        return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    public static Biquad HighShelf(double f0, double q, double gainDb, int fs)
    {
        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2.0 * Math.PI * f0 / fs;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);

        double s = 1.0 / (1.0 + (1.0 / (q * q) - 2.0) / (a + 1.0 / a));
        double alpha = sinW0 / 2 * Math.Sqrt((a + 1.0 / a) * (1.0 / s - 1.0) + 2.0);
        double sqA = Math.Sqrt(a);

        double b0 = a * ((a + 1) + (a - 1) * cosW0 + 2 * sqA * alpha);
        double b1 = -2 * a * ((a - 1) + (a + 1) * cosW0);
        double b2 = a * ((a + 1) + (a - 1) * cosW0 - 2 * sqA * alpha);
        double a0 = (a + 1) - (a - 1) * cosW0 + 2 * sqA * alpha;
        double a1 = 2 * ((a - 1) - (a + 1) * cosW0);
        double a2 = (a + 1) - (a - 1) * cosW0 - 2 * sqA * alpha;

        return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    public double Process(double x)
    {
        double y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    public void Reset() => _z1 = _z2 = 0;
}
