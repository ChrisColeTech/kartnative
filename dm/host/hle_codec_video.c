// Switch 2 video decoders (nn::codec::video, SDK 19 for the Switch 2): the H.264 / VP8 / VP9 / AV1 decoder API and its image pools, which the SDK serves through the gsv
// service and NVIDIA's hardware decoder. The host decodes on a host thread per decoder (VP8 / VP9 with libvpx, H.264 with VideoToolbox through hle_movie_avc.c) into the image pools'
// memory, and stands in for the service's syncpoints with its own counters (hle_syncpt.c).
// The SDK's protocol, as the client side shows it (sdk+0x2c9c80 Register, +0x2c9fe0 Acquire, +0x2ca030 Release, +0x2ca800 output properties):
// - an image pool is `count` images of one layout in client memory; image i has the native image handle first + i;
// - the client registers up to 8 output buffers (decoder index → pool image); each registration or release hands the buffer a NativeSyncHandle that is reached once a frame
//   has been decoded into it, and queues the index on a ring; Acquire pops the ring (empty: 0x1c286f) with no service call. Frames therefore fill the images in
//   registration / release order, and the k-th handle handed out is reached by the k-th frame;
// - a release carries the client's own read fence: the image may be written again only after it is reached;
// - the "submission ready" SystemEvent lives in the decoder object (+8) and is signalled while the input queue has room (8 packets);
// - Submit takes one compressed frame with its time (TimeSpan, sent as µs) and an end-of-stream flag; an end-of-stream packet yields, after its own frames, one more output
//   flagged end of stream.
// Image layouts (preference word 1 = layout code; word 2 = (width - 1) | (height - 1) << 16), pitch-linear: codes 1 / 4 / 7 NV12, 2 / 5 / 8 BGRA, 3 / 6 / 9 RGBA, 10-13 HDR
// formats (written as 8-bit RGBA); pitch = AlignUp(width × (NV12 ? 1 : 4), 64), chroma at pitch × AlignUp(height, 128), size adds pitch × AlignUp((height + 1) / 2, 128) for NV12.
#include "hle.h"
#include <pthread.h>
#include <unistd.h>
#include "vpx/vpx_decoder.h"
#include "vpx/vp8dx.h"
#include "hle_yuv.h"
#include "hle_movie_avc.h"

#define CV_NOT_READY 0x1c286f   // the SDK's result for an empty output ring (and for "no frame finished yet")
#define CV_BUFS 8               // output buffers per decoder (the SDK's ring and handle table hold 8)
#define CV_MAXQ 8               // queued input packets while the submission ready event stays signalled
#define CV_IMAGES 1024          // native image handles the host hands out

// ---------- Image pools and native image handles ----------
static pthread_mutex_t g_img_mu = PTHREAD_MUTEX_INITIALIZER;
static dm_gs_image g_img[CV_IMAGES];
static uint8_t g_img_used[CV_IMAGES];
static size_t al(size_t v, size_t a) { return (v + a - 1) / a * a; }
static uint32_t cv_kind(uint32_t code) {   // dm_gs_image kind of a preference's layout code, 0 = unknown
    if (code >= 1 && code <= 9) return (uint32_t[]){ 8, 10, 9 }[code % 3];
    if (code >= 10 && code <= 13) { static int red; if (!red++) LOG("[RED] codec: HDR image layout %u written as 8-bit RGBA", code); return 8; }
    return 0;
}
static int cv_layout(const uint32_t *pref, dm_gs_image *im) {   // 0 for a layout code the host does not know
    memset(im, 0, sizeof *im);
    im->kind = cv_kind(pref[1]);
    im->w = (pref[2] & 0xffff) + 1; im->h = (pref[2] >> 16 & 0xffff) + 1;
    if (!im->kind) { LOG("[RED] codec: decoder image layout code %u is unknown (%ux%u)", pref[1], im->w, im->h); return 0; }
    int nv12 = im->kind == 10;
    im->pitch = al((size_t)im->w * (nv12 ? 1 : 4), 64);
    im->chroma_off = im->pitch * al(im->h, 128);
    im->size = im->chroma_off + (nv12 ? im->pitch * al((im->h + 1) / 2, 128) : 0);
    return 1;
}
const dm_gs_image *dm_gs_image_get(uint32_t handle) {
    pthread_mutex_lock(&g_img_mu);
    const dm_gs_image *r = handle && handle <= CV_IMAGES && g_img_used[handle - 1] ? &g_img[handle - 1] : NULL;
    pthread_mutex_unlock(&g_img_mu);
    return r;
}
// VideoDecoderImagePoolType as the SDK fills it (sdk+0x2c56f0): u32 native memory, u32 first image handle, u32 count, then the memory pointer at +0x10
typedef struct { uint32_t mem_handle, first, count, pad; uint8_t *mem; } cv_pool;

