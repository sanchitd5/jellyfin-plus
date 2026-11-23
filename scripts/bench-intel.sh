#!/usr/bin/env bash
# bench-intel.sh - the Intel Arc end-to-end matrix (docs/architecture/GPU_VENDORS.md, 5.8) as a script.
#
#   scripts/bench-intel.sh --dry-run            print the plan: gates, thresholds, the commands; run nothing
#   scripts/bench-intel.sh [--rows 2,3,7]       run the rows (default: all 13), median of BENCH_RUNS runs
#   scripts/bench-intel.sh --list               one line per row
#   scripts/bench-intel.sh --no-gates ...       skip the quiet-box gates; needs BENCH_CONFIRM_NO_GATES=1 (exit 2 without it)
#
# It runs INSIDE the Intel image (docker/intel/Dockerfile) on the GPU node, from the bench Job. Rows
# 1, 6, 10 and 12 and the end-to-end halves of rows 3 and 4 drive a Jellyfin through its API
# (BENCH_JELLYFIN_URL, BENCH_JELLYFIN_TOKEN); without a token those parts are SKIP, never PASS. Row 13
# is a human check and is always MANUAL. Every figure is the median of BENCH_RUNS (3) runs.
#
# Statuses: PASS, FAIL, SKIP (an input this run lacks: said which), MANUAL (a person must look).
# Results go to stdout as `result:` lines and to $BENCH_OUT/results.jsonl. Exit 1 if any row FAILs,
# 2 for a refusal (bad argument, a gate closed, a missing tool), 0 otherwise. A SKIP is not a pass:
# read the summary.
#
# The thresholds are STARTING POINTS (the owner's decision page); the run records the measured value
# either way. Nothing here has run on an Arc card: --dry-run and the plan test prove the plan only.
#
# Inputs (all optional; the defaults are the image's own paths):
#   BENCH_FFMPEG          patched ffmpeg                 /usr/lib/jellyfin-ffmpeg-oidn/ffmpeg
#   BENCH_STOCK_FFMPEG    stock jellyfin-ffmpeg          /usr/lib/jellyfin-ffmpeg/ffmpeg
#   BENCH_CLIPS           the lossless clips             /clips      <title>_<height>p.mkv, see below
#   BENCH_OUT             results and outputs            $BENCH_CLIPS/bench-out
#   BENCH_TITLE           clip title prefix              tears       (the second source: BENCH_TITLE=anim)
#   BENCH_RENDER_NODE     the Arc render node            /dev/dri/renderD128
#   BENCH_SHADER          the FSRCNNX shader             /usr/share/jellyfin-shaders/FSRCNNX_x2_8-0-4-1.glsl
#   BENCH_ORT_MODEL       an ONNX model for row 5        (none: row 5 SKIPs)
#   BENCH_FSR2_OPTS       extra fsr2 filter options      (none)
#   BENCH_RUNS            runs per figure                3          (row 5 runs once per clip, see ORT_FRAMES)
#   BENCH_RUN_TIMEOUT     seconds each measured ffmpeg may run (then TERM, KILL 10 s later; the row records FAIL timeout after Ns)   240
#   BENCH_JELLYFIN_URL    the test Jellyfin              http://127.0.0.1:8096
#   BENCH_JELLYFIN_TOKEN  an admin API token, sent as the MediaBrowser Authorization header   (none)
#   BENCH_ASSERT_QUIET=1  the caller asserts no vzdump, no Plex or production Jellyfin transcode
#   BENCH_PLEX_TRANSCODING=1  the caller asserts one Plex hardware transcode is running (row 11 only)
#   BENCH_CONFIRM_NO_GATES=1  required with --no-gates: the caller owns the quiet-box check the gates make
#   BENCH_PSI_FILE        memory pressure file for the gate    /proc/pressure/memory
#   BENCH_SHIM_LOG        the shim's log; rows 1 and 12 read only what is appended during the run   /var/log/jellyfin-upscale.log
#   BENCH_NEURAL_LEVEL    the neural level row 6 asks for      realesr-anime-x2 (realesr-anime-x2|realesr-anime-x4|realesr-general-x4)
#   BENCH_BLOCK_WINDOWS   the gate's blocked clock windows, HHMM-HHMM comma list, start included, end not   0100-0700,1630-1830
#   BENCH_TZ              the zone those windows are read in    Australia/Melbourne
#
# Clips: <title>_{360,720,1080,2160}p.mkv lossless masters (the 360/720/1080 are `scale=flags=area`
# from the 2160p one), still_720p.png (one frame), pan_720p.mkv and pan_1080p.mkv (a panning cut).
set -euo pipefail

HERE="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
# shellcheck source=ffmpeg-vendor.sh
. "$HERE/ffmpeg-vendor.sh"

PATCHED_FFMPEG="${BENCH_FFMPEG:-/usr/lib/jellyfin-ffmpeg-oidn/ffmpeg}"
STOCK_FFMPEG="${BENCH_STOCK_FFMPEG:-/usr/lib/jellyfin-ffmpeg/ffmpeg}"
PREFIX="$(dirname "$PATCHED_FFMPEG")"
CLIPS="${BENCH_CLIPS:-/clips}"
OUT="${BENCH_OUT:-$CLIPS/bench-out}"
TITLE="${BENCH_TITLE:-tears}"
NODE="${BENCH_RENDER_NODE:-/dev/dri/renderD128}"
SHADER="${BENCH_SHADER:-/usr/share/jellyfin-shaders/FSRCNNX_x2_8-0-4-1.glsl}"
ORT_MODEL="${BENCH_ORT_MODEL:-}"
FSR2_OPTS="${BENCH_FSR2_OPTS:-}"
RUNS="${BENCH_RUNS:-3}"
RUN_TIMEOUT="${BENCH_RUN_TIMEOUT:-240}"
# Row 5 (Real-ESRGAN x2 through ORT on OpenVINO) runs ~1-2 fps on an A380 and compiles the model on every
# invocation: RUNS x the whole clip took hours. 48 frames, one run per clip, bounds it to minutes and still
# shows whether the model runs on the GPU and roughly how fast. The fps it reports includes the compile time.
ORT_FRAMES=48
ORT_RUNS=1
JF_URL="${BENCH_JELLYFIN_URL:-http://127.0.0.1:8096}"
JF_TOKEN="${BENCH_JELLYFIN_TOKEN:-}"
SHIM_LOG="${BENCH_SHIM_LOG:-/var/log/jellyfin-upscale.log}"
PSI_FILE="${BENCH_PSI_FILE:-/proc/pressure/memory}"
NEURAL_LEVEL="${BENCH_NEURAL_LEVEL:-realesr-anime-x2}"
BLOCK_TZ="${BENCH_TZ:-Australia/Melbourne}"
BLOCK_WINDOWS="${BENCH_BLOCK_WINDOWS:-0100-0700,1630-1830}"

ALL_ROWS=(1 2 3 4 5 6 7 8 9 10 11 12 13)
DRY=0
GATES=1
ROWS=()

say() { echo "bench: $*"; }
die() { echo "bench: $*" >&2; exit 2; }

# ---- the matrix: one place for each row's name, thresholds and command ------------------------------

row_title() {
    case "$1" in
        1)  echo "Vendor record" ;;
        2)  echo "FSRCNNX 360p to 1080p, software decode" ;;
        3)  echo "FSRCNNX 720p to 1080p" ;;
        4)  echo "FSRCNNX 1080p to 2160p" ;;
        5)  echo "Neural (ORT on OpenVINO), 360p and 720p to 1080p" ;;
        6)  echo "Routing by source height" ;;
        7)  echo "fsr2 without CUDA, still pixels" ;;
        8)  echo "fsr2 without CUDA, moving content" ;;
        9)  echo "Hardware decode off/on" ;;
        10) echo "QSV encode" ;;
        11) echo "Contention with one Plex hardware transcode" ;;
        12) echo "Client, headless" ;;
        13) echo "Client, human" ;;
        *)  return 1 ;;
    esac
}

