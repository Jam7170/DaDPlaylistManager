using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DaDPlaylistManager.Core.Sync;

public sealed record SyncPlayer(int Id, string Name);

public sealed record LoginPlayer(int Id, string Name, bool Claimed);

public sealed record SharedPlaylistSummary(int Id, string Name, bool Shuffle, SyncPlayer CreatedBy, string CreatedAt, string UpdatedAt, int SongCount, string SetCode);

public sealed record SharedSong(string Hash, int Position, long Size, SyncPlayer AddedBy, string AddedAt, SyncPlayer UploadedBy, string? MetaUpdatedAt, JsonObject Meta);

public sealed record SharedMember(SyncPlayer Player, string? LastSyncedAt, string? SetCode);

public sealed record SharedPlaylist(int Id, string Name, bool Shuffle, SyncPlayer CreatedBy, string CreatedAt, string UpdatedAt, string SetCode,
    List<SharedSong> Songs, List<SharedMember> Members);

public sealed record SharedSongInfo(string Hash, long Size, SyncPlayer? UploadedBy, string? MetaUpdatedAt, bool HasAudio, JsonObject? Meta);

public sealed class SyncException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;
}

/// <summary>
/// Client for the shared-playlist API on the group's website (/api/dad). Authenticates with the
/// site's own session cookie, obtained by logging in with a player's name and password.
/// </summary>
public sealed class SyncClient : IDisposable
{
    public const string SessionCookie = "hg_session";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public SyncClient(string serverUrl, string? sessionToken = null)
    {
        ServerUrl = serverUrl.TrimEnd('/');
        _http = new HttpClient(new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All })
        {
            BaseAddress = new Uri(ServerUrl + "/"),
            Timeout = TimeSpan.FromMinutes(10),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DaDPlaylistManager", "1.1"));
        SessionToken = sessionToken;
    }

    public string ServerUrl { get; }

    /// <summary>The site's session token (the value of the hg_session cookie).</summary>
    public string? SessionToken { get; private set; }

    public Task<List<LoginPlayer>> GetLoginPlayersAsync(CancellationToken ct = default) =>
        SendAsync<List<LoginPlayer>>(HttpMethod.Get, "api/auth/players", null, ct);

    /// <summary>Logs in and keeps the session token. Returns the signed-in player.</summary>
    public async Task<SyncPlayer> LoginAsync(int playerId, string password, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/auth/login") { Content = JsonContent.Create(new { playerId, password }, options: Json) };
        using var res = await _http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        SessionToken = ReadSessionCookie(res) ?? throw new SyncException("The server didn't start a session. Is this the right website?");
        return await GetMeAsync(ct);
    }

    public Task<SyncPlayer> GetMeAsync(CancellationToken ct = default) => SendAsync<SyncPlayer>(HttpMethod.Get, "api/auth/me", null, ct);

    public async Task LogoutAsync()
    {
        try { await SendAsync<JsonObject>(HttpMethod.Post, "api/auth/logout", new { }, CancellationToken.None); }
        catch { }
        SessionToken = null;
    }

    public Task<List<SharedPlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default) =>
        SendAsync<List<SharedPlaylistSummary>>(HttpMethod.Get, "api/dad/playlists", null, ct);

    public Task<SharedPlaylist> GetPlaylistAsync(int id, CancellationToken ct = default) =>
        SendAsync<SharedPlaylist>(HttpMethod.Get, $"api/dad/playlists/{id}", null, ct);

    public Task<SharedPlaylist> CreatePlaylistAsync(string name, bool shuffle, CancellationToken ct = default) =>
        SendAsync<SharedPlaylist>(HttpMethod.Post, "api/dad/playlists", new { name, shuffle }, ct);

    public Task<SharedPlaylist> UpdatePlaylistAsync(int id, string? name, bool? shuffle, CancellationToken ct = default) =>
        SendAsync<SharedPlaylist>(HttpMethod.Patch, $"api/dad/playlists/{id}", new { name, shuffle }, ct);

    public Task DeletePlaylistAsync(int id, CancellationToken ct = default) =>
        SendAsync<JsonNode>(HttpMethod.Delete, $"api/dad/playlists/{id}", null, ct);

    public Task<SharedPlaylist> AddSongsAsync(int id, IEnumerable<string> hashes, CancellationToken ct = default) =>
        SendAsync<SharedPlaylist>(HttpMethod.Post, $"api/dad/playlists/{id}/songs", new { hashes = hashes.ToArray() }, ct);

    public Task RemoveSongAsync(int id, string hash, CancellationToken ct = default) =>
        SendAsync<JsonNode>(HttpMethod.Delete, $"api/dad/playlists/{id}/songs/{hash}", null, ct);

    public Task<SharedPlaylist> ReportSyncedAsync(int id, string setCode, CancellationToken ct = default) =>
        SendAsync<SharedPlaylist>(HttpMethod.Post, $"api/dad/playlists/{id}/synced", new { setCode }, ct);

    /// <summary>Song info, or null if the server doesn't have this song.</summary>
    public async Task<SharedSongInfo?> GetSongAsync(string hash, CancellationToken ct = default)
    {
        try
        {
            return await SendAsync<SharedSongInfo>(HttpMethod.Get, $"api/dad/songs/{hash}", null, ct);
        }
        catch (SyncException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task UploadAudioAsync(string hash, string audioPath, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(audioPath);
        using var req = new HttpRequestMessage(HttpMethod.Put, $"api/dad/songs/{hash}/audio") { Content = new StreamContent(fs) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        AddAuth(req);
        using var res = await _http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
    }

    public Task<SharedSongInfo> UploadMetaAsync(string hash, JsonObject meta, CancellationToken ct = default) =>
        SendAsync<SharedSongInfo>(HttpMethod.Put, $"api/dad/songs/{hash}/meta", new { meta }, ct);

    /// <summary>Downloads a song's audio to <paramref name="destination"/> (via a temp file).</summary>
    public async Task DownloadAudioAsync(string hash, string destination, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/dad/songs/{hash}/audio");
        AddAuth(req);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(res, ct);
        string tmp = destination + ".download";
        await using (var fs = File.Create(tmp))
            await res.Content.CopyToAsync(fs, ct);
        File.Move(tmp, destination, overwrite: true);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        AddAuth(req);
        using var res = await _http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        if (res.StatusCode == HttpStatusCode.NoContent || res.Content.Headers.ContentLength == 0) return default!;
        return await res.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new SyncException("The server sent an empty response.");
    }

    private void AddAuth(HttpRequestMessage req)
    {
        if (SessionToken is not null) req.Headers.Add("Cookie", $"{SessionCookie}={Uri.EscapeDataString(SessionToken)}");
    }

    private static string? ReadSessionCookie(HttpResponseMessage res)
    {
        if (!res.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
        foreach (var c in cookies)
        {
            var first = c.Split(';')[0].Trim();
            if (first.StartsWith(SessionCookie + "=", StringComparison.Ordinal))
                return Uri.UnescapeDataString(first[(SessionCookie.Length + 1)..]);
        }
        return null;
    }

    private static async Task EnsureSuccess(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        string message = $"{(int)res.StatusCode} {res.ReasonPhrase}";
        try
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            // The site reports errors as { "error": "..." }.
            if (JsonNode.Parse(text) is JsonObject o && o["error"]?.GetValue<string>() is { Length: > 0 } err) message = err;
        }
        catch
        {
            // Not JSON; keep the status text.
        }
        throw new SyncException(message, res.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}
