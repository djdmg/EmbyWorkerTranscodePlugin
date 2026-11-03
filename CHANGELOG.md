# Changelog

Version française : [CHANGELOG.fr.md](CHANGELOG.fr.md)

Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Most recent version at the top.

The Emby plugin and the Windows worker have had separate version numbers since 0.7.7; each entry states the component concerned: **plugin** (Emby DLL, including the coordinator and the relay), **worker** (`EmbyRemoteWorker.exe`), **installer** (`EmbyRemoteWorker-Setup-<version>.exe`), **bench** (local test bench). Dates are the build or installation dates recorded in `REPRISE.txt` and the project documents; when a version cannot be dated or its exact content is not established, versions are grouped together.

Worker PCs are referred to by generic names (PC-A, PC-B, PC-C).

## Plugin 0.7.38 — 2026-11-03

Built and published by GitHub Actions (release v0.7.38). Worker unchanged (0.7.20).

### Fixed

- **Plugin** — A real AVI with MP3 was rejected for its AAC being 3.3 ms off its grid at 1079 s, as it is in Emby's own conversion: ffmpeg follows the source's clock with packets a sample short. The check now looks for a jump of more than 3 ms between windows; a slow drift away from the source shows at the sound's places and at the stream's end.

## Plugin 0.7.37 — 2026-11-03

Built and published by GitHub Actions (release v0.7.37). Worker unchanged (0.7.20).

### Fixed

- **Plugin** — A conversion in one slice (AVI without presentation times, MPEG-TS) stopped at 100 chunks, 50 minutes: 3000 s of a 3424 s AVI, measured on a real file. The check rejected it and Emby converted the file. The last slice no longer has that limit.

## Plugin 0.7.36 and worker 0.7.20 — 2026-11-03

Built and published by GitHub Actions (release v0.7.36). Workers need 0.7.19 at least to receive conversion slices. Worker 0.7.20 is the first one that can update itself: going from an older worker to 0.7.20 is done by hand, once.

### Added

- **Plugin** — A/V sync check of a conversion's result over the whole file, failing closed (`SyncCheck`). The shim reads the source and the result with Emby's ffprobe, packets only, in windows of 1.5 s: the assembly's chunk boundaries (all of them, up to 1200), at least the file's quarters, each stream's start and the end. The check itself decodes only the source's first frame, from its first packets. A missing or unreadable value rejects the file, as does a ffprobe still running after 600 s: the result is deleted and Emby's own command converts on the server. Each comparison counts from the source's file start:
  - Video start: the result's first frame where the source's first frame comes out of the decoder, within 6 ms.
  - Video through the file: each source frame of a window has a result frame within 6 ms when both run at the same rate, the source lies on its own grid and starts on the grid of Emby's `-r`; otherwise within half an output frame plus 3 ms. Two frames closer than a quarter of a frame are rejected as an overlap.
  - Video end: within 6 ms under the same conditions, otherwise one output frame plus 3 ms; for a source without presentation times, 3 frames or the decoder's delay plus one frame, whichever is larger. The video's length check stays: 1.5 frames, 3 without presentation times.
  - Audio, each stream on its own, same number of streams: the first packet at the source's start minus the encoder's priming, within 3 ms (samples: AAC 1024, MP3 1105, AC-3 and E-AC-3 256, Opus 312, MP2 481, Vorbis 128, FLAC, ALAC, DTS and PCM 0). A copied stream's first packet is compared with the source's first packet, and its packets one by one in the windows, within 3 ms. An encoded stream has other packets: sound in the same places (within 2 packets plus 3 ms) and packets on the grid its first packet set (within 3 ms), unless the source's own grid moves there.
  - Audio end: 6 ms for an encoded stream; a copied stream within a packet plus 3 ms; AAC and encoders padding their last packet (MP3, for instance) may end up to a packet plus 3 ms late.
- **Plugin** — Commands whose result could not be compared stay on Emby's ffmpeg from the start, before anything is encoded (`WhyResultCannotBeVerified`): `-async`, `-ss`, `-sseof`, `-to`, `-t`, `-itsoffset`, `-output_ts_offset`, `-itsscale`; `-copyts` without `-start_at_zero`; `-avoid_negative_ts` other than `disabled`, or absent for a Matroska output; audio filters other than `volume`, `anull`, `aformat`, `pan`; audio encoders without a measured priming; sources other than Matroska/WebM, MP4/MOV, AVI and MPEG-TS; outputs other than MP4, MOV and Matroska; source streams not written `0:N`. The planner also refuses the video filters `setpts`, `trim`, `select`, `tpad`, `loop`, `reverse`. Such a conversion does not appear in the plugin's history; the reason is in Emby's ffmpeg log.
- **Plugin** — Slice-shift check before assembly (`SliceShiftCheck`): the coordinator reads the first frames of a worker run from its first chunk. A run more than 3/4 of an output frame from where it should start rejects the conversion, and so does a run starting 10 s or more into the file whose first frame cannot be placed. Failure report with the shifted-frames verdict, spool kept, Emby's command on the server.
- **Plugin** — Monitoring page in the dashboard, « Suivi du transcodage » (in French): workers, conversions in progress, history with the reason of each failure (the 100 most recent, kept 7 days), Emby's queue length, refreshed every 5 s while shown. The shim's check line and a rejected assembly with audio converted ahead now reach the status.
- **Plugin** — Audio-ahead switch on that page, off by default: `EncodeAudioAhead` is saved and applies to the conversions captured from then on, without a restart.
- **Plugin** — Library diagnostic: a manual scheduled task and its own page, « Diagnostic de la bibliothèque », with `GET /RemoteTranscode/LibraryDiagnostic` (administrators only). It lists the video files at risk for a slice-based conversion: timestamp holes, packets without presentation times, variable frame rate, stored frame rate differing from the real one, video shorter than audio, video starting after the audio, file start read differently. One file at a time, lowest priority, paused during playbacks and conversions, resumable. Files are never modified.
- **Worker** — Automatic update from the project's GitHub releases, on by default, with a box in the settings under the pairing and a one-time notice. The worker reads the latest release of the repository compiled into it at start, then about every 6 h, takes a setup only with the expected name and a strictly newer version, checks its SHA-256 and runs it with `--quiet` after 2 minutes without a job, refusing new ones meanwhile. A failed version waits 24 h. Unticked: no contact with GitHub at all. The coordinator can only ask for an earlier check: no URL, path or hash.
- **Plugin** — Minimum worker version for conversions: workers older than 0.7.19, or with an unknown version, are offered no slice, and a conversion treats them as not connected. Playback is unaffected. The refusal shows in the status API (`conversionsRefused`, `conversionsRefusedLabel`), the plugin and monitoring pages, the conversion's note and give-up error, and one log warning per connection.

