// Switch 2 video decoders (hle_codec_video.c) and sync counters (hle_syncpt.c) through the SDK's entry points, as Mario Kart World's movie player drives them
// (main+0x19e5860 configure, +0x19e60a0 register, +0x19e6320 take a frame, +0x19e6e00 release and acquire again):
// a synthetic VP9 key frame (16x16 red, no game data) checks the image layout, the ring order, the handles and the end-of-stream output.
// With a WebM file given (t_codec_video <movie.webm> <frame> <out.png>), its VP9 track goes through the same calls and the chosen frame is written as a PNG
// (Mario Kart World: RomFS Movie/*.webm; the file stays outside the repository).
#include "../hle.h"
#undef HLE_FORCE
#define HLE_FORCE(mangled, why, ret, cname, ...) static ret cname(__VA_ARGS__)
#include "../hle_syncpt.c"
#include "../hle_codec_video.c"
#include <assert.h>
#include <ImageIO/ImageIO.h>
#include <CoreServices/CoreServices.h>

// ---- host stand-ins: guest SystemEvents become flags; objmap a small table ----
__thread _Atomic int dm_host_work;
uint64_t now_ns(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (uint64_t)t.tv_sec * 1000000000ull + (uint64_t)t.tv_nsec; }
void dm_thread_native_wait(int entering) { (void)entering; }
void dm_host_helper_qos(qos_class_t q) { (void)q; }
static struct { void *ev; int sig; uint64_t id; } ev_tab[256]; static int nev; static pthread_mutex_t ev_mu = PTHREAD_MUTEX_INITIALIZER;
static int ev_find(void *e) { for (int i = 0; i < nev; i++) if (ev_tab[i].ev == e) return i; return -1; }
void hle_bind_system_event(void *e, int ac) { (void)ac; pthread_mutex_lock(&ev_mu); if (ev_find(e) < 0) { ev_tab[nev].ev = e; ev_tab[nev].id = (uint64_t)nev + 1; nev++; } pthread_mutex_unlock(&ev_mu); }
uint64_t hle_system_event_binding(void *e) { pthread_mutex_lock(&ev_mu); int i = ev_find(e); pthread_mutex_unlock(&ev_mu); return i < 0 ? 0 : ev_tab[i].id; }
int hle_signal_system_event_binding(uint64_t b) { pthread_mutex_lock(&ev_mu); if (b && b <= (uint64_t)nev) ev_tab[b - 1].sig = 1; pthread_mutex_unlock(&ev_mu); return 1; }
void hle_signal_system_event(void *e) { pthread_mutex_lock(&ev_mu); int i = ev_find(e); if (i >= 0) ev_tab[i].sig = 1; pthread_mutex_unlock(&ev_mu); }
void hle_clear_system_event(void *e) { pthread_mutex_lock(&ev_mu); int i = ev_find(e); if (i >= 0) ev_tab[i].sig = 0; pthread_mutex_unlock(&ev_mu); }
static int ev_state(void *e) { pthread_mutex_lock(&ev_mu); int i = ev_find(e), s = i >= 0 && ev_tab[i].sig; pthread_mutex_unlock(&ev_mu); return s; }
static int g_destroyed;
static void destroy_ev(void *e) { pthread_mutex_lock(&ev_mu); int i = ev_find(e); if (i >= 0) { ev_tab[i].ev = NULL; ev_tab[i].sig = 0; } pthread_mutex_unlock(&ev_mu); g_destroyed++; }   // closing the handles unbinds
static int trywait_ev(void *e) { return ev_state(e); }
void *guest_sym(const char *n) { if (strstr(n, "DestroySystemEvent")) return (void *)destroy_ev; if (strstr(n, "TryWaitSystemEvent")) return (void *)trywait_ev; return NULL; }
static struct { const void *g; void *h; } om[64];
void *objmap_get(const void *g) { for (int i = 0; i < 64; i++) if (om[i].g == g) return om[i].h; return NULL; }
void objmap_put(const void *g, void *h) { for (int i = 0; i < 64; i++) if (!om[i].g) { om[i].g = g; om[i].h = h; return; } abort(); }
void *objmap_del(const void *g) { for (int i = 0; i < 64; i++) if (om[i].g == g) { void *h = om[i].h; om[i].g = NULL; return h; } return NULL; }

