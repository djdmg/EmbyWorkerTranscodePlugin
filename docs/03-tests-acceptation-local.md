# Local acceptance tests (without Emby, without NAS) — 2026-10-28

Version française : [03-tests-acceptation-local.fr.md](03-tests-acceptation-local.fr.md)

Test bench: `C:\emby\test\local\` (instructions in `README.md`). It reproduces the whole chain on a single PC:
the standalone coordinator, two test workers (three for T12) and the `remote-ffmpeg` relay launched with
arguments copied from real Emby 4.10 commands. The relay plays the role of Emby: it receives the commands, and the bench
reads on its stderr what Emby would read. The verdicts below come from the last complete run
(`run-tests.ps1`, 13 min, 10:20–10:33). The earlier runs of the same day give the same results.

## 1. Environment

| Item | Value |
|---|---|
| OS | Windows 11 Enterprise 10.0.26200 (Intel Core Ultra 5 225U, Intel iGPU) |
| .NET | .NET 8 SDK, self-contained `win-x64` publish (coordinator.exe, remote-ffmpeg.exe, EmbyRemoteWorker.exe) |
| Sources | `C:\emby\src` as of 2026-10-28 (no git repository, hence no reference commit); unit tests: 115/115 passed (129/129 on 2026-10-29) |
| Workers' ffmpeg | `C:\emby\artifacts\worker\EmbyRemoteWorker\ffmpeg\ffmpeg.exe` 9.0.2-essentials (gyan.dev); validated encoders: h264_qsv, hevc_qsv, av1_qsv, libx264, libx265 |
| "Server" ffmpeg/ffprobe | Gyan 9.0.2 full build (winget), **upstream build** instead of Emby's modified ffmpeg |
| Ports | 38097 (workers, HTTPS, pinned self-signed certificate), 38098 (relay/administration, loopback) |
| Media | `film_h264.mkv`: H.264 1080p25, 150 s, audio 0:1 fre (440 Hz sine) and 0:2 eng (880 Hz), embedded SRT, plus an external `.srt`; `film_hdr10.mkv`: HEVC Main10 BT.2020/PQ with HDR10 metadata, 120 s |
| Production worker | PID 13308, never touched (checked after each test); test workers isolated by `EMBY_WORKER_HOME` |

## 2. Results

| # | Test | Verdict | Summary |
|---|---|---|---|
| T01 | Conversion by an available worker | **PASS** | w2, 47 s, 5 chunks, assembled and verified; 150.02 s, h264 1280x720 + aac, full decode without errors |
| T02 | Distribution over two workers | **PASS** | 4 simultaneous playbacks: w1, w2, w1, w2; 2 simultaneous conversions: one on each worker, both valid |
| T03 | No worker available | **PASS** (with limitation) | Refused by the coordinator, then fallback to the local ffmpeg. Conversion done on the server and valid. Playback: see limitation L1 |
| T04 | A worker that became unavailable refuses new jobs | **PASS** | After `release`: Unavailable, new playbacks and conversions refused |
| T05a | PC reclaimed: « Libérer » (Release) (immediate) | **FAIL** (partial) | Immediate resume on w2 at the exact segment, continuous indices, no duplicate. But the join has an **80 ms PTS step back** (D1) |
| T05b | PC reclaimed: draining (FinishPlayback) | **PASS** | Draining state, playback continues on the same job, the new playback is refused; Unavailable once playback is finished |
| T05c | PC reclaimed: `ReleaseImmediately` (end of time window) | **FAIL** (partial) | Release and resume on w2 correct, same 80 ms step back at the join (D1) |
| T06 | Remote playback: start, `q`, seek | **PASS** | 1st segment in 1.9 s; exit 0.7 s after `q`; seek `-ss 60` / segment 20 on the same worker (session affinity) |
| T07 | Remote ffmpeg stopped after `q` | **PASS** | Job's ffmpeg gone 0.9 s after `q` (seek job included) |
| T08 | Worker disconnection / reconnection | **FAIL** (partial) | Worker killed: resume on the other one in 0.1 s, no orphan ffmpeg; restarted, it is Available again in 4.9 s. Same 80 ms step back (D1) |
| T09a | Worker killed during a conversion (another one is free) | **PASS** | Resume on the other worker at the next chunk; final file complete and valid; no file before assembly |
| T09b | Only worker killed during a conversion | **PASS** | Fallback to the server at chunk 1, back to the worker as soon as it returns; final file complete and valid |
| T10a | Playback: burned-in SRT subtitles | **PASS** | Filter translated (`subtitles@f2=filename='aux/…'`); PSNR of the bottom of the picture against a reference without subtitles: 21.4 dB, so the text is indeed rendered |
| T10b | Playback: audio track selection | **PASS** | `-map 0:2` gives 880 Hz (the `0:1` reference gives 440 Hz) |
| T10c | Conversion with two audio tracks | **PASS** | Done remotely; 2 aac tracks at 440 and 880 Hz; 150.02 s |
| T10d | Conversion with burned-in subtitles | **PASS** (on the server) | The planner leaves it to the server (D3); valid output |
| T10e | HDR10 to SDR conversion | **PASS** | Done remotely, `supertonemap` translated into zscale+tonemap; h264 yuv420p bt709; Y PSNR against the upstream reference chain: 45.3 dB |
| T10f | HDR10 to SDR playback | **PASS** | h264 yuv420p/bt709 1280x720 segments |
| T11 | Direct Play | **NOT TESTABLE** | Emby decides, no ffmpeg is launched. Remux stays local (unit tests) |
| T12 | Unauthorized access refused | **PASS** | 31 compliant checks (details in §3) |

Summary: 16 PASS, 3 partial FAILs all with the same cause (D1), 1 NOT TESTABLE.

**Update of 2026-10-29.**

- **D1** was checked on the real server in Chrome (Emby Web, hls.js): playback started at 0, release at 114 s then back on the worker at 324 s, without any pause or gap in the buffer. The 80 ms step back therefore has no effect for this player; the three partial FAILs are kept in the bench as an indicator, not as a blocking defect.
- **D2** is fixed (relay 0.7.9): the advertised duration of a segment is computed from the end of the previous segment, not from the `Start=0` of the first segment after a resume.
- **Conversions**: they are now split into **slices** of about 2 minutes (then 60, 60, 30, 30 s at the end), encoded in parallel by the workers, with boundaries placed on frames. Scenario added: conversion of a 30 min media file in 18 slices with a worker killed along the way, **45,000 frames out of 45,000**. The 30 s chunks described below still exist, inside each slice.
- **Playback starting on the server** (plugin 0.7.7 and later): the T tests keep `LocalFirstSeconds = 0`. A **fake Emby ffmpeg** (`test\local\fake-emby-ffmpeg`) strips the fork's options and writes the `SegmentComplete` lines; it is used by the **LF** tests (`run-tests.ps1 -Only LF`, see the bench's README).

**Update of 2026-10-29 (plugin 0.7.10).**

LF tests, first series: 4 real defects, fixed in 0.7.10.

| Defect | Fix |
|---|---|
| Coordinator advice after more than 2 s (1st playback of a movie, slow ffprobe): playback stayed entirely on the server, without trace | Advice within 1.2 s at most (probe cached, continued in the background); late advice makes the relay reconnect, and it starts on the server |
| 4 s rule: a server at 1.5x declared too slow, 0.00x remembered for 30 days | Delay of 3 s + one segment at 1.2x (5.5 s); only a measured speed is remembered (segments, or ffmpeg progress), for 7 days |
| Stuck server: 10 s wait for a segment that will never come | With no segment produced, immediate stop |
| Prefetch arriving after the hand-over | Probe reused, no keyframe search from 0, no hint if a worker already has the playback |

Results after the fix: **LFa, LFb, LFb2, LFb3, LFb4, LFc, LFd, LFd2, LFe, LFf0, LFf: 11 PASS**. Server at 0.8x: speed 0.70x remembered, the 2nd playback goes directly to the worker; stuck server: worker 6.7 s after launch (15 to 19 s before); coordinator probe of 5.6 s: playback still moves to the worker.

Conversions: a last chunk of 0.12 s (3 frames) lost on the NAS in 0.7.4 led to two safeguards: no final chunk shorter than 2 s, and verification of the video duration of the assembled file to within 1.5 frames. **T01, T02, T09a, T09b: PASS**; over the 5 conversions, source and result video identical (0.000–150.000 s), no false rejection. The bench's log capture is in UTF-8 (the accented characters in the relay's messages made searches fail wrongly).

Simulated sleep (T13, `tests-sleep.ps1`, `run-tests.ps1 -Only T13`): the worker and its ffmpeg processes are frozen for 40 s (NtSuspendProcess), then thawed. **T13a, playback**: loss detected in 16 s, continued on the other worker at the exact segment, no duplicate; on wake-up, the stale ffmpeg stops and the worker becomes available again. Partial FAIL for the sole reason D1 (−80 ms at the join, like T08). **T13b, conversion**: PASS, loss detected by the heartbeat, slice resumed elsewhere, complete file, video identical to the source to the frame; on wake-up, no ffmpeg left. Real sleep also cuts the network; that case (Wi-Fi cut) had been verified on the real server on 2026-10-28.

## 3. Details per test

**General method.** Each test launches `remote-ffmpeg.exe` with an Emby-format command. The relay's stderr is
logged (`work\logs\shim-*.log`), as are the coordinator (`coordinator.log`) and each worker
(`worker-*.log`). The workers' state is read from `/status`, outputs are checked with ffprobe (streams, duration) and
fully decoded (`ffmpeg -v error -f null`). For playbacks, two continuity measurements are made:
1. on the `SegmentComplete` lines: contiguous indices, no duplicate;
2. on the `.ts` files themselves: gap between the first video PTS of the resumed segment and the end of the previous segment.

**T01 / T02.** `New-ConversionArgs` produces `-y -copyts -start_at_zero -f matroska,webm -c:v:0 h264 -noautorotate -i
/emby/.../film_h264.mkv -map 0:0 -map 0:1 -sn -c:v:0 libx264 -vf scale=… -crf:v:0 23 … -f mp4 <output>`.
Relay excerpt: `remote-transcode: conversion … confiée aux workers`, then
`vidéo encodée par les workers, assemblage du fichier demandé par Emby`. On `/status`, the encode is
`Completed` with the note « assemblé et vérifié par le serveur » (assembled and verified by the server). For T02, the four relays were launched 0.7 s
apart. The score (`WorkerMatching.Score`, free slots) does alternate w1/w2.

**T03.** w1 and w2 are stopped and show as Offline.
- a) Real Emby command: `not offloaded (no worker connected), using local ffmpeg`, then the upstream ffmpeg
  stops on `Invalid loglevel "timing"` (limitation L1).
- b) The same command without `-loglevel +timing`, `-print_graphs_file` or `-segment_write_temp`: the local ffmpeg
  writes the segments, and `q` ends it with exit code 0.
- c) Conversion: `conversion sur le serveur (aucun worker disponible pour cette conversion)`. Emby's command
  runs locally and the output is valid (150.02 s).

**T04.** w1 runs in tray mode with its own `EMBY_WORKER_HOME`. `EmbyRemoteWorker.exe release`
returns 0, the worker goes to Unavailable « libéré par l'utilisateur » (released by the user), and `Available=false` is saved.
Relay excerpt: `not offloaded (no available worker: w1: Unavailable (désactivé par l'utilisateur))`. The
conversion is refused the same way. The refusal happens in the coordinator (`PickWorker`): no offer is
sent to the worker, so the worker-side check (`CheckOffer`) does not come into play here.

**T05.**
- a) Playback starts on w1 (1080p, preset medium), then w2 is started. `release` on w1: the job's ffmpeg
  stops in less than 1.8 s. Relay excerpts:
  `resuming at segment 16 (47.995s)` then `job ca23784f… continues on worker w2`.
  Segments 0 to 19 are contiguous and without duplicates.
- b) w1's time window ends 25 s after startup, with `Reclaim=FinishPlayback`. w1 goes to
  `Draining (hors plage horaire)` (outside time window). Playback continues on the same job (26 then 50 segments, no resume) and a
  new playback is refused: `no available worker: w1: Draining (hors plage horaire)`. After `q`, w1 is
  Unavailable.
