// Switch 2 audio output streams and virtual output devices (nn::audio, SDK 19 for the Switch 2).
// The SDK's OutputStream (sdk+0x1eda80 OutputStreamImpl::Open) is a frame ring the game writes into, mixed by the system's sysmix and attached to a virtual device through
// devmgr (Start = AttachStream, Stop = DetachStream); its "stream data" SystemEvent wakes the writer (sysmix::AcquireStreamDataEvent with the threshold the game passes).
// The host has neither service: a stream is a host PCM output (hle_audio.c, the AudioOut machinery: sound card or silent sink) fed one block per write; its free frames are
// the capacity less the frames written and not yet played, and a host thread signals the event while at least capacity - threshold frames are free.
#include "hle.h"
#include <pthread.h>
#include <stdatomic.h>

void *dm_pcmout_open(int ch, const char *name);   // hle_audio.c
void dm_pcmout_close(void *k);
void dm_pcmout_start(void *k);
void dm_pcmout_stop(void *k);
int dm_pcmout_append(void *k, void *b);
void *dm_pcmout_released(void *k);
void dm_pcmout_wait(void *k, int ms);

#define OS_NAME "DeviceOut"     // the host's only device (the AudioOut name too)
#define OS_MAXBLOCKS 48          // blocks in flight per stream (the PCM output queue holds 64)
enum { OS_CLOSED = 0, OS_STOPPED = 1, OS_STARTED = 2 };   // OutputStreamImpl +0x110 (sdk+0x1ee318 GetState)
typedef struct { void *ptr; uint64_t cap, size; } os_block;   // laid out as the front of hle_audio.c's AudioOutBuffer (gbuf), which the PCM output queues
typedef struct { int32_t fmt, ch, rate; } stream_format; // nn::audio::StreamFormat (OutputStreamImpl keeps it at +0x114)
typedef struct {
    pthread_mutex_t mu; pthread_t th;
    void *out; void *ev; uint64_t ev_binding;
    int ch, cap, threshold, nblocks; _Atomic int state, pending, quit;   // pending: frames written and not yet played
} ostream;

static ostream *os_of(const void *t) { ostream *s = objmap_get(t); if (!s) die("audio: output stream %p was not opened by the host", t); return s; }
static int os_free(ostream *s) { int f = s->cap - atomic_load(&s->pending); return f > 0 ? f : 0; }
static void os_reclaim(ostream *s) {   // played blocks back from the output
    os_block *b;
    while ((b = dm_pcmout_released(s->out))) { atomic_fetch_sub(&s->pending, (int)(b->size / (uint64_t)(2 * s->ch))); pthread_mutex_lock(&s->mu); s->nblocks--; pthread_mutex_unlock(&s->mu); free(b->ptr); free(b); }
}
void dm_host_helper_qos(qos_class_t q);   // hle_thread.c
static void *os_thread(void *arg) {
    ostream *s = arg;
    pthread_setname_np("dm output stream");
    dm_host_helper_qos(QOS_CLASS_USER_INTERACTIVE);   // the writer refills on its wake-ups
    while (!atomic_load(&s->quit)) {
        dm_pcmout_wait(s->out, 10);
        os_reclaim(s);
        if (s->ev_binding && atomic_load(&s->state) == OS_STARTED && os_free(s) >= s->cap - s->threshold) hle_signal_system_event_binding(s->ev_binding);
    }
    return NULL;
}

HLE_FORCE("_ZN2nn5audio29GetDefaultVirtualOutputDeviceEPNS0_15AudioDeviceNameE", "default virtual output device: the host's only device (no devmgr)", void, h_os_DefDev, char *name) {
    memset(name, 0, 0x100); strcpy(name, OS_NAME);
}
// VirtualDeviceProperties (sdk+0x1ed640): the device name (0x100 bytes), then a u64 at +0x100 and a u32 at +0x108 from the device's profile, whose meaning is not known; the game reads neither
HLE_FORCE("_ZN2nn5audio32GetVirtualOutputDevicePropertiesEPNS0_23VirtualDevicePropertiesERKNS0_15AudioDeviceNameE", "virtual output device properties: the host's device (no devmgr)",
          Result, h_os_DevProps, uint8_t *p, const char *name) {
    if (strncmp(name, OS_NAME, 0x100)) { LOG("[RED] audio: properties of virtual output device \"%.32s\", which the host does not have", name); return 0x299; }   // the SDK's result for an unknown device
    memset(p, 0, 0x110); strcpy((char *)p, OS_NAME);
    return RES_OK;
}
static Result os_open(void *t, void *ev, const char *name, const stream_format *f, int cap, int threshold, uint32_t flags) {
    if (strncmp(name, OS_NAME, 0x100)) { LOG("[RED] audio: output stream on virtual device \"%.32s\", which the host does not have", name); return 0x299; }
    if (f->fmt != 2 || f->rate != 48000 || f->ch < 1 || f->ch > 6) { LOG("[RED] audio: output stream format %d, %d channels, %d Hz: the host plays PCM16 at 48 kHz", f->fmt, f->ch, f->rate); return 0x899; }
    if (objmap_get(t)) die("audio: output stream %p opened twice", t);
    ostream *s = calloc(1, sizeof *s); if (!s) die("audio: out of memory");
    pthread_mutex_init(&s->mu, NULL);
    s->ch = f->ch; s->cap = cap; s->threshold = threshold; atomic_store(&s->state, OS_STOPPED);
    s->out = dm_pcmout_open(f->ch, "OutputStream");
    if (ev) { s->ev = ev; hle_bind_system_event(ev, 1); s->ev_binding = hle_system_event_binding(ev); }
    if (pthread_create(&s->th, NULL, os_thread, s)) die("audio: cannot start an output stream thread");
    objmap_put(t, s);
    *(void **)t = (uint8_t *)t + 8;   // where the SDK keeps its implementation; the game tests it for "open"
    LOG("audio: output stream %p: %d channels, %d frames, event below %d filled frames%s (flags %#x)", t, f->ch, cap, threshold, ev ? "" : " (no event)", flags);
    return RES_OK;
}
HLE_FORCE("_ZN2nn5audio16OpenOutputStreamEPNS0_16OutputStreamTypeEPNS_2os11SystemEventEPvmRKNS0_15AudioDeviceNameERKNS0_12StreamFormatEii", "output stream: a host PCM output fed by the writes (no sysmix)",
          Result, h_os_Open, void *t, void *ev, void *buf, uint64_t n, const char *name, const stream_format *f, int cap, int threshold) { (void)buf; (void)n; return os_open(t, ev, name, f, cap, threshold, 0); }
