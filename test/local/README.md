# Local test bench — distributed transcoding (without Emby, without NAS)

Version française : [README.fr.md](README.fr.md)

Acceptance tests (specification §11) run entirely on this PC: a standalone coordinator, two or
three **test workers** (separate instances of `EmbyRemoteWorker.exe`) and the `remote-ffmpeg` relay launched with
arguments copied from real Emby 4.10 commands. Report: `C:\emby\docs\03-tests-acceptation-local.md`.

## Safety of the production worker

* The production worker (`C:\emby\artifacts\worker\EmbyRemoteWorker\EmbyRemoteWorker.exe`, data in
  `%LOCALAPPDATA%\EmbyRemoteWorker`) is **never** touched: each test worker has its own
  `EMBY_WORKER_HOME` (`work\workers\w1`, `w2`, `w3`), hence its own configuration, its own mutex and its own
  `release` command.
* Only the processes started by the bench are stopped, **by PID** (`work\pids.txt`, with a check of the name and
  start time). After each test, the bench checks that the production worker's PID is still there.
* Ports: 38097 (workers, HTTPS, loopback) and 38098 (relay/administration, loopback).

## Prerequisites

* .NET 8 SDK (`C:\Program Files\dotnet\dotnet.exe`) and the local `nuget.config` of `C:\emby`.
* Worker ffmpeg: the "full" 9.0.2 build (libplacebo, as bundled with worker 0.7.15): winget's if it is the full build, else `C:\emby\wip\gputonemap\ffmpeg-full\...\ffmpeg.exe`, else `C:\emby\artifacts\worker\EmbyRemoteWorker\ffmpeg\ffmpeg.exe` (media generation
  and encoding on the test workers).
* "Server" ffmpeg/ffprobe: upstream Gyan 9.0.2 build installed by winget
  (`%LOCALAPPDATA%\Microsoft\WinGet\Packages\Gyan.FFmpeg*\...\bin`), replacing Emby's modified ffmpeg.
* `curl.exe` (shipped with Windows).

## Usage

```powershell
cd C:\emby\test\local
# 1. Build coordinator, relay and worker (win-x64) into work\bin
$d = 'C:\Program Files\dotnet\dotnet.exe'
foreach ($p in @(@('RemoteTranscode.Coordinator','coordinator'), @('RemoteTranscode.Shim','shim'), @('RemoteTranscode.Worker','worker'))) {
  & $d publish "C:\emby\src\$($p[0])\$($p[0]).csproj" -c Release -r win-x64 -o "work\bin\$($p[1])" --nologo -v q }
# 1b. Fake Emby ffmpeg (only for the LF tests, see below)
& $d publish fake-emby-ffmpeg\FakeEmbyFfmpeg.csproj -c Release -r win-x64 --self-contained false -o work\bin\fake-emby-ffmpeg --nologo -v q
# 2. Generate the media (≈ 3 min the first time)
pwsh -File .\00-gen-media.ps1
# 3. Quick check (coordinator + w1 + one playback)
pwsh -File .\smoke.ps1
# 4. All tests (≈ 30–40 min) or a selection
pwsh -File .\run-tests.ps1
pwsh -File .\run-tests.ps1 -Only T06,T08
# After an abrupt interruption: stop everything the bench launched (and nothing else)
pwsh -File .\cleanup.ps1
```

Each run of `run-tests.ps1` starts from scratch: new coordinator data (certificate, secrets,
pairings), new test workers, outputs and logs wiped.

## Fast start (LocalFirstSeconds): fake Emby ffmpeg and LF tests

Since version 0.7.7, the relay starts an HLS playback on the server's ffmpeg and then hands it to a worker at the
exact segment (`RunLocallyAsync` → `HandBackAsync`). This tracking relies on the `SegmentComplete=` stderr lines that only
Emby's modified ffmpeg writes; the bench's upstream ffmpeg does not write them and rejects the fork's options. Tests
T01–T12 therefore keep `LocalFirstSeconds = 0` and the upstream ffmpeg (relay `work\bin\shim`, unchanged).

