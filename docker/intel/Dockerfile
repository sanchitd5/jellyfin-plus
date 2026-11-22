# syntax=docker/dockerfile:1.7
#
# Jellyfin Plus for Intel Arc: the production Jellyfin image plus the Intel build of the plugin.
#
#   docker build -f docker/intel/Dockerfile -t jellyfin-plus-arc .      (from the repository root)
#
# docs/architecture/GPU_VENDORS.md, 5.8 describes the layout, what is pinned and what is not. The
# binary this builds links GPL components (libx264 and others): it is for the person who built it and
# is NEVER pushed to a public registry or attached to a release. This file publishes nothing.
#
# The build stage starts FROM the image production runs, so ldd resolves against the same Debian the
# binary will meet. Everything the LXC install (scripts/proxmox-build.sh) writes to a fixed path is
# placed at the same path here; the stock /usr/lib/jellyfin-ffmpeg is never touched.

# The exact image production runs: a tag for the reader, the digest for the build.
ARG JELLYFIN_IMAGE=docker.io/jellyfin/jellyfin:12.4.20261026-211555@sha256:fea863d0cd9ec445985d9c75fc6ed64e38a1d2766cf0c250a33d553f4fc1b771

# ---- the packages the base already has, so the build can name only the ones it adds ----------------
FROM ${JELLYFIN_IMAGE} AS base-pkgs
RUN dpkg-query -W -f='${Package}\n' | sort -u > /base-pkgs.txt

# ---- build: ffmpeg, FSR2, the plugin and patcher, the shaders, the web client -----------------------
FROM ${JELLYFIN_IMAGE} AS build

# FidelityFX-FSR2 at the commit docs/filters/FSR2.md was verified against. A git commit id is a content hash.
ARG FSR2_COMMIT=1680d1edd5c034f88ebbbb793d8b88f8842cf804
# The .NET 10 SDK, from Microsoft's release metadata, checked against its published sha512.
ARG DOTNET_SDK_URL=https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.404/dotnet-sdk-10.0.404-linux-x64.tar.gz
ARG DOTNET_SDK_SHA512=577d0eb17fec33d029104c5c63a1ab0accc3e28776274abef2218dbac5af18d2109b5406d26b31d76f5c245215e3ba0926a94c20246c672c41847c9e1eb1dc50
ARG JELLYFIN_BIN=/jellyfin
ARG JELLYFIN_WEB=/jellyfin/jellyfin-web
ARG PLUGIN_VERSION=1.0.0.0

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    PATH=/opt/dotnet:$PATH

# What must exist before build-ffmpeg.sh's own preflight: the FSR2 library is built out of tree and the
# script refuses to start without it, and its Vulkan backend needs vulkan/vulkan.h. The rest is the
# script's own intel apt list (scripts/test_bench_intel.py keeps the two equal), so its apt step is a no-op.
# Not libshaderc-dev: the script builds shaderc from source, the distro one breaks nlmeans_vulkan.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl git python3 build-essential glslang-tools nodejs npm \
        pkg-config nasm yasm meson ninja-build cmake libx264-dev libxcb1-dev libvulkan-dev libva-dev libvpl-dev libdrm-dev \
 && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL "$DOTNET_SDK_URL" -o /tmp/dotnet.tar.gz \
 && echo "$DOTNET_SDK_SHA512  /tmp/dotnet.tar.gz" | sha512sum -c - \
 && mkdir -p /opt/dotnet \
 && tar -C /opt/dotnet -xzf /tmp/dotnet.tar.gz \
 && rm /tmp/dotnet.tar.gz

