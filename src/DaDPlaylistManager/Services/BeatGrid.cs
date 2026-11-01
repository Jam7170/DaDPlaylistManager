using DaDPlaylistManager.Core.Songs;

namespace DaDPlaylistManager.Services;

/// <summary>
/// Beat times implied by a song's tempo settings: the grid starts at the beat offset with the base
/// tempo, and restarts at each custom tempo section's start time with that section's tempo.
/// </summary>
public static class BeatGrid
{
    public static double[] Build(double tempo, int beatOffsetMs, IEnumerable<TempoSection> sections, double durationSeconds)
    {
        var beats = new List<double>();
        if (tempo <= 0 || durationSeconds <= 0) return [];

        var segments = new List<(double Start, double Tempo)> { (beatOffsetMs / 1000.0, tempo) };
        segments.AddRange(sections.Where(s => s.Tempo > 0).OrderBy(s => s.StartAbsoluteTime).Select(s => (s.StartAbsoluteTime, s.Tempo)));

        for (int i = 0; i < segments.Count; i++)
        {
            double start = segments[i].Start;
            double end = i + 1 < segments.Count ? segments[i + 1].Start : durationSeconds;
            double period = 60.0 / segments[i].Tempo;
            for (double t = start; t < end - 1e-6 && beats.Count < 100_000; t += period)
                if (t >= 0) beats.Add(t);
        }
        return beats.ToArray();
    }
}
