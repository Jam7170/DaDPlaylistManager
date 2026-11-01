using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DaDPlaylistManager.Core;
using DaDPlaylistManager.Core.Sync;
using DaDPlaylistManager.Services;
using DaDPlaylistManager.Views;

namespace DaDPlaylistManager.ViewModels;

public partial class MainViewModel
{
    private readonly AudioHashCache _hashCache = new();
    private HashSet<string> _sharedHashes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _localHashByPath = new(StringComparer.OrdinalIgnoreCase);
    private ICollectionView? _shareView;

    public ObservableCollection<SharedPlaylistSummary> SharedPlaylists { get; } = [];
    public ObservableCollection<SharedSongRow> SharedSongs { get; } = [];
    public ObservableCollection<SharedMemberRow> SharedMembers { get; } = [];

    [ObservableProperty] private SharedPlaylistSummary? _selectedShared;
    [ObservableProperty] private SharedPlaylist? _sharedDetail;
    [ObservableProperty] private bool _sharedBusy;
    [ObservableProperty] private string _sharedStatus = "";
    [ObservableProperty] private string _shareSearch = "";
    [ObservableProperty] private string? _localSetCode;

    public bool IsSignedIn => _settings.SyncToken is not null && _settings.SyncPlayerId is not null;
    public string SignedInText => IsSignedIn ? $"Signed in as {_settings.SyncPlayerName} on {DisplayServer}" : "Not signed in";
    public string DisplayServer => Uri.TryCreate(_settings.SyncServer, UriKind.Absolute, out var u) ? u.Host : _settings.SyncServer;
    public bool AutoSyncShared
    {
        get => _settings.AutoSyncShared;
        set
        {
            _settings.AutoSyncShared = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool SharedIsMine => SharedDetail is not null && SharedDetail.CreatedBy.Id == _settings.SyncPlayerId;
    public bool SetCodesMatch => SharedDetail is not null && LocalSetCode == SharedDetail.SetCode;

    /// <summary>Library songs that aren't in the selected shared playlist yet.</summary>
    public ICollectionView ShareView => _shareView ??= CreateShareView();

    private ICollectionView CreateShareView()
    {
        var view = new CollectionViewSource { Source = Songs }.View;
        view.Filter = o => o is SongViewModel s && !s.HasError && MatchesSearch(s, ShareSearch) &&
                           !(_localHashByPath.TryGetValue(s.Entry.AudioPath, out var h) && _sharedHashes.Contains(h));
        view.SortDescriptions.Add(new SortDescription(nameof(SongViewModel.Title), ListSortDirection.Ascending));
        return view;
    }

    partial void OnShareSearchChanged(string value) => ShareView.Refresh();

    partial void OnSharedDetailChanged(SharedPlaylist? value)
    {
        OnPropertyChanged(nameof(SharedIsMine));
        OnPropertyChanged(nameof(SetCodesMatch));
    }

    partial void OnLocalSetCodeChanged(string? value) => OnPropertyChanged(nameof(SetCodesMatch));

    async partial void OnSelectedSharedChanged(SharedPlaylistSummary? value)
    {
        if (value is null)
        {
            SharedDetail = null;
            SharedSongs.Clear();
            SharedMembers.Clear();
            return;
        }
        await LoadSharedDetail(value.Id);
    }

    private SyncClient CreateClient() => new(_settings.SyncServer, _settings.SyncToken);

    private SharedSync CreateSync(SyncClient client) => new(client, _library, _store, _backups, _hashCache);

    private string LinkKey(int playlistId) => $"{_settings.SyncServer.TrimEnd('/')}#{playlistId}";

    private void NotifySignIn()
    {
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(SignedInText));
        OnPropertyChanged(nameof(DisplayServer));
        OnPropertyChanged(nameof(SharedIsMine));
    }

    /// <summary>Runs a server operation with busy state and friendly errors. Returns false on failure.</summary>
    private async Task<bool> SharedOp(string busyText, Func<SyncClient, Task> work)
    {
        if (!IsSignedIn)
        {
            SignIn();
            if (!IsSignedIn) return false;
        }
        SharedBusy = true;
        SharedStatus = busyText;
        try
        {
            using var client = CreateClient();
            await work(client);
            return true;
        }
        catch (SyncException ex) when (ex.IsUnauthorized)
        {
            _settings.SyncToken = null;
            _settings.Save();
            NotifySignIn();
            SharedStatus = "Your sign-in expired. Sign in again.";
            Dialogs.Error("Your sign-in has expired or was revoked. Please sign in again.");
            return false;
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            SharedStatus = "Couldn't reach the server.";
            Dialogs.Error($"Couldn't reach {DisplayServer}. Is the site up?\n\n{ex.Message}");
            return false;
        }
        catch (Exception ex) when (ex is SyncException or TaskCanceledException or IOException or InvalidDataException)
        {
            SharedStatus = ex.Message;
            Dialogs.Error(ex.Message);
            return false;
        }
        finally
        {
            SharedBusy = false;
        }
    }

    [RelayCommand]
    private void SignIn()
    {
        var window = new SignInWindow(_settings.SyncServer) { Owner = System.Windows.Application.Current.MainWindow };
        if (window.ShowDialog() != true || window.Result is not { } r) return;
        _settings.SyncServer = r.Server;
        _settings.SyncToken = r.Token;
        _settings.SyncPlayerId = r.Player.Id;
        _settings.SyncPlayerName = r.Player.Name;
        _settings.Save();
        NotifySignIn();
        _ = RefreshShared();
    }

    [RelayCommand]
    private async Task SignOut()
    {
        if (!IsSignedIn) return;
        using (var client = CreateClient()) await client.LogoutAsync();
        _settings.SyncToken = null;
        _settings.Save();
        NotifySignIn();
        SharedPlaylists.Clear();
        SharedStatus = "Signed out.";
    }

    [RelayCommand]
    private async Task RefreshShared()
    {
        int? keep = SelectedShared?.Id;
        List<SharedPlaylistSummary>? list = null;
        if (!await SharedOp("Loading shared playlists…", async c => list = await c.GetPlaylistsAsync())) return;
        SharedPlaylists.Clear();
        foreach (var p in list!) SharedPlaylists.Add(p);
        SelectedShared = SharedPlaylists.FirstOrDefault(p => p.Id == keep) ?? SharedPlaylists.FirstOrDefault();
        SharedStatus = SharedPlaylists.Count == 0 ? "No shared playlists yet. Create one!" : $"{SharedPlaylists.Count} shared playlist(s).";
    }

    private async Task LoadSharedDetail(int id)
    {
        SharedPlaylist? detail = null;
        if (!await SharedOp("Loading playlist…", async c => detail = await c.GetPlaylistAsync(id))) return;
        await ShowSharedDetail(detail!);
        SharedStatus = "";
    }

    private async Task ShowSharedDetail(SharedPlaylist detail)
    {
        var entries = _library.Load();
        var local = await Task.Run(() => _hashCache.Index(entries));
        _localHashByPath = local.ToDictionary(kv => kv.Value.AudioPath, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
        _sharedHashes = detail.Songs.Select(s => s.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);

        int me = _settings.SyncPlayerId ?? -1;
        SharedSongs.Clear();
        foreach (var s in detail.Songs)
            SharedSongs.Add(new SharedSongRow(s, SharedSync.StateOf(s, local, me), s.AddedBy.Id == me || detail.CreatedBy.Id == me));
        SharedMembers.Clear();
        foreach (var m in detail.Members.OrderBy(m => m.Player.Name)) SharedMembers.Add(new SharedMemberRow(m, detail.SetCode));

        var present = detail.Songs.Where(s => local.ContainsKey(s.Hash)).Select(s => (s.Hash, local[s.Hash].Meta!)).ToList();
        LocalSetCode = present.Count == detail.Songs.Count ? SetCode.Compute(present) : null;
        SharedDetail = detail;
        ShareView.Refresh();
    }

    [RelayCommand]
    private async Task NewSharedPlaylist()
    {
        var name = InputWindow.Ask("New shared playlist", "Name (e.g. \"Challenge Week 1\"):", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        SharedPlaylist? created = null;
        if (!await SharedOp("Creating playlist…", async c => created = await c.CreatePlaylistAsync(name.Trim(), false))) return;
        await RefreshShared();
        SelectedShared = SharedPlaylists.FirstOrDefault(p => p.Id == created!.Id);
    }

    [RelayCommand]
    private async Task RenameSharedPlaylist()
    {
        if (SharedDetail is not { } d || !SharedIsMine) return;
        var name = InputWindow.Ask("Rename shared playlist", "Name:", d.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == d.Name) return;
        if (await SharedOp("Renaming…", c => c.UpdatePlaylistAsync(d.Id, name.Trim(), null))) await RefreshShared();
    }

    [RelayCommand]
    private async Task ToggleSharedShuffle()
    {
        if (SharedDetail is not { } d || !SharedIsMine) return;
        SharedPlaylist? updated = null;
        if (await SharedOp("Updating…", async c => updated = await c.UpdatePlaylistAsync(d.Id, null, !d.Shuffle))) await ShowSharedDetail(updated!);
    }

    [RelayCommand]
    private async Task DeleteSharedPlaylist()
    {
        if (SharedDetail is not { } d || !SharedIsMine) return;
        if (!Dialogs.Confirm($"Delete the shared playlist \"{d.Name}\" for everyone?\n\nSongs stay on everyone's PC, and the local \"{d.Name}{SharedSync.LocalNameSuffix}\" playlist isn't removed.")) return;
        if (await SharedOp("Deleting…", c => c.DeletePlaylistAsync(d.Id)))
        {
            _settings.SharedPlaylistLinks.Remove(LinkKey(d.Id));
            _settings.Save();
            await RefreshShared();
        }
    }

    [RelayCommand]
    private async Task ShareSongs(IList? selected)
    {
        if (SharedDetail is not { } d) return;
        var songs = (selected?.Cast<SongViewModel>() ?? []).Where(s => !s.HasError).ToList();
        if (songs.Count == 0)
        {
            Dialogs.Info("Select songs from your library on the right first (Ctrl/Shift-click to pick several).");
            return;
        }
        var dirty = songs.Where(s => s.IsDirty).ToList();
        if (dirty.Count > 0 && Dialogs.Confirm($"{dirty.Count} of these songs have unsaved edits. Save them first so the shared copy has them?"))
            foreach (var s in dirty) SaveSongCore(s);

        int me = _settings.SyncPlayerId ?? -1;
        var progress = new Progress<string>(t => SharedStatus = t);
        SharedPlaylist? updated = null;
        if (!await SharedOp("Uploading…", async c => updated = await CreateSync(c).ShareSongsAsync(d.Id, songs.Select(s => s.Entry).ToList(), me, progress))) return;
        await ShowSharedDetail(updated!);
        await SyncSharedCore(d.Id, quiet: true);
        SharedStatus = $"Shared {songs.Count} song(s) to \"{d.Name}\".";
    }

    [RelayCommand]
    private async Task RemoveSharedSongs(IList? selected)
    {
        if (SharedDetail is not { } d) return;
        var rows = (selected?.Cast<SharedSongRow>() ?? []).ToList();
        var allowed = rows.Where(r => r.CanRemove).ToList();
        if (allowed.Count == 0)
        {
            if (rows.Count > 0) Dialogs.Info("You can only remove songs you added (or any song from a playlist you created).");
            return;
        }
        if (!Dialogs.Confirm($"Remove {allowed.Count} song(s) from \"{d.Name}\" for everyone?\n\nThe songs stay in everyone's library.")) return;
        if (await SharedOp("Removing…", async c => { foreach (var r in allowed) await c.RemoveSongAsync(d.Id, r.Song.Hash); }))
            await LoadSharedDetail(d.Id);
    }

    [RelayCommand]
    private async Task SyncShared()
    {
        if (SharedDetail is { } d) await SyncSharedCore(d.Id, quiet: false);
    }

    /// <summary>Syncs one shared playlist and refreshes the library/playlist views.</summary>
    private async Task<SyncResult?> SyncSharedCore(int playlistId, bool quiet)
    {
        if (!quiet && !ConfirmDiscardOrSave("syncing")) return null;
        int me = _settings.SyncPlayerId ?? -1;
        var progress = new Progress<string>(t => SharedStatus = t);
        SyncResult? result = null;
        string key = LinkKey(playlistId);
        bool ok = await SharedOp("Syncing…", async c =>
        {
            var (r, guid) = await CreateSync(c).SyncAsync(playlistId, me, _settings.SharedPlaylistLinks.GetValueOrDefault(key), progress);
            result = r;
            _settings.SharedPlaylistLinks[key] = guid;
            _settings.Save();
        });
        if (!ok || result is null) return null;

        if (!HasUnsavedChanges) ReloadAll();
        if (SelectedShared?.Id == playlistId) await LoadSharedDetail(playlistId);

        string summary = $"Synced: {result.Downloaded} downloaded, {result.Updated} timing updated, {result.Pushed} of your edits uploaded. " +
                         (result.InSync ? $"Set code {result.LocalSetCode} ✓ matches." : $"Set code {result.LocalSetCode} ≠ server {result.ServerSetCode}.");
        SharedStatus = summary;
        Status = summary;
        if (!quiet && result.Warnings.Count > 0) Dialogs.Info(summary + "\n\n" + string.Join("\n", result.Warnings));
        if (!quiet && GamePaths.IsGameRunning()) Status += " Restart the game to see the changes.";
        return result;
    }

    /// <summary>Called once after startup: refreshes and syncs every shared playlist this PC has synced before.</summary>
    public async Task AutoSyncOnStartup()
    {
        if (!IsSignedIn) return;
        await RefreshShared();
        if (!AutoSyncShared || SharedPlaylists.Count == 0 || HasUnsavedChanges) return;
        string prefix = _settings.SyncServer.TrimEnd('/') + "#";
        var linked = SharedPlaylists.Where(p => _settings.SharedPlaylistLinks.ContainsKey(prefix + p.Id)).ToList();
        int synced = 0;
        foreach (var p in linked)
            if (await SyncSharedCore(p.Id, quiet: true) is not null) synced++;
        if (synced > 0) Status = $"Synced {synced} shared playlist(s) on startup. " + SharedStatus;
    }
}
