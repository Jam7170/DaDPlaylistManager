using DaDPlaylistManager.Core.Songs;
using DaDPlaylistManager.Core.Sync;

namespace DaDPlaylistManager.Tests;

public class SyncTests
{
    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(135.0, "135")]
    [InlineData(-3.0, "-3")]
    [InlineData(164.72999572753906, "164.72999572753906")]
    [InlineData(0.7099999785423279, "0.7099999785423279")]
    [InlineData(4001823574.0, "4001823574")]
    [InlineData(0.0002288818359375, "0.0002288818359375")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(1.5e21, "1.5e+21")]
    [InlineData(1e21, "1e+21")]
    [InlineData(123456789012345680000.0, "123456789012345680000")]
    [InlineData(0.1, "0.1")]
    [InlineData(0.000001, "0.000001")]
    public void JsNumber_MatchesJavaScript(double value, string expected) => Assert.Equal(expected, SetCode.JsNumber(value));

    [Fact]
    public void SetCode_IsStableAndFormatted()
    {
        var a = SongMeta.CreateNew(1, "A", ["X"], 128, 250);
        a.Seed = 42;
        a.CustomTempoSections = [new TempoSection(100, 30.5)];
        string line = SetCode.Line("0123456789abcdef0123456789abcdef", a);
        Assert.Equal("0123456789abcdef0123456789abcdef|128|250|0|0|42|100@30.5", line);

        string code = SetCode.Compute([("0123456789abcdef0123456789abcdef", a)]);
        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}$", code);
        Assert.Equal(code, SetCode.ComputeFromLines([line]));

        // Empty playlist = SHA-256 of "" -> e3b0c442...
        Assert.Equal("E3B0-C442", SetCode.Compute([]));
    }

    /// <summary>Shared vector with the website's implementation (server/dad.ts on the site).</summary>
    [Fact]
    public void SetCode_MatchesServerSampleVector()
    {
        var a = SongMeta.Parse("""
            {"tempo":128,"beatOffset":0.25,"startSongOffset":1.5,"endSongOffset":200,"seed":42,
             "customTempoSections":[{"tempo":130,"startAbsoluteTime":60.5},{"tempo":128,"startAbsoluteTime":120}]}
            """);
        var b = SongMeta.Parse("""{"tempo":100.5}""");
        const string ha = "0175147f4dd121d391d770306e00e5a8", hb = "3f60607aba014c79af46fbb481f242dc";

        Assert.Equal("0175147f4dd121d391d770306e00e5a8|128|0.25|1.5|200|42|130" + (char)64 + "60.5,128" + (char)64 + "120", SetCode.Line(ha, a));
        Assert.Equal("3f60607aba014c79af46fbb481f242dc|100.5|0|0|0|0|", SetCode.Line(hb, b));
        Assert.Equal("441F-59DD", SetCode.Compute([(ha, a), (hb, b)]));
    }

    [Fact]
    public void AdoptFrom_KeepsLocalIdentity()
    {
        var local = SongMeta.CreateNew(111, "Old", ["Me"], 120, 0);
        local.OriginalAudioFilePath = "C:/local/Audio.ogg";
        local.OriginalAudioFileHash = "abc";
        var remote = SongMeta.CreateNew(999, "New", ["Them"], 140, 300);
        remote.Seed = 7;

        Assert.False(local.SharedFieldsEqual(remote));
        local.AdoptFrom(remote);
        Assert.Equal(111u, local.UniqueId);
        Assert.Equal("C:/local/Audio.ogg", local.OriginalAudioFilePath);
        Assert.Equal("abc", local.OriginalAudioFileHash);
        Assert.Equal(140, local.Tempo);
        Assert.Equal(7u, local.Seed);
        Assert.True(local.SharedFieldsEqual(remote));
    }
}
