// The global pointer table (nvn_mtl.m gptr_gpu, dmsh_abi.h DMSH_GPTR_*): every memory pool's guest range and Metal address, sorted, overlaps cut, padded with sentinels to a
// power of two, rebuilt when a pool comes or goes, and every pool resident once it is built. Run with no arguments it checks the table on the CPU.
// With a translated library folder (dmsh_build.py output holding Mario Kart World's two pointer programs) it also runs them on the GPU against pools made here:
//   t_gptr <library folder>
// 0459a55e79335cf6 (gsys clear_geometry_culling, vertex): vertex i stores {0, 1, 0, 0, 0} through the i-th pointer of its storage buffer; 791fb7522aa03e61 (agl
// invalid_pixel_detector, compute): one NaN texel among 16 x 16, and with c0 word 0 set the shader adds the word at its pointer + a per-kind offset to word 20.
// A pointer into no pool stores nothing, loads 0 and counts its misses in the table, which the host logs.
#include "../nvn/nvn_mtl.m"
#include <sys/mman.h>
void *front_metal_layer(void) { return NULL; }
module_t *module_of(const void *p) { (void)p; return NULL; }
module_t *g_mod; int g_nmod;
void svc_thread_dump(void) {}
uint64_t now_ns(void) { return bp_now(); }
static int checks, failed;
#define CHECK(label, c) do { checks++; if (!(c)) { failed++; fprintf(stderr, "FAIL: %s\n", label); } } while (0)

static uint8_t *pool_mem(size_t n, uint8_t fill) {
    uint8_t *p = mmap(NULL, n, PROT_READ | PROT_WRITE, MAP_ANON | MAP_PRIVATE, -1, 0);
    assert(p != MAP_FAILED); memset(p, fill, n); return p;
}
typedef struct { uint64_t lo, hi, delta, zero; } entry;
static const entry *table_entries(id<MTLBuffer> b) { return (const entry *)((const uint8_t *)b.contents + DMSH_GPTR_HEADER); }
// The Metal address the table gives a guest address (the shader's search, on the CPU), 0 when no entry holds it
static uint64_t table_lookup(id<MTLBuffer> b, uint64_t a) {
    const uint32_t *h = b.contents; const entry *e = table_entries(b); uint32_t n = 1u << (DMSH_GPTR_STEPS - h[0]), pos = 0;
    for (int s = 0; s < DMSH_GPTR_STEPS; s++) { uint32_t half = (1u << (DMSH_GPTR_STEPS - 1)) >> s >> h[0], i = MAX(pos + half, 1u) - 1; if (half && e[i].lo <= a) pos += half; }
    (void)n; return pos && a < e[pos - 1].hi ? a + e[pos - 1].delta : 0;
}
static uint64_t host_gpu(const void *p) { void *mb; uint64_t mo, gpu; return mtl_gpu_addr((uint64_t)(uintptr_t)p, 4, &mb, &mo, &gpu) ? gpu : 0; }

// The argument buffer member of the binding of kind k in the shader's reflection
static int arg_id(NSDictionary *j, const char *k) { for (NSDictionary *b in j[@"bindings"]) if ([b[@"kind"] isEqualToString:@(k)]) return [b[@"arg_id"] intValue]; return -1; }
static NSDictionary *refl(const char *dir, const char *key) {
    NSData *d = [NSData dataWithContentsOfFile:[NSString stringWithFormat:@"%s/%s_b.json", dir, key]]; assert(d);
    return [NSJSONSerialization JSONObjectWithData:d options:0 error:nil];
}
static id<MTLFunction> lib_fn(const char *dir, const char *key, NSString *entry) {
    NSError *e = nil; id<MTLLibrary> lib = [g_dev newLibraryWithURL:[NSURL fileURLWithPath:[NSString stringWithFormat:@"%s/%s_b.metallib", dir, key]] error:&e];
    if (!lib) { fprintf(stderr, "library %s: %s\n", key, e.localizedDescription.UTF8String); exit(1); }
    id<MTLFunction> f = [lib newFunctionWithName:entry]; assert(f); return f;
}

