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
 * Which ONNX Runtime provider calls does ffmpeg/vf_ort.c make for each device= value?
 *
 * vf_ort.c is included whole (its functions are static), the ONNX Runtime API is a fake that
 * records what it is asked to do and fails on demand, and init_session() and uninit() run against
 * it. This is not an inference test: no model is loaded and no GPU is touched. It proves the
 * provider selection, the CPU fallbacks and that NVIDIA's default (CUDA) call sequence is the one
 * it was. scripts/test-vf-ort.sh builds and runs it. docs/architecture/GPU_VENDORS.md 5.4.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "libavutil/log.h"
#include "vf_ort.c"

/* ---- the fake ONNX Runtime ---------------------------------------------------------------- */

typedef struct FakeOpts { int has_ov, has_cuda, released; } FakeOpts;

static OrtApi fake;
static OrtApiBase fake_base;
static OrtAllocator fake_alloc;
static char err_text[128];

static struct {
    int fail_cuda_append;       /* CUDA provider append fails */
    int fail_ov_append;         /* OpenVINO provider append fails */
    int fail_session_with_ov;   /* CreateSession fails when the options carry OpenVINO */
    int fail_session_always;

    int create_options, release_options, cuda_append, ov_append, create_session;
    char ov_key[32], ov_val[32];
    size_t ov_nkeys;
    int session_had_ov[4], session_had_cuda[4];
    int session_opts_distinct;
    FakeOpts *first_opts;
} rec;

static char logbuf[2048];

static OrtStatus *err(const char *m)
{
    snprintf(err_text, sizeof(err_text), "%s", m);
    return (OrtStatus *)err_text;
}

