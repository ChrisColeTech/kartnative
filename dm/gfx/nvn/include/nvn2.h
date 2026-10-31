/* Clean-room declarations of the NVN2 API (Switch 2 NVN, API 231.16, SDK NintendoSDK_NVN-19_3_3) as Mario Kart World 1.0.0
 * uses it, written from the facts in docs/reference/nvn2.md (sdk+0x... / main+0x... offsets there). Not NVIDIA's header: names of
 * new types and enums are ours where the binary has none (marked [name] or [INFERENCE]). Object sizes are what MKW allocates
 * (its SDK's reserved sizes) unless a comment says otherwise. Plain C11; the NVN2 prefix keeps it apart from nvn.h (NVN1).
 * Prototypes are given for functions that are new in NVN2 or whose ABI differs from nvn.h; every other function keeps its nvn.h
 * signature with the NVN2 object types. */
#pragma once
#include <stddef.h>
#include <stdint.h>

/* ---- Device ---- */
typedef struct NVN2device { uint8_t opaque[0x3000]; } NVN2device;              /* main+0xcf3abc allocates 0x3000; the driver zeroes 0x22c8 */
typedef struct NVN2deviceBuilder { uint8_t opaque[0x18]; } NVN2deviceBuilder;  /* SetDefaults zeroes 0x18 (sdk+0x5b0c30); NVN1 reserved 0x40 */
typedef uint64_t NVN2commandHandle;
enum { NVN2_API_MAJOR_VERSION = 231, NVN2_API_MINOR_VERSION = 16 };            /* nvnDeviceGetInteger items 0 / 1 (sdk+0x5b57c0) */
enum { NVN2_SHADER_CODE_MAJOR_VERSION = 201, NVN2_SHADER_CODE_MAX_MINOR_VERSION = 11 }; /* items 0x2e/0x2f, 0x30/0x31; control+0x4 / +0x8 */
/* (device, u64 gpuAddress, u64 size): (gpuAddress >> 4) | (ceil(size / 16) << 45) */
typedef uint64_t (*PFNNVN2DEVICEGETUNIFORMBUFFERHANDLEPROC)(const NVN2device *device, uint64_t gpuAddress, uint64_t size);
/* (handle >> 38) & 0x3fffffc when (handle & 3) == 2, else 0 */
typedef uint64_t (*PFNNVN2DEVICEGETCOMMANDHANDLEMEMORYSIZEPROC)(const NVN2device *device, NVN2commandHandle handle);
typedef int (*PFNNVN2DEVICEGETFLAGSPROC)(const NVN2device *device);
typedef void (*PFNNVN2DEVICEREGISTERFASTCLEARCOLORINDEXPROC)(NVN2device *device, const float *color, int format, int index);   /* [INFERENCE: argument roles] */
typedef void (*PFNNVN2DEVICEREGISTERFASTCLEARCOLORIINDEXPROC)(NVN2device *device, const int *color, int format, int index);    /* [INFERENCE] */
typedef void (*PFNNVN2DEVICEREGISTERFASTCLEARCOLORUIINDEXPROC)(NVN2device *device, const uint32_t *color, int format, int index); /* [INFERENCE] */
typedef uint8_t (*PFNNVN2DEVICEREGISTERFASTCLEARSTENCILPROC)(NVN2device *device, int value);                                 /* [INFERENCE: argument] */


/* ==== Resources ==== */
/* NVN2 (NintendoSDK NVN-19_3_3, API 231.16) resource objects as seen in Mario Kart World 1.0.0.
 * Clean-room declarations from driver facts (sdk = NVN2 driver module offsets). Prefix NVN2 avoids clashing with nvn.h.
 * Sizes: "sure" = established by the driver's Initialize/SetDefaults write range and by main's allocation;
 *        "used/reserved" = the driver touches fewer bytes than the game reserves. */


/* ---------- object sizes ---------- */
typedef struct NVN2memoryPoolBuilder { uint8_t opaque[0x40]; } NVN2memoryPoolBuilder;        /* driver uses 0x38 (sdk+0x5bcdb0); reserved 0x40 [INFERENCE from main+0x2480f0 frame] */
typedef struct NVN2memoryPool { uint8_t opaque[0x100]; } NVN2memoryPool;               /* sure: Initialize zeroes 0x100 (sdk+0x5bcef0); main embeds 0x100 */
typedef struct NVN2externalMemoryPoolBuilder { uint8_t opaque[0x10]; } NVN2externalMemoryPoolBuilder;/* >= 0xc used (sdk+0x612e30..0x612e70); 0x10 [INFERENCE] */
typedef struct NVN2bufferBuilder { uint8_t opaque[0x40]; } NVN2bufferBuilder;            /* driver uses 0x28 (sdk+0x59e190); 0x40 as NVN1 [INFERENCE] */
typedef struct NVN2buffer { uint8_t opaque[0x30]; } NVN2buffer;                   /* sure: Initialize zeroes 0x30 (sdk+0x59e1e0) */
typedef struct NVN2textureBuilder { uint8_t opaque[0x80]; } NVN2textureBuilder;           /* driver uses 0x68; main strides builders by 0x80 (main+0x2625f0) */
typedef struct NVN2texture { uint8_t opaque[0x80]; } NVN2texture;                  /* sure: Initialize zeroes 0x80 (sdk+0x5ca240); main memset 0x80 (main+0x130394) */
typedef struct NVN2textureView { uint8_t opaque[0x10]; } NVN2textureView;              /* sure: SetDefaults zeroes 16 bytes (sdk+0x5c9560) */
typedef struct NVN2texturePool { uint8_t opaque[0x20]; } NVN2texturePool;              /* sure (sdk+0x5cad10; main+0xcee0a8) */
typedef struct NVN2samplerPool { uint8_t opaque[0x20]; } NVN2samplerPool;              /* sure (sdk+0x5c6690; main+0xcee088) */
typedef struct NVN2samplerBuilder { uint8_t opaque[0x40]; } NVN2samplerBuilder;           /* driver uses 0x30; main zeroes 0x40 (main+0x1675080) [INFERENCE: reserved 0x40] */
typedef struct NVN2sampler { uint8_t opaque[0x40]; } NVN2sampler;                  /* sure, GREW from 0x30: Initialize zeroes 0x40 (sdk+0x5c6500); main zeroes 0x40 (main+0x1c5890) */
typedef struct NVN2blendState { uint8_t opaque[0x8]; } NVN2blendState;               /* sure */
typedef struct NVN2channelMaskState { uint8_t opaque[0x4]; } NVN2channelMaskState;         /* sure */
typedef struct NVN2colorState { uint8_t opaque[0x4]; } NVN2colorState;               /* sure */
typedef struct NVN2depthStencilState { uint8_t opaque[0x8]; } NVN2depthStencilState;        /* sure */
typedef struct NVN2multisampleState { uint8_t opaque[0x18]; } NVN2multisampleState;         /* sure */
typedef struct NVN2polygonState { uint8_t opaque[0x4]; } NVN2polygonState;             /* sure */
typedef struct NVN2vertexAttribState { uint8_t opaque[0x4]; } NVN2vertexAttribState;        /* sure */
typedef struct NVN2vertexStreamState { uint8_t opaque[0x8]; } NVN2vertexStreamState;        /* sure */

/* ---------- memory pools ---------- */
enum {
    NVN2_MEMORY_POOL_FLAGS_CPU_NO_ACCESS   = 0x1,
    NVN2_MEMORY_POOL_FLAGS_CPU_UNCACHED    = 0x2,
    NVN2_MEMORY_POOL_FLAGS_CPU_CACHED      = 0x4,
    NVN2_MEMORY_POOL_FLAGS_GPU_NO_ACCESS   = 0x8,
    NVN2_MEMORY_POOL_FLAGS_GPU_UNCACHED    = 0x10,
    NVN2_MEMORY_POOL_FLAGS_GPU_CACHED      = 0x20,
    NVN2_MEMORY_POOL_FLAGS_SHADER_CODE     = 0x40,
    NVN2_MEMORY_POOL_FLAGS_COMPRESSIBLE    = 0x80,
    NVN2_MEMORY_POOL_FLAGS_PHYSICAL        = 0x100,
    NVN2_MEMORY_POOL_FLAGS_VIRTUAL         = 0x200,
    NVN2_MEMORY_POOL_FLAGS_UNKNOWN_400     = 0x400,      /* only in the driver's internal pool (0x412, sdk+0x5b54e0); meaning unknown */
    NVN2_MEMORY_POOL_FLAGS_USER_MASK       = 0x3fffff,   /* nvnMemoryPoolGetFlags returns flags & this (sdk+0x5bd330) */
    /* driver-internal bits kept in pool+0x10 (hidden by GetFlags) */
    NVN2_MEMORY_POOL_INTERNAL_KIND_MODIFIED = 0x8000000,  /* a compressible texture re-kinded part of the special mapping (sdk+0x5cca2c) */
    NVN2_MEMORY_POOL_INTERNAL_DRIVER_OWNED  = 0x10000000, /* device-internal pool (sdk+0x5b54e0) */
    NVN2_MEMORY_POOL_INTERNAL_RESERVED_LIST = 0x20000000  /* builder flag: +0x28 = u32 count, +0x30 = NVN2reservedAddress* (sdk+0x5ccd38) */
};
#define NVN2_MEMORY_POOL_BUILDER_DEFAULT_FLAGS 0x21u      /* CPU_NO_ACCESS | GPU_CACHED (sdk+0x5bcdb0) */

/* Entry of the reserved-address list (builder flag 0x20000000). type: 1 generic mapping (pool+0x18), 2 compressible mapping (pool+0x28), 5 second generic mapping (pool+0x20). */
typedef struct { uint32_t type; uint32_t _pad; uint64_t gpuAddress; uint64_t size; } NVN2reservedAddress;   /* 0x18 bytes */

/* nvnMemoryPoolMapVirtual request: 0x28 bytes (sdk+0x5cd730; main+0x521640) */
typedef struct {
    NVN2memoryPool *physicalPool;   /* NULL = unmap */
    int64_t  physicalOffset;        /* multiple of MEMORY_POOL_PAGE_SIZE */
    int64_t  virtualOffset;         /* multiple of MEMORY_POOL_PAGE_SIZE */
    uint64_t size;                  /* multiple of MEMORY_POOL_PAGE_SIZE */
    int32_t  storageClass;          /* see NVN2_STORAGE_CLASS_* */
    int32_t  _pad;
} NVN2mappingRequest;
_Static_assert(sizeof(NVN2mappingRequest) == 0x28, "NVN2mappingRequest");

/* Storage class encoding (TextureBuilderGetStorageClass sdk+0x5c9360, MapVirtual sdk+0x5cd730) */
#define NVN2_STORAGE_CLASS_GENERIC        0x100u     /* the device generic PTE kind (device+0x1da0, = 6) */
#define NVN2_STORAGE_CLASS_VALID          0x100u     /* set in every non-generic class too: class = 0x100 | kind */
#define NVN2_STORAGE_CLASS_EXTRA          0x200u     /* only with DeviceBuilder flag 0x10000 for block-linear textures */
#define NVN2_STORAGE_CLASS_KIND(c)        ((uint32_t)(c) & ~0x300u)

