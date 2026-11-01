using System.Collections;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DaDPlaylistManager.Core.Audio;
using DaDPlaylistManager.Core.Packs;
using DaDPlaylistManager.Core.Songs;
using DaDPlaylistManager.Services;
using DaDPlaylistManager.Views;
using Microsoft.VisualBasic.FileIO;

namespace DaDPlaylistManager.ViewModels;

public partial class MainViewModel
{
    private DateTime _gameWarningAcceptedAt = DateTime.MinValue;

    [ObservableProperty] private SongViewModel? _selectedSong;
    [ObservableProperty] private string _songSearch = "";

    partial void OnSongSearchChanged(string value) => SongsView.Refresh();

    private bool ConfirmWriteOnce()
    {
        // Ask at most once a minute, so "Save all" doesn't prompt for every file.
        if (DateTime.Now - _gameWarningAcceptedAt < TimeSpan.FromMinutes(1)) return true;
        if (!ConfirmWriteWhileGameRunning()) return false;
        _gameWarningAcceptedAt = DateTime.Now;
        return true;
    }

    [RelayCommand]
    private void SaveSong(SongViewModel? song)
    {
        song ??= SelectedSong;
        if (song is not null && SaveSongCore(song)) Status = $"Saved \"{song.Title}\".";
    }

    private bool SaveSongCore(SongViewModel song)
    {
        if (!song.CanEdit) return true;
        if (song.Validate() is { } problem)
        {
            Dialogs.Error($"Can't save \"{song.Title}\": {problem}");
            SelectedSong = song;
            return false;
        }
        if (!ConfirmWriteOnce()) return false;

        try
        {
            _backups.BackupSongMetadata(song.Entry.Directory);
            song.ApplyToMeta();

            string desired = song.DesiredFolderName;
            if (SyncFolderNames && !string.Equals(desired, song.FolderName, StringComparison.Ordinal))
            {
                string unique = _library.MakeUniqueFolderName(desired, song.Entry.Directory);
                try
                {
                    bool wasPlaying = NowPlaying == song;
                    if (wasPlaying) StopPlayback();
                    song.ReplaceEntry(_library.Rename(song.Entry, unique));
                }
                catch (Exception ex)
                {
                    _library.SaveMeta(song.Entry);
                    Status = $"Saved \"{song.Title}\" but couldn't rename its folder: {ex.Message}";
                    song.IsDirty = false;
                    return true;
                }
            }
            else
            {
                _library.SaveMeta(song.Entry);
            }
            song.IsDirty = false;
            SongsView.Refresh();
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.Error($"Couldn't save \"{song.Title}\".\n\n{ex.Message}");
            return false;
        }
    }

    [RelayCommand]
    private void RevertSong(SongViewModel? song)
    {
        song ??= SelectedSong;
        if (song?.Entry.Meta is null) return;
        // Re-read from disk so the edit buffer matches the file exactly.
        try
        {
            song.Entry.Meta = SongMeta.Load(song.Entry.MetaPath);
        }
        catch
        {
            // Keep the in-memory copy if the file can't be read.
        }
        song.LoadFromEntry();
    }