// gsys clear_geometry_culling: each vertex resets the 20-byte record its pointer names (an indexed indirect draw: 0 indices, 1 instance)
static void run_vertex(const char *dir, uint8_t *a, uint8_t *c) {
    const char *key = "0459a55e79335cf6"; NSDictionary *j = refl(dir, key);
    id<MTLFunction> fn = lib_fn(dir, key, j[@"entry_discard"] ?: @"main0");   // the entry point the host draws a rasterizer-discard program with (void, no outputs)
    MTLRenderPipelineDescriptor *pd = [MTLRenderPipelineDescriptor new]; pd.vertexFunction = fn; pd.rasterizationEnabled = NO;
    NSError *e = nil; id<MTLRenderPipelineState> ps = [g_dev newRenderPipelineStateWithDescriptor:pd error:&e];
    if (!ps) { fprintf(stderr, "vertex pipeline: %s\n", e.localizedDescription.UTF8String); exit(1); }
    uint8_t *rec[6] = {a + 0x100, a + 0x2000 + 0x40, a + 0xfff0 - 20, c + 0x10, c + 0x7fe8, (uint8_t *)(uintptr_t)0x10000};   // the second lies in the alias pool too; the last in no pool
    id<MTLBuffer> list = [g_dev newBufferWithLength:sizeof rec options:MTLResourceStorageModeShared]; memcpy(list.contents, rec, sizeof rec);
    __unsafe_unretained id<MTLBuffer> tb = nil; uint64_t tg = gptr_gpu(&tb);
    uint32_t missed0 = ((const uint32_t *)tb.contents)[1];
    id<MTLArgumentEncoder> ae = [fn newArgumentEncoderWithBufferIndex:0];
    id<MTLBuffer> ab = [g_dev newBufferWithLength:ae.encodedLength options:MTLResourceStorageModeShared]; [ae setArgumentBuffer:ab offset:0];
    [ae setBuffer:list offset:0 atIndex:(NSUInteger)arg_id(j, DMSH_KIND_SBUF)]; [ae setBuffer:tb offset:0 atIndex:(NSUInteger)arg_id(j, DMSH_KIND_GPTR)];
    CHECK("the vertex program's reflection names the table", arg_id(j, DMSH_KIND_GPTR) >= 0);
    CHECK("the table's Metal address", tg == tb.gpuAddress);
    MTLRenderPassDescriptor *rp = [MTLRenderPassDescriptor renderPassDescriptor]; rp.renderTargetWidth = rp.renderTargetHeight = 1; rp.defaultRasterSampleCount = 1;
    id<MTLCommandBuffer> cb = [g_q commandBuffer]; id<MTLRenderCommandEncoder> en = [cb renderCommandEncoderWithDescriptor:rp];
    [en setRenderPipelineState:ps]; [en setVertexBuffer:ab offset:0 atIndex:0];
    [en useResource:list usage:MTLResourceUsageRead stages:MTLRenderStageVertex];   // the pools and the table are in the host's residency set
    [en drawPrimitives:MTLPrimitiveTypePoint vertexStart:0 vertexCount:6]; [en endEncoding]; [cb commit]; [cb waitUntilCompleted];
    CHECK("the vertex draw completed", cb.status == MTLCommandBufferStatusCompleted);
    const uint32_t want[5] = {0, 1, 0, 0, 0};
    for (int i = 0; i < 5; i++) {
        uint32_t w[7]; memcpy(w, rec[i] - 4, sizeof w);
        char l[96]; snprintf(l, sizeof l, "record %d holds {0, 1, 0, 0, 0}", i); CHECK(l, !memcmp(w + 1, want, sizeof want));
        snprintf(l, sizeof l, "record %d: the words around it are untouched", i); CHECK(l, w[0] == 0xeeeeeeee && w[6] == 0xeeeeeeee);
    }
    const uint32_t *h = tb.contents;
    CHECK("the pointer into no pool missed its 5 stores", h[1] - missed0 == 5);
    uint64_t last = (uint64_t)h[2] | (uint64_t)h[3] << 32;
    CHECK("the last miss names its address", last >= 0x10000 && last <= 0x10010);
    printf("vertex 0459a55e79335cf6: 5 records reset through pointers into 3 pools, 1 pointer into no pool: %u misses, the last at %#llx\n", h[1] - missed0, (unsigned long long)last);
}