static const uint8_t vp9_red[] = {   // the 16x16 red key frame of t_movie_vpx.c
    0x82,0x49,0x83,0x42,0x00,0x00,0xf0,0x00,0xf6,0x00,0x38,0x24,0x1c,0x18,0x4a,0x00,
    0x00,0x30,0x60,0x00,0x00,0x10,0xbf,0xff,0xf7,0x1d,0xaf,0xff,0xff,0xff,0x5f,0xdf,
    0xff,0xff,0xff,0xf2,0x2a,0xc0,0x00,
};
// MakeDecodeImagePreference (the SDK's own, sdk+0x2c5520) for NV12 in codec mode 1: word 1 = layout code 1, word 2 = (w - 1) | (h - 1) << 16
static void pref_nv12(uint32_t *p, int w, int h) { memset(p, 0, 64); p[1] = 1; p[2] = (uint32_t)(w - 1) | (uint32_t)(h - 1) << 16; }

typedef struct { _Alignas(16) uint8_t t[0x400]; cv_pool pool[8]; uint8_t *mem[8]; int w, h, nbuf; uint32_t ev_slot[8]; } player;
static void wait_handle(const dm_sync_handle *h) { assert(dm_syncpt_wait(h, 5000000000ull)); }

static void player_open(player *p, int w, int h, int nbuf) {
    memset(p, 0, sizeof *p); p->w = w; p->h = h; p->nbuf = nbuf;
    assert(!h_cv_Init());
    assert(!h_cv_InitVp9W(p->t, NULL, 0x6400000, 0, (uint32_t)w, (uint32_t)h, 60, 1, 3));
    uint32_t pref[16]; pref_nv12(pref, w, h);
    uint64_t size, align; assert(!h_cv_PoolSize(&size, &align, pref, 1));
    dm_gs_image lay; assert(cv_layout(pref, &lay));
    assert(align == 0x2000 && size == al(lay.size, 0x2000) && lay.kind == 10 && lay.pitch == al((size_t)w, 64) && lay.chroma_off == lay.pitch * al((size_t)h, 128));
    for (int i = 0; i < nbuf; i++) {
        p->mem[i] = aligned_alloc(0x2000, size);
        assert(!h_cv_PoolInit(&p->pool[i], p->mem[i], size, pref, 1, 1));
        assert(h_cv_PoolHandle(&p->pool[i], 0) == p->pool[i].first && dm_gs_image_get(p->pool[i].first)->mem == p->mem[i]);
        assert(!h_cv_RegVp9(p->t, (uint32_t)i, &p->pool[i], 0));
    }
    assert(h_cv_RegVp9(p->t, 0, &p->pool[0], 0) == 0xfa26f);   // twice: the SDK's result
    void *ev; assert(!h_cv_EvVp9(&ev, p->t) && ev == p->t + 8);
    assert(!ev_state(ev));
    assert(!h_cv_StartVp9(p->t));
    assert(ev_state(ev));   // room for input
}
static void player_close(player *p) {
    int d0 = g_destroyed;
    h_cv_FiniVp9(p->t);
    assert(g_destroyed == d0 + 1 && !objmap_get(p->t));
    for (int i = 0; i < p->nbuf; i++) { h_cv_PoolFini(&p->pool[i]); free(p->mem[i]); }
}

