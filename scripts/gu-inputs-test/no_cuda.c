/*
 * Copyright (c) 2026
 *
 * This file is part of FFmpeg.
 *
 * FFmpeg is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.
 *
 * FFmpeg is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 */

/*
 * Does ffmpeg/gu_inputs.h, built with -DGU_NO_NVOF, run with no CUDA at all?
 *
 * The header is included whole (its functions are static) and gu_inputs_init() and
 * gu_inputs_frame() run on synthetic frames. No ffnvcodec, no Optical Flow SDK, no libcuda and no
 * ONNX Runtime library are involved. Expected: init succeeds, the flow and reactive mask are zero,
 * the depth is flat 0.5, and the phase-correlation jitter still measures a known shift. This is not
 * a run on an Intel GPU and not a run of fsr2. scripts/test-gu-inputs.sh builds and runs it.
 * Built without -DGU_NO_NVOF it is the second variant (the #else below): the default header against
 * stub CUDA. docs/architecture/GPU_VENDORS.md 5.5.
 */

#include <errno.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>

#include "libavutil/log.h"
#include "libavutil/version.h"
#include "gu_inputs.h"

#define W 64
#define H 64
#define SHIFT 3

static int failures;

static void check(int ok, const char *what)
{
    printf("%s  %s\n", ok ? "PASS" : "FAIL", what);
    if (!ok)
        failures++;
}

/* A repeatable texture with structure at every scale, evaluated at integer positions. */
static float texture(int x, int y)
{
    unsigned h = ((unsigned)x * 73856093u) ^ ((unsigned)y * 19349663u);
    h = (h ^ (h >> 13)) * 0x5bd1e995u;
    return 0.5f + 0.25f * sinf(0.21f * x + 0.13f * y) + 0.15f * sinf(0.07f * x - 0.31f * y)
           + 0.10f * ((h & 0xffff) / 65535.0f - 0.5f);
}

static void fill(float *planes[3], int shift)
{
    int x, y, p;
    for (p = 0; p < 3; p++)
        for (y = 0; y < H; y++)
            for (x = 0; x < W; x++)
                planes[p][y * W + x] = texture(x - shift, y) * (p == 0 ? 1.0f : p == 1 ? 0.9f : 0.8f);
}

static const AVClass test_class = {
    .class_name = "gu-test",
    .item_name  = av_default_item_name,
    .version    = LIBAVUTIL_VERSION_INT,
};

#ifdef GU_NO_NVOF
int main(void)
{
    static float plane[3][W * H];
    float *planes[3] = { plane[0], plane[1], plane[2] };
    AVFilterContext ctx = { 0 };
    GUInputs g = { 0 };
    AVFrame in = { 0 };
    int i, ret, nonzero = 0;
    float dx;

    ctx.av_class = &test_class;
    in.data[0] = (uint8_t *)plane[0];   /* G */
    in.data[1] = (uint8_t *)plane[1];   /* B */
    in.data[2] = (uint8_t *)plane[2];   /* R */
    in.linesize[0] = in.linesize[1] = in.linesize[2] = W * (int)sizeof(float);

    g.jitter_mode = GU_JITTER_MEASURED;
    g.depth_mode  = GU_DEPTH_FLAT;
    g.react_mode  = GU_REACT_FLOW;     /* asked for, and there is no flow to build it from */

    ret = gu_inputs_init(&ctx, &g, W, H);
    check(ret == 0, "gu_inputs_init succeeds without CUDA");

    fill(planes, 0);
    ret = gu_inputs_frame(&ctx, &g, &in);
    check(ret == 0, "first frame succeeds");
    fill(planes, SHIFT);
    ret = gu_inputs_frame(&ctx, &g, &in);
    check(ret == 0, "second frame succeeds");

    for (i = 0; i < W * H * 2; i++)
        nonzero += g.flow[i] != 0.0f;
    check(nonzero == 0, "motion vectors are zero");

    nonzero = 0;
    for (i = 0; i < W * H; i++)
        nonzero += g.reactive[i] != 0.0f;
    check(nonzero == 0, "reactive mask is zero even with react_mode=flow");

    nonzero = 0;
    for (i = 0; i < W * H; i++)
        nonzero += g.depth[i] != 0.5f;
    check(nonzero == 0, "depth is flat 0.5");

    dx = fabsf(g.jitter_x);
    check(g.pcpeak > 0.0f && dx > SHIFT - 1.0f && dx < SHIFT + 1.0f && fabsf(g.jitter_y) < 1.0f,
          "jitter is still measured (a 3 px shift reads as about 3 px)");
    printf("      jitter_x=%.3f jitter_y=%.3f pcpeak=%.3f\n", g.jitter_x, g.jitter_y, g.pcpeak);

    check(g.frame_index == 2 && g.have_previous, "two frames counted");

    gu_inputs_uninit(&g);
    check(g.flow == NULL && g.reactive == NULL && g.depth == NULL, "uninit frees the buffers");

    return failures ? 1 : 0;
}