/* ---------- textures ---------- */
enum {
    NVN2_TEXTURE_FLAGS_UNKNOWN_800  = 0x800,   /* new: round mip sizes up (1 + ((x-1)>>1)) in sdk+0x59c2e0; also descriptor word2 bit 20 [INFERENCE] */
    NVN2_TEXTURE_FLAGS_UNKNOWN_1000 = 0x1000   /* new: passed to the PTE-kind chooser for colour formats (sdk+0x5beddc) */
};
#define NVN2_TEXTURE_FLAGS_STORED_MASK 0xffffu  /* the texture keeps 16 bits (sdk+0x5cabc0) */

/* New NVNformat values (format table sdk+0xd934b8 has 0xa3 entries; 0x00..0x9c = NVN1 values). Names unknown. */
enum {
    NVN2_FORMAT_UNKNOWN_9D = 0x9d,   /* 4x4 block, 8 B,  UNORM, same hw format word as RGB_DXT1 */
    NVN2_FORMAT_UNKNOWN_9E = 0x9e,   /* 4x4 block, 16 B, UNORM, same hw word as RGBA_DXT3 */
    NVN2_FORMAT_UNKNOWN_9F = 0x9f,   /* = 0x9d, sRGB */
    NVN2_FORMAT_UNKNOWN_A0 = 0xa0,   /* = 0x9e, sRGB */
    NVN2_FORMAT_UNKNOWN_A1 = 0xa1,   /* 1x1, 8 B, signed integer, hw word of RG32I   [INFERENCE: R64 signed] */
    NVN2_FORMAT_UNKNOWN_A2 = 0xa2,   /* 1x1, 8 B, unsigned integer, hw word of RG32UI [INFERENCE: R64 unsigned] */
    NVN2_FORMAT_COUNT      = 0xa3
};

#define NVN2_TEXTURE_DESCRIPTOR_SIZE       0x20   /* GetInteger(0xf), static table sdk+0x59bf50 */
#define NVN2_SAMPLER_DESCRIPTOR_SIZE       0x20   /* GetInteger(0x10) */
#define NVN2_RESERVED_TEXTURE_DESCRIPTORS  0x100  /* GetInteger(0x11) */
#define NVN2_RESERVED_SAMPLER_DESCRIPTORS  0x100  /* GetInteger(0x12) */
#define NVN2_TEXTURE_BLOCKLINEAR_ALIGNMENT 0x200  /* GetStorageAlignment minimum for block-linear generic kind (sdk+0x5c9080) */

/* ---------- state-object defaults (SetDefaults constants) ---------- */
#define NVN2_BLEND_STATE_DEFAULT          0x01020102110c0000ull  /* sdk+0x5c69a0 */
#define NVN2_CHANNEL_MASK_STATE_DEFAULT   0xffffffffu            /* sdk+0x5c6b80 */
#define NVN2_COLOR_STATE_DEFAULT          0x08030000u            /* sdk+0x5c6c10 */
#define NVN2_DEPTH_STENCIL_STATE_DEFAULT  0x1118111800000022ull  /* sdk+0x5c6da0 */
#define NVN2_MULTISAMPLE_STATE_DEFAULT_W0 0x5u                   /* + 16 bytes of 0x88 at +4, u32 0 at +0x14 (sdk+0x5c7050) */
#define NVN2_POLYGON_STATE_DEFAULT        0x14u                  /* sdk+0x5c6f80 */
#define NVN2_SAMPLER_BUILDER_PACKED_DEFAULT 0x08110111u          /* builder+0x2c (sdk+0x5c6220) */

/* ---------- prototypes: functions new in NVN2 ---------- */
typedef void        (*PFNNVN2MULTISAMPLESTATESETCOVERAGETRUNCATIONENABLEPROC)(NVN2multisampleState *state, uint8_t enable);   /* bit 5, sdk+0x5c7190 */
typedef uint8_t     (*PFNNVN2MULTISAMPLESTATEGETCOVERAGETRUNCATIONENABLEPROC)(const NVN2multisampleState *state);             /* sdk+0x5c71b0 */

/* NVX (GetProcAddress strcmp fallback sdk+0x5b5b40) */
typedef void        (*PFNNVN2MEMORYPOOLBUILDERSETNATIVEHANDLENVXPROC)(NVN2memoryPoolBuilder *b, uint32_t nvrmMemHandle);     /* +0x20; clears memory/size */
typedef uint32_t    (*PFNNVN2MEMORYPOOLBUILDERGETNATIVEHANDLENVXPROC)(const NVN2memoryPoolBuilder *b);
typedef uint32_t    (*PFNNVN2MEMORYPOOLGETNATIVEHANDLENVXPROC)(const NVN2memoryPool *pool);                                 /* pool+0x58 */
typedef void        (*PFNNVN2MEMORYPOOLBUILDERGETRESERVEDADDRESSESNVXPROC)(const NVN2memoryPoolBuilder *b, uint64_t *genericAddress, uint64_t *specialAddress);
typedef void        (*PFNNVN2MEMORYPOOLBUILDERSETGENERICADDRESSNVXPROC)(NVN2memoryPoolBuilder *b, uint64_t gpuAddress);      /* +0x28 */
typedef uint64_t    (*PFNNVN2MEMORYPOOLGETGENERICADDRESSNVXPROC)(const NVN2memoryPool *pool);                               /* pool+0x18 */
typedef void        (*PFNNVN2MEMORYPOOLBUILDERSETSPECIALADDRESSNVXPROC)(NVN2memoryPoolBuilder *b, uint64_t gpuAddress);      /* +0x30 */
typedef uint64_t    (*PFNNVN2MEMORYPOOLGETSPECIALADDRESSNVXPROC)(const NVN2memoryPool *pool);                               /* pool+0x28 */

/* ExternalMemoryPoolBuilder: plain sdk exports (sdk+0x612e30..0x612e70), not in GetProcAddress */
typedef uint32_t NVN2nativeMemoryHandle;   /* nn::gs::NativeMemoryHandle */
void                   nvnExternalMemoryPoolBuilderSetDefaults(NVN2externalMemoryPoolBuilder *b);               /* +0x8 = 0 */
void                   nvnExternalMemoryPoolBuilderSetDevice(NVN2externalMemoryPoolBuilder *b, NVN2device *d);  /* +0x0 */
void                   nvnExternalMemoryPoolBuilderSetNativeMemoryHandle(NVN2externalMemoryPoolBuilder *b, NVN2nativeMemoryHandle h); /* +0x8 */
NVN2device            *nvnExternalMemoryPoolBuilderGetDevice(const NVN2externalMemoryPoolBuilder *b);
NVN2nativeMemoryHandle nvnExternalMemoryPoolBuilderGetNativeMemoryHandle(const NVN2externalMemoryPoolBuilder *b);

/* ---------- prototypes: names present in NVN1 without a prototype in nvn.h ---------- */
typedef NVN2device *(*PFNNVN2MEMORYPOOLBUILDERGETDEVICEPROC)(const NVN2memoryPoolBuilder *b);     /* +0x8 */
typedef NVN2device *(*PFNNVN2BUFFERBUILDERGETDEVICEPROC)(const NVN2bufferBuilder *b);             /* +0x8 */
typedef NVN2device *(*PFNNVN2TEXTUREBUILDERGETDEVICEPROC)(const NVN2textureBuilder *b);           /* +0x8 */
typedef NVN2device *(*PFNNVN2SAMPLERBUILDERGETDEVICEPROC)(const NVN2samplerBuilder *b);           /* +0x0 */
typedef int         (*PFNNVN2TEXTUREBUILDERGETRAWSTORAGECLASSPROC)(const NVN2textureBuilder *b);  /* 1 linear, 2 colour/D32F, 3 S8/D16, 4 D24(S8), 5 D32F_S8 */
typedef int         (*PFNNVN2TEXTUREGETRAWSTORAGECLASSPROC)(const NVN2texture *t);
typedef ptrdiff_t   (*PFNNVN2TEXTUREBUILDERGETVIEWOFFSETPROC)(const NVN2textureBuilder *b, const NVN2textureView *v);
typedef uint8_t     (*PFNNVN2TEXTUREBUILDERGETPACKAGEDTEXTURELAYOUTPROC)(const NVN2textureBuilder *b, void *layout /* 8 bytes; one u32 written */);

/* ---------- functions whose behaviour differs from NVN1 at the same signature ----------
 * nvnTextureBuilderSetPackagedTextureLayout: reads only the first 2 bytes (12 bits) of the 8-byte layout.
 * nvnTextureBuilderSetStride / GetStride, nvnTextureGetStride, nvnVertexStreamStateSetStride / GetStride: 32-bit storage.
 * nvnSamplerInitialize: writes 0x40 bytes.
 * nvnMemoryPoolFlushMappedRange / InvalidateMappedRange (and Buffer / Texture flush / invalidate): no-ops. */


/* ==== CommandBuffer ==== */
/* NVN2 CommandBuffer family — clean-room declarations from the NVN-19_3_3 driver as linked into
 * Mario Kart World 1.0.0. Offsets: sdk+... = driver image, main+... = game. Names in brackets are
 * inferences. Plain C11; only <stdint.h>/<stddef.h>. */

/* ---- Types owned by other families: forward declarations. Drop these when the merged header
 *      already defines them (they are incomplete tagged structs only). ---- */
typedef uint8_t NVN2boolean;
typedef uint64_t NVN2bufferAddress;
typedef uint64_t NVN2textureHandle;
typedef uint64_t NVN2imageHandle;
typedef uint64_t NVN2separateTextureHandle;
typedef uint64_t NVN2separateSamplerHandle;
typedef struct NVN2device NVN2device;
typedef struct NVN2memoryPool NVN2memoryPool;
typedef struct NVN2texture NVN2texture;
typedef struct NVN2textureView NVN2textureView;
typedef struct NVN2deviceCommandBuffer NVN2deviceCommandBuffer;   /* see DeviceCommandBuffer family */
typedef struct NVN2deviceMessageBuffer NVN2deviceMessageBuffer;   /* see DeviceMessageBuffer family */
typedef struct { NVN2bufferAddress address; uint64_t size; } NVN2bufferRange;   /* as NVN1 */
typedef struct { int32_t xoffset, yoffset, zoffset, width, height, depth; } NVN2copyRegion; /* as NVN1 */

/* ---- NVNcommandBuffer ----
 * Size: 0xC0 reserved by the game (main+0xcf42a0 allocates 0xC0, align 8, then Initialize);
 * the driver uses 0x00-0xAF (Initialize sdk+0x59ee80 zeroes 22 qwords, last write is +0xA8).
 * NVN1 nvn.h reserved 0xA0: too small for NVN2. Certainty: high. */
typedef struct NVN2commandBuffer { uint8_t opaque[0xC0]; } NVN2commandBuffer;

/* Driver field offsets (for reference; nothing outside the driver should touch them except the
 * game's own byte store at +0x10). */