HLE_FORCE("_ZN2nn5codec5video10InitializeEv", "video decoder library: no service session (the host decodes itself)", Result, h_cv_Init, void) { return RES_OK; }
HLE_FORCE("_ZN2nn5codec5video8FinalizeEv", "video decoder library: no service session", Result, h_cv_Fini, void) { return RES_OK; }
HLE_FORCE("_ZN2nn5codec31CalculateDecoderImagePoolBufferEPmS1_RKNS0_27VideoDecoderImagePreferenceEj", "decoder image pool size: the host's pitch-linear layout (no gsv)",
          Result, h_cv_PoolSize, uint64_t *size, uint64_t *align, const uint32_t *pref, uint32_t count) {
    dm_gs_image im;
    if (!cv_layout(pref, &im)) return RES_FAIL;
    *align = 0x2000; *size = al(im.size * count, 0x2000);
    return RES_OK;
}
HLE_FORCE("_ZN2nn5codec26InitializeDecoderImagePoolEPNS0_25VideoDecoderImagePoolTypeEPvmRKNS0_27VideoDecoderImagePreferenceEjNS0_9CodecModeE", "decoder image pool: host images in the client's memory (no gsv)",
          Result, h_cv_PoolInit, cv_pool *pool, uint8_t *mem, uint64_t memsize, const uint32_t *pref, uint32_t count, int mode) {
    (void)mode;
    memset(pool, 0, sizeof *pool);
    dm_gs_image im;
    if (!cv_layout(pref, &im)) return RES_FAIL;
    if (!count || im.size * count > memsize) { LOG("[RED] codec: image pool of %u %ux%u images needs %#zx bytes, the client gave %#llx", count, im.w, im.h, im.size * count, memsize); return RES_FAIL; }
    pthread_mutex_lock(&g_img_mu);
    uint32_t first = 0;
    for (uint32_t i = 0; i + count <= CV_IMAGES && !first; i++) {
        uint32_t k = 0; while (k < count && !g_img_used[i + k]) k++;
        if (k == count) first = i + 1; else i += k;
    }
    if (first) for (uint32_t k = 0; k < count; k++) { g_img[first - 1 + k] = im; g_img[first - 1 + k].mem = mem + im.size * k; g_img_used[first - 1 + k] = 1; }
    pthread_mutex_unlock(&g_img_mu);
    if (!first) { LOG("[RED] codec: no room for %u more decoder images (%d in all)", count, CV_IMAGES); return RES_FAIL; }
    pool->first = first; pool->count = count; pool->mem = mem;
    return RES_OK;
}
HLE_FORCE("_ZN2nn5codec24FinalizeDecoderImagePoolEPNS0_25VideoDecoderImagePoolTypeE", "decoder image pool: frees its native image handles", void, h_cv_PoolFini, cv_pool *pool) {
    pthread_mutex_lock(&g_img_mu);
    for (uint32_t k = 0; k < pool->count; k++) if (pool->first + k - 1 < CV_IMAGES) g_img_used[pool->first + k - 1] = 0;
    pthread_mutex_unlock(&g_img_mu);
    memset(pool, 0, sizeof *pool);
}
HLE_FORCE("_ZN2nn5codec36GetDecoderImagePoolNativeImageHandleEPKNS0_25VideoDecoderImagePoolTypeEj", "decoder image: the host's native image handle (dm_gs_image_get)",
          uint32_t, h_cv_PoolHandle, const cv_pool *pool, uint32_t i) { return pool->first + i; }
// Copy a rectangle of an NV12 image out (luma rows, then the interleaved chroma rows of the rectangle's half height)
HLE_FORCE("_ZN2nn5codec26ReadDecoderOutputImageNv12EPKNS0_25VideoDecoderImagePoolTypeEjttttPvmmS4_mm", "decoder image: CPU copy of an NV12 image",
          Result, h_cv_ReadNv12, const cv_pool *pool, uint32_t i, uint32_t x, uint32_t y, uint32_t w, uint32_t h, uint8_t *ydst, uint64_t ypitch, uint64_t ysize, uint8_t *uvdst, uint64_t uvpitch, uint64_t uvsize) {
    x &= 0xffff; y &= 0xffff; w &= 0xffff; h &= 0xffff;
    const dm_gs_image *im = i < pool->count ? dm_gs_image_get(pool->first + i) : NULL;
    if (!im || im->kind != 10) { LOG("[RED] codec: ReadDecoderOutputImageNv12 on image %u of pool %u, which is not NV12 (kind %u)", i, pool->first, im ? im->kind : 0); return RES_FAIL; }
    if (x + w > im->w || y + h > im->h || (size_t)(h ? h - 1 : 0) * ypitch + w > ysize || (size_t)((h + 1) / 2 ? (h + 1) / 2 - 1 : 0) * uvpitch + al(w, 2) > uvsize) {
        LOG("[RED] codec: ReadDecoderOutputImageNv12 rectangle %u,%u %ux%u does not fit the %ux%u image or the client's buffers", x, y, w, h, im->w, im->h); return RES_FAIL; }
    for (uint32_t r = 0; r < h; r++) memcpy(ydst + r * ypitch, im->mem + (size_t)(y + r) * im->pitch + x, w);
    for (uint32_t r = 0; r < (h + 1) / 2; r++) memcpy(uvdst + r * uvpitch, im->mem + im->chroma_off + (size_t)(y / 2 + r) * im->pitch + (x & ~1u), al(w, 2));
    return RES_OK;
}

// ---------- Decoders ----------
enum { CV_H264, CV_VP8, CV_VP9, CV_AV1 };
static const char *const cv_name[] = { "H.264", "VP8", "VP9", "AV1" };
typedef struct { uint8_t *p; size_t n; int64_t pts_ns; int eos; } cv_in;
typedef struct {
    void *guest; int codec, w, h;
    pthread_mutex_t mu; pthread_cond_t cv; pthread_t th;
    uint32_t ctr; uint64_t handed;   // the host sync counter; handles handed out (the k-th is reached by the k-th frame)
    struct { int reg, filled; uint32_t img; uint64_t seq; dm_sync_handle fence; uint16_t fw, fh; int64_t pts_ns; int eos; } buf[CV_BUFS];
    uint32_t ring[CV_BUFS]; int rh, rn;    // the client's acquire ring (register / release order)
    uint32_t fill[CV_BUFS]; int fh, fn;    // the same order, for the decoding thread
    cv_in *q; int qh, qn, qcap; uint64_t nsub;
    int started, quit, gen, last;          // gen: bumped by Flush / Stop (the decoding thread resets its codec and drops what it was writing); last: the buffer filled last (-1 none)
    void *ready_ev; uint64_t ready;        // the submission ready SystemEvent (guest +8) and its binding
    // decoding thread only
    int dgen; vpx_codec_ctx_t vpx; int vpx_on; mavc *avc; const uint8_t *csd; size_t csd_n; uint8_t *nv12; int red;
    uint64_t frames;
} cvdec;

