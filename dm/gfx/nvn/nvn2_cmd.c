// NVN2 (Switch 2) command buffer entries: the ones that are new in NVN2 or whose ABI changed, registered with NVN2 (nvn_impl.h) so only a Switch 2 build
// (DM_GAME_SWITCH2) uses them. Records go through hle_nvn.c's allocator (nvn_cmd_rec) in the payloads of nvn2_cmd.h; the replay is nvn_mtl.m.
// Facts: docs/reference/nvn2.md#command-buffers (driver offsets sdk+..., Mario Kart World call sites main+...); what the host does with each: docs/reference/rendering.md, NVN2 command buffers.
#include "../../host/hle.h"
#include "nvn_impl.h"
#include "nvn_obj.h"
#include "nvn2_cmd.h"
#include <stdatomic.h>
#include <stdlib.h>

// The record allocator is hle_nvn.c's. This weak definition only lets the tests that link the NVN layer without hle_nvn.c (t_link_ticks, nvn_replay, ...) link;
// none of them records NVN2 commands
__attribute__((weak)) void *nvn_cmd_rec(NVNcommandBuffer *c, uint32_t op, size_t payload) {
    (void)c; (void)payload; LOG("[RED] NVN2 command record %u without hle_nvn.c", op); abort();
}
#define ONCE_LOG(...) do { static _Atomic int once_; if (!atomic_exchange(&once_, 1)) LOG(__VA_ARGS__); } while (0)

// ---------- Recording mode ----------
// SetRecordingMode (sdk+0x59f2c0, MKW: 17 calls with 0, 1 and 2): the engine a recording targets, 0 graphics, 1 compute, 2 copy, 3 / 4 as 0 / 1 but EndRecording may
// return a single-segment (tag 2) handle. The mode decides how the driver encodes the same commands (GPU methods or queue tokens) and which hardware engine runs them;
// within a command buffer their order is the recording's, and across engines only syncs and barriers order them. The host replays every recording in order on one
// Metal queue, an order at least as strict, and its handles stay record pointers (tag 0) in every mode, so there is nothing to record
NVN2(nvnCommandBufferSetRecordingMode, void, NVNcommandBuffer *c, int mode) {
    (void)c; if ((unsigned)mode > 4) ONCE_LOG("[RED] NVN2 SetRecordingMode %d: not a driver mode (0-4); recorded as graphics", mode);
}
// The host has no control memory (its records hold what the driver's control tokens hold), so it never runs out of any
NVN2(nvnCommandBufferSetControlMemoryCallbackEnabled, void, NVNcommandBuffer *c, uint32_t on) { (void)c; (void)on; }

// ---------- Push constants ----------
// PushGraphicsConstants (sdk+0x5d34b0) / PushComputeConstants (sdk+0x5d3a30): (cmd, slot, offset, size, data). Size in bytes: above 255 it is 256, below 4 nothing is pushed,
// and whole words are taken. Graphics keeps slot & 7 and offset & 0xff (root table selector, method 0x504); compute addresses words, offset / 4. The Deferred forms
// (sdk+0x5d3670 / sdk+0x5d3df0, tokens 0x1e / 0x1f) keep the pointer and the driver reads the bytes at submit. MKW: one call, (0, 0, 16, {1/W, 1/H, W, H}) at main+0x16d72e8
static void push(NVNcommandBuffer *c, int flags, int slot, int offset, int size, const void *data) {
    uint32_t n = (uint32_t)size > 0xff ? PUSH_BLOCK : (uint32_t)size;
    if (n < 4 || !data) return;
    n &= ~3u;
    uint32_t len = flags & PUSH_DEFERRED ? 8 : n;
    p_push_const *r = nvn_cmd_rec(c, OP_PUSH_CONST, offsetof(p_push_const, data) + len);
    r->flags = (uint8_t)flags; r->slot = (uint8_t)(slot & 7); r->offset = (uint16_t)(flags & PUSH_COMPUTE ? (offset & 0x3fc) : (offset & 0xff)); r->size = n;
    if (flags & PUSH_DEFERRED) { uint64_t p = (uint64_t)(uintptr_t)data; memcpy(r->data, &p, 8); } else memcpy(r->data, data, n);
}
NVN2(nvnCommandBufferPushGraphicsConstants, void, NVNcommandBuffer *c, int slot, int offset, int size, const void *data) { push(c, 0, slot, offset, size, data); }
NVN2(nvnCommandBufferPushComputeConstants, void, NVNcommandBuffer *c, int slot, int offset, int size, const void *data) { push(c, PUSH_COMPUTE, slot, offset, size, data); }
NVN2(nvnCommandBufferPushGraphicsConstantsDeferred, void, NVNcommandBuffer *c, int slot, int offset, int size, const void *data) { push(c, PUSH_DEFERRED, slot, offset, size, data); }
NVN2(nvnCommandBufferPushComputeConstantsDeferred, void, NVNcommandBuffer *c, int slot, int offset, int size, const void *data) { push(c, PUSH_COMPUTE | PUSH_DEFERRED, slot, offset, size, data); }

