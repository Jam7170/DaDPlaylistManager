using DaDPlaylistManager.Core.Sync;

namespace DaDPlaylistManager.ViewModels;

/// <summary>A song in a shared playlist, with how it compares to this PC.</summary>
public sealed class SharedSongRow(SharedSong song, SharedSongState state, bool canRemove)
{
    public SharedSong Song { get; } = song;
    public SharedSongState State { get; } = state;
    public bool CanRemove { get; } = canRemove;

    public int Number => Song.Position + 1;
    public string Title => Song.Meta["songName"]?.ToString() ?? "(untitled)";
    public string Artists => Song.Meta["performedBy"] is System.Text.Json.Nodes.JsonArray a ? string.Join(", ", a.Select(x => x?.ToString())) : "";
    public string Bpm => double.TryParse(Song.Meta["tempo"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t)
        ? t.ToString("0.##") : "";
    public string AddedBy => Song.AddedBy.Name;

    public string StateText => State switch
    {
        SharedSongState.Synced => "✓ On this PC",
        SharedSongState.Missing => "↓ Not downloaded",
        SharedSongState.Differs => "⟳ Timing differs",
        SharedSongState.LocalChanges => "↑ Your edits not uploaded",
        _ => "",
    };
}

public sealed class SharedMemberRow(SharedMember member, string currentSetCode)
{
    public string Name => member.Player.Name;
    public bool InSync => member.SetCode == currentSetCode;

    public string Status
    {
        get
        {
            if (member.LastSyncedAt is null) return "never synced";
            string when = DateTime.TryParse(member.LastSyncedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                ? Ago(t.ToLocalTime()) : member.LastSyncedAt;
            return InSync ? $"✓ in sync · {when}" : $"out of date · {when}";
        }
    }

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }
}
