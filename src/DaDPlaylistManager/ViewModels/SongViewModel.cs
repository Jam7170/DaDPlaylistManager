using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DaDPlaylistManager.Core.Songs;

namespace DaDPlaylistManager.ViewModels;

public partial class TempoSectionViewModel : ObservableObject
{
    [ObservableProperty] private double _tempo;
    [ObservableProperty] private double _startTime;
}

/// <summary>An imported song with editable copies of its metadata.</summary>
public partial class SongViewModel : ObservableObject
{
    private bool _loading;

    public SongViewModel(SongEntry entry)
    {
        Entry = entry;
        TempoSections.CollectionChanged += OnSectionsChanged;
        LoadFromEntry();
    }

    public SongEntry Entry { get; private set; }

    public string FolderName => Entry.FolderName;
    public uint UniqueId => Entry.Meta?.UniqueId ?? 0;
    public bool HasError => Entry.Meta is null || !Entry.HasAudio;
    public string? ErrorText => Entry.LoadError ?? (!Entry.HasAudio ? "Audio.ogg is missing" : null);
    public bool CanEdit => Entry.Meta is not null;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artists = "";
    [ObservableProperty] private string _writtenBy = "";
    [ObservableProperty] private double _tempo;
    [ObservableProperty] private int _beatOffset;
    [ObservableProperty] private double _startOffset;
    [ObservableProperty] private double _endOffset;
    [ObservableProperty] private uint _seed;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private TimeSpan? _duration;
    [ObservableProperty] private int _playlistCount;

    public ObservableCollection<TempoSectionViewModel> TempoSections { get; } = [];

    public string DurationText => Duration is { } d ? $"{(int)d.TotalMinutes}:{d.Seconds:00}" : "";
    public string ArtistsDisplay => string.Join(", ", SplitList(Artists));

    /// <summary>Raised when anything that affects the beat grid changes.</summary>
    public event EventHandler? TimingChanged;

    partial void OnDurationChanged(TimeSpan? value) => OnPropertyChanged(nameof(DurationText));

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(Title) or nameof(Artists) or nameof(WrittenBy) or nameof(StartOffset) or nameof(EndOffset) or nameof(Seed):
                MarkDirty();
                if (e.PropertyName == nameof(Artists)) OnPropertyChanged(nameof(ArtistsDisplay));
                break;
            case nameof(Tempo) or nameof(BeatOffset):
                MarkDirty();
                TimingChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnSectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (TempoSectionViewModel s in e.NewItems) s.PropertyChanged += OnSectionPropertyChanged;
        if (e.OldItems is not null)
            foreach (TempoSectionViewModel s in e.OldItems) s.PropertyChanged -= OnSectionPropertyChanged;
        MarkDirty();
        TimingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkDirty();
        TimingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MarkDirty()
    {
        if (!_loading && CanEdit) IsDirty = true;
    }

    public void LoadFromEntry()
    {
        _loading = true;
        try
        {
            var m = Entry.Meta;
            Title = m?.SongName is { Length: > 0 } t ? t : Entry.FolderName;
            Artists = m is null ? "" : string.Join("; ", m.PerformedBy);
            WrittenBy = m is null ? "" : string.Join("; ", m.WrittenBy);
            Tempo = m?.Tempo ?? 0;
            BeatOffset = m?.BeatOffset ?? 0;
            StartOffset = m?.StartSongOffset ?? 0;
            EndOffset = m?.EndSongOffset ?? 0;
            Seed = m?.Seed ?? 0;
            TempoSections.Clear();
            if (m is not null)
                foreach (var s in m.CustomTempoSections)
                    TempoSections.Add(new TempoSectionViewModel { Tempo = s.Tempo, StartTime = s.StartAbsoluteTime });
            IsDirty = false;
        }
        finally
        {
            _loading = false;
        }
        TimingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Copies the edited values into the entry's metadata (does not write to disk).</summary>
    public void ApplyToMeta()
    {
        var m = Entry.Meta ?? throw new InvalidOperationException("This song has no Meta.json to edit.");
        m.SongName = Title.Trim();
        m.PerformedBy = SplitList(Artists);
        m.WrittenBy = SplitList(WrittenBy);
        m.Tempo = Tempo;
        m.BeatOffset = BeatOffset;
        m.StartSongOffset = StartOffset;
        m.EndSongOffset = EndOffset;
        m.Seed = Seed;
        m.CustomTempoSections = TempoSections.Select(s => new TempoSection(s.Tempo, s.StartTime)).ToList();
    }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Title)) return "Title can't be empty.";
        if (Tempo is <= 0 or > 1000) return "Tempo must be between 0 and 1000 BPM.";
        if (BeatOffset < 0) return "Beat offset can't be negative.";
        if (StartOffset < 0 || EndOffset < 0) return "Start/end trims can't be negative.";
        if (TempoSections.Any(s => s.Tempo <= 0 || s.StartTime < 0)) return "Tempo sections need a positive tempo and a start time of 0 or more.";
        return null;
    }

    public void ReplaceEntry(SongEntry entry)
    {
        Entry = entry;
        OnPropertyChanged(nameof(Entry));
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(UniqueId));
    }

    public string DesiredFolderName => SongLibrary.BuildFolderName(SplitList(Artists), Title);

    public static List<string> SplitList(string text) =>
        text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public List<TempoSection> CurrentSections() => TempoSections.Select(s => new TempoSection(s.Tempo, s.StartTime)).ToList();
}
