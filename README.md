# Emby Distributed Transcoding

Version française : [README.fr.md](README.fr.md)

**New here? Read the [User guide](docs/USER-GUIDE.md).**

**Emby Distributed Transcoding** lets an Emby Server — even a small NAS without a GPU — hand its heavy video work to the Windows PCs of the household. Playback transcodes start instantly on the server and move to a worker at an exact segment boundary, with no stall. Offline conversions ("Convert" / downloads) are split into frame-exact slices encoded in parallel by every available PC (and the server itself), then reassembled and verified to the frame. Workers come and go freely (sleep, shutdown, gaming): unfinished work resumes elsewhere, and Emby always falls back to its own ffmpeg. Everything ships as a single plugin DLL plus a Windows tray app; Emby itself is not modified.

Offloads Emby's playback transcodes and conversions to Windows PCs ("workers"). When no worker is available, Emby transcodes by itself, exactly as it would without the plugin. Hand-overs between a worker and the server happen at an exact segment boundary, without interrupting playback.

Full documentation (detailed operation, installation, operations, troubleshooting, tests), currently in French only: `final/Documentation-Transcodage-distribue.pdf`.

## Components

| Project (`src/`) | Role | Deliverable |
|---|---|---|
| `RemoteTranscode.EmbyPlugin` | Emby plugin: admin page, API, detection of the Emby installation, hosting of the coordinator, interception of Emby's ffmpeg | `RemoteTranscode.EmbyPlugin.dll` (single DLL, about 46 MB) |
| `RemoteTranscode.Coordinator` | Dispatches jobs to workers, serves media and receives segments; runs inside the Emby process (or standalone for development) | compiled into the plugin DLL |
| `RemoteTranscode.Shim` | `remote-ffmpeg` relay that Emby launches instead of its ffmpeg; sends the work to the coordinator and takes over locally when needed | embedded in the DLL for linux-x64, linux-arm64, linux-musl-x64 and win-x64 |
| `RemoteTranscode.Worker` | Windows app (notification area): availability, ffmpeg execution, local source cache, segment upload, uninstallation (`--uninstall`) | `EmbyRemoteWorker.exe` |
| `RemoteTranscode.Installer` | Worker installer, no administrator rights required; a plain update when the worker is already installed | `EmbyRemoteWorker-Setup-<version>.exe` |
| `RemoteTranscode.Common` | Shared code: protocol, parsing of Emby's arguments, conversion slicing, argument allow-list, availability engine | — |
| `RemoteTranscode.Tests` | Unit tests (xUnit) | — |

## How it works

### Interception

