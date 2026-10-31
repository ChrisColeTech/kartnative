// Turns DM_NVN_UPLOADDUMP files (nvn_deswizzle.c) into PNGs: the dump's linear level is put into a Metal texture of the format the host maps the NVN
// format to (nvn_fmt_to_mtl, so BC and ASTC decode as the GPU samples them, with the format's alpha remapping) and read back as RGBA8 (slice 0).
// Integer formats and formats with no Metal counterpart are skipped. Headless (no window).
//   clang -fobjc-arc -O1 -o /tmp/uploaddump_png dm/gfx/tex/tools/uploaddump_png.m dm/gfx/tex/nvn_fmt_mtl.c -framework Metal -framework Foundation -framework ImageIO -framework CoreGraphics
//   /tmp/uploaddump_png <out dir> <dump>.bin...
#import <Metal/Metal.h>
#import <ImageIO/ImageIO.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "../nvn_fmt_mtl.h"

static const char *kSrc = "#include <metal_stdlib>\nusing namespace metal;\n"
    "kernel void k(texture2d<float, access::sample> t [[texture(0)]], device uchar4 *o [[buffer(0)]], uint2 g [[thread_position_in_grid]]) {\n"
    "  if (g.x >= t.get_width() || g.y >= t.get_height()) return;\n"
    "  constexpr sampler s(coord::pixel, filter::nearest);\n"
    "  o[g.y * t.get_width() + g.x] = uchar4(clamp(t.sample(s, float2(g) + 0.5), 0.0, 1.0) * 255.0 + 0.5); }\n";

int main(int argc, char **argv) {
    @autoreleasepool {
        if (argc < 3) { fprintf(stderr, "usage: %s <out dir> <dump.bin>...\n", argv[0]); return 2; }
        id<MTLDevice> dev = MTLCreateSystemDefaultDevice(); NSError *e = nil;
        id<MTLLibrary> lib = [dev newLibraryWithSource:@(kSrc) options:nil error:&e];
        id<MTLComputePipelineState> ps = lib ? [dev newComputePipelineStateWithFunction:[lib newFunctionWithName:@"k"] error:&e] : nil;
        if (!ps) { fprintf(stderr, "pipeline: %s\n", e.description.UTF8String); return 1; }
        id<MTLCommandQueue> q = [dev newCommandQueue]; int bad = 0;
        for (int i = 2; i < argc; i++) {
            FILE *f = fopen(argv[i], "rb"); char line[256]; unsigned fmt, w, h, dep, bpb, rows, sl; int bw, bh; size_t pitch;
            if (!f || !fgets(line, sizeof line, f) || sscanf(line, "nvn %x %u %u %u %u %d %d %zu %u %u", &fmt, &w, &h, &dep, &bpb, &bw, &bh, &pitch, &rows, &sl) != 10) { fprintf(stderr, "%s: not a dump\n", argv[i]); if (f) fclose(f); bad++; continue; }
            size_t n = pitch * rows; uint8_t *b = malloc(n); size_t got = fread(b, 1, n, f); fclose(f);
            uint32_t pf; uint8_t swz[4]; int kind = nvn_fmt_to_mtl((int)fmt, &pf, swz);
            if (got != n || !kind || (pf >= 13 && pf <= 14) || (pf >= 23 && pf <= 24) || (pf >= 33 && pf <= 34) || (pf >= 53 && pf <= 54) || (pf >= 63 && pf <= 64) || pf == 73 || pf == 74
                || pf == 91 || (pf >= 103 && pf <= 104) || (pf >= 113 && pf <= 114) || (pf >= 123 && pf <= 124) || pf >= 250) {   // short file, no counterpart, integer or depth formats
                printf("%s: format %#x skipped\n", argv[i], fmt); free(b); continue; }
            MTLTextureDescriptor *td = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:(MTLPixelFormat)pf width:w height:h mipmapped:NO];
            td.usage = MTLTextureUsageShaderRead; td.storageMode = MTLStorageModeShared; td.swizzle = MTLTextureSwizzleChannelsMake(swz[0], swz[1], swz[2], swz[3]);
            id<MTLTexture> t = [dev newTextureWithDescriptor:td];
            if (!t) { printf("%s: cannot create format %#x\n", argv[i], fmt); free(b); bad++; continue; }
            [t replaceRegion:MTLRegionMake2D(0, 0, w, h) mipmapLevel:0 withBytes:b bytesPerRow:pitch]; free(b);
            id<MTLBuffer> ob = [dev newBufferWithLength:(NSUInteger)w * h * 4 options:MTLResourceStorageModeShared];
            id<MTLCommandBuffer> cb = [q commandBuffer]; id<MTLComputeCommandEncoder> ce = [cb computeCommandEncoder];
            [ce setComputePipelineState:ps]; [ce setTexture:t atIndex:0]; [ce setBuffer:ob offset:0 atIndex:0];
            [ce dispatchThreads:MTLSizeMake(w, h, 1) threadsPerThreadgroup:MTLSizeMake(8, 8, 1)]; [ce endEncoding]; [cb commit]; [cb waitUntilCompleted];
            CGColorSpaceRef cs = CGColorSpaceCreateDeviceRGB();
            CGContextRef cg = CGBitmapContextCreate(ob.contents, w, h, 8, (size_t)w * 4, cs, (CGBitmapInfo)kCGImageAlphaPremultipliedLast);
            CGImageRef img = CGBitmapContextCreateImage(cg);
            NSString *base = [[@(argv[i]) lastPathComponent] stringByDeletingPathExtension];
            NSURL *url = [NSURL fileURLWithPath:[[@(argv[1]) stringByAppendingPathComponent:base] stringByAppendingPathExtension:@"png"]];
            CGImageDestinationRef dst = CGImageDestinationCreateWithURL((__bridge CFURLRef)url, CFSTR("public.png"), 1, NULL);
            CGImageDestinationAddImage(dst, img, NULL); bool ok = CGImageDestinationFinalize(dst);
            CFRelease(dst); CGImageRelease(img); CGContextRelease(cg); CGColorSpaceRelease(cs);
            printf("%s: format %#x %ux%u%s\n", argv[i], fmt, w, h, ok ? "" : " (PNG write failed)"); bad += !ok;
        }
        return bad != 0;
    }
}