- c) Same scenario with `Reclaim=ReleaseImmediately`. w1's log:
  `PC repris (hors plage horaire) : libération immédiate de 1 tâche(s)`. Resume on w2 at segment 30 (89.995 s).
- For a and c, the FAIL verdict is due solely to the join between files: `fin 15=58,080s, début 16=58,000s,
  écart -80 ms` (end of 15 = 58.080 s, start of 16 = 58.000 s, gap −80 ms; see D1). In `SegmentComplete`, the resumed segment shows `Start=0.000000` (see D2). The criterion
  "Start of the resumed segment = End of the previous one within 10 ms" therefore cannot be checked on these lines. It was
  checked on the files, where it fails by 80 ms.

**T06 / T07.**
- First segment 1.9 s after the relay is launched. After `q`, the relay prints `[q] command received. Exiting.`,
  exits with code 0 in 0.7 s, and the worker logs `job …: ended (exit 0), 7 segments uploaded`.
- The job's ffmpeg (identified by `/jobs/<id>/` in its command line) disappears in 0.9 s.
- Seek: new relay in the same session folder with `-ss 00:01:00.000 -segment_start_number 20`. The
  first segment has index 20, `End=63.000`; segment 21 covers [73.000; 76.000] in the file (constant
  mpegts offset of +10 s).