static const char *ORT_API_CALL f_msg(const OrtStatus *s) { return (const char *)s; }
static void ORT_API_CALL f_rel_status(OrtStatus *s) { (void)s; }
static OrtStatus *ORT_API_CALL f_env(OrtLoggingLevel l, const char *id, OrtEnv **o) { (void)l; (void)id; *o = (OrtEnv *)&rec; return NULL; }
static OrtStatus *ORT_API_CALL f_opts(OrtSessionOptions **o)
{
    FakeOpts *f = calloc(1, sizeof(*f));
    rec.create_options++;
    if (!rec.first_opts)
        rec.first_opts = f;
    *o = (OrtSessionOptions *)f;
    return NULL;
}
static OrtStatus *ORT_API_CALL f_opt_level(OrtSessionOptions *o, GraphOptimizationLevel l) { (void)o; (void)l; return NULL; }
static OrtStatus *ORT_API_CALL f_threads(OrtSessionOptions *o, int n) { (void)o; (void)n; return NULL; }
static OrtStatus *ORT_API_CALL f_cuda_create(OrtCUDAProviderOptionsV2 **o) { *o = (OrtCUDAProviderOptionsV2 *)&rec; return NULL; }
static OrtStatus *ORT_API_CALL f_cuda_update(OrtCUDAProviderOptionsV2 *o, const char *const *k, const char *const *v, size_t n) { (void)o; (void)k; (void)v; (void)n; return NULL; }
static OrtStatus *ORT_API_CALL f_cuda_append(OrtSessionOptions *o, const OrtCUDAProviderOptionsV2 *c)
{
    (void)c;
    rec.cuda_append++;
    if (rec.fail_cuda_append)
        return err("no CUDA runtime");
    ((FakeOpts *)o)->has_cuda = 1;
    return NULL;
}
static void ORT_API_CALL f_cuda_release(OrtCUDAProviderOptionsV2 *o) { (void)o; }
static OrtStatus *ORT_API_CALL f_ov_append(OrtSessionOptions *o, const char *const *k, const char *const *v, size_t n)
{
    rec.ov_append++;
    rec.ov_nkeys = n;
    snprintf(rec.ov_key, sizeof(rec.ov_key), "%s", n ? k[0] : "");
    snprintf(rec.ov_val, sizeof(rec.ov_val), "%s", n ? v[0] : "");
    if (rec.fail_ov_append)
        return err("provider library not found");
    ((FakeOpts *)o)->has_ov = 1;
    return NULL;
}
static OrtStatus *ORT_API_CALL f_session(const OrtEnv *e, const char *path, const OrtSessionOptions *o, OrtSession **out)
{
    const FakeOpts *f = (const FakeOpts *)o;
    int i = rec.create_session++;
    (void)e; (void)path;
    if (i < 4) {
        rec.session_had_ov[i]   = f->has_ov;
        rec.session_had_cuda[i] = f->has_cuda;
    }
    if (i == 1 && f != rec.first_opts && rec.first_opts->released)
        rec.session_opts_distinct = 1;
    if (rec.fail_session_always || (rec.fail_session_with_ov && f->has_ov))
        return err("device initialisation failed");
    *out = (OrtSession *)&rec;
    return NULL;
}
static OrtStatus *ORT_API_CALL f_alloc(OrtAllocator **a) { *a = &fake_alloc; return NULL; }
static OrtStatus *ORT_API_CALL f_count(const OrtSession *s, size_t *n) { (void)s; *n = 1; return NULL; }
static OrtStatus *ORT_API_CALL f_name(const OrtSession *s, size_t i, OrtAllocator *a, char **v) { (void)s; (void)i; (void)a; *v = (char *)"x"; return NULL; }
static OrtStatus *ORT_API_CALL f_meminfo(enum OrtAllocatorType t, enum OrtMemType m, OrtMemoryInfo **o) { (void)t; (void)m; *o = (OrtMemoryInfo *)&rec; return NULL; }
static void ORT_API_CALL f_rel_meminfo(OrtMemoryInfo *o) { (void)o; }
static void ORT_API_CALL f_rel_session(OrtSession *o) { (void)o; }
/* Not freed: the test compares the first options object with the second by address. */
static void ORT_API_CALL f_rel_opts(OrtSessionOptions *o) { rec.release_options++; ((FakeOpts *)o)->released = 1; }
static void ORT_API_CALL f_rel_env(OrtEnv *o) { (void)o; }
static void ORT_API_CALL f_free(OrtAllocator *a, void *p) { (void)a; (void)p; }

static const OrtApi *ORT_API_CALL f_getapi(uint32_t v) { (void)v; return &fake; }

const OrtApiBase *ORT_API_CALL OrtGetApiBase(void)
{
    fake_base.GetApi = f_getapi;
    fake_base.GetVersionString = NULL;
    return &fake_base;
}

/* The four libavfilter symbols vf_ort.c reaches from code this test never calls. */
int ff_filter_execute(AVFilterContext *c, avfilter_action_func *f, void *a, int *r, int n) { (void)c; (void)f; (void)a; (void)r; (void)n; return 0; }
int ff_filter_frame(AVFilterLink *l, AVFrame *f) { (void)l; (void)f; return 0; }
int ff_filter_get_nb_threads(AVFilterContext *c) { (void)c; return 1; }
AVFrame *ff_get_video_buffer(AVFilterLink *l, int w, int h) { (void)l; (void)w; (void)h; return NULL; }

/* ---- the checks ---------------------------------------------------------------------------- */

static int failed, passed;

static void check(const char *name, int ok)
{
    if (ok) {
        passed++;
        printf("PASS  %s\n", name);
    } else {
        failed++;
        printf("FAIL  %s\n", name);
    }
}

static void capture(void *avcl, int level, const char *fmt, va_list vl)
{
    char line[512];
    (void)avcl;
    if (level > AV_LOG_WARNING)
        return;
    vsnprintf(line, sizeof(line), fmt, vl);
    if (strlen(logbuf) + strlen(line) < sizeof(logbuf) - 1)
        strcat(logbuf, line);
}