# FSR2's Vulkan backend as a static library, the recipe in docs/filters/FSR2.md (gen_perm.py stands in
# for upstream's Windows-only shader compiler).
COPY ffmpeg/gen_perm.py ffmpeg/fsr2compat.h /opt/fsr2-build/
RUN set -eux; \
    mkdir /opt/fsr2 && cd /opt/fsr2 && git init -q \
 && git fetch -q --depth 1 https://github.com/GPUOpen-Effects/FidelityFX-FSR2.git "$FSR2_COMMIT" \
 && git checkout -q --detach FETCH_HEAD \
 && test "$(git rev-parse HEAD)" = "$FSR2_COMMIT"; \
    API=/opt/fsr2/src/ffx-fsr2-api; \
    GLSLANG=glslangValidator python3 /opt/fsr2-build/gen_perm.py "$API/shaders" /opt/fsr2-perm; \
    mkdir -p /opt/fsr2-obj /opt/fsr2-lib && cd /opt/fsr2-obj; \
    for f in "$API"/*.cpp "$API"/vk/*.cpp "$API"/vk/shaders/*.cpp; do \
        g++ -O2 -fPIC -std=c++17 -DFFX_GCC -include /opt/fsr2-build/fsr2compat.h -I/opt/fsr2-perm -c "$f" -o "$(basename "$f" .cpp).o"; \
    done; \
    ar rcs /opt/fsr2-lib/libffx_fsr2_vk.a ./*.o

COPY . /src
WORKDIR /src

# The Intel build: oidn and ort (OpenVINO), plus fsr2 without CUDA (patch 0016, the fsr2-nocuda marker).
# Slow (shaderc, libplacebo and ffmpeg compile here) and needs about 12 GB of free disk.
RUN GPU_VENDOR=intel WITH_ORT=1 ORT_EP=openvino WITH_FSR2=1 \
    FSR2_SDK=/opt/fsr2/src/ffx-fsr2-api FSR2_LIB=/opt/fsr2-lib \
    PREFIX=/usr/lib/jellyfin-ffmpeg-oidn \
    bash scripts/build-ffmpeg.sh

# The apt packages the new binary needs at run time that the base does not carry, named from its own ldd
# so the final stage installs exactly those and nothing is guessed. A library built from source under
# /usr/local (shaderc: the distro one breaks nlmeans_vulkan) has no package, so it is copied under the
# soname the binary asks for and the final stage carries it.
COPY --from=base-pkgs /base-pkgs.txt /base-pkgs.txt
RUN set -eu; mkdir -p /out/local-libs; P=/usr/lib/jellyfin-ffmpeg-oidn; \
    ldd "$P/ffmpeg" | awk '/=>/ && $3 ~ /^\// { print $1, $3 }' | while read -r n l; do \
        r=$(readlink -f "$l"); \
        case "$r" in "$P"/*) continue ;; /usr/local/*) cp "$r" "/out/local-libs/$n"; echo "local: $n" >&2; continue ;; esac; \
        dpkg -S "$r" 2>/dev/null | head -1 | cut -d: -f1; \
    done | sort -u > /tmp/needed.txt; \
    comm -23 /tmp/needed.txt /base-pkgs.txt > /out/runtime-packages.txt; \
    cat /out/runtime-packages.txt

# The plugin and patcher against the Jellyfin assemblies of this exact image.
RUN bash docker/intel/stage-plugin.sh /src "$JELLYFIN_BIN" /out/plugin-dist /out/patcher

# The web client, built from web/src and injected into the web root now: the pod's root filesystem is
# read-only, so the runtime injector (scripts/jellyfin-plus-webinject) cannot run there.
RUN set -eux; \
    echo "web: npm ci"; \
    (cd web && npm ci --no-audit --no-fund); \
    echo "web: build"; \
    bash scripts/build-web-panel.sh; \
    echo "web: install bundle and theme"; \
    install -m 0644 web/jellyfin-plus.js /out/patcher/jellyfin-plus.js; \
    install -m 0644 web/theme/jellyfinplus.css /out/patcher/jellyfinplus-theme.css; \
    echo "web: inject into $JELLYFIN_WEB"; \
    JP_WEB="$JELLYFIN_WEB" JP_SRC=/out/patcher/jellyfin-plus.js JP_THEME_SRC=/out/patcher/jellyfinplus-theme.css \
        bash scripts/jellyfin-plus-webinject

# The shaders. install-shaders.sh pins and hashes the FSRCNNX, Anime4K and NIS files; the gist and
# raw/master ones (FSR, SSimSuperRes, KrigBilateral, ravu, CuNNy) are still mutable (a recorded gap).
# The test -f list is every SR file the plugin offers by default or in its menu, plus the sharpeners.
RUN bash scripts/install-shaders.sh /usr/share/jellyfin-shaders \
 && test -f /usr/share/jellyfin-shaders/FSRCNNX_x2_8-0-4-1.glsl \
 && test -f /usr/share/jellyfin-shaders/FSRCNNX_x2_16-0-4-1.glsl \
 && test -f /usr/share/jellyfin-shaders/Anime4K_Upscale_CNN_x2_S.glsl \
 && test -f /usr/share/jellyfin-shaders/Anime4K_Upscale_CNN_x2_M.glsl \
 && test -f /usr/share/jellyfin-shaders/NVScaler.glsl \
 && test -f /usr/share/jellyfin-shaders/RCAS-1.7.glsl

# ---- the image -----------------------------------------------------------------------------------
FROM ${JELLYFIN_IMAGE}
LABEL org.opencontainers.image.title="jellyfin-plus-arc" \
      org.opencontainers.image.description="Jellyfin 12.4 with Jellyfin Plus, GPU_VENDOR=intel build. Contains GPL components: do not redistribute." \
      org.opencontainers.image.licenses="GPL-3.0-or-later"

ENV DEBIAN_FRONTEND=noninteractive

# Run-time libraries the new binary links (named by the build stage), python3 for the shim (the base has
# none; full python3, not -minimal: the shim imports json, subprocess and re), and jq for bench-intel.sh.
COPY --from=build /out/runtime-packages.txt /tmp/runtime-packages.txt
RUN apt-get update \
 && xargs -a /tmp/runtime-packages.txt apt-get install -y --no-install-recommends python3 jq \
 && rm -rf /var/lib/apt/lists/* /tmp/runtime-packages.txt

# The drivers libvulkan and libva dlopen: the ldd-derived list above cannot see them, so they are named here.
# iHD is in non-free (the free build lacks Arc's HuC encode, which QSV needs); that component is enabled for
# this one RUN and the source file is removed before it ends.
RUN set -eu; \
    printf 'Types: deb\nURIs: http://deb.debian.org/debian\nSuites: trixie\nComponents: non-free\nSigned-By: /usr/share/keyrings/debian-archive-keyring.pgp\n' > /etc/apt/sources.list.d/intel-non-free.sources; \
    apt-get update; \
    apt-get install -y --no-install-recommends mesa-vulkan-drivers intel-media-va-driver-non-free libigdgmm12; \
    rm -f /etc/apt/sources.list.d/intel-non-free.sources; \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /usr/lib/jellyfin-ffmpeg-oidn /usr/lib/jellyfin-ffmpeg-oidn
# The base lists /usr/local/lib in /etc/ld.so.conf.d/libc.conf (checked on the pinned image); the cache
# is rebuilt here, before the acceptance check below resolves the binary's libraries.
COPY --from=build /out/local-libs/ /usr/local/lib/
RUN ldconfig
# Driver acceptance: an Intel ICD JSON exists and the ICD library and iHD resolve every dependency. vainfo
# and vulkaninfo need a GPU, which a build does not have, so the Arc bench is the real proof they work.
RUN set -eu; \
    icds=$(ls /usr/share/vulkan/icd.d/*intel*.json); \
    for j in $icds; do \
        l=$(jq -r .ICD.library_path "$j"); \
        case "$l" in /*) ;; *) l="/usr/lib/x86_64-linux-gnu/$l" ;; esac; \
        test -f "$l"; \
        if ldd "$l" | grep 'not found'; then echo "unresolved libraries in $l" >&2; exit 1; fi; \
    done; \
    ihd=/usr/lib/x86_64-linux-gnu/dri/iHD_drv_video.so; \
    test -f "$ihd"; \
    if ldd "$ihd" | grep 'not found'; then echo "unresolved libraries in $ihd" >&2; exit 1; fi
COPY --from=build /out/patcher /usr/lib/jellyfin-plus
COPY --from=build /out/plugin-dist /usr/lib/jellyfin-plus-dist
COPY --from=build /usr/share/jellyfin-shaders /usr/share/jellyfin-shaders
COPY --from=build /jellyfin/jellyfin-web /jellyfin/jellyfin-web
COPY --chmod=0755 shim/jellyfin-ffmpeg-upscale /usr/local/bin/jellyfin-ffmpeg-upscale
COPY --chmod=0755 scripts/bench-intel.sh scripts/ffmpeg-vendor.sh /usr/local/lib/jellyfin-plus/scripts/

# Jellyfin loads plugins only from $JELLYFIN_DATA_DIR/plugins, an empty volume on first start: the wrapper
# seeds the staged plugin there, then execs the base image's own ENTRYPOINT (/jellyfin/jellyfin) with the
# same arguments. Without it the image starts a stock Jellyfin.
COPY --chmod=0755 docker/intel/entrypoint.py /usr/local/bin/jellyfin-plus-entrypoint
ENTRYPOINT ["/usr/local/bin/jellyfin-plus-entrypoint"]

# The shim's config and log paths are fixed and the pod's root filesystem is read-only, so they are links
# into /var/cache/jellyfin, which the Deployment mounts as an emptyDir (as the shader cache already is).
# The shim wants ffprobe beside itself (INSTALL.md).
RUN ln -s /var/cache/jellyfin/jellyfin-upscale.json /etc/jellyfin-upscale.json \
 && ln -s /var/cache/jellyfin/jellyfin-upscale.log /var/log/jellyfin-upscale.log \
 && ln -s /usr/lib/jellyfin-ffmpeg/ffprobe /usr/local/bin/ffprobe \
 && ln -s /usr/local/lib/jellyfin-plus/scripts/bench-intel.sh /usr/local/bin/bench-intel

# Jellyfin starts the shim, which routes to the patched binary per session and strips any node it lacks.
# The Deployment may set this itself; it is here so the image works without it.
ENV JELLYFIN_FFMPEG=/usr/local/bin/jellyfin-ffmpeg-upscale

# The build acceptance the owner's decision names, run against the files as they will ship. A miss fails
# the build, so an image that exists has passed them.
RUN --mount=type=bind,from=base-pkgs,source=/usr/lib/jellyfin-ffmpeg/ffmpeg,target=/tmp/base-ffmpeg \
    set -eu; \
    F=/usr/lib/jellyfin-ffmpeg-oidn/ffmpeg; \
    . /usr/local/lib/jellyfin-plus/scripts/ffmpeg-vendor.sh; \
    if ldd "$F" | grep 'not found'; then echo "unresolved libraries in $F" >&2; exit 1; fi; \
    filters=$("$F" -hide_banner -filters); \
    for f in $(ffmpeg_required_filters intel) fsr2 libplacebo xpsnr; do \
        echo "$filters" | grep -qE "^ [A-Z.]+ +$f " || { echo "no $f filter in $F" >&2; exit 1; }; \
    done; \
    test -f /usr/lib/jellyfin-ffmpeg-oidn/fsr2-nocuda; \
    "$F" -hide_banner -hwaccels | grep -qx vaapi; \
    "$F" -hide_banner -encoders | grep -q h264_qsv; \
    cmp /tmp/base-ffmpeg /usr/lib/jellyfin-ffmpeg/ffmpeg; \
    v=$(/usr/local/bin/jellyfin-ffmpeg-upscale -version); \
    echo "$v" | grep -q '^ffmpeg version'; \
    test -f /jellyfin/jellyfin-web/jellyfin-plus.js; \
    grep -q 'jellyfin-plus.js' /jellyfin/jellyfin-web/index.html; \
    test -f /usr/lib/jellyfin-plus/Jellyfin.Plugin.Plus.Patcher.dll; \
    test -f /usr/lib/jellyfin-plus/0Harmony.dll; \
    test -f /usr/lib/jellyfin-plus-dist/JellyfinPlus_1.0.0.0/Jellyfin.Plugin.Plus.dll

# The plugin acceptance: a headless start through the entrypoint must seed the plugin dir and log
# "Loaded plugin: Jellyfin Plus". Last, so it runs against the finished image.
RUN --mount=type=bind,source=docker/intel/accept-plugin.sh,target=/tmp/accept-plugin.sh \
    bash /tmp/accept-plugin.sh /usr/local/bin/jellyfin-plus-entrypoint