row_must() {
    case "$1" in
        1)  echo "vendor=intel; render node $NODE; Vulkan vk@dr; no NVIDIA-only level offered; fsr2 offered (marker present); no nvenc/cuda string in any command line" ;;
        2)  echo "fps>=100; xpsnr>=bicubic+0.5dB" ;;
        3)  echo "end-to-end fps>=1.2x source fps; xpsnr>=bicubic+0.3dB" ;;
        4)  echo "scaler-only fps>=34; end-to-end with QSV encode >=1.2x source fps; xpsnr>=bicubic+0.3dB" ;;
        5)  echo "session record device=openvino (not a CPU fallback); fps measured at 360p and 720p; xpsnr>=FSRCNNX where it runs; slower than real time is a valid result" ;;
        6)  echo "NeuralMaxSourceHeight=480: a 720p source goes to the shader path and the record says why" ;;
        7)  echo "after the first ceil(8*scale^2) frames: mean consecutive-frame luma difference<=0.5, per-frame max<=16 in 99% of frames; FSRCNNX control exactly 0" ;;
        8)  echo "xpsnr on the panning cut >= bicubic (a miss means the image ships without fsr2-nocuda: a result, not a blocker)" ;;
        9)  echo "on: -hwaccel vaapi and hwmap=derive_device=vulkan, no 'Conversion failed!', video engine busy, cpu time per frame >=30% lower, xpsnr(on,off)>=50dB; off: command identical to a build without hardware decode" ;;
        10) echo "h264_qsv/hevc_qsv chosen; the served segment's codec matches; encoderSessions counts the session; /Gpu/Load within +-10 points of the fdinfo figure" ;;
        11) echo "one stream holds fps>=36 while one Plex hardware transcode runs" ;;
        12) echo "forced transcode at each target height: ffprobe shows the target height and a QSV codec; the transcode log carries the expected filter" ;;
        13) echo "browser, after a hard reload: Enhance menu, window.__jellyfinPlus.installed===true; 5 min at two levels plus one 2160p output, two seeks, no stall over 2s" ;;
        *)  return 1 ;;
    esac
}

# Thresholds the runner compares against. One definition: the plan prints them and the rows read them.
FPS_R2=100
XPSNR_R2=0.5
E2E_X=1.2
XPSNR_R3=0.3
FPS_R4=34
XPSNR_R4=0.3
FPS_R11=36
STILL_MEAN_MAX=0.5
STILL_PEAK_MAX=16
STILL_PEAK_PCT=99
HWDEC_CPU_GAIN=30
HWDEC_XPSNR_MIN=50
LOAD_TOLERANCE=10

clip() { echo "$CLIPS/${TITLE}_${1}p.mkv"; }

# ---- command builders: fill CMD; the plan prints it and the run executes it, so they cannot differ ---

INIT=(-init_hw_device "drm=dr:$NODE" -init_hw_device "vulkan=vk@dr" -filter_hw_device vk)

chain_shader() { echo "format=yuv420p,hwupload,libplacebo=w=$1:h=$2:upscaler=ewa_lanczos:custom_shader_path=$SHADER,hwdownload,format=yuv420p"; }
chain_bicubic() { echo "scale=$1:$2:flags=bicubic,format=yuv420p"; }
chain_ort() { echo "format=yuv420p,format=gbrpf32le,ort=model=${ORT_MODEL:-MODEL}:device=openvino,format=yuv420p,hwupload,libplacebo=w=$1:h=$2,hwdownload,format=yuv420p"; }
chain_fsr2() { echo "format=yuv420p,format=gbrpf32le,fsr2=w=$1:h=$2${FSR2_OPTS:+:$FSR2_OPTS},format=yuv420p,hwupload,libplacebo=w=$1:h=$2,hwdownload,format=yuv420p"; }
chain_hwdec() { echo "setparams=range=tv,hwmap=derive_device=vulkan,libplacebo=w=$1:h=$2:upscaler=ewa_lanczos:custom_shader_path=$SHADER,hwdownload,format=nv12"; }

# cmd_run <clip> <chain> [output] [frames]: a measured run; output omitted = discard (-f null), else lossless
# ffv1; frames given = stop after that many frames (-frames:v).
cmd_run() {
    CMD=("$PATCHED_FFMPEG" -hide_banner -nostdin -y -benchmark -stats "${INIT[@]}" -i "$1" -vf "$2" -an)
    if [[ -n "${4:-}" ]]; then CMD+=(-frames:v "$4"); fi
    if [[ -n "${3:-}" ]]; then CMD+=(-c:v ffv1 "$3"); else CMD+=(-f null -); fi
}
# cmd_run_hw: as cmd_run with VA-API decode and the VA-API to Vulkan device (GPU_VENDORS.md 5.6).
cmd_run_hw() {
    CMD=("$PATCHED_FFMPEG" -hide_banner -nostdin -y -benchmark -stats
         -init_hw_device "vaapi=va:$NODE" -init_hw_device "vulkan=vk@va" -hwaccel vaapi -hwaccel_output_format vaapi -filter_hw_device vk
         -i "$1" -vf "$2" -an)
    if [[ -n "${3:-}" ]]; then CMD+=(-c:v ffv1 "$3"); else CMD+=(-f null -); fi
}
# cmd_xpsnr <candidate> <master> <stats>: XPSNR of the candidate against the master.
cmd_xpsnr() {
    CMD=("$PATCHED_FFMPEG" -hide_banner -nostdin -i "$1" -i "$2" -lavfi "xpsnr=stats_file=$3" -f null -)
}
# cmd_still <png> <chain> <metadata-file>: a looped still, consecutive-frame difference per frame.
cmd_still() {
    CMD=("$PATCHED_FFMPEG" -hide_banner -nostdin -y "${INIT[@]}" -loop 1 -framerate 24 -t 10 -i "$1"
         -vf "$2,tblend=all_mode=difference,signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=$3" -an -f null -)
}

# Frames to ignore at the start of the still test: ceil(8 * scale^2), the first two jitter cycles.
still_skip() { awk -v s="$1" 'BEGIN { v = 8 * s * s; printf "%d\n", (v == int(v)) ? v : int(v) + 1 }'; }

# ff_exec: run CMD under a hard time limit. The plan prints CMD without this wrapper; every measured run goes
# through it, so a hung chain (a model compile, a GPU hang) ends the row instead of the whole bench.
ff_exec() { timeout --kill-after=10 "$RUN_TIMEOUT" "${CMD[@]}"; }
# timed_out <exit status>: 124 is timeout's own; 137 is the KILL it sends when ffmpeg ignored the TERM.
timed_out() { [[ "$1" == 124 || "$1" == 137 ]]; }
# why <exit status> <text>: "timeout after Ns" when the run was timed out, else the text.
why() { if timed_out "$1"; then echo "timeout after ${RUN_TIMEOUT}s"; else echo "$2"; fi; }

# ---- the plan --------------------------------------------------------------------------------------

plan_cmd() { # label, then CMD is set
    printf 'plan: row %s cmd: %s\n' "$1" "${CMD[*]}"
}