/* One init_session() run for a device value, from a clean fake, then uninit(). */
static int run(int device, int device_id, ORTContext **out_priv, AVFilterContext **out_ctx)
{
    AVFilterContext *ctx = calloc(1, sizeof(*ctx));
    ORTContext *s = calloc(1, sizeof(*s));
    int ret;

    {
        const int cuda = rec.fail_cuda_append, ov = rec.fail_ov_append,
                  with_ov = rec.fail_session_with_ov, always = rec.fail_session_always;
        memset(&rec, 0, sizeof(rec));
        rec.fail_cuda_append = cuda;
        rec.fail_ov_append = ov;
        rec.fail_session_with_ov = with_ov;
        rec.fail_session_always = always;
    }
    logbuf[0] = 0;
    ctx->av_class = &ort_class;
    ctx->priv = s;
    s->class = &ort_class;
    s->model = (char *)"model.onnx";
    s->device = device;
    s->device_id = device_id;
    ret = init_session(ctx);
    *out_priv = s;
    *out_ctx = ctx;
    return ret;
}

static void done(ORTContext *s, AVFilterContext *ctx)
{
    uninit(ctx);
    free(s);
    free(ctx);
}

int main(void)
{
    ORTContext *s;
    AVFilterContext *ctx;
    int ret;

    av_log_set_callback(capture);
    fake.GetErrorMessage = f_msg;
    fake.ReleaseStatus = f_rel_status;
    fake.CreateEnv = f_env;
    fake.CreateSessionOptions = f_opts;
    fake.SetSessionGraphOptimizationLevel = f_opt_level;
    fake.SetIntraOpNumThreads = f_threads;
    fake.SetInterOpNumThreads = f_threads;
    fake.CreateCUDAProviderOptions = f_cuda_create;
    fake.UpdateCUDAProviderOptions = f_cuda_update;
    fake.SessionOptionsAppendExecutionProvider_CUDA_V2 = f_cuda_append;
    fake.ReleaseCUDAProviderOptions = f_cuda_release;
    fake.SessionOptionsAppendExecutionProvider_OpenVINO_V2 = f_ov_append;
    fake.CreateSession = f_session;
    fake.GetAllocatorWithDefaultOptions = f_alloc;
    fake.SessionGetInputCount = f_count;
    fake.SessionGetOutputCount = f_count;
    fake.SessionGetInputName = f_name;
    fake.SessionGetOutputName = f_name;
    fake.CreateCpuMemoryInfo = f_meminfo;
    fake.ReleaseMemoryInfo = f_rel_meminfo;
    fake.ReleaseSession = f_rel_session;
    fake.ReleaseSessionOptions = f_rel_opts;
    fake.ReleaseEnv = f_rel_env;
    fake_alloc.Free = f_free;

    /* device=cuda is the default, and the NVIDIA call sequence: one CUDA append, no OpenVINO. */
    ret = run(ORT_DEVICE_CUDA, 0, &s, &ctx);
    check("cuda: the session is built", ret == 0);
    check("cuda: the CUDA provider is appended once and OpenVINO never", rec.cuda_append == 1 && rec.ov_append == 0);
    check("cuda: one session, built with the CUDA options", rec.create_session == 1 && rec.session_had_cuda[0] && !rec.session_had_ov[0]);
    check("cuda: the device is reported as cuda", strcmp(device_name(s), "cuda") == 0);
    done(s, ctx);

    rec.fail_cuda_append = 1;
    ret = run(ORT_DEVICE_CUDA, 0, &s, &ctx);
    check("cuda unavailable: still a session, on the CPU, with the warning", ret == 0 && rec.create_session == 1 && strstr(logbuf, "CUDA execution provider unavailable") != NULL);
    check("cuda unavailable: OpenVINO is not tried", rec.ov_append == 0);
    done(s, ctx);
    rec.fail_cuda_append = 0;

    ret = run(ORT_DEVICE_CPU, 0, &s, &ctx);
    check("cpu: no provider is appended at all", ret == 0 && rec.cuda_append == 0 && rec.ov_append == 0 && rec.create_session == 1);
    check("cpu: the device is reported as cpu", strcmp(device_name(s), "cpu") == 0);
    done(s, ctx);

    ret = run(ORT_DEVICE_OPENVINO, 0, &s, &ctx);
    check("openvino: the session is built", ret == 0);
    check("openvino: one append, device_type GPU, one key", rec.ov_append == 1 && rec.ov_nkeys == 1 && strcmp(rec.ov_key, "device_type") == 0 && strcmp(rec.ov_val, "GPU") == 0);
    check("openvino: CUDA is not appended", rec.cuda_append == 0);
    check("openvino: one session, built with the OpenVINO options, no warning", rec.create_session == 1 && rec.session_had_ov[0] && !rec.session_had_cuda[0] && logbuf[0] == 0);
    check("openvino: the device is reported as openvino", strcmp(device_name(s), "openvino") == 0);
    done(s, ctx);

    ret = run(ORT_DEVICE_OPENVINO, 1, &s, &ctx);
    check("openvino, device_id 1: device_type GPU.1", ret == 0 && strcmp(rec.ov_val, "GPU.1") == 0);
    done(s, ctx);
    ret = run(ORT_DEVICE_OPENVINO, 15, &s, &ctx);
    check("openvino, device_id 15: device_type GPU.15", ret == 0 && strcmp(rec.ov_val, "GPU.15") == 0);
    done(s, ctx);

    rec.fail_ov_append = 1;
    ret = run(ORT_DEVICE_OPENVINO, 0, &s, &ctx);
    check("openvino append fails: a CPU session is built, not an error", ret == 0 && rec.create_session == 1 && !rec.session_had_ov[0]);
    check("openvino append fails: the warning says so", strstr(logbuf, "OpenVINO execution provider unavailable (provider library not found), using CPU") != NULL);
    check("openvino append fails: the device is reported as cpu", strcmp(device_name(s), "cpu") == 0);
    done(s, ctx);
    rec.fail_ov_append = 0;

    rec.fail_session_with_ov = 1;
    ret = run(ORT_DEVICE_OPENVINO, 0, &s, &ctx);
    check("openvino session fails: the filter still comes up", ret == 0);
    check("openvino session fails: exactly two sessions were tried, the first with OpenVINO and the second without", rec.create_session == 2 && rec.session_had_ov[0] && !rec.session_had_ov[1]);
    check("openvino session fails: the second used fresh options, and the first was released first", rec.session_opts_distinct && rec.create_options == 2 && rec.release_options == 1);
    check("openvino session fails: the warning says so", strstr(logbuf, "OpenVINO session could not be created (device initialisation failed), using CPU") != NULL);
    check("openvino session fails: the device is reported as cpu", strcmp(device_name(s), "cpu") == 0 && s->ep_openvino == 0);
    done(s, ctx);
    rec.fail_session_with_ov = 0;

    rec.fail_session_always = 1;
    ret = run(ORT_DEVICE_OPENVINO, 0, &s, &ctx);
    check("openvino and the CPU both fail: an error, after exactly one retry", ret == AVERROR_EXTERNAL && rec.create_session == 2);
    done(s, ctx);

    ret = run(ORT_DEVICE_CPU, 0, &s, &ctx);
    check("cpu fails: an error, with no retry", ret == AVERROR_EXTERNAL && rec.create_session == 1);
    done(s, ctx);
    rec.fail_session_always = 0;

    check("the option range reaches openvino", ort_options[1].max == ORT_DEVICE_OPENVINO && ort_options[1].default_val.i64 == ORT_DEVICE_CUDA);

    printf("\n%d passed, %d failed\n", passed, failed);
    return failed ? 1 : 0;
}
