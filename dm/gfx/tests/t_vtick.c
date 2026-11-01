// Virtual display-link ticks (nvn_vtick.h): which screens count them, and a simulated link that calls back on every refresh of the screen and presents queued frames the way
// NvMetalDisplayLinkDelegate does (a counted tick takes the oldest waiting frame or is a missed slot; a refresh between ticks may take the frame that missed it, and is then
// the slot's tick: the game's acquire returns on it).
#include "../nvn/nvn_vtick.h"
#include <math.h>
#include <stdio.h>

static int checks, failed;
#define CHECK(c, ...) do { checks++; if (!(c)) { failed++; printf("FAIL: "); printf(__VA_ARGS__); printf("\n"); } } while (0)

typedef struct { int presents, ticks, late, n_iv; double iv_min, iv_max, iv_sum, at[4096]; int hist[8]; } run_t;   // hist: on-screen intervals in refreshes (0..7)

// hz: the screen; rate: the game's; tick_driven 1: the game paces itself at its acquire as on the host (mtl_link_wait_ticks): after presenting a frame it starts the next at
// once if a tick came since that frame started, else at the next tick; each frame is ready work_ms after it starts, and one frame already waits when the link starts (a running
// game); 2: the same from a cold start (the first tick finds nothing); 0: frames are ready every work_ms from the start, whatever the ticks.
// Simulates secs seconds of refreshes; the callback's time is the refresh's target time.
static run_t simulate(int hz, int rate, int late_ok, int tick_driven, double work_ms, double secs) {
    run_t r = {.iv_min = 1e9};
    nvn_vtick v = {0}; double vp = 1.0 / rate, half = 0.5 / hz, w = work_ms / 1000, ready[8192]; int nready = 0, head = 0;
    double last = -1, next_free = 0;   // next_free: free-running producer's next frame time
    double cur_ready = 0; int waiting = 1, ticked = 0;   // acquire-paced game: its frame in progress ends at cur_ready; waiting: presented, waiting for a tick to start the next
    if (tick_driven == 1) ready[nready++] = 0;
    for (long k = 1; k <= (long)(secs * hz); k++) {
        double t = (double)k / hz;
        if (!tick_driven) while (next_free <= t && nready < 8192) { ready[nready++] = next_free; next_free += w; }
        while (tick_driven && !waiting && cur_ready <= t && nready < 8192) {   // the frame is presented; the next starts at once if a tick came since this one started
            ready[nready++] = cur_ready;
            if (ticked) { ticked = 0; cur_ready += w; } else waiting = 1;
        }
        int queued = head < nready && ready[head] <= t;
        int kind = nvn_vtick_step(&v, t, vp, half, late_ok, v.missed && queued);
        if (kind == NVN_VT_SKIP) continue;
        r.ticks++;   // the game's acquire returns on the tick, a late frame's included
        if (tick_driven) { if (waiting) { waiting = 0; ticked = 0; cur_ready = t + w; } else ticked = 1; }
        if (!queued) { nvn_vtick_took(&v, 0); continue; }
        nvn_vtick_took(&v, 1);
        head++; r.late += kind == NVN_VT_LATE;
        if (r.presents < 4096) r.at[r.presents] = t;
        r.presents++;
        if (last >= 0) { double iv = (t - last) * 1000; r.n_iv++; r.iv_sum += iv; if (iv < r.iv_min) r.iv_min = iv; if (iv > r.iv_max) r.iv_max = iv;
            int n = (int)lround((t - last) * hz); r.hist[n < 7 ? n : 7]++; }
        last = t;
    }
    return r;
}