The plugin configures Emby so that it launches a **dispatcher** instead of its ffmpeg (the "custom ffmpeg path" setting; the administrator's own value is saved and later restored). The dispatcher starts the `remote-ffmpeg` relay, which reads Emby's command:

- **probes, thumbnails, remuxes, audio-only conversions**: Emby's ffmpeg, immediately, unchanged;
- **playback with video re-encoding** (HLS) and **conversion with video re-encoding**: the coordinator decides where the work is done.

Safeguards: relay self-test at Emby startup, kill switch (`disabled` file), allow-list of ffmpeg arguments, unknown commands left to the server. If anything fails, Emby falls back to its own ffmpeg.

### Playback

1. **On click**, the coordinator chooses where to start:
   - **directly on a worker** when the server could not keep up: HDR tone mapping, source above 1080p (more than 1088 lines), or a movie for which the server has already been measured too slow (below 1.2x, remembered per movie for 7 days);
   - the coordinator answers within 1.2 s at most (the analysis of the movie continues in the background for subsequent playbacks);
   - **otherwise on the server**: first segment in one to two seconds.
2. **Start on the server**: the worker that will take over is told to **prefetch** into its cache the movie's header and index and the data from the start point to beyond the hand-over point (temporary authorization for that single file). Once the server has encoded about **20 s ahead**, or as soon as it proves too slow (below 1.2x, or no first segment within 5.5 s: 3 s of ffmpeg startup plus one segment at 1.2x), playback moves to the worker. A server that has not produced any segment yet is stopped on the spot.
3. **Hand-overs at the exact segment**: the relay stops the running ffmpeg after a complete segment and the rest resumes at the exact end of that segment, on a worker or on the server. This applies to the fast start, to a worker being released or lost (detected within 15 to 17 s), and to the return to a worker when it becomes available again. A worker whose ffmpeg fails is excluded from that playback for 10 minutes.
4. **Same encoder as the server** by default, for invisible joins; encoding on the worker's GPU is an option.
5. **Text subtitles** requested by the web player (WebVTT outputs) are extracted on the server, the video on the worker.
6. Playbacks have **priority** over conversions: when a playback is not progressing fast enough on a worker, a conversion running on that worker is moved away to make room (the playback itself stays on that worker).

### Conversions

1. The source is split into **slices** of about 2 minutes, shorter at the end (60, 60, 30, 30 s). Each boundary is placed exactly on a frame, read by ffprobe: no frame lost or duplicated. Exception: a source whose packets have no presentation timestamps (DivX/Xvid AVI with packed B-frames) is not split, because runs that seek into it do not meet on the same frame; it is converted as a single slice.
   - A conversion is **accepted as soon as a capable worker is connected or the server can encode its slices**: the server starts alone if needed, and every 10 s pending slices are offered to workers again; a PC switched on mid-episode joins in immediately. Emby converts by itself, as without the plugin, only if neither the workers nor the server can encode the slices.
   - HDR → SDR: Emby's `supertonemap` filter is translated into standard filters. Since plugin 0.7.26 and worker 0.7.15, when a connected worker has validated **GPU tone mapping** (libplacebo through Vulkan, 2 to 2.4 times faster, about 8 % CPU instead of about 50 %), the coordinator chooses it when the first slice starts (since 0.7.29; 60 s of grace after a restart, for the workers to come back) and freezes it for the whole episode: only workers with that capability take its slices (never the server, whose ffmpeg has no libplacebo), and if none is connected for `EncodeNoWorkerMinutes` the conversion is given back to Emby. Otherwise `zscale` + `tonemap` on the processor, the same way on workers and on the server. The two are never mixed in one file. The worker validates the GPU picture at start-up against the processor chain (PSNR), because some drivers (Intel iGPU) return a corrupted picture without any error.
   - **Remuxes** (video copied, only the audio converted) stay on the server: there is nothing to compute, and transferring to a worker would cost more than the remux. With Emby's "full-speed conversion" setting (`EnableFullSpeedTranscoding`), they take a few minutes instead of real time.
2. Each available worker takes **one slice at a time**. While it encodes it, the **next slice is reserved for it** and its data is **prefetched** into its cache. About 6 s before the end of its slice, it receives the next one and **already launches its ffmpeg** (`EncodeOverlapSeconds`): the startup (about 4 s) does not leave the encoder idle.
   - The **server also encodes slices in parallel** with the workers (`EncodeLocalAlongsideWorkers`), one at a time, never a slice reserved for a worker. It only passes its slice to a free worker once nothing is left pending.
   - The worker window (0.7.10, refreshed every 30 s) and the plugin page show the **number of items remaining in Emby's conversion queue**, read by the DLL inside Emby itself.
   - The plugin page opens in a browser (desktop or mobile), not in the Emby Android app, which does not run the JavaScript of third-party plugins.
3. Toward the end of a conversion, the coordinator takes into account the **measured speed** of each worker: it does not give a slice to a slow worker (or to the server) if a faster one would clearly finish it first.
4. An interrupted slice (worker released or lost) resumes at its first missing chunk (from the source frame, read by ffprobe, where the previous run ended: ffmpeg lists chunk times on its own frame grid, up to half a frame off when the video does not start on it), on another worker, or on the server if no worker has been free for 20 s, then returns to a worker.
5. When all slices are ready, the coordinator first reads where each seeking worker run really starts (a run more than 3/4 of an output frame off, or one starting 10 s or more into the file that cannot be placed, rejects the conversion). The server then **assembles** the file with Emby's command (video copied, Emby's audio and subtitles) and **verifies** it, rejecting it when in doubt: total duration, video duration **to the frame** (one and a half frames of tolerance, three for a source without presentation timestamps), then, since 0.7.36, an **A/V sync check over the whole file** (`SyncCheck`). It reads packet timestamps with Emby's ffprobe, in windows of 1.5 s at the chunk boundaries (all of them, up to 1200), at least the file's quarters, each stream's start and the end, and compares video and each audio stream: within 6 ms for video and 3 ms for audio where positions are exact, within half an output frame plus 3 ms where the frame rates differ or the source is off its grid. A value that is missing or unreadable rejects the file: it is deleted and the conversion is redone by Emby's ffmpeg. A command whose result could not be compared (time offsets, seeks, audio filters working on time, video filters moving frames, unmeasured containers or audio encoders, maps not written `0:N`) stays on Emby's ffmpeg from the start.
   - The `concat.txt` assembly list gives the **exact duration of each chunk** (`duration` directive of the concat format, taken from the segment lists: a chunk lasts up to the start of the next one, and a slice's last chunk up to the slice's end when its listed end is within 20 ms of it, so each slice lasts exactly its length). Without it, Emby's ffmpeg 5.1 sometimes estimated an MPEG-TS chunk one frame too short: the next chunk overlapped and the video ended frames too early (seen on a 4K episode: 10 frames, 0.42 s, rejected by the frame-exact check).
   - A slice never ends with a chunk shorter than 2 s (its chunks are lengthened).
   - The coordinator log records the assembled chunks (`assembled from chunks`).
