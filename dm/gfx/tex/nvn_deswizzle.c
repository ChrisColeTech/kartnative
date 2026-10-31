//     (x%64/32)·256 + (y%8/2)·64 + (x%32/16)·32 + (y%2)·16 + x%16
// Tegra X1 block-linear layout (public descriptions: deko3d / Ryujinx BlockLinearLayout):
//   GOB = 64 bytes × 8 rows = 512 bytes; offset of (x byte, y row) within a GOB =
//     (x%64/32)·256 + (y%8/2)·64 + (x%32/16)·32 + (y%2)·16 + x%16
//   block = 1 GOB wide × 2^tileH GOBs high × 2^tileD GOBs deep, ordered y first then z within a block; blocks fill the level in x → y → z row-major order.
// Switch 2 (nvn_gob_s2): the same blocks and GOBs, with bits 5 and 6 of the in-GOB offset exchanged, so bit 5 holds y%4/2 and bit 6 holds x%32/16:
//     (x%64/32)·256 + (y%8/4)·128 + (x%32/16)·64 + (y%4/2)·32 + (y%2)·16 + x%16
//   Found by decoding Mario Kart World's packaged textures (R16, BC4, ASTC 8×8 and 6×6) both ways: only the exchanged order gives whole pictures,
//   for every format (docs/reference/nvn-objects.md#switch-2-block-linear-gobs).
// Level / layer start offsets and the actual block height and block depth of this level come from nvn_tex.c; here we only do intra-level addressing and copying.
#include "nvn_deswizzle.h"
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int nvn_gob_s2;

static uint32_t gob_off(uint32_t x, uint32_t y) {
    if (nvn_gob_s2) return (x % 64 / 32) * 256 + (y % 8 / 4) * 128 + (x % 32 / 16) * 64 + (y % 4 / 2) * 32 + (y % 2) * 16 + x % 16;
    return (x % 64 / 32) * 256 + (y % 8 / 2) * 64 + (x % 32 / 16) * 32 + (y % 2) * 16 + x % 16;
}

uint64_t nvn_bl_offset(uint32_t x, uint32_t y, uint32_t z, uint32_t row_bytes, uint32_t rows, unsigned th, unsigned td) {
    uint32_t bh = 1u << th, bd = 1u << td;
    uint64_t gobs_x = (row_bytes + 63) / 64, blocks_y = (rows + 8 * bh - 1) / (8 * bh);
    uint64_t block = 512ull * bh * bd;                                  // bytes in one block
    return (z / bd) * gobs_x * blocks_y * block                        // which block layer (along depth)
         + (y / (8 * bh)) * gobs_x * block                             // which block row
         + (x / 64) * block                                            // which block column
         + ((z % bd) * bh + (y % (8 * bh)) / 8) * 512ull               // which GOB within the block
         + gob_off(x, y);
}

static uint32_t mip(uint32_t s, unsigned l) { s >>= l; return s ? s : 1; }

// Geometry of this level: width-in-blocks byte count, block row count, slice count, and where this level and layer starts in storage; returns -1 for unsupported textures
int nvn_deswizzle_geom(const nvn_texdesc *d, unsigned level, unsigned layer,
                       uint32_t *row_bytes, uint32_t *rows, uint32_t *slices, uint64_t *base, unsigned *th, unsigned *td, uint32_t *bpb) {
    if (d->target == 5 || d->target == 6 || d->target == 10 || (d->flags & 0x50) || d->samples > 1) return -1;   // MS / BUFFER / LINEAR / LINEAR_RENDER_TARGET
    int bw, bh; *bpb = (uint32_t)nvn_fmt_bpb(d->format, &bw, &bh);
    if (!*bpb || level >= (d->levels ? d->levels : 1)) return -1;
    nvn_texlayout L; nvn_tex_layout(d, &L);
    if (layer >= L.layers) return -1;
    int one_d = d->target == 0 || d->target == 3;
    uint32_t w = mip(d->width, level), h = one_d ? 1 : mip(d->height, level);
    *slices = d->target == 2 ? mip(d->depth ? d->depth : 1, level) : 1;
    *row_bytes = (w + bw - 1) / bw * *bpb; *rows = (h + bh - 1) / bh;
    *base = (uint64_t)layer * L.layer_size + nvn_tex_level_offset(d, level);
    nvn_tex_level_tiles(d, level, th, td);
    return 0;
}

