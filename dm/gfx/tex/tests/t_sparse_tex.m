// nvn_sparse_tex: sparse textures backed by Metal placement-sparse textures, on the local GPU (headless).
// S1 a 2D array whose guest pool maps some 64 KB tiles: mapped tiles and the mip tails read back as the guest's texels (block-linear, both GOB
//    variants), unmapped ones as 0; a written tile is uploaded again, an unmapped one reads 0 again.
// S2 the shape of Mario Kart World's virtual textures (8192×8192×68 ASTC 8×8 sRGB, 12 levels): created with no memory, a handful of tiles mapped:
//    the device's allocated size grows by those tiles, not by the texture's 1.45 GB.
// S3 tiles with the same physical memory behind them (in one texture, and in two textures of one format) share a heap page and read the same texels;
//    a tile whose memory moves takes a page of its own with its new texels; freeing both textures gives every page back.
#import <Metal/Metal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "../nvn_sparse_tex.h"
#include "../nvn_deswizzle.h"

static int checks, fails;
#define CHECK(c, ...) do { checks++; if (!(c)) { fails++; printf("  ✗ "); printf(__VA_ARGS__); printf("\n"); } } while (0)

// The fake guest: storage, a mapped flag and a written flag per 64 KB, and (S3) the physical memory behind each 64 KB (0: none known)
typedef struct { uint8_t *mem; uint64_t size; uint8_t *map, *wr; uint64_t *phys; } guest;
static int g_mapped(void *c, uint64_t a, uint64_t n) { guest *g = c; for (uint64_t p = a >> 16; p < (a + n + 0xffff) >> 16; p++) if (!g->map[p]) return 0; return 1; }
static int g_changed(void *c, uint64_t a, uint64_t n) { guest *g = c; int w = 0; for (uint64_t p = a >> 16; p < (a + n + 0xffff) >> 16; p++) w |= g->wr[p]; return w; }
static void g_copy(void *c, void *dst, uint64_t a, uint64_t n) { guest *g = c; for (uint64_t i = 0; i < n; i++) ((uint8_t *)dst)[i] = g->map[(a + i) >> 16] ? g->mem[a + i] : 0; }
static uint64_t g_backing(void *c, uint64_t a) { guest *g = c; return g->phys[a >> 16]; }
static void g_zero(void *c, void *dst, uint64_t a, uint64_t n) { (void)c; (void)a; memset(dst, 0, n); }   // S2: a guest of zeros, no storage

typedef struct { id<MTLBuffer> b; size_t used; id<MTLResidencySet> rs; __unsafe_unretained id<MTL4ComputeCommandEncoder> enc; } host;
static id<MTLBuffer> h_stage(void *c, size_t n, size_t *off) { host *h = c; size_t o = (h->used + 255) & ~(size_t)255; if (o + n > h->b.length) return nil; h->used = o + n; *off = o; return h->b; }
static void h_resident(void *c, id<MTLAllocation> a) { host *h = c; [h->rs addAllocation:a]; [h->rs commit]; }
static id<MTL4ComputeCommandEncoder> h_encoder(void *c) { return ((host *)c)->enc; }

static id<MTLDevice> dev; static id<MTL4CommandQueue> q; static id<MTL4CommandAllocator> al; static id<MTLSharedEvent> ev; static uint64_t evv;
static void run(void (^enc)(id<MTL4ComputeCommandEncoder>));
static int64_t sync_in(nvn_stex *s, const nvn_stex_guest *g, const nvn_stex_host *h) {   // one sync in a command buffer of its own, waited for
    __block int64_t up = 0;
    run(^(id<MTL4ComputeCommandEncoder> ce) { ((host *)h->ctx)->enc = ce; up = nvn_stex_sync(s, q, g, h, 0); });
    return up;
}
static void run(void (^enc)(id<MTL4ComputeCommandEncoder>)) {   // one command buffer, waited for
    id<MTL4CommandBuffer> cb = [dev newCommandBuffer]; [al reset]; [cb beginCommandBufferWithAllocator:al];
    id<MTL4ComputeCommandEncoder> ce = [cb computeCommandEncoder]; enc(ce); [ce endEncoding]; [cb endCommandBuffer];
    [q commit:&cb count:1]; [q signalEvent:ev value:++evv]; [ev waitUntilSignaledValue:evv timeoutMS:10000];
}
static uint8_t *readback(id<MTLTexture> t, id<MTLResidencySet> rs, unsigned slice, unsigned level, size_t pitch, uint32_t w, uint32_t h) {   // texels of one level, linear
    id<MTLBuffer> b = [dev newBufferWithLength:pitch * h options:MTLResourceStorageModeShared]; [rs addAllocation:b]; [rs commit];
    run(^(id<MTL4ComputeCommandEncoder> ce) { [ce copyFromTexture:t sourceSlice:slice sourceLevel:level sourceOrigin:MTLOriginMake(0, 0, 0) sourceSize:MTLSizeMake(w, h, 1)
                                                          toBuffer:b destinationOffset:0 destinationBytesPerRow:pitch destinationBytesPerImage:pitch * h]; });
    uint8_t *r = malloc(pitch * h); memcpy(r, b.contents, pitch * h); [rs removeAllocation:b]; [rs commit]; return r;
}