static void synthetic(void) {
    player p; player_open(&p, 16, 16, 2);
    // acquire both, in registration order, with consecutive handles
    uint32_t i0, i1, ix; dm_sync_handle h0, h1, hx;
    assert(!h_cv_AcqVp9(&i0, &h0, p.t) && i0 == 0);
    assert(!h_cv_AcqVp9(&i1, &h1, p.t) && i1 == 1 && h1.id_inv == h0.id_inv && h1.value == h0.value + 1);
    assert(h_cv_AcqVp9(&ix, &hx, p.t) == CV_NOT_READY);
    assert(!dm_syncpt_reached(&h0));
    // a sync point event armed on the first handle, as main+0x19e7760 does (TryWait, Cancel, Request)
    uint32_t spe; assert(!h_sp_Create(&spe));
    assert(!h_sp_TryWait(&spe));
    h_sp_Cancel(&spe); assert(h_sp_TryWait(&spe));   // cancel always signals
    h_sp_Request(&spe, &h0); assert(!h_sp_TryWait(&spe));   // request clears and arms
    uint64_t id;
    assert(!h_cv_SubVp9(&id, p.t, vp9_red, sizeof vp9_red, 1500000123, 0) && id == 1);
    wait_handle(&h0);
    for (int k = 0; k < 100 && !h_sp_TryWait(&spe); k++) usleep(1000);
    assert(h_sp_TryWait(&spe));   // the counter signalled the armed event
    uint16_t w, h, w2, h2; uint32_t cs; int64_t pts; uint8_t eos;
    assert(!h_cv_PropsVp9(&w, &h, &w2, &h2, &cs, &pts, &eos, p.t, i0) && w == 16 && h == 16 && w2 == 16 && h2 == 16 && cs == 0 && pts == 1500000000 && !eos);
    uint8_t y[256], uv[128];
    assert(!h_cv_ReadNv12(&p.pool[i0], 0, 0, 0, 16, 16, y, 16, sizeof y, uv, 16, sizeof uv));
    for (int k = 0; k < 256; k++) assert(abs(y[k] - 81) <= 3);
    for (int k = 0; k < 128; k += 2) { assert(abs(uv[k] - 90) <= 3); assert(abs(uv[k + 1] - 240) <= 3); }
    assert(h_cv_ReadNv12(&p.pool[i0], 0, 8, 8, 16, 16, y, 16, sizeof y, uv, 16, sizeof uv) == RES_FAIL);   // outside the image
    // end of stream: the second buffer gets the flag and the last picture again
    assert(!h_cv_SubVp9(&id, p.t, NULL, 0, 1600000000, 1) && id == 2);
    wait_handle(&h1);
    assert(!h_cv_PropsVp9(&w, &h, &w2, &h2, &cs, &pts, &eos, p.t, i1) && eos && w == 16 && pts == 1600000000);
    assert(!memcmp(dm_gs_image_get(p.pool[0].first)->mem, dm_gs_image_get(p.pool[1].first)->mem, dm_gs_image_get(p.pool[0].first)->size));
    // release (zero fence, as main does) and acquire again: the next handle, reached by the next frame
    h_cv_RelVp9(p.t, i0, 0, 0);
    assert(!h_cv_AcqVp9(&ix, &hx, p.t) && ix == i0 && hx.value == h1.value + 1 && !dm_syncpt_reached(&hx));
    assert(!h_cv_FlushVp9(p.t));   // flush: the waiting buffer keeps waiting, the next frame fills it
    assert(!h_cv_SubVp9(&id, p.t, vp9_red, sizeof vp9_red, 0, 0));
    wait_handle(&hx);
    h_sp_Destroy(&spe);
    player_close(&p);
    puts("codec: VP9 through the SDK calls: layout, ring order, handles, sync point events, end of stream, flush");
}