static cvdec *cv_of(const void *t) { cvdec *d = objmap_get(t); if (!d) die("codec: decoder %p was not initialized by the host", t); return d; }
static void cv_ready_signal(cvdec *d) {   // holding mu: the submission ready event while the queue has room
    if (d->started && d->qn < CV_MAXQ && d->ready) hle_signal_system_event_binding(d->ready);
}
// Hand one picture (or, for an end-of-stream output without one, the last picture again) to the next buffer in fill order; 0 when a Flush / Stop / Finalize came first
static int cv_emit(cvdec *d, const yuv_pic *pic, int64_t pts_ns, int eos, int gen) {
    dm_host_mutex_lock(&d->mu);
    while (!d->quit && d->gen == gen && !d->fn) pthread_cond_wait(&d->cv, &d->mu);
    if (d->quit || d->gen != gen) { pthread_mutex_unlock(&d->mu); return 0; }
    uint32_t i = d->fill[d->fh]; d->fh = (d->fh + 1) % CV_BUFS; d->fn--;
    dm_sync_handle fence = d->buf[i].fence; uint32_t img = d->buf[i].img; int last = d->last; uint32_t limg = last >= 0 ? d->buf[last].img : 0;
    pthread_mutex_unlock(&d->mu);
    if (!dm_syncpt_wait(&fence, 100000000ull)) { static int red; if (!red++) LOG("[RED] codec: the client's read fence on buffer %u did not pass in 100 ms; writing it anyway", i); }
    const dm_gs_image *im = dm_gs_image_get(img);
    uint16_t fw = 0, fh = 0;
    if (im && pic) {
        fw = (uint16_t)pic->w; fh = (uint16_t)pic->h;
        if (im->kind == 10) yuv_to_nv12(pic, im->mem, im->pitch, im->mem + im->chroma_off, im->pitch, (int)im->w, (int)im->h);
        else yuv_to_rgba(pic, im->mem, im->pitch, (int)im->w, (int)im->h, im->kind == 9);
    } else if (im) {   // end of stream without a picture: the last one again, so a client that shows it shows the movie's last frame
        const dm_gs_image *lm = limg ? dm_gs_image_get(limg) : NULL;
        if (lm && lm != im && lm->size == im->size) memcpy(im->mem, lm->mem, im->size);
        fw = last >= 0 ? d->buf[last].fw : (uint16_t)d->w; fh = last >= 0 ? d->buf[last].fh : (uint16_t)d->h;
    }
    dm_host_mutex_lock(&d->mu);
    d->buf[i].fw = fw; d->buf[i].fh = fh; d->buf[i].pts_ns = pts_ns; d->buf[i].eos = eos; d->buf[i].filled = 1; d->last = (int)i;
    pthread_cond_broadcast(&d->cv);
    pthread_mutex_unlock(&d->mu);
    dm_syncpt_incr(d->ctr);   // reaches buffer i's handle (its seq) and wakes the client's sync point event
    d->frames++;
    return 1;
}
static void cv_reset_codec(cvdec *d) {
    if (d->vpx_on) { vpx_codec_destroy(&d->vpx); d->vpx_on = 0; }
    if (d->avc) mavc_drop(d->avc);
}
static int cv_threads(void) { long n = sysconf(_SC_NPROCESSORS_ONLN); return n >= 8 ? 4 : 2; }
static void cv_decode(cvdec *d, const cv_in *in, int gen) {
    int64_t pts = in->pts_ns / 1000 * 1000;   // the SDK sends µs (sdk+0x2c9f20) and reports µs × 1000
    if (d->codec == CV_VP8 || d->codec == CV_VP9) {
        if (!d->vpx_on) {
            vpx_codec_dec_cfg_t cfg = { .threads = (unsigned)cv_threads(), .w = (unsigned)d->w, .h = (unsigned)d->h };
            vpx_codec_err_t e = vpx_codec_dec_init(&d->vpx, d->codec == CV_VP8 ? vpx_codec_vp8_dx() : vpx_codec_vp9_dx(), &cfg, 0);
            if (e) { LOG("[RED] codec: libvpx failed to create the %s decoder: %s", cv_name[d->codec], vpx_codec_err_to_string(e)); return; }
            if (d->codec == CV_VP9) vpx_codec_control(&d->vpx, VP9D_SET_ROW_MT, 1);
            d->vpx_on = 1;
        }
        if (in->n && vpx_codec_decode(&d->vpx, in->p, (unsigned)in->n, NULL, 0)) {
            if (!d->red++) LOG("[RED] codec: %s decode error (first case, %zu bytes): %s", cv_name[d->codec], in->n, vpx_codec_error(&d->vpx));
        } else if (in->n) {
            vpx_codec_iter_t it = NULL; vpx_image_t *im;
            while ((im = vpx_codec_get_frame(&d->vpx, &it))) {
                if (im->fmt != VPX_IMG_FMT_I420) { if (!d->red++) LOG("[RED] codec: %s picture format %#x is not 4:2:0 8-bit", cv_name[d->codec], im->fmt); continue; }
                yuv_pic p = yuv_from_vpx(im, d->h);
                if (!cv_emit(d, &p, pts, 0, gen)) return;
            }
        }
    } else if (d->codec == CV_H264) {
        if (!d->avc) { d->avc = mavc_new(); if (d->csd_n) mavc_param_sets(d->avc, d->csd, d->csd_n); }
        if (!d->nv12) d->nv12 = malloc((size_t)d->w * d->h * 3 / 2);
        if (in->n && mavc_decode(d->avc, in->p, (int)in->n, in->pts_ns / 1000) && !d->red++) LOG("[RED] codec: H.264 decode error (first case, %zu bytes)", in->n);
        int64_t us;
        while (mavc_take(d->avc, in->eos, d->nv12, d->w, d->h, &us)) {
            yuv_pic p = { { d->nv12, d->nv12 + (size_t)d->w * d->h, d->nv12 + (size_t)d->w * d->h + 1 }, { d->w, d->w, d->w }, d->w, d->h, 0, d->h >= 720, 2 };
            if (!cv_emit(d, &p, us * 1000, 0, gen)) return;
        }
    }
    if (in->eos) cv_emit(d, NULL, pts, 1, gen);   // end of stream: one more output carries the flag (its picture: the last one)
}
void dm_host_helper_qos(qos_class_t q);   // hle_thread.c
static void *cv_worker(void *arg) {
    cvdec *d = arg;
    pthread_setname_np("dm video decoder");
    dm_host_helper_qos(QOS_CLASS_USER_INITIATED);
    dm_host_mutex_lock(&d->mu);
    for (;;) {
        while (!d->quit && !(d->started && d->qn)) pthread_cond_wait(&d->cv, &d->mu);
        if (d->quit) break;
        cv_in in = d->q[d->qh]; d->qh = (d->qh + 1) % d->qcap; d->qn--;
        int gen = d->gen;
        cv_ready_signal(d);
        pthread_mutex_unlock(&d->mu);
        if (d->dgen != gen) { cv_reset_codec(d); d->dgen = gen; }
        cv_decode(d, &in, gen);
        free(in.p);
        dm_host_mutex_lock(&d->mu);
    }
    pthread_mutex_unlock(&d->mu);
    return NULL;
}
static void cv_drop_inputs(cvdec *d) { while (d->qn) { free(d->q[d->qh].p); d->qh = (d->qh + 1) % d->qcap; d->qn--; } }   // holding mu