static int move(const nvn_texdesc *d, unsigned level, unsigned layer, uint8_t *bl, uint8_t *lin, size_t pitch, int to_linear) {
    uint32_t row_bytes, rows, slices, bpb; uint64_t base; unsigned th, td;
    if (nvn_deswizzle_geom(d, level, layer, &row_bytes, &rows, &slices, &base, &th, &td, &bpb)) return -1;
    if (pitch < row_bytes) return -1;
    bl += base; uint32_t lb = nvn_tex_level_gobs(d, level) * 64;   // the row as laid out (padded in a sparse texture's tiled levels)
    if (lb < row_bytes) lb = row_bytes;
    // Hoist nvn_bl_offset's slice and row terms out of the segment loop. The block height and depth are powers of two,
    // so their division / remainder become shifts and masks. At x = 16*j the block column is j >> 2, and the segment
    // has one of four positions inside the GOB (x%64 = 0/16/32/48 -> 0/32/256/288); the y part depends only on y%8.
    // The Switch 2 GOB (gob_off) moves x%32/16 to bit 6 and y%4/2 to bit 5: 0/64/256/320 and (y%8/4)*128 + (y%4/2)*32 + (y%2)*16.
    static const uint16_t kox1[4] = {0, 32, 256, 288}, kox2[4] = {0, 64, 256, 320};                                         // x%16 is always 0 here: segments start at multiples of 16
    static const uint16_t koy1[8] = {0, 16, 64, 80, 128, 144, 192, 208}, koy2[8] = {0, 16, 32, 48, 128, 144, 160, 176};   // (y%8/2)*64 + (y%2)*16
    const uint16_t *kox = nvn_gob_s2 ? kox2 : kox1, *koy = nvn_gob_s2 ? koy2 : koy1;
    uint32_t bh = 1u << th, bd = 1u << td;
    uint64_t block = 512ull * bh * bd, gobs_x = (uint64_t)(lb + 63) >> 6, blocks_y = ((uint64_t)rows + 8 * bh - 1) >> (3 + th);
    for (uint32_t z = 0; z < slices; z++) {
        uint64_t zb = (uint64_t)(z >> td) * gobs_x * blocks_y * block + (uint64_t)(z & (bd - 1)) * bh * 512;
        for (uint32_t y = 0; y < rows; y++) {
            uint64_t p = (uint64_t)(y >> (3 + th)) * gobs_x * block + (uint64_t)((y & (8 * bh - 1)) >> 3) * 512 + koy[y & 7];
            uint8_t *row = lin + ((size_t)z * rows + y) * pitch;
            uint64_t yb = zb + p;   // the row's segments: block column (j >> 2) at yb + j>>2 times block + kox[j & 3]
            uint32_t nfull = row_bytes >> 4, tail = row_bytes & 15;
            // The full 16-byte segments take a constant-size copy (a memcpy of a runtime length is a call per segment);
            // a row whose byte count is not divisible by 16 has a tail (e.g. R8 or RGB formats at an odd width).
            if (to_linear) {
                for (uint32_t j = 0; j < nfull; j++) memcpy(row + (j << 4), bl + yb + ((uint64_t)(j >> 2) * block) + kox[j & 3], 16);
                if (tail) memcpy(row + (nfull << 4), bl + yb + ((uint64_t)(nfull >> 2) * block) + kox[nfull & 3], tail);
            } else {
                for (uint32_t j = 0; j < nfull; j++) memcpy(bl + yb + ((uint64_t)(j >> 2) * block) + kox[j & 3], row + (j << 4), 16);
                if (tail) memcpy(bl + yb + ((uint64_t)(nfull >> 2) * block) + kox[nfull & 3], row + (nfull << 4), tail);
            }
        }
    }
    return 0;
}

// DM_NVN_UPLOADDUMP=<dir>[:<n>] (diagnostic): every level 0, layer 0 the CPU deswizzles (texture uploads, ReadTexels) is also written to <dir>/<k>_f<format>_<w>x<h>.bin,
// up to n files (default 200): one text line "nvn format w h depth bpb bw bh pitch rows slices flags target packaged\n", then the linear block rows as passed to Metal.
// Turned into pictures by a decoder of the texture's Metal format (to check a layout on a game's own data, docs/reference/nvn-objects.md#switch-2-block-linear-gobs)
static const char *g_dump_dir; static _Atomic int g_dump_max = -1, g_dump_n;
int nvn_upload_dumping(void) {
    if (g_dump_max < 0) { const char *e = getenv("DM_NVN_UPLOADDUMP"); static char b[1024]; int m = 0;
        if (e && *e) { snprintf(b, sizeof b, "%s", e); char *c = strrchr(b, ':'); m = 200; if (c && c[1] >= '0' && c[1] <= '9') { *c = 0; m = atoi(c + 1); } g_dump_dir = b; }
        g_dump_max = m; }
    return g_dump_dir != NULL;
}
static void upload_dump(const nvn_texdesc *d, const uint8_t *lin, size_t pitch) {
    if (!nvn_upload_dumping()) return;
    int k = g_dump_n++; if (k >= g_dump_max) return;
    uint32_t rb, rows, slices, bpb; uint64_t base; unsigned th, td; int bw, bh;
    if (nvn_deswizzle_geom(d, 0, 0, &rb, &rows, &slices, &base, &th, &td, &bpb)) return;
    nvn_fmt_bpb(d->format, &bw, &bh);
    char p[1200]; snprintf(p, sizeof p, "%s/%04d_f%02x_%ux%u.bin", g_dump_dir, k, (unsigned)d->format, d->width, d->height);
    FILE *f = fopen(p, "wb"); if (!f) return;
    fprintf(f, "nvn %#x %u %u %u %u %d %d %zu %u %u %#x %d %d\n", (unsigned)d->format, d->width, d->height, d->depth, bpb, bw, bh, pitch, rows, slices, (unsigned)d->flags, d->target, d->pkg_block_h);
    fwrite(lin, 1, pitch * rows * slices, f); fclose(f);
}

int nvn_deswizzle(const nvn_texdesc *d, unsigned level, unsigned layer, const void *src, void *dst, size_t dst_pitch) {
    int r = move(d, level, layer, (uint8_t *)src, (uint8_t *)dst, dst_pitch, 1);
    if (!r && !level && !layer) upload_dump(d, dst, dst_pitch);
    return r;
}
int nvn_swizzle(const nvn_texdesc *d, unsigned level, unsigned layer, const void *src, size_t src_pitch, void *dst) {
    return move(d, level, layer, (uint8_t *)dst, (uint8_t *)src, src_pitch, 0);
}