print_plan() {
    local r
    echo "plan: bench-intel: rows=${ROWS[*]} runs=$RUNS mode=dry-run"
    echo "plan: required filters: $(ffmpeg_required_filters intel) fsr2"
    echo "plan: gates: window outside $BLOCK_TEXT $BLOCK_TZ; memory PSI some avg60 < 1%; BENCH_ASSERT_QUIET=1 (no vzdump, no Plex or production Jellyfin transcode: the pod cannot see them)"
    echo "plan: every measured ffmpeg runs under timeout --kill-after=10 ${RUN_TIMEOUT}s (BENCH_RUN_TIMEOUT); row 5 runs ${ORT_RUNS}x per clip, ${ORT_FRAMES} frames"
    echo "plan: binary: $PATCHED_FFMPEG; stock: $STOCK_FFMPEG; clips: $CLIPS ($TITLE); out: $OUT; render node: $NODE"
    for r in "${ROWS[@]}"; do
        printf 'plan: row %s: %s\n' "$r" "$(row_title "$r")"
        printf 'plan: row %s must pass: %s\n' "$r" "$(row_must "$r")"
        case "$r" in
            1)  echo "plan: row 1 cmd: curl $JF_URL/JellyfinPlus/Status (admin token), then $JF_URL/JellyfinPlus/Session/bench-probe" ;;
            2)  cmd_run "$(clip 360)" "$(chain_shader 1920 1080)"; plan_cmd 2
                cmd_run "$(clip 360)" "$(chain_bicubic 1920 1080)"; plan_cmd 2
                echo "plan: row 2 threshold: fps>=$FPS_R2 xpsnr>=+$XPSNR_R2" ;;
            3)  cmd_run "$(clip 720)" "$(chain_shader 1920 1080)"; plan_cmd 3
                echo "plan: row 3 cmd: forced transcode of $(clip 720) through $JF_URL (end to end, 5 segments timed)"
                echo "plan: row 3 threshold: e2e>=${E2E_X}x xpsnr>=+$XPSNR_R3" ;;
            4)  cmd_run "$(clip 1080)" "$(chain_shader 3840 2160)"; plan_cmd 4
                echo "plan: row 4 cmd: forced transcode of $(clip 1080) at 2160p through $JF_URL (QSV encode, 5 segments timed)"
                echo "plan: row 4 threshold: fps>=$FPS_R4 e2e>=${E2E_X}x xpsnr>=+$XPSNR_R4" ;;
            5)  cmd_run "$(clip 360)" "$(chain_ort 1920 1080)" "" "$ORT_FRAMES"; plan_cmd 5
                cmd_run "$(clip 720)" "$(chain_ort 1920 1080)" "" "$ORT_FRAMES"; plan_cmd 5
                echo "plan: row 5 cmd: a 360p stream with neural=realesr-anime-x2, then NeuralDevice from $JF_URL/JellyfinPlus/Session/<its PlaySessionId>"
                echo "plan: row 5 threshold: device=openvino; largest NeuralMaxSourceHeight holding >=${E2E_X}x" ;;
            6)  echo "plan: row 6 cmd: set NeuralMaxSourceHeight=480 (restored after), transcode $(clip 720) with neural=$NEURAL_LEVEL, read Session/<id> NeuralRouting" ;;
            7)  cmd_still "$CLIPS/still_720p.png" "$(chain_fsr2 1920 1080)" "$OUT/still-fsr2.txt"; plan_cmd 7
                cmd_still "$CLIPS/still_720p.png" "$(chain_shader 1920 1080)" "$OUT/still-control.txt"; plan_cmd 7
                echo "plan: row 7 skip frames: 3x=$(still_skip 3) 2x=$(still_skip 2) 1.5x=$(still_skip 1.5)"
                echo "plan: row 7 threshold: mean<=$STILL_MEAN_MAX peak<=$STILL_PEAK_MAX in ${STILL_PEAK_PCT}% control==0" ;;
            8)  cmd_run "$CLIPS/pan_720p.mkv" "$(chain_fsr2 1920 1080)" "$OUT/pan-fsr2.mkv"; plan_cmd 8
                cmd_run "$CLIPS/pan_720p.mkv" "$(chain_bicubic 1920 1080)" "$OUT/pan-bicubic.mkv"; plan_cmd 8
                echo "plan: row 8 threshold: xpsnr(fsr2)>=xpsnr(bicubic)" ;;
            9)  cmd_run_hw "$(clip 1080)" "$(chain_hwdec 1920 1080)" "$OUT/hwdec-on.mkv"; plan_cmd 9
                cmd_run "$(clip 1080)" "$(chain_shader 1920 1080)" "$OUT/hwdec-off.mkv"; plan_cmd 9
                echo "plan: row 9 threshold: cpu gain>=${HWDEC_CPU_GAIN}% xpsnr(on,off)>=${HWDEC_XPSNR_MIN}dB no 'Conversion failed!'" ;;
            10) echo "plan: row 10 cmd: forced transcode through $JF_URL, ffprobe the segment codec, compare $JF_URL/JellyfinPlus/Gpu/Load with the fdinfo figure"
                echo "plan: row 10 threshold: |load - fdinfo| <= $LOAD_TOLERANCE points" ;;
            11) cmd_run "$(clip 1080)" "$(chain_shader 3840 2160)"; plan_cmd 11
                echo "plan: row 11 threshold: fps>=$FPS_R11 with BENCH_PLEX_TRANSCODING=1" ;;
            12) echo "plan: row 12 cmd: $JF_URL/Videos/<id>/master.m3u8 at 480p, 720p, 1080p and 2160p targets, first 3 segments, ffprobe" ;;
            13) echo "plan: row 13: MANUAL, the owner's browser check over a port-forward" ;;
        esac
    done
}

# ---- helpers for a real run ------------------------------------------------------------------------

RESULTS=()
FAILED=0
STREAM_SID=""
STREAM_SEG=""
STREAM_SPEED=0
STREAM_ERR=""
SHIM_LOG_START=0

# detail can carry server text (quotes, backslashes, newlines): jq writes the JSONL, the console line is one line.
record() { # row status detail
    printf 'result: row %s %s %s\n' "$1" "$2" "${3//$'\n'/ }"
    RESULTS+=("$1 $2")
    [[ "$2" != FAIL ]] || FAILED=1
    mkdir -p "$OUT"
    jq -nc --argjson row "$1" --arg status "$2" --arg detail "$3" '{row:$row,status:$status,detail:$detail}' >> "$OUT/results.jsonl"
}

need() { command -v "$1" >/dev/null 2>&1 || [[ -x "$1" ]] || die "missing tool: $1"; }

median() { sort -n | awk '{ a[NR] = $1 } END { if (NR) print a[int((NR + 1) / 2)] }'; }

ge() { awk -v a="$1" -v b="$2" 'BEGIN { exit !(a >= b) }'; }
le() { awk -v a="$1" -v b="$2" 'BEGIN { exit !(a <= b) }'; }

BLOCK_LO=()
BLOCK_HI=()
BLOCK_TEXT=""