static Result cv_init(void *t, int codec, uint32_t w, uint32_t h, const void *csd, uint64_t csd_n) {
    w &= 0xffff; h &= 0xffff;
    if (codec == CV_AV1) { LOG("[RED] codec: AV1 decoder (%ux%u) asked for: the host has no AV1 decoder", w, h); return RES_FAIL; }
    if (objmap_get(t)) die("codec: decoder %p initialized twice", t);
    cvdec *d = calloc(1, sizeof *d); if (!d) die("codec: out of memory");
    d->guest = t; d->codec = codec; d->w = (int)w; d->h = (int)h; d->last = -1;
    d->qcap = 64; d->q = calloc((size_t)d->qcap, sizeof *d->q);
    if (csd && csd_n) { uint8_t *c = malloc(csd_n); memcpy(c, csd, csd_n); d->csd = c; d->csd_n = csd_n; }
    pthread_mutex_init(&d->mu, NULL); pthread_cond_init(&d->cv, NULL);
    d->ctr = dm_syncpt_alloc(); d->handed = dm_syncpt_value(d->ctr);
    d->ready_ev = (uint8_t *)t + 8;   // where the SDK keeps it (sdk+0x2c9f10)
    hle_bind_system_event(d->ready_ev, 1);
    d->ready = hle_system_event_binding(d->ready_ev);
    pthread_attr_t a; pthread_attr_init(&a); pthread_attr_setstacksize(&a, 4 << 20);
    if (pthread_create(&d->th, &a, cv_worker, d)) die("codec: cannot start a decoding thread");
    pthread_attr_destroy(&a);
    objmap_put(t, d);
    LOG("[codec] %s decoder %ux%u (libvpx / VideoToolbox on a host thread)", cv_name[codec], w, h);
    return RES_OK;
}
static void cv_fini(void *t) {
    cvdec *d = objmap_del(t); if (!d) return;
    dm_host_mutex_lock(&d->mu); d->quit = 1; pthread_cond_broadcast(&d->cv); pthread_mutex_unlock(&d->mu);
    pthread_join(d->th, NULL);
    cv_reset_codec(d); if (d->avc) mavc_free(d->avc);
    cv_drop_inputs(d); free(d->q); free(d->nv12); free((void *)d->csd);
    dm_syncpt_free(d->ctr);
    static void (*destroy)(void *);   // the event's kernel handles close, which unbinds it (a new decoder in the same memory binds afresh)
    if (!destroy && !(destroy = guest_sym("_ZN2nn2os18DestroySystemEventEPNS0_15SystemEventTypeE"))) die("codec: missing SDK symbol nn::os::DestroySystemEvent");
    destroy(d->ready_ev);
    LOG("[codec] %s decoder done: %llu outputs", cv_name[d->codec], (unsigned long long)d->frames);
    pthread_mutex_destroy(&d->mu); pthread_cond_destroy(&d->cv); free(d);
}
static void cv_queue(cvdec *d, uint32_t i) {   // holding mu: a registered / released buffer gets its next handle and joins both orders
    d->buf[i].seq = ++d->handed; d->buf[i].filled = 0;
    d->ring[(d->rh + d->rn++) % CV_BUFS] = i;
    d->fill[(d->fh + d->fn++) % CV_BUFS] = i;
    pthread_cond_broadcast(&d->cv);
}
static Result cv_register(void *t, uint32_t i, const cv_pool *pool, uint32_t img) {
    cvdec *d = cv_of(t);
    if (i >= CV_BUFS || img >= pool->count) { LOG("[RED] codec: register buffer %u (image %u of %u)", i, img, pool->count); return RES_FAIL; }
    dm_host_mutex_lock(&d->mu);
    if (d->buf[i].reg) { pthread_mutex_unlock(&d->mu); return 0xfa26f; }   // the SDK's result for a buffer registered twice
    d->buf[i].reg = 1; d->buf[i].img = pool->first + img; d->buf[i].fence = (dm_sync_handle){ 0, 0, 0 };
    cv_queue(d, i);
    pthread_mutex_unlock(&d->mu);
    return RES_OK;
}
static Result cv_start(void *t) {
    cvdec *d = cv_of(t);
    dm_host_mutex_lock(&d->mu); d->started = 1; pthread_cond_broadcast(&d->cv); cv_ready_signal(d); pthread_mutex_unlock(&d->mu);
    return RES_OK;
}
static Result cv_flush(void *t, int stop) {   // queued packets are dropped and the codec starts over; buffers waiting for a frame keep waiting
    cvdec *d = cv_of(t);
    dm_host_mutex_lock(&d->mu);
    cv_drop_inputs(d); d->gen++;
    if (stop) d->started = 0;
    pthread_cond_broadcast(&d->cv); cv_ready_signal(d);
    pthread_mutex_unlock(&d->mu);
    return RES_OK;
}
static Result cv_submit(uint64_t *id, void *t, const void *p, uint64_t n, int64_t pts_ns, uint32_t eos) {
    cvdec *d = cv_of(t);
    cv_in in = { n ? malloc(n) : NULL, n, pts_ns, (int)(eos & 1) };
    if (n) memcpy(in.p, p, n);
    dm_host_mutex_lock(&d->mu);
    if (d->qn == d->qcap) {   // the client submits past the ready event: grow the queue rather than lose a packet
        cv_in *q = calloc((size_t)d->qcap * 2, sizeof *q);
        for (int k = 0; k < d->qn; k++) q[k] = d->q[(d->qh + k) % d->qcap];
        free(d->q); d->q = q; d->qh = 0; d->qcap *= 2;
    }
    d->q[(d->qh + d->qn++) % d->qcap] = in;
    *id = ++d->nsub;
    pthread_cond_broadcast(&d->cv); cv_ready_signal(d);
    pthread_mutex_unlock(&d->mu);
    return RES_OK;
}
static Result cv_acquire(uint32_t *idx, dm_sync_handle *h, void *t) {
    cvdec *d = cv_of(t);
    dm_host_mutex_lock(&d->mu);
    if (!d->rn) { pthread_mutex_unlock(&d->mu); return CV_NOT_READY; }
    uint32_t i = d->ring[d->rh]; d->rh = (d->rh + 1) % CV_BUFS; d->rn--;
    *idx = i; *h = dm_syncpt_handle(d->ctr, d->buf[i].seq);
    pthread_mutex_unlock(&d->mu);
    return RES_OK;
}
static void cv_release(void *t, uint32_t i, uint64_t h0, uint64_t h1) {
    cvdec *d = cv_of(t);
    if (i >= CV_BUFS) return;
    dm_host_mutex_lock(&d->mu);
    if (d->buf[i].reg) { memcpy(&d->buf[i].fence, (uint64_t[]){ h0, h1 }, 16); cv_queue(d, i); }
    pthread_mutex_unlock(&d->mu);
}
static Result cv_props(uint16_t *w, uint16_t *h, uint16_t *w2, uint16_t *h2, uint32_t *cs, int64_t *pts, uint8_t *eos, void *t, uint32_t i) {
    cvdec *d = cv_of(t);
    if (i >= CV_BUFS) return RES_FAIL;
    dm_host_mutex_lock(&d->mu);
    struct timespec dl; clock_gettime(CLOCK_REALTIME, &dl); dl.tv_sec += 1;   // the SDK waits for the service's next finished frame too (sdk+0x2ca800); a frame is normally there already
    while (d->buf[i].reg && !d->buf[i].filled && !d->quit) {
        dm_thread_native_wait(1); int r = pthread_cond_timedwait(&d->cv, &d->mu, &dl); dm_thread_native_wait(0);
        if (r == ETIMEDOUT) break;
    }
    if (!d->buf[i].filled) { pthread_mutex_unlock(&d->mu); LOG("[RED] codec: output properties of buffer %u, which holds no frame", i); return CV_NOT_READY; }
    *w = *w2 = d->buf[i].fw; *h = *h2 = d->buf[i].fh; *cs = 0; *pts = d->buf[i].pts_ns; *eos = (uint8_t)d->buf[i].eos;
    pthread_mutex_unlock(&d->mu);
    return RES_OK;
}

