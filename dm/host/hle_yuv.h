// 4:2:0 picture → NV12 or RGBA in pitch-linear memory: the movie decoders' output conversions (hle_movie.c: movie::Decoder, pitch = width; hle_codec_video.c: nn::codec decoder images, pitch from the image layout)
#pragma once
#include <arm_neon.h>
#include <stdint.h>
#include <string.h>

// 4:2:0 8-bit planes; bt709: the picture's matrix (else BT.601); cstep: bytes between a chroma plane's samples, 1 planar (I420), 2 interleaved (NV12: plane[2] = plane[1] + 1)
typedef struct { const uint8_t *plane[3]; int stride[3]; int w, h; int full_range, bt709, cstep; } yuv_pic;

#ifdef VPX_VPX_VPX_IMAGE_H_
// a libvpx picture (I420); out_h: the output's height, for the matrix of a picture that declares none (BT.709 from 720 lines up, else BT.601)
static inline yuv_pic yuv_from_vpx(const vpx_image_t *im, int out_h) {
    yuv_pic p = { { im->planes[0], im->planes[1], im->planes[2] }, { im->stride[0], im->stride[1], im->stride[2] }, (int)im->d_w, (int)im->d_h,
                  im->range == VPX_CR_FULL_RANGE, im->cs == VPX_CS_BT_709 || (im->cs == VPX_CS_UNKNOWN && out_h >= 720), 1 };
    return p;
}
#endif
// NV12: luma rows of w bytes at y + r * ypitch, interleaved U/V rows at uv + r * uvpitch; a smaller picture is padded with video black, a larger one cropped
static inline void yuv_to_nv12(const yuv_pic *p, uint8_t *y, size_t ypitch, uint8_t *uv, size_t uvpitch, int w, int h) {
    int cw = p->w < w ? p->w : w, ch = p->h < h ? p->h : h;
    if (cw < w || ch < h) { for (int r = 0; r < h; r++) memset(y + (size_t)r * ypitch, 16, (size_t)w); for (int r = 0; r < h / 2; r++) memset(uv + (size_t)r * uvpitch, 128, (size_t)w); }
    for (int r = 0; r < ch; r++) memcpy(y + (size_t)r * ypitch, p->plane[0] + (size_t)r * p->stride[0], (size_t)cw);
    int cw2 = (cw + 1) / 2, ch2 = (ch + 1) / 2;
    for (int r = 0; r < ch2 && r < h / 2; r++) {
        const uint8_t *u = p->plane[1] + (size_t)r * p->stride[1], *v = p->plane[2] + (size_t)r * p->stride[2];
        uint8_t *o = uv + (size_t)r * uvpitch;
        if (p->cstep == 2) { memcpy(o, u, (size_t)(2 * cw2 <= w ? 2 * cw2 : w)); continue; }   // already interleaved
        int x = 0;
        for (; x + 16 <= cw2; x += 16) { uint8x16x2_t t = { { vld1q_u8(u + x), vld1q_u8(v + x) } }; vst2q_u8(o + 2 * x, t); }
        for (; x < cw2 && 2 * x + 1 < w; x++) { o[2 * x] = u[x]; o[2 * x + 1] = v[x]; }
    }
}
// RGBA (bgra: B, G, R, A byte order) rows of w pixels at dst + r * pitch, by the picture's matrix and range, 16-bit fixed point; a smaller picture is padded with black, a larger one cropped
static inline void yuv_to_rgba(const yuv_pic *p, uint8_t *dst, size_t pitch, int w, int h, int bgra) {
    int full = p->full_range, b709 = p->bt709;
    double ky = full ? 1.0 : 255.0 / 219.0, kc = full ? 1.0 : 255.0 / 224.0;
    double rv = b709 ? 1.5748 : 1.402, gu = b709 ? 0.1873 : 0.3441, gv = b709 ? 0.4681 : 0.7141, bu = b709 ? 1.8556 : 1.772;
    int Y = (int)(ky * 65536), RV = (int)(rv * kc * 65536), GU = (int)(gu * kc * 65536), GV = (int)(gv * kc * 65536), BU = (int)(bu * kc * 65536), y0 = full ? 0 : 16;
    int cw = p->w < w ? p->w : w, ch = p->h < h ? p->h : h, ri = bgra ? 2 : 0, bi = bgra ? 0 : 2, cs = p->cstep == 2 ? 2 : 1;
    if (cw < w || ch < h) for (int r = 0; r < h; r++) for (int x = 0; x < w; x++) ((uint32_t *)(dst + (size_t)r * pitch))[x] = 0xff000000u;
    #define YUV_CL(v) ((v) < 0 ? 0 : (v) > 255 ? 255 : (v))
    for (int r = 0; r < ch; r++) {
        const uint8_t *py = p->plane[0] + (size_t)r * p->stride[0], *pu = p->plane[1] + (size_t)(r >> 1) * p->stride[1], *pv = p->plane[2] + (size_t)(r >> 1) * p->stride[2];
        uint8_t *o = dst + (size_t)r * pitch;
        for (int x = 0; x < cw; x++) {
            int l = (py[x] - y0) * Y, u = pu[(x >> 1) * cs] - 128, v = pv[(x >> 1) * cs] - 128;
            int cr = (l + RV * v + 32768) >> 16, cg = (l - GU * u - GV * v + 32768) >> 16, cb = (l + BU * u + 32768) >> 16;
            o[4 * x + ri] = (uint8_t)YUV_CL(cr); o[4 * x + 1] = (uint8_t)YUV_CL(cg); o[4 * x + bi] = (uint8_t)YUV_CL(cb); o[4 * x + 3] = 255;
        }
    }
    #undef YUV_CL
}
