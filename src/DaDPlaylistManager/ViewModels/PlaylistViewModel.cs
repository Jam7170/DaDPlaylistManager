using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using DaDPlaylistManager.Core.Playlists;

namespace DaDPlaylistManager.ViewModels;

/// <summary>One row in a playlist. <see cref="Song"/> is null for entries this tool can't resolve.</summary>
public partial class PlaylistEntryViewModel : ObservableObject
{
    public PlaylistEntryViewModel(PlaylistEntry entry, SongViewModel? song)
    {
        Entry = entry;
        Song = song;
    }

    public PlaylistEntry Entry { get; }
    public SongViewModel? Song { get; }

    [ObservableProperty] private int _number;

    public bool IsMissing => Song is null;
    public bool IsBuiltIn => Entry.ImportedSongId is null;

    public string Title => Song?.Title ?? (IsBuiltIn ? BuiltInName(Entry.ObjectPath) : PlaylistFile.FolderNameFromPath(Entry.ObjectPath) ?? "(unknown)");

    public string Subtitle => Song is not null
        ? Song.ArtistsDisplay
        : IsBuiltIn ? "Built-in game song (kept as is)" : "Missing imported song (will be kept)";

    private static string BuiltInName(string path)
    {
        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOf('/');
        return dot > slash ? path[(dot + 1)..] : path;
    }
}

public partial class PlaylistViewModel : ObservableObject
{
    private bool _loading;

    public PlaylistViewModel(PlaylistDocument? document, PlaylistFile file, Func<uint, SongViewModel?> resolve)
    {
        Document = document;
        File = file;
        _loading = true;
        Name = file.Name;
        Shuffle = file.Shuffle;
        foreach (var e in file.Entries)
            Entries.Add(new PlaylistEntryViewModel(e, e.ImportedSongId is { } id ? resolve(id) : null));
        _loading = false;
        Entries.CollectionChanged += OnEntriesChanged;
        Renumber();
    }

    /// <summary>Null for playlists that haven't been saved yet.</summary>
    public PlaylistDocument? Document { get; set; }

    /// <summary>The last loaded/saved file, kept so properties this tool doesn't edit are preserved.</summary>
    public PlaylistFile File { get; private set; }

    public ObservableCollection<PlaylistEntryViewModel> Entries { get; } = [];

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _shuffle;
    [ObservableProperty] private bool _isDirty;

    public bool IsNew => Document is null;

    public string Summary
    {
        get
        {
            var total = TimeSpan.FromSeconds(Entries.Sum(e => e.Song?.Duration?.TotalSeconds ?? 0));
            string time = total.TotalHours >= 1 ? $"{(int)total.TotalHours}h {total.Minutes}m" : $"{total.Minutes}m {total.Seconds}s";
            return $"{Entries.Count} song{(Entries.Count == 1 ? "" : "s")} · {time}";
        }
    }

    partial void OnNameChanged(string value) => MarkDirty();
    partial void OnShuffleChanged(bool value) => MarkDirty();

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Renumber();
        MarkDirty();
    }

    private void MarkDirty()
    {
        if (!_loading) IsDirty = true;
    }

    public void Renumber()
    {
        for (int i = 0; i < Entries.Count; i++) Entries[i].Number = i + 1;
        OnPropertyChanged(nameof(Summary));
    }

    public void RefreshSummary() => OnPropertyChanged(nameof(Summary));

    public bool Contains(SongViewModel song) => Entries.Any(e => e.Song == song);

    /// <summary>Builds the file to save from the current edits.</summary>
    public PlaylistFile BuildFile()
    {
        var file = PlaylistFile.Parse(File.Serialize());
        string prefix = File.TransientPrefix;
        file.Name = Name.Trim();
        file.Shuffle = Shuffle;
        file.Entries.Clear();
        foreach (var e in Entries)
        {
            file.Entries.Add(e.Song is { } song
                ? new PlaylistEntry(PlaylistFile.ImportedObjectPath(prefix, song.FolderName), song.UniqueId)
                : e.Entry);
        }
        return file;
    }

    public void MarkSaved(PlaylistDocument document, PlaylistFile saved)
    {
        Document = document;
        File = saved;
        IsDirty = false;
        OnPropertyChanged(nameof(IsNew));
    }

    public void Move(int from, int to)
    {
        if (from == to || from < 0 || from >= Entries.Count) return;
        to = Math.Clamp(to, 0, Entries.Count - 1);
        Entries.Move(from, to);
    }

    /// <summary>Replaces all entries in one go (one change notification for sorting etc.).</summary>
    public void ReplaceAll(IEnumerable<PlaylistEntryViewModel> items)
    {
        var list = items.ToList();
        Entries.CollectionChanged -= OnEntriesChanged;
        Entries.Clear();
        foreach (var i in list) Entries.Add(i);
        Entries.CollectionChanged += OnEntriesChanged;
        Renumber();
        MarkDirty();
    }
}