enum {
    NVN2_CMDBUF_OFF_CMD_WRITE      = 0x00, /* u8* */
    NVN2_CMDBUF_OFF_CMD_LIMIT      = 0x08, /* u8*  soft limit (segment <= 0x7FFFFC bytes) */
    NVN2_CMDBUF_OFF_GAME_BYTE      = 0x10, /* u8   written as 1 by MKW after AddCommandMemory, never read by the driver */
    NVN2_CMDBUF_OFF_RECORDING      = 0x11, /* u8 */
    NVN2_CMDBUF_OFF_TOKEN_HANDLE   = 0x12, /* u8   force tag-0 handle */
    NVN2_CMDBUF_OFF_FLAGS          = 0x13, /* u8   bit0 empty recording, bit3 device+0x1c28 bit0 */
    NVN2_CMDBUF_OFF_CMD_CB_ENABLE  = 0x14, /* u8 */
    NVN2_CMDBUF_OFF_CTRL_CB_ENABLE = 0x15, /* u8 */
    NVN2_CMDBUF_OFF_RECORDING_MODE = 0x18, /* u32 */
    NVN2_CMDBUF_OFF_CMD_END        = 0x20,
    NVN2_CMDBUF_OFF_CTRL_WRITE     = 0x28,
    NVN2_CMDBUF_OFF_CTRL_END       = 0x30, /* end - 0x10 */
    NVN2_CMDBUF_OFF_CMD_START      = 0x38,
    NVN2_CMDBUF_OFF_CTRL_START     = 0x40,
    NVN2_CMDBUF_OFF_SEGMENT_START  = 0x48,
    NVN2_CMDBUF_OFF_RECORDING_HDR  = 0x50,
    NVN2_CMDBUF_OFF_GATHER_GROUP   = 0x58,
    NVN2_CMDBUF_OFF_CMD_GPU_START  = 0x60,
    NVN2_CMDBUF_OFF_RESERVE_WORDS  = 0x68, /* u32 */
    NVN2_CMDBUF_OFF_DEVICE         = 0x70,
    NVN2_CMDBUF_OFF_CALLBACK       = 0x78,
    NVN2_CMDBUF_OFF_CALLBACK_DATA  = 0x80,
    NVN2_CMDBUF_OFF_COPY_ROW_STRIDE   = 0x90, /* i32 */
    NVN2_CMDBUF_OFF_COPY_IMAGE_STRIDE = 0x94, /* i32 */
    NVN2_CMDBUF_OFF_FINALIZE_BLOCK = 0x98, /* freed by Finalize */
    NVN2_CMDBUF_OFF_INLINE_COMPUTE = 0xA8  /* u8 device+0x08 bit18 */
};

/* Data export that gates the game's header-inline fast paths (sdk+0xedb150). The host must keep it 0. */
/* extern uint32_t nvnPrivate0; */

/* ---- NVNcommandHandle: tagged 64-bit value (low 2 bits) ---- */
typedef uint64_t NVN2commandHandle;
enum {
    NVN2_COMMAND_HANDLE_TAG_STREAM = 0, /* CPU pointer to a control-token stream (8-byte aligned) */
    NVN2_COMMAND_HANDLE_TAG_GATHER = 1, /* bits 2-47: CPU pointer to first gather record; bits 48-63: record count */
    NVN2_COMMAND_HANDLE_TAG_DIRECT = 2  /* bits 2-39: GPU address; bits 40-63: word count (modes 3/4 only) */
};

/* Control memory gather record (0x18 bytes) inside a token-3 group {u32 3, u32 count}. */
typedef struct {
    uint64_t gpuAddress;
    uint64_t cpuAddress;
    uint32_t words;      /* < 2^21 */
    uint8_t  flags;      /* bit0 own command memory (mergeable/copyable), bit1 flush point, bit2 GPFIFO sync */
    uint8_t  pad[3];
} NVN2gatherRecord;

/* Control token types (low byte of the first word). Driver-internal. */
enum {
    NVN2_CTRL_TOKEN_END = 2, NVN2_CTRL_TOKEN_GATHER = 3, NVN2_CTRL_TOKEN_CHAIN = 4,
    NVN2_CTRL_TOKEN_FENCE_SYNC = 5, NVN2_CTRL_TOKEN_WAIT_SYNC = 6, NVN2_CTRL_TOKEN_SIGNAL_EVENT = 7,
    NVN2_CTRL_TOKEN_DISPATCH_COMPUTE = 0x0F, NVN2_CTRL_TOKEN_DISPATCH_COMPUTE_INDIRECT = 0x10,
    NVN2_CTRL_TOKEN_TEXTURE_POOL = 0x14, NVN2_CTRL_TOKEN_SAMPLER_POOL = 0x15,
    NVN2_CTRL_TOKEN_BIND_TEXTURES_DEFERRED = 0x17, NVN2_CTRL_TOKEN_BIND_SEPARATE_TEXTURES_DEFERRED = 0x18,
    NVN2_CTRL_TOKEN_BIND_SEPARATE_SAMPLERS_DEFERRED = 0x19, NVN2_CTRL_TOKEN_BIND_UNIFORM_BUFFERS_DEFERRED = 0x1A,
    NVN2_CTRL_TOKEN_BIND_STORAGE_BUFFERS_DEFERRED = 0x1B, NVN2_CTRL_TOKEN_BIND_IMAGES_DEFERRED = 0x1C,
    NVN2_CTRL_TOKEN_BIND_ACCEL_STRUCTS_DEFERRED = 0x1D, NVN2_CTRL_TOKEN_PUSH_GRAPHICS_DEFERRED = 0x1E,
    NVN2_CTRL_TOKEN_PUSH_COMPUTE_DEFERRED = 0x1F, NVN2_CTRL_TOKEN_COMPUTE_OVERLAP = 0x27,
    NVN2_CTRL_TOKEN_DISPATCH_RAYS = 0x49, NVN2_CTRL_TOKEN_DISPATCH_RAYS_INDIRECT = 0x4A,
    NVN2_CTRL_TOKEN_BUILD_ACCEL = 0x4B, NVN2_CTRL_TOKEN_COPY_ACCEL = 0x4C, NVN2_CTRL_TOKEN_COMPACT_ACCEL = 0x4D,
    NVN2_CTRL_TOKEN_RELOCATE_ACCEL = 0x4E, NVN2_CTRL_TOKEN_DIRTY_TILE_MAP = 0x55, NVN2_CTRL_TOKEN_COPY_BUFFER = 0x56,
    NVN2_CTRL_TOKEN_CLEAR_BUFFER = 0x57, NVN2_CTRL_TOKEN_EVALUATE_DLSS = 0x59, NVN2_CTRL_TOKEN_WAIT_SYNC_COMPUTE = 0x5B,
    NVN2_CTRL_TOKEN_BIND_DMB = 0x61, NVN2_CTRL_TOKEN_BIND_DMB_DEFERRED = 0x62, NVN2_CTRL_TOKEN_RESET_DMB_DEFERRED = 0x63
};

/* ---- Memory callback: unchanged from NVN1 ---- */
typedef enum {
    NVN2_COMMAND_BUFFER_MEMORY_EVENT_OUT_OF_COMMAND_MEMORY = 0, /* minSize >= 0x200 */
    NVN2_COMMAND_BUFFER_MEMORY_EVENT_OUT_OF_CONTROL_MEMORY = 1  /* minSize >= 0x100 */
} NVN2commandBufferMemoryEvent;
typedef void (*PFNNVN2COMMANDBUFFERMEMORYCALLBACKPROC)(NVN2commandBuffer *cmdBuf, NVN2commandBufferMemoryEvent event,
                                                       size_t minSize, void *callbackData);

/* ---- Recording mode (SetRecordingMode, +0x18). No names in the binary. ---- */
enum {
    NVN2_RECORDING_MODE_GRAPHICS = 0,        /* [name] default; 3D subchannel */
    NVN2_RECORDING_MODE_COMPUTE = 1,         /* [name] compute subchannel; copies/reports become queue tokens */
    NVN2_RECORDING_MODE_COPY = 2,            /* [name] copy engine; Barrier is a no-op */
    NVN2_RECORDING_MODE_GRAPHICS_DIRECT = 3, /* [name] as 0, EndRecording may return a tag-2 handle */
    NVN2_RECORDING_MODE_COMPUTE_DIRECT = 4   /* [name] as 1, tag-2 handles */
};

/* Shader stages accepted by stage-indexed binds: 0-5 as NVN1, 6/7 [task/mesh, order unknown], 8 [ray tracing]. */
enum { NVN2_SHADER_STAGE_6 = 6, NVN2_SHADER_STAGE_7 = 7, NVN2_SHADER_STAGE_8 = 8 };

/* Barrier bits added in NVN2 (0x01-0x80 as NVN1). Names unknown. */
enum { NVN2_BARRIER_BIT_0x100 = 0x100, NVN2_BARRIER_BIT_0x200 = 0x200, NVN2_BARRIER_BIT_0x400 = 0x400 };

/* ReportCounter: NVN1 types 0x00-0x10 plus a new 0x11 (two 16-byte reports). */
enum { NVN2_COUNTER_TYPE_0x11 = 0x11 };

/* ---- Argument structures read by the driver ---- */
typedef struct { uint32_t count, first; } NVN2drawMeshTasksIndirectData;   /* 8 bytes  [field order inferred] */
typedef struct { uint32_t x, y, z; } NVN2drawMeshTasks3DIndirectData;      /* 12 bytes [field order inferred] */

/* Ray-dispatch shader-table region (0x18 bytes): passed on as (field0, field2, field1). [names inferred] */
typedef struct { NVN2bufferAddress address; uint64_t size; uint32_t stride; uint32_t pad; } NVN2rayTableRegion;

/* BuildAccelerationStructures entry (0x40 bytes); geometry entries are 0x40 bytes too. [names inferred] */
typedef struct {
    uint32_t flags;          /* bit28 is split off by the driver */
    uint32_t type;           /* compared with 1 */
    const void *geometries;  /* count at +0x10, 0x40 bytes each */
    int32_t geometryCount;
    uint32_t pad;
    uint64_t addresses[5];   /* +0x18..+0x3F, passed to the builder */
} NVN2accelerationStructureBuildInfo;
typedef struct { uint32_t kind; uint32_t pad; NVN2bufferAddress address; } NVN2accelerationStructurePostBuildInfo; /* kind < 5; stride per build = {8,0,0,8,0x18}[kind] */

/* ---- Prototypes: new in NVN2 ---- */
typedef void (*PFNNVN2COMMANDBUFFERSETRECORDINGMODEPROC)(NVN2commandBuffer*, int mode);
typedef void (*PFNNVN2COMMANDBUFFERSETGEOMETRYPROCESSINGMODEPROC)(NVN2commandBuffer*, int mode);
typedef void (*PFNNVN2COMMANDBUFFERDRAWMESHTASKSPROC)(NVN2commandBuffer*, int first, int count);
typedef void (*PFNNVN2COMMANDBUFFERDRAWMESHTASKS3DPROC)(NVN2commandBuffer*, int x, int y, int z);
typedef void (*PFNNVN2COMMANDBUFFERDRAWMESHTASKSINDIRECTPROC)(NVN2commandBuffer*, NVN2bufferAddress indirect);
typedef void (*PFNNVN2COMMANDBUFFERDRAWMESHTASKS3DINDIRECTPROC)(NVN2commandBuffer*, NVN2bufferAddress indirect);
typedef void (*PFNNVN2COMMANDBUFFERMULTIDRAWMESHTASKSINDIRECTPROC)(NVN2commandBuffer*, NVN2bufferAddress indirect,
                                                                    int drawCount, int stride);