static void s1(int s2) {
    nvn_gob_s2 = s2;
    nvn_texdesc d = {4, 0x25, 0x20, 0, 1024, 600, 3, 11, 0, 0};   // RGBA8 2D array, SPARSE: 64 KB tiles of 128×128 texels, 3 tiled levels then the tail
    nvn_texlayout L; nvn_tex_layout(&d, &L);
    guest g = {calloc(1, L.size), L.size, calloc(L.size >> 16, 1), calloc(L.size >> 16, 1)};
    uint8_t *ref[3][11] = {{0}};
    for (unsigned z = 0; z < 3; z++) for (unsigned l = 0; l < 11; l++) {
        uint32_t w = MAX(1024u >> l, 1u), h = MAX(600u >> l, 1u); size_t n = (size_t)w * 4 * h; ref[z][l] = malloc(n);
        for (size_t i = 0; i < n; i++) ref[z][l][i] = (uint8_t)(i * 31 + z * 7 + l * 13 + (i >> 12));
        nvn_swizzle(&d, l, z, ref[z][l], (size_t)w * 4, g.mem);
    }
    // layer 0 all mapped, layer 1 every other 64 KB, layer 2 nothing but its mip tail
    for (uint64_t p = 0; p < L.size >> 16; p++) { uint64_t a = p << 16; unsigned z = (unsigned)(a / L.layer_size); g.map[p] = z == 0 || (z == 1 && (p & 1)); }
    uint64_t tail = nvn_tex_level_offset(&d, 3); for (uint64_t a = 2 * L.layer_size + tail; a < 3 * L.layer_size; a += 0x10000) g.map[a >> 16] = 1;
    MTLTextureDescriptor *td = [MTLTextureDescriptor new]; td.textureType = MTLTextureType2DArray; td.pixelFormat = MTLPixelFormatRGBA8Unorm;
    td.width = 1024; td.height = 600; td.arrayLength = 3; td.mipmapLevelCount = 11; td.usage = MTLTextureUsageShaderRead;
    CHECK(nvn_stex_wanted(dev, &d, td.pixelFormat, 0), "S1 RGBA8 sparse array not taken");
    nvn_stex *s = nvn_stex_new(dev, td, &d); CHECK(s != NULL, "S1 nvn_stex_new failed"); if (!s) return;
    id<MTLResidencySet> rs = [dev newResidencySetWithDescriptor:[MTLResidencySetDescriptor new] error:nil];
    host h = {[dev newBufferWithLength:64 << 20 options:MTLResourceStorageModeShared], 0, rs};
    [rs addAllocation:h.b]; [rs addAllocation:nvn_stex_texture(s)]; [rs commit]; [q addResidencySet:rs];
    nvn_stex_guest gg = {&g, 0, g_mapped, g_changed, g_copy}; nvn_stex_host hh = {&h, h_stage, h_encoder, h_resident};
    int64_t up = sync_in(s, &gg, &hh);
    CHECK(up > 0, "S1 first sync uploaded %lld bytes", (long long)up);
    int bad = 0;
    for (unsigned z = 0; z < 3; z++) for (unsigned l = 0; l < 11; l++) {
        uint32_t w = MAX(1024u >> l, 1u), hgt = MAX(600u >> l, 1u); size_t pitch = (size_t)w * 4;
        uint8_t *got = readback(nvn_stex_texture(s), rs, z, l, pitch, w, hgt);
        for (uint32_t y = 0; y < hgt; y++) for (uint32_t x = 0; x < w; x++) {
            uint64_t a = (uint64_t)z * L.layer_size + nvn_tex_level_offset(&d, l) + (l < 3 ? ((uint64_t)(y / 128) * ((w + 127) / 128) + x / 128) << 16 : 0);
            int m = g.map[a >> 16]; const uint8_t *want = m ? ref[z][l] + (size_t)y * pitch + x * 4 : (const uint8_t[4]){0};
            if (memcmp(got + (size_t)y * pitch + x * 4, want, 4)) { if (bad++ < 3) printf("  layer %u level %u texel (%u,%u) mapped %d differs\n", z, l, x, y, m); }
        }
        free(got);
    }
    CHECK(!bad, "S1%s: %d texels differ from the guest's (mapped) or 0 (unmapped)", s2 ? " Switch 2 GOB" : "", bad);
    // write one tile of layer 0 level 0 (tile (2, 1)) and unmap one of layer 1: the next sync uploads the first and drops the second
    uint32_t tx0 = 2, ty0 = 1; uint64_t ta = (uint64_t)(ty0 * 8 + tx0) << 16;
    for (uint32_t y = 0; y < 128; y++) for (uint32_t x = 0; x < 128; x++) ref[0][0][(size_t)(ty0 * 128 + y) * 4096 + (tx0 * 128 + x) * 4] ^= 0xff;
    nvn_swizzle(&d, 0, 0, ref[0][0], 4096, g.mem); g.wr[ta >> 16] = 1;
    uint64_t ub = L.layer_size + (1ull << 16); g.map[ub >> 16] = 0;   // layer 1, level 0 tile 1 (mapped before: odd page)
    h.used = 0;
    up = sync_in(s, &gg, &hh);
    CHECK(up == 0x10000, "S1 second sync uploaded %lld bytes, expected one tile", (long long)up);
    uint8_t *got = readback(nvn_stex_texture(s), rs, 0, 0, 4096, 1024, 600);
    CHECK(!memcmp(got + (size_t)(ty0 * 128) * 4096 + tx0 * 512, ref[0][0] + (size_t)(ty0 * 128) * 4096 + tx0 * 512, 512), "S1 the written tile was not uploaded again");
    free(got); got = readback(nvn_stex_texture(s), rs, 1, 0, 4096, 1024, 600);
    int zero = 1; for (uint32_t y = 0; y < 128; y++) for (uint32_t x = 128; x < 256; x++) zero &= !*(uint32_t *)(got + (size_t)y * 4096 + x * 4);
    CHECK(zero, "S1 the unmapped tile does not read 0");
    free(got);
    nvn_stex_free(s, q);
    for (unsigned z = 0; z < 3; z++) for (unsigned l = 0; l < 11; l++) free(ref[z][l]);
    free(g.mem); free(g.map); free(g.wr);
    printf("  S1 %s GOB: mapped tiles and mip tails read the guest's texels, unmapped tiles 0, a written tile re-uploaded, an unmapped one 0\n", s2 ? "Switch 2" : "Switch 1");
}

