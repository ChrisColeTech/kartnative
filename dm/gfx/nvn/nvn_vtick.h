// Virtual display-link ticks (pure functions, shared by Mac / iOS; unit test tests/t_vtick.c).
// The display link runs at the screen's rate and only the refreshes nearest the slots of a steady clock at the game's rate count as ticks (NvMetalDisplayLinkDelegate,
// link_range in nvn_mtl.m). A counted tick that finds no frame waiting is a missed slot: the frame that missed it may then take one of the refreshes before the next slot,
// and the slots start again from that refresh, so a frame a little over one period goes on screen at the next refresh instead of at the next slot (25 ms instead of
// 33.3 ms at 60 fps on a 120 Hz screen), and the next frame still stays up at least one period.
#pragma once

// Whether the link counts virtual ticks: the screen is faster than the game's rate and either not a multiple of it (144 or 165 Hz against 60: asked for 60 Hz the system
// fires the link on every third refresh, 55 Hz) or the window is fullscreen (a ProMotion screen asked for 60 Hz there lets the window server lower the panel to 60 Hz on
// its own phase: every present went on screen one 120 Hz refresh after its target and the link called back 16.7 and 33.3 ms apart in turn, 40 Hz; docs/reference/frame-rate.md)
static inline int nvn_vtick_on(int rate, int hz, int fullscreen) { return rate > 0 && hz > rate && (hz % rate != 0 || fullscreen); }

typedef struct { double next, vp; int missed; } nvn_vtick;
enum { NVN_VT_SKIP, NVN_VT_TICK, NVN_VT_LATE };
// t: the refresh's target presentation time (s); vp: seconds per counted tick; half: half a refresh of the screen; late_ok: missed slots may be taken late;
// pending: a frame waits in the present queue and nothing holds it back. Returns NVN_VT_TICK for a counted tick, NVN_VT_LATE for a refresh between ticks that takes
// the frame which missed the last slot, NVN_VT_SKIP otherwise. A late refresh is the slot's tick from then on: the clock restarts from it, and the caller counts it as a tick
// (the game's acquire and vsync event return on it), or a game that starts its frame on a tick would keep missing the slot after it
static inline int nvn_vtick_step(nvn_vtick *v, double t, double vp, double half, int late_ok, int pending) {
    if (vp != v->vp || t > v->next + vp) { v->next = t; v->vp = vp; v->missed = 0; }   // the first refresh, a new rate, or after a stall: the clock starts again here
    if (t >= v->next - half) { v->next += vp; return NVN_VT_TICK; }   // a tick lies up to half a refresh off its slot
    if (late_ok && v->missed && pending) { v->next = t + vp; v->missed = 0; return NVN_VT_LATE; }
    return NVN_VT_SKIP;
}
// After a counted tick: whether it found a frame (or kept one on screen for its present interval). A tick without one is a missed slot
static inline void nvn_vtick_took(nvn_vtick *v, int took) { v->missed = !took; }
