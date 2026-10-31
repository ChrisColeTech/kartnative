// NVN2 (Switch 2) queues, submission and syncs: what differs from the NVN1 ones in hle_nvn.c, registered with NVN2 (nvn_impl.h) so only a Switch 2
// build (DM_GAME_SWITCH2) uses it. Facts: docs/reference/nvn2.md (QueueBuilder, Queue, Sync).
// The host runs every NVN queue's work on its one Metal queue in submission order (hle_nvn.c nvnQueueSubmitCommands), so the NVN2 queue kinds (builder
// flags 0x4 subcontext of a primary queue, 0x8 primary that allows subcontexts, 0x10 queue without a TSG) need no host state of their own: they only
// change how the real driver creates its GPU channel. Syncs stay Metal fence serials (sync_t).
#include "../../host/hle.h"
#include "nvn_impl.h"
#include "nvn2_core.h"

// Command handles: the host's are 8-byte aligned pointers to its first command record (hle_nvn.c nvnCommandBufferEndRecording), which is NVN2's tag 0
// (low bits 0: a CPU pointer to a stream the queue walks). Tags 1 (gather records) and 2 (one GPU command segment) are made only by the driver's
// EndRecording or by the game's inlined header code, which runs only when nvnPrivate0 is non-zero (docs/reference/nvn2.md#recording-nvncommandhandle-and-submission);
// the host never sets it, so such a handle means a game path the host does not know
int nvn2_handles_ok(int n, const NVNcommandHandle *h) {
    for (int i = 0; i < n; i++)
        if (h[i] & 3) {
            static int once; if (!once++) LOG("[RED] NVN2 command handle %#llx has tag %llu: not made by the host's EndRecording (tag 0); the submission is dropped", (unsigned long long)h[i], (unsigned long long)(h[i] & 3));
            return 0;
        }
    return 1;
}

// ---------- QueueBuilder (driver layout 0x48 bytes, sdk+0x5c2b60..0x5c2fb0) ----------
// The host keeps its own layout. dev stays first: hle_nvn.c nvnQueueInitialize reads it as queue_builder_t.dev. set: bit 0 command, 1 compute, 2 control,
// 3 ray-tracing memory size, 4 flush threshold given (the driver's "was set" bytes +0x24..+0x28; the getters return them)
typedef struct { NVNdevice *dev; void *mem; NVNqueue *primary; uint32_t flags; int32_t cmd, comp, ctl, rt, flush, mem_sz, prio; uint8_t set; } q2b_t;
_Static_assert(sizeof(q2b_t) <= 0x48, "the NVN2 queue builder is 0x48 bytes");
_Static_assert(offsetof(q2b_t, dev) == offsetof(queue_builder_t, dev), "nvnQueueInitialize reads the device of the builder");
#define Q2(b) ((q2b_t *)(b))
#define CQ2(b) ((const q2b_t *)(b))
// SetDefaults (sdk+0x5c2b70): everything but the device 0, priority 100 (NORMAL)
NVN2(nvnQueueBuilderSetDefaults, void, NVNqueueBuilder *b) { NVNdevice *d = Q2(b)->dev; memset(b, 0, sizeof(q2b_t)); Q2(b)->dev = d; Q2(b)->prio = 100; }
NVN2(nvnQueueBuilderSetDevice, void, NVNqueueBuilder *b, NVNdevice *d) { Q2(b)->dev = d; }
NVN2(nvnQueueBuilderSetFlags, void, NVNqueueBuilder *b, int f) { Q2(b)->flags = (uint32_t)f; }
// The sizes are stored as 32 bits (the driver stores w1)
NVN2(nvnQueueBuilderSetCommandMemorySize, void, NVNqueueBuilder *b, size_t n) { Q2(b)->cmd = (int32_t)n; Q2(b)->set |= 1; }
NVN2(nvnQueueBuilderSetComputeMemorySize, void, NVNqueueBuilder *b, size_t n) { Q2(b)->comp = (int32_t)n; Q2(b)->set |= 2; }
NVN2(nvnQueueBuilderSetControlMemorySize, void, NVNqueueBuilder *b, size_t n) { Q2(b)->ctl = (int32_t)n; Q2(b)->set |= 4; }
NVN2(nvnQueueBuilderSetRayTracingMemorySize, void, NVNqueueBuilder *b, size_t n) { Q2(b)->rt = (int32_t)n; Q2(b)->set |= 8; }
NVN2(nvnQueueBuilderSetCommandFlushThreshold, void, NVNqueueBuilder *b, size_t n) { Q2(b)->flush = (int32_t)n; Q2(b)->set |= 16; }
NVN2(nvnQueueBuilderSetQueueMemory, void, NVNqueueBuilder *b, void *m, size_t n) { Q2(b)->mem = m; Q2(b)->mem_sz = (int32_t)n; }
NVN2(nvnQueueBuilderSetQueuePriority, void, NVNqueueBuilder *b, int p) { Q2(b)->prio = p; }
// The primary queue of a subcontext queue (flag 0x4; MKW main+0x8f7104): the host has one GPU queue, nothing to tie
NVN2(nvnQueueBuilderSetPrimaryQueue, void, NVNqueueBuilder *b, NVNqueue *q) { Q2(b)->primary = q; }
// Queue memory (sdk+0x5c2e30): control (default 0x4000) + command (default 0x10000) memory, as the driver sizes it without its GPU context part. The host
// replays from the game's command buffers and uses none of it; the game only allocates this much
NVN2(nvnQueueBuilderGetQueueMemorySize, size_t, const NVNqueueBuilder *b) {
    size_t n = (size_t)(CQ2(b)->set & 4 ? CQ2(b)->ctl : 0x4000) + (size_t)(CQ2(b)->set & 1 ? CQ2(b)->cmd : 0x10000);
    return (n + 0xfff) & ~(size_t)0xfff;
}
NVN2(nvnQueueBuilderGetDevice, NVNdevice *, const NVNqueueBuilder *b) { return CQ2(b)->dev; }
NVN2(nvnQueueBuilderGetFlags, int, const NVNqueueBuilder *b) { return (int)CQ2(b)->flags; }
// (builder, int *size) -> whether the size was set (sdk+0x5c2ef0..0x5c2f30)
NVN2(nvnQueueBuilderGetCommandMemorySize, uint32_t, const NVNqueueBuilder *b, int *n) { *n = CQ2(b)->cmd; return CQ2(b)->set & 1; }
NVN2(nvnQueueBuilderGetComputeMemorySize, uint32_t, const NVNqueueBuilder *b, int *n) { *n = CQ2(b)->comp; return CQ2(b)->set >> 1 & 1; }
NVN2(nvnQueueBuilderGetControlMemorySize, uint32_t, const NVNqueueBuilder *b, int *n) { *n = CQ2(b)->ctl; return CQ2(b)->set >> 2 & 1; }
NVN2(nvnQueueBuilderGetRayTracingMemorySize, uint32_t, const NVNqueueBuilder *b, int *n) { *n = CQ2(b)->rt; return CQ2(b)->set >> 3 & 1; }
NVN2(nvnQueueBuilderGetCommandFlushThreshold, uint32_t, const NVNqueueBuilder *b, int *n) { *n = CQ2(b)->flush; return CQ2(b)->set >> 4 & 1; }
NVN2(nvnQueueBuilderGetMemorySize, int, const NVNqueueBuilder *b) { return CQ2(b)->mem_sz; }
NVN2(nvnQueueBuilderGetMemory, void *, const NVNqueueBuilder *b) { return CQ2(b)->mem; }
NVN2(nvnQueueBuilderGetQueuePriority, int, const NVNqueueBuilder *b) { return CQ2(b)->prio; }