# Fills BLOCK_LO/BLOCK_HI/BLOCK_TEXT from BLOCK_WINDOWS; a malformed list or zone is a refusal, never a gate left open.
parse_block_windows() {
    local item lo hi i items
    [[ "$BLOCK_TZ" =~ ^[A-Za-z0-9_+/:.-]+$ ]] || die "BENCH_TZ must be a time zone name such as Australia/Melbourne, got: $BLOCK_TZ"
    [[ "$BLOCK_WINDOWS" =~ ^[0-9]{4}-[0-9]{4}(,[0-9]{4}-[0-9]{4})*$ ]] \
        || die "BENCH_BLOCK_WINDOWS must be a comma list of HHMM-HHMM such as 0100-0415,1630-1830, got: $BLOCK_WINDOWS"
    IFS=, read -r -a items <<<"$BLOCK_WINDOWS"
    for item in "${items[@]}"; do
        lo=$((10#${item%-*}))
        hi=$((10#${item#*-}))
        (( lo % 100 <= 59 && lo < 2400 && hi % 100 <= 59 && hi <= 2400 && lo < hi )) \
            || die "BENCH_BLOCK_WINDOWS: $item is not a time window (HHMM 0000-2359, end up to 2400, start before end), got: $BLOCK_WINDOWS"
        BLOCK_LO+=("$lo")
        BLOCK_HI+=("$hi")
        BLOCK_TEXT+="${BLOCK_TEXT:+ and }$(printf '%02d:%02d-%02d:%02d' $((lo / 100)) $((lo % 100)) $((hi / 100)) $((hi % 100)))"
    done
}

check_gates() {
    local hm psi i
    [[ "${BENCH_ASSERT_QUIET:-}" == 1 ]] \
        || die "gate: set BENCH_ASSERT_QUIET=1 once you have checked there is no vzdump, no Plex transcode and no production Jellyfin transcode"
    # 10#: HHMM has a leading zero, which bash reads as octal (0830 and 0900 are then errors)
    hm=$((10#$(TZ="$BLOCK_TZ" date +%H%M)))
    for i in "${!BLOCK_LO[@]}"; do
        if (( hm >= BLOCK_LO[i] && hm < BLOCK_HI[i] )); then
            die "gate: $(printf '%04d' "$hm") $BLOCK_TZ is inside a blocked window ($BLOCK_TEXT), drain, trivy or backup time"
        fi
    done
    if [[ -r "$PSI_FILE" ]]; then
        psi=$(awk '/^some/ { split($3, a, "="); print a[2] }' "$PSI_FILE")
        le "${psi:-0}" 1 || die "gate: memory PSI some avg60=$psi >= 1%"
    fi
}

# fdinfo busy percent of one pid's DRM clients over a window: "<class> <percent>" per engine class.
# i915 exposes drm-engine-<class>: N ns; xe exposes drm-cycles-<class> and drm-total-cycles-<class>.
fdinfo_sample() {
    local f
    for f in /proc/"$1"/fdinfo/*; do
        awk '
            /^drm-client-id:/ { id = $2 }
            /^drm-engine-capacity-/ { next }
            /^drm-engine-/ { c = $1; sub(/^drm-engine-/, "", c); sub(/:$/, "", c); print "ns", id, c, $2 }
            /^drm-cycles-/ { c = $1; sub(/^drm-cycles-/, "", c); sub(/:$/, "", c); print "cy", id, c, $2 }
            /^drm-total-cycles-/ { c = $1; sub(/^drm-total-cycles-/, "", c); sub(/:$/, "", c); print "tc", id, c, $2 }
        ' "$f" 2>/dev/null || true
    done | sort -u
}
fdinfo_busy() { # pid seconds
    local a b
    a=$(fdinfo_sample "$1"); sleep "$2"; b=$(fdinfo_sample "$1")
    awk -v t="$2" '
        FNR == NR { before[$1 " " $2 " " $3] = $4; next }
        { d = $4 - before[$1 " " $2 " " $3]; if ($1 == "ns") ns[$3] += d; if ($1 == "cy") cy[$3] += d; if ($1 == "tc") tc[$3] += d }
        END {
            for (c in ns) printf "%s %.1f\n", c, ns[c] / (t * 1e9) * 100
            for (c in cy) if (tc[c] > 0) printf "%s %.1f\n", c, cy[c] / tc[c] * 100
        }' <(echo "$a") <(echo "$b")
}

# The pid of the patched ffmpeg a session is running (the shim routes to it), from /proc: no procps needed.
ffmpeg_pid() {
    local p
    for p in /proc/[0-9]*; do
        if tr '\0' ' ' <"$p/cmdline" 2>/dev/null | grep -q 'jellyfin-ffmpeg-oidn/ffmpeg'; then
            echo "${p#/proc/}"
            return 0
        fi
    done
    return 0
}

# run_fps <clip> <chain> [output] [runs] [frames]: the median fps of <runs> (default RUNS) runs (frames over wall
# time, from -benchmark). On failure it prints 0 and returns ffmpeg's exit status (124 = timed out, see why); callers
# run it in $(...), so the status is its return value, not a variable.
run_fps() {
    local i log n v vals=() runs="${4:-$RUNS}" rc
    log=$(mktemp)
    for ((i = 0; i < runs; i++)); do
        cmd_run "$1" "$2" "${3:-}" "${5:-}"
        # collected in this shell, not piped into median from the loop: a return inside a pipe is lost
        ff_exec 2>"$log" >/dev/null || { rc=$?; echo 0; rm -f "$log"; return "$rc"; }
        # -stats separates its updates with \r, so the bench: line can share a line with the last one
        tr '\r' '\n' <"$log" >"$log.n"
        n=$(awk '/^frame=/ { gsub(/frame= */, ""); f = $1 } END { print f + 0 }' "$log.n")
        v=$(awk -v n="$n" -v r="$(awk -F'[= ]+' '/^bench:.*rtime/ { for (i = 1; i <= NF; i++) if ($i == "rtime") { v = $(i + 1); sub(/s/, "", v); print v } }' "$log.n" | tail -1)" \
            'BEGIN { if (r > 0) printf "%.2f\n", n / r; else print 0 }')
        vals+=("$v")
    done
    rm -f "$log" "$log.n"
    printf '%s\n' "${vals[@]}" | median
}

# xpsnr_y <candidate> <master>: the luma XPSNR of a candidate against its master. Prints nothing and returns
# non-zero (ffmpeg's status: 124 = timed out; 1 when no score parses) when it was not measured: a score that was
# not measured must never read as 0 dB.
xpsnr_y() {
    local log v rc
    log=$(mktemp)
    cmd_xpsnr "$1" "$2" "$OUT/xpsnr.txt"
    ff_exec 2>"$log" >/dev/null || { rc=$?; rm -f "$log"; return "$rc"; }
    # ffmpeg prints `XPSNR  y: 14.4167  u: ...`: two spaces after XPSNR, so match on whitespace, not one blank
    v=$(awk '/XPSNR +y:/ { for (i = 1; i <= NF; i++) if ($i == "y:") v = $(i + 1) } END { if (v != "") print v + 0 }' "$log")
    rm -f "$log"
    [[ -n "$v" ]] || return 1
    echo "$v"
}

src_fps() { "$FFPROBE" -v error -select_streams v:0 -show_entries stream=r_frame_rate -of csv=p=0 "$1" | awk -F/ '{ printf "%.3f\n", $1 / ($2 ? $2 : 1) }'; }

# master_dims <reference master> <dst-w> <dst-h>: "W H" of the reference master itself (ffprobe), the size the test
# output, the bicubic control and the master all compare at. Never the source clip's shape scaled: the masters are
# letterboxed and cut from the 2160p one, so 3840x1714 is the 2160p master but 1920x858 doubled is 3840x1716.
# ffprobe failing (no such master, no stream) gives the nominal size.
master_dims() {
    local d
    d=$("$FFPROBE" -v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 "$1" 2>/dev/null \
        | awk -F, 'NF >= 2 && $1 > 0 && $2 > 0 { printf "%d %d\n", $1, $2 }') || d=""
    echo "${d:-$2 $3}"
}

# ---- Jellyfin API ----------------------------------------------------------------------------------

# api <path> [curl args]: the token goes in on stdin (--config -), so it is never on a command line (ps, /proc).
# -f makes an HTTP error a non-zero exit: every caller handles it and records FAIL.
api() {
    local p="$1" tok="$JF_TOKEN"
    shift
    tok="${tok//\\/\\\\}"; tok="${tok//\"/\\\"}"
    printf 'header = "Authorization: MediaBrowser Token=\\"%s\\""\n' "$tok" | curl -fsS --config - "$@" "$JF_URL$p"
}

# api_get <path>: one call, called directly (not in $(...)) so it can set API_BODY and API_CODE (000 when curl
# could not connect). Returns 1 on an HTTP or transport error.
API_BODY=""
API_CODE=000
api_get() {
    local raw rc=0
    raw=$(api "$1" -w '\n%{http_code}' 2>/dev/null) || rc=$?
    API_CODE="${raw##*$'\n'}"
    API_BODY="${raw%$'\n'*}"
    [[ "$API_CODE" =~ ^[0-9]{3}$ ]] || API_CODE=000
    return "$rc"
}

# An authed probe: /System/Info/Public answers anyone, so it passes with a token the server rejects.
# API_WHY says why it failed: rejected (401/403, the token is wrong) or unreachable.
API_WHY=""
have_api() {
    API_WHY=""
    [[ -n "$JF_TOKEN" ]] || return 1
    api_get /System/Info && return 0
    case "$API_CODE" in 401 | 403) API_WHY=rejected ;; *) API_WHY=unreachable ;; esac
    return 1
}
# no_api <row> <skip text>: after have_api failed. A rejected token is a FAIL, never a quiet SKIP.
no_api() {
    if [[ "$API_WHY" == rejected ]]; then
        record "$1" FAIL "token rejected (HTTP $API_CODE) by $JF_URL/System/Info: BENCH_JELLYFIN_TOKEN is not valid for this server"
    else
        record "$1" SKIP "$2"
    fi
}

# The bytes the shim log gained since this run began: a line from an earlier session says nothing about this one.
shim_log_new() { if [[ -r "$SHIM_LOG" ]]; then tail -c +$((SHIM_LOG_START + 1)) "$SHIM_LOG"; fi; }

# item_id <clip file name>: sets ITEM_ID (empty when the library lacks the clip). Returns 1 with ITEM_ERR on an
# HTTP or transport error, so a 401 or 500 is not reported as a clip missing from the library. Call it directly.
ITEM_ID=""
ITEM_ERR=""
item_id() {
    ITEM_ID=""; ITEM_ERR=""
    api_get "/Items?Recursive=true&Fields=Path&SearchTerm=${1%.*}" || { ITEM_ERR="item lookup HTTP $API_CODE"; return 1; }
    ITEM_ID=$(jq -r --arg n "$1" 'first(.Items[] | select(.Path | endswith("/" + $n)) | .Id) // empty' <<<"$API_BODY") \
        || { ITEM_ERR="item lookup: unreadable answer (HTTP $API_CODE)"; return 1; }
}

# api_stream <item id> <height> <extra query>: 0 and STREAM_SPEED (media seconds per wall second over the
# first 5 segments), STREAM_SID (the PlaySessionId) and STREAM_SEG on success; 1 and STREAM_ERR when the
# item is unknown, a request fails or no segment comes back. Bitrates map to the plugin's bitrate_targets.
api_stream() {
    local id="$1" h="$2" extra="${3:-}" br m3u8 seg t0 t1 n=0 dir variant
    case "$h" in 2160) br=20000000 ;; 1440) br=8000000 ;; *) br=4000000 ;; esac
    STREAM_SID="bench-$(date +%s)-$h"; STREAM_SEG=""; STREAM_SPEED=0; STREAM_ERR=""
    [[ -n "$id" ]] || { STREAM_ERR="the clip is not in the Jellyfin library"; return 1; }
    dir="$OUT/stream-$h"; mkdir -p "$dir"
    m3u8="/Videos/$id/master.m3u8?MediaSourceId=$id&PlaySessionId=$STREAM_SID&VideoCodec=hevc,h264&MaxHeight=$h&VideoBitrate=$br&TranscodingMaxAudioChannels=2&allowVideoStreamCopy=false&allowAudioStreamCopy=false${extra:+&$extra}"
    api "$m3u8" >"$dir/master.m3u8" || { STREAM_ERR="GET master.m3u8 failed"; return 1; }
    variant=$(awk '!/^#/ && NF { print; exit }' "$dir/master.m3u8")
    [[ -n "$variant" ]] || { STREAM_ERR="master.m3u8 lists no variant"; return 1; }
    api "/Videos/$id/$variant" >"$dir/main.m3u8" || { STREAM_ERR="GET $variant failed"; return 1; }
    t0=$(date +%s.%N)
    while read -r seg; do
        [[ -n "$seg" ]] || continue
        api "/Videos/$id/${seg}" >"$dir/seg$n.bin" || break
        n=$((n + 1)); [[ $n -lt 5 ]] || break
    done < <(awk '!/^#/ && NF' "$dir/main.m3u8")
    t1=$(date +%s.%N)
    [[ $n -gt 0 ]] || { STREAM_ERR="no segment came back"; return 1; }
    # a clock without sub-second resolution reads 0 elapsed for a fast stream: count it as 1 s, a lower bound
    STREAM_SPEED=$(awk -v n="$n" -v a="$t0" -v b="$t1" 'BEGIN { d = b - a; if (d < 0.001) d = 1; printf "%.2f\n", n * 6 / d }')
    STREAM_SEG="$dir/seg0.bin"
}