// ---- a WebM (Matroska) reader for the test: the first video track's blocks ----
static uint64_t vint(const uint8_t **p, const uint8_t *e, int keep) {
    if (*p >= e) return 0;
    int n = __builtin_clz((uint32_t)**p << 24 | 0x800000) + 1; uint64_t v = keep ? **p : (uint64_t)(**p & (0xff >> n));
    for (int i = 1; i < n && *p + i < e; i++) v = v << 8 | (*p)[i];
    *p += n; return v;
}
typedef struct { const uint8_t *p; size_t n; int64_t ns; } wpkt;
static wpkt *g_pk; static int g_npk, g_cappk; static uint64_t g_track, g_scale = 1000000, g_cluster; static int g_w, g_h, g_vp9;
static void webm(const uint8_t *p, const uint8_t *e, uint64_t cur_track) {
    while (p < e) {
        uint64_t id = vint(&p, e, 1), sz = vint(&p, e, 0);
        const uint8_t *b = p, *be = sz == 0xffffffffffffffull || p + sz > e ? e : p + sz;
        switch (id) {
        case 0x18538067: case 0x1F43B675: case 0x1654AE6B: case 0xA0: case 0x1549A966: webm(b, be, cur_track); break;   // Segment, Cluster, Tracks, BlockGroup, Info
        case 0xAE: { uint64_t tn = 0; const uint8_t *q = b; while (q < be) { uint64_t i2 = vint(&q, be, 1), s2 = vint(&q, be, 0); if (i2 == 0xD7) { tn = 0; for (uint64_t k = 0; k < s2; k++) tn = tn << 8 | q[k]; } q += s2; } webm(b, be, tn); break; }
        case 0x86: if (!g_track && sz == 5 && !memcmp(b, "V_VP9", 5)) { g_track = cur_track; g_vp9 = 1; } break;
        case 0xE0: if (cur_track && cur_track == g_track) webm(b, be, cur_track); break;
        case 0xB0: case 0xBA: { uint64_t v = 0; for (uint64_t k = 0; k < sz; k++) v = v << 8 | b[k]; if (id == 0xB0) g_w = (int)v; else g_h = (int)v; break; }
        case 0x2AD7B1: { uint64_t v = 0; for (uint64_t k = 0; k < sz; k++) v = v << 8 | b[k]; g_scale = v; break; }
        case 0xE7: { uint64_t v = 0; for (uint64_t k = 0; k < sz; k++) v = v << 8 | b[k]; g_cluster = v; break; }
        case 0xA3: case 0xA1: {   // SimpleBlock / Block: track, s16 time, flags, one frame (video is not laced)
            const uint8_t *q = b; uint64_t tn = vint(&q, be, 0);
            int16_t rel = (int16_t)(q[0] << 8 | q[1]); uint8_t fl = q[2]; q += 3;
            if (tn == g_track && !(fl & 6)) {
                if (g_npk == g_cappk) { g_cappk = g_cappk ? g_cappk * 2 : 1024; g_pk = realloc(g_pk, (size_t)g_cappk * sizeof *g_pk); }
                g_pk[g_npk++] = (wpkt){ q, (size_t)(be - q), (int64_t)(g_cluster + rel) * (int64_t)g_scale };
            }
            break; }
        default: break;
        }
        p = be;
    }
}
static void write_png(const char *path, const uint8_t *rgba, int w, int h) {
    CGColorSpaceRef cs = CGColorSpaceCreateDeviceRGB();
    CGContextRef c = CGBitmapContextCreate((void *)rgba, (size_t)w, (size_t)h, 8, (size_t)w * 4, cs, kCGImageAlphaNoneSkipLast);
    CGImageRef im = CGBitmapContextCreateImage(c);
    CFURLRef u = CFURLCreateFromFileSystemRepresentation(NULL, (const UInt8 *)path, (CFIndex)strlen(path), false);
    CGImageDestinationRef d = CGImageDestinationCreateWithURL(u, CFSTR("public.png"), 1, NULL);
    CGImageDestinationAddImage(d, im, NULL); assert(CGImageDestinationFinalize(d));
    CFRelease(d); CFRelease(u); CGImageRelease(im); CGContextRelease(c); CGColorSpaceRelease(cs);
}
// Play a movie the way main's player does: submit while the ready event is up, keep every buffer acquired, take frames in handle order, release each after reading it
static void movie(const char *path, int want, const char *png) {
    FILE *f = fopen(path, "rb"); assert(f); fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET);
    uint8_t *data = malloc((size_t)n); assert(fread(data, 1, (size_t)n, f) == (size_t)n); fclose(f);
    webm(data, data + n, 0);
    assert(g_vp9 && g_npk && g_w && g_h);
    printf("codec: %s: VP9 %dx%d, %d packets\n", path, g_w, g_h, g_npk);
    player p; player_open(&p, (g_w + 1) & ~1, (g_h + 1) & ~1, 4);
    uint32_t q[8]; dm_sync_handle qh[8]; int qn = 0, sent = 0, taken = 0; uint64_t id;
    for (int i = 0; i < p.nbuf; i++) { assert(!h_cv_AcqVp9(&q[qn], &qh[qn], p.t)); qn++; }
    size_t ysz = (size_t)p.w * p.h; uint8_t *nv = malloc(ysz * 3 / 2), *rgba = malloc(ysz * 4);
    uint64_t t0 = now_ns(); int last = want < g_npk ? want : g_npk - 1;
    while (taken <= last) {
        while (sent < g_npk && sent - taken < CV_MAXQ + p.nbuf) { assert(!h_cv_SubVp9(&id, p.t, g_pk[sent].p, g_pk[sent].n, g_pk[sent].ns, sent == g_npk - 1)); sent++; }
        wait_handle(&qh[0]);
        uint32_t i = q[0]; memmove(q, q + 1, (size_t)--qn * sizeof *q); memmove(qh, qh + 1, (size_t)qn * sizeof *qh);
        uint16_t w, h, w2, h2; uint32_t cs; int64_t pts; uint8_t eos;
        assert(!h_cv_PropsVp9(&w, &h, &w2, &h2, &cs, &pts, &eos, p.t, i));
        if (taken == last) {
            assert(!h_cv_ReadNv12(&p.pool[i], 0, 0, 0, (uint32_t)p.w, (uint32_t)p.h, nv, (uint64_t)p.w, ysz, nv + ysz, (uint64_t)p.w, ysz / 2));
            yuv_pic pic = { { nv, nv + ysz, nv + ysz + 1 }, { p.w, p.w, p.w }, p.w, p.h, 0, p.h >= 720, 2 };
            yuv_to_rgba(&pic, rgba, (size_t)p.w * 4, p.w, p.h, 0);
            write_png(png, rgba, p.w, p.h);
            printf("codec: frame %d (%ux%u, %.3f s) written to %s; %d frames decoded in %.2f s\n", taken, w, h, pts / 1e9, png, taken + 1, (now_ns() - t0) / 1e9);
        }
        h_cv_RelVp9(p.t, i, 0, 0);
        assert(!h_cv_AcqVp9(&q[qn], &qh[qn], p.t)); qn++;
        taken++;
    }
    player_close(&p); free(nv); free(rgba); free(data);
}

int main(int argc, char **argv) {
    synthetic();
    if (argc == 4) movie(argv[1], atoi(argv[2]), argv[3]);
    return 0;
}
