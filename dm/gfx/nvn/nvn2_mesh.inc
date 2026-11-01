// NVN2 (Switch 2) mesh-shader draws: the DrawMeshTasks family (OP_MESH, nvn2_cmd.c) replayed as Metal mesh draws. Included by nvn_mtl.m after nvn_geometry.inc.
// A program with task and / or mesh stages (control +0x56 = 6 / 7) draws through a mesh pipeline: the task program as the object function, the mesh program as the mesh
// function, the program's fragment stage (or the bound one) as the fragment function. The functions are the translator's kernels wrapped by dm/gfx/tools/dmsh_mesh.py
// (taskmesh); each library's json names its threads, payload bytes and whether it runs under a task ("mesh", dmsh_mesh.layout). The draw goes through draw() as a
// synthetic one (as geo_mesh_draw's does), so the render pass, state, fragment arguments and ordering are a plain draw's; draw() calls mesh2_pso for its pipeline,
// mesh2_args for the task / mesh arguments (NVN stages 6 / 7, through bind_args → mesh2_bind_args) and mesh2_encode for the draw itself.
// docs/reference/rendering.md (Mesh-shader draws), docs/reference/shaders-sm86.md (Task and mesh programs).
enum { NVN2_MESH_DRAW_SLOT = 12, NVN2_STAGE_TASK = 6, NVN2_STAGE_MESH = 7 };   // NVN2_MESH_DRAW_SLOT = dmsh_mesh.py TM_DRAW_SLOT: the draw's {first, 0, 0, 0}
#define MESH2_RED_ONCE(...) do { static int once_; if (!once_++) MLOG("[RED] " __VA_ARGS__); } while (0)
@interface Mesh2Prog : NSObject { @public NvProg *pr; NvShader *ts, *ms, *fs; uint32_t tthreads, mthreads, payload; int mesh_task; } @end   // pr: the draw's fragment stage alone (sh[1]); fs: the program's own
@implementation Mesh2Prog @end
static struct { int on, miss; __unsafe_unretained Mesh2Prog *mp; uint32_t first, groups; } g_mesh2;
static id<MTL4ArgumentTable> g_mesh2_ot;   // the object stage's table; the mesh stage uses the vertex table (g_vt), as geo_mesh_encode
static uint64_t g_mesh2_n, g_mesh2_groups, g_mesh2_wait, g_mesh2_skip;
static int mesh2_on(void) {
    static int on = -1;
    if (on < 0) { const char *e = getenv("DM_NVN_MESH"); on = !(e && !strcmp(e, "0")); if (!on) MLOG("DM_NVN_MESH=0: NVN2 mesh-shader draws are skipped (diagnostic; the picture lacks their geometry)"); }
    return on;
}
// Diagnostic DM_NVN_MESH_SKIP=<key prefix>[,...]: skips the mesh draws whose task, mesh or fragment key starts with one of them (to find which program a wrong picture comes from)
static int mesh2_skip_key(const Mesh2Prog *mp) {
    static char keys[16][20]; static int n = -1;
    if (n < 0) { n = 0; for (const char *q = getenv("DM_NVN_MESH_SKIP"); q && *q && n < 16; q = strchr(q, ',') ? strchr(q, ',') + 1 : q + strlen(q)) { size_t l = strcspn(q, ","); if (l > 19) l = 19; memcpy(keys[n], q, l); keys[n++][l] = 0; }
                 if (n) MLOG("DM_NVN_MESH_SKIP: skipping the mesh draws of %d key prefixes (diagnostic; the picture is wrong)", n); }
    for (int i = 0; i < n; i++) { size_t l = strlen(keys[i]);
        if ((mp->ts && !strncmp(mp->ts->key, keys[i], l)) || !strncmp(mp->ms->key, keys[i], l) || (mp->pr->sh[1] && !strncmp(mp->pr->sh[1]->key, keys[i], l))) return 1; }
    return 0;
}
// The facts dmsh_build.py writes beside a task / mesh library ("mesh": dmsh_mesh.layout); 0 when the json has none
static int mesh2_info(NvShader *sh, uint32_t *threads, uint32_t *payload, int *task) {
    NSData *d = [NSData dataWithContentsOfFile:[NSString stringWithFormat:@"%@/%s.json", g_msl_dir, sh->key]];
    NSDictionary *j = d ? [NSJSONSerialization JSONObjectWithData:d options:0 error:nil] : nil, *m = [j isKindOfClass:NSDictionary.class] ? j[@"mesh"] : nil;
    if (![m isKindOfClass:NSDictionary.class] || ![m[@"local"] isKindOfClass:NSArray.class]) return 0;
    *threads = [m[@"local"][0] unsignedIntValue]; *payload = [m[@"payload"] unsignedIntValue]; *task = [m[@"task"] boolValue];
    return *threads >= 1 && *threads <= 32;
}
// The mesh program of a bound program: its task, mesh and fragment stages (the fragment stage from the draw's bound program when it has none); nil when it has no mesh
// stage or a stage has no library. Cached by program serial for the program's life (mtl_prog_release does not reach here: a serial is never reused)
static NSMutableDictionary<NSNumber *, id> *g_mesh2_progs;
static Mesh2Prog *mesh2_prog_get(const prog_t *p) {
    if (!p) return nil;
    if (!g_mesh2_progs) g_mesh2_progs = [NSMutableDictionary dictionary];
    NSNumber *ck = @(p->serial ? p->serial : (uint64_t)(uintptr_t)p);
    id have = g_mesh2_progs[ck];
    if (have) return have == NSNull.null ? nil : have;
    shmap_load();
    Mesh2Prog *o = [Mesh2Prog new]; o->pr = [NvProg new]; NSString *fail = nil;
    for (unsigned i = 0; i < p->nstage && !fail; i++) {
        uint8_t md[8]; prog_key(p, i, md);
        NSString *k = [NSString stringWithFormat:@"%02x%02x%02x%02x%02x%02x%02x%02x", md[0], md[1], md[2], md[3], md[4], md[5], md[6], md[7]], *stage = g_shmap[k];
        int st = [stage isEqualToString:@"task"] ? NVN2_STAGE_TASK : [stage isEqualToString:@"mesh"] ? NVN2_STAGE_MESH : [stage isEqualToString:@"pixel"] ? 1 : -1;
        if (st < 0) continue;
        NvShader *sh = shader_get([k stringByAppendingString:@"_b"], st);
        if (!sh || !sh->fn) { fail = [NSString stringWithFormat:@"%@ %@ has no library", stage, k]; break; }
        if (st == 1) o->fs = sh; else if (st == NVN2_STAGE_TASK) o->ts = sh; else o->ms = sh;
    }
    int ttask = 0;
    if (!fail && !o->ms) fail = @"no mesh stage";
    if (!fail && !mesh2_info(o->ms, &o->mthreads, &o->payload, &o->mesh_task)) fail = @"the mesh library's json has no mesh layout";
    if (!fail && o->ts && !mesh2_info(o->ts, &o->tthreads, &o->payload, &ttask)) fail = @"the task library's json has no task layout";
    if (!fail && !!o->ts != !!o->mesh_task) fail = o->ts ? @"its mesh program is one drawn without a task" : @"its mesh program runs under a task, and the program has none";
    if (fail) { static int n; if (n++ < 8) MLOG("[RED] NVN2 mesh program %p: %s; its mesh draws are skipped", (const void *)p, fail.UTF8String); g_mesh2_progs[ck] = NSNull.null; return nil; }
    g_mesh2_progs[ck] = o;
    return o;
}
static void mesh2_table_set(id<MTL4ArgumentTable> t, uint64_t gpu, NSUInteger i) { if (rec_on()) rec_table_op(RO_T_ADDR, t, gpu, i); else [t setAddress:gpu atIndex:i]; }
static id<MTL4ArgumentTable> mesh2_ot(void) {
    if (!g_mesh2_ot) {
        MTL4ArgumentTableDescriptor *d = [MTL4ArgumentTableDescriptor new]; d.maxBufferBindCount = 31; d.maxTextureBindCount = 1; NSError *e = nil;
        g_mesh2_ot = rec_table([g_dev newArgumentTableWithDescriptor:d error:&e]); if (!g_mesh2_ot) MDIE("failed to create the object argument table: %s", e.localizedDescription.UTF8String);
    }
    return g_mesh2_ot;
}
// bind_args for NVN stages 6 / 7: the task program's argument buffer in the object table, the mesh program's in the vertex table, which the mesh stage shares
static void mesh2_bind_args(id<MTLCommandEncoder> en, int nvn_stage, uint64_t gpu, NSUInteger ai) {
    if (nvn_stage == NVN2_STAGE_MESH) enc_r_vbuf((id<MTL4RenderCommandEncoder>)en, ai, gpu);
    else mesh2_table_set(mesh2_ot(), gpu, ai);
}
static nvn_hmap g_pso_mesh2 = {.klen = sizeof(pso_key)}; static os_unfair_lock g_pso_mesh2_lock = OS_UNFAIR_LOCK_INIT;
static NSObject *g_pso_mesh2_building;
// The draw's mesh pipeline (draw calls it while g_mesh2.on), or nil while it is built in the background (or when that failed): the draw then counts as waiting and nothing is drawn
static id<MTLRenderPipelineState> mesh2_pso(NvProg *pr) {
    Mesh2Prog *mp = g_mesh2.mp; NvShader *fs = pr->sh[1];
    pso_key k; pso_key_fill(&k, mp->ms, fs, g_pass.cfmt, g_pass.dfmt, g_pass.sfmt, g_pass.samples, 0);
    k.fsfn = mp->ts ? (__bridge void *)mp->ts : NULL;   // the key's spare function slot names the task stage
    os_unfair_lock_lock(&g_pso_mesh2_lock); id ps = hm_get_id(&g_pso_mesh2, &k);
    if (!ps) { if (!g_pso_mesh2_building) g_pso_mesh2_building = [NSObject new]; hm_put_id(&g_pso_mesh2, &k, g_pso_mesh2_building); }
    os_unfair_lock_unlock(&g_pso_mesh2_lock);
    if (!ps) {
        MTLRenderPipelineDescriptor *d = pso_desc(&k, mp->ms, fs, nil, 0);
        MTLMeshRenderPipelineDescriptor *m = [MTLMeshRenderPipelineDescriptor new];
        m.objectFunction = mp->ts ? mp->ts->fn : nil; m.meshFunction = mp->ms->fn; m.fragmentFunction = d.fragmentFunction;
        if (mp->ts) { m.payloadMemoryLength = mp->payload; m.maxTotalThreadsPerObjectThreadgroup = mp->tthreads; }
        m.maxTotalThreadsPerMeshThreadgroup = mp->mthreads;
        m.rasterSampleCount = d.rasterSampleCount; m.alphaToCoverageEnabled = d.alphaToCoverageEnabled;
        for (int i = 0; i < 8; i++) m.colorAttachments[i] = d.colorAttachments[i];
        m.depthAttachmentPixelFormat = d.depthAttachmentPixelFormat; m.stencilAttachmentPixelFormat = d.stencilAttachmentPixelFormat;
        const char *tk = mp->ts ? mp->ts->key : "-", *mk = mp->ms->key, *fk = fs ? fs->key : "-"; pso_key kk = k;
        dispatch_async(pp_bg_queue(), ^{
            NSError *e = nil; uint64_t t0 = bp_now();
            id<MTLRenderPipelineState> p = [g_dev newRenderPipelineStateWithMeshDescriptor:m options:MTLPipelineOptionNone reflection:nil error:&e];
            MLOG("%sNVN2 mesh pipeline: task %s mesh %s fragment %s, %.1f ms in the background%s%s", p ? "" : "[RED] ", tk, mk, fk, (bp_now() - t0) / 1e6, p ? "" : ": ", p ? "" : e.localizedDescription.UTF8String);
            os_unfair_lock_lock(&g_pso_mesh2_lock); hm_put_id(&g_pso_mesh2, &kk, p ? (id)p : (id)NSNull.null); os_unfair_lock_unlock(&g_pso_mesh2_lock);
        });
        return nil;
    }
    return ps == NSNull.null || ps == g_pso_mesh2_building ? nil : ps;
}
// The task and mesh arguments (draw calls it where a plain draw encodes its vertex stage's)
static void mesh2_args(id<MTLRenderCommandEncoder> en) {
    if (g_mesh2.mp->ts) encode_args(en, g_mesh2.mp->ts, NVN2_STAGE_TASK, 0);
    encode_args(en, g_mesh2.mp->ms, NVN2_STAGE_MESH, 0);
}
// The draw itself: the draw block {first} beside each stage's arguments, then the mesh threadgroups (under a task: its threadgroups, which launch the mesh ones)
static void mesh2_encode(id<MTLRenderCommandEncoder> en_) {
    id<MTL4RenderCommandEncoder> en = (id<MTL4RenderCommandEncoder>)en_; Mesh2Prog *mp = g_mesh2.mp;
    id<MTLBuffer> b; size_t o = arena_alloc(16, &b); uint32_t blk[4] = {g_mesh2.first, 0, 0, 0}; memcpy((uint8_t *)b.contents + o, blk, sizeof blk);
    [en setArgumentTable:g_vt atStages:MTLRenderStageMesh];
    enc_r_vbuf(en, NVN2_MESH_DRAW_SLOT, b.gpuAddress + o);
    if (mp->ts) { [en setArgumentTable:mesh2_ot() atStages:MTLRenderStageObject]; mesh2_table_set(mesh2_ot(), b.gpuAddress + o, NVN2_MESH_DRAW_SLOT); }
    [en drawMeshThreadgroups:MTLSizeMake(g_mesh2.groups, 1, 1) threadsPerObjectThreadgroup:MTLSizeMake(mp->ts ? mp->tthreads : 1, 1, 1) threadsPerMeshThreadgroup:MTLSizeMake(mp->mthreads, 1, 1)];
}
// One mesh draw of groups workgroups from first, through draw() with the mesh program's fragment stage as its program
static void mesh2_one(Mesh2Prog *mp, uint32_t first, uint32_t groups) {
    if (!groups) return;
    NvProg *notes = [NvProg new]; notes->sh[0] = mp->ms; notes->sh[1] = mp->pr->sh[1]; notes->sh[2] = mp->ts;   // the stages' storage / image writes, for the pass's ordering
    order_draw(notes, 0);
    g_mesh2.on = 1; g_mesh2.miss = 0; g_mesh2.mp = mp; g_mesh2.first = first; g_mesh2.groups = groups;
    g_geo_program = mp->pr;
    p_draw synthetic = {.prim = 4, .count = 3, .instances = 1};
    draw(&synthetic, NULL);
    g_geo_program = nil; g_mesh2.on = 0; g_mesh2.mp = nil;
    if (g_mesh2.miss) { g_mesh2_wait++; return; }
    swrite_note(notes); udc_note_draw(notes); g_wr_epoch++; g_mdx_epoch++;   // as geo_mesh_draw: the stages' storage writes
    g_mesh2_groups += groups;
    uint64_t n = ++g_mesh2_n;
    if (!(n & (n - 1)))   // the first draw and each power of two after it
        MLOG("NVN2 mesh draw %llu: task %s mesh %s fragment %s, %u workgroups from %u (%llu workgroups so far; %llu draws waited for their pipeline, %llu skipped)",
             (unsigned long long)g_mesh2_n, mp->ts ? mp->ts->key : "-", mp->ms->key, mp->pr->sh[1] ? mp->pr->sh[1]->key : "-", groups, first,
             (unsigned long long)g_mesh2_groups, (unsigned long long)g_mesh2_wait, (unsigned long long)g_mesh2_skip);
}
// Indirect records: read on the CPU after the GPU work before them (as mdi_live reads a count); MKW draws none
static uint64_t g_mesh2_rd_epoch = ~0ull, g_mesh2_rd_sub;
static int mesh2_read(uint64_t a, void *out, size_t n) {
    if (g_mesh2_rd_epoch != g_wr_epoch || g_mesh2_rd_sub != g_sub_serial) { mdi_wait(); g_mesh2_rd_epoch = g_wr_epoch; g_mesh2_rd_sub = g_sub_serial; }
    memset(out, 0, n);
    if (!a || !nvn_host_readable(a, n)) return 0;
    memcpy(out, (const void *)(uintptr_t)a, n); return 1;
}
// OP_MESH: the record's forms (nvn2_cmd.h p_mesh). A direct draw is {first, count}; a 3-D one {x, y, z} workgroups. Indirect records are {count, first} (8 bytes) and {x, y, z}
// (12 bytes), the direct methods' data (docs/reference/nvn2.md, Mesh/task draws: the word order is inferred); the multi-draws read n of them stride apart, the count forms
// at most the count at cnt. A 3-D grid is drawn as its x * y * z workgroups in a row: the program reads one workgroup index (VERTEX_ID), and how NVN2 gives it the grid's
// shape is not known (no MKW draw has one)
static void mesh2_draw(const p_mesh *m) {
    if (!mesh2_on()) { g_mesh2_skip++; return; }
    Mesh2Prog *mp = mesh2_prog_get(S.mprog);
    if (!mp) { g_mesh2_skip++; PD("Z %llu mesh draw skipped: the bound mesh program has no libraries", g_stat_pass + !g_enc); return; }
    __unsafe_unretained NvProg *bound = !mp->fs && S.prog ? prog_get(S.prog) : nil;
    mp->pr->sh[1] = mp->fs ? mp->fs : bound ? bound->sh[1] : nil;
    if (mesh2_skip_key(mp)) { g_mesh2_skip++; return; }
    int three = m->kind == MESH_TASKS_3D || m->kind == MESH_3D_INDIRECT || m->kind == MESH_3D_MULTI_INDIRECT || m->kind == MESH_3D_MULTI_INDIRECT_COUNT;
    if (three) MESH2_RED_ONCE("NVN2 3-D mesh draw (kind %d): its workgroups are drawn in a row, x * y * z of them (how the program learns the grid's shape is not known)", m->kind);
    if (m->kind == MESH_TASKS) { mesh2_one(mp, (uint32_t)m->x, (uint32_t)m->y); return; }
    if (m->kind == MESH_TASKS_3D) { mesh2_one(mp, 0, (uint32_t)m->x * (uint32_t)m->y * (uint32_t)m->z); return; }
    uint32_t n = m->kind == MESH_INDIRECT || m->kind == MESH_3D_INDIRECT ? 1 : (uint32_t)MAX(m->n, 0), size = three ? 12 : 8;
    uint64_t stride = m->stride > 0 ? (uint64_t)m->stride : size;
    if (m->kind == MESH_MULTI_INDIRECT_COUNT || m->kind == MESH_3D_MULTI_INDIRECT_COUNT) {
        uint32_t c; if (!mesh2_read(m->cnt, &c, 4)) { MESH2_RED_ONCE("NVN2 mesh draw: the draw count at %#llx is not readable, nothing drawn", (unsigned long long)m->cnt); return; }
        n = MIN(n, c);
    }
    for (uint32_t i = 0; i < n; i++) {
        uint32_t r[3];
        if (!mesh2_read(m->ind + stride * i, r, size)) { MESH2_RED_ONCE("NVN2 mesh draw: the indirect record at %#llx is not readable, nothing drawn", (unsigned long long)(m->ind + stride * i)); return; }
        if (three) mesh2_one(mp, 0, r[0] * r[1] * r[2]); else mesh2_one(mp, r[1], r[0]);
    }
}
