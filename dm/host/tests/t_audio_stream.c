// hle_audio_stream.c unit test (silent sink): a Switch 2 output stream as Mario Kart World opens it (main+0xb9f35c: silence written ahead, then Start, then refills on the
// stream data event): free frames follow the real-time consumption, writes past the free frames are cut, Stop drops what is unplayed, the event fires while enough is free.
#include "../hle_audio.c"
#include "../hle_audio_stream.c"
#include <assert.h>
#include <time.h>
__thread _Atomic int dm_host_work;
uint64_t now_ns(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (uint64_t)t.tv_sec * 1000000000ull + (uint64_t)t.tv_nsec; }
static struct { const void *k; void *v; } om[16];
void *objmap_get(const void *k) { for (int i = 0; i < 16; i++) if (om[i].k == k) return om[i].v; return NULL; }
void objmap_put(const void *k, void *v) { for (int i = 0; i < 16; i++) if (!om[i].k) { om[i].k = k; om[i].v = v; return; } abort(); }
void *objmap_del(const void *k) { for (int i = 0; i < 16; i++) if (om[i].k == k) { om[i].k = 0; return om[i].v; } return NULL; }
static _Atomic int g_sig;
void hle_signal_system_event(void *g) { (void)g; }
void hle_bind_system_event(void *g, int autoclear) { (void)g; assert(autoclear == 1); }
uint64_t hle_system_event_binding(void *g) { (void)g; return 7; }
int hle_signal_system_event_binding(uint64_t b) { assert(b == 7); atomic_fetch_add(&g_sig, 1); return 1; }
module_t *module_of(const void *p) { (void)p; return NULL; }
int dm_guest_as_contains(uintptr_t a) { (void)a; return 0; }
void dm_host_helper_qos(qos_class_t q) { (void)q; }
static void sleep_ms(int ms) { struct timespec t = { 0, ms * 1000000L }; nanosleep(&t, NULL); }

int main(void) {
    char name[0x100]; uint8_t props[0x110];
    h_os_DefDev(name);
    assert(!strcmp(name, "DeviceOut") && !h_os_DevProps(props, name) && !strcmp((char *)props, "DeviceOut"));
    assert(h_os_DevProps(props, "Other") == 0x299);
    _Alignas(16) uint8_t t[0x350], ev[0x30]; static int16_t pcm[4800 * 2];
    stream_format f = { 2, 2, 44100 };
    assert(h_os_Open(t, ev, NULL, 0, name, &f, 4800, 4560) == 0x899);   // 48 kHz only
    f.rate = 48000;
    assert(h_os_State(t) == OS_CLOSED);
    assert(!h_os_OpenFl(t, ev, NULL, 0, name, &f, 4800, 4560, 0) && h_os_State(t) == OS_STOPPED && *(void **)t == t + 8);
    for (int i = 0; i < 4; i++) assert(h_os_Write(t, pcm, 960) == 960);   // silence ahead of Start, as the game does
    assert(h_os_Free(t) == 960);
    assert(h_os_Write(t, pcm, 2000) == 960 && h_os_Free(t) == 0);   // cut to the free frames
    sleep_ms(30); assert(h_os_Free(t) == 0);                           // nothing plays before Start
    uint64_t t0 = now_ns();
    assert(!h_os_Start(t) && h_os_State(t) == OS_STARTED && atomic_load(&g_sig) >= 1);
    // 20 ms blocks played in real time: some by now, and at most two blocks ahead of the clock (four of the five after 50 ms). On a busy machine (a CI runner) the
    // sleeps run long, so the bounds follow the time measured, not the time asked for
    int fr = 0; uint64_t ms = 0;
    for (int i = 0; i < 200 && !fr; i++) { sleep_ms(i ? 5 : 50); fr = h_os_Free(t); ms = (now_ns() - t0) / 1000000; }
    assert(fr >= 960 && fr <= 960 * (int)(ms / 20 + 2));
    for (int i = 0; i < 100 && atomic_load(&g_sig) < 2; i++) sleep_ms(10);   // the stream thread signals right after it takes the played blocks back
    int sig = atomic_load(&g_sig); assert(sig >= 2);
    for (int i = 0; i < 100 && h_os_Free(t) < 4800; i++) sleep_ms(10);
    assert(h_os_Free(t) == 4800);
    assert(h_os_Write(t, pcm, 4800) == 4800 && h_os_Free(t) == 0);
    h_os_Stop(t); assert(h_os_State(t) == OS_STOPPED && h_os_Free(t) == 4800);   // unplayed frames dropped
    h_os_Close(t); assert(h_os_State(t) == OS_CLOSED && !*(void **)t);
    printf("output stream: free frames follow playback (%d free after %llu ms), writes cut to the free frames, Stop drops the rest, %d data events\n", fr, (unsigned long long)ms, sig);
    return 0;
}