// The SDK's entry points, one set per codec (literal names, so dm/tools/nso_syms.py sees them); each decoder type keeps the submission ready SystemEvent at +8 (GetXxxDecoderSubmissionReadyEvent)
HLE_FORCE("_ZN2nn5codec18FinalizeVp9DecoderEPNS0_14Vp9DecoderTypeE", "video decoder finalize: stops the host decoding thread", void, h_cv_FiniVp9, void *t) { cv_fini(t); }
HLE_FORCE("_ZN2nn5codec24RegisterVp9DecoderBufferEPNS0_14Vp9DecoderTypeEjPNS0_25VideoDecoderImagePoolTypeEj", "video decoder: register an output buffer (host image ring)", Result, h_cv_RegVp9, void *t, uint32_t i, const cv_pool *pool, uint32_t img) { return cv_register(t, i, pool, img); }
HLE_FORCE("_ZN2nn5codec15StartVp9DecoderEPNS0_14Vp9DecoderTypeE", "video decoder start", Result, h_cv_StartVp9, void *t) { return cv_start(t); }
HLE_FORCE("_ZN2nn5codec14StopVp9DecoderEPNS0_14Vp9DecoderTypeE", "video decoder stop: queued input dropped, codec reset", void, h_cv_StopVp9, void *t) { cv_flush(t, 1); }
HLE_FORCE("_ZN2nn5codec15FlushVp9DecoderEPNS0_14Vp9DecoderTypeE", "video decoder flush: queued input dropped, codec reset", Result, h_cv_FlushVp9, void *t) { return cv_flush(t, 0); }
HLE_FORCE("_ZN2nn5codec33GetVp9DecoderSubmissionReadyEventEPPNS_2os15SystemEventTypeEPNS0_14Vp9DecoderTypeE", "video decoder: the submission ready event at +8, bound to a host-signalled kernel event", Result, h_cv_EvVp9, void **ev, uint8_t *t) { *ev = t + 8; return RES_OK; }
HLE_FORCE("_ZN2nn5codec27SubmitVp9DecoderInputBufferEPmPNS0_14Vp9DecoderTypeEPKvmNS_8TimeSpanEb", "video decoder: queue one compressed frame for the host decoding thread", Result, h_cv_SubVp9, uint64_t *id, void *t, const void *p, uint64_t n, int64_t pts, uint32_t eos) { return cv_submit(id, t, p, n, pts, eos); }
HLE_FORCE("_ZN2nn5codec29AcquireVp9DecoderOutputBufferEPjPNS_2gs16NativeSyncHandleEPNS0_14Vp9DecoderTypeE", "video decoder: pop the output ring (no service call, as in the SDK)", Result, h_cv_AcqVp9, uint32_t *idx, dm_sync_handle *h, void *t) { return cv_acquire(idx, h, t); }
HLE_FORCE("_ZN2nn5codec29ReleaseVp9DecoderOutputBufferEPNS0_14Vp9DecoderTypeEjNS_2gs16NativeSyncHandleE", "video decoder: release an output buffer (a new handle, back on the ring)", void, h_cv_RelVp9, void *t, uint32_t i, uint64_t h0, uint64_t h1) { cv_release(t, i, h0, h1); }
HLE_FORCE("_ZN2nn5codec35GetVp9DecoderOutputBufferPropertiesEPtS1_S1_S1_PNS_5movie10ColorSpaceEPNS_8TimeSpanEPbPNS0_14Vp9DecoderTypeEj", "video decoder: properties of a decoded frame", Result, h_cv_PropsVp9, uint16_t *w, uint16_t *h, uint16_t *w2, uint16_t *h2, uint32_t *cs, int64_t *pts, uint8_t *eos, void *t, uint32_t i) { return cv_props(w, h, w2, h2, cs, pts, eos, t, i); }
HLE_FORCE("_ZN2nn5codec18FinalizeVp8DecoderEPNS0_14Vp8DecoderTypeE", "video decoder finalize: stops the host decoding thread", void, h_cv_FiniVp8, void *t) { cv_fini(t); }
HLE_FORCE("_ZN2nn5codec24RegisterVp8DecoderBufferEPNS0_14Vp8DecoderTypeEjPNS0_25VideoDecoderImagePoolTypeEj", "video decoder: register an output buffer (host image ring)", Result, h_cv_RegVp8, void *t, uint32_t i, const cv_pool *pool, uint32_t img) { return cv_register(t, i, pool, img); }
HLE_FORCE("_ZN2nn5codec15StartVp8DecoderEPNS0_14Vp8DecoderTypeE", "video decoder start", Result, h_cv_StartVp8, void *t) { return cv_start(t); }
HLE_FORCE("_ZN2nn5codec14StopVp8DecoderEPNS0_14Vp8DecoderTypeE", "video decoder stop: queued input dropped, codec reset", void, h_cv_StopVp8, void *t) { cv_flush(t, 1); }
HLE_FORCE("_ZN2nn5codec15FlushVp8DecoderEPNS0_14Vp8DecoderTypeE", "video decoder flush: queued input dropped, codec reset", Result, h_cv_FlushVp8, void *t) { return cv_flush(t, 0); }
HLE_FORCE("_ZN2nn5codec33GetVp8DecoderSubmissionReadyEventEPPNS_2os15SystemEventTypeEPNS0_14Vp8DecoderTypeE", "video decoder: the submission ready event at +8, bound to a host-signalled kernel event", Result, h_cv_EvVp8, void **ev, uint8_t *t) { *ev = t + 8; return RES_OK; }
HLE_FORCE("_ZN2nn5codec27SubmitVp8DecoderInputBufferEPmPNS0_14Vp8DecoderTypeEPKvmNS_8TimeSpanEb", "video decoder: queue one compressed frame for the host decoding thread", Result, h_cv_SubVp8, uint64_t *id, void *t, const void *p, uint64_t n, int64_t pts, uint32_t eos) { return cv_submit(id, t, p, n, pts, eos); }
HLE_FORCE("_ZN2nn5codec29AcquireVp8DecoderOutputBufferEPjPNS_2gs16NativeSyncHandleEPNS0_14Vp8DecoderTypeE", "video decoder: pop the output ring (no service call, as in the SDK)", Result, h_cv_AcqVp8, uint32_t *idx, dm_sync_handle *h, void *t) { return cv_acquire(idx, h, t); }
HLE_FORCE("_ZN2nn5codec29ReleaseVp8DecoderOutputBufferEPNS0_14Vp8DecoderTypeEjNS_2gs16NativeSyncHandleE", "video decoder: release an output buffer (a new handle, back on the ring)", void, h_cv_RelVp8, void *t, uint32_t i, uint64_t h0, uint64_t h1) { cv_release(t, i, h0, h1); }
HLE_FORCE("_ZN2nn5codec35GetVp8DecoderOutputBufferPropertiesEPtS1_S1_S1_PNS_5movie10ColorSpaceEPNS_8TimeSpanEPbPNS0_14Vp8DecoderTypeEj", "video decoder: properties of a decoded frame", Result, h_cv_PropsVp8, uint16_t *w, uint16_t *h, uint16_t *w2, uint16_t *h2, uint32_t *cs, int64_t *pts, uint8_t *eos, void *t, uint32_t i) { return cv_props(w, h, w2, h2, cs, pts, eos, t, i); }
HLE_FORCE("_ZN2nn5codec19FinalizeH264DecoderEPNS0_15H264DecoderTypeE", "video decoder finalize: stops the host decoding thread", void, h_cv_FiniH264, void *t) { cv_fini(t); }
HLE_FORCE("_ZN2nn5codec25RegisterH264DecoderBufferEPNS0_15H264DecoderTypeEjPNS0_25VideoDecoderImagePoolTypeEj", "video decoder: register an output buffer (host image ring)", Result, h_cv_RegH264, void *t, uint32_t i, const cv_pool *pool, uint32_t img) { return cv_register(t, i, pool, img); }
HLE_FORCE("_ZN2nn5codec16StartH264DecoderEPNS0_15H264DecoderTypeE", "video decoder start", Result, h_cv_StartH264, void *t) { return cv_start(t); }
HLE_FORCE("_ZN2nn5codec15StopH264DecoderEPNS0_15H264DecoderTypeE", "video decoder stop: queued input dropped, codec reset", void, h_cv_StopH264, void *t) { cv_flush(t, 1); }
HLE_FORCE("_ZN2nn5codec16FlushH264DecoderEPNS0_15H264DecoderTypeE", "video decoder flush: queued input dropped, codec reset", Result, h_cv_FlushH264, void *t) { return cv_flush(t, 0); }
HLE_FORCE("_ZN2nn5codec34GetH264DecoderSubmissionReadyEventEPPNS_2os15SystemEventTypeEPNS0_15H264DecoderTypeE", "video decoder: the submission ready event at +8, bound to a host-signalled kernel event", Result, h_cv_EvH264, void **ev, uint8_t *t) { *ev = t + 8; return RES_OK; }
HLE_FORCE("_ZN2nn5codec28SubmitH264DecoderInputBufferEPmPNS0_15H264DecoderTypeEPKvmNS_8TimeSpanEb", "video decoder: queue one compressed frame for the host decoding thread", Result, h_cv_SubH264, uint64_t *id, void *t, const void *p, uint64_t n, int64_t pts, uint32_t eos) { return cv_submit(id, t, p, n, pts, eos); }
HLE_FORCE("_ZN2nn5codec30AcquireH264DecoderOutputBufferEPjPNS_2gs16NativeSyncHandleEPNS0_15H264DecoderTypeE", "video decoder: pop the output ring (no service call, as in the SDK)", Result, h_cv_AcqH264, uint32_t *idx, dm_sync_handle *h, void *t) { return cv_acquire(idx, h, t); }
HLE_FORCE("_ZN2nn5codec30ReleaseH264DecoderOutputBufferEPNS0_15H264DecoderTypeEjNS_2gs16NativeSyncHandleE", "video decoder: release an output buffer (a new handle, back on the ring)", void, h_cv_RelH264, void *t, uint32_t i, uint64_t h0, uint64_t h1) { cv_release(t, i, h0, h1); }
HLE_FORCE("_ZN2nn5codec36GetH264DecoderOutputBufferPropertiesEPtS1_S1_S1_PNS_5movie10ColorSpaceEPNS_8TimeSpanEPbPNS0_15H264DecoderTypeEj", "video decoder: properties of a decoded frame", Result, h_cv_PropsH264, uint16_t *w, uint16_t *h, uint16_t *w2, uint16_t *h2, uint32_t *cs, int64_t *pts, uint8_t *eos, void *t, uint32_t i) { return cv_props(w, h, w2, h2, cs, pts, eos, t, i); }
HLE_FORCE("_ZN2nn5codec18FinalizeAv1DecoderEPNS0_14Av1DecoderTypeE", "video decoder finalize: stops the host decoding thread", void, h_cv_FiniAv1, void *t) { cv_fini(t); }
HLE_FORCE("_ZN2nn5codec24RegisterAv1DecoderBufferEPNS0_14Av1DecoderTypeEjPNS0_25VideoDecoderImagePoolTypeEj", "video decoder: register an output buffer (host image ring)", Result, h_cv_RegAv1, void *t, uint32_t i, const cv_pool *pool, uint32_t img) { return cv_register(t, i, pool, img); }
HLE_FORCE("_ZN2nn5codec15StartAv1DecoderEPNS0_14Av1DecoderTypeE", "video decoder start", Result, h_cv_StartAv1, void *t) { return cv_start(t); }
HLE_FORCE("_ZN2nn5codec14StopAv1DecoderEPNS0_14Av1DecoderTypeE", "video decoder stop: queued input dropped, codec reset", void, h_cv_StopAv1, void *t) { cv_flush(t, 1); }
HLE_FORCE("_ZN2nn5codec15FlushAv1DecoderEPNS0_14Av1DecoderTypeE", "video decoder flush: queued input dropped, codec reset", Result, h_cv_FlushAv1, void *t) { return cv_flush(t, 0); }
HLE_FORCE("_ZN2nn5codec33GetAv1DecoderSubmissionReadyEventEPPNS_2os15SystemEventTypeEPNS0_14Av1DecoderTypeE", "video decoder: the submission ready event at +8, bound to a host-signalled kernel event", Result, h_cv_EvAv1, void **ev, uint8_t *t) { *ev = t + 8; return RES_OK; }
HLE_FORCE("_ZN2nn5codec27SubmitAv1DecoderInputBufferEPmPNS0_14Av1DecoderTypeEPKvmNS_8TimeSpanEb", "video decoder: queue one compressed frame for the host decoding thread", Result, h_cv_SubAv1, uint64_t *id, void *t, const void *p, uint64_t n, int64_t pts, uint32_t eos) { return cv_submit(id, t, p, n, pts, eos); }
HLE_FORCE("_ZN2nn5codec29AcquireAv1DecoderOutputBufferEPjPNS_2gs16NativeSyncHandleEPNS0_14Av1DecoderTypeE", "video decoder: pop the output ring (no service call, as in the SDK)", Result, h_cv_AcqAv1, uint32_t *idx, dm_sync_handle *h, void *t) { return cv_acquire(idx, h, t); }
HLE_FORCE("_ZN2nn5codec29ReleaseAv1DecoderOutputBufferEPNS0_14Av1DecoderTypeEjNS_2gs16NativeSyncHandleE", "video decoder: release an output buffer (a new handle, back on the ring)", void, h_cv_RelAv1, void *t, uint32_t i, uint64_t h0, uint64_t h1) { cv_release(t, i, h0, h1); }
HLE_FORCE("_ZN2nn5codec35GetAv1DecoderOutputBufferPropertiesEPtS1_S1_S1_PNS_5movie10ColorSpaceEPNS_8TimeSpanEPbPNS0_14Av1DecoderTypeEj", "video decoder: properties of a decoded frame", Result, h_cv_PropsAv1, uint16_t *w, uint16_t *h, uint16_t *w2, uint16_t *h2, uint32_t *cs, int64_t *pts, uint8_t *eos, void *t, uint32_t i) { return cv_props(w, h, w2, h2, cs, pts, eos, t, i); }