typedef void (*PFNNVN2COMMANDBUFFERMULTIDRAWMESHTASKSINDIRECTCOUNTPROC)(NVN2commandBuffer*, NVN2bufferAddress indirect,
                                                                         NVN2bufferAddress count, int maxDrawCount, int stride);
typedef void (*PFNNVN2COMMANDBUFFERMULTIDRAWMESHTASKS3DINDIRECTPROC)(NVN2commandBuffer*, NVN2bufferAddress indirect,
                                                                      int drawCount, int stride);
typedef void (*PFNNVN2COMMANDBUFFERMULTIDRAWMESHTASKS3DINDIRECTCOUNTPROC)(NVN2commandBuffer*, NVN2bufferAddress indirect,
                                                                           NVN2bufferAddress count, int maxDrawCount, int stride);

typedef void (*PFNNVN2COMMANDBUFFERPUSHGRAPHICSCONSTANTSPROC)(NVN2commandBuffer*, int slot, int offset, int size, const void *data);
typedef void (*PFNNVN2COMMANDBUFFERPUSHCOMPUTECONSTANTSPROC)(NVN2commandBuffer*, int slot, int offset, int size, const void *data);
typedef void (*PFNNVN2COMMANDBUFFERPUSHGRAPHICSCONSTANTSDEFERREDPROC)(NVN2commandBuffer*, int slot, int offset, int size, const void *data);
typedef void (*PFNNVN2COMMANDBUFFERPUSHCOMPUTECONSTANTSDEFERREDPROC)(NVN2commandBuffer*, int slot, int offset, int size, const void *data);

typedef void (*PFNNVN2COMMANDBUFFERSETEXCLUSIVESCISSORPROC)(NVN2commandBuffer*, int x, int y, int w, int h);
typedef void (*PFNNVN2COMMANDBUFFERSETEXCLUSIVESCISSORSPROC)(NVN2commandBuffer*, int first, int count, const int *rects /* {x,y,w,h} */);
typedef void (*PFNNVN2COMMANDBUFFERENABLEEXCLUSIVESCISSORPROC)(NVN2commandBuffer*, NVN2boolean enable);
typedef void (*PFNNVN2COMMANDBUFFERBINDSHADINGRATEIMAGEPROC)(NVN2commandBuffer*, const NVN2texture*, const NVN2textureView*);
typedef void (*PFNNVN2COMMANDBUFFERSHADINGRATEIMAGEPALETTEPROC)(NVN2commandBuffer*, int index, int count, const int *rates /* <= 16 */);
typedef void (*PFNNVN2COMMANDBUFFERSETSHADINGRATEPALETTEMODEPROC)(NVN2commandBuffer*, int mode);
typedef void (*PFNNVN2COMMANDBUFFERSETREPRESENTATIVEFRAGMENTTESTPROC)(NVN2commandBuffer*, NVN2boolean enable);
typedef void (*PFNNVN2COMMANDBUFFERSETCONSERVATIVERASTERMODEPROC)(NVN2commandBuffer*, int mode, int param);

typedef void (*PFNNVN2COMMANDBUFFERSETCOMPUTEENABLEPROC)(NVN2commandBuffer*, NVN2boolean enable);
typedef void (*PFNNVN2COMMANDBUFFERSETCOMPUTEENABLECONDITIONALPROC)(NVN2commandBuffer*, int conditionalRenderMode, NVN2bufferAddress counter);
typedef void (*PFNNVN2COMMANDBUFFERSETCOPYENABLEPROC)(NVN2commandBuffer*, NVN2boolean enable);
typedef void (*PFNNVN2COMMANDBUFFERSETCOPYENABLECONDITIONALPROC)(NVN2commandBuffer*, int conditionalRenderMode, NVN2bufferAddress counter);
typedef void (*PFNNVN2COMMANDBUFFERSETCOMPUTEOVERLAPMODEPROC)(NVN2commandBuffer*, int mode);

typedef void (*PFNNVN2COMMANDBUFFERPUSHDEBUGGROUPGRAPHICSPROC)(NVN2commandBuffer*, uint32_t domainId, const char *name);
typedef void (*PFNNVN2COMMANDBUFFERPUSHDEBUGGROUPCOMPUTEPROC)(NVN2commandBuffer*, uint32_t domainId, const char *name);
typedef void (*PFNNVN2COMMANDBUFFERPOPDEBUGGROUPGRAPHICSPROC)(NVN2commandBuffer*, uint32_t domainId);
typedef void (*PFNNVN2COMMANDBUFFERPOPDEBUGGROUPCOMPUTEPROC)(NVN2commandBuffer*, uint32_t domainId);
typedef void (*PFNNVN2COMMANDBUFFERINSERTDEBUGMARKERGRAPHICSPROC)(NVN2commandBuffer*, uint32_t domainId, const char *marker);
typedef void (*PFNNVN2COMMANDBUFFERINSERTDEBUGMARKERCOMPUTEPROC)(NVN2commandBuffer*, uint32_t domainId, const char *marker);

typedef void (*PFNNVN2COMMANDBUFFERBEGINDEVICECOMMANDSPROC)(NVN2commandBuffer*, const NVN2deviceCommandBuffer*);
typedef void (*PFNNVN2COMMANDBUFFERENDDEVICECOMMANDSPROC)(NVN2commandBuffer*, const NVN2deviceCommandBuffer*);
typedef void (*PFNNVN2COMMANDBUFFERSUBMITDEVICECOMMANDSPROC)(NVN2commandBuffer*, const NVN2deviceCommandBuffer*);
typedef void (*PFNNVN2COMMANDBUFFERBINDDEVICEMESSAGEBUFFERPROC)(NVN2commandBuffer*, int stage, int index, const NVN2deviceMessageBuffer*);
typedef void (*PFNNVN2COMMANDBUFFERBINDDEVICEMESSAGEBUFFERDEFERREDPROC)(NVN2commandBuffer*, int stage, int index, const NVN2deviceMessageBuffer*);
typedef void (*PFNNVN2COMMANDBUFFERRESETDEVICEMESSAGEBUFFERPROC)(NVN2commandBuffer*, const NVN2deviceMessageBuffer*);
typedef void (*PFNNVN2COMMANDBUFFERRESETDEVICEMESSAGEBUFFERDEFERREDPROC)(NVN2commandBuffer*, const NVN2deviceMessageBuffer*);

typedef void (*PFNNVN2COMMANDBUFFERBINDACCELERATIONSTRUCTUREPROC)(NVN2commandBuffer*, int stage, int index, uint64_t accelerationStructure);
typedef void (*PFNNVN2COMMANDBUFFERBINDACCELERATIONSTRUCTURESPROC)(NVN2commandBuffer*, int stage, int first, int count, const uint64_t *structures);
typedef void (*PFNNVN2COMMANDBUFFERBINDACCELERATIONSTRUCTURESDEFERREDPROC)(NVN2commandBuffer*, int stage, int first, int count, const uint64_t *structures);
typedef void (*PFNNVN2COMMANDBUFFERBUILDACCELERATIONSTRUCTURESPROC)(NVN2commandBuffer*, uint64_t count, const NVN2accelerationStructureBuildInfo *infos,
                                                                     uint64_t postBuildCount, const NVN2accelerationStructurePostBuildInfo *postBuild);
typedef void (*PFNNVN2COMMANDBUFFERCOPYACCELERATIONSTRUCTUREPROC)(NVN2commandBuffer*, uint64_t a, uint64_t b);     /* [dst/src order unknown] */
typedef void (*PFNNVN2COMMANDBUFFERCOMPACTACCELERATIONSTRUCTUREPROC)(NVN2commandBuffer*, uint64_t a, uint64_t b);  /* [dst/src order unknown] */
typedef void (*PFNNVN2COMMANDBUFFERRELOCATEACCELERATIONSTRUCTUREPROC)(NVN2commandBuffer*, uint64_t accelerationStructure);
typedef void (*PFNNVN2COMMANDBUFFERDISPATCHRAYSPROC)(NVN2commandBuffer*, uint64_t raygen, const NVN2rayTableRegion *miss,
                                                     const NVN2rayTableRegion *hit, const NVN2rayTableRegion *callable,
                                                     int width, int height, int depth);
typedef void (*PFNNVN2COMMANDBUFFERDISPATCHRAYSINDIRECTPROC)(NVN2commandBuffer*, uint64_t raygen, const NVN2rayTableRegion *miss,
                                                             const NVN2rayTableRegion *hit, const NVN2rayTableRegion *callable,
                                                             NVN2bufferAddress dimensions);
typedef void (*PFNNVN2COMMANDBUFFERCOMPUTEDIRTYTILEMAPPROC)(NVN2commandBuffer*, const NVN2texture*, uint64_t a,
                                                            const int region[6] /* x,y,z,w,h,d */, int b, uint64_t c,
                                                            int rowStride /* 0 = auto */, int imageStride /* 0 = auto */);
/* nvnCommandBufferEvaluateDlss: see the Dlss section. */

/* ---- Prototypes: ABI differs from dm/gfx/nvn/include/nvn.h ---- */
typedef void (*PFNNVN2COMMANDBUFFERPOPDEBUGGROUPPROC)(NVN2commandBuffer*, uint32_t domainId);               /* nvn.h: (cmd) */
typedef void (*PFNNVN2COMMANDBUFFERINSERTDEBUGMARKERPROC)(NVN2commandBuffer*, uint32_t domainId, const char *marker); /* nvn.h: (cmd, const char*) */
typedef void (*PFNNVN2COMMANDBUFFERCOPYBUFFERTOBUFFERPROC)(NVN2commandBuffer*, NVN2bufferAddress src, NVN2bufferAddress dst,
                                                           size_t size, int flagsIgnored);                /* 5th arg never read */
typedef void (*PFNNVN2COMMANDBUFFERSETCOPYROWSTRIDEPROC)(NVN2commandBuffer*, ptrdiff_t stride);   /* stored as 32 bits */
typedef ptrdiff_t (*PFNNVN2COMMANDBUFFERGETCOPYROWSTRIDEPROC)(const NVN2commandBuffer*);          /* returns zero-extended 32 bits */

