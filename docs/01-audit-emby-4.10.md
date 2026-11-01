# Emby integration audit — milestone 1

Version française : [01-audit-emby-4.10.fr.md](01-audit-emby-4.10.fr.md)

Date: 2026-10-26. All the observations below were made on the test server, without modifying it.

## Tested environment

| Item | Observed value |
|---|---|
| Emby Server | 4.10.2.0 (Synology package `emby-server-synology72`, 4.10.2.0-724100200) |
| NAS | Synology DS1522+, AMD Ryzen Embedded R1600 (4 threads), 8 GB RAM, DSM 7.4.1 |
| NAS GPU | none (`/dev/dri` missing) → all local transcoding is software-based |
| Emby ffmpeg | fork `5.1-emby_2023_06_25_p6` (softworkz / Emby LLC), `/var/packages/EmbyServer/target/bin/ffmpeg` |
| Launch | `target/bin/emby-server` → `EmbyServer -ffmpeg … -ffprobe … -ffdetect …`, user `emby` |
| Data | `/var/packages/EmbyServer/var` → `/volume4/@appdata/EmbyServer` |
| Transcoding folder | `/var/packages/EmbyServer/var/transcoding-temp` |
| Internal Emby port | 18096 (HTTP), exposed through an HTTPS reverse proxy `emby.example.com` |
| Libraries | `/volume4/Film`, `/volume4/Series`, `/volume4/Emissions`, `/volume4/Documentaire` (also shared over SMB) |
| Dev worker | Windows 11 Enterprise 26200, Intel Core Ultra 5 225U (Intel iGPU), FFmpeg 9.0.2 gyan.dev |

## 1. Intercepting the transcoding request

**No SDK API allows the transcoder to be replaced.** `IMediaEncoder` and `IFfmpegManager` expose the configuration and a few operations, but no extension point for running a transcode elsewhere.

**Extension point chosen: Emby's custom ffmpeg path.**
Emby 4.10 contains an `ffmpeg` configuration (`FfmpegConfiguration`) with `UseCustomEncoderPath` / `CustomEncoderPath`, which can be changed through the admin API:

- `GET /Encoding/FfmpegOptions` and `POST /Encoding/FfmpegOptions` (Admin role), stored in `config/ffmpeg.xml`.
- Applied **on the fly** (`FfmpegManager.FfmpegConfiguration` is replaced immediately): **no restart**.
- **Does not modify any file of the package**: survives Emby updates; rolling back = setting `UseCustomEncoderPath=false` again.
- ffprobe and ffdetect have separate settings: they stay on Emby's binaries.

Limits and sensitivity to updates:

- Emby displays for this setting: "temporary settings for development. Using alternate ffmpeg versions is not supported". It is therefore **not a documented extension point**; it may disappear or change.
- The relay must mimic the protocol of Emby's ffmpeg (see below). This protocol is internal and may change with every Emby version → the non-regression test has to be rerun after each update, with automatic fallback to the original ffmpeg in case of anomaly.

## 2. Protocol between Emby and its ffmpeg (observed and decompiled)

Actual command of an HLS transcode (test session, movie "Tanguy, le retour"):

```
ffmpeg -loglevel +timing -y -print_graphs_file ".../logs/ffmpeg-transcode-<id>_1graph.txt"
  -copyts -start_at_zero -f mp4 -c:v:0 h264 -noautorotate -i "/volume4/Film/Tanguy, le retour.mp4"
  -filter_complex "[0:0]scale@f1=width=720:height=388,subtitles@f2=filename='/volume4/Film/Tanguy, le retour.fre.forced.vtt':fontsdir='/var/packages/EmbyServer/var/fonts':charenc=utf-8[f2_out0]"
  -map [f2_out0] -map 0:1 -sn -c:v:0 libx264 … -c:a:0 aac …
  -f segment -segment_format mpegts -segment_list ".../transcoding-temp/F55394/F55394.m3u8" -segment_list_type m3u8
  -segment_time 00:00:03.000 -segment_start_number 0 -individual_header_trailer 0 -write_header_trailer 0
  -segment_write_temp 1 ".../transcoding-temp/F55394/F55394_%d.ts"
```

What Emby expects from the process:

| Channel | Content | Consequence for the relay |
|---|---|---|
| stderr | timestamped lines `HH:mm:ss.fff …` (`-loglevel +timing`, specific to the fork) | the relay rewrites the lines with this prefix |
| stderr | `SegmentComplete=video:0 Index=N Start=… End=… Duration=… … filename=X_N.ts` after the `.tmp` is renamed | **essential**: Emby only serves a segment after this message (`TranscodingStateReader`) |
| stderr | progress `elapsed=… frame=… fps=… time=… throttle=off speed=…` | re-emitted from the worker's progress |
| stdin | `?` → help listing the commands; `t` + value → throttling; `q` → stop | `q` relayed to the worker; `t` not advertised in the prototype (Emby then disables throttling) |
| files | `.ts` segments written as `.tmp` then renamed (`-segment_write_temp`, specific to the fork) | the relay writes `.tmp` then renames |
| exit code | 0 when stopped with `q` | relayed |

Fork-specific options, rejected by standard FFmpeg: `-loglevel +timing`, `-segment_write_temp`. The rest of the command was run as is by FFmpeg 9.0.2 on the worker (successful local test, 4 segments produced).

## 3. Seeking and stopping

Observed: on stop (`DELETE /Videos/ActiveEncodings`), Emby sends `q` on stdin ("Stopping ffmpeg process with q command"), then kills the process if needed. A seek launches a new ffmpeg with `-ss` and `-segment_start_number`. The relay therefore only has to translate "end of my process / `q`" into "cancel the remote job"; each relaunch is a new job.

## 4. Choices for media and streams

- **Input**: the worker reads files over **authenticated HTTP served by the coordinator**, limited to the files referenced in the job's command (per-job token). This also covers files outside the SMB shares (subtitles extracted into `cache/temp`, fonts in `var/fonts`). SMB with path mapping remains a possible optimization.
- **Output**: the worker produces the segments locally and sends them to the coordinator, which forwards them to the relay; the relay (running as the `emby` user) writes them into `transcoding-temp`. **Emby keeps serving the segments itself**: access controls, HTTPS, reverse proxy and clients are unchanged.
- **Direct Play / Direct Stream**: do not call ffmpeg for the video, so they do not go through the relay.

## 5. Access controls

The client never talks to the worker. Authorizations remain Emby's (PlaybackInfo, HLS URL with token). The worker only has access to the files of the current job, with a short-lived token, and never receives an arbitrary command: the coordinator sends a validated argument list (encoders, filters, paths) and the worker validates it again.

## 6. Points of attention identified

- The NAS only has 8 GB of RAM, already heavily used (T-Pot, etc.): the coordinator must stay lightweight.
- Emby's logs contain access tokens in clear text (Emby behaviour, to keep in mind when exporting logs).
- Emby's arguments are generated for **software** transcoding (the NAS has no GPU). By default, the worker uses **the same software encoder as Emby**, so that hand-overs between worker and server remain invisible. Encoding on the worker's GPU is an **option** in its settings, separate for conversions and for playback: the worker then translates the encoder (`libx264` → `h264_nvenc`/`h264_qsv`/`h264_amf`) while keeping the maximum bitrate and GOP size; Emby's profile is left to the hardware encoder's choice (Emby requests `main10` from x265 even for an 8-bit picture).

  *Update of 2026-10-29: this document describes the initial audit. The current behaviour (playback starting on the server, prefetching, conversions in slices) is described in `README.md` and in `final/Documentation-Transcodage-distribue.pdf`.*
- A worker that disappears during playback: the relay relaunches the job on another worker or locally from the last complete segment (`-ss` + `-segment_start_number`). The client-side result remains to be validated (probable micro-interruption, no transparent migration promised).

## 7. Prototype results (2026-10-26)

| Test | Result |
|---|---|
| Playback through Emby (dedicated API session, movie "Tanguy, le retour") | ✅ segments served by Emby, produced on the worker (h264_qsv, burned-in subtitles) |
| Seek to 10:00 | ✅ Emby stops the job (`q`) and starts a new one at segment 200 on the worker |
| Stopping playback | ✅ `q` relayed, worker's ffmpeg stopped (exit code 0), job folder deleted |
| Media not selected for offloading | ✅ Emby's local ffmpeg |
| Coordinator stopped | ✅ Emby's local ffmpeg |
| Worker killed mid-playback | ✅ local resume at the next segment, continuous segments (200→244) |
| Segment timestamps | ✅ identical to Emby's native ffmpeg |
| Hardware detection | ✅ NVENC/AMF listed by FFmpeg but rejected in the real test, QSV validated |

First-segment latency: 11 to 17 s on the development PC, of which about 5 s for launching
`ffmpeg.exe` alone (corporate security software). To be measured on a personal PC.

Emby was restored to its original configuration after the tests (`UseCustomEncoderPath=false`).