### Fixed

- **Plugin** — AVI with packed B-frames: the first chunk came out 40 to 120 ms early, followed by a hole (ffmpeg 9.0.2). The video now starts where the decoder gives its first frame.
- **Plugin** — Last slice without `-r`: everything after the first chunk could be up to half a frame early. Chunk lengths now come from the frames, counted or measured in the chunks.
- **Plugin** — MPEG-TS sources are now converted as one slice: a seek lands on the PCR, up to 0.7 s past the frame asked for, losing frames at each slice (18 per slice with ffmpeg 9.0.2).

### Changed

- **Plugin** — End of a conversion: a machine is passed over when the machines clearly faster than it (finishing in under 80 % of its time) would finish everything waiting before it finished this slice (`SliceEndgame`). With a free faster machine it comes second, offered in the same pass if nobody took the slice; with a busy one it waits, decided again at each pass. A worker with no measured speed goes after free measured ones only when these can take every waiting slice.
- **Installer** — The check before installing and the file copy retry a locked file for up to 20 s. A quiet update that fails restarts the worker it closed.

### Limits

- The sync check reads timestamps, not picture or sound content: a frame or a packet at the right time with the wrong content is not seen. Only the windows are compared, so a fault that starts and ends between two of them is not seen either.
- Under Emby's `-r` at another rate than the source, or where the source is off its own grid, a video shift of up to half an output frame (plus 3 ms: 23 ms at 25 fps) cannot be told from what ffmpeg's frame grid does on purpose, and is accepted.
- An AVI with packed B-frames has no presentation time on many packets: its video is compared on decoding times, and its end to about 3 frames.
- Encoded audio is compared on packet times: a fraction of a packet shows on the grid and whole packets at the stream's end. A shift of whole packets that does not reach the end shows only if it opens a gap larger than the tolerance in a window.
- The slice-shift check does not judge server runs, runs without a seek, runs starting less than 10 s into the file or on a time that is not a known frame, or runs whose worker did not report its file start. It can miss a late run whose rounding pulls it back by more than a quarter of a frame.
- An interrupted one-slice run (MPEG-TS, AVI with packed B-frames) still resumes with a seek. Only the checks above stand behind that case: this was not measured.
- A file converted by Emby's own command, after a refusal or a rejection, does not go through the sync check.
- The tolerances were measured with ffmpeg 9.0.2. `test/local/nas-sync-probe.sh` measures on the server what the check relies on with Emby's ffprobe. Conversions of real AVI and MPEG-TS files have not been run with this version.
- The library diagnostic reads about 40 s of each file (start, five passages, end): a file that is not listed may still hold a defect elsewhere.
- The setups are not code-signed, and the SHA-256 comes from the same release: automatic update trusts the project's GitHub account and repository. Untick it to install each version yourself. It only runs in tray mode, for a copy in its installation folder.
- Both minimum versions are raised by hand. At 0.7.19, a worker with an updater is never below them, so the request for an earlier check is not sent to any such worker yet.

### Bench

- T27a expects the shifted-frames rejection.
- T30, added: an assembly desynchronized on purpose (+12 ms, then +200 ms on one chunk) must be rejected and the file delivered by Emby's command correct; clean conversions of an AVI with packed B-frames and MP3 and of a 300 s MPEG-TS must not be rejected.

## Plugin 0.7.35 — 2026-11-02

Built and published by GitHub Actions (release v0.7.35). Worker unchanged (0.7.19).

### Fixed

- **Plugin** — A video starting after the file's start came out that much ahead of its audio, from the first frame to the last: the concat demuxer starts the chunks at 0. Measured on real files, 79 ms and 21 ms. The assembly now puts the video back in place with `-itsoffset` on the chunk list.

### Added

- **Plugin** — The check of a conversion also compares where the video starts from the file's start, within half a frame (5 ms at least). A result out of step with its audio is rejected and converted on the server. Not checked on sources without exact presentation times (some AVI files).

### Bench

- T29: video starting 79 ms after the audio.

## Plugin 0.7.34 and worker 0.7.19 — 2026-11-02

Built and published by GitHub Actions (release v0.7.34).

### Fixed

