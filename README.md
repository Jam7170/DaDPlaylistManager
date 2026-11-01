# Dead as Disco Playlist Manager

> [!WARNING]
> **Vibe coded.** This project was made entirely with AI (Claude Code), from investigating the game's file formats to the code, tests and this README. A human chose the features and tested it in the game, but nobody has reviewed the code line by line. It edits the game's save data, so it backs files up before changing them, but use it at your own risk and keep your own backups of `%LOCALAPPDATA%\Pagoda\Saved`. It's a fan-made tool, not affiliated with Brain Jar Games.

A Windows app for managing **imported songs** and **custom playlists** in *Dead as Disco*.

- **Playlists:** create, rename, duplicate and delete playlists. Add songs by drag and drop, reorder them, sort, and toggle shuffle.
- **Song editor:** edit title, artists, BPM, first-beat offset, start/end trims, tempo changes and level seed. Optionally keep folder names in sync with the title and artist.
- **Preview with beat clicks:** play any song with a metronome overlay generated from its timing, so you can hear whether the BPM and offset line up. Edits are heard live.
- **Import audio:** turn mp3, wav, flac, m4a, ogg and other formats into game songs. Titles and artists come from the file's tags, and BPM and first beat are estimated automatically. You can also drag files onto the window.
- **Share song packs:** export songs, or a whole playlist with its songs, as a `.dadpack` file. Importing a pack skips songs you already have, fixes ID clashes, and rewrites machine-specific paths.
- **Shared playlists:** sync playlists with friends through the group website (heroicgames.uk by default). Everyone adds songs from their own library and presses **Sync**. Each player then has the same songs, the same timing and the same playlist in the game, plus a **set code** to check everyone's setup matches before a challenge.
- **Safe by default:** before the app changes a file, it copies it to `%LOCALAPPDATA%\DaDPlaylistManager\Backups` (once per session). Deleted songs go to the Recycle Bin. The app also warns you if the game is running.

## Install

Download **[DaDPlaylistManager.exe](https://github.com/Jam7170/DaDPlaylistManager/releases/latest/download/DaDPlaylistManager.exe)** from the [latest release](https://github.com/Jam7170/DaDPlaylistManager/releases/latest) and run it. (Windows may show a SmartScreen warning because the exe isn't code-signed; choose **More info → Run anyway**.) There's no installer and nothing else to install: the .NET runtime and ffmpeg are both built in.
The app finds the game's data automatically at `%LOCALAPPDATA%\Pagoda\Saved`. If yours is elsewhere, use **Tools → Change game data folder**.

> The game reads songs and playlists when it starts. **Restart Dead as Disco** to see changes.

## Shared playlists

1. Open the **Shared** tab and **sign in** with your website name and password. The app keeps only the site's login token, encrypted for your Windows account.
2. Click **New** to create a shared playlist, or pick an existing one.
3. Select songs under **Share from your library** and click **Share selected**. Their audio and settings are uploaded.
4. Click **Sync**. This downloads songs you're missing, applies the shared timing, uploads any edits you've made to songs you shared, and writes a game playlist called `<name> (shared)`.
5. Compare **set codes**. When everyone's code matches, everyone has exactly the same songs, BPM, offsets and level seeds.

Rules: each song's settings belong to whoever shared it. If you edit a song someone else shared, the next sync puts their version back (your old copy goes to the backups folder). You can remove songs you added. A playlist's creator can remove any song from it, rename it, turn shuffle on or off, or delete it. With **Sync on startup** turned on, the app syncs every shared playlist you've synced before each time it opens. Restart the game afterwards to see the changes.

The server side lives in the website project. It provides `/api/dad/...` endpoints, behind the site's normal login, and stores the audio in its data folder.

## How the game stores things

| What | Where | Format |
|---|---|---|
| Imported songs | `Saved\ImportedSongs\<Artist - Title>\` | `Audio.ogg` (Vorbis, 44.1 kHz stereo) + `Meta.json` |
| Playlists | `Saved\Playlists\Playlist_<GUID>.bjpl` | UE5 tagged properties (see below) |

`Meta.json` fields: `uniqueId` (uint32, the key playlists use), `songName`, `performedBy[]`, `writtenBy[]`, `seed`, `tempo` (BPM), `beatOffset` (ms to the first beat), `startSongOffset` / `endSongOffset` (seconds trimmed from the start and end), `customTempoSections[]` (`{tempo, startAbsoluteTime}`), `originalAudioFileHash` (MD5 of `Audio.ogg`) and `originalAudioFilePath`.

`.bjpl` layout (version 2, `CustomSerializeImportedSongs`):

```
int32  version (2)
uint8  serialization control (0)
tagged properties until "None":
  UniqueID        StructProperty<Guid>   (also the file name)
  Name            StrProperty
  bShouldShuffle  BoolProperty            (only written when true)
  Songs           ArrayProperty<ObjectProperty>  (object paths as strings)
uint32 0
int32  N, then N × { uint32 song uniqueId, int32 index into Songs }
```

Imported songs are resolved through the trailing id/index table. The object paths point at transient objects that only exist while the game is running. Entries the app doesn't recognise, such as built-in songs, are kept exactly as they are.

## Building from source

Requires the .NET 10 SDK.

```powershell
dotnet run --project src\DaDPlaylistManager             # run from source (uses ffmpeg on PATH)
dotnet run --project src\DaDPlaylistManager -- --data C:\path\to\copy\of\Saved   # work on a copy
dotnet test                                            # unit tests
.\build.ps1                                            # portable exe in .\dist (downloads LGPL ffmpeg to embed)
.\build.ps1 -FfmpegPath C:\tools\ffmpeg.exe            # embed a specific ffmpeg
.\build.ps1 -NoFfmpeg                                  # smaller exe without ffmpeg
```

Project layout:

- `src/DaDPlaylistManager.Core`: file formats (`.bjpl`, `Meta.json`), the song library, song packs, the ffmpeg wrapper and the BPM detector. No UI.
- `src/DaDPlaylistManager`: the WPF app (Fluent dark theme, MVVM, NAudio for playback).
- `tests/DaDPlaylistManager.Tests`: unit tests, including a byte-exact round trip of any playlists found on the machine.

## Licences

The app's code is yours to license. The embedded ffmpeg is LGPL ([ffmpeg.org](https://ffmpeg.org/legal.html)); its licence file ships next to the exe. Playback uses NAudio and NVorbis (MIT). This project is not affiliated with Brain Jar Games.
