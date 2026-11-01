using DaDPlaylistManager.Core;
using DaDPlaylistManager.Core.Packs;
using DaDPlaylistManager.Core.Playlists;
using DaDPlaylistManager.Core.Songs;

namespace DaDPlaylistManager.Tests;

public sealed class SongPackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dadpm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (SongLibrary Lib, PlaylistStore Store, GamePaths Paths) MakeGame(string name)
    {
        var paths = new GamePaths(Path.Combine(_root, name));
        paths.EnsureDirectories();
        var backups = new BackupService(Path.Combine(_root, name + "-backups"), paths.Saved);
        return (new SongLibrary(paths.ImportedSongs), new PlaylistStore(paths.Playlists, backups), paths);
    }

    private static SongEntry AddSong(SongLibrary lib, uint id, string artist, string title, byte audioByte)
    {
        string dir = Path.Combine(lib.Root, SongLibrary.BuildFolderName([artist], title));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, SongLibrary.AudioFileName), Enumerable.Repeat(audioByte, 1000).ToArray());
        var meta = SongMeta.CreateNew(id, title, [artist], 120, 0);
        meta.OriginalAudioFileHash = SongLibrary.Md5Hex(Path.Combine(dir, SongLibrary.AudioFileName));
        meta.Save(Path.Combine(dir, SongLibrary.MetaFileName));
        return new SongEntry { Directory = dir, Meta = meta };
    }

    [Fact]
    public void ExportImport_RoundTripsSongsAndPlaylists_AndDeduplicates()
    {
        var (srcLib, _, _) = MakeGame("src");
        var a = AddSong(srcLib, 111, "Artist A", "Song A", 1);
        var b = AddSong(srcLib, 222, "Artist B", "Song B", 2);
        var pl = new PlaylistFile { Name = "Shared", Shuffle = true };
        pl.Entries.Add(new PlaylistEntry("x.PagodaSongCatalogSubsystem_1.Artist B - Song B", 222));
        pl.Entries.Add(new PlaylistEntry("/Game/Builtin.Builtin", null));
        pl.Entries.Add(new PlaylistEntry("x.PagodaSongCatalogSubsystem_1.Artist A - Song A", 111));

        string pack = Path.Combine(_root, "test" + SongPack.Extension);
        SongPack.Export(pack, [a, b], [pl]);

        // Destination already has a different song with id 222 -> must be reassigned.
        var (dstLib, dstStore, _) = MakeGame("dst");
        AddSong(dstLib, 222, "Someone", "Other", 9);

        var result = SongPack.Import(pack, dstLib, dstLib.Load(), dstStore, PlaylistFile.DefaultTransientPrefix);
        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.AlreadyPresent);
        Assert.Equal(1, result.IdsReassigned);
        Assert.Equal(1, result.PlaylistsAdded);

        var songs = dstLib.Load();
        Assert.Equal(3, songs.Count);
        Assert.Equal(3, songs.Select(s => s.Meta!.UniqueId).Distinct().Count());
        var importedB = songs.Single(s => s.Title == "Song B");
        Assert.NotEqual(222u, importedB.Meta!.UniqueId);
        Assert.EndsWith("/dst/ImportedSongs/Artist B - Song B/Audio.ogg", importedB.Meta.OriginalAudioFilePath);

        var imported = dstStore.Load().Single().File!;
        Assert.Equal("Shared", imported.Name);
        Assert.True(imported.Shuffle);
        Assert.Equal(importedB.Meta.UniqueId, imported.Entries[0].ImportedSongId);
        Assert.Equal("/Game/Builtin.Builtin", imported.Entries[1].ObjectPath);
        Assert.Equal(111u, imported.Entries[2].ImportedSongId);

        // Importing again adds no songs, and a second playlist gets a distinct name.
        var again = SongPack.Import(pack, dstLib, dstLib.Load(), dstStore, PlaylistFile.DefaultTransientPrefix);
        Assert.Equal(0, again.Added);
        Assert.Equal(2, again.AlreadyPresent);
        Assert.Equal(3, dstLib.Load().Count);
        Assert.Contains(dstStore.Load(), d => d.File!.Name == "Shared (2)");
    }
}