**Fake Emby ffmpeg** (`fake-emby-ffmpeg\`, standalone .NET 8 project published into `work\bin\fake-emby-ffmpeg\`):

* strips the fork's options along with their values (`-loglevel +timing` → time prefix on its own lines,
  `-segment_write_temp`, `-print_graphs_file`, `-min_frame_time`, `-write_empty_segments`) and launches the upstream ffmpeg
  with the rest; stdin (`q` key) and exit code passed through; upstream ffmpeg attached to a Windows job object
  (it dies with the fake ffmpeg);
* HLS output (`-f segment … -segment_list x.m3u8`): makes the upstream ffmpeg write a CSV list (`x.m3u8.fake.csv`,
  absolute times like the fork's muxer), maintains the m3u8 list itself and writes for each closed segment
  `SegmentComplete=video:0 Index=N Start=… End=… Duration=… offset_pts=0 start_pts=… Frames=0 filename=…`
  (exact absolute End; Start = 0 on the 1st segment of a run with `-ss`, like the muxer); the upstream ffmpeg's
  `frame=… time=… speed=…` progress lines are relayed line by line;
* settings re-read at each launch from `work\bin\fake-emby-ffmpeg\fake-emby-ffmpeg.json` (written by
  `Set-FakeFfmpeg` in `lib.ps1`) or from the `FAKE_EMBY_FFMPEG_*` environment variables (which take precedence):

| Setting | Effect |
|---|---|
| `RealFfmpeg` / `FAKE_EMBY_FFMPEG_REAL` | upstream ffmpeg launched (the bench's "server" ffmpeg) |
| `ReadRate` / `FAKE_EMBY_FFMPEG_READRATE` | `-readrate x` added before the 1st `-i`: `0.8` simulates a server that is too slow (< 1.2x) |
| `ReadRateInitialBurst` / `FAKE_EMBY_FFMPEG_BURST` | `-readrate_initial_burst` (ffmpeg default 0.5 s) |
| `StartDelayMs` / `FAKE_EMBY_FFMPEG_START_DELAY_MS` | wait before launching ffmpeg (server that produces nothing; `q` is still honoured) |
| `StartZero` / `FAKE_EMBY_FFMPEG_START_ZERO` | `first` (default, like the muxer), `all` (Start=0 everywhere), `none` (real Start) |
| `ProbeDelayMs` / `FAKE_EMBY_FFMPEG_PROBE_DELAY_MS` | ffprobe mode (`-show_*` arguments): upstream ffprobe launched after this delay — serves as a slow `ServerFfprobe` for the coordinator |
| `LogFile` / `FAKE_EMBY_FFMPEG_LOG` | launch log (`work\logs\fake-emby-ffmpeg.log`: arguments received/passed on, exit code, number of segments) |

**Relay `work\bin\shim-localfirst\`**: a copy of `shim\remote-ffmpeg.exe` whose `shim.json` has
`RealFfmpeg` = fake ffmpeg and `LocalFirstSeconds = 20` (`Initialize-LocalFirstShim`, called by `run-tests.ps1` as soon as
an LF test is requested).

```powershell
pwsh -File .\run-tests.ps1 -Only LF          # LFa, LFc, LFd, LFe, LFb, LFb4, LFf (≈ 8 min)
pwsh -File .\run-tests.ps1 -Only LFa,LFb
```

Before the tests, `Initialize-LocalFirstMedia` creates the copies of `film_h264.mkv` (`_lent`, `_bloque`, `_1x5`, `_q`, `_avis0`,
`_avis`), reads them once, then does a "dry" playback of each one (except `_avis*`) so that the coordinator has
already probed the source: otherwise the `ShimStartAdvice` advice may exceed the 2 s the relay waits for, and the relay then falls back to
pure local ffmpeg, without trace or hand-over (relay defect, demonstrated by LFf).

| Test | Scenario | Checks |
|---|---|---|
| LFa | w1 alone (restarted, empty cache), 720p playback | 1st segment by the server (click → segment delay), hand-over `back on worker` after ~20 s of video, delay of the worker's 1st segment, continuity (indices, actual join of the .ts files, m3u8), `préchargement` (prefetch) in w1's log and blocks in `cache\inputs` before the hand-over, speed remembered by the coordinator, `q`: everything stops |
| LFb / LFb2 / LFb3 | server at 0.8x (`_lent`); 2nd playback of the same file; server stuck for 30 s (`_bloque`) | hand-over from the 2nd segment or after 4 s; advice « serveur trop lent pour ce film » (server too slow for this movie) and no launch of the server ffmpeg; 4 s rule (worker expected ≤ 8 s after launch) |
| LFb4 | server at 1.5x (`_1x5`), above the 1.2x threshold | stays on the server until ~20 s ahead, remembered speed ≥ 1.2x |
| LFc | HDR10 with tone mapping | directly on the worker, no launch of the server ffmpeg |
| LFd / LFd2 | no worker, server at 3x; then w1 restarted (empty cache) | everything on the server without errors; then hand-over without prefetch (delay compared with LFa) |
| LFe | `q` during the server phase (3x, `_q`) | relay exits, no ffmpeg / fake ffmpeg of the session left, no remote job |
| LFf0 / LFf | coordinator restarted with the fake ffmpeg as ffprobe (probe duration logged): real probe (`_avis0`) then slowed by 3 s (`_avis`), files not pre-warmed; coordinator restored afterwards (logs `coordinator-lff`, `coordinator-after-lff`) | the relay must start on the server and then hand over even if the advice arrives after 2 s |

The copies of `film_h264.mkv` prevent the "too slow" speed remembered by the coordinator (per file)
from influencing the other scenarios. The server (run without `-ss`) → worker (run with `-ss`) join steps back by ~80 ms
(D1, known and without effect on Emby Web): it is tolerated between −100 and +10 ms, with no duplicated frame.

## Files

| File | Role |
|---|---|
| `lib.ps1` | Paths, process start/stop (C# helper: timestamped stderr to file, stdin kept open for `q`), coordinator/workers/relay configuration, building of "Emby-style" arguments, ffprobe checks, segments and joins |
| `tests.ps1` | Tests T01 to T12 |
| `tests-localfirst.ps1` | The fast-start LF tests (`shim-localfirst` relay + fake Emby ffmpeg) |
| `fake-emby-ffmpeg\` | Fake Emby ffmpeg (standalone .NET 8 project, no dependency on `src`) |
| `run-tests.ps1` | Bench setup, execution, shutdown, results table |
| `tests-sleep.ps1` | T13: simulated sleep of a worker (processes frozen for 40 s) during a playback and a conversion; T14–T19 (conversions: failing worker, server alongside a worker, audio ahead, server first, HDR, server alone); T20: AV1 10-bit source with a 7 ms video start time (`media\film_av1_10bit.mkv`, generated on first use), w1 (software, then GPU) + server, frame count of the output and listed vs real duration of every chunk (spool copied to `out\spool-t20x`, table `chunks-measured.csv`); T21: HDR conversion tone mapped on the GPU (libplacebo) by w1 alone, graphs sent, Vulkan device, no slice on the server, frame count; T30: whole-file A/V check: an assembly desynchronized on purpose (`EncodeTestListedDurationShift`, +12 ms then +200 ms on one chunk) rejected, the file delivered by Emby's command correct; a clean conversion of an AVI with packed B-frames and MP3 and of a 300 s MPEG-TS not rejected. T31: variable frame rate source (frames of 10 to 160 ms, checked with ffprobe) and Emby's `-r`, each frame showing its number in 16 boxes: rejected without the `fps` filter (T31a), with it no rejection and the right frame count (T31b, c), a worker stopped midway included (T31d); first frame off the grid (T31e, its file to be measured apart); a last frame of 100 ms before its place on the grid (T31f). `nas-sync-probe.sh`, to run on the server: the values that check relies on, measured with Emby's own ffprobe, and what it would conclude. |
| `00-gen-media.ps1` | Test media: H.264 1080p 150 s (2 audio tracks fre 440 Hz / eng 880 Hz, embedded SRT subtitles + external `.srt`), 10-bit HEVC HDR10 (BT.2020/PQ) 120 s, font |
| `smoke.ps1` / `cleanup.ps1` | Quick check / cleanup |

## Generated data (`work\`, not versioned)

* `work\media\`: test media; `work\out\`: HLS segments (one folder per session) and `conv-*.mp4` conversions.
* `work\logs\`: `coordinator.log`, `worker-<name>[-n].log` (n = restart), `shim-<test>.log` (relay stderr,
  that is, exactly what Emby would read: `SegmentComplete=` lines, progress, `remote-transcode:` messages).
* `work\results.jsonl` / `work\results.md`: verdicts.
* `work\coord\data\`: certificate, `workers.json`, `shim.secret`, `admin.secret`, `encodes.json`.

## Bench-specific settings

* Test workers: load thresholds neutralized (`MaxForeign*` = 1000 %) because the test workers and the production
  worker share the same processor and would see each other as "other applications"; 2 s stabilization.
* Playback commands follow the real form of Emby 4.10 (`-loglevel +timing`, `-segment_write_temp 1`,
  `-print_graphs_file`, filters `scale@f1`, `subtitles@f2`, `supertonemap@f4`...). Conversions are sent
  **without** the options specific to Emby's fork, so that assembly and fallback to the server can run
  with the upstream ffmpeg.
* Paths: the planners expect Unix paths; the bench passes `C:\emby\...` as `/emby/...`
  (same drive), which the coordinator accepts on Windows (`ServerFiles.Canonical`).