// agl invalid_pixel_detector: words [1..3] enable the three kinds, [4] the range limit, [8..11] the channels, [12] the record limit, [14..15] the pointer, [16..18] the kinds'
// counters, [19] the invalid pixels; with [0] set the shader adds the word at pointer + 0xf000a000 / 0xf000b000 / 0xf000c000 (by the pixel's kind) to word 20
static uint32_t run_compute(const char *dir, uint64_t ptr, uint32_t out[24]) {
    const char *key = "791fb7522aa03e61"; NSDictionary *j = refl(dir, key);
    id<MTLFunction> fn = lib_fn(dir, key, @"main0");
    NSError *e = nil; id<MTLComputePipelineState> ps = [g_dev newComputePipelineStateWithFunction:fn error:&e]; assert(ps);
    MTLTextureDescriptor *td = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA32Float width:16 height:16 mipmapped:NO]; td.usage = MTLTextureUsageShaderRead;
    id<MTLTexture> tex = [g_dev newTextureWithDescriptor:td]; float px[16 * 16 * 4]; for (int i = 0; i < 16 * 16 * 4; i++) px[i] = 0.5f;
    px[(5 * 16 + 3) * 4] = NAN; [tex replaceRegion:MTLRegionMake2D(0, 0, 16, 16) mipmapLevel:0 withBytes:px bytesPerRow:16 * 16];
    id<MTLBuffer> sup = [g_dev newBufferWithLength:4096 options:MTLResourceStorageModeShared]; float *sf = sup.contents; for (int i = 0; i < 73; i++) sf[48 + 4 * i] = 1.0f;   // support buffer: render scale 1 (_m5 at byte 192)
    id<MTLBuffer> s0 = [g_dev newBufferWithLength:4096 options:MTLResourceStorageModeShared]; uint32_t *w = s0.contents; memset(w, 0, 4096);
    float lim = 1e30f; w[0] = 1; w[1] = w[2] = w[3] = 1; memcpy(&w[4], &lim, 4); w[8] = w[9] = w[10] = w[11] = 1; w[12] = 0; w[14] = (uint32_t)ptr; w[15] = (uint32_t)(ptr >> 32); w[20] = 7;
    __unsafe_unretained id<MTLBuffer> tb = nil; gptr_gpu(&tb); uint32_t missed0 = ((const uint32_t *)tb.contents)[1];
    MTLSamplerDescriptor *sd = [MTLSamplerDescriptor new]; sd.supportArgumentBuffers = YES; id<MTLSamplerState> ss = [g_dev newSamplerStateWithDescriptor:sd];
    id<MTLArgumentEncoder> ae = [fn newArgumentEncoderWithBufferIndex:0];
    id<MTLBuffer> ab = [g_dev newBufferWithLength:ae.encodedLength options:MTLResourceStorageModeShared]; [ae setArgumentBuffer:ab offset:0];
    [ae setBuffer:sup offset:0 atIndex:(NSUInteger)arg_id(j, DMSH_KIND_SUPPORT)]; [ae setBuffer:s0 offset:0 atIndex:(NSUInteger)arg_id(j, DMSH_KIND_SBUF)];
    [ae setTexture:tex atIndex:(NSUInteger)arg_id(j, DMSH_KIND_TEX)]; [ae setBuffer:tb offset:0 atIndex:(NSUInteger)arg_id(j, DMSH_KIND_GPTR)];
    int si = arg_id(j, DMSH_KIND_SAMP_TEX); if (si < 0) si = arg_id(j, DMSH_KIND_SAMP); if (si >= 0) [ae setSamplerState:ss atIndex:(NSUInteger)si];
    id<MTLCommandBuffer> cb = [g_q commandBuffer]; id<MTLComputeCommandEncoder> en = [cb computeCommandEncoder];
    [en setComputePipelineState:ps]; [en setBuffer:ab offset:0 atIndex:0];
    [en useResource:sup usage:MTLResourceUsageRead]; [en useResource:s0 usage:MTLResourceUsageRead | MTLResourceUsageWrite]; [en useResource:tex usage:MTLResourceUsageRead];
    [en dispatchThreadgroups:MTLSizeMake(1, 1, 1) threadsPerThreadgroup:MTLSizeMake(16, 16, 1)]; [en endEncoding]; [cb commit]; [cb waitUntilCompleted];
    CHECK("the compute dispatch completed", cb.status == MTLCommandBufferStatusCompleted);
    memcpy(out, w, 24 * 4);
    return ((const uint32_t *)tb.contents)[1] - missed0;
}

