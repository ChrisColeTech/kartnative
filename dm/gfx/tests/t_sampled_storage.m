// Uncompressed texels the CPU stored before nvnTextureInitialize in a CPU_CACHED pool, with no flush (Mario Kart World's terrain tiles). On Switch 2 (NVN2, coherent memory) a texture
// whose first use is a sample is taken from its storage, re-uploaded when the CPU rewrites it, and a new object over a reused slot samples the slot's new data; a texture first used as
// a render target stays the GPU texture that a later sampled object over the same storage shares; a depth texture stays a GPU texture. On Switch 1 nothing changes: the unflushed
// texels are not the content (a GPU texture of zeros). Run under both profiles: make GAME=0400C3F00006E000 check_t_sampled_storage and GAME=010015100B514000
#include "../nvn/nvn_mtl.m"
void *front_metal_layer(void) { return NULL; }
module_t *module_of(const void *p) { (void)p; return NULL; }
module_t *g_mod; int g_nmod;
void svc_thread_dump(void) {}
uint64_t now_ns(void) { return bp_now(); }
static int checks, failed;
#define CHECK(label,c) do {checks++;if(!(c)){failed++;fprintf(stderr,"FAIL: %s\n",label);}}while(0)
static void finish(void) { CHECK("submission completes",mtl_wait(mtl_sub_commit(),5000000000ull)); CHECK("GPU has no error",!mtl_last_commit_error()); }
enum { W = 16 };
static void fill(tex_t *t, uint32_t rgba) {   // every texel of level 0 = rgba (bytes R, G, B, A), written in place as the CPU would
    uint32_t linear[W * W]; for (int i = 0; i < W * W; i++) linear[i] = rgba;
    dm_guest_write_prepare(t->mem, t->L.size);
    CHECK("fixture swizzle succeeds", !nvn_swizzle(&t->d, 0, 0, linear, W * 4, t->mem));
}
static uint32_t probe(id<MTLTexture> image) {   // texel (5, 7) as RGBA8 bytes
    static id<MTLComputePipelineState> ps; static id<MTLCommandQueue> queue; NSError *error = nil;
    if (!ps) { id<MTLLibrary> lib = [g_dev newLibraryWithSource:@"#include <metal_stdlib>\nusing namespace metal;kernel void rd(texture2d<float> t[[texture(0)]],device uint *o[[buffer(0)]]){float4 c=t.read(uint2(5,7));o[0]=uint(round(c.r*255))|uint(round(c.g*255))<<8|uint(round(c.b*255))<<16|uint(round(c.a*255))<<24;}" options:nil error:&error];
        id<MTLFunction> rd = [lib newFunctionWithName:@"rd"]; ps = rd ? [g_dev newComputePipelineStateWithFunction:rd error:&error] : nil; queue = [g_dev newCommandQueue]; }
    CHECK("probe compiles", ps != nil); if (!ps || !image) return 0xdeadbeef;
    id<MTLBuffer> out = [g_dev newBufferWithLength:4 options:MTLResourceStorageModeShared];
    id<MTLCommandBuffer> cb = [queue commandBuffer]; id<MTLComputeCommandEncoder> ce = [cb computeCommandEncoder];
    [ce setComputePipelineState:ps]; [ce setTexture:image atIndex:0]; [ce setBuffer:out offset:0 atIndex:0]; [ce dispatchThreadgroups:MTLSizeMake(1,1,1) threadsPerThreadgroup:MTLSizeMake(1,1,1)]; [ce endEncoding];
    [cb commit]; [cb waitUntilCompleted];
    return *(uint32_t *)out.contents;
}
static tex_t make(pool_t *pool, uint64_t off, uint64_t serial, int format, uint32_t flags) {
    tex_t t = {.magic = MAGIC('T','E','X',0), .serial = serial, .pool = pool, .mem = pool->mem + off, .off = off};
    t.d = (nvn_texdesc){.target = NVN_TEXTURE_TARGET_2D, .format = format, .width = W, .height = W, .depth = 1, .levels = 1, .samples = 1, .flags = flags};
    nvn_tex_layout(&t.d, &t.L);
    return t;
}
static id<MTLTexture> sampled(const tex_t *t) { mtl_sub_begin(); id<MTLTexture> r = tex_mtl_sampled(t); finish(); return r; }
int main(void) { @autoreleasepool { mtl_init();
    enum { POOL = 1 << 20 }; void *mem = NULL; if (posix_memalign(&mem, getpagesize(), POOL)) abort(); memset(mem, 0, POOL);
    pool_t pool = {.mem = mem, .size = POOL, .flags = NVN_MEMORY_POOL_FLAGS_CPU_CACHED | NVN_MEMORY_POOL_FLAGS_GPU_CACHED};
    mtl_pool_add(mem, POOL); nvn_dirty_add_range(mem, POOL);
    const uint32_t A = 0xff2040a0u, B = 0xff10e030u, C = 0x80c0c0c0u, STALE = 0xff0000ffu;
    const int s2 = DM_GAME_SWITCH2;
    // A tile slot: texels stored before init, first use a sample
    tex_t t = make(&pool, 0, 9800, NVN_FORMAT_RGBA8, NVN_TEXTURE_FLAGS_MINIMAL_LAYOUT);
    fill(&t, A); mtl_tex_init(&t);
    uint32_t got = probe(sampled(&t));
    if (s2) CHECK("NVN2: the stored texels at the first sample", got == A); else CHECK("NVN1: unflushed texels are not the content", got == 0);
    // The CPU rewrites the slot under the same object
    fill(&t, B); got = probe(sampled(&t));
    if (s2) CHECK("NVN2: a later CPU write is re-uploaded", got == B); else CHECK("NVN1: unchanged", got == 0);
    // The slot is reused: the object goes, the CPU stores the next tile, a new object with the same description takes the slot
    mtl_tex_release(&t); fill(&t, C);
    tex_t u = make(&pool, 0, 9801, NVN_FORMAT_RGBA8, NVN_TEXTURE_FLAGS_MINIMAL_LAYOUT); mtl_tex_init(&u);
    got = probe(sampled(&u));
    if (s2) CHECK("NVN2: a new object over a reused slot samples the slot's new data", got == C); else CHECK("NVN1: unchanged", got == 0);
    mtl_tex_release(&u);
    // A render target over storage holding stale bytes: first used as a target (not a sample), it stays a GPU texture; a sampled object over the same storage shares it
    tex_t rt = make(&pool, 0x40000, 9802, NVN_FORMAT_RGBA8, 0); fill(&rt, STALE); mtl_tex_init(&rt);
    mtl_sub_begin(); id<MTLTexture> target = tex_mtl(&rt); finish();
    CHECK("a render target is not taken from its storage", !tex_cpu_written(rt.serial));
    tex_t view = make(&pool, 0x40000, 9803, NVN_FORMAT_RGBA8, 0); mtl_tex_init(&view);
    id<MTLTexture> sv = sampled(&view);
    CHECK("a sampled object over a render target's storage shares the target", sv && (sv == target || sv.parentTexture == target));
    CHECK("... and is not taken from the storage", !tex_cpu_written(view.serial));
    mtl_tex_release(&view); mtl_tex_release(&rt);
    // A depth texture first sampled stays a GPU texture
    tex_t dt = make(&pool, 0x80000, 9804, NVN_FORMAT_DEPTH32F, 0); fill(&dt, A); mtl_tex_init(&dt);
    sampled(&dt); CHECK("a depth texture is not taken from its storage", !tex_cpu_written(dt.serial));
    mtl_tex_release(&dt);
    // An image first resolved through its texture ID stays a GPU texture
    tex_t im = make(&pool, 0xc0000, 9805, NVN_FORMAT_RGBA8, NVN_TEXTURE_FLAGS_IMAGE); fill(&im, A); mtl_tex_init(&im);
    sampled(&im); CHECK("an image is not taken from its storage", !tex_cpu_written(im.serial));
    mtl_tex_release(&im);
    nvn_dirty_remove_range(mem); mtl_pool_del(mem); mtl_drain(); free(mem);
    printf("t_sampled_storage (%s): %d checks, %d failed\n", s2 ? "Switch 2" : "Switch 1", checks, failed); return failed != 0; } }