**T08.** Only w2's `EmbyRemoteWorker.exe` is killed, by PID (`Process.Kill`), not its process tree.
- Its ffmpeg disappears immediately (Job Object), without orphans.
- The coordinator logs `Worker w2 dropped: connection closed`.
- The relay logs `continues on worker w1` 0.1 s later.
- On restart, w2 is Available in 4.9 s and logs `dossier de tâche abandonné supprimé : d5952413…` (abandoned job folder deleted).
- Partial FAIL for the same −80 ms join (D1).

**T09.** The output file does not exist before assembly. The 30 s chunks are resumed from the first
missing chunk, without duplicates or truncation: duration 150.02 s identical to the source, full decode without errors.
- a) Coordinator: `Job e0fad559… lost: worker w1 lost: connection closed`, then resume on w2.
- b) With no worker at all: `NAS (local) : aucun worker : encodage sur le serveur à partir du morceau 1` (no worker: encoding on the server from chunk 1). When w1
  returns: `a worker is available, stopping the local encode to hand over`.

**T10.** The measurements are objective:
- burn-in: PSNR of the lower quarter of the picture against an identical playback without subtitles;
- audio track: frequency of the sine, computed from the zero-crossing rate reported by `astats`;
- tone mapping: PSNR against `zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable,zscale=t=bt709…`
  applied directly to the source.

