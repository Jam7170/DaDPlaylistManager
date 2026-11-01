namespace DaDPlaylistManager.Core.Audio;

/// <summary>
/// Estimates tempo and first-beat offset from mono PCM using a spectral-flux onset envelope,
/// autocorrelation for the coarse tempo, and a comb/phase search to refine it. It is meant as
/// a starting point; the game's own BPM calibrator is the place to fine-tune.
/// </summary>
public static class BpmDetector
{
    public sealed record Result(double Bpm, int BeatOffsetMs, double Confidence);

    private const int FrameSize = 1024;
    private const int Hop = 256;

    public static Result Detect(float[] samples, int sampleRate, double minBpm = 70, double maxBpm = 200)
    {
        double fps = (double)sampleRate / Hop;
        var (env, low) = OnsetEnvelopes(samples, sampleRate, fps);
        if (env.Length < fps * 5) throw new ArgumentException("Audio is too short to estimate a tempo.");

        double coarse = CoarseTempo(env, fps, minBpm, maxBpm);

        // Refine: coarse step over +-4%, then fine step around the best.
        var (bpm, phase, score) = BestComb(env, fps, coarse * 0.96, coarse * 1.04, 0.05);
        (bpm, phase, score) = BestComb(env, fps, bpm - 0.06, bpm + 0.06, 0.005);

        // Most songs sit on a whole-number tempo; prefer it when it scores almost as well.
        double rounded = Math.Round(bpm);
        if (Math.Abs(bpm - rounded) < 0.15)
        {
            var (_, rPhase, rScore) = BestComb(env, fps, rounded, rounded, 1);
            if (rScore >= score * 0.985) (bpm, phase, score) = (rounded, rPhase, rScore);
        }
        else
        {
            bpm = Math.Round(bpm, 2);
        }

        // Broadband onsets often lock onto off-beat hi-hats. Kicks land on the beat, so let the
        // low-frequency envelope decide between the found phase and the half-beat opposite it.
        double beatPeriod = fps * 60 / bpm;
        double opposite = (phase + beatPeriod / 2) % beatPeriod;
        if (CombScore(low, phase, beatPeriod) < CombScore(low, opposite, beatPeriod) * 0.9) phase = opposite;

        // Convert the envelope phase to seconds. Spectral flux at frame f reflects the change
        // between frames f-1 and f, i.e. roughly at the centre of frame f minus half a hop.
        double seconds = (phase * Hop + FrameSize / 2.0 - Hop / 2.0) / sampleRate;
        double periodMs = 60000.0 / bpm;
        int offsetMs = (int)Math.Round(seconds * 1000.0 % periodMs);

        double mean = env.Average();
        double confidence = mean > 0 ? score / mean : 0;
        return new Result(bpm, offsetMs, confidence);
    }

    /// <summary>Returns the broadband onset envelope and a low-band (kick) envelope.</summary>
    private static (double[] Full, double[] Low) OnsetEnvelopes(float[] samples, int sampleRate, double fps)
    {
        int frames = Math.Max(0, (samples.Length - FrameSize) / Hop);
        var full = new double[frames];
        var low = new double[frames];
        var window = new double[FrameSize];
        for (int i = 0; i < FrameSize; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1));

        int bins = FrameSize / 2;
        int lowBins = Math.Max(2, (int)(150.0 * FrameSize / sampleRate));
        var re = new double[FrameSize];
        var im = new double[FrameSize];
        var prev = new double[bins];
        var cur = new double[bins];

        for (int f = 0; f < frames; f++)
        {
            int start = f * Hop;
            for (int i = 0; i < FrameSize; i++)
            {
                re[i] = samples[start + i] * window[i];
                im[i] = 0;
            }
            Fft(re, im);
            double flux = 0, lowFlux = 0;
            for (int k = 1; k < bins; k++)
            {
                cur[k] = Math.Log(1 + 1000 * Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
                double d = cur[k] - prev[k];
                if (d > 0)
                {
                    flux += d;
                    if (k <= lowBins) lowFlux += d;
                }
            }
            (prev, cur) = (cur, prev);
            if (f > 0)
            {
                full[f] = flux;
                low[f] = lowFlux;
            }
        }

        int half = Math.Max(1, (int)(fps * 0.25)); // ~0.5 s window
        return (RemoveTrend(full, half), RemoveTrend(low, half));
    }