/* ---- Prototypes: names present in NVN1 but missing from nvn.h (signatures as read by the driver) ---- */
typedef void (*PFNNVN2COMMANDBUFFERSETCOMMANDMEMORYCALLBACKENABLEDPROC)(NVN2commandBuffer*, NVN2boolean enabled);
typedef void (*PFNNVN2COMMANDBUFFERSETCONTROLMEMORYCALLBACKENABLEDPROC)(NVN2commandBuffer*, NVN2boolean enabled);
typedef NVN2boolean (*PFNNVN2COMMANDBUFFERINSERTCHECKPOINTPROC)(NVN2commandBuffer*);   /* Static/Dynamic: return true, args ignored */
typedef void (*PFNNVN2COMMANDBUFFERBINDUNIFORMBUFFERSDEFERREDPROC)(NVN2commandBuffer*, int stage, int first, int count, const NVN2bufferRange*);
typedef void (*PFNNVN2COMMANDBUFFERBINDSTORAGEBUFFERSDEFERREDPROC)(NVN2commandBuffer*, int stage, int first, int count, const NVN2bufferRange*);
typedef void (*PFNNVN2COMMANDBUFFERBINDTEXTURESDEFERREDPROC)(NVN2commandBuffer*, int stage, int first, int count, const NVN2textureHandle*);
typedef void (*PFNNVN2COMMANDBUFFERBINDIMAGESDEFERREDPROC)(NVN2commandBuffer*, int stage, int first, int count, const NVN2imageHandle*);
typedef void (*PFNNVN2COMMANDBUFFERBINDSEPARATETEXTURESDEFERREDPROC)(NVN2commandBuffer*, int stage, int first, int count, const NVN2separateTextureHandle*);
typedef void (*PFNNVN2COMMANDBUFFERBINDSEPARATESAMPLERSDEFERREDPROC)(NVN2commandBuffer*, int stage, int first, int count, const NVN2separateSamplerHandle*);
typedef void (*PFNNVN2COMMANDBUFFERSETSTENCILCULLCRITERIAPROC)(NVN2commandBuffer*, int stencilFunc, int ref, int mask);
typedef void (*PFNNVN2COMMANDBUFFERSETPROVOKINGVERTEXMODEPROC)(NVN2commandBuffer*, int mode);
typedef void (*PFNNVN2COMMANDBUFFERSETCOLORREDUCTIONENABLEPROC)(NVN2commandBuffer*, NVN2boolean enable);
typedef void (*PFNNVN2COMMANDBUFFERSETCOLORREDUCTIONTHRESHOLDSPROC)(NVN2commandBuffer*, int formatClass /* 0-5 */, int conservative, int aggressive);
typedef void (*PFNNVN2COMMANDBUFFEROVERRIDESUBTILESIZEPROC)(NVN2commandBuffer*, NVN2boolean enable, int size /* 1-128 */);
typedef void (*PFNNVN2COMMANDBUFFERSETZCULLZF32COMPRESSIONENABLEPROC)(NVN2commandBuffer*, NVN2boolean enable);

/* ---- NVX (separate lookup, sdk+0x5b0770; not in the 698-name table) ---- */
typedef void (*PFNNVN2COMMANDBUFFERCOPYTEXTURETOTEXTUREWITHCOPYENGINENVXPROC)(NVN2commandBuffer*, const NVN2texture *src,
                                                                               const NVN2copyRegion *srcRegion,
                                                                               const NVN2texture *dst,
                                                                               const NVN2copyRegion *dstRegion);


/* ==== QueueWindowSync ==== */
/* NVN2 (API 231.16, Mario Kart World 1.0.0): Queue, QueueBuilder, Window, WindowBuilder, Sync, Event, EventBuilder.
 * Clean-room declarations from docs/reference/nvn2.md. Sizes: the reserved size MKW allocates
 * (likely the NVN2 header's), with the bytes the driver actually touches in the comment. */

typedef uint8_t NVN2boolean;
typedef struct NVN2device NVN2device;          /* defined in its own section */
typedef struct NVN2texture NVN2texture;        /* defined in its own section */
typedef struct NVN2memoryPool NVN2memoryPool;  /* defined in its own section */
typedef void *NVN2nativeWindow;                /* nn::vi::GetNativeWindow result (vi2 TLayerNativeWindow / gs INativeWindow) */
typedef void *NVN2nvRmSync;                    /* NvRmSync handle (NVX interop) */
typedef uint64_t NVN2commandHandle;

/* Sizes: high confidence for the driver-used sizes; reserved sizes inferred from MKW allocations. */
typedef struct NVN2queue { uint8_t opaque[0x3000]; } NVN2queue;          /* driver memset 0x2cc0; MKW allocates 0x3000 */
typedef struct NVN2queueBuilder { uint8_t opaque[0x48]; } NVN2queueBuilder;     /* driver uses 0x48; MKW stack slot <= 0x50 (reserve uncertain: 0x48 or 0x50) */
typedef struct NVN2window { uint8_t opaque[0x280]; } NVN2window;          /* driver memset 0x218; MKW allocates 0x280 */
typedef struct NVN2windowBuilder { uint8_t opaque[0x80]; } NVN2windowBuilder;    /* driver uses 0x60; MKW stack spacing 0x80 */
typedef struct NVN2sync { uint8_t opaque[0x40]; } NVN2sync;             /* driver uses 0x38; MKW allocates 0x40 */
typedef struct NVN2event { uint8_t opaque[0x40]; } NVN2event;            /* driver uses 0x28; MKW stride 0x40 */
typedef struct NVN2eventBuilder { uint8_t opaque[0x20]; } NVN2eventBuilder;     /* driver uses 0x10; reserve assumed unchanged from NVN1 */

typedef struct { int32_t x, y, width, height; } NVN2rectangle;
/* GetError (MMU fault only) writes +0 u64 fault address and +8 u32 access type (0/1); total size unknown (nvn.h leaves it undefined). */
typedef struct { uint64_t faultAddress; int32_t accessType; int32_t reserved[5]; } NVN2queueErrorInfo;

/* Queue builder flags (bit 0/1 as NVN1; 0x4/0x8/0x10 names are guesses, behaviour established). */
enum {
    NVN2_QUEUE_BUILDER_FLAGS_NONE = 0x0,
    NVN2_QUEUE_BUILDER_FLAGS_NO_FRAGMENT_INTERLOCK = 0x1,
    NVN2_QUEUE_BUILDER_FLAGS_NO_ZCULL = 0x2,
    NVN2_QUEUE_BUILDER_FLAGS_SUBCONTEXT = 0x4,        /* needs SetPrimaryQueue; channel is a TSG subcontext of the primary */
    NVN2_QUEUE_BUILDER_FLAGS_ALLOW_SUBCONTEXTS = 0x8, /* required on the primary of a SUBCONTEXT queue; +0x39100 queue memory */
    NVN2_QUEUE_BUILDER_FLAGS_NO_GRAPHICS = 0x10       /* no TSG, other engine class, no context memory [INFERENCE: copy/async] */
};

/* Priority values the driver maps to TSG timeslices (10 -> 0x80, 1000 -> 0x3fc00, other -> 0x400). Default 100. */
enum { NVN2_QUEUE_PRIORITY_LOW = 10, NVN2_QUEUE_PRIORITY_NORMAL = 100, NVN2_QUEUE_PRIORITY_HIGH = 1000 };

/* Default memory sizes applied when not set. */
enum {
    NVN2_QUEUE_DEFAULT_COMMAND_MEMORY = 0x10000,
    NVN2_QUEUE_DEFAULT_CONTROL_MEMORY = 0x4000,
    NVN2_QUEUE_DEFAULT_COMPUTE_MEMORY = 0x40000
};

typedef enum {
    NVN2_QUEUE_GET_ERROR_RESULT_GPU_NO_ERROR = 0,
    NVN2_QUEUE_GET_ERROR_RESULT_GPU_ERROR_UNKNOWN = 1,
    NVN2_QUEUE_GET_ERROR_RESULT_GPU_ERROR_MMU_FAULT = 2,
    NVN2_QUEUE_GET_ERROR_RESULT_GPU_ERROR_PBDMA_EXCEPTION = 3,
    NVN2_QUEUE_GET_ERROR_RESULT_GPU_ERROR_ENGINE_EXCEPTION = 4,
    NVN2_QUEUE_GET_ERROR_RESULT_GPU_ERROR_TIMEOUT = 5
} NVN2queueGetErrorResult;

typedef enum { NVN2_SYNC_CONDITION_ALL_GPU_COMMANDS_COMPLETE = 0, NVN2_SYNC_CONDITION_GRAPHICS_WORLD_SPACE_COMPLETE = 1 } NVN2syncCondition;
enum { NVN2_SYNC_FLAG_FLUSH_FOR_CPU = 0x1 };
typedef enum {
    NVN2_SYNC_WAIT_RESULT_ALREADY_SIGNALED = 0,
    NVN2_SYNC_WAIT_RESULT_CONDITION_SATISFIED = 1,
    NVN2_SYNC_WAIT_RESULT_TIMEOUT_EXPIRED = 2,
    NVN2_SYNC_WAIT_RESULT_FAILED = 3
} NVN2syncWaitResult;
typedef enum { NVN2_WINDOW_ACQUIRE_TEXTURE_RESULT_SUCCESS = 0, NVN2_WINDOW_ACQUIRE_TEXTURE_RESULT_NATIVE_ERROR = 1 } NVN2windowAcquireTextureResult;
typedef enum { NVN2_EVENT_SIGNAL_MODE_WRITE = 0, NVN2_EVENT_SIGNAL_MODE_ADD = 1 } NVN2eventSignalMode;

/* Driver-internal sync type at NVN2sync+0x08 (useful for a host that must interpret game-visible syncs). */
enum {
    NVN2_SYNC_TYPE_NONE = 0, NVN2_SYNC_TYPE_SIGNALED = 1, NVN2_SYNC_TYPE_QUEUE_FENCE = 2,
    NVN2_SYNC_TYPE_CUDA_SEMAPHORE = 3, NVN2_SYNC_TYPE_GL = 4, NVN2_SYNC_TYPE_SYNCPOINTS = 5
};
typedef struct { uint32_t id, value; } NVN2syncpointFence;

/* Window formats accepted by the display path (nn::wsi::ounce::native::NativeWindow::Initialize). */
enum {
    NVN2_WINDOW_FORMAT_RGBA8 = 0x25, NVN2_WINDOW_FORMAT_RGBA16F = 0x29, NVN2_WINDOW_FORMAT_RGBX8_SRGB = 0x37,
    NVN2_WINDOW_FORMAT_RGBA8_SRGB = 0x38, NVN2_WINDOW_FORMAT_RGB10A2 = 0x3D, NVN2_WINDOW_FORMAT_RGBX8 = 0x6D,
    NVN2_WINDOW_FORMAT_RGBX16F = 0x71, NVN2_WINDOW_FORMAT_BGRA8 = 0x9A, NVN2_WINDOW_FORMAT_BGRA8_SRGB = 0x9C
};

/* ---- QueueBuilder: new functions and those whose ABI is not in NVN1's nvn.h ---- */
typedef void (*PFNNVN2QUEUEBUILDERSETPRIMARYQUEUEPROC)(NVN2queueBuilder *, NVN2queue *primary);
typedef void (*PFNNVN2QUEUEBUILDERSETRAYTRACINGMEMORYSIZEPROC)(NVN2queueBuilder *, int size);
typedef NVN2boolean (*PFNNVN2QUEUEBUILDERGETRAYTRACINGMEMORYSIZEPROC)(const NVN2queueBuilder *, int *size);
typedef NVN2boolean (*PFNNVN2QUEUEBUILDERGETCOMMANDMEMORYSIZEPROC)(const NVN2queueBuilder *, int *size);
typedef NVN2boolean (*PFNNVN2QUEUEBUILDERGETCOMPUTEMEMORYSIZEPROC)(const NVN2queueBuilder *, int *size);
typedef NVN2boolean (*PFNNVN2QUEUEBUILDERGETCONTROLMEMORYSIZEPROC)(const NVN2queueBuilder *, int *size);
typedef NVN2boolean (*PFNNVN2QUEUEBUILDERGETCOMMANDFLUSHTHRESHOLDPROC)(const NVN2queueBuilder *, int *size);
typedef int (*PFNNVN2QUEUEBUILDERGETMEMORYSIZEPROC)(const NVN2queueBuilder *);
typedef void *(*PFNNVN2QUEUEBUILDERGETMEMORYPROC)(const NVN2queueBuilder *);
typedef void (*PFNNVN2QUEUEBUILDERSETQUEUEPRIORITYPROC)(NVN2queueBuilder *, int priority);
typedef int (*PFNNVN2QUEUEBUILDERGETQUEUEPRIORITYPROC)(const NVN2queueBuilder *);
typedef NVN2device *(*PFNNVN2QUEUEBUILDERGETDEVICEPROC)(const NVN2queueBuilder *);
typedef int (*PFNNVN2QUEUEBUILDERGETFLAGSPROC)(const NVN2queueBuilder *);
/* Unchanged from NVN1 (sizes are stored as 32-bit by the driver): SetDevice, SetDefaults, SetFlags,
 * SetCommand/Compute/ControlMemorySize, SetCommandFlushThreshold, SetQueueMemory, GetQueueMemorySize. */