**T12.** Checks done with `curl -k`. The WebSocket upgrade request is checked before the `101`. Each
failure case uses a different `X-Real-IP` so as not to mix up the rate-limiting counters.

| Check | Obtained (expected) |
|---|---|
| `/ws/worker` with an invalid token, then without a token | 401, 401 |
| Revocation of w3 via `POST /admin/workers/{id}/revoke` | 200. w3 is disconnected then goes to Revoked; its reconnection gets 401 (`Refused worker connection from 127.0.0.1`; w3: `status code '401' when status code '101' was expected`); its token gets 401 |
| `/jobs/{id}/files/0`: wrong token, no token, non-existent job | 401, 401, 401 |
| `/jobs/{id}/files/0` with the right token (byte range) | 206 |
| File not in the plan | 404 |
| Segment PUT with a wrong token | 410 |
| Right token once the job is finished | 401 |
| Input outside the roots, path with `..`, subtitles outside the roots, conversion outside the roots | Never handed to a worker: `input outside allowed roots`, `filename outside allowed roots`, `source hors des bibliothèques autorisées` (source outside the allowed libraries) |
| `/ws/shim` without a secret, then with a wrong secret | 401, 401 |
| Relay without a secret file | `secret unavailable …, using local ffmpeg` |
| `/status` on the workers port; `/ws/worker` on the local port | 404, 404 |
| `/admin/status` without a secret, then with the admin secret | 401, 200 |
| `POST /admin/workers/pair` without a secret | 401 |
| Failure rate limiting | Attempts 1 to 10: 401; 11th: 429. The same client stays blocked on all worker routes (429), another client is not (401). w1 stays connected |

## 4. Defects and findings

**D1. 80 ms PTS step back at the join of a resume (T05a, T05c, T08).** When a playback started at the
beginning (without `-ss`) is resumed elsewhere, the resumed segment starts 80 ms before the end of the previous one. Example:
end of segment 15 = 58.080 s, start of segment 16 = 58.000 s.
- The content itself is continuous: no frame is lost or duplicated in source time. Only the time base
  differs by 2 frames.