    /// <summary>Removes the slowly varying loudness so only the onsets remain.</summary>
    private static double[] RemoveTrend(double[] env, int half)
    {
        int frames = env.Length;
        var result = new double[frames];
        double sum = 0;
        int lo = 0, hi = -1;
        for (int i = 0; i < frames; i++)
        {
            while (hi < Math.Min(frames - 1, i + half)) sum += env[++hi];
            while (lo < i - half) sum -= env[lo++];
            result[i] = Math.Max(0, env[i] - sum / (hi - lo + 1));
        }
        return result;
    }

    private static double CoarseTempo(double[] env, double fps, double minBpm, double maxBpm)
    {
        int minLag = (int)Math.Floor(fps * 60 / maxBpm);
        int maxLag = (int)Math.Ceiling(fps * 60 / minBpm);
        int acMax = Math.Min(env.Length - 1, maxLag * 2 + 2);
        var ac = new double[acMax + 1];
        for (int lag = minLag; lag <= acMax; lag++)
        {
            double s = 0;
            for (int i = 0; i + lag < env.Length; i++) s += env[i] * env[i + lag];
            ac[lag] = s / (env.Length - lag);
        }

        double best = double.MinValue;
        double bestLag = minLag;
        for (int lag = minLag; lag <= maxLag && lag < ac.Length; lag++)
        {
            double harmonic = 2 * lag < ac.Length ? 0.5 * ac[2 * lag] : 0;
            double bpm = fps * 60 / lag;
            // Gentle prior towards ~125 BPM to pick a sensible octave.
            double prior = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 125.0) / 0.9, 2));
            double score = (ac[lag] + harmonic) * prior;
            if (score > best)
            {
                best = score;
                bestLag = lag;
            }
        }

        // Parabolic interpolation around the peak.
        int l = (int)bestLag;
        if (l > minLag && l + 1 < ac.Length)
        {
            double a = ac[l - 1], b = ac[l], c = ac[l + 1];
            double denom = a - 2 * b + c;
            if (Math.Abs(denom) > 1e-12) bestLag = l + 0.5 * (a - c) / denom;
        }
        return fps * 60 / bestLag;
    }

    /// <summary>Searches tempos in [lo, hi] for the beat grid that lines up best with the onsets.</summary>
    private static (double Bpm, double Phase, double Score) BestComb(double[] env, double fps, double lo, double hi, double step)
    {
        double bestBpm = lo, bestPhase = 0, bestScore = double.MinValue;
        for (double bpm = lo; bpm <= hi + 1e-9; bpm += step)
        {
            double period = fps * 60 / bpm;
            if ((env.Length - 1) / period < 4) continue;
            for (double phase = 0; phase < period; phase += 0.5)
            {
                double s = CombScore(env, phase, period);
                if (s > bestScore)
                {
                    bestScore = s;
                    bestBpm = bpm;
                    bestPhase = phase;
                }
            }
        }
        return (bestBpm, bestPhase, bestScore);
    }

    /// <summary>Mean envelope value sampled on the beat grid (phase, phase + period, ...).</summary>
    private static double CombScore(double[] env, double phase, double period)
    {
        int beats = (int)((env.Length - 1 - phase) / period);
        if (beats <= 0) return 0;
        double s = 0;
        for (int k = 0; k < beats; k++)
        {
            double t = phase + k * period;
            int i = (int)t;
            double frac = t - i;
            s += env[i] * (1 - frac) + env[i + 1] * frac;
        }
        return s / beats;
    }

    /// <summary>In-place iterative radix-2 FFT. Length must be a power of two.</summary>
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
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
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * cr - im[b] * ci;
                    double ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }
}
