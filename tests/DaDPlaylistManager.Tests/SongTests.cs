using DaDPlaylistManager.Core;
using DaDPlaylistManager.Core.Audio;
using DaDPlaylistManager.Core.Songs;

namespace DaDPlaylistManager.Tests;

public class SongTests
{
    private const string GameMeta = """
        {
          "version": 1,
          "uniqueId": 3669255012,
          "songName": "POP/STARS",
          "performedBy": ["K/DA"],
          "writtenBy": [],
          "seed": 4001823574,
          "tempo": 164.72999572753906,
          "customTempoSections": [{"tempo": 220, "startAbsoluteTime": 131.4517059326172}],
          "beatOffset": 470,
          "startSongOffset": 0.7099999785423279,
          "endSongOffset": 15,
          "futureField": {"keep": true},
          "originalAudioFileHash": "9ca6fd83d1b1f72d836419d7d52e6c25",
          "originalAudioFilePath": "C:/x/Audio.ogg"
        }
        """;

    [Fact]
    public void Meta_ReadsAndPreservesFields()
    {
        var m = SongMeta.Parse(GameMeta);
        Assert.Equal(3669255012u, m.UniqueId);
        Assert.Equal(4001823574u, m.Seed);
        Assert.Equal(164.72999572753906, m.Tempo);
        Assert.Equal(470, m.BeatOffset);
        Assert.Equal(15, m.EndSongOffset);
        Assert.Equal(["K/DA"], m.PerformedBy);
        Assert.Single(m.CustomTempoSections);

        m.Tempo = 128;
        m.PerformedBy = ["A", "B"];
        m.CustomTempoSections = [new TempoSection(100, 1.5)];
        var back = SongMeta.Parse(m.ToJson());
        Assert.Equal(128, back.Tempo);
        Assert.Equal(["A", "B"], back.PerformedBy);
        Assert.Equal(new TempoSection(100, 1.5), back.CustomTempoSections[0]);
        Assert.Contains("futureField", back.ToJson());
        Assert.Contains("\"tempo\": 128,", back.ToJson());
    }

    [Theory]
    [InlineData(new[] { "K/DA" }, "POP/STARS", "KDA - POPSTARS")]
    [InlineData(new[] { "Maroon 5", "J. Cole" }, "Animals", "Maroon 5, J. Cole - Animals")]
    [InlineData(new[] { "Mili" }, "world.execute(me);", "Mili - world.execute(me);")]
    [InlineData(new string[0], "Solo", "Solo")]
    public void FolderName_MatchesGameConvention(string[] artists, string title, string expected) =>
        Assert.Equal(expected, SongLibrary.BuildFolderName(artists, title));

    [Theory]
    [InlineData(128.0, 250)]
    [InlineData(95.0, 100)]
    [InlineData(174.0, 40)]
    public void Bpm_DetectsSyntheticClickTrack(double bpm, int offsetMs)
    {
        const int sr = 22050;
        var samples = new float[sr * 40];
        var rng = new Random(1);
        for (int i = 0; i < samples.Length; i++) samples[i] = (float)(rng.NextDouble() - 0.5) * 0.01f;
        double period = 60.0 / bpm;
        for (double t = offsetMs / 1000.0; t < 40; t += period)
        {
            int start = (int)(t * sr);
            for (int i = 0; i < 400 && start + i < samples.Length; i++)
                samples[start + i] += (float)(Math.Sin(i * 0.6) * Math.Exp(-i / 80.0));
        }

        var result = BpmDetector.Detect(samples, sr);
        Assert.InRange(result.Bpm, bpm - 0.5, bpm + 0.5);
        double periodMs = 60000 / bpm;
        double diff = Math.Abs(result.BeatOffsetMs - offsetMs) % periodMs;
        Assert.True(Math.Min(diff, periodMs - diff) < 25, $"offset {result.BeatOffsetMs} vs {offsetMs}");
    }
}
