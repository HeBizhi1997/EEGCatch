namespace EegAcquisition.Services.SignalProcessing;

/// <summary>
/// Second-order IIR (biquad) filter section.
/// Transfer function: H(z) = (b0 + b1*z^-1 + b2*z^-2) / (1 + a1*z^-1 + a2*z^-2)
///
/// Coefficients are pre-normalized (a0 = 1).
/// Uses Direct Form II Transposed for numerical stability.
/// </summary>
public sealed class BiquadFilter
{
    private readonly double _b0, _b1, _b2;
    private readonly double _a1, _a2;

    // Filter state (Direct Form II Transposed)
    private double _z1, _z2;

    private BiquadFilter(double b0, double b1, double b2, double a1, double a2)
    {
        _b0 = b0; _b1 = b1; _b2 = b2;
        _a1 = a1; _a2 = a2;
    }

    /// <summary>Process a single sample through the filter.</summary>
    public double Process(double input)
    {
        double output = _b0 * input + _z1;
        _z1 = _b1 * input - _a1 * output + _z2;
        _z2 = _b2 * input - _a2 * output;
        return output;
    }

    /// <summary>Process a span of samples in-place.</summary>
    public void Process(Span<double> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = Process(samples[i]);
        }
    }

    /// <summary>Reset the filter state to zero.</summary>
    public void Reset()
    {
        _z1 = 0;
        _z2 = 0;
    }

    /// <summary>
    /// Create a notch (band-reject) filter.
    /// Removes a narrow band centered at <paramref name="centerFreqHz"/>.
    /// </summary>
    /// <param name="sampleRate">Sampling frequency in Hz.</param>
    /// <param name="centerFreqHz">Center frequency to reject.</param>
    /// <param name="q">Quality factor. Higher Q = narrower notch. Typical: 30.</param>
    public static BiquadFilter Notch(double sampleRate, double centerFreqHz, double q = 30.0)
    {
        double w0 = 2.0 * Math.PI * centerFreqHz / sampleRate;
        double alpha = Math.Sin(w0) / (2.0 * q);
        double cosW0 = Math.Cos(w0);

        double a0 = 1.0 + alpha;
        double b0 = 1.0 / a0;
        double b1 = -2.0 * cosW0 / a0;
        double b2 = 1.0 / a0;
        double a1 = -2.0 * cosW0 / a0;
        double a2 = (1.0 - alpha) / a0;

        return new BiquadFilter(b0, b1, b2, a1, a2);
    }

    /// <summary>
    /// Create a second-order Butterworth low-pass filter.
    /// </summary>
    /// <param name="sampleRate">Sampling frequency in Hz.</param>
    /// <param name="cutoffHz">Cutoff frequency (-3 dB) in Hz.</param>
    public static BiquadFilter LowPass(double sampleRate, double cutoffHz)
    {
        double w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        double cosW0 = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2.0 * 0.7071067811865476); // Q = 1/sqrt(2) for Butterworth

        double a0 = 1.0 + alpha;
        double b0 = (1.0 - cosW0) / 2.0 / a0;
        double b1 = (1.0 - cosW0) / a0;
        double b2 = (1.0 - cosW0) / 2.0 / a0;
        double a1 = -2.0 * cosW0 / a0;
        double a2 = (1.0 - alpha) / a0;

        return new BiquadFilter(b0, b1, b2, a1, a2);
    }

    /// <summary>
    /// Create a second-order Butterworth high-pass filter.
    /// </summary>
    /// <param name="sampleRate">Sampling frequency in Hz.</param>
    /// <param name="cutoffHz">Cutoff frequency (-3 dB) in Hz.</param>
    public static BiquadFilter HighPass(double sampleRate, double cutoffHz)
    {
        double w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        double cosW0 = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2.0 * 0.7071067811865476); // Q = 1/sqrt(2) for Butterworth

        double a0 = 1.0 + alpha;
        double b0 = (1.0 + cosW0) / 2.0 / a0;
        double b1 = -(1.0 + cosW0) / a0;
        double b2 = (1.0 + cosW0) / 2.0 / a0;
        double a1 = -2.0 * cosW0 / a0;
        double a2 = (1.0 - alpha) / a0;

        return new BiquadFilter(b0, b1, b2, a1, a2);
    }
}