    [RelayCommand]
    private void DeleteSongs(IList? selected)
    {
        var songs = (selected?.Cast<SongViewModel>() ?? (SelectedSong is null ? [] : [SelectedSong])).ToList();
        if (songs.Count == 0) return;

        var affected = Playlists.Where(p => songs.Any(p.Contains)).ToList();
        string what = songs.Count == 1 ? $"\"{songs[0].Title}\"" : $"{songs.Count} songs";
        string playlists = affected.Count > 0 ? $"\n\nIt will also be removed from {affected.Count} playlist(s): {string.Join(", ", affected.Select(p => p.Name))}." : "";
        if (!Dialogs.Confirm($"Delete {what}?\n\nThe song folder(s) will be moved to the Recycle Bin.{playlists}")) return;
        if (!ConfirmWriteOnce()) return;

        if (NowPlaying is not null && songs.Contains(NowPlaying)) StopPlayback();

        foreach (var song in songs)
        {
            try
            {
                if (Directory.Exists(song.Entry.Directory))
                {
                    _backups.BackupSongMetadata(song.Entry.Directory);
                    FileSystem.DeleteDirectory(song.Entry.Directory, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
                Songs.Remove(song);
            }
            catch (Exception ex)
            {
                Dialogs.Error($"Couldn't delete \"{song.Title}\".\n\n{ex.Message}");
            }
        }

        foreach (var p in affected)
        {
            bool wasDirty = p.IsDirty;
            foreach (var e in p.Entries.Where(e => e.Song is not null && !Songs.Contains(e.Song)).ToList()) p.Entries.Remove(e);
            if (!wasDirty && !p.IsNew) SavePlaylistCore(p);
        }
        UpdatePlaylistCounts();
        AvailableView.Refresh();
        Status = $"Deleted {what}.";
    }

    [RelayCommand]
    private void OpenSongFolder(SongViewModel? song)
    {
        song ??= SelectedSong;
        if (song is not null) Dialogs.OpenInExplorer(song.Entry.Directory);
    }

    [RelayCommand]
    private void AddTempoSection()
    {
        if (SelectedSong is not { CanEdit: true } song) return;
        double start = NowPlaying == song
            ? Math.Round(PlaybackPosition, 3)
            : song.TempoSections.Count > 0 ? song.TempoSections.Max(s => s.StartTime) + 10 : 30;
        song.TempoSections.Add(new TempoSectionViewModel { Tempo = song.Tempo, StartTime = start });
    }

    [RelayCommand]
    private void RemoveTempoSection(TempoSectionViewModel? section)
    {
        if (section is not null) SelectedSong?.TempoSections.Remove(section);
    }

    [RelayCommand]
    private void SetBeatOffsetFromPlayback()
    {
        if (SelectedSong is not { CanEdit: true } song) return;
        if (NowPlaying != song)
        {
            Status = "Play the song and press this on a beat to set the offset.";
            return;
        }
        double periodMs = song.Tempo > 0 ? 60000 / song.Tempo : double.MaxValue;
        double ms = PlaybackPosition * 1000;
        // Keep it within the first bar-ish so it reads like the game's values.
        while (ms >= periodMs * 4) ms -= periodMs * 4;
        song.BeatOffset = (int)Math.Round(ms);
    }

    [RelayCommand]
    private void NewSeed()
    {
        if (SelectedSong is { CanEdit: true } song) song.Seed = SongMeta.RandomUInt();
    }

    private Ffmpeg? GetFfmpeg()
    {
        var path = FfmpegLocator.Find(_settings.FfmpegPath);
        if (path is null)
        {
            if (Dialogs.Confirm("ffmpeg wasn't found, and it's needed to convert audio.\n\nPick an ffmpeg.exe now? (Download one from https://ffmpeg.org/download.html)"))
            {
                path = Dialogs.OpenFile("Locate ffmpeg.exe", "ffmpeg.exe|ffmpeg.exe|Programs (*.exe)|*.exe");
                if (path is not null)
                {
                    _settings.FfmpegPath = path;
                    _settings.Save();
                }
            }
        }
        return path is null ? null : new Ffmpeg(path);
    }

    [RelayCommand]
    private void ImportAudio() => ImportAudioFiles(null);

    public void ImportAudioFiles(string[]? files)
    {
        if (DataFolderMissing && !Dialogs.Confirm($"The game data folder wasn't found:\n{SavedDirectory}\n\nCreate it and import anyway?")) return;
        files ??= Dialogs.OpenFiles("Choose audio files to import",
            "Audio files|" + string.Join(";", AudioImporter.SupportedExtensions.Select(e => "*" + e)) + "|All files (*.*)|*.*");
        if (files.Length == 0) return;
        var ffmpeg = GetFfmpeg();
        if (ffmpeg is null) return;

        _paths.EnsureDirectories();
        DataFolderMissing = false;
        var importer = new AudioImporter(ffmpeg, _library);
        var vm = new ImportViewModel(importer, files, () => Songs.Select(s => s.UniqueId), Playlists.ToList());
        var window = new ImportWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow };
        window.ShowDialog();

        if (vm.Imported.Count == 0) return;
        var added = vm.Imported.Select(AddSong).ToList();
        if (vm.TargetPlaylist is { } target)
        {
            foreach (var s in added) target.Entries.Add(new PlaylistEntryViewModel(new Core.Playlists.PlaylistEntry("", s.UniqueId), s));
            SelectedPlaylist = target;
        }
        AvailableView.Refresh();
        SelectedSong = added[^1];
        Status = $"Imported {added.Count} song(s)." + (vm.TargetPlaylist is { } t ? $" Added to \"{t.Name}\" (not saved yet)." : "");
    }

    private SongViewModel AddSong(SongEntry entry)
    {
        var vm = new SongViewModel(entry);
        vm.TimingChanged += OnSongTimingChanged;
        vm.Duration = AudioPlayer.ReadDuration(entry.AudioPath);
        Songs.Add(vm);
        return vm;
    }

    [RelayCommand]
    private async Task ExportSongs(IList? selected)
    {
        var songs = (selected?.Cast<SongViewModel>() ?? []).Where(s => !s.HasError).ToList();
        if (songs.Count == 0)
        {
            Dialogs.Info("Select one or more songs to export (Ctrl/Shift-click to pick several).");
            return;
        }
        string name = songs.Count == 1 ? songs[0].FolderName : $"Dead as Disco songs ({songs.Count})";
        var path = Dialogs.SaveFile("Export song pack", $"Song pack (*{SongPack.Extension})|*{SongPack.Extension}", SongLibrary.SanitizeFolderName(name) + SongPack.Extension);
        if (path is null) return;
        await RunBusy($"Exporting {songs.Count} song(s)…", () => SongPack.Export(path, songs.Select(s => s.Entry).ToList(), []));
        Status = $"Exported {songs.Count} song(s) to {Path.GetFileName(path)}.";
    }

    [RelayCommand]
    private async Task ImportPack()
    {
        var path = Dialogs.OpenFile("Import song pack", $"Song packs (*{SongPack.Extension};*.zip)|*{SongPack.Extension};*.zip|All files (*.*)|*.*");
        if (path is null) return;
        await ImportPackFile(path);
    }

    public async Task ImportPackFile(string path)
    {
        SongPack.Manifest manifest;
        try
        {
            manifest = SongPack.ReadManifest(path);
        }
        catch (Exception ex)
        {
            Dialogs.Error($"That doesn't look like a song pack.\n\n{ex.Message}");
            return;
        }

        string playlists = manifest.Playlists.Count > 0 ? $" and {manifest.Playlists.Count} playlist(s) ({string.Join(", ", manifest.Playlists.Select(p => p.Name))})" : "";
        if (!Dialogs.Confirm($"Import {manifest.Songs.Count} song(s){playlists} from {Path.GetFileName(path)}?\n\nSongs you already have are skipped.")) return;
        if (!ConfirmDiscardOrSave("importing")) return;
        if (!ConfirmWriteOnce()) return;

        _paths.EnsureDirectories();
        var existing = Songs.Select(s => s.Entry).ToList();
        string prefix = Playlists.Select(p => p.File.TransientPrefix).FirstOrDefault() ?? Core.Playlists.PlaylistFile.DefaultTransientPrefix;
        SongPack.ImportResult? result = null;
        await RunBusy("Importing song pack…", () => result = SongPack.Import(path, _library, existing, _store, prefix));
        ReloadAll();
        if (result is null) return;

        var msg = $"Added {result.Added} song(s), skipped {result.AlreadyPresent} already in your library";
        if (result.IdsReassigned > 0) msg += $", gave {result.IdsReassigned} a new ID to avoid clashes";
        if (result.PlaylistsAdded > 0) msg += $", added {result.PlaylistsAdded} playlist(s)";
        msg += ".";
        if (result.Warnings.Count > 0) msg += "\n\n" + string.Join("\n", result.Warnings.Take(15));
        Dialogs.Info(msg);
        Status = msg.Split('\n')[0];
    }

    private async Task RunBusy(string message, Action work)
    {
        IsBusy = true;
        Status = message;
        try
        {
            await Task.Run(work);
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