// ---------- Mesh-shader draws ----------
// Recorded with their arguments (OP_MESH); the replay has no mesh path yet and counts them (nvn_mtl.m). A direct draw's record stops before ind (mesh_rec), as a plain draw's does
static void mesh(NVNcommandBuffer *c, p_mesh m) {
    size_t n = m.ind || m.cnt ? sizeof m : offsetof(p_mesh, ind);
    memcpy(nvn_cmd_rec(c, OP_MESH, n), &m, n);
}
// DrawMeshTasks (sdk+0x5e2e80): count task workgroups from first (method 0xea2 {count, first}); MKW: 9 calls, first 0, count = items / 32 (main+0x2792b0)
NVN2(nvnCommandBufferDrawMeshTasks, void, NVNcommandBuffer *c, int first, int count) { mesh(c, (p_mesh){.kind = MESH_TASKS, .x = first, .y = count}); }
NVN2(nvnCommandBufferDrawMeshTasks3D, void, NVNcommandBuffer *c, int x, int y, int z) { mesh(c, (p_mesh){.kind = MESH_TASKS_3D, .x = x, .y = y, .z = z}); }
NVN2(nvnCommandBufferDrawMeshTasksIndirect, void, NVNcommandBuffer *c, uint64_t ind) { mesh(c, (p_mesh){.kind = MESH_INDIRECT, .ind = ind}); }
NVN2(nvnCommandBufferDrawMeshTasks3DIndirect, void, NVNcommandBuffer *c, uint64_t ind) { mesh(c, (p_mesh){.kind = MESH_3D_INDIRECT, .ind = ind}); }
NVN2(nvnCommandBufferMultiDrawMeshTasksIndirect, void, NVNcommandBuffer *c, uint64_t ind, int n, int stride) { mesh(c, (p_mesh){.kind = MESH_MULTI_INDIRECT, .ind = ind, .n = n, .stride = stride}); }
NVN2(nvnCommandBufferMultiDrawMeshTasks3DIndirect, void, NVNcommandBuffer *c, uint64_t ind, int n, int stride) { mesh(c, (p_mesh){.kind = MESH_3D_MULTI_INDIRECT, .ind = ind, .n = n, .stride = stride}); }
NVN2(nvnCommandBufferMultiDrawMeshTasksIndirectCount, void, NVNcommandBuffer *c, uint64_t ind, uint64_t cnt, int max, int stride) {
    mesh(c, (p_mesh){.kind = MESH_MULTI_INDIRECT_COUNT, .ind = ind, .cnt = cnt, .n = max, .stride = stride}); }
NVN2(nvnCommandBufferMultiDrawMeshTasks3DIndirectCount, void, NVNcommandBuffer *c, uint64_t ind, uint64_t cnt, int max, int stride) {
    mesh(c, (p_mesh){.kind = MESH_3D_MULTI_INDIRECT_COUNT, .ind = ind, .cnt = cnt, .n = max, .stride = stride}); }
// SetGeometryProcessingMode (sdk+0x5e2e10, method 0xe26): vertex pipeline (0) or mesh pipeline (1, 2; what tells them apart is not established). MKW sets 1 or 2 right before
// its DrawMeshTasks and 0 after (main+0x5d6308..0x5d647c, main+0x403d0c..0x403db0). The host takes the pipeline from the program a draw binds (a Metal mesh pipeline is a
// pipeline of its own), so the mode adds nothing to record
NVN2(nvnCommandBufferSetGeometryProcessingMode, void, NVNcommandBuffer *c, int mode) {
    (void)c; if ((unsigned)mode > 2) ONCE_LOG("[RED] NVN2 SetGeometryProcessingMode %d: not a mode MKW was seen to use (0-2)", mode);
}