# ---- the rows --------------------------------------------------------------------------------------

row_1() {
    have_api || { no_api 1 "no Jellyfin API: set BENCH_JELLYFIN_URL and BENCH_JELLYFIN_TOKEN"; return; }
    local st probe bad="" why="" offered
    st=$(api /JellyfinPlus/Status) || { record 1 FAIL "GET /JellyfinPlus/Status failed"; return; }
    probe=$(api /JellyfinPlus/Session/bench-probe) || { record 1 FAIL "GET /JellyfinPlus/Session failed"; return; }
    [[ "$(jq -r '.Gpu.Vendor' <<<"$st")" == intel ]] || bad+=" vendor=$(jq -r '.Gpu.Vendor' <<<"$st")"
    [[ "$(jq -r '.Gpu.RenderNode' <<<"$st")" == "$NODE" ]] || bad+=" node=$(jq -r '.Gpu.RenderNode' <<<"$st")"
    offered=$(jq -r '[.Levels.Denoise[]?, .Levels.Neural[]?, .Levels.Game[]?] | join(" ")' <<<"$probe")
    local lvl
    for lvl in optix optix-temporal dlpp-1 dlpp-2 dlpp-3 dlpp-4 vsr-rtcuda dlss dlaa; do
        [[ " $offered " != *" $lvl "* ]] || bad+=" offered:$lvl"
    done
    [[ " $offered " == *" fsr2 "* ]] || bad+=" fsr2-not-offered"
    [[ -f "$PREFIX/fsr2-nocuda" ]] || bad+=" marker-missing"
    if [[ -r "$SHIM_LOG" ]]; then
        local hits
        hits=$(shim_log_new | grep -ciE 'nvenc|cuda' || true)
        [[ "${hits:-0}" -eq 0 ]] || bad+=" nvenc/cuda-in-log:$hits"
    else
        why=" (no $SHIM_LOG yet: the command-line check needs a session first)"
    fi
    "$PATCHED_FFMPEG" -hide_banner -nostdin -init_hw_device "drm=dr:$NODE" -init_hw_device vulkan=vk@dr \
        -f lavfi -i nullsrc=s=64x64 -frames:v 1 -f null - >/dev/null 2>&1 || bad+=" vulkan-vk@dr-failed"
    if [[ -z "$bad" ]]; then record 1 PASS "vendor intel, $NODE, fsr2 offered, no NVIDIA-only level offered$why"; else record 1 FAIL "$bad"; fi
}

