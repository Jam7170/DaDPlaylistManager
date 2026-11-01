using DaDPlaylistManager.Core;
using DaDPlaylistManager.Core.Playlists;

namespace DaDPlaylistManager.Tests;

public class PlaylistFileTests
{
    [Fact]
    public void NewPlaylist_RoundTrips()
    {
        var pl = new PlaylistFile { Name = "Boss Rush", Shuffle = true };
        pl.Entries.Add(new PlaylistEntry(PlaylistFile.ImportedObjectPath(PlaylistFile.DefaultTransientPrefix, "Ado - unravel"), 3669255012));
        pl.Entries.Add(new PlaylistEntry("/Game/Pagoda/Audio/Songs/Foo/Foo.Foo", null));
        pl.Entries.Add(new PlaylistEntry(PlaylistFile.ImportedObjectPath(PlaylistFile.DefaultTransientPrefix, "ハロー - テスト"), 42));

        var bytes = pl.Serialize();
        var back = PlaylistFile.Parse(bytes);

        Assert.Equal("Boss Rush", back.Name);
        Assert.True(back.Shuffle);
        Assert.Equal(pl.Guid, back.Guid);
        Assert.Equal(pl.Entries, back.Entries);
        Assert.Equal(bytes, back.Serialize());
        Assert.Equal("ハロー - テスト", PlaylistFile.FolderNameFromPath(back.Entries[2].ObjectPath));
    }

    [Fact]
    public void Edits_ArePersisted()
    {
        var pl = new PlaylistFile { Name = "A" };
        pl.Entries.Add(new PlaylistEntry("x.PagodaSongCatalogSubsystem_1.Song A", 1));
        var back = PlaylistFile.Parse(pl.Serialize());

        back.Name = "Renamed";
        back.Shuffle = true;
        back.Entries.Insert(0, new PlaylistEntry("x.PagodaSongCatalogSubsystem_1.Song B", 2));
        var again = PlaylistFile.Parse(back.Serialize());

        Assert.Equal("Renamed", again.Name);
        Assert.True(again.Shuffle);
        Assert.Equal([2u, 1u], again.Entries.Select(e => e.ImportedSongId!.Value));
        Assert.Equal("x.PagodaSongCatalogSubsystem_1", again.TransientPrefix);
    }

    [Fact]
    public void FileName_MatchesGameGuidFormat()
    {
        var pl = new PlaylistFile { Guid = [0x96B024D7, 0x4A84B028, 0xE9D293AD, 0x48976B10] };
        Assert.Equal("Playlist_96B024D74A84B028E9D293AD48976B10.bjpl", pl.FileName);
    }

    /// <summary>Byte-for-byte round trip of real playlists on this machine, when present.</summary>
    [Fact]
    public void RealGamePlaylists_RoundTripExactly()
    {
        var dir = new GamePaths(GamePaths.DefaultSavedDirectory).Playlists;
        if (!Directory.Exists(dir)) return;
        foreach (var path in Directory.EnumerateFiles(dir, "*.bjpl"))
        {
            var original = File.ReadAllBytes(path);
            var pl = PlaylistFile.Parse(original);
            Assert.Equal(Path.GetFileName(path), pl.FileName);
            Assert.All(pl.Entries, e => Assert.NotNull(e.ImportedSongId));
            Assert.Equal(original, pl.Serialize());
        }
    }
}