/* ---- Queue ---- */
typedef size_t (*PFNNVN2QUEUEGETTOTALRAYTRACINGMEMORYUSEDPROC)(NVN2queue *);
typedef void (*PFNNVN2QUEUESETTIMEOUTNVXPROC)(NVN2queue *, uint64_t timeoutNs);           /* NvRmGpuChannelSetTimeout(ns / 1e6 ms) */
typedef NVN2boolean (*PFNNVN2QUEUESETTIMESLICENVXPROC)(NVN2queue *, uint32_t timeslice);  /* sdk+0x5c2f70; scaled by device info, TSG timeslice */
/* Unchanged from NVN1: Initialize, Finalize, SetDebugLabel (no-op), SubmitCommands, Flush, Finish, GetError,
 * GetTotalCommand/Compute/ControlMemoryUsed, ResetMemoryUsageCounts, FenceSync, WaitSync, PresentTexture.
 * Removed in NVN2: nvnQueueAcquireTexture. */

/* ---- WindowBuilder ---- */
typedef void (*PFNNVN2WINDOWBUILDERSETNUMACTIVETEXTURESPROC)(NVN2windowBuilder *, int);
typedef int (*PFNNVN2WINDOWBUILDERGETNUMACTIVETEXTURESPROC)(const NVN2windowBuilder *);
typedef int (*PFNNVN2WINDOWBUILDERGETNUMTEXTURESPROC)(const NVN2windowBuilder *);
typedef NVN2texture *(*PFNNVN2WINDOWBUILDERGETTEXTUREPROC)(const NVN2windowBuilder *, int index);
typedef NVN2device *(*PFNNVN2WINDOWBUILDERGETDEVICEPROC)(const NVN2windowBuilder *);
typedef void (*PFNNVN2WINDOWBUILDERSETCOLORSPACEPROC)(NVN2windowBuilder *, int colorSpace);
typedef int (*PFNNVN2WINDOWBUILDERGETCOLORSPACEPROC)(const NVN2windowBuilder *);
/* 10 floats: 8 in s0-s7, the 9th and 10th on the stack (AAPCS64). Default: 0.64 0.33 0.30 0.60 0.15 0.06 0.3127 0.329 0.03 1000.0
 * [INFERENCE order: red xy, green xy, blue xy, white xy, min luminance, max luminance]. */
typedef void (*PFNNVN2WINDOWBUILDERSETHDRDISPLAYMETADATAPROC)(NVN2windowBuilder *, float, float, float, float, float,
                                                               float, float, float, float, float);
typedef void (*PFNNVN2WINDOWBUILDERGETHDRDISPLAYMETADATAPROC)(const NVN2windowBuilder *, float *, float *, float *, float *,
                                                               float *, float *, float *, float *, float *, float *);
typedef void (*PFNNVN2WINDOWBUILDERSETHDRCONTENTMETADATAPROC)(NVN2windowBuilder *, float, float); /* default 200.0, 60.0 */
typedef void (*PFNNVN2WINDOWBUILDERGETHDRCONTENTMETADATAPROC)(const NVN2windowBuilder *, float *, float *);
/* Unchanged from NVN1: SetDevice, SetDefaults, SetNativeWindow, SetTextures, SetPresentInterval, GetNativeWindow, GetPresentInterval. */

/* ---- Window ---- */
typedef void (*PFNNVN2WINDOWSETNUMACTIVETEXTURESPROC)(NVN2window *, int);
typedef int (*PFNNVN2WINDOWGETNUMACTIVETEXTURESPROC)(const NVN2window *);
typedef int (*PFNNVN2WINDOWGETNUMTEXTURESPROC)(const NVN2window *);
typedef void (*PFNNVN2WINDOWSETHDRDISPLAYMETADATAPROC)(NVN2window *, float, float, float, float, float,
                                                        float, float, float, float, float);
typedef void (*PFNNVN2WINDOWGETHDRDISPLAYMETADATAPROC)(const NVN2window *, float *, float *, float *, float *,
                                                        float *, float *, float *, float *, float *, float *);
typedef void (*PFNNVN2WINDOWSETHDRCONTENTMETADATAPROC)(NVN2window *, float, float);
typedef void (*PFNNVN2WINDOWGETHDRCONTENTMETADATAPROC)(const NVN2window *, float *, float *);
/* Unchanged from NVN1: Initialize, Finalize, SetDebugLabel (no-op), AcquireTexture (always SUCCESS), GetNativeWindow,
 * Get/SetPresentInterval, SetCrop (w or h == 0 resets to texture 0 size), GetCrop. */

/* ---- Sync ---- */
typedef NVN2boolean (*PFNNVN2SYNCINITIALIZEFROMFENCEDGLSYNCPROC)(NVN2sync *, NVN2device *, uint64_t glSync, int *error);
typedef int (*PFNNVN2SYNCCREATEGLSYNCPROC)(const NVN2sync *, uint64_t *glSync);
typedef NVN2boolean (*PFNNVN2SYNCINITIALIZEFROMNVRMSYNCNVXPROC)(NVN2sync *, NVN2device *, NVN2nvRmSync);
typedef NVN2boolean (*PFNNVN2SYNCGETNVRMSYNCNVXPROC)(const NVN2sync *, NVN2nvRmSync);
typedef NVN2boolean (*PFNNVN2SYNCGETSYNCPOINTSCOUNTNVXPROC)(const NVN2sync *, int *count);
typedef NVN2boolean (*PFNNVN2SYNCGETSYNCPOINTSNVXPROC)(const NVN2sync *, int count, NVN2syncpointFence *out);  /* sdk+0x5c7df0 */
typedef NVN2boolean (*PFNNVN2SYNCSETSYNCPOINTSNVXPROC)(NVN2sync *, int count, const NVN2syncpointFence *in);
/* Unchanged from NVN1: Initialize, Finalize, SetDebugLabel (no-op), Wait (timeout in ns). */

/* ---- Event / EventBuilder ---- */
typedef const NVN2memoryPool *(*PFNNVN2EVENTBUILDERGETSTORAGEPROC)(const NVN2eventBuilder *, int64_t *offset);
typedef const NVN2memoryPool *(*PFNNVN2EVENTBUILDERGETMEMORYPOOLPROC)(const NVN2eventBuilder *);
typedef int64_t (*PFNNVN2EVENTBUILDERGETMEMORYOFFSETPROC)(const NVN2eventBuilder *);
typedef const NVN2memoryPool *(*PFNNVN2EVENTGETMEMORYPOOLPROC)(const NVN2event *);
typedef int64_t (*PFNNVN2EVENTGETMEMORYOFFSETPROC)(const NVN2event *);
/* Unchanged from NVN1: EventBuilderSetDefaults, EventBuilderSetStorage, EventInitialize, EventFinalize (no-op),
 * EventGetValue, EventSignal. */


/* ==== ProgramDlss ==== */
/* NVN2 Program / ProgramBuilder / Dlss / DeviceCommandBuffer / DeviceMessageBuffer / interception and interop NVX names.
 * Clean-room declarations from docs/reference/nvn2.md (Mario Kart World 1.0.0); sdk offsets cited there.
 * Objects owned by other families (device, memory pool, texture, texture view, texture pool, command buffer) are passed as void pointers. */

/* ---- Object sizes ---- */
typedef struct NVN2program { uint8_t opaque[0xA0]; } NVN2program;               /* sure: Initialize zeroes 0xa0; MKW stores programs at stride 0xa0 (NVN1 reserved 0xC0) */
typedef struct NVN2programBuilder { uint8_t opaque[0x80]; } NVN2programBuilder;        /* likely: driver uses 0x08..0x63; MKW zeroes 0x80 before SetDefaults */
typedef struct NVN2dlssNetworkBuilder { uint8_t opaque[0xC0]; } NVN2dlssNetworkBuilder;    /* sure: SetDefaults writes 0x08..0xbf, SetDevice 0x00 */
typedef struct NVN2dlssNetwork { uint8_t opaque[0x218]; } NVN2dlssNetwork;          /* sure: Initialize clears exactly 0x218 */
typedef struct NVN2dlssParameters { uint8_t opaque[0xC8]; } NVN2dlssParameters;        /* sure: SetDefaults writes 0x00..0xc7 */
typedef struct NVN2deviceCommandBuffer { uint8_t opaque[0x30]; } NVN2deviceCommandBuffer;   /* minimum: driver touches 0x00..0x2f; reserved size not observed */
typedef struct NVN2deviceMessageBuffer { uint8_t opaque[0x28]; } NVN2deviceMessageBuffer;   /* minimum: driver touches 0x00..0x27; reserved size not observed */

/* ---- Shader stages (control +0x56) ---- */
typedef enum {
    NVN2_SHADER_STAGE_VERTEX = 0,
    NVN2_SHADER_STAGE_FRAGMENT = 1,
    NVN2_SHADER_STAGE_GEOMETRY = 2,
    NVN2_SHADER_STAGE_TESS_CONTROL = 3,
    NVN2_SHADER_STAGE_TESS_EVALUATION = 4,
    NVN2_SHADER_STAGE_COMPUTE = 5,
    NVN2_SHADER_STAGE_TASK = 6,
    NVN2_SHADER_STAGE_MESH = 7,
    NVN2_SHADER_STAGE_RAY_TRACING = 8      /* only through ProgramBuilderSetRayTracingModules; SetShaders rejects it */
} NVN2shaderStage;

/* nvnProgramSetSampleShading / ProgramBuilderSetSampleShading (program+0x80 bits 28-31) */
typedef enum {
    NVN2_SAMPLE_SHADING_DEFAULT = 0,       /* keep control +0x5a */
    NVN2_SAMPLE_SHADING_ENABLE = 1,
    NVN2_SAMPLE_SHADING_DISABLE = 2
} NVN2sampleShadingMode;

/* nvnDeviceGetInteger indices MKW queries before SetShaders (main+0x1675200) */
enum {
    NVN2_DEVICE_INFO_GPU_CODE_MAX_MAJOR = 0x2E,   /* 0xc9 */
    NVN2_DEVICE_INFO_GPU_CODE_MIN_MAJOR = 0x2F,   /* 0xc9 */
    NVN2_DEVICE_INFO_GPU_CODE_MAX_MINOR = 0x30,   /* 11 */
    NVN2_DEVICE_INFO_GPU_CODE_MIN_MINOR = 0x31    /* 0 */
};