- **Plugin and worker** — When the server's ffmpeg and a worker's read a file's start more than the seek margin apart (0 and 0.021 s at 25 fps, margin 20 ms), every worker slice began one frame late, with a repeated or skipped frame where it met a server slice, and a last slice on a worker lost its last frame. The conversion check accepted these files. The coordinator now sends the start it reads (`ServerFileStart`), and the worker moves its `-ss` by the difference and reports it (`SeekCorrection`), so the chunk times go back to the server's. Older workers ignore it and behave as before. Bench test T27: 7499 frames of 7500 without the correction, 7500 with it.
- **Plugin** — `EncodeAudioAhead`: the prepared MP4 wrote its time shift in the default movie time scale (1000), rounded to the millisecond, so all the audio came out 0.667 ms early. It now uses `-movie_timescale 28224000`. Measured with Emby's ffmpeg 5.1 on 3 minutes of audio: 0.000000 s from the classic conversion, packets of the same sizes.
- **Plugin** — A last slice whose run was stopped once everything was encoded (a worker becoming available just as the server's run ended) was resumed on its last frame, which the assembled video then held twice. The slice is now done there.
- **Plugin** — Last slice: chunk lengths are rounded to whole frames when Emby's command sets the frame rate (`-r`). A listing shift used to stay in the length of a run's first chunk (54 ms between two frames at 25 fps, the rest of the slice 14 ms late).

### Bench

- T27: file start read 21 ms apart at 25 fps, without and with the correction.
- T28: `EncodeAudioAhead` against the classic assembly on 10 kinds of files (AC-3, E-AC-3, DTS, FLAC, Opus, MP3, two tracks at 48 and 44.1 kHz, HEVC 10 bits, audio starting 0.5 s late, MPEG-TS, 23.976 to 29.97 fps). No packet differs. MPEG-TS sources stay on the classic assembly, by design.

`EncodeAudioAhead` stays off by default.

## Plugin 0.7.33 — 2026-11-02

Built and published by GitHub Actions (release v0.7.33). Worker unchanged (0.7.18).

### Added

- **Plugin** — `POST /RemoteTranscode/AudioAhead` switches `EncodeAudioAhead` on a real server, for the conversions captured from then on, without a restart. It stays off by default, with no switch on the page: until now it could only be set on the bench, so it could not be validated with Emby's ffmpeg 5.1.

## Plugin 0.7.32 — 2026-11-02

Built and published by GitHub Actions (release v0.7.32). Worker unchanged (0.7.18).

### Fixed

- **Plugin** — Conversions rejected for missing frames although no frame was missing (issue #4: -12 frames on an MP4 whose file starts at 0.062 s, at 23.976 fps). Emby's `-r` puts the listed times on its own grid, up to half a frame from the slice boundaries: 20.3 ms here, just over the fixed 20 ms under which a listing shift was corrected, so each slice lost 20.3 ms in the concat list. The tolerance now follows the file (a little over half a frame, at least 20 ms); a whole missing frame stays well above it and still shows in the check. Present since 0.7.21. Bench test T26 (source generated from the report's recipe): fails without the fix, 10070/10070 frames with it.
- **Plugin** — Failure reports: the recipe warns that the aac encoder moves the audio's start by 1024 samples, which had hidden this defect on the first try.

## Plugin 0.7.31 and worker 0.7.18 — 2026-11-02

Built and published by GitHub Actions (release v0.7.31).

### Fixed

- **Plugin** — Conversions rejected by a few extra frames (+3 to +22) on sources whose video starts a few milliseconds after 0 (issue #3). For the same file, Emby's ffmpeg 5.1 reads the file's start as 0 and the workers' ffmpeg 9 as 0.005 s. Slice boundaries are computed on the first clock, and a worker's `-ss` counts on the second: with a margin of 1 ms it skipped the first frame of its slice and took the first frame of the next one. Runs now aim halfway between two frames (half of the smallest gap found around the boundary, between 1 and 50 ms), so a disagreement of a few milliseconds lands on the same frame. Bench test T24 (fake ffprobe imitating Emby's, source starting at 5 ms, workers only, also with a worker killed): 11509/11509 frames.
- **Plugin** — Failure reports no longer mask version numbers shaped like IPv4 addresses ("worker <ip>" for a worker 0.7.15.0, a GPU driver 11.1.45.729 shown as "<ip>"; issue #2): only four numbers up to 255, the first one not 0, are masked.

### Added

- **Plugin, worker** — Failure reports now hold what is needed to solve an issue without the file. Before sending, the plugin measures the failure in the background (lowest priority, 150 s at most, without delaying Emby's conversion or the next one in the queue): timing fingerprint of the source, listed against measured times for each slice, frames counted against expected, first frame's shift, the file's start as each machine's ffmpeg read it, an automatic verdict (`shifted-frames`, `file-start-disagreement`, `listing-late`...), and a ffmpeg command that generates a test file with the same characteristics (test pattern and silent audio). Paths, names, titles and sizes of source packets are never sent; commands keep their technical settings only. Worker 0.7.18 sends the file's start it read. What is sent is listed in the user guide.

### Bench

- T24: the bug of issue #3 (fails without the fix, +3 frames). T25: a real rejection forced with the old margin, the full report written to a file (`IssueReportFile`) and checked: verdict, recipe, slice table, nothing personal. `fake-emby-ffprobe` imitates Emby's ffprobe, whose container start is 0.

## Plugin 0.7.30 — 2026-11-01

Built and published by GitHub Actions (release v0.7.30). Worker unchanged (0.7.17).

### Fixed

- **Plugin** — A slice reserved for a worker (fetched ahead while it encodes its current slice) no longer blocks the conversion when that worker becomes unavailable without disconnecting (its PC got busy). The reserved slice was offered to that worker only and, refused, stopped the scheduler pass: the following slices were never offered to the other, idle workers. Seen on 2026-11-01: a 4K HDR episode waited 30 minutes with one worker idle, then was given back to Emby with 4 slices left, which converted it on the server's processor. A refused reserved slice is now open to any machine and the pass goes on with the next slices.

## Plugin 0.7.29 and worker 0.7.17 — 2026-11-01

Built and published by GitHub Actions (release v0.7.29).

### Changed

- **Plugin** — The HDR → SDR tone mapper of a conversion is no longer frozen when the conversion is captured but when its first slice starts (`toneMappingDecided` in the status). Until then it is re-evaluated at every scheduler pass from the connected workers. For `EncodeToneMapperGraceSeconds` (60 s) after the coordinator starts, an HDR conversion that no connected worker can tone map on the GPU waits instead of starting on the processor, as long as some paired worker is not connected yet: Emby relaunches its conversions within a second of starting, before the workers are back, and the whole episode used to be frozen on the processor (seen on 2026-11-01, two times slower). zscale and libplacebo are still never mixed in one file.

### Fixed

- **Installer** — An update started from another Windows session of the same user (for example over SSH, in session 0, while the worker runs on the desktop) now closes the worker: processes are matched by owner and install folder in any session, the worker also listens to a `Global\` exit event, and anything still running after 20 s is closed. From a session without a desktop, the worker is relaunched in the user's desktop session through a one-shot scheduled task, or, if no desktop session is open, at the next logon when it starts with Windows. Before, the update failed with "file in use".

### Bench

- T23: lip-sync measured with 40 flash + beep markers over 10 minutes, slices encoded by two workers and the server: same offset with and without `EncodeAudioAhead` (−0.2 ms). T22A: video shorter than audio with `EncodeAudioAhead`. `EncodeAudioAhead` stays off until it is checked with Emby's ffmpeg 5.1 on a real server.

## Plugin 0.7.28 and worker 0.7.16 — 2026-11-01

Built and published by GitHub Actions (release v0.7.28).

### Fixed

- **Plugin** — The last slice of a video that stops before the end of the file is now really finished. In 0.7.27 a worker resumed on the very last frame gave a chunk of one frame, already encoded, at every run: each run counted as progress, the slice was restarted forever and one more copy of that frame was kept each time. A run that does not complete its slice and moves its encoded end by less than half a second is now an idle run, and its chunks are dropped; two idle runs in a row on the last slice end it where the video ends, three on any other slice count as a failure. Bench test T22 (HDR video stopping 34 s before its audio): 6000 frames in and out, full audio kept, conversion check passed.
- **Worker** — A refused GPU tone mapping is no longer kept in the capability cache: the probe runs again at each start (a few seconds) until it passes, since a BIOS setting or the memory other programs hold can change the result without any change to ffmpeg or the drivers. The cache is renewed once by this version.

### Added

- **Worker** — Worker and job logs older than 7 days are deleted automatically (at the first line written each day). A busy worker wrote more than 5,700 job logs in a few days.
- **Worker** — When the GPU tone mapping probe fails because Vulkan memory is refused (`VK_ERROR_OUT_OF_DEVICE_MEMORY`), the log says so and suggests enabling Resizable BAR. Seen on a PC with an RTX 4080 whose Resizable BAR was off: a 256 MB BAR window, almost full, where libplacebo creates its lookup tables; with Resizable BAR on, the probe gives 28.9 dB as on an Intel iGPU.

## Plugin 0.7.27 — 2026-11-01

Built and published by GitHub Actions (release v0.7.27). Worker unchanged (0.7.15).

### Fixed

- **Plugin** — A conversion whose video stream stops before the end of the file (the audio runs on) no longer loops on its last slice. Since 0.7.25 a run that ends without an ffmpeg error counts as an interruption, so a last slice resumed where the video stops was restarted every 2 seconds without ever producing a chunk (seen on 2026-11-01: a 4K Dolby Vision episode whose video stops 34 s before its audio, an hour of restarts on both workers). Now the last slice is done once two runs in a row, resumed after its chunks, end normally without a single chunk: the video ends there. The assembled file keeps the source's audio, and the conversion check compares the video spans as before.
- **Plugin** — Any slice whose runs end three times in a row without error but without a single chunk now counts as a failure (worker set aside, retried after 5 minutes, conversion given back to Emby after 30 minutes) instead of being restarted forever.

## Plugin 0.7.26 and worker 0.7.15 — 2026-11-01

### Added

- **Worker** — HDR → SDR tone mapping on the GPU: libplacebo through Vulkan, 2 to 2.4 times faster than the processor chain with about 8 % CPU instead of about 50 % (4K and 1080p HDR10, measured on an Intel iGPU), same frames and timestamps. Validated at start-up by a probe that tone maps a generated PQ clip with the exact chain jobs use and compares it with the processor chain (PSNR, at least 20 dB; an exit code 0 is not enough: the default Vulkan device of an Intel iGPU returns pink/green pictures without any error). Each Vulkan device (the encoders' GPU first, then the default one) is tried as is, then with `disable_multiplane=1,linear_images=1`, which fixes Intel. The validated method and device are announced to the coordinator (`WorkerCapabilities.GpuToneMapping`); the capabilities cache is renewed.
- **Worker** — Setting « HDR → SDR (tone mapping) par le GPU quand il a été validé au démarrage » (on by default); the worker window shows how HDR is tone mapped. After 3 failures GPU tone mapping is suspended until the next start, like GPU decoding: playback falls back to the processor at once (same frames), conversion slices tone mapped on the GPU are refused.
- **Plugin** — The coordinator chooses the tone mapper of each HDR conversion when it captures it and freezes it: libplacebo when at least one connected worker can run the conversion that way, zscale otherwise. It translates every slice itself in that mode; slices of a libplacebo conversion only go to workers with the capability, never to the server (Emby's ffmpeg has no libplacebo); with no such worker connected for `EncodeNoWorkerMinutes` the conversion is given back to Emby. zscale and libplacebo are never mixed in one episode. The status shows the conversion's `ToneMapping`.
- **Plugin, worker** — Translation of `supertonemap` in libplacebo mode: `hwupload`, `libplacebo` (`tonemapping` from Emby's algorithm, `bt2390` → `bt.2390`, the default; `peak_detect=0`, otherwise the brightness would change from one slice to the next; BT.709 limited-range `yuv420p`), `hwdownload`, `format`. The worker names its validated Vulkan device (`-init_hw_device … -filter_hw_device vk`) before the inputs. The zscale translation (`desat=0`) stays the fallback.
- **Worker** — Playback is tone mapped by the worker that runs it, on its GPU when validated. A playback moving between two workers of different capability may change rendering slightly from that point.
- **Installer** — The worker now bundles gyan.dev's FFmpeg 9.0.2 "full" build (the "essentials" build has no libplacebo): setup of 218 MB instead of 170 MB.

### Security

- **Worker** — The job validator accepts `libplacebo` but refuses its user shader options (`custom_shader_path`, `custom_shader_bin`).

### Bench

- Test workers use the "full" FFmpeg build. T21: HDR conversion with w1 alone, tone mapped on its GPU (libplacebo in every graph sent, Vulkan device on the command line, no slice on the server, frame count identical to the source). 215 unit tests.

## Plugin 0.7.25 — 2026-11-01

Built and published by GitHub Actions (release v0.7.25).

### Fixed

- **Plugin** — A worker closed or updated in the middle of a slice (its job ends early without any ffmpeg error) is no longer treated as failing: the slice resumes elsewhere from its last chunk and the worker stays welcome for that conversion. Real failures always carry ffmpeg's error lines and still set the worker aside. Seen on 2026-11-01: both workers were updated during one 4K HDR episode, both were set aside, and the last of 30 slices waited forever (the server cannot do HDR → SDR, and connected workers kept the give-up from starting).
- **Plugin** — A conversion the server cannot encode itself keeps trying to place its slices on the workers for 30 minutes (`EncodeNoWorkerMinutes`, was 10) before it is given back to Emby, and this now also applies when workers are connected but none can take the slice. Workers set aside after a real failure are retried after 5 minutes (`EncodeSetAsideRetryMinutes`) when they are the only ones that could take the work.

## Plugin 0.7.24 — 2026-11-01

### Added

- **Plugin** — Automatic, anonymous failure reports: when a conversion fails (assembly rejected by the check, a worker failing a slice and set aside, conversion given back to Emby), the plugin opens an issue on the project's GitHub repository with the labels `auto-report` and `bug`, or comments the open one with the same signature (event + normalised error + video codec). The report carries what is needed to fix the problem — versions (plugin, Emby, server OS/architecture, workers by version and GPU model), the error without any path, the worker's last ffmpeg error lines, the source's technical characteristics (container, codec, profile, bit depth, resolution, frame rate, video start time, duration, HDR, audio tracks), the encoder and filter names Emby asked for, the type of machine of each slice, the `assembled from chunks` summary and the check line — and nothing personal: no path, file name, title, domain, IP address, host, PC, worker or user name, Emby id. The source is designated by an anonymous code, linked to the file only in the local Emby log; the installation by a truncated random id. At most 5 reports a day per installation, never twice for the same conversion, sent in the background with a short timeout (errors ignored). Not reported: no capable worker connected, cancellation by Emby, restart, source removed meanwhile.
- **Plugin** — Plugin page: « Signaler automatiquement les échecs de conversion sur GitHub (anonyme) » (on by default) with its state (« actif », « désactivé », « inactif : pas de jeton dans cette version »).
- **Build** — The GitHub token is never in the repository: GitHub Actions passes the `ISSUE_REPORT_TOKEN` secret to `tools/build-plugin.ps1`, which embeds it in the DLL's metadata. Local builds and forks have no token and never report.
- **Plugin** — The shim's verdict on an assembly now carries its check line (source and result video spans), for the reports.

## Plugin 0.7.23 and worker 0.7.14 — 2026-11-01

Built and published by GitHub Actions (release v0.7.23).

### Fixed

- **Plugin, worker** — HDR → SDR conversions washed the highlights out to near white: the standard `tonemap` filter that stands in for Emby's `supertonemap` desaturates highlights by default (`desat=2`). The translation now sets `desat=0` unless Emby asks for a value (seen on an HDR10 test pattern compared with libplacebo; less marked on real films, but real).
- **Plugin** — Every slice of a conversion now gets the same filter graph whoever encodes it: the coordinator translates Emby-only filters before sending a slice, instead of leaving it to each worker (an older worker would otherwise have kept the washed-out rendering for its slices).

## Plugin 0.7.22 — 2026-11-01

Built and published by GitHub Actions (release v0.7.22).

### Fixed

- **Plugin** — Duplicated frame where a slice is resumed, on sources whose video does not start at 0 (Mr. Robot S03E09: video starting at 0.007 s, assembled 12 then 14 frames too long and given back to Emby). ffmpeg lists chunk times on its own frame grid (multiples of 1/23.976 s from 0), so they are a few milliseconds off the source's real frame times; a resumed slice (hand-over from the server to a worker, lost worker, run stopped right at its end) restarted from that listed time and its `-t` ran past the slice end, encoding the next slice's first frame again. A resumed slice now restarts from the source frame nearest to the listed end (read with ffprobe, at most 0.05 s away), and a run stopped at its slice's end marks the slice done instead of encoding one more frame.
- **Plugin** — Durations of the assembly list: each chunk lasts until the next one starts, and a slice's last chunk ends at the slice end when its listed end is within 0.02 s of it, so every slice lasts exactly its length whatever the time base of the segment lists or overlapping tiny chunks of the server's runs (larger gaps are not stretched, so missing frames stay visible to the check). The log line « assembled from chunks » now gives `slice:count@end/listed length`, and the kept `chunks.csv` of a rejected assembly has a `concat_duration` column.

### Bench

- T20: AV1 10-bit source whose video starts at 7 ms, encoded by a worker (software, then GPU) together with the server, with a server → worker hand-over: 11509/11509 frames, no duplicate. 175 unit tests.

## Worker 0.7.13 — 2026-11-01

### Fixed

- **Installer** — An update (or an uninstall) also closes the bundled `ffmpeg.exe` / `ffprobe.exe` still running from the installation folder after the worker has closed: a run outliving its worker kept `ffmpeg.exe` locked and the update stopped. Only programs started from that folder, in the user's own session, are closed.

## Plugin 0.7.21 — 2026-11-01

Installed on the test server on 2026-11-01 at 12:32.

### Added

- **Plugin** — The plugin page shows the conversion in progress in a box above the history: episode, progress bar, percentage, speed, time left, slices done, and which machines work on which slice.
- **Plugin** — A conversion whose assembly is rejected by the check keeps its chunks, its concat list and a chunk table (`chunks.csv`: slice, slice bounds, chunk, start, end) in its spool folder, and the log says where: only the latest one is kept. Until now everything was deleted, which left a wrong length (e.g. +12 frames on an AV1 episode encoded by a worker and the server) impossible to analyse.

### Fixed

- **Plugin** — A conversion could end as « annulée » (cancelled) although its file was assembled and verified: during the assembly the relay does not read its connection for minutes, the connection may drop, and the coordinator took that for Emby stopping the conversion before the relay's verdict arrived on a new connection. While assembling, a closed connection now waits up to 2 minutes for the verdict.

### Known issues

- An AV1 episode (Mr. Robot S03E09) encoded by a worker and the server together came out 12 to 14 frames too long and went back to Emby; under investigation.

## Plugin 0.7.20 and worker 0.7.12 — 2026-11-01

Plugin installed on the test server on 2026-11-01 at 12:08.

### Added

- **Worker** — Video decoding on the GPU when encoding happens there: NVDEC (`-hwaccel cuda`) with NVENC, D3D11VA with QSV (Intel) or AMF (AMD), validated at worker startup for each codec; frames brought back to system memory for Emby's filters. Automatic fallback to software decoding if the GPU cannot read the stream (and immediate relaunch without GPU if ffmpeg fails before the first chunk). Checkbox « Décodage vidéo par le GPU quand l'encodage s'y fait » (GPU video decoding when encoding on the GPU) in the settings (enabled by default). Measured on PC-A (Intel): decoded frames identical to software decoding, segment identical to the byte; 4K HDR → SDR 0.37x → 0.40x with 25 % less CPU, 4K → 1080p 0.80x → 0.91x (−38 %). The bottleneck of HDR conversions remains the HDR → SDR conversion (`zscale` + `tonemap`) on the CPU.
- **Worker** — Icon (version 0.7.11, never installed, included here).

### Fixed

- **Plugin** — Conversions encoded by the server: assembled video 3 to 4 frames too short, hence rejected and redone by Emby. Emby's ffmpeg, with `-start_at_zero`, lists chunk times offset from the start of the source video (5 ms on Ted Lasso S03E09, 7 ms on Mr. Robot S03E04); each slice lost this offset in the durations of the assembly list (26 slices × 5 ms = 3 frames). A finished slice, whose end is known to the frame, now has its times realigned on that end; a gap of more than half a frame is not corrected (it would mean missing frames, which the check must see). Observed on 2026-11-01: all conversions done by the NAS alone (Ted Lasso, Mr. Robot) were handed back to Emby.
- **Plugin** — The server encoding alone no longer waits 20 s between two slices when no capable worker is connected (about 20 % of time lost).

### Bench

- T19: conversion done entirely by the server, over several slices.

## Plugin 0.7.19 — 2026-10-31

Installed on the test server on 2026-10-31 at 22:18 (full bench PASS, 12 verdicts; proof of sync of audio converted ahead of time: 0 differing audio packets, identical MD5s). Observed right away: Narcos S02E02 (4K HDR) split into 34 slices and handed to PC-B.

### Fixed

- **Plugin** — A conversion is no longer handed back to Emby because the workers have not reconnected yet: if at least one worker is paired (even if disconnected at the moment), it is accepted and waits for a capable worker for up to `EncodeNoWorkerMinutes` (10 min). Observed on 2026-10-31 at 21:36: Emby relaunched Narcos S02E02 8 s after its restart, before the workers came back; since the server could not do the HDR → SDR translation alone (no `zscale`), the whole episode went back to the NAS at 0.36x.
- **Plugin** — Duplicated frame at a slice boundary: when the server handed over to a worker right at the end of its slice, the worker relaunched the slice for a few milliseconds and encoded the first frame of the next slice (video one frame, 42 ms, behind the sound for the rest of the episode; the frame-exact check, with a tolerance of 1.5 frames, let it through). A slice whose chunks already reach the end is now marked finished. Defect predating 0.7.18, found on the bench (T16b).

## Plugin 0.7.18 and worker 0.7.11 — 2026-10-31

Plugin installed on the test server on 2026-10-31 at 21:36.

### Added

- **Worker, installer** — Worker icon (green disc, "play" triangle in slices) for the executable (Explorer, taskbar, shortcuts, installed apps), the window, the notification area (same drawing, colour depending on state) and the installer (file and banner at the top of its windows). Drawn by `tools\make-worker-icon.py`.
- **Plugin** — Audio conversion during video encoding (`EncodeAudioAhead`), to shorten assembly (~3 min 30 per episode on the NAS): present but **disabled by default**, pending validation of audio/video sync.

### Changed

- **Plugin** — The workers / server choice is no longer fixed at the start of a conversion. A conversion is accepted as soon as a capable worker is connected **or** the server can encode its slices; the server then starts alone and, at each scheduler pass (every 10 s), pending slices are offered to workers again: a PC switched on mid-episode joins in immediately. Observed on 2026-10-30/31: with PC-B switched off at night, all conversions started without a worker were left entirely to the NAS (« aucun worker connecté capable de cette conversion », no connected worker capable of this conversion), even after the PC came back. Only case handed back to Emby: the server cannot encode the slices and no capable worker connects within `EncodeNoWorkerMinutes` (10 min).
- **Plugin** — HDR → SDR conversions (Emby requests its `supertonemap` filter, absent from standard FFmpeg): the server now applies to the slices it encodes **the same translation as the workers** (`zscale` + `tonemap`), so that all slices of a movie have the same rendering; if it lacks these filters, it leaves these slices to the workers. Previously it kept `supertonemap`: rendering could differ from one slice to another when the server encoded in parallel with the workers.

### Fixed

- **Plugin** — An unreadable slice boundary no longer puts the whole episode into a single slice: it is skipped and its two neighbouring slices are merged into one; the single slice only remains if most boundaries are unreadable. Observed on Narcos S02E02 (4K HDR, frame at 3831.5 s unreadable, 64 min in a single slice on PC-B at 0.6x).

### Bench

- T17: conversion started with no worker connected, accepted and started by the server, then a worker started along the way takes slices. T18: Emby graph with `supertonemap` encoded by the server (the bench's standard FFmpeg) thanks to the translation, alone and then with a worker.

## Plugin 0.7.17 and worker 0.7.10 — 2026-10-30

Plugin installed on the test server on 2026-10-30 at 17:27 (counter observed: 150 items); worker 0.7.10 installed on PC-A at 16:36.

### Added

- **Plugin** — The plugin page also shows Emby's conversion queue (Conversions section).

### Changed

- **Plugin, worker** — The number of items in Emby's conversion queue is sent to **all connected workers every 30 s**, whether they have a job or not. Previously it only arrived with the progress of a slice encoded by the worker: an unavailable worker (CPU busy with other applications) showed "—". Workers 0.7.8 and 0.7.9 ignore these messages.

### Known issues

- The plugin page does not open in the Emby Android app (« veuillez vous connecter directement à votre serveur local », please connect directly to your local server): the apps do not run the JavaScript of third-party plugins. It works in a browser, including on mobile. An app-compatible page would require Emby's declarative interface (IHasUIPages).

## Plugin 0.7.16 — 2026-10-30

Installed on the test server on 2026-10-30 at 16:17.

### Fixed

- **Plugin** — Emby conversion queue counter: 0.7.15 looked for the sync manager in Emby's container, where Emby 4.10 does not register it ("SyncManager is not registered" in the log, counter "—" on the workers). The DLL now takes the instance Emby itself uses (`SyncManager.Current`); if it does not exist yet (Emby still starting), it asks again later instead of giving up.

## Plugin 0.7.15 and worker 0.7.9 — 2026-10-30

Plugin installed on the test server on 2026-10-30 at 16:03; worker 0.7.9 installed on PC-A at 15:57; installer dropped into a shared folder for PC-C. Observed on the real server (Grey's Anatomy S05E13): each of PC-A's slices starts 4 to 7 s before the end of the previous one, and the server encodes its own slices (3, 7…) at the same time.

### Added

- **Plugin, worker** — The worker window shows the **number of items remaining in Emby's conversion queue** (pending or in progress, all conversion jobs combined). The DLL reads it inside Emby itself (sync manager, at most every 30 s), without an API key or external program; if an Emby version no longer allows it, the counter shows "—". Requires worker 0.7.9; workers 0.7.7 and 0.7.8 ignore this information.

### Changed

- **Plugin** — The server also encodes slices **in parallel with the workers** (`EncodeLocalAlongsideWorkers`, enabled by default): only one ffmpeg at a time on the server, never a slice reserved for a worker (already prefetched), and not an end-of-conversion slice that a busy worker would clearly finish first (the server's speed is now measured like the workers'). The server only passes its slice to a free worker once nothing is left pending. Previously it only encoded if no worker was working on the conversion.
- **Plugin** — **Next slice started early** (`EncodeOverlapSeconds`, 6 s): a few seconds before the end of its slice (based on its measured speed), the worker receives its next slice and launches it immediately; it then briefly counts two jobs. The ffmpeg startup (opening the source, seeking, encoder initialization: about 4 s out of 26 s per slice with QSV, measured on PC-A) no longer leaves the encoder idle. Works with all workers that accept 2 simultaneous jobs, whatever their version.

### Bench

- Test T15: a single worker; the server encodes a slice in parallel and the worker has two simultaneous slices; frame-exact check passed. T14, T09a and T09b PASS; 134 unit tests OK.

## Plugin 0.7.14 and worker 0.7.8 — 2026-10-30

Plugin installed on the test server on 2026-10-30 at 12:49; worker 0.7.8 installed on PC-A at 12:55; installer dropped into a shared folder for PC-C.

### Added

- **Worker** — The job list shows the progress of each conversion: columns « Fichier » (File, episode title), « Tranche » (Slice, "7/24"), « Position » (start and end of the slice within the episode), « Tranche % » (Slice %), « Total » (progress of the whole episode, all machines combined, with the estimated time remaining), « Vitesse » (Speed) and « Durée » (Duration), with tooltips.
- **Plugin** — The job offer carries the slice (title, number, count, start, end), and the coordinator sends the overall conversion progress (`ConversionProgress`) to the workers taking part. This message is only sent to workers 0.7.8 or later: a 0.7.7 worker keeps working, without the new columns.

### Bench

- T01, T02, T09a, T09b and T14 PASS; 134 unit tests OK.

## Plugin 0.7.13 — 2026-10-30

Installed on the test server on 2026-10-30 at 10:57.

### Fixed

- **Plugin** — A worker that fails on a slice is excluded from **that conversion only**; the slice is taken over by another worker, or by the server if none is left. Only failures of the server itself hand the conversion back to Emby (after 3 attempts). Previously, 3 failures of the same worker were enough to hand the whole conversion back to Emby, which redid it alone on the NAS: observed on 2026-10-30 on Grey's Anatomy S07E19, when PC-C was shut down during an NVENC encode (GPU unavailable during Windows shutdown, "Invalid argument" on 3 slices).

### Bench

- Test T14: a worker's ffmpeg is stopped mid-slice; the worker is excluded from the conversion, which finishes on the other worker, without being handed back to Emby.

## Plugin 0.7.12 — 2026-10-30

### Fixed

- **Plugin** — AVI files without presentation timestamps on their packets (DivX/Xvid with packed B-frames): they are no longer split into slices, because runs that seek into them do not meet on the same frame. They are converted as a single slice. Observed on Monk S07E06: 22 slices gave 34 extra frames; as one slice, the result is identical to Emby's conversion (57,072 frames).
- **Plugin** — Frame-exact check: decoding takes into account packets without presentation timestamps (decoding timestamp instead). For these sources, whose last frame has no timestamp, the end is only known to within two frames: tolerance of 3 frames instead of 1.5.
- **Plugin** — Frame-exact check: an unreadable end of video is no longer taken for a success. The measurement is repeated on the last 10 minutes; if it fails again, the file is rejected and Emby redoes the conversion. ffprobe errors are written to the log.
- **Plugin** — A conversion is accepted as soon as a capable worker is connected, even if it is momentarily busy. The server encodes slices in the meantime (after 20 s) and hands over to the worker as soon as it becomes free. Previously, the whole conversion stayed on the NAS (observed on a HEVC episode while PC-A was loaded). With no capable worker connected at all, nothing changes: Emby converts by itself.

### Bench

- Test T04 adapted: a worker that is connected but unavailable no longer refuses the conversion; the server starts it in slices.

## Plugin 0.7.11 — 2026-10-29

Installed on the test server on 2026-10-29 at 16:37.

### Changed

- **Plugin** — Installation on Synology without SSH: generic DSM task "Installer plugin RemoteTranscode" (root, disabled) that stitches `plugin.part0`..`plugin.part5` back together, verifies the hash in `plugin.sha256` and runs `install-plugin.sh`; log `install-<date>.log`. A new version no longer requires changing the task (operations tool, no change in the DLL).

### Fixed

- **Plugin** — Conversion assembly: the `concat.txt` list gives the exact duration of each chunk (`duration` directive, taken from the worker's segment list).
- **Plugin** — Without this duration, Emby's ffmpeg 5.1 sometimes estimated an MPEG-TS chunk one frame too short; the next chunk overlapped and the video ended frames too early. Observed on a 4K episode of 104 chunks: 10 missing frames (0.42 s), file rejected by the frame-exact check.
- **Validated** on the real server on 2026-10-29: the same episode (104 chunks, encoded by PC-A then by the NAS) is assembled without any "Non-monotonous DTS" warning; video of 2837.879 s for 2837.876 s in the source, accepted by the check.

## Plugin 0.7.10 — 2026-10-29

Installed on the test server on 2026-10-29 at 12:05, without SSH.

### Added

- **Plugin** — Conversions: check of the video duration of the assembled file to within 1.5 frames; otherwise the file is rejected and the conversion redone by Emby.
- **Plugin** — Assembly log (`assembled from chunks`).
- **Bench** — Fake Emby ffmpeg (`test\local\fake-emby-ffmpeg`) and fast-start LF tests (`run-tests.ps1 -Only LF`, 11 passed).
- **Bench** — Test T13: simulated sleep of a worker (processes frozen for 40 s), during playback and conversion.
- **Bench** — Log capture in UTF-8.

### Fixed

- **Plugin** — Fast start: the coordinator's advice arrives within 1.2 s at most (ffprobe probe cached, continued in the background). Previously, advice taking more than 2 s left the whole playback on the server.
- **Plugin** — Late advice (after 2 s): the relay reconnects and starts on the server.
- **Plugin** — Delay of the 1st segment on the server: 3 s plus one segment at the minimum speed (5.5 s) instead of 4 s.
- **Plugin** — Only a measured server speed is remembered (on segments, or read from ffmpeg's progress), for 7 days instead of 30, never 0.00x.
- **Plugin** — A server that has produced no segment is stopped without waiting (before: 10 s wait).
- **Plugin** — Prefetch: probe reused, no keyframe search from 0, no hint when a worker already has the playback.
- **Plugin** — Conversions: no more final chunk shorter than 2 s (`-segment_time` lengthened). Origin: 3 missing frames at the end of a 0.7.4 conversion (last chunk of 0.12 s lost on the server side).

## Worker and installer 0.7.7 — 2026-10-29

### Added

- **Worker** — Local source cache (`InputCache`): 4 MB blocks, at most 4 GB and a quarter of the free space, emptied at startup.
- **Worker** — Uninstallation via `EmbyRemoteWorker.exe --uninstall` (`--quiet` for no window), limited to the files in the manifest.

### Changed

- **Worker** — All ffmpeg inputs go through the cache, which enables prefetching and avoids re-reading the header and index on each resume.

## Plugin 0.7.7 – 0.7.9 — 2026-10-28 to 2026-10-29

Versions built but not installed on the test server; 0.7.10 replaces them. Exact dates not established.

### Added

- **Plugin** — Playback starting on the server (0.7.7): first segment by Emby's ffmpeg, then hand-over to a worker at the exact segment after about 20 s ahead (`LocalFirstSeconds`), or as soon as the server proves too slow (below 1.2x).
- **Plugin** — Decision on click: directly on a worker for HDR tone mapping, a source of more than 1088 lines, or a movie for which the server has already been measured too slow.
- **Plugin** — Prefetch during the start on the server: the worker that will take over loads the header, the end of the file and the data up to beyond the hand-over point (3-minute authorization limited to that file).

### Fixed

- **Plugin** — D2 (0.7.9): the `EXTINF` duration of a segment is computed from the end of the previous segment, not from the `Start=0` of the first segment after a resume.

## Worker and installer 0.7.5 – 0.7.6 — 2026-10-28

Content specific to 0.7.6 not established.

### Changed

- **Installer** — No longer copies itself as `Uninstall.exe`; an `Uninstall.exe` left by an earlier version is deleted during the update.
- **Installer** — Closes the worker cleanly ("-exit" event, like « Quitter » (Quit)) and only forces it to stop after 20 s or for a version that does not know this signal.

### Fixed

- **Installer** — Installer 0.7.4 had been flagged by Kaspersky ("PDM:Trojan.Win32.Generic", behavioural detection). Do not use 0.7.4.

## Plugin and worker 0.7.1 – 0.7.4 — 2026-10-28

Successive versions on the same day; the exact version of each change is not established by the sources.

### Added

- **Plugin** — Conversions in **slices** of about 2 minutes (then 60, 60, 30, 30 s at the end), boundaries placed on frames read by ffprobe, encoded in parallel by the workers; one slice per worker, the next one reserved.
- **Plugin** — End of conversion based on the workers' measured speed; fallback to the server after 20 s without a free worker, then back to a worker.
- **Plugin** — Playback has priority: a conversion is moved off a worker on which a playback is not progressing fast enough.
- **Plugin** — A worker whose ffmpeg fails for a playback is excluded from it for 10 minutes.
- **Worker** — Optional GPU encoding, separate for conversions and playback, disabled by default (QVBR for Intel QSV, `-cq` for NVIDIA NVENC). A fix of the main10 profile in GPU playback is recorded, with no established version.

### Fixed

- **Plugin** — 0.7.3: file whose video starts after the audio (6 s); each slice now stops on its boundary (21,579 frames out of 21,579).

### Known issues

- **Plugin** — 0.7.4: a GPU conversion lost its last 3 frames (fixed in 0.7.10).
- **Installer** — 0.7.4: flagged by Kaspersky (fixed in 0.7.5).

## Plugin and worker 0.7.0 — 2026-10-28

### Added

- **Plugin** — Relay self-test before activation; the administrator's custom ffmpeg is saved and later restored.
- **Plugin** — Hardened allow-list of ffmpeg arguments and filters; secrets with 0600 permissions.
- **Plugin** — Rate limiting of worker authentication attempts: 10 failures within 10 minutes block the address for 15 minutes (`X-Real-IP` read only behind the local proxy).
- **Installer** — Manifest-based uninstallation (`install-manifest.txt`).
- **Worker** — « Aide » (Help) → « À propos » (About) menu (version, FFmpeg, identifier), version in the title.

### Changed

- **Plugin, worker, installer** — Full code review (about 55 findings fixed), 111 unit tests.

### Fixed

- **Worker** — The segment upload loop no longer stops on an error (antivirus, network outage), which used to block playback.
- **Plugin** — A worker stopping is no longer taken for the end of the video; no more "resurrected" jobs in the Running state.

## Plugin 0.6 — 2026-10-27

### Added

- **Plugin** — Standalone plugin: the DLL alone is enough. The coordinator is hosted inside the Emby process.
- **Plugin** — Embedded relay for linux-x64, linux-arm64, linux-musl-x64 and win-x64, extracted at startup, generated dispatcher.
- **Plugin** — Everything is detected from Emby at each startup: ffmpeg, ffprobe, environment variables, transcoding folder, libraries.
- **Plugin** — Uninstallation: back to Emby's ffmpeg on the fly, without a restart.

## Plugin and installer 0.5.0 — 2026-10-27

### Added

- **Plugin** — First Emby plugin: dashboard page (switch, workers, jobs, conversions), administrator-only API, relayed to a separate coordinator.
- **Plugin** — Interception of Emby's playbacks **and** conversions, with Emby's own settings (no more project-specific profiles). Remux left to Emby's ffmpeg.
- **Plugin** — Translation of Emby's HDR `supertonemap` filter into zscale + tonemap for the workers.
- **Plugin** — Hand-over at the exact segment: resume at the exact end of the last delivered segment, back to a worker when it becomes available again.
- **Installer** — First all-in-one worker installer, without administrator rights.

### Fixed

- **Worker** — A segment truncated by a job being stopped is no longer delivered (it left a gap in the player's buffer).

## Before 0.5.0 — prototype, 2026-10-26 to 2026-10-27

No version number.

- Interception of Emby 4.10 through the "custom ffmpeg path" setting and imitation of the `SegmentComplete` lines (2026-10-26).
- Worker: manual or automatic availability, time windows, CPU/GPU/memory thresholds, notification area icon, standalone package with FFmpeg 9.0.2 (2026-10-27).
- Multiple workers: score-based selection, resume on another worker at the next segment, worker's ffmpeg processes stopped with it (Job Object) (2026-10-27).
- Background conversion queue in 30 s chunks with resume at the first missing chunk (2026-10-27).