static void s2(void) {
    nvn_texdesc d = {4, 0x8E, 0x20, 0, 8192, 8192, 68, 12, 0, 0};
    nvn_texlayout L; nvn_tex_layout(&d, &L);
    MTLTextureDescriptor *td = [MTLTextureDescriptor new]; td.textureType = MTLTextureType2DArray; td.pixelFormat = MTLPixelFormatASTC_8x8_sRGB;
    td.width = 8192; td.height = 8192; td.arrayLength = 68; td.mipmapLevelCount = 12; td.usage = MTLTextureUsageShaderRead;
    size_t a0 = dev.currentAllocatedSize;
    nvn_stex *s = nvn_stex_new(dev, td, &d); CHECK(s != NULL, "S2 8192×8192×68 ASTC 8×8 not created"); if (!s) return;
    size_t a1 = dev.currentAllocatedSize;
    // map 16 tiles of layer 5 level 0 and layer 5's mip tail; the guest data is 0 (the content is S1's business)
    guest g = {NULL, L.size, calloc(L.size >> 16, 1), calloc(L.size >> 16, 1)};
    for (unsigned i = 0; i < 16; i++) g.map[(5 * L.layer_size >> 16) + i] = 1;
    for (uint64_t a = 5 * L.layer_size + nvn_tex_level_offset(&d, 5); a < 6 * L.layer_size; a += 0x10000) g.map[a >> 16] = 1;
    id<MTLResidencySet> rs = [dev newResidencySetWithDescriptor:[MTLResidencySetDescriptor new] error:nil];
    host h = {[dev newBufferWithLength:8 << 20 options:MTLResourceStorageModeShared], 0, rs};
    [rs addAllocation:h.b]; [rs addAllocation:nvn_stex_texture(s)]; [rs commit]; [q addResidencySet:rs];
    nvn_stex_guest gg = {&g, 0, g_mapped, g_changed, g_zero}; nvn_stex_host hh = {&h, h_stage, h_encoder, h_resident};
    int64_t up = sync_in(s, &gg, &hh);
    size_t a2 = dev.currentAllocatedSize;
    printf("  S2 8192×8192×68 ASTC 8×8 sRGB (storage %.2f GB): created +%.1f MB, 16 tiles and a mip tail mapped and uploaded (%lld KB) +%.1f MB, mapped %.1f MB\n",
           L.size / 1073741824.0, (a1 - a0) / 1048576.0, (long long)up / 1024, (a2 - a1) / 1048576.0, nvn_stex_mapped_bytes() / 1048576.0);
    CHECK(a1 - a0 < (32u << 20), "S2 creating the sparse texture allocated %.1f MB", (a1 - a0) / 1048576.0);
    CHECK(a2 - a1 <= (160u << 20), "S2 mapping 17 tiles allocated %.1f MB (one 128 MB heap expected)", (a2 - a1) / 1048576.0);
    CHECK(nvn_stex_mapped_bytes() == 17 * 0x10000, "S2 %llu bytes mapped, expected 17 tiles", (unsigned long long)nvn_stex_mapped_bytes());
    nvn_stex_free(s, q);
    CHECK(nvn_stex_mapped_bytes() == 0, "S2 tiles still mapped after free");
    free(g.map); free(g.wr);
}