/* ---- Control section ---- */
#define NVN2_CONTROL_MAGIC 0x98761234u   /* +0x00 */
enum {
    NVN2_CONTROL_MAJOR = 0xC9,              /* +0x04 */
    NVN2_CONTROL_MAX_VERSION = 11,          /* +0x08: 0-1, 2, 3-11 are the three parser classes */
    NVN2_CONTROL_SIZE = 0xD8,               /* fixed header of versions 3-11 (0xD0 for 0-2); host key bytes */
    NVN2_GPU_ARCH = 0x170,                  /* +0x0c must equal device+0x54 (MKW data) */
    NVN2_GPU_IMPL = 0xE                     /* +0x0e must equal device+0x58 (MKW data) */
};

typedef struct {
    uint32_t magic;                 /* 0x00 */
    uint32_t major;                 /* 0x04 0xc9 */
    uint32_t version;               /* 0x08 */
    uint16_t gpu_arch, gpu_impl;    /* 0x0c, 0x0e */
    uint64_t unk10;                 /* 0x10 per-stage table +0x28; NVX debug-data-hash getter for compute */
    uint64_t unk18;                 /* 0x18 */
    uint8_t debug_hash[16];         /* 0x20 ignored by the driver */
    uint32_t length;                /* 0x30 control length incl. trailing blobs; ignored */
    uint32_t unk34;                 /* 0x34 */
    uint32_t code_end;              /* 0x38 end of instructions in the code area */
    uint32_t c1_size;               /* 0x3c */
    uint32_t c1_start;              /* 0x40 */
    uint32_t code_size;             /* 0x44 code-area size */
    uint32_t entry;                 /* 0x48 program entry offset */
    uint32_t registers;             /* 0x4c */
    uint32_t local_memory;          /* 0x50 scratch bytes */
    uint8_t xfb_buffers;            /* 0x54 transform-feedback buffer count */
    uint8_t unk55;                  /* 0x55 */
    uint8_t stage;                  /* 0x56 NVN2shaderStage */
    uint8_t pad57;
    union {                         /* 0x58 */
        uint32_t vertex[6];                                         /* split program: [0] entry,[1] end, [3] entry2,[4] end2,[5] registers2 */
        struct { uint8_t flags[9]; uint8_t unk61, color_mask; uint8_t pad63; float unk64[4]; } fragment;   /* flags[2] (+0x5a) = per-sample shading */
        struct { uint32_t local_size[3], shared_memory, unk68, unk6c, unk70, unk74; } compute;
        struct { uint16_t local_size, payload_size, unk5c; } task;
        struct { uint16_t local_size, task_payload_size, unk5c; uint8_t unk5e, unk5f; uint16_t max_vertices, max_primitives; uint32_t shared_memory; uint16_t unk68; } mesh;
        struct { uint32_t link_flags, blob_size, blob_offset, code_end; uint8_t unk68[4], unk6c[4], unk70[7]; } ray_tracing;
        uint8_t raw[0x20];
    } u;
    uint32_t unk78;                 /* 0x78 */
    uint32_t unk7c[4];              /* 0x7c */
    uint32_t unk8c;                 /* 0x8c */
    uint8_t unk90[4];               /* 0x90..0x93 */
    uint8_t uses_subroutines;       /* 0x94 */
    uint8_t pad95[3];
    uint32_t unk98, unk9c;          /* 0x98 (length copy), 0x9c */
    uint8_t unka0[7];               /* 0xa0..0xa6 */
    uint8_t unka7;                  /* 0xa7 transform-feedback encoding select; required for compute on flag-bit-18 devices */
    uint8_t compute_device_launch;  /* 0xa8 ==1: GetDeviceComputeMemorySize non-zero */
    uint8_t unka9[8];               /* 0xa9..0xb0 */
    uint8_t unkb1[8];               /* 0xb1..0xb8 */
    uint8_t padb9;
    uint16_t compute_device_memory; /* 0xba */
    uint32_t blob_offset, blob_size;    /* 0xbc, 0xc0 not read by the driver */
    uint32_t xfb_offset;            /* 0xc4 */
    uint32_t debug_offset, debug_size;  /* 0xc8, 0xcc */
    uint8_t unkd0, unkd1;           /* 0xd0, 0xd1 (versions 3+) */
    uint8_t padd2[6];
} NVN2controlSection;
_Static_assert(sizeof(NVN2controlSection) == 0xD8, "NVN2 control header");
_Static_assert(offsetof(NVN2controlSection, stage) == 0x56, "stage");
_Static_assert(offsetof(NVN2controlSection, u) == 0x58, "union");
_Static_assert(offsetof(NVN2controlSection, compute_device_memory) == 0xBA, "+0xba");
_Static_assert(offsetof(NVN2controlSection, debug_offset) == 0xC8, "+0xc8");

/* ---- Shader records ---- */
typedef struct { uint64_t data; const void *control; } NVN2shaderData;     /* unchanged from NVN1 */
typedef struct { uint32_t build_id[4]; uint32_t debug_hash_lo, debug_hash_hi; } NVN2debugDataHash;   /* 24 bytes; driver uses the last 8 */
typedef struct { uint64_t data; const void *control; const NVN2debugDataHash *debug_hash; } NVN2shaderDataExt;   /* 24 bytes */
typedef struct { int32_t index[3]; } NVN2shaderGroupIndices;               /* passed by value in x1:x2; -1 = none */
_Static_assert(sizeof(NVN2debugDataHash) == 24, "hash record");
_Static_assert(sizeof(NVN2shaderDataExt) == 24, "ext record");

/* ---- Program (new or changed) ---- */
typedef uint8_t (*PFNNVN2PROGRAMINITIALIZEFROMBUILDERPROC)(NVN2program *program, const NVN2programBuilder *builder);
typedef uint8_t (*PFNNVN2PROGRAMSETSHADERSEXTPROC)(NVN2program *program, int count, const NVN2shaderDataExt *shaders);  /* count <= 9 */
typedef void (*PFNNVN2PROGRAMSETSAMPLESHADINGPROC)(NVN2program *program, NVN2sampleShadingMode mode);
typedef uint8_t (*PFNNVN2PROGRAMGETSUBTILESIZEPROC)(const NVN2program *program);      /* returns program byte +0 */
typedef int (*PFNNVN2PROGRAMGETDEVICECOMPUTEMEMORYSIZEPROC)(const NVN2program *program);
typedef int (*PFNNVN2PROGRAMGETSCRATCHBYTESPERWARPPROC)(const NVN2program *program);
typedef void (*PFNNVN2PROGRAMGETSHADERGROUPHANDLEPROC)(const NVN2program *program, NVN2shaderGroupIndices group, void *handle32);
typedef uint64_t (*PFNNVN2PROGRAMGETDEBUGDATAHASHESNVXPROC)(const NVN2program *program);

/* ---- ProgramBuilder (all new) ---- */
typedef void (*PFNNVN2PROGRAMBUILDERSETDEFAULTSPROC)(NVN2programBuilder *b);
typedef void (*PFNNVN2PROGRAMBUILDERSETDEVICEPROC)(NVN2programBuilder *b, void *device);
typedef void *(*PFNNVN2PROGRAMBUILDERGETDEVICEPROC)(const NVN2programBuilder *b);
typedef void (*PFNNVN2PROGRAMBUILDERSETSAMPLESHADINGPROC)(NVN2programBuilder *b, NVN2sampleShadingMode mode);
typedef NVN2sampleShadingMode (*PFNNVN2PROGRAMBUILDERGETSAMPLESHADINGPROC)(const NVN2programBuilder *b);
typedef void (*PFNNVN2PROGRAMBUILDERSETDEBUGDATAHASHESPROC)(NVN2programBuilder *b, int count, const NVN2debugDataHash *hashes);
typedef const NVN2debugDataHash *(*PFNNVN2PROGRAMBUILDERGETDEBUGDATAHASHESPROC)(const NVN2programBuilder *b);
typedef void (*PFNNVN2PROGRAMBUILDERSETSHADERSPROC)(NVN2programBuilder *b, int count, const NVN2shaderData *shaders);
typedef const NVN2shaderData *(*PFNNVN2PROGRAMBUILDERGETSHADERSPROC)(const NVN2programBuilder *b);
typedef int (*PFNNVN2PROGRAMBUILDERGETSHADERCOUNTPROC)(const NVN2programBuilder *b);
typedef void (*PFNNVN2PROGRAMBUILDERSETRAYTRACINGMODULESPROC)(NVN2programBuilder *b, int count, const NVN2shaderData *modules);
typedef void (*PFNNVN2PROGRAMBUILDERSETSUBROUTINELINKAGEPROC)(NVN2programBuilder *b, int count, void *const *linkageMaps);
typedef void *const *(*PFNNVN2PROGRAMBUILDERGETSUBROUTINELINKAGEPROC)(const NVN2programBuilder *b);
typedef int (*PFNNVN2PROGRAMBUILDERGETSUBROUTINELINKAGECOUNTPROC)(const NVN2programBuilder *b);
typedef void (*PFNNVN2PROGRAMBUILDERSETMAXTRACERECURSIONDEPTHPROC)(NVN2programBuilder *b, int depth);        /* default 2 */
typedef int (*PFNNVN2PROGRAMBUILDERGETMAXTRACERECURSIONDEPTHPROC)(const NVN2programBuilder *b);
typedef void (*PFNNVN2PROGRAMBUILDERSETMAXCALLABLERECURSIONDEPTHPROC)(NVN2programBuilder *b, int depth);     /* default 2 */
typedef int (*PFNNVN2PROGRAMBUILDERGETMAXCALLABLERECURSIONDEPTHPROC)(const NVN2programBuilder *b);
typedef void (*PFNNVN2PROGRAMBUILDERSETPROGRAMRAYTRACINGFLAGSPROC)(NVN2programBuilder *b, uint32_t flags);   /* bits 0-1 used */
typedef uint32_t (*PFNNVN2PROGRAMBUILDERGETPROGRAMRAYTRACINGFLAGSPROC)(const NVN2programBuilder *b);

