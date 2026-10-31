// NVN2 (Switch 2) programs: the program builder and nvnProgramInitializeFromBuilder (new in NVN2), and the driver's checks of NVN2 control sections in front
// of the NVN1 program in hle_nvn.c, which keys each stage (nvn_shader_key.h) for the replay layer (nvn_mtl.m prog_get: shader map stage, library <key>_b).
// Registered with NVN2 (nvn_impl.h): only a Switch 2 build (DM_GAME_SWITCH2) uses this file. Facts: docs/reference/nvn2.md#program-and-programbuilder
// (sdk+0x... offsets of Mario Kart World 1.0.0's NVN2 driver).
// The NVN1 implementations are taken from the registration section (__DATA,swnvn) rather than linked: the graphics unit tests link this file without hle_nvn.c.
#include "../../host/hle.h"
#include "nvn_impl.h"
#include "include/nvn2.h"
#include <mach-o/getsect.h>
#include <mach-o/ldsyms.h>
#include <pthread.h>

// The NVN1 implementation registered for name (NULL when this executable has none)
static void *nvn1_impl(const char *name) {
    unsigned long sz;
    const nvn_impl *im = (const nvn_impl *)getsectiondata(&_mh_execute_header, "__DATA", "swnvn", &sz);
    for (unsigned long k = 0; im && k < sz / sizeof *im; k++) if (!strcmp(im[k].name, name)) return im[k].fn;
    return NULL;
}
static NVNT_nvnProgramInitialize nvn1_init(void) { static NVNT_nvnProgramInitialize f; if (!f) f = (NVNT_nvnProgramInitialize)nvn1_impl("nvnProgramInitialize"); return f; }
static NVNT_nvnProgramSetShaders nvn1_set(void) { static NVNT_nvnProgramSetShaders f; if (!f) f = (NVNT_nvnProgramSetShaders)nvn1_impl("nvnProgramSetShaders"); return f; }

// ---------- Program builder (0x80 bytes; the driver's layout, so its getters read back what the game set) ----------
typedef struct {
    uint64_t pad0; NVNdevice *dev; uint64_t pad10;
    int32_t sample_shading, nhash; const NVN2debugDataHash *hash;
    int32_t nshader, pad2c; const NVNshaderData *shader;
    int32_t nrt, pad3c; const NVNshaderData *rt;
    int32_t nlink, pad4c; void *const *link;
    int32_t max_trace, max_callable; uint32_t rt_flags;
} prog_builder2_t;
_Static_assert(sizeof(prog_builder2_t) <= sizeof(NVN2programBuilder), "program builder");
_Static_assert(__builtin_offsetof(prog_builder2_t, shader) == 0x30 && __builtin_offsetof(prog_builder2_t, rt_flags) == 0x60, "program builder layout");
#define PB(b) ((prog_builder2_t *)(b))
#define CPB(b) ((const prog_builder2_t *)(b))