HLE_FORCE("_ZN2nn5audio16OpenOutputStreamEPNS0_16OutputStreamTypeEPNS_2os11SystemEventEPvmRKNS0_15AudioDeviceNameERKNS0_12StreamFormatEiiNS_4util10BitFlagSetILi32ENS0_16OutputStreamFlagEEE",
          "output stream with flags: a host PCM output fed by the writes (no sysmix)",
          Result, h_os_OpenFl, void *t, void *ev, void *buf, uint64_t n, const char *name, const stream_format *f, int cap, int threshold, uint32_t flags) { (void)buf; (void)n; return os_open(t, ev, name, f, cap, threshold, flags); }
HLE_FORCE("_ZN2nn5audio17StartOutputStreamEPNS0_16OutputStreamTypeE", "output stream start", Result, h_os_Start, void *t) {
    ostream *s = os_of(t);
    if (atomic_exchange(&s->state, OS_STARTED) != OS_STARTED) { dm_pcmout_start(s->out); if (s->ev_binding) hle_signal_system_event_binding(s->ev_binding); }
    return RES_OK;
}
HLE_FORCE("_ZN2nn5audio16StopOutputStreamEPNS0_16OutputStreamTypeE", "output stream stop: unplayed frames are dropped", void, h_os_Stop, void *t) {
    ostream *s = os_of(t);
    if (atomic_exchange(&s->state, OS_STOPPED) == OS_STARTED) { dm_pcmout_stop(s->out); os_reclaim(s); }
}
HLE_FORCE("_ZN2nn5audio17CloseOutputStreamEPNS0_16OutputStreamTypeE", "output stream close", void, h_os_Close, void *t) {
    ostream *s = objmap_del(t); if (!s) return;
    atomic_store(&s->quit, 1); pthread_join(s->th, NULL);
    if (atomic_load(&s->state) == OS_STARTED) dm_pcmout_stop(s->out);
    os_reclaim(s);
    dm_pcmout_close(s->out);
    pthread_mutex_destroy(&s->mu); free(s);
    *(void **)t = NULL;
}
HLE_FORCE("_ZN2nn5audio20GetOutputStreamStateEPKNS0_16OutputStreamTypeE", "output stream state (1 stopped, 2 started)", int, h_os_State, const void *t) {
    ostream *s = objmap_get(t); return s ? atomic_load(&s->state) : OS_CLOSED;
}
HLE_FORCE("_ZN2nn5audio29GetOutputStreamFreeFrameCountEPKNS0_16OutputStreamTypeE", "output stream: capacity less the frames not yet played", int, h_os_Free, const void *t) {
    ostream *s = os_of(t); os_reclaim(s); return os_free(s);
}
HLE_FORCE("_ZN2nn5audio17WriteOutputStreamEPNS0_16OutputStreamTypeEPKvi", "output stream write: one block for the host PCM output", int, h_os_Write, void *t, const void *data, int frames) {
    ostream *s = os_of(t);
    os_reclaim(s);
    int n = frames < os_free(s) ? frames : os_free(s);
    pthread_mutex_lock(&s->mu); int room = s->nblocks < OS_MAXBLOCKS; if (room && n > 0) s->nblocks++; pthread_mutex_unlock(&s->mu);
    if (n <= 0 || !room) return 0;
    size_t bytes = (size_t)n * 2 * (size_t)s->ch;
    os_block *b = malloc(sizeof *b); void *p = malloc(bytes); if (!b || !p) die("audio: out of memory");
    memcpy(p, data, bytes); *b = (os_block){ p, bytes, bytes };
    atomic_fetch_add(&s->pending, n);
    if (!dm_pcmout_append(s->out, b)) { atomic_fetch_sub(&s->pending, n); pthread_mutex_lock(&s->mu); s->nblocks--; pthread_mutex_unlock(&s->mu); free(p); free(b); return 0; }
    return n;
}
