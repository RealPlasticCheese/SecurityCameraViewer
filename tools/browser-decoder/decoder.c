/* SPDX-License-Identifier: LGPL-2.1-or-later
 * Streaming H.264/HEVC decoder. FFmpeg remains a separately replaceable WASM module.
 */
#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <libavcodec/avcodec.h>
#include <libavutil/imgutils.h>
#include <libswscale/swscale.h>

typedef struct {
    AVCodecContext *codec;
    AVFrame *frame;
    AVPacket *packet;
    struct SwsContext *scale;
    uint8_t *rgba;
    size_t capacity;
} CameraDecoder;

void camera_destroy(CameraDecoder *d)
{
    if (!d) return;
    avcodec_free_context(&d->codec);
    av_frame_free(&d->frame);
    av_packet_free(&d->packet);
    sws_freeContext(d->scale);
    av_free(d->rgba);
    free(d);
}

CameraDecoder *camera_create(int hevc)
{
    CameraDecoder *d = calloc(1, sizeof(*d));
    if (!d) return NULL;
    const AVCodec *codec = avcodec_find_decoder(hevc ? AV_CODEC_ID_HEVC : AV_CODEC_ID_H264);
    d->codec = avcodec_alloc_context3(codec);
    d->frame = av_frame_alloc();
    d->packet = av_packet_alloc();
    if (!d->codec || !d->frame || !d->packet) { camera_destroy(d); return NULL; }
    d->codec->thread_count = 1;
    d->codec->pkt_timebase = (AVRational){1, 1000000};
    if (avcodec_open2(d->codec, codec, NULL) < 0) { camera_destroy(d); return NULL; }
    return d;
}

int camera_push(CameraDecoder *d, const uint8_t *data, int length, double pts)
{
    if (!d || length < 0 || (length > 0 && !data)) return AVERROR(EINVAL);
    if (length == 0) return avcodec_send_packet(d->codec, NULL);
    av_packet_unref(d->packet);
    int result = av_new_packet(d->packet, length);
    if (result < 0) return result;
    memcpy(d->packet->data, data, length);
    d->packet->pts = (int64_t)pts;
    d->packet->dts = AV_NOPTS_VALUE;
    result = avcodec_send_packet(d->codec, d->packet);
    av_packet_unref(d->packet);
    return result;
}

int camera_next(CameraDecoder *d)
{
    if (!d) return AVERROR(EINVAL);
    av_frame_unref(d->frame);
    int result = avcodec_receive_frame(d->codec, d->frame);
    if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) return 0;
    if (result < 0) return result;
    const int width = d->frame->width, height = d->frame->height;
    int size = av_image_get_buffer_size(AV_PIX_FMT_RGBA, width, height, 1);
    if (size < 0) return size;
    if ((size_t)size > d->capacity) {
        uint8_t *rgba = av_realloc(d->rgba, size);
        if (!rgba) return AVERROR(ENOMEM);
        d->rgba = rgba; d->capacity = size;
    }
    d->scale = sws_getCachedContext(d->scale, width, height, d->frame->format,
        width, height, AV_PIX_FMT_RGBA, SWS_BICUBIC, NULL, NULL, NULL);
    if (!d->scale) return AVERROR(ENOMEM);
    int colorspace = d->frame->colorspace == AVCOL_SPC_BT709 ? SWS_CS_ITU709 :
        d->frame->colorspace == AVCOL_SPC_BT2020_NCL ? SWS_CS_BT2020 : SWS_CS_ITU601;
    const int *coefficients = sws_getCoefficients(colorspace);
    sws_setColorspaceDetails(d->scale, coefficients, d->frame->color_range == AVCOL_RANGE_JPEG,
        coefficients, 1, 0, 1 << 16, 1 << 16);
    uint8_t *dest[4] = {d->rgba, NULL, NULL, NULL};
    int stride[4] = {width * 4, 0, 0, 0};
    if (sws_scale(d->scale, (const uint8_t *const *)d->frame->data, d->frame->linesize,
        0, height, dest, stride) != height) return AVERROR(EINVAL);
    return 1;
}

int camera_width(CameraDecoder *d) { return d->frame->width; }
int camera_height(CameraDecoder *d) { return d->frame->height; }
uint8_t *camera_pixels(CameraDecoder *d) { return d->rgba; }
double camera_pts(CameraDecoder *d) { return (double)d->frame->best_effort_timestamp; }