xpsnr_delta() { # candidate bicubic master -> candidate minus bicubic, in dB; non-zero (xpsnr_y's status) when either score is missing
    local c b
    # explicit: inside a caller's `d=$(...) || ...` errexit is off, so a failed score would not stop this
    c=$(xpsnr_y "$1" "$3") || return $?
    b=$(xpsnr_y "$2" "$3") || return $?
    awk -v c="$c" -v b="$b" 'BEGIN { printf "%.2f\n", c - b }'
}

shader_row() { # row src-height dst-w dst-h fps-min xpsnr-min [e2e]; dst-w/dst-h are the 16:9 nominal, the dst-h master's own shape decides the real size
    local row="$1" sh="$2" w="$3" h="$4" fmin="$5" xmin="$6" e2e="${7:-0}" fps d status=PASS detail="" src ow oh rc=0 xrc=0
    mkdir -p "$OUT"
    read -r ow oh <<<"$(master_dims "$(clip "$h")" "$w" "$h")"
    fps=$(run_fps "$(clip "$sh")" "$(chain_shader "$ow" "$oh")") || { rc=$?; record "$row" FAIL "$(why "$rc" "ffmpeg failed")"; return; }
    cmd_run "$(clip "$sh")" "$(chain_shader "$ow" "$oh")" "$OUT/r$row-shader.mkv"; ff_exec >/dev/null 2>&1 || rc=$?
    timed_out "$rc" && { record "$row" FAIL "$(why "$rc" "")"; return; }
    cmd_run "$(clip "$sh")" "$(chain_bicubic "$ow" "$oh")" "$OUT/r$row-bicubic.mkv"; ff_exec >/dev/null 2>&1 || rc=$?
    timed_out "$rc" && { record "$row" FAIL "$(why "$rc" "")"; return; }
    detail="fps=$fps"
    d=$(xpsnr_delta "$OUT/r$row-shader.mkv" "$OUT/r$row-bicubic.mkv" "$(clip "$h")") || xrc=$?
    if [[ "$xrc" == 0 ]]; then
        detail+=" xpsnr_delta=+${d}dB"
        ge "$d" "$xmin" || status=FAIL
    else
        detail+=" xpsnr failed ($(why "$xrc" "ffmpeg xpsnr of the ${ow}x${oh} outputs against $(clip "$h")"))"
        status=FAIL
    fi
    if [[ "$fmin" != 0 ]]; then ge "$fps" "$fmin" || status=FAIL; fi
    if [[ "$e2e" == 1 ]]; then
        if have_api; then
            src=$(src_fps "$(clip "$sh")" 2>/dev/null || true)
            if ! item_id "${TITLE}_${sh}p.mkv"; then
                detail+=" e2e=FAIL($ITEM_ERR)"
                status=FAIL
            elif api_stream "$ITEM_ID" "$h" "sr=fsrcnnx"; then
                detail+=" e2e=${STREAM_SPEED}x_realtime src_fps=$src"
                ge "$STREAM_SPEED" "$E2E_X" || status=FAIL
            else
                detail+=" e2e=FAIL($STREAM_ERR)"
                status=FAIL
            fi
        elif [[ "$API_WHY" == rejected ]]; then
            detail+=" e2e=FAIL(token rejected (HTTP $API_CODE) by $JF_URL/System/Info)"
            status=FAIL
        else
            detail+=" e2e=SKIP(no API)"
            [[ "$status" == PASS ]] && status=SKIP
        fi
    fi
    record "$row" "$status" "$detail"
}

row_2() { shader_row 2 360 1920 1080 "$FPS_R2" "$XPSNR_R2" 0; }
row_3() { shader_row 3 720 1920 1080 0 "$XPSNR_R3" 1; }
row_4() { shader_row 4 1080 3840 2160 "$FPS_R4" "$XPSNR_R4" 1; }

row_5() {
    [[ -n "$ORT_MODEL" ]] || { record 5 SKIP "no BENCH_ORT_MODEL"; return; }
    local f360 f720 rec dev applied status=MANUAL note
    local rc
    f360=$(run_fps "$(clip 360)" "$(chain_ort 1920 1080)" "" "$ORT_RUNS" "$ORT_FRAMES") || { rc=$?; record 5 FAIL "$(why "$rc" "the ort chain failed at 360p (fps_360p=$f360)")"; return; }
    f720=$(run_fps "$(clip 720)" "$(chain_ort 1920 1080)" "" "$ORT_RUNS" "$ORT_FRAMES") || { rc=$?; record 5 FAIL "$(why "$rc" "the ort chain failed at 720p (fps_720p=$f720)")"; return; }
    note="fps_360p=$f360 fps_720p=$f720 over the first $ORT_FRAMES frames, one run each, model compile included (real time = source fps; choose NeuralMaxSourceHeight from these)"
    if ! have_api; then
        if [[ "$API_WHY" == rejected ]]; then
            record 5 FAIL "$note token rejected (HTTP $API_CODE) by $JF_URL/System/Info"
        else
            record 5 MANUAL "$note device=SKIP(no API: the session record is what proves openvino)"
        fi
        return
    fi
    # the device comes from a neural stream of this run: the record is per PlaySessionId
    item_id "${TITLE}_360p.mkv" || { record 5 FAIL "$note $ITEM_ERR"; return; }
    if ! api_stream "$ITEM_ID" 1080 "neural=$NEURAL_LEVEL"; then
        record 5 FAIL "$note neural stream: $STREAM_ERR"
        return
    fi
    rec=$(api "/JellyfinPlus/Session/$STREAM_SID") || { record 5 FAIL "$note GET /JellyfinPlus/Session/$STREAM_SID failed"; return; }
    dev=$(jq -r '.NeuralDevice // "none"' <<<"$rec")
    applied=$(jq -r '.NeuralApplied // false' <<<"$rec")
    [[ "$dev" == openvino ]] || status=FAIL
    record 5 "$status" "$note device=$dev neural_applied=$applied"
}

# The plugin's configuration is changed for row 6 and put back as it was found, on any exit.
CFG_ID=""
CFG_ORIG=""
cfg_post() { # a file holding the JSON body
    api "/Plugins/$CFG_ID/Configuration" -X POST -H "Content-Type: application/json" --data-binary "@$1" >/dev/null
}
cfg_restore() {
    [[ -n "$CFG_ORIG" && -f "$CFG_ORIG" ]] || return 0
    cfg_post "$CFG_ORIG" || echo "bench: could not restore the plugin configuration: POST it back from $CFG_ORIG" >&2
    CFG_ORIG=""
}