// ---------- Hints without a Metal counterpart ----------
// SetStencilCullCriteria (sdk+0x5e9270, one method: (func - 1) | ref << 16 | mask << 24): the stencil test the zcull unit assumes when it culls tiles early. A cull
// criterion only lets the GPU skip work whose result the stencil test would discard anyway; Metal culls by itself, so dropping it changes no pixel. MKW: (3, 0xff, 0xff) at main+0x16d73f8
NVN2(nvnCommandBufferSetStencilCullCriteria, void, NVNcommandBuffer *c, int func, int ref, int mask) { (void)c; (void)func; (void)ref; (void)mask; }
// SetComputeOverlapMode (sdk+0x5e9780, token 0x27): whether consecutive dispatches may overlap (the queue writes mode == 0 to a compute method, only on queues that support it).
// It allows overlap, never asks for it: the replay orders each dispatch after everything before it in its encoder (nvn_mtl.m dispatch), the strictest mode. MKW: 3 calls
NVN2(nvnCommandBufferSetComputeOverlapMode, void, NVNcommandBuffer *c, int mode) { (void)c; (void)mode; }

// Variable-rate shading. ShadingRateImagePalette (sdk+0x5fe0a0): (cmd, table, count, rates): up to 16 4-bit rates for rate table 0-15, entries past count 5, rates NULL turns the
// table off. The values follow NVIDIA's shading-rate order (NV_shading_rate_image): 0 no fragment invocations, 1-4 16 / 8 / 4 / 2 per pixel, 5 one per pixel, 6-11 one per 1x2,
// 2x1, 2x2, 2x4, 4x2, 4x4 pixels. A primitive uses the table its vertex stage writes to a[0x5c] (Ampere SET_VARIABLE_PIXEL_RATE_SHADING_INDEX_TO_RATE_A/B(table), as Mesa's NVK
// fills them); BindShadingRateImage (sdk+0x5fe860) picks the entry per 16x16 tile, entry 0 without an image. MKW fills tables 0-4 (main+0x403480..0x4034f0) and binds a 160x90
// rate image for its lit scene passes (docs/reference/rendering.md, Variable-rate shading at the full rate, with the measured bound on what coarse shading would save).
// Metal has no per-primitive or per-tile shading rate: its rasterization rate maps lower the rasterization of a whole render pass along separable screen axes, which neither MKW's
// per-primitive tables nor its shared targets fit. The host shades every fragment at the full rate: the same picture at a higher cost, except for rate 0, which would draw nothing
NVN2(nvnCommandBufferShadingRateImagePalette, void, NVNcommandBuffer *c, int table, int n, const int *rates) {
    (void)c;
    if (!rates) return;
    for (int i = 0; i < n && i < 16; i++) {
        if (rates[i] == 5) continue;
        ONCE_LOG("[nvn] NVN2 variable-rate shading (table %d entry %d rate %d): shaded at the full rate, Metal has no per-primitive coarse shading", table, i, rates[i]);
        if (rates[i] == 0) ONCE_LOG("[RED] NVN2 shading-rate table entry with no fragment invocations (table %d entry %d): the host still shades those fragments", table, i);
    }
}
NVN2(nvnCommandBufferBindShadingRateImage, void, NVNcommandBuffer *c, const NVNtexture *t, const NVNtextureView *v) {
    (void)c; (void)v; if (t) ONCE_LOG("[nvn] NVN2 shading-rate image bound: shaded at the full rate, Metal has no per-tile coarse shading");
}
// SetShadingRatePaletteMode (sdk+0x5fe9a0, one method, data mode != 1; MKW passes 1): its meaning is not established, and it has none at the full rate
NVN2(nvnCommandBufferSetShadingRatePaletteMode, void, NVNcommandBuffer *c, int mode) { (void)c; (void)mode; }

// ---------- Debug groups and markers ----------
// The engine-forced forms (sdk+0x5e7db0 ... 0x5e8fb0): (cmd, domainId[, name]). The host keeps no debug groups (the NVN1 names are empty in hle_nvn.c g_nops, whose NVN2
// ABI, a domain ID before the string, they ignore as well)
NVN2(nvnCommandBufferPushDebugGroupGraphics, void, NVNcommandBuffer *c, uint32_t domain, const char *s) { (void)c; (void)domain; (void)s; }
NVN2(nvnCommandBufferPushDebugGroupCompute, void, NVNcommandBuffer *c, uint32_t domain, const char *s) { (void)c; (void)domain; (void)s; }
NVN2(nvnCommandBufferPopDebugGroupGraphics, void, NVNcommandBuffer *c, uint32_t domain) { (void)c; (void)domain; }
NVN2(nvnCommandBufferPopDebugGroupCompute, void, NVNcommandBuffer *c, uint32_t domain) { (void)c; (void)domain; }
NVN2(nvnCommandBufferInsertDebugMarkerGraphics, void, NVNcommandBuffer *c, uint32_t domain, const char *s) { (void)c; (void)domain; (void)s; }
NVN2(nvnCommandBufferInsertDebugMarkerCompute, void, NVNcommandBuffer *c, uint32_t domain, const char *s) { (void)c; (void)domain; (void)s; }
