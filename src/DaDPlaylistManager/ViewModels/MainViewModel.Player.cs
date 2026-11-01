using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DaDPlaylistManager.Services;

namespace DaDPlaylistManager.ViewModels;

public partial class MainViewModel
{
    private AudioPlayer _player = null!;
    private DispatcherTimer _positionTimer = null!;
    private bool _updatingPosition;

    [ObservableProperty] private SongViewModel? _nowPlaying;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _playbackPosition;
    [ObservableProperty] private double _playbackDuration;
    [ObservableProperty] private double _volume;
    [ObservableProperty] private bool _metronomeEnabled;

    /// <summary>Set by the view while the user drags the position slider.</summary>
    public bool IsUserSeeking { get; set; }

    public string PositionText => $"{Format(PlaybackPosition)} / {Format(PlaybackDuration)}";

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private void InitPlayer()
    {
        _player = new AudioPlayer { Volume = (float)Volume, MetronomeEnabled = MetronomeEnabled };
        _player.StateChanged += (_, _) => IsPlaying = _player.IsPlaying;
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _positionTimer.Tick += (_, _) =>
        {
            if (IsUserSeeking || _player.CurrentFile is null) return;
            _updatingPosition = true;
            PlaybackPosition = _player.Position.TotalSeconds;
            _updatingPosition = false;
        };
        _positionTimer.Start();
    }

    partial void OnPlaybackPositionChanged(double value)
    {
        OnPropertyChanged(nameof(PositionText));
        if (!_updatingPosition) _player.Seek(TimeSpan.FromSeconds(value));
    }

    partial void OnPlaybackDurationChanged(double value) => OnPropertyChanged(nameof(PositionText));

    partial void OnVolumeChanged(double value) => _player.Volume = (float)value;

    partial void OnMetronomeEnabledChanged(bool value) => _player.MetronomeEnabled = value;

    [RelayCommand]
    private void PlaySong(SongViewModel? song)
    {
        song ??= SelectedSong;
        if (song is null || !song.Entry.HasAudio) return;
        try
        {
            if (NowPlaying != song || _player.CurrentFile is null)
            {
                _player.Open(song.Entry.AudioPath);
                NowPlaying = song;
                PlaybackDuration = _player.Duration.TotalSeconds;
                UpdateBeats();
                _updatingPosition = true;
                PlaybackPosition = 0;
                _updatingPosition = false;
            }
            _player.Play();
        }
        catch (Exception ex)
        {
            Dialogs.Error($"Couldn't play \"{song.Title}\".\n\n{ex.Message}");
            StopPlayback();
        }
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (_player.IsPlaying) _player.Pause();
        else if (NowPlaying is not null && _player.CurrentFile is not null) _player.Play();
        else PlaySong(SelectedSong);
    }

    [RelayCommand]
    private void StopPlayback()
    {
        _player.Close();
        NowPlaying = null;
        _updatingPosition = true;
        PlaybackPosition = 0;
        PlaybackDuration = 0;
        _updatingPosition = false;
    }

    [RelayCommand]
    private void SkipBy(string? seconds)
    {
        if (_player.CurrentFile is null || !double.TryParse(seconds, System.Globalization.CultureInfo.InvariantCulture, out var delta)) return;
        PlaybackPosition = Math.Clamp(PlaybackPosition + delta, 0, PlaybackDuration);
    }

    private void OnSongTimingChanged(object? sender, EventArgs e)
    {
        if (sender == NowPlaying) UpdateBeats();
    }

    private void UpdateBeats()
    {
        if (NowPlaying is not { } s) return;
        _player.SetBeats(BeatGrid.Build(s.Tempo, s.BeatOffset, s.CurrentSections(), PlaybackDuration));
    }

    private void DisposePlayer()
    {
        _positionTimer.Stop();
        _player.Dispose();
    }
}
