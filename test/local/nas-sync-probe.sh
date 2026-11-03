#!/bin/sh
# Measures on the server, with Emby's own ffprobe, the values the check of an assembled conversion relies on
# (src/RemoteTranscode.Common/SyncCheck.cs), and prints what the check would conclude. Reads packets only, plus the
# first few dozen video packets decoded; writes nothing.
#
#   nas-sync-probe.sh SOURCE RESULT [ENCODERS] [RATE] [MAPS]
#
# ENCODERS  audio encoder of each audio stream of the result, comma-separated, as in Emby's command (aac, ac3, copy...);
#           default: "copy" where the codec is the source's, the codec's name otherwise
# RATE      Emby's -r (25, 24000/1001); default: none
# MAPS      source streams Emby maps (0:N), comma-separated; default: every audio stream, in order
#
# T: Emby's package folder, unless given in the environment. LIST: a concat.txt, to read its boundaries (default: quarters).
T="${T:-/volume4/@appstore/EmbyServer}"
export T
[ $# -ge 2 ] || { sed -n '2,12p' "$0"; exit 2; }
exec python3 - "$@" <<'PY'
import json, os, subprocess, sys, time

T = os.environ["T"]
FFPROBE = [T + "/lib/ld-linux-x86-64.so.2", "--library-path", T + "/lib:" + T + "/ffmpeg/lib", T + "/bin/ffprobe"]
TOLERANCE, ON_GRID, WINDOW, MAX_WINDOWS, PRE_TS, POST, TAIL = 0.003, 0.002, 1.5, 32, 2.5, 2.0, 30.0
PRIMING = {"copy": 0, "aac": 1024, "libmp3lame": 1105, "mp3": 1105, "ac3": 256, "eac3": 256, "libopus": 312, "opus": 312,
           "mp2": 481, "libtwolame": 481, "libvorbis": 128, "vorbis": 128, "flac": 0, "alac": 0, "dca": 0, "dts": 0}
ENTRIES = "format=format_name,start_time,duration:stream=index,codec_type,codec_name,sample_rate,start_time,r_frame_rate,avg_frame_rate:stream_disposition=attached_pic"
spent = 0.0

def probe(*args):
    global spent
    t = time.time()
    r = subprocess.run(FFPROBE + ["-v", "error"] + list(args), stdout=subprocess.PIPE, stderr=subprocess.PIPE, universal_newlines=True)
    spent += time.time() - t
    if r.returncode:
        print("  FFPROBE CODE", r.returncode, r.stderr[-300:])
    return json.loads(r.stdout or "{}")

def num(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None

def rate(v):
    try:
        n, d = v.split("/")
        return float(d) / float(n) if float(n) > 0 and float(d) > 0 else None
    except (AttributeError, ValueError):
        return None

def first_frame(path):
    for interval in ("%+#32", "%+15"):
        j = probe("-select_streams", "V:0", "-read_intervals", interval, "-show_entries", "frame=best_effort_timestamp_time:format=start_time", "-print_format", "json", path)
        times = [t for t in (num(f.get("best_effort_timestamp_time")) for f in j.get("frames", [])) if t is not None]
        if times:
            return min(times) - (num(j.get("format", {}).get("start_time")) or 0), interval, len(times)
    return None, None, 0

def intervals(meta, windows):
    start = num(meta["format"].get("start_time")) or 0
    duration = num(meta["format"].get("duration")) or 0
    pre = PRE_TS if "mpegts" in meta["format"].get("format_name", "") else 0.5
    parts = ["%+#400"]
    for s in meta["streams"]:
        st = num(s.get("start_time"))
        if s["codec_type"] in ("video", "audio") and st is not None and st - start > 3:
            parts.append("%.6f%%%.6f" % (max(0, st - pre), st + 1 + POST))
    parts += ["%.6f%%%.6f" % (max(0, start + w - pre), start + w + WINDOW + POST) for w in windows]
    parts.append("%.6f%%%.6f" % (max(0, start + duration - TAIL), start + duration + 3600))
    return ",".join(parts)

def read(path, meta, windows):
    j = probe("-read_intervals", intervals(meta, windows), "-show_entries", "packet=stream_index,pts_time,dts_time,duration_time", "-print_format", "json", path)
    packets = {}
    for p in j.get("packets", []):
        packets.setdefault(p.get("stream_index"), {})[p.get("pts_time") or p.get("dts_time")] = (num(p.get("pts_time")), num(p.get("dts_time")), num(p.get("duration_time")))
    return {k: list(v.values()) for k, v in packets.items()}

def nearest(sorted_times, t):
    return min(sorted_times, key=lambda x: abs(x - t)) - t if sorted_times else None

def rem(v, step):
    return v - round(v / step) * step

def mean_gap(times):
    gaps = sorted(b - a for a, b in zip(times, times[1:]) if b > a)
    if not gaps:
        return 0
    median = gaps[len(gaps) // 2]
    regular = [g for g in gaps if abs(g - median) <= median / 4]
    return sum(regular) / len(regular) if regular else median

def step(times, sample_rate):
    gaps = sorted(b - a for a, b in zip(times, times[1:]) if b > a)
    if not gaps:
        return 0
    median = gaps[len(gaps) // 2]
    regular = [g for g in gaps if abs(g - median) <= 1.5 * TOLERANCE]
    mean = sum(regular) / len(regular) if regular else median
    return round(mean * sample_rate) / sample_rate if sample_rate > 0 else mean

def ms(v):
    return "n/a" if v is None else "%+.2f ms" % (v * 1000)

source, result = sys.argv[1], sys.argv[2]
args = sys.argv[3:]
meta_s = probe("-show_entries", ENTRIES, "-print_format", "json", source)
meta_r = probe("-show_entries", ENTRIES, "-print_format", "json", result)
fs = num(meta_s["format"].get("start_time"))
print("SOURCE format=%s file start=%s duration=%s" % (meta_s["format"].get("format_name"), meta_s["format"].get("start_time"), meta_s["format"].get("duration")))
print("RESULT format=%s file start=%s duration=%s" % (meta_r["format"].get("format_name"), meta_r["format"].get("start_time"), meta_r["format"].get("duration")))
duration = max(num(meta_s["format"].get("duration")) or 0, num(meta_r["format"].get("duration")) or 0)

boundaries = []
if os.environ.get("LIST"):
    at = 0.0
    for line in open(os.environ["LIST"]):
        if line.startswith("duration "):
            at += float(line.split()[1])
            boundaries.append(at)
inside = sorted(b for b in boundaries if WINDOW < b < duration - WINDOW)
chosen = inside if len(inside) <= MAX_WINDOWS else [inside[int((i + 0.5) * len(inside) / MAX_WINDOWS)] for i in range(MAX_WINDOWS)]
for q in (0.25, 0.5, 0.75):
    if duration > 4 * WINDOW and not any(abs(c - duration * q) < duration / 8 for c in chosen):
        chosen.append(duration * q)
windows = sorted(max(0, c - WINDOW / 2) for c in chosen)

first, how, count = first_frame(source)
print("first frame from the decoder: %s s from the file's start (%d frames, %s)" % (first, count, how))
packets_s, packets_r = read(source, meta_s, windows), read(result, meta_r, windows)
print("%d windows read, ffprobe time so far %.1f s" % (len(windows), spent))

verdicts = []
def judge(ok, text):
    print(("  ok      " if ok else "  REJECT  ") + text)
    if not ok:
        verdicts.append(text)

if fs is None or first is None:
    judge(False, "file start or first frame unreadable")
video_s = next((s for s in meta_s["streams"] if s["codec_type"] == "video" and not s.get("disposition", {}).get("attached_pic")), None)
video_r = next((s for s in meta_r["streams"] if s["codec_type"] == "video" and not s.get("disposition", {}).get("attached_pic")), None)
if fs is not None and first is not None and video_s and video_r:
    raw = packets_s.get(video_s["index"], [])
    approximate = any(p[0] is None for p in raw)
    delay = first - (min(p[1] for p in raw if p[1] is not None) - fs) if approximate else 0
    frames_s = sorted(((p[1] + delay) if approximate else p[0]) - fs for p in raw if (p[1] if approximate else p[0]) is not None)
    frames_r = sorted(p[0] for p in packets_r.get(video_r["index"], []) if p[0] is not None)
    print("VIDEO source stream start=%s r_frame_rate=%s first packets %s; no pts on some packets: %s (decoding times + %s)" % (
        video_s.get("start_time"), video_s.get("r_frame_rate"), ["%.6f" % x for x in frames_s[:3]], approximate, ms(delay)))
    print("VIDEO result stream start=%s r_frame_rate=%s first packets %s" % (video_r.get("start_time"), video_r.get("r_frame_rate"), ["%.6f" % x for x in frames_r[:3]]))
    if frames_s and frames_r:
        judge(abs(frames_r[0] - first) <= TOLERANCE, "video start: %.6f for %.6f (%s, tolerance %s)" % (frames_r[0], first, ms(frames_r[0] - first), ms(TOLERANCE)))
        out_frame = rate(args[1]) if len(args) > 1 and args[1] not in ("", "-") else mean_gap(frames_r)
        src_frame = rate(video_s.get("r_frame_rate")) or mean_gap(frames_s)
        same = bool(src_frame) and abs(src_frame - out_frame) <= 0.002 * src_frame
        print("  frame: output %.6f s, source %.6f s, same rate: %s" % (out_frame, src_frame or 0, same))
        for w in windows:
            sw = [t for t in frames_s if w <= t <= w + WINDOW]
            if not sw:
                print("  window %.3f: no source frame" % w)
                continue
            grid = same and all(abs(rem(t - first, src_frame)) <= ON_GRID for t in sw)
            tol = TOLERANCE if grid else out_frame / 2 + TOLERANCE
            dist = [nearest(frames_r, t) for t in sw]
            worst = max(dist, key=lambda d: abs(d))
            near = [t for t in frames_r if w <= t <= w + WINDOW]
            over = any(b - a < out_frame / 4 for a, b in zip(near, near[1:]))
            judge(abs(worst) <= tol and not over, "video window %.3f: %d frames, worst %s, on the grid: %s, tolerance %s%s" % (w, len(sw), ms(worst), grid, ms(tol), ", OVERLAP" if over else ""))
        end_tol = max(3 * out_frame, delay + out_frame) if approximate else TOLERANCE if same and abs(rem(frames_s[-1] - first, src_frame)) <= ON_GRID else out_frame + TOLERANCE
        judge(abs(frames_r[-1] - frames_s[-1]) <= end_tol, "video end: last frame %.6f for %.6f (%s, tolerance %s)" % (frames_r[-1], frames_s[-1], ms(frames_r[-1] - frames_s[-1]), ms(end_tol)))
    else:
        judge(False, "no video packet read")

audio_s = [s for s in meta_s["streams"] if s["codec_type"] == "audio"]
audio_r = [s for s in meta_r["streams"] if s["codec_type"] == "audio"]
if len(args) > 2 and args[2]:
    wanted = [int(x) for x in args[2].split(",")]
    audio_s = [s for w in wanted for s in meta_s["streams"] if s["index"] == w and s["codec_type"] == "audio"]
judge(len(audio_s) == len(audio_r), "audio streams: %d for %d" % (len(audio_r), len(audio_s)))
encoders = args[0].split(",") if args and args[0] else []
for i, (s, r) in enumerate(zip(audio_s, audio_r)):
    enc = encoders[i] if i < len(encoders) else ("copy" if s.get("codec_name") == r.get("codec_name") else r.get("codec_name"))
    from_s = sorted((((p[0] if p[0] is not None else p[1]) - (fs or 0)), p[2] or 0) for p in packets_s.get(s["index"], []) if (p[0] if p[0] is not None else p[1]) is not None)
    to = sorted(((p[0] if p[0] is not None else p[1]), p[2] or 0) for p in packets_r.get(r["index"], []) if (p[0] if p[0] is not None else p[1]) is not None)
    print("AUDIO %d encoder %s: source %s stream start=%s first packet %s; result %s stream start=%s first packet %s" % (
        i + 1, enc, s.get("codec_name"), s.get("start_time"), "%.6f" % from_s[0][0] if from_s else None, r.get("codec_name"), r.get("start_time"), "%.6f" % to[0][0] if to else None))
    start = num(s.get("start_time"))
    priming = PRIMING.get(enc)
    if priming is None and not enc.startswith("pcm_"):
        judge(False, "audio %d: priming of %s not known" % (i + 1, enc))
        continue
    priming = priming or 0
    sr = int(num(r.get("sample_rate")) or 0)
    if not from_s or not to or start is None or (priming and not sr):
        judge(False, "audio %d: a value is missing" % (i + 1))
        continue
    expected = from_s[0][0] if enc == "copy" else start - (fs or 0) - (priming / float(sr) if priming else 0)
    judge(abs(to[0][0] - expected) <= TOLERANCE, "audio %d start: first packet %.6f for %.6f, priming %d samples (%s, tolerance %s)" % (i + 1, to[0][0], expected, priming, ms(to[0][0] - expected), ms(TOLERANCE)))
    ts, tr = [p[0] for p in from_s], [p[0] for p in to]
    ss, sr_step = step(ts, int(num(s.get("sample_rate")) or 0)), step(tr, sr)
    margin = max(ss, sr_step)
    for w in windows:
        a = [t for t in ts if w <= t <= w + WINDOW]
        b = [t for t in tr if w <= t <= w + WINDOW]
        if not a and not b:
            continue
        if enc == "copy":
            worst = max([abs(nearest(tr, t)) for t in a] + [abs(nearest(ts, t)) for t in b])
            judge(worst <= TOLERANCE, "audio %d window %.3f: %d packets, worst %s" % (i + 1, w, len(a), ms(worst)))
            continue
        gone = max([abs(nearest(tr, t)) for t in a] + [abs(nearest(ts, t)) for t in b])
        source_on = all(abs(rem(t - ts[0], ss)) <= TOLERANCE for t in a)
        off = max([abs(rem(t - tr[0], sr_step)) for t in b] or [0])
        judge(gone <= 2 * margin + TOLERANCE and (off <= TOLERANCE or not source_on),
              "audio %d window %.3f: nearest sound %s (limit %s), result off its grid by %s, source on its own: %s" % (i + 1, w, ms(gone), ms(2 * margin + TOLERANCE), ms(off), source_on))
    end_s, end_r = from_s[-1][0] + from_s[-1][1], to[-1][0] + to[-1][1]
    late = margin + TOLERANCE if enc == "copy" or enc not in ("aac", "ac3", "eac3", "libopus", "opus", "flac") else sr_step / 2 + TOLERANCE if enc == "aac" else 2 * TOLERANCE
    early = margin + TOLERANCE if enc == "copy" else 2 * TOLERANCE
    judge(end_r - end_s <= late and end_s - end_r <= early, "audio %d end: %.6f for %.6f (%s, limits +%s -%s)" % (i + 1, end_r, end_s, ms(end_r - end_s), ms(late), ms(early)))

print("ffprobe time: %.1f s" % spent)
print("CONCLUSION: " + ("in step" if not verdicts else "REJECTED, %d finding(s); first: %s" % (len(verdicts), verdicts[0])))
PY