// SetDefaults (sdk+0x5c0920) clears +0x18..+0x63 and keeps the device; both recursion depths default to 2
NVN2(nvnProgramBuilderSetDefaults, void, NVN2programBuilder *b) {
    NVNdevice *d = PB(b)->dev; memset(b, 0, sizeof(prog_builder2_t)); PB(b)->dev = d; PB(b)->max_trace = PB(b)->max_callable = 2;
}
NVN2(nvnProgramBuilderSetDevice, void, NVN2programBuilder *b, NVNdevice *d) { PB(b)->dev = d; }
NVN2(nvnProgramBuilderGetDevice, NVNdevice *, const NVN2programBuilder *b) { return CPB(b)->dev; }
NVN2(nvnProgramBuilderSetSampleShading, void, NVN2programBuilder *b, int mode) { PB(b)->sample_shading = mode; }
NVN2(nvnProgramBuilderGetSampleShading, int, const NVN2programBuilder *b) { return CPB(b)->sample_shading; }
NVN2(nvnProgramBuilderSetDebugDataHashes, void, NVN2programBuilder *b, int n, const NVN2debugDataHash *h) { PB(b)->nhash = n; PB(b)->hash = h; }
NVN2(nvnProgramBuilderGetDebugDataHashes, const NVN2debugDataHash *, const NVN2programBuilder *b) { return CPB(b)->hash; }
// The shader records are not copied (the driver keeps the pointer too): they must live until InitializeFromBuilder
NVN2(nvnProgramBuilderSetShaders, void, NVN2programBuilder *b, int n, const NVNshaderData *sd) { PB(b)->nshader = n; PB(b)->shader = sd; }
NVN2(nvnProgramBuilderGetShaders, const NVNshaderData *, const NVN2programBuilder *b) { return CPB(b)->shader; }
NVN2(nvnProgramBuilderGetShaderCount, int, const NVN2programBuilder *b) { return CPB(b)->nshader; }
NVN2(nvnProgramBuilderSetRayTracingModules, void, NVN2programBuilder *b, int n, const NVNshaderData *m) { PB(b)->nrt = n; PB(b)->rt = m; }
NVN2(nvnProgramBuilderSetSubroutineLinkage, void, NVN2programBuilder *b, int n, void *const *maps) { PB(b)->nlink = n; PB(b)->link = maps; }
NVN2(nvnProgramBuilderGetSubroutineLinkage, void *const *, const NVN2programBuilder *b) { return CPB(b)->link; }
NVN2(nvnProgramBuilderGetSubroutineLinkageCount, int, const NVN2programBuilder *b) { return CPB(b)->nlink; }
NVN2(nvnProgramBuilderSetMaxTraceRecursionDepth, void, NVN2programBuilder *b, int v) { PB(b)->max_trace = v; }
NVN2(nvnProgramBuilderGetMaxTraceRecursionDepth, int, const NVN2programBuilder *b) { return CPB(b)->max_trace; }
NVN2(nvnProgramBuilderSetMaxCallableRecursionDepth, void, NVN2programBuilder *b, int v) { PB(b)->max_callable = v; }
NVN2(nvnProgramBuilderGetMaxCallableRecursionDepth, int, const NVN2programBuilder *b) { return CPB(b)->max_callable; }
NVN2(nvnProgramBuilderSetProgramRayTracingFlags, void, NVN2programBuilder *b, uint32_t f) { PB(b)->rt_flags = f; }
NVN2(nvnProgramBuilderGetProgramRayTracingFlags, uint32_t, const NVN2programBuilder *b) { return CPB(b)->rt_flags; }

// ---------- The SetShaders core (sdk+0x5bf8b0): what makes the driver refuse a program ----------
static const char *const k_stage[9] = {"vertex", "fragment", "geometry", "tess control", "tess evaluation", "compute", "task", "mesh", "ray tracing"};

// A refused program answers false, as the driver's does; the game decides what follows (MKW compares the result with 1, main+0x1675d38)
static int prog2_refuse(const char *why, int i, const void *ctl) {
    static _Atomic unsigned n;
    if (n++ < 8) LOG("[RED] [nvn2] program refused: shader %d %s (control %p)", i, why, ctl);
    return 0;
}

// Each distinct combination of stages a program has, logged the first time it is seen: the replay layer draws vertex / fragment / compute programs; task and
// mesh programs (MKW: task + mesh + fragment) have no Metal pipeline yet, their stages are listed here for that work
static void prog2_note_stages(unsigned mask) {
    static pthread_mutex_t mu = PTHREAD_MUTEX_INITIALIZER; static uint64_t seen[(1u << 8) / 64]; static unsigned ncomb;
    pthread_mutex_lock(&mu);
    int first = !(seen[mask / 64] >> (mask % 64) & 1);
    if (first) { seen[mask / 64] |= 1ull << (mask % 64); ncomb++; }
    pthread_mutex_unlock(&mu);
    if (!first) return;
    char s[96] = ""; for (int st = 0; st < 8; st++) if (mask >> st & 1) { if (s[0]) strlcat(s, " + ", sizeof s); strlcat(s, k_stage[st], sizeof s); }
    if (mask & (3u << NVN2_SHADER_STAGE_TASK)) LOG("[RED] [nvn2] program stages %s (combination %u): mesh-shader programs are recorded but not drawn", s, ncomb);
    else LOG("[nvn2] program stages %s (combination %u)", s, ncomb);
}