- Reproduced with ffmpeg 9.0.2 alone, with `-copyts -start_at_zero` and libx264 with B-frames:
  - run without `-ss` (or with `-ss 0`): PTS = source time + 10.08 s;
  - run with `-ss` greater than 0: PTS = source time + 10.00 s;
  - two runs with `-ss` greater than 0 join at 0 ms.
- The resume is built by `ResumePoint` / `ResumeArgs.Build`
  (`src\RemoteTranscode.Shim\Program.cs:662-667` and `:846`). It assumes an identical time base in both
  runs, which is wrong when the first run started without `-ss`.
- Impact on hls.js not measured here. The "Seamless hand-over" design note says that small
  overlaps can stall the player. **To be checked in a browser** before fixing: for example, force a
  common time base or shift the resume point by the reordering delay.
- The same gap is likely between a worker and Emby's ffmpeg, to be checked on the NAS.

**D2. `Start=0` in `SegmentComplete` for the first segment of any run launched with `-ss` (seek or
resume).**
- Cause: the upstream segment muxer writes `start=0` in its CSV list, which I reproduced. The worker passes this
  value on as is (`src\RemoteTranscode.Worker\JobRunner.cs:358`).
- Consequence: the relay writes `#EXTINF:63.000000` and `#EXT-X-TARGETDURATION:63` in the `-segment_list` list
  (`src\RemoteTranscode.Shim\Program.cs:593`, duration computed as `End - Start`), and the line shows `Duration=63`.
- `End` is correct, and the resume logic relies on it alone. According to the project notes, Emby's fork
  may also print `Start=0`, hence a low severity.
- A simple fix is possible (not done, I prefer to report it): when `Start` is 0 on an index greater than the
  first, use `lastCompleteEnd` or the nominal duration.

**D3. Conversions with burned-in subtitles stay on the server** ("filter subtitles filename must use a
placeholder"). `EmbyConversionPlanner` (`src\RemoteTranscode.Common\EmbyConversionPlanner.cs:160`) does not replace
the files referenced by filters with auxiliary files as `EmbyJobPlanner` does. The behaviour is safe; this
is a functional limitation.

**D4. Minor findings.**
- `ServerFiles.Canonical` does not resolve symbolic links on Windows (`ServerFiles.cs:22`). This only affects
  development: Linux uses `realpath`.
- The rate-limiting key is based on `X-Real-IP` for any loopback connection (`CoordinatorHost.cs:341`).
  This is intended for the reverse proxy, but a local process can thereby choose the key.
- `/status` clears the `Worker` field of a finished encode (display only).

## 5. Bench limitations

- **L1. Local fallback of a playback with the real Emby command.** The fork-specific options (`-loglevel +timing`,
  `-segment_write_temp`) are rejected by the upstream ffmpeg: `Invalid loglevel "timing"` and `Unrecognized option
  'segment_write_temp'`. This fallback therefore fails here whereas it works on the NAS. Without these options, the upstream
  ffmpeg does produce the segments, but does not emit `SegmentComplete` lines. The following cases therefore remain to be
  validated on the NAS:
  - the worker-to-server resume at the exact segment;
  - the return from the server to a worker during playback.
  The remote path (translation by `EmbyJobPlanner` and `WorkerCommandBuilder`), on the other hand, was exercised with the real
  commands.
- Conversions are sent without the fork's options so that assembly and fallback run with the upstream
  ffmpeg.
- All processes run on a single PC, over loopback: no network latency. The load thresholds of the test
  workers are neutralized (they share the processor with the production worker).
- Direct Play (T11), Emby's own behaviour (reading the playlist, sending `q` when a client stops playing,
  `t` throttling) and browser playback at the join are not testable here.
- The +10 s mpegts offset is constant and has no effect on the comparisons.

## 6. Cleanup

All processes launched by the bench (coordinator, test workers, relays, ffmpeg) were stopped by PID.
Only the production worker (PID 13308) is still running, and `%LOCALAPPDATA%\EmbyRemoteWorker` was not touched.
The test data remains in `C:\emby\test\local\work\`: media, outputs, logs, `results.md` and
`results.jsonl`.