int main(int argc, char **argv) { @autoreleasepool {
    mtl_init();
    // Pools: a (64 KB), b an alias of a's pages 0x2000..0x6000 (as a virtual pool over a physical one), c (32 KB) elsewhere
    uint8_t *a = pool_mem(0x10000, 0xee), *c = pool_mem(0x8000, 0xee);
    mtl_pool_add(a, 0x10000); mtl_pool_add(a + 0x2000, 0x4000); mtl_pool_add(c, 0x8000);
    __unsafe_unretained id<MTLBuffer> t1 = nil; gptr_gpu(&t1);
    const uint32_t *h = t1.contents; const entry *e = table_entries(t1);
    CHECK("two ranges and the sentinel: 4 entries", h[0] == DMSH_GPTR_STEPS - 2 && t1.length == DMSH_GPTR_HEADER + 4 * DMSH_GPTR_ENTRY);
    CHECK("sorted by start", e[0].lo < e[1].lo && e[0].lo == (uint64_t)(uintptr_t)MIN(a, c));
    CHECK("the alias inside a is cut away", (e[0].hi - e[0].lo) + (e[1].hi - e[1].lo) == 0x18000);
    CHECK("sentinels after the ranges", e[2].lo == ~0ull && e[3].lo == ~0ull);
    CHECK("no misses yet", h[1] == 0);
    uint64_t probes[] = {(uintptr_t)a, (uintptr_t)a + 0x2004, (uintptr_t)a + 0xfffc, (uintptr_t)c + 0x100, (uintptr_t)c + 0x7ffc};
    for (unsigned i = 0; i < sizeof probes / sizeof *probes; i++) { char l[64]; snprintf(l, sizeof l, "address %u: the pool's Metal address", i); CHECK(l, table_lookup(t1, probes[i]) == host_gpu((void *)(uintptr_t)probes[i])); }
    CHECK("an address past a pool is in no entry", table_lookup(t1, (uintptr_t)c + 0x8000) == 0 || (uintptr_t)c + 0x8000 == (uintptr_t)a);
    CHECK("an address below every pool is in no entry", table_lookup(t1, 0x1000) == 0);
    __unsafe_unretained id<MTLBuffer> t2 = nil; gptr_gpu(&t2);
    CHECK("no pool change: the same table", t1 == t2);
    uint8_t *d = pool_mem(0x4000, 0xee); mtl_pool_add(d, 0x4000);
    __unsafe_unretained id<MTLBuffer> t3 = nil; gptr_gpu(&t3);
    CHECK("a new pool: a new table that holds it", t3 != t1 && table_lookup(t3, (uintptr_t)d + 8) == host_gpu(d + 8));
    pthread_mutex_lock(&g_mu); int res = 1; for (int i = 0; i < g_npool; i++) res &= g_pools[i].res; pthread_mutex_unlock(&g_mu);
    CHECK("every pool is resident", res);
    if (argc > 1) {
        run_vertex(argv[1], a, c);
        // the compute program's three words, 0x1000 apart, at pointer + 0xf000a000 + k * 0x1000 (a 64-bit sum: the shader carries into the high word)
        uint8_t *v = c + 0x400; uint32_t vals[3] = {1000, 2000, 3000}; for (int k = 0; k < 3; k++) memcpy(v + 0x1000 * k, &vals[k], 4);
        uint32_t out[24]; uint32_t miss = run_compute(argv[1], (uint64_t)(uintptr_t)v - 0xf000a000ull, out);
        int kind = out[16] ? 0 : out[17] ? 1 : out[18] ? 2 : -1;
        CHECK("one invalid pixel, of one kind", out[19] == 1 && out[16] + out[17] + out[18] == 1 && kind >= 0);
        CHECK("word 20 = 7 + the word at its kind's address through the pointer", kind >= 0 && out[20] == 7 + vals[kind]);
        CHECK("no miss", miss == 0);
        printf("compute 791fb7522aa03e61: counters %u %u %u, invalid %u, word 20 = %u (7 + %u, read through the pointer for kind %d)\n", out[16], out[17], out[18], out[19], out[20], kind >= 0 ? vals[kind] : 0, kind);
        miss = run_compute(argv[1], 0x20000, out);
        CHECK("a pointer into no pool loads 0 and counts its miss", out[20] == 7 && miss == 1);
        printf("compute 791fb7522aa03e61, pointer into no pool: word 20 = %u, %u miss\n", out[20], miss);
        __unsafe_unretained id<MTLBuffer> t4 = nil; gptr_gpu(&t4);   // the host logs the misses it finds
        CHECK("the host counted every miss", atomic_load(&g_gptr_missed) == 6);
    }
    printf("t_gptr: %d checks, %d failed\n", checks, failed);
    return failed != 0;
} }
