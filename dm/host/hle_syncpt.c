// Switch 2 sync counters and nn::syncpt::sdk events.
// On the console a nn::gs::NativeSyncHandle {u32 ~syncpoint, u32 mode, u64 value} names a GPU syncpoint and the value it must reach, and nn::syncpt::sdk's events are slots of the
// nx2:syncpt service: an event in slot k is a manual-clear SystemEvent the service signals once the syncpoint passes the requested value (sdk+0x528ea8 RequestSyncPointEvent: the
// server clears the slot event and arms it; sdk+0x528fd4 CancelSyncPointEvent: the server always signals it and the client then waits on it; the client keeps the slot number in
// SyncPointEventType, and TryWait / InitializeMultiWaitHolder use the slot's SystemEvent). The host has no syncpoints: the producers of handles are host code (the video decoders,
// hle_codec_video.c), so the counters live here and the events are armed against them. An all-zero handle counts as reached (the SDK skips the request for one).
#include "hle.h"
#include <pthread.h>

#define SP_COUNTERS 64
#define SP_SLOTS 64   // the SDK's slot table holds 64 events (0xc58 bytes: 64 SystemEventType of 0x30 from +0x58)
static pthread_mutex_t g_sp_mu = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t g_sp_cv = PTHREAD_COND_INITIALIZER;
static struct { uint64_t value; int used; } g_ctr[SP_COUNTERS];
static struct {
    _Alignas(16) uint8_t ev[0x40];   // the slot's nn::os::SystemEventType (0x30 bytes), guest code waits on it
    int used, bound, armed;
    uint64_t binding;
    dm_sync_handle want;
} g_slot[SP_SLOTS];

static int sp_reached_locked(const dm_sync_handle *h) {
    if (!h->id_inv && !h->mode && !h->value) return 1;
    uint32_t id = ~h->id_inv;
    if (id >= SP_COUNTERS) { static int red; if (!red++) LOG("[RED] syncpt: handle names counter %#x, which the host never handed out: taken as reached", id); return 1; }
    uint64_t v = g_ctr[id].value;
    return h->mode == 1 ? v >= h->value : (int32_t)((uint32_t)v - (uint32_t)h->value) >= 0;
}
uint32_t dm_syncpt_alloc(void) {
    pthread_mutex_lock(&g_sp_mu);
    for (uint32_t i = 0; i < SP_COUNTERS; i++) if (!g_ctr[i].used) { g_ctr[i].used = 1; pthread_mutex_unlock(&g_sp_mu); return i; }
    pthread_mutex_unlock(&g_sp_mu);
    die("syncpt: all %d host sync counters are in use", SP_COUNTERS);
}
void dm_syncpt_free(uint32_t id) { pthread_mutex_lock(&g_sp_mu); if (id < SP_COUNTERS) g_ctr[id].used = 0; pthread_mutex_unlock(&g_sp_mu); }
uint64_t dm_syncpt_value(uint32_t id) { pthread_mutex_lock(&g_sp_mu); uint64_t v = id < SP_COUNTERS ? g_ctr[id].value : 0; pthread_mutex_unlock(&g_sp_mu); return v; }
dm_sync_handle dm_syncpt_handle(uint32_t id, uint64_t value) { return (dm_sync_handle){ ~id, 1, value }; }
int dm_syncpt_reached(const dm_sync_handle *h) { pthread_mutex_lock(&g_sp_mu); int r = sp_reached_locked(h); pthread_mutex_unlock(&g_sp_mu); return r; }
void dm_syncpt_incr(uint32_t id) {
    uint64_t fire[SP_SLOTS]; int n = 0;
    pthread_mutex_lock(&g_sp_mu);
    if (id < SP_COUNTERS) g_ctr[id].value++;
    for (int i = 0; i < SP_SLOTS; i++) if (g_slot[i].armed && sp_reached_locked(&g_slot[i].want)) { g_slot[i].armed = 0; fire[n++] = g_slot[i].binding; }
    pthread_cond_broadcast(&g_sp_cv);
    pthread_mutex_unlock(&g_sp_mu);
    for (int i = 0; i < n; i++) hle_signal_system_event_binding(fire[i]);   // outside the lock: signalling takes the kernel lock
}
int dm_syncpt_wait(const dm_sync_handle *h, uint64_t timeout_ns) {
    uint64_t end = now_ns() + timeout_ns;
    pthread_mutex_lock(&g_sp_mu);
    int r;
    while (!(r = sp_reached_locked(h))) {
        uint64_t t = now_ns(); if (t >= end) break;
        struct timespec ts; clock_gettime(CLOCK_REALTIME, &ts);
        uint64_t ns = (uint64_t)ts.tv_nsec + (end - t); ts.tv_sec += (time_t)(ns / 1000000000ull); ts.tv_nsec = (long)(ns % 1000000000ull);
        pthread_cond_timedwait(&g_sp_cv, &g_sp_mu, &ts);
    }
    pthread_mutex_unlock(&g_sp_mu);
    return r;
}