// Initialize: (decoder, [work buffer, its size,] u64, width, height, u32, input format, output format[, H.264 parameter sets, their size]); the host needs no work buffer
HLE_FORCE("_ZN2nn5codec20InitializeVp9DecoderEPNS0_14Vp9DecoderTypeEPvmmttjNS0_23VideoDecoderInputFormatENS0_24VideoDecoderOutputFormatE", "VP9 decoder: libvpx on a host thread",
          Result, h_cv_InitVp9W, void *t, void *work, uint64_t wn, uint64_t a, uint32_t w, uint32_t h, uint32_t rate, int in, int out) { (void)work; (void)wn; (void)a; (void)rate; (void)in; (void)out; return cv_init(t, CV_VP9, w, h, NULL, 0); }
HLE_FORCE("_ZN2nn5codec20InitializeVp9DecoderEPNS0_14Vp9DecoderTypeEmmttjNS0_23VideoDecoderInputFormatENS0_24VideoDecoderOutputFormatE", "VP9 decoder: libvpx on a host thread",
          Result, h_cv_InitVp9, void *t, uint64_t a, uint64_t b, uint32_t w, uint32_t h, uint32_t rate, int in, int out) { (void)a; (void)b; (void)rate; (void)in; (void)out; return cv_init(t, CV_VP9, w, h, NULL, 0); }
