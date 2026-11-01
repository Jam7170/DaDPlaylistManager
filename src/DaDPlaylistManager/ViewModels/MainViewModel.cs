using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DaDPlaylistManager.Core;
using DaDPlaylistManager.Core.Playlists;
using DaDPlaylistManager.Core.Songs;
using DaDPlaylistManager.Services;

namespace DaDPlaylistManager.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _gameTimer;
    private GamePaths _paths = null!;
    private SongLibrary _library = null!;
    private PlaylistStore _store = null!;
    private BackupService _backups = null!;

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        _volume = _settings.Volume;
        _metronomeEnabled = _settings.Metronome;
        _syncFolderNames = _settings.SyncFolderNames;

        SongsView = CollectionViewSource.GetDefaultView(Songs);
        SongsView.Filter = o => MatchesSearch((SongViewModel)o, SongSearch);
        SongsView.SortDescriptions.Add(new SortDescription(nameof(SongViewModel.Title), ListSortDirection.Ascending));

        // A second view over the same songs for the playlist editor's "available" list.
        AvailableView = new CollectionViewSource { Source = Songs }.View;
        AvailableView.Filter = o => o is SongViewModel s && !s.HasError && MatchesSearch(s, AvailableSearch) &&
                                    (SelectedPlaylist is null || !SelectedPlaylist.Contains(s));
        AvailableView.SortDescriptions.Add(new SortDescription(nameof(SongViewModel.Title), ListSortDirection.Ascending));

        InitPlayer();

        _gameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _gameTimer.Tick += (_, _) => IsGameRunning = GamePaths.IsGameRunning();
        _gameTimer.Start();
        IsGameRunning = GamePaths.IsGameRunning();

        OpenDataFolder(CommandLineDataFolder() ??
                       (_settings.SavedDirectory is { } s && GamePaths.LooksValid(s) ? s : GamePaths.DefaultSavedDirectory));
    }

    public ObservableCollection<SongViewModel> Songs { get; } = [];
    public ICollectionView SongsView { get; }
    public ObservableCollection<PlaylistViewModel> Playlists { get; } = [];
    public ICollectionView AvailableView { get; }

    [ObservableProperty] private string _savedDirectory = "";
    [ObservableProperty] private bool _dataFolderMissing;
    [ObservableProperty] private bool _isGameRunning;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _syncFolderNames;

    partial void OnSyncFolderNamesChanged(bool value)
    {
        _settings.SyncFolderNames = value;
        _settings.Save();
    }

    public string FfmpegPathSetting => _settings.FfmpegPath ?? "";

    private static bool MatchesSearch(SongViewModel s, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!(s.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                  s.Artists.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                  s.FolderName.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
                return false;
        }
        return true;
    }

    /// <summary>"--data &lt;folder&gt;" overrides the game data folder for this run (handy for testing on a copy).</summary>
    private static string? CommandLineDataFolder()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.FindIndex(args, a => a.Equals("--data", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null;
    }

    private void OpenDataFolder(string savedDirectory)
    {
        _paths = new GamePaths(savedDirectory);
        _library = new SongLibrary(_paths.ImportedSongs);
        _backups = new BackupService(Path.Combine(AppSettings.AppDataDirectory, "Backups"), savedDirectory);
        _store = new PlaylistStore(_paths.Playlists, _backups);
        SavedDirectory = savedDirectory;
        DataFolderMissing = !GamePaths.LooksValid(savedDirectory);
        ReloadAll();
    }

    private void ReloadAll()
    {
        StopPlayback();
        string? selectedSong = SelectedSong?.FolderName;
        string? selectedPlaylist = SelectedPlaylist?.Document?.FilePath;

        Songs.Clear();
        foreach (var entry in _library.Load())
        {
            var vm = new SongViewModel(entry);
            vm.TimingChanged += OnSongTimingChanged;
            Songs.Add(vm);
        }
        LoadDurationsAsync();

        Playlists.Clear();
        foreach (var doc in _store.Load())
        {
            if (doc.File is null)
            {
                Status = $"Couldn't read {Path.GetFileName(doc.FilePath)}: {doc.LoadError}";
                continue;
            }
            Playlists.Add(new PlaylistViewModel(doc, doc.File, FindSong));
        }
        UpdatePlaylistCounts();

        SelectedSong = Songs.FirstOrDefault(s => s.FolderName == selectedSong) ?? SongsView.Cast<SongViewModel>().FirstOrDefault();
        SelectedPlaylist = Playlists.FirstOrDefault(p => p.Document?.FilePath == selectedPlaylist) ?? Playlists.FirstOrDefault();

        int broken = Songs.Count(s => s.HasError);
        Status = $"Loaded {Songs.Count} songs and {Playlists.Count} playlists" + (broken > 0 ? $" ({broken} with problems)" : "") + ".";
    }

    private SongViewModel? FindSong(uint id) => Songs.FirstOrDefault(s => s.UniqueId == id && s.CanEdit);

    private async void LoadDurationsAsync()
    {
        var items = Songs.Where(s => s.Entry.HasAudio).Select(s => (Vm: s, s.Entry.AudioPath)).ToList();
        var results = await Task.Run(() => items.Select(i => (i.Vm, Duration: AudioPlayer.ReadDuration(i.AudioPath))).ToList());
        foreach (var (vm, duration) in results) vm.Duration = duration;
        foreach (var p in Playlists) p.RefreshSummary();
    }

    private void UpdatePlaylistCounts()
    {
        var counts = new Dictionary<SongViewModel, int>();
        foreach (var p in Playlists)
            foreach (var s in p.Entries.Select(e => e.Song).OfType<SongViewModel>().Distinct())
                counts[s] = counts.GetValueOrDefault(s) + 1;
        foreach (var s in Songs) s.PlaylistCount = counts.GetValueOrDefault(s);
    }

    public bool HasUnsavedChanges => Songs.Any(s => s.IsDirty) || Playlists.Any(p => p.IsDirty);

    /// <summary>Asks what to do with unsaved edits. Returns false if the caller should abort.</summary>
    public bool ConfirmDiscardOrSave(string action)
    {
        if (!HasUnsavedChanges) return true;
        var answer = Dialogs.YesNoCancel($"You have unsaved changes. Save them before {action}?");
        if (answer is null) return false;
        if (answer == true) return SaveAll();
        return true;
    }

    [RelayCommand]
    private void Refresh()
    {
        if (!ConfirmDiscardOrSave("reloading")) return;
        ReloadAll();
    }

    [RelayCommand]
    private void SaveAllChanges() => SaveAll();

    public bool SaveAll()
    {
        foreach (var s in Songs.Where(s => s.IsDirty).ToList())
            if (!SaveSongCore(s)) return false;
        foreach (var p in Playlists.Where(p => p.IsDirty).ToList())
            if (!SavePlaylistCore(p)) return false;
        Status = "All changes saved.";
        return true;
    }

    [RelayCommand]
    private void ChangeDataFolder()
    {
        var folder = Dialogs.OpenFolder("Select the game's Saved folder (…\\AppData\\Local\\Pagoda\\Saved)", SavedDirectory);
        if (folder is null) return;
        if (!GamePaths.LooksValid(folder) &&
            !Dialogs.Confirm("That folder doesn't look like Dead as Disco's Saved folder (no ImportedSongs, Playlists or SaveGames inside). Use it anyway?"))
            return;
        if (!ConfirmDiscardOrSave("switching folders")) return;
        _settings.SavedDirectory = folder;
        _settings.Save();
        OpenDataFolder(folder);
    }

    [RelayCommand]
    private void ResetDataFolder()
    {
        if (!ConfirmDiscardOrSave("switching folders")) return;
        _settings.SavedDirectory = null;
        _settings.Save();
        OpenDataFolder(GamePaths.DefaultSavedDirectory);
    }

    [RelayCommand]
    private void OpenSavedFolder() => Dialogs.OpenInExplorer(SavedDirectory);

    [RelayCommand]
    private void OpenBackupsFolder()
    {
        Directory.CreateDirectory(_backups.Root);
        Dialogs.OpenInExplorer(_backups.Root);
    }

    [RelayCommand]
    private void About()
    {
        string version = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "";
        Dialogs.Info(
            $"Dead as Disco Playlist Manager {version}\n\n" +
            "Manage imported songs and custom playlists for Dead as Disco.\n\n" +
            $"Game data: {SavedDirectory}\n" +
            $"Backups: {_backups.Root}\n\n" +
            "Before changing a file, the tool copies it to the backups folder (once per session).\n" +
            "The game reads songs and playlists when it starts, so restart it to see changes.\n\n" +
            "Not affiliated with Brain Jar Games. Bundled ffmpeg is licensed under the LGPL/GPL (see ffmpeg.org).");
    }

    /// <summary>Checks that it's reasonably safe to write, warning if the game is running.</summary>
    private bool ConfirmWriteWhileGameRunning()
    {
        if (!GamePaths.IsGameRunning()) return true;
        return Dialogs.Confirm(
            "Dead as Disco is running.\n\nThe game only reads songs and playlists at startup, and editing the same playlist in-game afterwards could overwrite these changes.\n\nSave anyway? (Restart the game afterwards to see the changes.)");
    }

    private void RunGuarded(Action action, string failurePrefix)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Dialogs.Error($"{failurePrefix}\n\n{ex.Message}");
        }
    }

    public void OnClosing(CancelEventArgs e)
    {
        if (!ConfirmDiscardOrSave("closing")) e.Cancel = true;
    }

    public void Dispose()
    {
        _gameTimer.Stop();
        _settings.Volume = Volume;
        _settings.Metronome = MetronomeEnabled;
        _settings.Save();
        DisposePlayer();
    }

    internal static Dispatcher UiDispatcher => Application.Current.Dispatcher;
}