row_6() {
    have_api || { no_api 6 "no Jellyfin API"; return; }
    local cfg rec routing applied seen new
    CFG_ID=$(api /Plugins | jq -r 'first(.[] | select(.Name == "Jellyfin Plus") | .Id) // empty' || true)
    [[ -n "$CFG_ID" ]] || { record 6 FAIL "the Jellyfin Plus plugin is not listed by GET /Plugins"; return; }
    cfg=$(api "/Plugins/$CFG_ID/Configuration") || { record 6 FAIL "GET the plugin configuration failed"; return; }
    mkdir -p "$OUT"
    if [[ "$(jq -r '.NeuralMaxSourceHeight // 0' <<<"$cfg")" != 480 || "$(jq -r '.NeuralAllowed // false' <<<"$cfg")" != true ]]; then
        CFG_ORIG="$OUT/plugin-config.orig.json"
        printf '%s' "$cfg" >"$CFG_ORIG"
        new="$OUT/plugin-config.row6.json"
        jq -c '.NeuralMaxSourceHeight = 480 | .NeuralAllowed = true' <<<"$cfg" >"$new"
        cfg_post "$new" || { cfg_restore; record 6 FAIL "could not set NeuralMaxSourceHeight=480 on the plugin configuration"; return; }
    fi
    seen=$(api /JellyfinPlus/Session/bench-probe | jq -r '.Levels.NeuralMaxSourceHeight // 0' || true)
    if [[ "$seen" != 480 ]]; then
        cfg_restore
        record 6 FAIL "the probe shows NeuralMaxSourceHeight=${seen:-none}, not 480: the plugin does not apply the setting, so row 6 proves nothing"
        return
    fi
    item_id "${TITLE}_720p.mkv" || { cfg_restore; record 6 FAIL "$ITEM_ERR"; return; }
    if ! api_stream "$ITEM_ID" 1080 "neural=$NEURAL_LEVEL"; then
        cfg_restore
        record 6 FAIL "neural stream: $STREAM_ERR"
        return
    fi
    rec=$(api "/JellyfinPlus/Session/$STREAM_SID") || { cfg_restore; record 6 FAIL "GET /JellyfinPlus/Session/$STREAM_SID failed"; return; }
    cfg_restore
    routing=$(jq -r '.NeuralRouting // empty' <<<"$rec")
    applied=$(jq -r '.NeuralApplied // false' <<<"$rec")
    # the record must say the neural level was NOT run and why; a "was chosen" record is the opposite
    if [[ "$routing" == *"NOT run"* && "$applied" != true ]]; then
        record 6 PASS "NeuralRouting=$routing"
    else
        record 6 FAIL "a 720p source with NeuralMaxSourceHeight=480 must go to the shader path with a record saying so: NeuralRouting=${routing:-none} neural_applied=$applied"
    fi
}

row_7() {
    [[ -f "$CLIPS/still_720p.png" ]] || { record 7 SKIP "no $CLIPS/still_720p.png"; return; }
    mkdir -p "$OUT"
    local skip kind status=PASS detail f rc
    skip=$(still_skip 1.5)
    for kind in fsr2 control; do
        if [[ "$kind" == fsr2 ]]; then cmd_still "$CLIPS/still_720p.png" "$(chain_fsr2 1920 1080)" "$OUT/still-$kind.txt"
        else cmd_still "$CLIPS/still_720p.png" "$(chain_shader 1920 1080)" "$OUT/still-$kind.txt"; fi
        ff_exec >/dev/null 2>&1 || { rc=$?; record 7 FAIL "$(why "$rc" "$kind chain failed")"; return; }
    done
    # Row 7 scales 720p to 1080p, i.e. 1.5x: skip = ceil(8 * 2.25) = 18 frames.
    f=$(awk -v skip="$skip" '/YAVG=/ { split($0, a, "="); n++; if (n > skip) { s += a[2]; c++; if (a[2] > 16) bad++ } } END { printf "%.3f %.1f\n", c ? s / c : -1, c ? 100 * (c - bad) / c : 0 }' "$OUT/still-fsr2.txt")
    local mean pct ctl
    mean=${f% *}; pct=${f#* }
    ctl=$(awk -v skip="$skip" '/YAVG=/ { split($0, a, "="); n++; if (n > skip) { s += a[2]; c++ } } END { printf "%.3f\n", c ? s / c : -1 }' "$OUT/still-control.txt")
    le "$mean" "$STILL_MEAN_MAX" && ge "$mean" 0 || status=FAIL
    ge "$pct" "$STILL_PEAK_PCT" || status=FAIL
    [[ "$ctl" == 0.000 ]] || status=FAIL
    detail="mean=$mean peak_ok_pct=$pct control_mean=$ctl skipped=$skip"
    record 7 "$status" "$detail"
}

row_8() {
    [[ -f "$CLIPS/pan_720p.mkv" && -f "$CLIPS/pan_1080p.mkv" ]] || { record 8 SKIP "no pan_720p.mkv / pan_1080p.mkv"; return; }
    mkdir -p "$OUT"
    local d ow oh rc=0
    read -r ow oh <<<"$(master_dims "$CLIPS/pan_1080p.mkv" 1920 1080)"
    cmd_run "$CLIPS/pan_720p.mkv" "$(chain_fsr2 "$ow" "$oh")" "$OUT/pan-fsr2.mkv"; ff_exec >/dev/null 2>&1 || { rc=$?; record 8 FAIL "$(why "$rc" "fsr2 chain failed")"; return; }
    cmd_run "$CLIPS/pan_720p.mkv" "$(chain_bicubic "$ow" "$oh")" "$OUT/pan-bicubic.mkv"; ff_exec >/dev/null 2>&1 || rc=$?
    timed_out "$rc" && { record 8 FAIL "$(why "$rc" "")"; return; }
    d=$(xpsnr_delta "$OUT/pan-fsr2.mkv" "$OUT/pan-bicubic.mkv" "$CLIPS/pan_1080p.mkv") || { rc=$?; record 8 FAIL "xpsnr failed ($(why "$rc" "the ${ow}x${oh} outputs against $CLIPS/pan_1080p.mkv"))"; return; }
    if ge "$d" 0; then record 8 PASS "xpsnr_delta=+${d}dB"; else record 8 FAIL "xpsnr_delta=${d}dB: ship the image without fsr2-nocuda"; fi
}

row_9() {
    mkdir -p "$OUT"
    local on_log off_log on_cpu off_cpu gain x status=PASS rc
    on_log="$OUT/hwdec-on.log"; off_log="$OUT/hwdec-off.log"
    cmd_run_hw "$(clip 1080)" "$(chain_hwdec 1920 1080)" "$OUT/hwdec-on.mkv"
    ff_exec >/dev/null 2>"$on_log" || { rc=$?; record 9 FAIL "$(why "$rc" "hardware decode chain failed: $(tail -1 "$on_log")")"; return; }
    ! grep -q 'Conversion failed' "$on_log" || status=FAIL
    cmd_run "$(clip 1080)" "$(chain_shader 1920 1080)" "$OUT/hwdec-off.mkv"
    ff_exec >/dev/null 2>"$off_log" || { rc=$?; record 9 FAIL "$(why "$rc" "software chain failed")"; return; }
    on_cpu=$(tr '\r' '\n' <"$on_log" | awk '/^bench:/ { for (i = 1; i <= NF; i++) if ($i ~ /^utime=/) { v = $i; sub(/utime=/, "", v); sub(/s/, "", v); print v } }' | tail -1)
    off_cpu=$(tr '\r' '\n' <"$off_log" | awk '/^bench:/ { for (i = 1; i <= NF; i++) if ($i ~ /^utime=/) { v = $i; sub(/utime=/, "", v); sub(/s/, "", v); print v } }' | tail -1)
    gain=$(awk -v a="${on_cpu:-0}" -v b="${off_cpu:-0}" 'BEGIN { printf "%.1f\n", (b > 0 ? (b - a) / b * 100 : 0) }')
    x=$(xpsnr_y "$OUT/hwdec-on.mkv" "$OUT/hwdec-off.mkv") || { rc=$?; record 9 FAIL "xpsnr failed ($(why "$rc" "hwdec-on against hwdec-off"))"; return; }
    ge "$gain" "$HWDEC_CPU_GAIN" || status=FAIL
    ge "$x" "$HWDEC_XPSNR_MIN" || status=FAIL
    record 9 "$status" "cpu_gain=${gain}% xpsnr_on_vs_off=${x}dB (the video-engine-busy and command-identical-when-off checks are made on the served session, row 12)"
}

row_10() {
    have_api || { no_api 10 "no Jellyfin API"; return; }
    local id codec load fd status=PASS stream_err=""
    item_id "${TITLE}_1080p.mkv" || { record 10 FAIL "$ITEM_ERR"; return; }
    id="$ITEM_ID"
    mkdir -p "$OUT"
    rm -f "$OUT/r10-load" "$OUT/r10-fd"
    # Sampled while the stream's segments are being produced; both read the same window.
    ( sleep 4; api /JellyfinPlus/Gpu/Load | jq -r '.gpuBusyPercent // empty' >"$OUT/r10-load" ) &
    ( sleep 4; pid=$(ffmpeg_pid); [[ -z "$pid" ]] || fdinfo_busy "$pid" 3 | sort -k2 -nr | awk 'NR == 1 { print $2 }' >"$OUT/r10-fd" ) &
    api_stream "$id" 1080 "sr=fsrcnnx" || stream_err="$STREAM_ERR"
    wait
    [[ -z "$stream_err" ]] || { record 10 FAIL "stream: $stream_err"; return; }
    load=$(cat "$OUT/r10-load" 2>/dev/null || true)
    fd=$(cat "$OUT/r10-fd" 2>/dev/null || true)
    codec=$("$FFPROBE" -v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 "${STREAM_SEG:-/dev/null}" 2>/dev/null || true)
    case "$codec" in h264 | hevc) ;; *) status=FAIL ;; esac
    if [[ -n "$load" && -n "$fd" ]]; then
        awk -v a="$load" -v b="$fd" -v t="$LOAD_TOLERANCE" 'BEGIN { d = a - b; if (d < 0) d = -d; exit !(d <= t) }' || status=FAIL
    else
        [[ "$status" == PASS ]] && status=MANUAL
    fi
    record 10 "$status" "segment_codec=${codec:-none} gpuBusyPercent=${load:-none} fdinfo_busy=${fd:-none}"
}