#else /* default build: gu_inputs.h with CUDA, against stub ffnvcodec and Optical Flow headers */

/*
 * The stub CUDA loader (test-gu-inputs.sh writes its headers) hands gu_inputs.h function pointers
 * that record their own name. The Optical Flow library is never there, so the NVOFA step always
 * degrades and what is checked is the CUDA call order of the default build: the order an NVIDIA
 * server runs, and that a CUDA that fails at any step is still an error from gu_inputs_init, with
 * only what was taken released by gu_inputs_uninit.
 */

static char calls[256];
static const char *fail_at;

static void rec(const char *name)
{
    if (strlen(calls) + strlen(name) + 2 >= sizeof(calls))
        return;
    if (calls[0])
        strcat(calls, ",");
    strcat(calls, name);
}

static CUresult step(const char *name)
{
    rec(name);
    return fail_at && !strcmp(fail_at, name) ? 1 : CUDA_SUCCESS;
}

static CUresult f_init(unsigned int flags)            { return step("cuInit"); }
static CUresult f_get(CUdevice *d, int i)             { *d = i; return step("cuDeviceGet"); }
static CUresult f_retain(CUcontext *c, CUdevice d)
{
    CUresult r = step("retain");

    if (r == CUDA_SUCCESS)      /* a real driver leaves *c alone when it fails */
        *c = (CUcontext)(size_t)1;
    return r;
}
static CUresult f_release(CUdevice d)                 { return step("release"); }
static CUresult f_push(CUcontext c)                   { return step("push"); }
static CUresult f_pop(CUcontext *c)                   { return step("pop"); }
static CUresult f_copy(const CUDA_MEMCPY2D *m)        { return step("copy"); }
static CUresult f_name(CUresult r, const char **n)    { *n = "STUB"; return CUDA_SUCCESS; }

int fake_cuda_load_functions(CudaFunctions **f, void *logctx)
{
    rec("load");
    if (fail_at && !strcmp(fail_at, "load")) {
        *f = NULL;
        return AVERROR(ENOSYS);
    }
    *f = calloc(1, sizeof(**f));
    if (!*f)
        return AVERROR(ENOMEM);
    (*f)->cuInit = f_init;
    (*f)->cuDeviceGet = f_get;
    (*f)->cuDevicePrimaryCtxRetain = f_retain;
    (*f)->cuDevicePrimaryCtxRelease = f_release;
    (*f)->cuCtxPushCurrent = f_push;
    (*f)->cuCtxPopCurrent = f_pop;
    (*f)->cuMemcpy2D = f_copy;
    (*f)->cuGetErrorName = f_name;
    return 0;
}

void fake_cuda_free_functions(CudaFunctions **f)
{
    rec("free");
    free(*f);
    *f = NULL;
}