HLE_FORCE("_ZN2nn5codec20InitializeVp8DecoderEPNS0_14Vp8DecoderTypeEPvmmttjNS0_23VideoDecoderInputFormatENS0_24VideoDecoderOutputFormatE", "VP8 decoder: libvpx on a host thread",
          Result, h_cv_InitVp8W, void *t, void *work, uint64_t wn, uint64_t a, uint32_t w, uint32_t h, uint32_t rate, int in, int out) { (void)work; (void)wn; (void)a; (void)rate; (void)in; (void)out; return cv_init(t, CV_VP8, w, h, NULL, 0); }
HLE_FORCE("_ZN2nn5codec20InitializeVp8DecoderEPNS0_14Vp8DecoderTypeEmmttjNS0_23VideoDecoderInputFormatENS0_24VideoDecoderOutputFormatE", "VP8 decoder: libvpx on a host thread",
          Result, h_cv_InitVp8, void *t, uint64_t a, uint64_t b, uint32_t w, uint32_t h, uint32_t rate, int in, int out) { (void)a; (void)b; (void)rate; (void)in; (void)out; return cv_init(t, CV_VP8, w, h, NULL, 0); }
HLE_FORCE("_ZN2nn5codec21InitializeH264DecoderEPNS0_15H264DecoderTypeEPvmmttjNS0_23VideoDecoderInputFormatENS0_24VideoDecoderOutputFormatEPKvm", "H.264 decoder: VideoToolbox on a host thread",
          Result, h_cv_InitH264W, void *t, void *work, uint64_t wn, uint64_t a, uint32_t w, uint32_t h, uint32_t rate, int in, int out, const void *csd, uint64_t csd_n) { (void)work; (void)wn; (void)a; (void)rate; (void)in; (void)out; return cv_init(t, CV_H264, w, h, csd, csd_n); }
