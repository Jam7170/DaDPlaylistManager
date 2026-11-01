using NAudio.Vorbis;
using NAudio.Wave;

namespace DaDPlaylistManager.Services;

/// <summary>Plays Ogg Vorbis files with an optional metronome overlay.</summary>
public sealed class AudioPlayer : IDisposable
{
    private const int BufferMs = 50;
    private const int Buffers = 3;

    private WaveOut? _output;
    private VorbisWaveReader? _reader;
    private MetronomeSampleProvider? _metronome;
    private float _volume = 0.7f;
    private bool _metronomeEnabled;
    private double[] _beats = [];

    /// <summary>Raised (on the UI thread) when playback starts, pauses, stops or ends.</summary>
    public event EventHandler? StateChanged;

    public string? CurrentFile { get; private set; }
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    /// <summary>Approximately what is audible right now (decoder position minus output latency).</summary>
    public TimeSpan Position
    {
        get
        {
            if (_reader is null) return TimeSpan.Zero;
            var t = _reader.CurrentTime;
            if (IsPlaying) t -= TimeSpan.FromMilliseconds(BufferMs * Buffers);
            return t < TimeSpan.Zero ? TimeSpan.Zero : t;
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_output is not null) _output.Volume = _volume;
        }
    }

    public bool MetronomeEnabled
    {
        get => _metronomeEnabled;
        set
        {
            _metronomeEnabled = value;
            if (_metronome is not null) _metronome.Enabled = value;
        }
    }

    public void SetBeats(double[] beats)
    {
        _beats = beats;
        _metronome?.SetBeats(beats);
    }

    public void Open(string file)
    {
        Close();
        var reader = new VorbisWaveReader(file);
        _reader = reader;
        int blockAlign = reader.WaveFormat.BlockAlign;
        _metronome = new MetronomeSampleProvider(reader, () => reader.Position / blockAlign)
        {
            Enabled = _metronomeEnabled,
        };
        _metronome.SetBeats(_beats);
        _output = new WaveOut { BufferMilliseconds = BufferMs, NumberOfBuffers = Buffers };
        _output.Init(_metronome.ToWaveProvider());
        _output.Volume = _volume;
        _output.PlaybackStopped += OnPlaybackStopped;
        CurrentFile = file;
    }

    public void Play()
    {
        if (_output is null || _reader is null) return;
        if (_reader.Position >= _reader.Length) _reader.Position = 0;
        _output.Play();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        _output?.Pause();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Seek(TimeSpan position)
    {
        if (_reader is null) return;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > _reader.TotalTime) position = _reader.TotalTime;
        _reader.CurrentTime = position;
    }

    public void Close()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Stop();
            _output.Dispose();
            _output = null;
        }
        _reader?.Dispose();
        _reader = null;
        _metronome = null;
        CurrentFile = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e) => StateChanged?.Invoke(this, EventArgs.Empty);

    public static TimeSpan? ReadDuration(string file)
    {
        try
        {
            using var r = new VorbisWaveReader(file);
            return r.TotalTime;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => Close();
}