row_11() {
    [[ "${BENCH_PLEX_TRANSCODING:-}" == 1 ]] || { record 11 SKIP "set BENCH_PLEX_TRANSCODING=1 while one Plex hardware transcode runs"; return; }
    local fps ow oh rc
    read -r ow oh <<<"$(master_dims "$(clip 2160)" 3840 2160)"
    fps=$(run_fps "$(clip 1080)" "$(chain_shader "$ow" "$oh")") || { rc=$?; record 11 FAIL "$(why "$rc" "ffmpeg failed")"; return; }
    if ge "$fps" "$FPS_R11"; then record 11 PASS "fps=$fps under contention"; else record 11 FAIL "fps=$fps under contention"; fi
}

row_12() {
    have_api || { no_api 12 "no Jellyfin API"; return; }
    local h id bad="" seen="" got codec new
    item_id "${TITLE}_2160p.mkv" || { record 12 FAIL "$ITEM_ERR"; return; }
    id="$ITEM_ID"
    for h in 480 720 1080 2160; do
        if ! api_stream "$id" "$h" "sr=fsrcnnx"; then
            bad+=" ${h}p:$STREAM_ERR"
            continue
        fi
        got=$("$FFPROBE" -v error -select_streams v:0 -show_entries stream=height,codec_name -of csv=p=0 "$STREAM_SEG" 2>/dev/null || true)
        codec=${got%%,*}
        seen+=" ${h}p:${got:-none}"
        [[ "$codec" == h264 || "$codec" == hevc ]] || bad+=" ${h}p"
    done
    if [[ -r "$SHIM_LOG" ]]; then
        new=$(shim_log_new)
        grep -q 'libplacebo' <<<"$new" || bad+=" no-libplacebo-in-log"
    fi
    if [[ -z "$bad" ]]; then record 12 PASS "$seen"; else record 12 FAIL "bad:$bad seen:$seen"; fi
}

row_13() {
    record 13 MANUAL "owner: browser over a port-forward, hard reload; window.__jellyfinPlus.installed===true; 5 min at two levels plus one 2160p output, two seeks, no stall over 2s"
}

# ---- main ------------------------------------------------------------------------------------------

usage() { sed -n '2,13p;21,43p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --dry-run | --plan) DRY=1 ;;
        --no-gates) GATES=0 ;;
        --list) for r in "${ALL_ROWS[@]}"; do printf 'row %s: %s\n' "$r" "$(row_title "$r")"; done; exit 0 ;;
        --rows) shift; [[ $# -gt 0 ]] || die "--rows needs a list"; IFS=, read -r -a ROWS <<<"$1" ;;
        --rows=*) IFS=, read -r -a ROWS <<<"${1#--rows=}" ;;
        -h | --help) usage; exit 0 ;;
        *) die "unknown argument: $1" ;;
    esac
    shift
done
[[ ${#ROWS[@]} -gt 0 ]] || ROWS=("${ALL_ROWS[@]}")
for r in "${ROWS[@]}"; do row_title "$r" >/dev/null || die "no such row: $r (rows are 1 to 13)"; done
[[ "$RUNS" =~ ^[1-9][0-9]*$ ]] || die "BENCH_RUNS must be a positive integer, got: $RUNS"
[[ "$RUN_TIMEOUT" =~ ^[1-9][0-9]*$ ]] || die "BENCH_RUN_TIMEOUT must be a positive integer (seconds), got: $RUN_TIMEOUT"
case "$NEURAL_LEVEL" in
    realesr-anime-x2 | realesr-anime-x4 | realesr-general-x4) ;;
    *) die "BENCH_NEURAL_LEVEL must be realesr-anime-x2, realesr-anime-x4 or realesr-general-x4, got: $NEURAL_LEVEL" ;;
esac
parse_block_windows

if [[ $DRY == 1 ]]; then
    print_plan
    exit 0
fi

if [[ $GATES == 1 ]]; then
    check_gates
elif [[ "${BENCH_CONFIRM_NO_GATES:-}" != 1 ]]; then
    die "--no-gates skips the quiet-box checks (no vzdump, no Plex or production transcode, no memory pressure): set BENCH_CONFIRM_NO_GATES=1 to say you have made them"
fi
FFPROBE="$(dirname "$STOCK_FFMPEG")/ffprobe"
need "$PATCHED_FFMPEG"; need "$FFPROBE"; need curl; need jq; need awk; need timeout
[[ -d "$CLIPS" ]] || die "no clips directory: $CLIPS"
mkdir -p "$OUT"
# The list is captured, then searched: `-filters | grep -q` lets grep quit at its match, the writer takes
# SIGPIPE, and pipefail reports a filter that is there as missing.
FILTERS=$("$PATCHED_FFMPEG" -hide_banner -filters 2>/dev/null) || die "$PATCHED_FFMPEG -filters failed"
for filter in $(ffmpeg_required_filters intel) fsr2 libplacebo xpsnr; do
    grep -qE "^ [A-Z.]+ +$filter " <<<"$FILTERS" || die "$PATCHED_FFMPEG lists no $filter filter: this is not the Intel build"
done
if [[ -r "$SHIM_LOG" ]]; then SHIM_LOG_START=$(wc -c <"$SHIM_LOG" | tr -d ' '); fi
trap cfg_restore EXIT
trap 'exit 130' INT TERM

for r in "${ROWS[@]}"; do
    say "row $r: $(row_title "$r")"
    "row_$r"
done

echo "summary: ${RESULTS[*]}"
exit "$FAILED"