// Checks the stages as the driver does (nvn2.md, "SetShaders core" step 3), then hands plain {code, control} records to the NVN1 program, which keys each stage.
// The debug-data hashes are not used: the host keys a stage on its control and code bytes
static NVNboolean prog2_set(NVNprogram *p, int n, const NVNshaderData *sd) {
    if (n < 0 || n > 9) return prog2_refuse("count out of range", n, NULL);
    unsigned mask = 0;
    for (int i = 0; i < n; i++) {
        const NVN2controlSection *c = sd[i].control;
        if (!c || ((uintptr_t)c & 7)) return prog2_refuse("control missing or not 8-byte aligned", i, c);
        if (!sd[i].data) return prog2_refuse("code address 0", i, c);
        if (c->major != NVN2_CONTROL_MAJOR || c->version > NVN2_CONTROL_MAX_VERSION) return prog2_refuse("not an NVN2 control section (+0x04 != 0xc9 or version > 11)", i, c);
        if (c->version < 5 && c->unkd1) return prog2_refuse("+0xd1 set below version 5", i, c);
        if (c->gpu_arch != NVN2_GPU_ARCH || c->gpu_impl != NVN2_GPU_IMPL) return prog2_refuse("built for another GPU (+0x0c / +0x0e)", i, c);
        if (c->stage >= NVN2_SHADER_STAGE_RAY_TRACING) return prog2_refuse("stage 8 (ray tracing) or above", i, c);
        if (mask >> c->stage & 1) return prog2_refuse("stage repeated", i, c);
        mask |= 1u << c->stage;
    }
    // prog_t (nvn_obj.h) holds 6 stages: no driver-valid program reaches 7 distinct stages, as vertex / tessellation / geometry and task / mesh exclude each other [INFERENCE]
    if (n > 6) return prog2_refuse("more than 6 stages", n, NULL);
    NVNT_nvnProgramSetShaders set = nvn1_set();
    if (!set) return prog2_refuse("no NVN1 program in this executable", 0, NULL);
    NVNboolean ok = set(p, n, sd);
    if (B(ok)) prog2_note_stages(mask);
    return ok;
}
NVN2(nvnProgramSetShaders, NVNboolean, NVNprogram *p, int n, const NVNshaderData *sd) { return prog2_set(p, n, sd); }
// sdk+0x5c03f0: 24-byte {code, control, debug-data hash} records, at most 9
NVN2(nvnProgramSetShadersExt, NVNboolean, NVNprogram *p, int n, const NVN2shaderDataExt *sd) {
    if (n < 0 || n > 9) return prog2_refuse("count out of range", n, NULL);
    NVNshaderData plain[9];
    for (int i = 0; i < n; i++) { plain[i].data = sd[i].data; plain[i].control = sd[i].control; }
    return prog2_set(p, n, plain);
}

// sdk+0x5c1730: Initialize, the sample-shading mode, then the SetShaders core with the builder's shaders (or, with ray-tracing modules, a ray-tracing link
// instead, which the host does not have), then the subroutine linkage
NVN2(nvnProgramInitializeFromBuilder, NVNboolean, NVNprogram *p, const NVN2programBuilder *b) {
    const prog_builder2_t *o = CPB(b);
    NVNT_nvnProgramInitialize init = nvn1_init();
    if (!init || !B(init(p, o->dev))) return 0;
    if (o->nrt > 0) { static int once; if (!once++) LOG("[RED] [nvn2] InitializeFromBuilder with %d ray-tracing modules: the host has no ray tracing, the program is refused", o->nrt); return 0; }
    if (o->sample_shading) { static int once; if (!once++) LOG("[nvn2] program builder sample shading mode %d: Metal shades per sample only where the shader reads the sample index", o->sample_shading); }
    if (!B(prog2_set(p, o->nshader, o->shader))) return 0;
    if (o->nlink > 0) { static int once; if (!once++) LOG("[RED] [nvn2] InitializeFromBuilder with %d subroutine linkage maps: subroutines are not supported", o->nlink); }
    return 1;
}
// sdk+0x5c0910 returns program byte +0, which every SetShaders clears and nothing writes
NVN2(nvnProgramGetSubtileSize, uint8_t, const NVNprogram *p) { (void)p; return 0; }