// ---------- Queue ----------
// Memory usage counters (sdk+0x5c5d30..0x5c5dd0): the host's queues use no command, compute, control or ray-tracing memory of their own
NVN2(nvnQueueGetTotalCommandMemoryUsed, size_t, NVNqueue *q) { (void)q; return 0; }
NVN2(nvnQueueGetTotalComputeMemoryUsed, size_t, NVNqueue *q) { (void)q; return 0; }
NVN2(nvnQueueGetTotalControlMemoryUsed, size_t, NVNqueue *q) { (void)q; return 0; }
NVN2(nvnQueueGetTotalRayTracingMemoryUsed, size_t, NVNqueue *q) { (void)q; return 0; }
NVN2(nvnQueueResetMemoryUsageCounts, void, NVNqueue *q) { (void)q; }
// NVX GPU-channel scheduling (sdk+0x5c5e40 watchdog timeout, sdk+0x5c2f70 TSG timeslice): Metal schedules the host's one queue; nothing to set
NVN2(nvnQueueSetTimeoutNVX, void, NVNqueue *q, uint64_t ns) { (void)q; (void)ns; }
NVN2(nvnQueueSetTimesliceNVX, uint32_t, NVNqueue *q, uint32_t t) { (void)q; (void)t; return 1; }

// ---------- Sync ----------
// GL interop (sdk+0x5c8860, 0x5c8950): only through the interface nvnSetGLInteropNVX installs, which the host does not have: the driver's answers without it
NVN2(nvnSyncInitializeFromFencedGLSync, uint32_t, NVNsync *s, NVNdevice *d, uint64_t gl, int *err) { (void)s; (void)d; (void)gl; if (err) *err = 4; return 0; }
NVN2(nvnSyncCreateGLSync, int, const NVNsync *s, uint64_t *gl) { (void)s; (void)gl; return 4; }
// NVX NvRm syncpoints (sdk+0x5c7cc0..0x5c7e60): the host's syncs are Metal fence serials, not syncpoints, and there are no NvRm sync objects
static void no_syncpoints(const char *fn) { static int once; if (!once++) LOG("[RED] %s: the host's syncs are Metal fences, not NvRm syncpoints (answered false)", fn); }
NVN2(nvnSyncInitializeFromNvRmSyncNVX, uint32_t, NVNsync *s, NVNdevice *d, void *rm) { (void)s; (void)d; (void)rm; no_syncpoints("nvnSyncInitializeFromNvRmSyncNVX"); return 0; }
NVN2(nvnSyncGetNvRmSyncNVX, uint32_t, const NVNsync *s, void *rm) { (void)s; (void)rm; no_syncpoints("nvnSyncGetNvRmSyncNVX"); return 0; }
NVN2(nvnSyncGetSyncpointsCountNVX, uint32_t, const NVNsync *s, int *n) { (void)s; (void)n; no_syncpoints("nvnSyncGetSyncpointsCountNVX"); return 0; }
NVN2(nvnSyncGetSyncpointsNVX, uint32_t, const NVNsync *s, int n, uint32_t (*sp)[2]) { (void)s; (void)n; (void)sp; no_syncpoints("nvnSyncGetSyncpointsNVX"); return 0; }
NVN2(nvnSyncSetSyncpointsNVX, uint32_t, NVNsync *s, int n, const uint32_t (*sp)[2]) { (void)s; (void)n; (void)sp; no_syncpoints("nvnSyncSetSyncpointsNVX"); return 0; }