int main(void) {
    // Which screens count virtual ticks
    CHECK(!nvn_vtick_on(60, 120, 0), "120 Hz screen in a window: every callback of a 60 Hz link is a tick, as before");
    CHECK(nvn_vtick_on(60, 120, 1), "120 Hz screen in fullscreen: link at 120 Hz, a tick every 1/60 s");
    CHECK(nvn_vtick_on(30, 120, 1), "120 Hz screen in fullscreen at the 30 fps target");
    CHECK(!nvn_vtick_on(60, 60, 1), "60 Hz screen in fullscreen: nothing faster to run at");
    CHECK(!nvn_vtick_on(60, 0, 1), "screen rate unknown");
    CHECK(nvn_vtick_on(60, 144, 0) && nvn_vtick_on(60, 165, 0), "144 and 165 Hz screens in a window, as before");
    CHECK(nvn_vtick_on(60, 144, 1), "144 Hz screen in fullscreen (front_screen_match switches it to 120 where it can)");

    // A tick-driven game whose frame is ready 5.7 ms after the tick (Mario Kart World's race in a window): every frame stays up exactly one period on a 120 Hz screen
    run_t a = simulate(120, 60, 1, 1, 5.7, 10);
    CHECK(a.ticks == 600 && a.presents >= 599, "120 Hz, frames in time: 60 ticks and 60 presents a second (ticks %d, presents %d)", a.ticks, a.presents);
    CHECK(a.hist[2] == a.n_iv && a.late == 0, "120 Hz, frames in time: every interval 2 refreshes, none late (%d of %d, late %d)", a.hist[2], a.n_iv, a.late);
    run_t a0 = simulate(120, 60, 0, 1, 5.7, 10);
    CHECK(a0.hist[2] == a0.n_iv, "the same without late frames");
    // From a cold start the first tick finds nothing: its frame goes up one refresh late, and that refresh is the slot from then on (one late frame, then every interval 2 refreshes)
    run_t a2 = simulate(120, 60, 1, 2, 5.7, 10);
    CHECK(a2.late == 1 && a2.hist[2] == a2.n_iv && a2.presents >= 599, "cold start: one late frame, then one per slot (late %d, 2-refresh %d of %d, presents %d)", a2.late, a2.hist[2], a2.n_iv, a2.presents);

    // A free-running game at 17.5 ms a frame (57 fps): a frame that misses its slot goes up at the next refresh (25 ms) instead of the next slot (33.3 ms)
    run_t b = simulate(120, 60, 1, 0, 17.5, 10);
    CHECK(b.hist[4] == 0, "17.5 ms frames, late frames on: no frame waits 4 refreshes (33.3 ms), %d did", b.hist[4]);
    CHECK(b.iv_min > 16.6, "17.5 ms frames: never two presents within one period (shortest %.2f ms)", b.iv_min);
    CHECK(b.late > 0 && b.hist[3] == b.late, "17.5 ms frames: each late frame 3 refreshes after the one before (late %d, 3-refresh intervals %d)", b.late, b.hist[3]);
    double fps_b = b.presents / 10.0;
    CHECK(fps_b > 53, "17.5 ms frames: %.1f presents a second with late frames", fps_b);
    run_t b0 = simulate(120, 60, 0, 0, 17.5, 10);
    CHECK(b0.hist[4] > 0 && b0.hist[3] == 0 && b0.late == 0, "17.5 ms frames, DM_LINK_LATE=0: missed slots wait for the next slot (4-refresh intervals %d)", b0.hist[4]);
    CHECK(fps_b > b0.presents / 10.0, "late frames show more frames: %.1f against %.1f a second", fps_b, b0.presents / 10.0);

    // The game at its rate or faster than the screen keeps one frame per slot, no late frames
    run_t c = simulate(120, 60, 1, 0, 16.0, 10);
    CHECK(c.late == 0 && c.hist[2] == c.n_iv, "16 ms frames (a queue builds up): one per slot, no late (late %d)", c.late);
    // A 22 ms game: late frames keep it at 25 ms (no 33.3)
    run_t d = simulate(120, 60, 1, 0, 22.0, 10);
    CHECK(d.hist[4] == 0 && d.iv_min > 16.6, "22 ms frames: 25 ms on screen, never 33.3 (4-refresh intervals %d)", d.hist[4]);
    // A game paced at its acquire whose frame takes 17.5 ms: it goes on at once when a tick came during its frame, so it runs at 57 fps, and without late frames each slot it
    // misses (about one in 20) leaves the frame before up for 33.3 ms; with them the frame that missed goes up at the next refresh (25 ms)
    run_t g = simulate(120, 60, 1, 1, 17.5, 10), g0 = simulate(120, 60, 0, 1, 17.5, 10);
    CHECK(g0.hist[4] > 0 && g0.hist[3] == 0, "acquire-paced 17.5 ms frames, DM_LINK_LATE=0: missed slots leave frames up 33.3 ms (%d of %d, %d presents)", g0.hist[4], g0.n_iv, g0.presents);
    CHECK(g.hist[4] == 0 && g.iv_min > 16.6 && g.presents >= g0.presents, "acquire-paced 17.5 ms frames: never 33.3 ms (%d presents, 3-refresh %d, 2-refresh %d)", g.presents, g.hist[3], g.hist[2]);
    printf("17.5 ms frames on a 120 Hz screen, on-screen intervals 16.7 / 25 / 33.3 ms: free-running %d / %d / %d (DM_LINK_LATE=0: %d / %d / %d), acquire-paced %d / %d / %d (%d / %d / %d)\n",
           b.hist[2], b.hist[3], b.hist[4], b0.hist[2], b0.hist[3], b0.hist[4], g.hist[2], g.hist[3], g.hist[4], g0.hist[2], g0.hist[3], g0.hist[4]);

    // 144 Hz screen in a window (virtual ticks before this change): the average stays 60 a second, frames up 2 or 3 refreshes, unchanged for frames in time
    run_t e = simulate(144, 60, 1, 1, 5.7, 10);
    CHECK(e.ticks == 600, "144 Hz: 60 ticks a second (%d in 10 s)", e.ticks);
    CHECK(e.hist[2] + e.hist[3] == e.n_iv && e.late == 0, "144 Hz: every frame up 2 or 3 refreshes, none late");

    // 30 fps target on a 120 Hz screen in fullscreen: a tick every fourth refresh
    run_t f = simulate(120, 30, 1, 1, 5.7, 10);
    CHECK(f.ticks == 300 && f.hist[4] == f.n_iv, "30 fps target at 120 Hz: every interval 4 refreshes (%d of %d)", f.hist[4], f.n_iv);

    // A stall (no callbacks for 200 ms, the window covered): the clock starts again at the first refresh after it, with no burst of ticks
    {
        nvn_vtick v = {0}; double vp = 1.0 / 60, half = 0.5 / 120; int ticks = 0;
        for (int k = 1; k <= 24; k++) ticks += nvn_vtick_step(&v, k / 120.0, vp, half, 1, 0) == NVN_VT_TICK;
        int after = 0; for (int k = 48; k <= 72; k++) after += nvn_vtick_step(&v, k / 120.0, vp, half, 1, 0) == NVN_VT_TICK;
        CHECK(ticks == 12 && after == 13, "stall: 12 ticks before, 13 in the 25 refreshes after it (%d, %d)", ticks, after);
        // a missed slot before a stall is forgotten: no late take after it
        nvn_vtick_took(&v, 0); int k2 = nvn_vtick_step(&v, 200 / 120.0, vp, half, 1, 1);
        CHECK(k2 == NVN_VT_TICK && !v.missed, "after a stall the first refresh is a tick, the earlier miss forgotten");
    }
    // A rate change restarts the clock
    {
        nvn_vtick v = {0}; double half = 0.5 / 120;
        nvn_vtick_step(&v, 1 / 120.0, 1.0 / 60, half, 1, 0); nvn_vtick_took(&v, 0);
        CHECK(nvn_vtick_step(&v, 2 / 120.0, 1.0 / 30, half, 1, 1) == NVN_VT_TICK, "a new rate: the next refresh is a tick, not a late frame");
    }
    printf("== %d checks, %d failed ==\n", checks, failed);
    return failed != 0;
}