// ---------- nn::syncpt::sdk events (SyncPointEventType holds the u32 slot, as in the SDK) ----------
static uint32_t sp_slot(const void *t) { uint32_t s = *(const uint32_t *)t; if (s >= SP_SLOTS || !g_slot[s].used) die("syncpt: event %p holds slot %u, which is not in use", t, s); return s; }
HLE_FORCE("_ZN2nn6syncpt3sdk20CreateSyncPointEventEPNS1_18SyncPointEventTypeE", "sync point event: a host slot whose SystemEvent the host counters signal (no nx2:syncpt service)",
          Result, h_sp_Create, uint32_t *t) {
    pthread_mutex_lock(&g_sp_mu);
    int s = -1; for (int i = 0; i < SP_SLOTS; i++) if (!g_slot[i].used) { s = i; break; }
    if (s >= 0) { g_slot[s].used = 1; g_slot[s].armed = 0; }
    int bound = s >= 0 && g_slot[s].bound;
    pthread_mutex_unlock(&g_sp_mu);
    if (s < 0) { LOG("[RED] syncpt: all %d event slots are in use", SP_SLOTS); *t = 0; return 0x80f3; }   // the SDK's result when no slot is free (sdk+0x528d70)
    if (!bound) {   // the slot's SystemEvent is made once, on this guest thread, and kept (the SDK keeps a slot's event too); manual clear like the SDK's (sdk+0x528600)
        hle_bind_system_event(g_slot[s].ev, 0);
        pthread_mutex_lock(&g_sp_mu); g_slot[s].bound = 1; g_slot[s].binding = hle_system_event_binding(g_slot[s].ev); pthread_mutex_unlock(&g_sp_mu);
    }
    hle_clear_system_event(g_slot[s].ev);   // a new event starts unsignalled, whatever the slot's last owner left
    *t = (uint32_t)s;
    return RES_OK;
}
HLE_FORCE("_ZN2nn6syncpt3sdk21DestroySyncPointEventEPNS1_18SyncPointEventTypeE", "sync point event: frees the host slot", void, h_sp_Destroy, uint32_t *t) {
    uint32_t s = sp_slot(t);
    pthread_mutex_lock(&g_sp_mu); g_slot[s].armed = 0; g_slot[s].used = 0; pthread_mutex_unlock(&g_sp_mu);
    hle_clear_system_event(g_slot[s].ev);
}
HLE_FORCE("_ZN2nn6syncpt3sdk21RequestSyncPointEventEPNS1_18SyncPointEventTypeERKNS1_15SyncPointHandleE", "sync point event: clear, then signal once the host counter reaches the handle's value",
          void, h_sp_Request, uint32_t *t, const dm_sync_handle *h) {
    uint32_t s = sp_slot(t);
    if (!h->id_inv && !h->mode && !h->value) return;   // the SDK sends nothing for an all-zero handle
    hle_clear_system_event(g_slot[s].ev);
    pthread_mutex_lock(&g_sp_mu);
    int now = sp_reached_locked(h);
    g_slot[s].want = *h; g_slot[s].armed = !now;
    pthread_mutex_unlock(&g_sp_mu);
    if (now) hle_signal_system_event(g_slot[s].ev);
}
HLE_FORCE("_ZN2nn6syncpt3sdk20CancelSyncPointEventEPNS1_18SyncPointEventTypeE", "sync point event: disarm and signal, as the service does", void, h_sp_Cancel, uint32_t *t) {
    uint32_t s = sp_slot(t);
    pthread_mutex_lock(&g_sp_mu); g_slot[s].armed = 0; pthread_mutex_unlock(&g_sp_mu);
    hle_signal_system_event(g_slot[s].ev);
}
HLE_FORCE("_ZN2nn6syncpt3sdk21TryWaitSyncPointEventEPKNS1_18SyncPointEventTypeE", "sync point event: the slot's SystemEvent state", int, h_sp_TryWait, const uint32_t *t) {
    static int (*trywait)(void *);
    if (!trywait && !(trywait = guest_sym("_ZN2nn2os18TryWaitSystemEventEPNS0_15SystemEventTypeE"))) die("syncpt: missing SDK symbol nn::os::TryWaitSystemEvent");
    return trywait(g_slot[sp_slot(t)].ev) & 1;
}
HLE_FORCE("_ZN2nn6syncpt3sdk25InitializeMultiWaitHolderEPNS_2os19MultiWaitHolderTypeEPNS1_18SyncPointEventTypeE", "sync point event: a multi-wait holder on the slot's SystemEvent",
          void, h_sp_Holder, void *holder, uint32_t *t) {
    static void (*init)(void *, void *);
    if (!init && !(init = guest_sym("_ZN2nn2os25InitializeMultiWaitHolderEPNS0_19MultiWaitHolderTypeEPNS0_15SystemEventTypeE"))) die("syncpt: missing SDK symbol nn::os::InitializeMultiWaitHolder");
    init(holder, g_slot[sp_slot(t)].ev);
}
