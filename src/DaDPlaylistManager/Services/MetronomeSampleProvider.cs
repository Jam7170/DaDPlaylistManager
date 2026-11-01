using NAudio.Wave;

namespace DaDPlaylistManager.Services;

/// <summary>Passes audio through and mixes a click on every beat of a <see cref="BeatGrid"/>.</summary>
public sealed class MetronomeSampleProvider(ISampleProvider source, Func<long> getSamplePosition) : ISampleProvider
{
    private const double ClickSeconds = 0.03;
    private volatile double[] _beats = [];

    public WaveFormat WaveFormat => source.WaveFormat;
    public bool Enabled { get; set; }
    public float ClickVolume { get; set; } = 0.6f;

    public void SetBeats(double[] beats) => _beats = beats;

    public int Read(Span<float> buffer)
    {
        int channels = WaveFormat.Channels;
        int rate = WaveFormat.SampleRate;
        // Position of the first sample of this block (in frames), taken before the source advances.
        long startFrame = getSamplePosition();
        int read = source.Read(buffer);
        if (!Enabled || read == 0) return read;

        var beats = _beats;
        if (beats.Length == 0) return read;

        int frames = read / channels;
        double blockStart = (double)startFrame / rate;
        double blockEnd = (double)(startFrame + frames) / rate;

        // Any beat whose click overlaps this block.
        int idx = LowerBound(beats, blockStart - ClickSeconds);
        for (; idx < beats.Length && beats[idx] < blockEnd; idx++)
        {
            double beat = beats[idx];
            float freq = idx % 4 == 0 ? 1760f : 1320f;
            long beatFrame = (long)(beat * rate);
            long clickFrames = (long)(ClickSeconds * rate);
            long from = Math.Max(beatFrame, startFrame);
            long to = Math.Min(beatFrame + clickFrames, startFrame + frames);
            for (long f = from; f < to; f++)
            {
                double t = (double)(f - beatFrame) / rate;
                float v = (float)(Math.Sin(2 * Math.PI * freq * t) * Math.Exp(-t * 120) * ClickVolume);
                int baseIndex = (int)(f - startFrame) * channels;
                for (int c = 0; c < channels; c++) buffer[baseIndex + c] += v;
            }
        }
        return read;
    }

    private static int LowerBound(double[] a, double value)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (a[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
