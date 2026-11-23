# ffmpeg-vendor.sh - the per-vendor facts build-ffmpeg.sh and proxmox-build.sh must agree on.
# Sourced, never run, by bash and by POSIX sh (dash in the image): no here-strings, [[ ]] or arrays.
# One list, because two copies of "which filters must survive a rebuild" drift apart (AGENTS.md
# invariant 10).

ffmpeg_vendor_valid () {
    case "${1:-}" in nvidia|intel) return 0 ;; *) return 1 ;; esac
}

# Both build scripts install over the live binary, so GPU_VENDOR=intel (a mistyped env var) over a binary
# that carries optix would replace the NVIDIA build. Returns 1 with a message, unless ALLOW_VENDOR_SWITCH=1.
# Usage: ffmpeg_vendor_switch_guard <vendor> <path of the binary about to be replaced>
# A missing or unrunnable binary is not a switch: there is nothing to lose.
ffmpeg_vendor_switch_guard () {
    [ "${1:-}" = "intel" ] || return 0
    if [ "${ALLOW_VENDOR_SWITCH:-0}" = "1" ]; then return 0; fi
    [ -x "${2:-}" ] || return 0
    local filters
    filters=$("$2" -hide_banner -filters 2>/dev/null) || return 0
    printf '%s\n' "$filters" | grep -qE '^ *[TSC.]* *optix ' || return 0
    echo "GPU_VENDOR=intel would replace $2, which carries the NVIDIA optix filter. Check GPU_VENDOR; set ALLOW_VENDOR_SWITCH=1 to switch this box to the Intel build on purpose." >&2
    return 1
}

# The filters a binary for this vendor must list in -filters, space separated, or the deploy is
# refused. The Maxine vsr filter (retired, WITH_MAXINE_VSR) and, on intel, fsr2 (opt-in, WITH_FSR2=1)
# are added by the caller, not here.
ffmpeg_required_filters () {
    case "${1:-}" in
        nvidia) echo "oidn optix ort fsr2 dlss" ;;
        intel)  echo "oidn ort" ;;
        *)      return 1 ;;
    esac
}