/* ---- DLSS (all new; texture arguments are (const NVNtexture*, const NVNtextureView*) pairs [inference]) ---- */
typedef void (*PFNNVN2COMMANDBUFFEREVALUATEDLSSPROC)(void *cmdBuf, const NVN2dlssNetwork *network, const NVN2dlssParameters *params);
typedef uint8_t (*PFNNVN2DLSSNETWORKINITIALIZEPROC)(NVN2dlssNetwork *network, const NVN2dlssNetworkBuilder *builder);
typedef void (*PFNNVN2DLSSNETWORKFINALIZEPROC)(NVN2dlssNetwork *network);
typedef void (*PFNNVN2DLSSNETWORKBUILDERSETDEFAULTSPROC)(NVN2dlssNetworkBuilder *b);
typedef void (*PFNNVN2DLSSNETWORKBUILDERSETDEVICEPROC)(NVN2dlssNetworkBuilder *b, void *device);
typedef void *(*PFNNVN2DLSSNETWORKBUILDERGETDEVICEPROC)(const NVN2dlssNetworkBuilder *b);
typedef void (*PFNNVN2DLSSNETWORKBUILDERSETFLAGSPROC)(NVN2dlssNetworkBuilder *b, uint32_t flags);
typedef uint32_t (*PFNNVN2DLSSNETWORKBUILDERGETFLAGSPROC)(const NVN2dlssNetworkBuilder *b);
typedef void (*PFNNVN2DLSSNETWORKBUILDERSETOUTPUTRESOLUTIONPROC)(NVN2dlssNetworkBuilder *b, int width, int height);
typedef void (*PFNNVN2DLSSNETWORKBUILDERGETOUTPUTRESOLUTIONPROC)(const NVN2dlssNetworkBuilder *b, int *width, int *height);
typedef void (*PFNNVN2DLSSNETWORKBUILDERSETMETHODPROC)(NVN2dlssNetworkBuilder *b, int method);
typedef int (*PFNNVN2DLSSNETWORKBUILDERGETMETHODPROC)(const NVN2dlssNetworkBuilder *b);
typedef void (*PFNNVN2DLSSNETWORKBUILDERSETPRESETPROC)(NVN2dlssNetworkBuilder *b, int preset);
typedef int (*PFNNVN2DLSSNETWORKBUILDERGETPRESETPROC)(const NVN2dlssNetworkBuilder *b);
typedef void (*PFNNVN2DLSSNETWORKBUILDERGETOPTIMALSETTINGSPROC)(const NVN2dlssNetworkBuilder *b, int *out0, int *out1, int *out2, int *out3, int *out4, int *out5);
typedef size_t (*PFNNVN2DLSSNETWORKBUILDERGETSTORAGESIZEPROC)(const NVN2dlssNetworkBuilder *b);
typedef void (*PFNNVN2DLSSNETWORKBUILDERSETSTORAGEPROC)(NVN2dlssNetworkBuilder *b, void *memory, size_t size);
typedef void *(*PFNNVN2DLSSNETWORKBUILDERGETMEMORYPROC)(const NVN2dlssNetworkBuilder *b);
typedef size_t (*PFNNVN2DLSSNETWORKBUILDERGETMEMORYSIZEPROC)(const NVN2dlssNetworkBuilder *b);
typedef void (*PFNNVN2DLSSNETWORKBUILDERSETTEXTUREIDSPROC)(NVN2dlssNetworkBuilder *b, const void *texturePool, const int ids[32]);
typedef void (*PFNNVN2DLSSNETWORKBUILDERSETMAXEVALUATIONENTRIESPROC)(NVN2dlssNetworkBuilder *b, int entries);   /* default 1 */
typedef int (*PFNNVN2DLSSNETWORKBUILDERGETMAXEVALUATIONENTRIESPROC)(const NVN2dlssNetworkBuilder *b);
typedef void (*PFNNVN2DLSSPARAMETERSSETDEFAULTSPROC)(NVN2dlssParameters *p);
typedef void (*PFNNVN2DLSSPARAMETERSSETOVERLAYDEBUGLAYERPROC)(NVN2dlssParameters *p, int value);
typedef void (*PFNNVN2DLSSPARAMETERSSETOVERLAYFULLSCREENPROC)(NVN2dlssParameters *p, int value);
typedef void (*PFNNVN2DLSSPARAMETERSSETOVERLAYSHOWNANSPROC)(NVN2dlssParameters *p, int value);
typedef void (*PFNNVN2DLSSPARAMETERSSETOVERLAYJITTERDEBUGPROC)(NVN2dlssParameters *p, int value);
typedef void (*PFNNVN2DLSSPARAMETERSSETTEXTUREPROC)(NVN2dlssParameters *p, const void *texture, const void *view);
/* the PFNNVN2DLSSPARAMETERSSETTEXTUREPROC shape serves SetInputColor, SetOutputColor, SetMotionVectors, SetDepth, SetBiasCurrentColorMask, SetExposureTexture */
typedef void (*PFNNVN2DLSSPARAMETERSSETFLOAT2PROC)(NVN2dlssParameters *p, float x, float y);          /* SetJitterOffset, SetMotionVectorScale */
typedef void (*PFNNVN2DLSSPARAMETERSGETFLOAT2PROC)(const NVN2dlssParameters *p, float *x, float *y);  /* GetJitterOffset, GetMotionVectorScale */
typedef void (*PFNNVN2DLSSPARAMETERSSETPREEXPOSUREPROC)(NVN2dlssParameters *p, float value);
typedef float (*PFNNVN2DLSSPARAMETERSGETPREEXPOSUREPROC)(const NVN2dlssParameters *p);
typedef void (*PFNNVN2DLSSPARAMETERSSETHISTORYRESETPROC)(NVN2dlssParameters *p, uint8_t reset);
typedef uint8_t (*PFNNVN2DLSSPARAMETERSGETHISTORYRESETPROC)(const NVN2dlssParameters *p);
typedef void (*PFNNVN2DLSSPARAMETERSSETEVALUATIONINDEXPROC)(NVN2dlssParameters *p, int index);
typedef int (*PFNNVN2DLSSPARAMETERSGETEVALUATIONINDEXPROC)(const NVN2dlssParameters *p);
typedef void (*PFNNVN2DLSSPARAMETERSSETINT2PROC)(NVN2dlssParameters *p, int x, int y);
/* SetInputSubrectangleSize and Set{InputColor,Depth,MotionVector,BiasCurrentColor,Output}SubrectangleBase */
typedef void (*PFNNVN2DLSSPARAMETERSGETINT2PROC)(const NVN2dlssParameters *p, int *x, int *y);   /* the matching getters */

/* ---- DeviceCommandBuffer / DeviceMessageBuffer (all new) ---- */
typedef void (*PFNNVN2DEVICECOMMANDBUFFERSETDEFAULTSPROC)(NVN2deviceCommandBuffer *d);
typedef void (*PFNNVN2DEVICECOMMANDBUFFERSETDEVICEPROC)(NVN2deviceCommandBuffer *d, void *device);
typedef void (*PFNNVN2DEVICECOMMANDBUFFERSETSTORAGEPROC)(NVN2deviceCommandBuffer *d, void *memoryPool, int64_t offset, uint64_t size);
typedef void (*PFNNVN2DEVICECOMMANDBUFFERSETCOMMANDMEMORYSIZEPROC)(NVN2deviceCommandBuffer *d, uint64_t size);
typedef void (*PFNNVN2DEVICECOMMANDBUFFERSETCOMPUTEMEMORYSIZEPROC)(NVN2deviceCommandBuffer *d, uint64_t size);
typedef uint64_t (*PFNNVN2DEVICECOMMANDBUFFERGETHANDLEPROC)(const NVN2deviceCommandBuffer *d);   /* pool GPU address + offset */
typedef void (*PFNNVN2DEVICEMESSAGEBUFFERSETDEFAULTSPROC)(NVN2deviceMessageBuffer *m);
typedef void (*PFNNVN2DEVICEMESSAGEBUFFERSETDEVICEPROC)(NVN2deviceMessageBuffer *m, void *device);
typedef void (*PFNNVN2DEVICEMESSAGEBUFFERSETSTORAGEPROC)(NVN2deviceMessageBuffer *m, void *memoryPool, int offset, int size);
typedef void (*PFNNVN2DEVICEMESSAGEBUFFERRESETPROC)(NVN2deviceMessageBuffer *m);
typedef void (*PFNNVN2DEVICEMESSAGEBUFFERBEGINMESSAGESPROC)(NVN2deviceMessageBuffer *m);
typedef uint8_t (*PFNNVN2DEVICEMESSAGEBUFFERGETNEXTMESSAGEPROC)(NVN2deviceMessageBuffer *m, int info[6], char *text, size_t textSize);
typedef void (*PFNNVN2DEVICEMESSAGEBUFFERENDMESSAGESPROC)(NVN2deviceMessageBuffer *m);

/* ---- Interception, interop and other NVX names (all new except SetExternalDebuggerEnabledNVX) ---- */
typedef void (*PFNNVN2GENERICFUNCPTRPROC)(void);
#define NVN2_DISPATCH_TABLE_ENTRIES 698     /* interception tables are 698 pointers (0x15d0 bytes), sorted-name order */
typedef uint8_t (*PFNNVN2INTERCEPTIONINITIALIZENVXPROC)(PFNNVN2GENERICFUNCPTRPROC table[NVN2_DISPATCH_TABLE_ENTRIES]);
typedef uint8_t (*PFNNVN2INTERCEPTIONSETPROCADDRESSNVXPROC)(PFNNVN2GENERICFUNCPTRPROC *table, const char *name, PFNNVN2GENERICFUNCPTRPROC fn, PFNNVN2GENERICFUNCPTRPROC *original);
typedef uint8_t (*PFNNVN2INTERCEPTIONSETDISPATCHRESETCALLBACKNVXPROC)(void *unused, PFNNVN2GENERICFUNCPTRPROC callback, PFNNVN2GENERICFUNCPTRPROC *previous);
typedef uint8_t (*PFNNVN2INTERCEPTIONTRYACQUIRENVXPROC)(PFNNVN2GENERICFUNCPTRPROC *table);
typedef uint8_t (*PFNNVN2INTERCEPTIONOWNEDNVXPROC)(PFNNVN2GENERICFUNCPTRPROC *table);
typedef uint8_t (*PFNNVN2INTERCEPTIONRELEASENVXPROC)(PFNNVN2GENERICFUNCPTRPROC *table);
typedef uint8_t (*PFNNVN2INTERCEPTIONFINALIZENVXPROC)(void);
typedef void (*PFNNVN2SETEXTERNALDEBUGGERENABLEDNVXPROC)(int enabled);   /* argument not observed: the default target is a ret */
typedef void (*PFNNVN2SETGLINTEROPNVXPROC)(void *interop);

typedef struct {
    uint16_t version;       /* in: must be 1; the driver writes 1 here and in reserved */
    uint16_t reserved;
    uint32_t result;        /* out: 0 ok, 1 version, 2 failure, 3 unsupported, 4 bad opcode */
    uint32_t opcode;        /* 0..9 */
    uint32_t pad;
    void *args;
} NVN2cudaInteropCall;
typedef void (*PFNNVN2CUDAINTEROPFUNCTIONPROC)(NVN2cudaInteropCall *call);

typedef void (*PFNNVN2H3IKR9IXPROC)(PFNNVN2GENERICFUNCPTRPROC texturePoolRegisterTexture);   /* replaces that default dispatch entry */
typedef void (*PFNNVN2YK1NUEJHPROC)(const void *texturePool, ptrdiff_t byteOffset, const uint32_t *words, size_t bytes);   /* raw descriptor write */

/* nvnCommandBufferCopyTextureToTextureWithCopyEngineNVX: see the CommandBuffer section. */

typedef struct { uint64_t address, size; uint32_t flags; uint32_t pad; } NVN2reservedAddressRange;   /* 24 bytes */
typedef void (*PFNNVN2DEVICEBUILDERSETRESERVEDADDRESSRANGESNVXPROC)(void *deviceBuilder, int count, const NVN2reservedAddressRange *ranges);
typedef int (*PFNNVN2DEVICEBUILDERGETRESERVEDADDRESSRANGESNVXPROC)(const void *deviceBuilder, const NVN2reservedAddressRange **ranges);