6. GPU encoding available (QVBR for Intel QSV, `-cq` for NVIDIA NVENC), disabled by default. A single episode can therefore mix chunks from different encoders (QSV, NVENC, x264): their decoding parameters differ, but the changes happen at keyframes, as in adaptive streaming; full decoding without errors and playback verified on a Samsung TV.

### Monitoring and diagnostic

- **Monitoring page** « Suivi du transcodage » in Emby's dashboard (in French, administrators): workers, conversions in progress, history with the reason of each failure (the 100 most recent, kept 7 days) and, when a conversion was handed back to Emby, the check of its file, delivered either way, Emby's queue length, refreshed every 5 s while shown. It also holds the switch of the audio converted ahead (`EncodeAudioAhead`, on by default; a server that already saved its settings keeps its value), for the conversions captured from then on.
- **Library diagnostic** « Diagnostic de la bibliothèque » (in French, administrators): a scheduled task that only runs when started by hand, and its page, list the video files at risk for a slice-based conversion. One file at a time, lowest priority, paused during playbacks and conversions, resumable; about 40 s read per file (start, five passages, end), so a file not listed may still hold a defect elsewhere. Files are never modified.

### Automatic failure reports

When a conversion fails (assembly rejected by the check, a worker failing a slice, conversion given back to Emby), the plugin opens an issue on this repository, or comments the open one with the same signature, so the problem can be fixed. Anonymous: plugin, Emby and worker versions, the server's OS and architecture, workers described by version and GPU model only, the error message with every path removed, the source's technical characteristics (container, codecs, profile, bit depth, resolution, frame rate, start time, duration, HDR, audio tracks), the conversion profile (encoder, Emby's filter names), which type of machine encoded each slice, the `assembled from chunks` summary and the check line. Never sent: path, file name, title, domain, IP address, host, PC, worker or user name, Emby id. The source is designated by an anonymous code, linked to the file only in this server's Emby log; the installation by the first 8 characters of a random id. At most 5 reports a day per installation, never twice for the same conversion; sent in the background, any network error is ignored. Not sent when no capable worker was connected, when Emby cancels or on a restart. Turn it off on the plugin page (« Signaler automatiquement les échecs de conversion sur GitHub (anonyme) »). Only the builds published by GitHub Actions carry the report token (a fine-grained token limited to this repository's issues); local builds and forks never send anything.

### Worker

- **Availability**: manual or automatic (idle PC), time windows, battery, load left by other applications (CPU, GPU, memory) with hysteresis; « Libérer le PC » (Release the PC) hands jobs back immediately, draining lets running playbacks finish.
- **Local cache** of sources (4 MB blocks, at most 4 GB and a quarter of the free space): ffmpeg reads the movie through it, which enables prefetching and avoids re-reading the header and index on each resume.
- **Update**: the installer asks the worker to close cleanly (its jobs resume elsewhere) and only forces it as a last resort (old version without this signal, or no exit within 20 s); it replaces the files while keeping the folder, settings and pairing, then restarts it.
- **Automatic update** (since worker 0.7.20, on by default, box « Mises à jour » in the settings): the worker reads the latest release of this repository, compiled into it, at start and then about every 6 h, takes a setup only with the expected name and a strictly newer version, checks its SHA-256, and runs it with `--quiet` after 2 minutes without a job. Off: no contact with GitHub. The server can only ask for an earlier check. The setups are not code-signed, so this trusts the project's GitHub account and repository. A worker older than 0.7.20 must be updated by hand once.

### Security

Each worker is paired with a code (a token of which only the hash is stored), each job has its own token, and prefetching uses a three-minute authorization limited to one file. Workers connect over TLS (direct or behind the reverse proxy); 10 authentication failures within 10 minutes block the address for 15 minutes. The server's secrets are readable only by the Emby user.

## Installation

- **Plugin**: copy `RemoteTranscode.EmbyPlugin.dll` into Emby's `plugins` folder, restart Emby, then configure the plugin under « Tableau de bord » (Dashboard) → « Transcodage distribué » (Distributed transcoding) (details in `docs/02-installation.md`).
- **Synology without SSH**: a generic DSM task "Installer plugin RemoteTranscode" (user root, disabled, run on demand) stitches back the `plugin.part0`..`plugin.part5` chunks dropped together with a `plugin.sha256` file into `docker/emby-remote/plugin`, then runs `tools/nas/install-plugin.sh` with that hash; log `install-<date>.log` in the same folder. For a new version, just drop the files and run the task: the task itself is not modified, so no DSM password needs to be entered.
- **Worker**: `EmbyRemoteWorker-Setup-<version>.exe`, no administrator rights required; run again on a PC that already has it, it performs a plain update.

## Documentation

- Version history: `CHANGELOG.md`.
- Detailed operation, installation, operations, troubleshooting: `final/Documentation-Transcodage-distribue.pdf` (French).
- Findings on Emby 4.10: `docs/01-audit-emby-4.10.md`.
- Installation, update, uninstallation: `docs/02-installation.md`.
- Local test bench and results: `test/local/README.md` and `docs/03-tests-acceptation-local.md`.

## Building

Prerequisites: Windows x64, .NET 8 SDK (`dotnet --list-sdks`). The Emby references live in `research/emby-4.10.2.0`, copied from a 4.10.2 server. NuGet packages go through the repository's `nuget.config`.

```powershell
# Build everything and run the tests
dotnet build src\RemoteTranscode.sln -c Release
dotnet test  src\RemoteTranscode.Tests -c Release

# Emby plugin (relay published for each platform, compressed and embedded)
pwsh tools\build-plugin.ps1                # -> artifacts\plugin\RemoteTranscode.EmbyPlugin.dll

# Windows worker, with FFmpeg 9.0.2 "full" from gyan.dev (libplacebo for GPU tone mapping), verified by SHA-256
pwsh tools\package-worker.ps1              # -> artifacts\worker-package\EmbyRemoteWorker\ and .zip
pwsh tools\build-installer.ps1             # -> artifacts\installer\EmbyRemoteWorker-Setup-<version>.exe
```

`build-plugin.ps1` embeds the token of the automatic failure reports only when one is given (`-IssueReportToken`, or the `ISSUE_REPORT_TOKEN` environment variable that GitHub Actions sets from the repository secret of the same name); without it the built plugin never reports. The token is never stored in the repository.

`package-worker.ps1` works in `artifacts\worker-package` and refuses to produce a package whose executable does not have the expected version (a worker running from the output folder would keep its old executable).

Code signing (possible, not planned): `build-installer.ps1` accepts `-CertThumbprint <thumbprint>` or `-PfxPath <file.pfx>` and signs then timestamps the worker and the installer. Lacking a trusted certificate (a deliberate choice for personal use), the deliverables are not signed.

Versions are set in `src/RemoteTranscode.EmbyPlugin/RemoteTranscode.EmbyPlugin.csproj`, `src/RemoteTranscode.Worker/RemoteTranscode.Worker.csproj` and `tools/build-installer.ps1`. The name of the plugin pages contains its version, so that browsers do not keep an old page.

## Versions

| Item | Version |
|---|---|
| Plugin | 0.7.38 |
| Worker and installer | 0.7.20, with icon, GPU decoding and GPU tone mapping (needs Resizable BAR on NVIDIA and AMD cards), logs kept 7 days (FFmpeg "full" build: installer of about 218 MB instead of 170 MB), automatic update |
| Minimum worker | 0.7.19 for conversions: an older worker, or one with an unknown version, gets no slice and still runs playbacks. The refusal shows in the status API, on the plugin and monitoring pages and in the log. |
| Emby Server tested | 4.10.2.0 (Synology DS1522+, DSM 7.4.1, AMD Ryzen R1600 without GPU); Emby's ffmpeg 5.1 (fork) |
| Client tested | Emby Web in Chrome (hls.js player 1.6.0-beta.2) |
| Workers tested | Windows 11 Enterprise, Intel Core Ultra 5 225U (x264 and QSV); PC with an RTX 4080 (x264 and NVENC, about 10x on a 1080p episode); FFmpeg 9.0.2 |
| .NET | 8 (Emby ships its own .NET 8 and ASP.NET Core runtime) |

## Known limitations

- The sync check reads timestamps, not picture or sound content, and only in its windows: a fault that starts and ends between two windows is not seen. Where Emby's `-r` differs from the source's rate, or the source is off its own grid, a video shift of up to half an output frame plus 3 ms is accepted, because ffmpeg's frame grid does that on purpose. An AVI with packed B-frames is compared on decoding times, and its end to about 3 frames. A file converted by Emby's own command does not go through this check. Conversions of real AVI and MPEG-TS files have not been run with 0.7.36.
- MPEG-TS sources and AVI with packed B-frames are converted as one slice, so without the gain of several workers. An interrupted run still resumes with a seek, a case that was not measured.
- Not yet tried on Emby for Windows, Emby under Docker or Linux arm64. The relay is embedded for these platforms, and a startup self-test keeps Emby on its own ffmpeg if the relay cannot run.
- The plugin relies on Emby's "custom ffmpeg path" setting, modified through reflection. Emby marks this setting as unsupported, and a future Emby version could change it. The plugin detects this and then leaves everything untouched.
- Playback started at the very beginning of the movie and then resumed elsewhere: the timestamps of the resumed segment step back by 80 ms (ffmpeg behaviour); no effect observed in Chrome.
- The local test bench cannot exercise what depends on Emby's ffmpeg (start on the server, WebVTT subtitles): those parts are validated on the real server.
- Conversions with burned-in subtitles are still done by the server.
- The installer and the worker are not signed: a corporate antivirus may block them. The installer avoids the behaviours they consider suspicious (no self-copy, clean shutdown of the worker), but only code signing provides a guarantee. The build script can sign (see "Building"); for personal use, no certificate was purchased.
- With a worker reached over the internet, segment throughput depends on the PC's connection.
- The original specification asked for jobs to be queued when no worker is available; at the user's request, the server instead transcodes by itself immediately, then hands the job back to a worker as soon as one returns.