static void scenario(const char *name, const char *fail, const char *want_init, const char *want_all)
{
    const int want_ok = !fail;
    static float plane[3][W * H];
    float *planes[3] = { plane[0], plane[1], plane[2] };
    AVFilterContext ctx = { 0 };
    GUInputs g = { 0 };
    AVFrame in = { 0 };
    char msg[1024];
    int i, ret, nonzero = 0;

    printf("-- %s\n", name);
    calls[0] = 0;
    fail_at = fail;
    ctx.av_class = &test_class;
    in.data[0] = (uint8_t *)plane[0];
    in.data[1] = (uint8_t *)plane[1];
    in.data[2] = (uint8_t *)plane[2];
    in.linesize[0] = in.linesize[1] = in.linesize[2] = W * (int)sizeof(float);
    g.jitter_mode = GU_JITTER_MEASURED;
    g.depth_mode  = GU_DEPTH_FLAT;
    g.react_mode  = GU_REACT_FLOW;

    ret = gu_inputs_init(&ctx, &g, W, H);
    check(want_ok ? ret == 0 : ret < 0,
          want_ok ? "gu_inputs_init succeeds" : "gu_inputs_init returns an error");
    snprintf(msg, sizeof(msg), "init CUDA calls are %s (got %s)", want_init, calls);
    check(!strcmp(calls, want_init), msg);

    if (!want_ok) {
        gu_inputs_uninit(&g);
        snprintf(msg, sizeof(msg), "all CUDA calls are %s (got %s)", want_all, calls);
        check(!strcmp(calls, want_all), msg);
        check(g.cu == NULL && g.cu_ctx == NULL, "the loader and the context are released");
        return;
    }
    check(g.nvof_ready == 0, "no NVOFA, so the flow stays off");

    fill(planes, 0);
    ret = gu_inputs_frame(&ctx, &g, &in);
    fill(planes, SHIFT);
    ret |= gu_inputs_frame(&ctx, &g, &in);
    check(ret == 0, "two frames succeed");
    snprintf(msg, sizeof(msg), "frames make no CUDA call (got %s)", calls);
    check(!strcmp(calls, want_init), msg);
    for (i = 0; i < W * H * 2; i++)
        nonzero += g.flow[i] != 0.0f;
    for (i = 0; i < W * H; i++)
        nonzero += g.reactive[i] != 0.0f;
    check(nonzero == 0, "flow and reactive mask are zero");
    check(g.pcpeak > 0.0f && fabsf(g.jitter_x) > SHIFT - 1.0f && fabsf(g.jitter_x) < SHIFT + 1.0f,
          "jitter is still measured");

    gu_inputs_uninit(&g);
    snprintf(msg, sizeof(msg), "all CUDA calls are %s (got %s)", want_all, calls);
    check(!strcmp(calls, want_all), msg);
    check(g.cu == NULL, "the loader is released");
}

int main(void)
{
    void *lib = dlopen("libnvidia-opticalflow.so.1", RTLD_NOW);

    if (lib) {
        printf("SKIP  libnvidia-opticalflow is installed here and the stub headers cannot drive it\n");
        dlclose(lib);
        return 0;
    }

    scenario("CUDA loads and starts: the call order an NVIDIA server runs", NULL,
             "load,cuInit,cuDeviceGet,retain,push,pop",
             "load,cuInit,cuDeviceGet,retain,push,pop,push,pop,release,free");
    scenario("CUDA fails to load: init fails, nothing to free", "load",
             "load", "load");
    scenario("cuInit fails: init fails, free the loader, no context calls", "cuInit",
             "load,cuInit", "load,cuInit,free");
    scenario("cuDeviceGet fails: init fails, free the loader, no context calls", "cuDeviceGet",
             "load,cuInit,cuDeviceGet", "load,cuInit,cuDeviceGet,free");
    scenario("the retain fails (no context set): init fails, no push, pop or release", "retain",
             "load,cuInit,cuDeviceGet,retain", "load,cuInit,cuDeviceGet,retain,free");
    scenario("the context cannot be made current: init fails, release what was retained", "push",
             "load,cuInit,cuDeviceGet,retain,push",
             "load,cuInit,cuDeviceGet,retain,push,push,pop,release,free");

    return failures ? 1 : 0;
}
#endif
