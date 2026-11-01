using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DaDPlaylistManager.Core.Audio;
using DaDPlaylistManager.Core.Songs;

namespace DaDPlaylistManager.ViewModels;

public partial class ImportItemViewModel(string sourcePath) : ObservableObject
{
    public string SourcePath { get; } = sourcePath;
    public string FileName => Path.GetFileName(SourcePath);

    [ObservableProperty] private bool _include = true;
    [ObservableProperty] private string _title = Path.GetFileNameWithoutExtension(sourcePath);
    [ObservableProperty] private string _artists = "";
    [ObservableProperty] private double _tempo = 120;
    [ObservableProperty] private int _beatOffset;
    [ObservableProperty] private string _status = "Waiting…";
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private bool _done;
}

/// <summary>Drives the "Import audio" dialog: guess tags, estimate BPM, then convert.</summary>
public partial class ImportViewModel : ObservableObject
{
    private readonly AudioImporter _importer;
    private readonly Func<IEnumerable<uint>> _existingIds;
    private readonly CancellationTokenSource _cts = new();

    public ImportViewModel(AudioImporter importer, IEnumerable<string> files, Func<IEnumerable<uint>> existingIds, IReadOnlyList<PlaylistViewModel> playlists)
    {
        _importer = importer;
        _existingIds = existingIds;
        foreach (var f in files) Items.Add(new ImportItemViewModel(f));
        PlaylistChoices = [new PlaylistChoice("(none)", null), .. playlists.Select(p => new PlaylistChoice(p.Name, p))];
        _selectedChoice = PlaylistChoices[0];
    }

    public ObservableCollection<ImportItemViewModel> Items { get; } = [];
    public List<SongEntry> Imported { get; } = [];

    public sealed record PlaylistChoice(string Name, PlaylistViewModel? Playlist);

    /// <summary>Playlists the imported songs can be appended to.</summary>
    public List<PlaylistChoice> PlaylistChoices { get; }

    [ObservableProperty] private PlaylistChoice _selectedChoice;

    public PlaylistViewModel? TargetPlaylist => SelectedChoice.Playlist;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private bool _finished;
    [ObservableProperty] private string _summary = "Reading tags and estimating tempo…";

    public event EventHandler? RequestClose;

    /// <summary>Called when the window opens.</summary>
    public async Task AnalyzeAllAsync()
    {
        IsAnalyzing = true;
        using var gate = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount / 2));
        var tasks = Items.Select(async item =>
        {
            await gate.WaitAsync(_cts.Token);
            try
            {
                item.IsWorking = true;
                item.Status = "Reading tags…";
                var guess = await _importer.GuessMetadataAsync(item.SourcePath, _cts.Token);
                item.Title = guess.Title;
                item.Artists = string.Join("; ", guess.Artists);
                item.Status = "Estimating BPM…";
                var bpm = await _importer.AnalyzeAsync(item.SourcePath, _cts.Token);
                item.Tempo = bpm.Bpm;
                item.BeatOffset = bpm.BeatOffsetMs;
                item.Status = "Ready";
            }
            catch (OperationCanceledException)
            {
                item.Status = "Cancelled";
            }
            catch (Exception ex)
            {
                item.Status = "Couldn't analyse: " + ex.Message.Split('\n')[0];
            }
            finally
            {
                item.IsWorking = false;
                gate.Release();
            }
        }).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
        IsAnalyzing = false;
        Summary = "Check the titles and tempos, then import. You can fine-tune timing later in the game's song editor.";
    }

    [RelayCommand]
    private async Task Import()
    {
        if (IsImporting) return;
        if (Finished)
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
            return;
        }
        IsImporting = true;
        _cts.TryReset();
        var ids = _existingIds().ToList();
        int ok = 0, failed = 0;
        foreach (var item in Items.Where(i => i.Include && !i.Done))
        {
            if (string.IsNullOrWhiteSpace(item.Title) || item.Tempo <= 0)
            {
                item.Status = "Needs a title and a tempo";
                failed++;
                continue;
            }
            item.IsWorking = true;
            item.Status = "Converting…";
            try
            {
                var entry = await _importer.ImportAsync(item.SourcePath, item.Title, SongViewModel.SplitList(item.Artists),
                    Math.Round(item.Tempo, 2), Math.Max(0, item.BeatOffset), ids, _cts.Token);
                ids.Add(entry.Meta!.UniqueId);
                Imported.Add(entry);
                item.Status = "Imported ✓";
                item.Done = true;
                ok++;
            }
            catch (Exception ex)
            {
                item.Status = "Failed: " + ex.Message.Split('\n')[0];
                failed++;
            }
            finally
            {
                item.IsWorking = false;
            }
        }
        IsImporting = false;
        Finished = failed == 0;
        Summary = failed == 0 ? $"Imported {ok} song(s)." : $"Imported {ok}, {failed} need attention. Fix them and press Import again, or close.";
        if (Finished) RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts.Cancel();
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
