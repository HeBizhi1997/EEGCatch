namespace EegAcquisition.Services.SignalProcessing;

/// <summary>
/// 基于 Cooley-Tukey 基-2 迭代 FFT 的频谱计算工具。
/// </summary>
public static class FftHelper
{
    /// <summary>
    /// 计算实信号的单侧幅度频谱（线性幅值，归一化）。
    /// </summary>
    /// <param name="samples">输入时域信号，长度必须是 2 的幂次。</param>
    /// <param name="mags">
    ///   输出幅度数组，长度 = samples.Length/2 + 1，对应频率
    ///   f[i] = i * sampleRate / samples.Length（Hz）。
    /// </param>
    /// <param name="re">与 samples 等长的实部工作缓冲区（调用方复用，避免重复分配）。</param>
    /// <param name="im">与 samples 等长的虚部工作缓冲区。</param>
    public static void ComputeMagnitudes(
        ReadOnlySpan<double> samples,
        double[] mags,
        double[] re,
        double[] im)
    {
        int n = samples.Length;

        // 施加 Hanning 窗，拷贝到工作缓冲区
        for (int i = 0; i < n; i++)
        {
            double w = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (n - 1)));
            re[i] = samples[i] * w;
            im[i] = 0.0;
        }

        FftInPlace(re, im, n);

        // 单侧幅度，归一化到信号幅值量纲
        int half = n / 2;
        for (int i = 0; i <= half; i++)
        {
            double mag = Math.Sqrt(re[i] * re[i] + im[i] * im[i]) / half;
            mags[i] = mag;
        }
    }

    // ── Cooley-Tukey 基-2 迭代 FFT ────────────────────────────────────

    private static void FftInPlace(double[] re, double[] im, int n)
    {
        // 位反转置换（bit-reversal permutation）
        int j = 0;
        for (int i = 1; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        // 蝶形运算（butterfly）
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            double wRe = Math.Cos(ang);
            double wIm = Math.Sin(ang);
            int half = len >> 1;

            for (int i = 0; i < n; i += len)
            {
                double curRe = 1.0, curIm = 0.0;
                for (int k = 0; k < half; k++)
                {
                    double uRe = re[i + k];
                    double uIm = im[i + k];
                    double vRe = re[i + k + half] * curRe - im[i + k + half] * curIm;
                    double vIm = re[i + k + half] * curIm + im[i + k + half] * curRe;

                    re[i + k]        = uRe + vRe;
                    im[i + k]        = uIm + vIm;
                    re[i + k + half] = uRe - vRe;
                    im[i + k + half] = uIm - vIm;

                    double nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }
}
