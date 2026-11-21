# ffmpeg-vendor.sh - the per-vendor facts build-ffmpeg.sh and proxmox-build.sh must agree on.
# Sourced, never run. One list, because two copies of "which filters must survive a rebuild" drift
# apart (AGENTS.md invariant 10).

ffmpeg_vendor_valid () {
    case "${1:-}" in nvidia|intel) return 0 ;; *) return 1 ;; esac
}

# The filters a binary for this vendor must list in -filters, space separated, or the deploy is
# refused. The Maxine vsr filter (retired, WITH_MAXINE_VSR) is added by the caller, not here.
ffmpeg_required_filters () {
    case "${1:-}" in
        nvidia) echo "oidn optix ort fsr2 dlss" ;;
        intel)  echo "oidn ort" ;;
        *)      return 1 ;;
    esac
}
