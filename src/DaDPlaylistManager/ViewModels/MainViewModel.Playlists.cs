using System.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DaDPlaylistManager.Core.Packs;
using DaDPlaylistManager.Core.Playlists;
using DaDPlaylistManager.Core.Songs;
using DaDPlaylistManager.Services;

namespace DaDPlaylistManager.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private PlaylistViewModel? _selectedPlaylist;
    [ObservableProperty] private string _availableSearch = "";

    partial void OnSelectedPlaylistChanged(PlaylistViewModel? value) => AvailableView.Refresh();
    partial void OnAvailableSearchChanged(string value) => AvailableView.Refresh();

    private string UniquePlaylistName(string name)
    {
        var taken = Playlists.Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int n = 2; ; n++)
            if (!taken.Contains($"{name} ({n})")) return $"{name} ({n})";
    }

    [RelayCommand]
    private void NewPlaylist()
    {
        var file = new PlaylistFile { Name = UniquePlaylistName("New Playlist") };
        var vm = new PlaylistViewModel(null, file, FindSong) { IsDirty = true };
        Playlists.Add(vm);
        SelectedPlaylist = vm;
        Status = "New playlist created. Add songs, then press Save.";
    }

    [RelayCommand]
    private void DuplicatePlaylist()
    {
        if (SelectedPlaylist is not { } source) return;
        var file = source.BuildFile().CloneAsNew(UniquePlaylistName(source.Name + " (copy)"));
        var vm = new PlaylistViewModel(null, file, FindSong) { IsDirty = true };
        Playlists.Add(vm);
        SelectedPlaylist = vm;
    }

    [RelayCommand]
    private void DeletePlaylist()
    {
        if (SelectedPlaylist is not { } pl) return;
        if (!Dialogs.Confirm($"Delete the playlist \"{pl.Name}\"?\n\nThe songs themselves are not deleted. A backup copy of the playlist is kept.")) return;
        if (!pl.IsNew)
        {
            if (!ConfirmWriteOnce()) return;
            try
            {
                _store.Delete(pl.Document!.FilePath);
            }
            catch (Exception ex)
            {
                Dialogs.Error("Couldn't delete the playlist.\n\n" + ex.Message);
                return;
            }
        }
        int index = Playlists.IndexOf(pl);
        Playlists.Remove(pl);
        SelectedPlaylist = Playlists.Count == 0 ? null : Playlists[Math.Min(index, Playlists.Count - 1)];
        UpdatePlaylistCounts();
        Status = $"Deleted playlist \"{pl.Name}\".";
    }

    [RelayCommand]
    private void SavePlaylist()
    {
        if (SelectedPlaylist is { } pl && SavePlaylistCore(pl)) Status = $"Saved playlist \"{pl.Name}\" ({pl.Entries.Count} songs).";
    }

    private bool SavePlaylistCore(PlaylistViewModel pl)
    {
        if (string.IsNullOrWhiteSpace(pl.Name))
        {
            Dialogs.Error("A playlist needs a name.");
            SelectedPlaylist = pl;
            return false;
        }
        if (pl.Entries.Count == 0 && !Dialogs.Confirm($"\"{pl.Name}\" has no songs. The game can't play an empty playlist. Save anyway?"))
            return false;
        if (!ConfirmWriteOnce()) return false;

        try
        {
            var file = pl.BuildFile();
            string path = _store.Save(file);
            var doc = new PlaylistDocument { FilePath = path, File = file };
            pl.MarkSaved(doc, file);
            UpdatePlaylistCounts();
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.Error($"Couldn't save \"{pl.Name}\".\n\n{ex.Message}");
            return false;
        }
    }

    [RelayCommand]
    private void RevertPlaylist()
    {
        if (SelectedPlaylist is not { } pl) return;
        int index = Playlists.IndexOf(pl);
        if (pl.IsNew)
        {
            Playlists.RemoveAt(index);
            SelectedPlaylist = Playlists.FirstOrDefault();
            return;
        }
        PlaylistFile file;
        try
        {
            file = PlaylistFile.Load(pl.Document!.FilePath);
        }
        catch
        {
            file = pl.File;
        }
        var fresh = new PlaylistViewModel(pl.Document, file, FindSong);
        Playlists[index] = fresh;
        SelectedPlaylist = fresh;
    }

    [RelayCommand]
    private void AddToPlaylist(IList? songs)
    {
        if (SelectedPlaylist is not { } pl || songs is null) return;
        AddSongsToPlaylist(pl, songs.Cast<SongViewModel>().ToList(), pl.Entries.Count);
    }

    public void AddSongsToPlaylist(PlaylistViewModel pl, IReadOnlyList<SongViewModel> songs, int index)
    {
        index = Math.Clamp(index, 0, pl.Entries.Count);
        foreach (var s in songs.Where(s => !s.HasError && !pl.Contains(s)))
            pl.Entries.Insert(index++, new PlaylistEntryViewModel(new PlaylistEntry("", s.UniqueId), s));
        AvailableView.Refresh();
    }

    [RelayCommand]
    private void AddAllToPlaylist()
    {
        if (SelectedPlaylist is not { } pl) return;
        AddSongsToPlaylist(pl, AvailableView.Cast<SongViewModel>().ToList(), pl.Entries.Count);
    }

    [RelayCommand]
    private void RemoveFromPlaylist(IList? entries)
    {
        if (SelectedPlaylist is not { } pl || entries is null) return;
        foreach (var e in entries.Cast<PlaylistEntryViewModel>().ToList()) pl.Entries.Remove(e);
        AvailableView.Refresh();
    }

    [RelayCommand]
    private void ClearPlaylist()
    {
        if (SelectedPlaylist is not { } pl || pl.Entries.Count == 0) return;
        if (!Dialogs.Confirm($"Remove all {pl.Entries.Count} songs from \"{pl.Name}\"? (Nothing is saved until you press Save.)")) return;
        pl.ReplaceAll([]);
        AvailableView.Refresh();
    }

    [RelayCommand]
    private void MoveEntries(string? direction)
    {
        if (SelectedPlaylist is not { } pl || SelectedEntries.Count == 0) return;
        var selected = SelectedEntries.OrderBy(pl.Entries.IndexOf).ToList();
        var order = pl.Entries.ToList();
        switch (direction)
        {
            case "up":
                foreach (var e in selected)
                {
                    int i = order.IndexOf(e);
                    if (i > 0 && !selected.Contains(order[i - 1])) (order[i - 1], order[i]) = (order[i], order[i - 1]);
                }
                break;
            case "down":
                foreach (var e in Enumerable.Reverse(selected))
                {
                    int i = order.IndexOf(e);
                    if (i < order.Count - 1 && !selected.Contains(order[i + 1])) (order[i + 1], order[i]) = (order[i], order[i + 1]);
                }
                break;
            case "top":
                order = [.. selected, .. order.Except(selected)];
                break;
            case "bottom":
                order = [.. order.Except(selected), .. selected];
                break;
        }
        ApplyOrder(pl, order);
    }

    /// <summary>Moves entries to a position (used by drag and drop).</summary>
    public void MoveEntriesTo(PlaylistViewModel pl, IReadOnlyList<PlaylistEntryViewModel> items, int targetIndex)
    {
        var order = pl.Entries.ToList();
        int before = order.Take(targetIndex).Count(items.Contains);
        order.RemoveAll(items.Contains);
        order.InsertRange(Math.Clamp(targetIndex - before, 0, order.Count), items);
        ApplyOrder(pl, order);
    }

    private void ApplyOrder(PlaylistViewModel pl, List<PlaylistEntryViewModel> order)
    {
        if (order.SequenceEqual(pl.Entries)) return;
        var keep = SelectedEntries.ToList();
        pl.ReplaceAll(order);
        EntriesReordered?.Invoke(this, keep);
    }

    /// <summary>Lets the view restore the selection after a reorder.</summary>
    public event EventHandler<List<PlaylistEntryViewModel>>? EntriesReordered;

    /// <summary>Set by the view from the playlist list's selection.</summary>
    public List<PlaylistEntryViewModel> SelectedEntries { get; set; } = [];

    [RelayCommand]
    private void SortPlaylist(string? mode)
    {
        if (SelectedPlaylist is not { } pl) return;
        IEnumerable<PlaylistEntryViewModel> e = pl.Entries;
        var cmp = StringComparer.CurrentCultureIgnoreCase;
        e = mode switch
        {
            "title" => e.OrderBy(x => x.Title, cmp),
            "artist" => e.OrderBy(x => x.Song?.ArtistsDisplay ?? "", cmp).ThenBy(x => x.Title, cmp),
            "bpm" => e.OrderBy(x => x.Song?.Tempo ?? 0),
            "length" => e.OrderBy(x => x.Song?.Duration ?? TimeSpan.Zero),
            "reverse" => e.Reverse(),
            "random" => e.OrderBy(_ => Random.Shared.Next()),
            _ => e,
        };
        pl.ReplaceAll(e.ToList());
    }

    [RelayCommand]
    private void RemoveMissing()
    {
        if (SelectedPlaylist is not { } pl) return;
        var missing = pl.Entries.Where(e => e.IsMissing && !e.IsBuiltIn).ToList();
        foreach (var m in missing) pl.Entries.Remove(m);
        Status = missing.Count == 0 ? "No missing songs in this playlist." : $"Removed {missing.Count} missing song(s).";
    }

    [RelayCommand]
    private async Task ExportPlaylist()
    {
        if (SelectedPlaylist is not { } pl) return;
        var songs = pl.Entries.Select(e => e.Song).OfType<SongViewModel>().Where(s => !s.HasError).Distinct().Select(s => s.Entry).ToList();
        var path = Dialogs.SaveFile("Export playlist as song pack", $"Song pack (*{SongPack.Extension})|*{SongPack.Extension}",
            SongLibrary.SanitizeFolderName(pl.Name) + SongPack.Extension);
        if (path is null) return;
        var file = pl.BuildFile();
        await RunBusy($"Exporting \"{pl.Name}\"…", () => SongPack.Export(path, songs, [file]));
        Status = $"Exported \"{pl.Name}\" with {songs.Count} song(s).";
    }
}
