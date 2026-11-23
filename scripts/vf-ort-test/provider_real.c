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
 * ffmpeg/vf_ort.c's session setup against the REAL ONNX Runtime, for device=cuda, cpu and openvino.
 *
 * Unlike provider_calls.c this loads a real libonnxruntime (the OpenVINO build that
 * scripts/build-ffmpeg.sh ORT_EP=openvino stages) and a real .onnx file, so it shows what the
 * provider does on THIS machine: on one with an Intel GPU and its OpenCL runtime the last line of the
 * openvino case reads device_name=openvino, ep_openvino=1; on one without, the filter warns and builds
 * a CPU session (device_name=cpu), which is the fallback working. (device=cuda reports cuda even where
 * the warning above it says the CUDA provider is absent: the CUDA branch is not tracked, by design.) Nothing is inferred and no frame is processed. The model must have one input
 * and one output (any of the Real-ESRGAN files does). Run by scripts/test-vf-ort.sh when ORT_LIB_DIR and
 * ORT_MODEL are set; exits 1 if any session could not be built at all.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "libavutil/log.h"
#include "vf_ort.c"

/* The four libavfilter symbols vf_ort.c reaches from code this test never calls. */
int ff_filter_execute(AVFilterContext *c, avfilter_action_func *f, void *a, int *r, int n) { (void)c; (void)f; (void)a; (void)r; (void)n; return 0; }
int ff_filter_frame(AVFilterLink *l, AVFrame *f) { (void)l; (void)f; return 0; }
int ff_filter_get_nb_threads(AVFilterContext *c) { (void)c; return 1; }
AVFrame *ff_get_video_buffer(AVFilterLink *l, int w, int h) { (void)l; (void)w; (void)h; return NULL; }

int main(int argc, char **argv)
{
    static const int devices[]  = { ORT_DEVICE_CUDA, ORT_DEVICE_CPU, ORT_DEVICE_OPENVINO };
    static const char *names[]  = { "cuda", "cpu", "openvino" };
    int failed = 0;

    if (argc != 2) {
        fprintf(stderr, "usage: %s model.onnx\n", argv[0]);
        return 2;
    }
    av_log_set_level(AV_LOG_VERBOSE);
    printf("ONNX Runtime %s, header API %d\n", OrtGetApiBase()->GetVersionString(), ORT_API_VERSION);

    for (int i = 0; i < 3; i++) {
        AVFilterContext *ctx = calloc(1, sizeof(*ctx));
        ORTContext *s = calloc(1, sizeof(*s));
        int ret;

        ctx->av_class = &ort_class;
        ctx->priv = s;
        s->class = &ort_class;
        s->model = argv[1];
        s->device = devices[i];
        printf("=== device=%s\n", names[i]);
        fflush(stdout);
        ret = init_session(ctx);
        printf("=== device=%s: init_session %d, device_name=%s, ep_openvino=%d\n", names[i], ret, device_name(s), s->ep_openvino);
        fflush(stdout);
        if (ret < 0)
            failed = 1;
        uninit(ctx);
        free(s);
        free(ctx);
    }
    return failed;
}