HLE_FORCE("_ZN2nn5codec21InitializeH264DecoderEPNS0_15H264DecoderTypeEmmttjNS0_23VideoDecoderInputFormatENS0_24VideoDecoderOutputFormatEPKvm", "H.264 decoder: VideoToolbox on a host thread",
          Result, h_cv_InitH264, void *t, uint64_t a, uint64_t b, uint32_t w, uint32_t h, uint32_t rate, int in, int out, const void *csd, uint64_t csd_n) { (void)a; (void)b; (void)rate; (void)in; (void)out; return cv_init(t, CV_H264, w, h, csd, csd_n); }
HLE_FORCE("_ZN2nn5codec20InitializeAv1DecoderEPNS0_14Av1DecoderTypeEPvmmttjNS0_23VideoDecoderInputFormatENS0_24VideoDecoderOutputFormatE", "AV1 decoder: the host has none, fails with a red line",
          Result, h_cv_InitAv1W, void *t, void *work, uint64_t wn, uint64_t a, uint32_t w, uint32_t h, uint32_t rate, int in, int out) { (void)work; (void)wn; (void)a; (void)rate; (void)in; (void)out; return cv_init(t, CV_AV1, w, h, NULL, 0); }
HLE_FORCE("_ZN2nn5codec20InitializeAv1DecoderEPNS0_14Av1DecoderTypeEmmttjNS0_23VideoDecoderInputFormatENS0_24VideoDecoderOutputFormatE", "AV1 decoder: the host has none, fails with a red line",
          Result, h_cv_InitAv1, void *t, uint64_t a, uint64_t b, uint32_t w, uint32_t h, uint32_t rate, int in, int out) { (void)a; (void)b; (void)rate; (void)in; (void)out; return cv_init(t, CV_AV1, w, h, NULL, 0); }