static void s3(void) {
    nvn_gob_s2 = 1;
    uint64_t p0 = nvn_stex_mapped_bytes(), t0 = nvn_stex_tile_bytes();
    // A: ASTC 8x8 4096x2560x2, one level (8x5 tiles of 512x512 texels, 64 rows of 1024 bytes each, a layer); B: ASTC 8x8 1024x1024 (2x2 tiles). Every 64 KB mapped
    enum { P = 8192, PB = 2048, TB = 1024, TR = 64, ROWS = 320 };
    nvn_texdesc da = {4, 0x8E, 0x20, 0, 4096, 2560, 2, 1, 0, 0}, db = {1, 0x8E, 0x20, 0, 1024, 1024, 1, 1, 0, 0};
    nvn_texlayout La, Lb; nvn_tex_layout(&da, &La); nvn_tex_layout(&db, &Lb);
    guest ga = {calloc(1, La.size), La.size, malloc(La.size >> 16), calloc(La.size >> 16, 1), calloc(La.size >> 16, 8)};
    guest gb = {calloc(1, Lb.size), Lb.size, malloc(Lb.size >> 16), calloc(Lb.size >> 16, 1), calloc(Lb.size >> 16, 8)};
    memset(ga.map, 1, La.size >> 16); memset(gb.map, 1, Lb.size >> 16);
    uint8_t *ref = malloc(P * ROWS); for (size_t i = 0; i < P * ROWS; i++) ref[i] = (uint8_t)(i * 29 + (i >> 11));
    nvn_swizzle(&da, 0, 0, ref, P, ga.mem); nvn_swizzle(&da, 0, 1, ref, P, ga.mem);   // both layers hold the same blocks
    // physical identity: A's tiles 1..n, except A layer 1 tile 0 = A layer 0 tile 0; B tile 0 = A layer 0 tile (2, 1) (its bytes copied there); B's other tiles of their own
    for (uint64_t p = 0; p < La.size >> 16; p++) ga.phys[p] = (p + 1) << 16;
    uint64_t l1 = La.layer_size >> 16, ta = 1 * 8 + 2; ga.phys[l1] = ga.phys[0];
    memcpy(gb.mem, ga.mem + (ta << 16), 0x10000); gb.phys[0] = ga.phys[ta];
    for (uint64_t p = 1; p < Lb.size >> 16; p++) gb.phys[p] = (0x1000 + p) << 16;
    MTLTextureDescriptor *tda = [MTLTextureDescriptor new]; tda.textureType = MTLTextureType2DArray; tda.pixelFormat = MTLPixelFormatASTC_8x8_sRGB; tda.width = 4096; tda.height = 2560; tda.arrayLength = 2; tda.usage = MTLTextureUsageShaderRead;
    MTLTextureDescriptor *tdb = [MTLTextureDescriptor new]; tdb.textureType = MTLTextureType2D; tdb.pixelFormat = MTLPixelFormatASTC_8x8_sRGB; tdb.width = 1024; tdb.height = 1024; tdb.usage = MTLTextureUsageShaderRead;
    nvn_stex *a = nvn_stex_new(dev, tda, &da), *b = nvn_stex_new(dev, tdb, &db); CHECK(a && b, "S3 sparse textures not created"); if (!a || !b) return;
    id<MTLResidencySet> rs = [dev newResidencySetWithDescriptor:[MTLResidencySetDescriptor new] error:nil];
    host h = {[dev newBufferWithLength:16 << 20 options:MTLResourceStorageModeShared], 0, rs};
    [rs addAllocation:h.b]; [rs addAllocation:nvn_stex_texture(a)]; [rs addAllocation:nvn_stex_texture(b)]; [rs commit]; [q addResidencySet:rs];
    nvn_stex_guest gga = {&ga, 0, g_mapped, g_changed, g_copy, g_backing}, ggb = {&gb, 0, g_mapped, g_changed, g_copy, g_backing}; nvn_stex_host hh = {&h, h_stage, h_encoder, h_resident};
    sync_in(a, &gga, &hh); h.used = 0; sync_in(b, &ggb, &hh); h.used = 0;
    uint64_t tiles = (La.size >> 16) + (Lb.size >> 16);
    CHECK(nvn_stex_tile_bytes() - t0 == tiles << 16, "S3 %llu tiles mapped, expected %llu", (unsigned long long)((nvn_stex_tile_bytes() - t0) >> 16), (unsigned long long)tiles);
    CHECK(nvn_stex_mapped_bytes() - p0 == (tiles - 2) << 16, "S3 %llu heap pages, expected %llu (two tiles share)", (unsigned long long)((nvn_stex_mapped_bytes() - p0) >> 16), (unsigned long long)(tiles - 2));
    #define RA(z) readback(nvn_stex_texture(a), rs, z, 0, P, 4096, 2560)
    #define RB() readback(nvn_stex_texture(b), rs, 0, 0, PB, 1024, 1024)
    #define TILE_EQ(got, pitch, gx, gy, want, wx, wy) ({ int eq_ = 1; for (uint32_t y_ = 0; y_ < TR; y_++) eq_ &= !memcmp((got) + (size_t)((gy) * TR + y_) * (pitch) + (gx) * TB, (want) + (size_t)((wy) * TR + y_) * P + (wx) * TB, TB); eq_; })
    uint8_t *ra0 = RA(0), *ra1 = RA(1), *rb = RB();
    CHECK(!memcmp(ra0, ref, P * ROWS) && !memcmp(ra1, ref, P * ROWS), "S3 a shared tile reads other blocks in A");
    CHECK(TILE_EQ(rb, PB, 0, 0, ref, 2, 1), "S3 B's tile shared with A's tile (2, 1) of another texture reads other blocks");
    free(ra0); free(ra1); free(rb);
    // the memory behind A's tile (2, 1) is written; only A syncs: B reads the new blocks through the page they share
    for (size_t y = TR; y < 2 * TR; y++) for (size_t x = 2 * TB; x < 3 * TB; x++) ref[y * P + x] ^= 0x33;
    nvn_swizzle(&da, 0, 0, ref, P, ga.mem); ga.wr[ta] = 1; sync_in(a, &gga, &hh); h.used = 0; ga.wr[ta] = 0;
    rb = RB(); CHECK(TILE_EQ(rb, PB, 0, 0, ref, 2, 1), "S3 B does not see the blocks A uploaded into the page they share"); free(rb);
    // the same within A: layer 0 tile 0 is written and uploaded, layer 1 tile 0 (the same memory, not written per its own flag) reads it
    for (size_t y = 0; y < TR; y++) for (size_t x = 0; x < TB; x++) ref[y * P + x] ^= 0x44;
    nvn_swizzle(&da, 0, 0, ref, P, ga.mem); memcpy(ga.mem + La.layer_size, ga.mem, 0x10000); ga.wr[0] = 1; sync_in(a, &gga, &hh); h.used = 0; ga.wr[0] = 0;
    ra1 = RA(1); CHECK(TILE_EQ(ra1, P, 0, 0, ref, 0, 0), "S3 A layer 1 tile 0 does not see the blocks uploaded through layer 0 tile 0, the page they share"); free(ra1);
    // A layer 1 tile 0's memory moves (new physical page, other blocks): it leaves the shared page for one of its own
    uint8_t *old = malloc(P * ROWS); memcpy(old, ref, P * ROWS);
    for (size_t y = 0; y < TR; y++) for (size_t x = 0; x < TB; x++) ref[y * P + x] ^= 0x5a;
    uint8_t *lay1 = calloc(1, La.layer_size); nvn_swizzle(&da, 0, 0, ref, P, lay1); memcpy(ga.mem + La.layer_size, lay1, 0x10000); free(lay1);
    ga.phys[l1] = 0x7777ull << 16; ga.wr[l1] = 1;
    sync_in(a, &gga, &hh); h.used = 0; ga.wr[l1] = 0;
    CHECK(nvn_stex_mapped_bytes() - p0 == (tiles - 1) << 16, "S3 %llu heap pages after the move, expected %llu", (unsigned long long)((nvn_stex_mapped_bytes() - p0) >> 16), (unsigned long long)(tiles - 1));
    ra0 = RA(0); ra1 = RA(1);
    CHECK(TILE_EQ(ra1, P, 0, 0, ref, 0, 0) && TILE_EQ(ra0, P, 0, 0, old, 0, 0), "S3 after the move, A layer 1 tile 0 does not read its new blocks, or layer 0 tile 0 changed with it");
    free(ra0); free(ra1); free(old);
    #undef RA
    #undef RB
    #undef TILE_EQ
    nvn_stex_free(a, q); nvn_stex_free(b, q);
    CHECK(nvn_stex_mapped_bytes() == p0 && nvn_stex_tile_bytes() == t0, "S3 pages or tiles left after free");
    // an uncompressed format keeps a page per tile (its Metal texture may be losslessly compressed, whose tiles cannot be shared): RGBA8 tiles with one memory behind them
    nvn_texdesc dc = {1, 0x25, 0x20, 0, 256, 256, 1, 1, 0, 0}; nvn_texlayout Lc; nvn_tex_layout(&dc, &Lc);
    guest gc = {calloc(1, Lc.size), Lc.size, malloc(Lc.size >> 16), calloc(Lc.size >> 16, 1), calloc(Lc.size >> 16, 8)};
    memset(gc.map, 1, Lc.size >> 16); for (uint64_t p = 0; p < Lc.size >> 16; p++) gc.phys[p] = 0x10000;
    MTLTextureDescriptor *tdc = [MTLTextureDescriptor new]; tdc.pixelFormat = MTLPixelFormatRGBA8Unorm; tdc.width = 256; tdc.height = 256; tdc.usage = MTLTextureUsageShaderRead;
    nvn_stex *c = nvn_stex_new(dev, tdc, &dc); CHECK(c != NULL, "S3 RGBA8 sparse texture not created");
    if (c) { [rs addAllocation:nvn_stex_texture(c)]; [rs commit]; nvn_stex_guest ggc = {&gc, 0, g_mapped, g_changed, g_copy, g_backing}; sync_in(c, &ggc, &hh); h.used = 0;
        CHECK(nvn_stex_mapped_bytes() - p0 == Lc.size, "S3 RGBA8 tiles shared a page (%llu pages for %llu tiles)", (unsigned long long)((nvn_stex_mapped_bytes() - p0) >> 16), (unsigned long long)(Lc.size >> 16));
        nvn_stex_free(c, q); }
    free(ref); free(ga.mem); free(ga.map); free(ga.wr); free(ga.phys); free(gb.mem); free(gb.map); free(gb.wr); free(gb.phys); free(gc.mem); free(gc.map); free(gc.wr); free(gc.phys);
    printf("  S3 ASTC 8x8: %llu tiles on %llu heap pages, shared tiles read the same blocks in one texture and across two, a moved tile gets a page of its own; RGBA8 tiles keep their own pages\n", (unsigned long long)tiles, (unsigned long long)(tiles - 2));
}

int main(void) {
    @autoreleasepool {
        dev = MTLCreateSystemDefaultDevice(); q = [dev newMTL4CommandQueue]; al = [dev newCommandAllocator]; ev = [dev newSharedEvent];
        printf("device %s\n", dev.name.UTF8String);
        printf("S1 sparse 2D array: tiles mapped where the guest maps memory\n"); s1(0); s1(1);
        printf("S2 Mario Kart World's virtual texture shape\n"); s2();
        printf("S3 tiles with one memory behind them share a heap page\n"); s3();
        printf("%s: %d/%d checks passed\n", fails ? "RED" : "all passed", checks - fails, checks);
        return fails ? 1 : 0;
    }
}
