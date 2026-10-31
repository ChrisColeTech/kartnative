// NVN2 (Switch 2) device: what differs from the NVN1 device in hle_nvn.c. Registered with NVN2 (nvn_impl.h), so only a Switch 2 build
// (DM_GAME_SWITCH2) uses it. Facts: docs/reference/nvn2.md#device (sdk+0x... offsets of Mario Kart World 1.0.0's NVN2 driver).
#include "../../host/hle.h"
#include "nvn_impl.h"

// nvnDeviceGetInteger (sdk+0x5b57c0): the driver's static table (sdk+0x59bf50) for items 0x00-0xb0; items it does not list answer 0.
// Items the driver computes from its NvRm GPU description (copied to device+0x10 by sdk+0x5bf090) take that copy's constants where the
// copy writes constants (texture limits from sdk+0xac4e00 / +0xac55f0, device+0x115 = 1, device+0x118 = 2, the 64 KiB pool page floor),
// and host choices where it takes NvRm's answer: the GPU is Ampere GA10F (characteristics: SM version 0x808), the rest is not observed.
static const int g_devinfo2[] = {
    [0x00] = 231, [0x01] = 16,   // API version: main requires major == 231 and minor >= 16 (main+0xcf3b20 / +0xcf3b2c), and skips its graphics set-up otherwise
    [0x02] = 14, [0x03] = 65536, [0x04] = 256, [0x05] = 8, [0x06] = 32, [0x07] = 4, [0x08] = 16, [0x09] = 32,
    [0x0a] = 16, [0x0b] = 4, [0x0c] = 4, [0x0d] = 4, [0x0e] = 32, [0x0f] = 32, [0x10] = 32, [0x11] = 256, [0x12] = 256,
    [0x13] = 4, [0x14] = 8, [0x15] = 512, [0x16] = 256,
    // 0x17 / 0x18 / 0x37 (scratch memory scale factors: device+0xbc, +0xb4 x +0xbc, +0xb0 x +0xb4 from NvRm): host choice, the GA10F's 12 SMs
    // [INFERENCE] with 32 resident warps each. The host never uses shader scratch memory (Metal allocates a shader's locals), so these only size
    // the pool the game sets aside for it.
    [0x17] = 12, [0x18] = 384, [0x19] = 4096, [0x1a] = 16384,
    [0x1b] = 16, [0x1c] = 1024, [0x1d] = 1024, [0x1e] = 64, [0x1f] = 1024, [0x20] = 2147483647, [0x21] = 65535, [0x22] = 65535,
    [0x23] = 8, [0x24] = 1048576, [0x25] = 4096, [0x26] = 16, [0x27] = 4096, [0x28] = 1, [0x29] = 1, [0x2a] = 1,
    [0x2b] = 1048576,   // L2 size (device+0x28 << 10, from NvRm): host choice, 1 MiB [INFERENCE: GA10F]
    [0x2c] = 16, [0x2d] = 2048,
    // 0x2e-0x31: accepted shader GPU-code versions, 201.0 to 201.11 (control section +0x4 / +0x8), not GLSLC's 1.x: the NVN1 GLSLC-based
    // override in hle_nvn.c does not apply
    [0x2e] = 201, [0x2f] = 201, [0x30] = 11, [0x31] = 0,
    [0x32] = 1, [0x33] = 8, [0x34] = 8, [0x35] = 4, [0x36] = 32, [0x37] = 384, [0x38] = 32, [0x39] = 128, [0x3a] = 65536, [0x3b] = 1, [0x3c] = 4,
    [0x3d] = 32768, [0x3e] = 134217728, [0x3f] = 16384, [0x40] = 32768, [0x41] = 32768, [0x42] = 1, [0x43] = 1, [0x44] = 1,
    [0x45] = 1, [0x46] = 5, [0x47] = 1, [0x48] = 1, [0x49] = 1, [0x4a] = 1, [0x4b] = 1, [0x4c] = 1, [0x4d] = 16,
    [0x4e] = 2048, [0x4f] = 32, [0x50] = 4096, [0x51] = 65536, [0x52] = 65536, [0x53] = 256, [0x54] = 16384,
    [0x55] = 262144, [0x56] = 4096, [0x57] = 1, [0x58] = 4, [0x59] = 2, [0x5a] = 0, [0x5b] = 4096, [0x5c] = 16384,
    [0x5d] = 4096, [0x5e] = 128, [0x5f] = 32, [0x60] = 65535, [0x61] = 1,
    [0x62] = 0,   // device+0xc4 (NvRm GPU info +0x220): meaning unknown, MKW does not query it; 0
    [0x63] = 256, [0x64] = 16, [0x65] = 65535, [0x66] = 65535, [0x67] = 256, [0x68] = 1, [0x69] = 16, [0x6a] = 16, [0x6b] = 16,
    [0x6c] = 1, [0x6d] = 1, [0x6e] = 1, [0x6f] = 256, [0x70] = 0, [0x71] = 16, [0x72] = 8, [0x73] = 16, [0x74] = 32, [0x75] = 31,
    [0x76] = 536870912, [0x77] = 16777216, [0x78] = 16777216, [0x79] = 1073741824, [0x7a] = 32, [0x7b] = 1, [0x7c] = 64,
    [0x7d] = 8,   // from the SM version (device+0xb8): 0x808 -> 8
    [0x7e] = 1, [0x7f] = 65535, [0x80] = 32, [0x81] = 32, [0x82] = 65535, [0x83] = 512, [0x84] = 256, [0x85] = 32, [0x86] = 16383,
    [0x87] = 1,   // device byte +0x115, a constant 1 of the GPU description
    [0x88] = 8, [0x89] = 0, [0x8a] = 0, [0x8b] = 4, [0x8c] = 256, [0x8d] = 16, [0x8e] = 0, [0x8f] = 1, [0x90] = 31, [0x91] = 0,
    [0x92] = 64, [0x93] = 1, [0x94] = 58368, [0x95] = 24576, [0x96] = 0, [0x97] = 256, [0x98] = 131072,
    [0x99] = 2,   // device+0x118, a constant 2 of the GPU description
    [0x9a] = 0,   // device+0x9f0 != 0: an optional driver object the host does not have
    [0x9b] = 0, [0x9c] = 201, [0x9d] = 11, [0x9e] = 201, [0x9f] = 11, [0xa0] = 163840, [0xa1] = 8, [0xa2] = 4, [0xa3] = 256,
    [0xa4] = 256, [0xa5] = 4, [0xa6] = 65536, [0xa7] = 201, [0xa8] = 5, [0xa9] = 1, [0xaa] = 0, [0xab] = 0, [0xac] = 0, [0xad] = 0,
    [0xae] = 0, [0xaf] = 0, [0xb0] = 4096,
};

NVN2(nvnDeviceGetInteger, void, const void *d, int what, int *out) {
    (void)d;
    unsigned w = (unsigned)what;
    if (w >= sizeof g_devinfo2 / sizeof *g_devinfo2) { LOG("[RED] nvnDeviceGetInteger (NVN2) unknown item %#x, returning 0", w); *out = 0; return; }
    *out = g_devinfo2[w];
    if (w <= 1) {   // DM_NVN_API=major.minor overrides the version, as for NVN1
        static int api[2] = {-1, -1};
        if (api[0] < 0) { api[0] = g_devinfo2[0]; api[1] = g_devinfo2[1]; const char *e = getenv("DM_NVN_API"); if (e) sscanf(e, "%d.%d", &api[0], &api[1]); LOG("[nvn] device API %d.%d (NVN2)", api[0], api[1]); }
        *out = api[w];
    }
}
