# NVN2 (Switch 2 NVN)

The NVN API of Switch 2 games (NVN2), as Mario Kart World 1.0.0 (`0400C3F00006E000`, SDK `NintendoSDK_NVN-19_3_3`) uses it: how the game reaches the driver, every function it fetches, object sizes and layouts, and the paths a Metal implementation has to replace (shader programs, memory pools, present, syncs, DLSS). It is the reference for reimplementing NVN2 on Metal; the host does not implement NVN2 yet (Switch 1 NVN: [nvn-objects.md](nvn-objects.md), [rendering.md](rendering.md)).

Evidence is given as module offsets in this game's build: `sdk+0x…` in the sdk module (build ID `90ebba182862b9ecdc21fcb87b999012`, which contains the NVN2 driver) and `main+0x…` in main (`9e244438c3d8ef1c703a6a85ef99c4f0`). Offsets are into the loaded image (`dm/tools/nso.py <module> <out.bin>`; Ghidra on `dm/tools/nso2elf.py` output adds 0x100000). "NVN1" means the NVN 55 interface in `dm/gfx/nvn/include/nvn.h` that the host implements; "same ABI" means the driver uses the arguments `nvn.h` declares, with the same types. Statements marked [INFERENCE] were not checked against code.

## Entry points

### How MKW fetches NVN2 functions

- main imports two sdk symbols: the function `nvnBootstrapLoader` (sdk+0x6129c0) and the data word `nvnPrivate0` (4 bytes, sdk+0xedb150; the driver writes it from `nvnDeviceGetProcAddress`: 1 while the default dispatch table is active for a device lookup).
- At start-up (main+0xcf3a3c..0xcf3a84) main calls `nvnBootstrapLoader("nvnDeviceInitialize")` and `nvnBootstrapLoader("nvnDeviceGetProcAddress")`, then its C proc loader main+0xcf5560 `(NULL, getProcAddress)`, which calls `getProcAddress(device, name)` for **698 names** and stores each result in a function-pointer slot reached through the GOT (the pattern `dm/tools/nvn_slots_got.py` reads). After `nvnDeviceInitialize` it runs the loader again with the device (main+0xcf3ae8).
- Static use sites: 260 of the 698 slots are loaded somewhere outside the loader (3,651 sites). The other 438 are fetched and never referenced by code that `nvn_slots_got.py` can follow; treat them as callable anyway.
- 169 of the 698 names are new compared with the host's NVN1 table (`dm/gfx/nvn/names.txt` before Switch 2); 529 existed. The driver also answers 29 extra names outside the sorted table (below). All of them are in `names.txt`, so `nvnBootstrapLoader` / `nvnDeviceGetProcAddress` return a counting thunk for each (unimplemented ones log `[RED] NVN unimplemented function called` once).

### The driver's lookup

- `nvnBootstrapLoader(name)` (sdk+0x6129c0) gets the driver interface object (sdk+0x5b6850 returns sdk+0xd6a0d8) and tail-calls its first entry, `nvnDeviceGetProcAddress(NULL, name)`.
- `nvnDeviceGetProcAddress` (dispatch entry sdk+0x5b0fd0 → patchable slot sdk+0xd96748 → sdk+0x5b6610): for names starting with `nvn`, a binary search of the 698 names without their prefix (sorted, u32 string offsets at sdk+0xb79b90 into the string block at sdk+0xb74c26); hit k returns entry k of the active dispatch table (default sdk+0xd95168, 698 × 8 bytes; an interception layer can replace it). Misses go to a list of 29 extra names (sdk+0x5b5b40): `nvnCommandBufferCopyTextureToTextureWithCopyEngineNVX`, `nvnSyncInitializeFromNvRmSyncNVX`, `nvnSyncGetNvRmSyncNVX`, `nvnMemoryPoolBuilderSet/GetNativeHandleNVX`, `nvnMemoryPoolGetNativeHandleNVX`, `nvnQueueSetTimeoutNVX`, `nvnCUDAInteropFunction`, `nvnSetGLInteropNVX`, two obfuscated names (`H3ikr9ix`, `yK1NUEJH`), `nvnSetExternalDebuggerEnabledNVX`, `nvnMemoryPoolBuilderGetReservedAddressesNVX`, `nvnProgramGetDebugDataHashesNVX` and the `nvnInterception*NVX` family. MKW looks none of these up.
- Two table entries are themselves patchable slots (interception): `nvnDeviceInitialize` (sdk+0x5b1050 → slot sdk+0xd96738 → sdk+0x5b5750) and `nvnDeviceGetProcAddress`. The other 696 entries point straight at their implementation. The implementation offsets are listed per object below.

## Device

### Version and creation

- `nvnDeviceGetInteger` items 0 / 1 answer **231.16** (0xE7 / 0x10, sdk+0x5b57c0). main requires major == 231 and minor ≥ 16 (main+0xcf3b20 / +0xcf3b2c); on a mismatch it skips its graphics set-up (main+0xcf3d54) and leaves its 0x260-byte graphics object null.
- main's sequence (main+0xcf3a88..0xcf3b18): `nvnDeviceBuilderSetDefaults(&b)` on a stack builder, `nvnDeviceBuilderSetFlags(&b, 0)` (a debug option at [obj+0xad] picks other flags through main+0x226d8f8), allocates the device as **0x3000 bytes** (main+0xcf3abc), `nvnDeviceInitialize(dev, &b)` must return true, reloads the procs with the device, checks the version, `nvnDeviceInstallDebugCallback` only with the debug option (main+0xcf3ddc), later `nvnDeviceSetDepthMode(dev, 0)` (main+0xcf3f84). It never calls `nvnDeviceSetWindowOriginMode` statically, so the origin stays the default (0).

### Device builder and device layout

`NVNdeviceBuilder`: `nvnDeviceBuilderSetDefaults` (sdk+0x5b0c30) zeroes 0x18 bytes (NVN1 reserves 0x40; main keeps it on the stack).

| Offset | Type | Meaning | Evidence |
|---|---|---|---|
| +0x4 | u32 | flags (`SetFlags` / `GetFlags`) | sdk+0x5b0c40 |
| +0x8 | i32 | number of reserved address ranges | sdk+0x5b54e0 |
| +0x10 | ptr | array of 24-byte ranges `{u64, u64, u32}` the device reserves at initialization (`nvnDeviceBuilderSetReservedAddressRangesNVX`) | sdk+0x5b54e0 |

`NVNdevice` stays **0x3000 bytes** (main allocates 0x3000; `nvnDeviceInitialize` zeroes 0x22c8 of it, sdk+0x5b4450), so the host's `dev_t_` fits. Driver fields that matter to an implementation or to reading the driver:

| Offset | Meaning | Evidence |
|---|---|---|
| +0x0 | window origin mode | `Get/SetWindowOriginMode` |
| +0x4 | depth mode | `Get/SetDepthMode` |
| +0x8 | device flags (bits 9 / 10 enable debug layers, bit 10 a "markers" buffer) | sdk+0x5b54e0, sdk+0x5b0c50 |
| +0x10..+0x11c | GPU description copied from `NvRmGpuDeviceGetInfo` (sdk+0x5bf090): +0x54 / +0x58 GPU architecture words a shader control section must match, +0x80 / +0x84 timestamp ns numerator / denominator, +0xb0 / +0xb4 / +0xbc scratch-memory factors, +0xd8 memory-pool page size (the GPU's value, at least 64 KiB; item 0x3a) | sdk+0x5bf090, sdk+0x5b57c0 |
| +0x968 / +0x970 / +0x980 | array and count of initialized memory pools, and their mutex: the driver turns a GPU address into a CPU pointer by searching it | sdk+0x5bf8b0 |
| +0x9a0 / +0x9a8 / +0x9b0 | list and mutex of programs | sdk+0x5bf2c0 |
| +0x9d0 | NvRm GPU context (device, address space, channel) | sdk+0x5b4450 |
| +0x1df0 | ray-tracing backend object (vtable) | sdk+0x5d29c0 |

### Device information

Every item not computed from the device comes from a static table (sdk+0x59bf50); items it does not list answer 0. Bold values differ from the NVN1 table the host reports today. Items 0x2e–0x31 are the accepted shader GPU-code versions: major 201, minor 0–11 — the major version in a control section's +0x4, the minor its +0x8 ([Program](#program)) — not GLSLC's 1.x, so the host's GLSLC-based override of 0x2e / 0x30 must not apply to a Switch 2 game. Device-dependent items come from the GPU description (values depend on the NvRm answer; the host chooses them).

| Item | NVN1 name (`nvn.h`) | NVN1 value (host `g_devinfo`) | NVN2 value (MKW sdk) | MKW queries |
|---|---|---|---|---|
| 0x00 | API_MAJOR_VERSION | 55 | **231** | yes |
| 0x01 | API_MINOR_VERSION | 15 | **16** | yes |
| 0x02 | UNIFORM_BUFFER_BINDINGS_PER_STAGE | 14 | 14 |  |
| 0x03 | MAX_UNIFORM_BUFFER_SIZE | 65536 | 65536 |  |
| 0x04 | UNIFORM_BUFFER_ALIGNMENT | 256 | 256 | yes |
| 0x05 | COLOR_BUFFER_BINDINGS | 8 | 8 |  |
| 0x06 | VERTEX_BUFFER_BINDINGS | 16 | **32** |  |
| 0x07 | TRANSFORM_FEEDBACK_BUFFER_BINDINGS | 4 | 4 |  |
| 0x08 | SHADER_STORAGE_BUFFER_BINDINGS_PER_STAGE | 16 | 16 |  |
| 0x09 | TEXTURE_BINDINGS_PER_STAGE | 32 | 32 |  |
| 0x0a | COUNTER_ALIGNMENT | 16 | 16 | yes |
| 0x0b | TRANSFORM_FEEDBACK_BUFFER_ALIGNMENT | 4 | 4 |  |
| 0x0c | TRANSFORM_FEEDBACK_CONTROL_ALIGNMENT | 4 | 4 |  |
| 0x0d | INDIRECT_DRAW_ALIGNMENT | 4 | 4 | yes |
| 0x0e | VERTEX_ATTRIBUTES | 16 | **32** |  |
| 0x0f | TEXTURE_DESCRIPTOR_SIZE | 32 | 32 | yes |
| 0x10 | SAMPLER_DESCRIPTOR_SIZE | 32 | 32 | yes |
| 0x11 | RESERVED_TEXTURE_DESCRIPTORS | 256 | 256 | yes |
| 0x12 | RESERVED_SAMPLER_DESCRIPTORS | 256 | 256 | yes |
| 0x13 | COMMAND_BUFFER_COMMAND_ALIGNMENT | 4 | 4 |  |
| 0x14 | COMMAND_BUFFER_CONTROL_ALIGNMENT | 8 | 8 | yes |
| 0x15 | COMMAND_BUFFER_MIN_COMMAND_SIZE | 512 | 512 |  |
| 0x16 | COMMAND_BUFFER_MIN_CONTROL_SIZE | 256 | 256 |  |
| 0x17 | SHADER_SCRATCH_MEMORY_SCALE_FACTOR_MINIMUM | 2 | device: device+0xbc (NvRm GPU info +0x210) |  |
| 0x18 | SHADER_SCRATCH_MEMORY_SCALE_FACTOR_RECOMMENDED | 256 | device: device+0xb4 × device+0xbc | yes |
| 0x19 | SHADER_SCRATCH_MEMORY_ALIGNMENT | 4096 | 4096 | yes |
| 0x1a | SHADER_SCRATCH_MEMORY_GRANULARITY | 131072 | **16384** | yes |
| 0x1b | MAX_TEXTURE_ANISOTROPY | 16 | 16 |  |
| 0x1c | MAX_COMPUTE_WORK_GROUP_SIZE_X | 1536 | **1024** |  |
| 0x1d | MAX_COMPUTE_WORK_GROUP_SIZE_Y | 1024 | 1024 |  |
| 0x1e | MAX_COMPUTE_WORK_GROUP_SIZE_Z | 64 | 64 |  |
| 0x1f | MAX_COMPUTE_WORK_GROUP_SIZE_THREADS | 1536 | **1024** |  |
| 0x20 | MAX_COMPUTE_DISPATCH_WORK_GROUPS_X | 65535 | **2147483647** |  |
| 0x21 | MAX_COMPUTE_DISPATCH_WORK_GROUPS_Y | 65535 | 65535 |  |
| 0x22 | MAX_COMPUTE_DISPATCH_WORK_GROUPS_Z | 65535 | 65535 |  |
| 0x23 | IMAGE_BINDINGS_PER_STAGE | 8 | 8 |  |
| 0x24 | MAX_TEXTURE_POOL_SIZE | 1048576 | 1048576 |  |
| 0x25 | MAX_SAMPLER_POOL_SIZE | 4096 | 4096 |  |
| 0x26 | MAX_VIEWPORTS | 16 | 16 |  |
| 0x27 | MEMPOOL_TEXTURE_OBJECT_PAGE_ALIGNMENT | 4096 | 4096 |  |
| 0x28 | SUPPORTS_MIN_MAX_FILTERING | 1 | 1 |  |
| 0x29 | SUPPORTS_STENCIL8_FORMAT | 1 | 1 |  |
| 0x2a | SUPPORTS_ASTC_FORMATS | 1 | 1 | yes |
| 0x2b | L2_SIZE | 262144 | device: device+0x28 << 10 (L2 size in KiB) |  |
| 0x2c | MAX_TEXTURE_LEVELS | 15 | **16** |  |
| 0x2d | MAX_TEXTURE_LAYERS | 2048 | device: device+0x44 |  |
| 0x2e | GLSLC_MAX_SUPPORTED_GPU_CODE_MAJOR_VERSION | 1 | **201** | yes |
| 0x2f | GLSLC_MIN_SUPPORTED_GPU_CODE_MAJOR_VERSION | 1 | **201** | yes |
| 0x30 | GLSLC_MAX_SUPPORTED_GPU_CODE_MINOR_VERSION | 14 | **11** | yes |
| 0x31 | GLSLC_MIN_SUPPORTED_GPU_CODE_MINOR_VERSION | 5 | **0** | yes |
| 0x32 | SUPPORTS_CONSERVATIVE_RASTER | 1 | 1 |  |
| 0x33 | SUBPIXEL_BITS | 8 | 8 |  |
| 0x34 | MAX_SUBPIXEL_BIAS_BITS | 8 | 8 |  |
| 0x35 | INDIRECT_DISPATCH_ALIGNMENT | 4 | 4 | yes |
| 0x36 | ZCULL_SAVE_RESTORE_ALIGNMENT | 32 | 32 | yes |
| 0x37 | SHADER_SCRATCH_MEMORY_COMPUTE_SCALE_FACTOR_MINIMUM | 128 | device: device+0xb0 × device+0xb4 |  |
| 0x38 | LINEAR_TEXTURE_STRIDE_ALIGNMENT | 32 | device: device+0x4c |  |
| 0x39 | LINEAR_RENDER_TARGET_STRIDE_ALIGNMENT | 128 | 128 |  |
| 0x3a | MEMORY_POOL_PAGE_SIZE | 65536 | device: device+0xd8 |  |
| 0x3b | SUPPORTS_ZERO_FROM_UNMAPPED_VIRTUAL_POOL_PAGES | 1 | 1 |  |
| 0x3c | UNIFORM_BUFFER_UPDATE_ALIGNMENT | 4 | 4 |  |
| 0x3d | MAX_TEXTURE_SIZE | 16384 | device: device+0x30 |  |
| 0x3e | MAX_BUFFER_TEXTURE_SIZE | 134217728 | device: device+0x48 |  |
| 0x3f | MAX_3D_TEXTURE_SIZE | 2048 | device: device+0x34 |  |
| 0x40 | MAX_CUBE_MAP_TEXTURE_SIZE | 16384 | device: device+0x38 |  |
| 0x41 | MAX_RECTANGLE_TEXTURE_SIZE | 16384 | device: device+0x3c |  |
| 0x42 | SUPPORTS_PASSTHROUGH_GEOMETRY_SHADERS | 1 | 1 |  |
| 0x43 | SUPPORTS_VIEWPORT_SWIZZLE | 1 | 1 |  |
| 0x44 | SUPPORTS_SPARSE_TILED_PACKAGED_TEXTURES | 1 | 1 |  |
| 0x45 | SUPPORTS_ADVANCED_BLEND_MODES | 1 | 1 |  |
| 0x46 | MAX_PRESENT_INTERVAL | 5 | 5 |  |
| 0x47 | SUPPORTS_DRAW_TEXTURE | 1 | 1 |  |
| 0x48 | SUPPORTS_TARGET_INDEPENDENT_RASTERIZATION | 1 | 1 |  |
| 0x49 | SUPPORTS_FRAGMENT_COVERAGE_TO_COLOR | 1 | 1 |  |
| 0x4a | SUPPORTS_POST_DEPTH_COVERAGE | 1 | 1 |  |
| 0x4b | SUPPORTS_IMAGES_USING_TEXTURE_HANDLES | 1 | 1 |  |
| 0x4c | SUPPORTS_SAMPLE_LOCATIONS | 1 | 1 |  |
| 0x4d | MAX_SAMPLE_LOCATION_TABLE_ENTRIES | 16 | 16 |  |
| 0x4e | SHADER_CODE_MEMORY_POOL_PADDING_SIZE | 1024 | **2048** |  |
| 0x4f | MAX_PATCH_SIZE | 32 | 32 |  |
| 0x50 | QUEUE_COMMAND_MEMORY_GRANULARITY | 4096 | 4096 |  |
| 0x51 | QUEUE_COMMAND_MEMORY_MIN_SIZE | 65536 | 65536 | yes |
| 0x52 | QUEUE_COMMAND_MEMORY_DEFAULT_SIZE | 65536 | 65536 |  |
| 0x53 | QUEUE_COMPUTE_MEMORY_GRANULARITY | 256 | 256 |  |
| 0x54 | QUEUE_COMPUTE_MEMORY_MIN_SIZE | 16384 | 16384 |  |
| 0x55 | QUEUE_COMPUTE_MEMORY_DEFAULT_SIZE | 262144 | 262144 | yes |
| 0x56 | QUEUE_COMMAND_MEMORY_MIN_FLUSH_THRESHOLD | 4096 | 4096 |  |
| 0x57 | SUPPORTS_FRAGMENT_SHADER_INTERLOCK | 1 | 1 |  |
| 0x58 | MAX_TEXTURES_PER_WINDOW | 4 | 4 |  |
| 0x59 | MIN_TEXTURES_PER_WINDOW | 2 | 2 |  |
| 0x5a | SUPPORTS_DEBUG_LAYER | 0 | 0 | yes |
| 0x5b | QUEUE_CONTROL_MEMORY_MIN_SIZE | 4096 | 4096 | yes |
| 0x5c | QUEUE_CONTROL_MEMORY_DEFAULT_SIZE | 16384 | 16384 |  |
| 0x5d | QUEUE_CONTROL_MEMORY_GRANULARITY | 4096 | 4096 |  |
| 0x5e | SEPARATE_TEXTURE_BINDINGS_PER_STAGE | 128 | 128 |  |
| 0x5f | SEPARATE_SAMPLER_BINDINGS_PER_STAGE | 4096 | **32** |  |
| 0x60 | DEBUG_GROUPS_MAX_DOMAIN_ID |  | 65535 |  |
| 0x61 | EVENTS_SUPPORT_REDUCTION_OPERATIONS |  | 1 |  |
| 0x62 |  |  | device: device+0xc4 |  |
| 0x63 |  |  | 256 |  |
| 0x64 |  |  | 16 |  |
| 0x65 |  |  | 65535 |  |
| 0x66 |  |  | 65535 |  |
| 0x67 |  |  | 256 |  |
| 0x68 |  |  | 1 |  |
| 0x69 |  |  | 16 |  |
| 0x6a |  |  | 16 |  |
| 0x6b |  |  | 16 |  |
| 0x6c |  |  | 1 |  |
| 0x6d |  |  | 1 |  |
| 0x6e |  |  | 1 |  |
| 0x6f |  |  | 256 | yes |
| 0x70 |  |  | not answered (0) |  |
| 0x71 |  |  | 16 |  |
| 0x72 |  |  | 8 |  |
| 0x73 |  |  | 16 |  |
| 0x74 |  |  | 32 |  |
| 0x75 |  |  | 31 |  |
| 0x76 |  |  | 536870912 |  |
| 0x77 |  |  | 16777216 |  |
| 0x78 |  |  | 16777216 |  |
| 0x79 |  |  | 1073741824 |  |
| 0x7a |  |  | 32 |  |
| 0x7b |  |  | 1 |  |
| 0x7c |  |  | 64 | yes |
| 0x7d |  |  | device: from device+0xb8 (0x806→2, 0x807→4, 0x808→8, 0x809→1, 0x703/0x705→1, else 0) |  |
| 0x7e |  |  | 1 |  |
| 0x7f |  |  | 65535 |  |
| 0x80 |  |  | 32 |  |
| 0x81 |  |  | 32 |  |
| 0x82 |  |  | 65535 |  |
| 0x83 |  |  | 512 |  |
| 0x84 |  |  | 256 |  |
| 0x85 |  |  | 32 |  |
| 0x86 |  |  | 16383 |  |
| 0x87 |  |  | device: device byte +0x115 |  |
| 0x88 |  |  | 8 |  |
| 0x89 |  |  | not answered (0) |  |
| 0x8a |  |  | not answered (0) |  |
| 0x8b |  |  | 4 |  |
| 0x8c |  |  | 256 |  |
| 0x8d |  |  | 16 |  |
| 0x8e |  |  | not answered (0) |  |
| 0x8f |  |  | 1 |  |
| 0x90 |  |  | 31 |  |
| 0x91 |  |  | not answered (0) |  |
| 0x92 |  |  | 64 |  |
| 0x93 |  |  | 1 |  |
| 0x94 |  |  | 58368 |  |
| 0x95 |  |  | 24576 |  |
| 0x96 |  |  | 0 |  |
| 0x97 |  |  | 256 |  |
| 0x98 |  |  | 131072 |  |
| 0x99 |  |  | device: device+0x118 |  |
| 0x9a |  |  | device: device+0x9f0 != 0 |  |
| 0x9b |  |  | 0 |  |
| 0x9c |  |  | 201 |  |
| 0x9d |  |  | 11 |  |
| 0x9e |  |  | 201 |  |
| 0x9f |  |  | 11 |  |
| 0xa0 |  |  | 163840 |  |
| 0xa1 |  |  | 8 |  |
| 0xa2 |  |  | 4 |  |
| 0xa3 |  |  | 256 |  |
| 0xa4 |  |  | 256 |  |
| 0xa5 |  |  | 4 |  |
| 0xa6 |  |  | 65536 |  |
| 0xa7 |  |  | 201 |  |
| 0xa8 |  |  | 5 |  |
| 0xa9 |  |  | 1 |  |
| 0xaa |  |  | not answered (0) |  |
| 0xab |  |  | not answered (0) |  |
| 0xac |  |  | not answered (0) |  |
| 0xad |  |  | not answered (0) |  |
| 0xae |  |  | not answered (0) |  |
| 0xaf |  |  | not answered (0) |  |
| 0xb0 |  |  | 4096 |  |

### Device functions

| Function | sdk impl | main uses | NVN1 ABI | Semantics |
|---|---|---|---|---|
| `nvnDeviceBuilderGetFlags` | 0x5b0c20 | 0 | no NVN1 prototype in `nvn.h` | builder+4 |
| `nvnDeviceBuilderSetDefaults` | 0x5b0c30 | 1 | same | zeroes 0x18 bytes |
| `nvnDeviceBuilderSetFlags` | 0x5b0c40 | 1 | same | builder+4 |
| `nvnDeviceCalculateAccelerationStructureMemoryRequirements` | 0x5cfc50 | 0 | new | NEW (ray tracing): computes build sizes; dispatches to the ray-tracing backend object at device+0x1df0 |
| `nvnDeviceCompactAccelerationStructure` | 0x5d29e0 | 0 | new | NEW (ray tracing): device+0x1df0 vtable +0xf8 |
| `nvnDeviceCopyAccelerationStructure` | 0x5d29c0 | 0 | new | NEW (ray tracing): device+0x1df0 vtable +0xf8 |
| `nvnDeviceFinalize` | 0x5b0c50 | 0 | same | clears `nvnPrivate0`, frees the internal pools and the NvRm objects, resets the dispatch table |
| `nvnDeviceFinalizeCommandHandle` | 0x5a12e0 | 0 | same | no-op |
| `nvnDeviceGenerateDebugDomainId` | 0x5b0ed0 | 0 | same | atomic counter |
| `nvnDeviceGetCommandHandleMemorySize` | 0x5b7340 | 0 | new | NEW (device, NVNcommandHandle) → u64: (handle >> 38) & 0x3fffffc when handle & 3 == 2, else 0 (the handle encodes the byte size of its command memory) |
| `nvnDeviceGetCurrentTimestampInNanoseconds` | 0x5b0f00 | 0 | same | reads the GPU timer through the NvRm context at device+0x9d0, same conversion |
| `nvnDeviceGetDepthMode` | 0x5b0f60 | 0 | same | device+4 |
| `nvnDeviceGetFlags` | 0x5b0f70 | 0 | new | NEW: device+8 (the builder flags) |
| `nvnDeviceGetImageHandle` | 0x5b0f80 | 1 | same | returns the image ID unchanged |
| `nvnDeviceGetInteger` | 0x5b0f90 | 11 | same | sdk+0x5b57c0, table below |
| `nvnDeviceGetProcAddress` | 0x5b6610 | 0 | same | through the patchable slot sdk+0xd96748 to sdk+0x5b6610 (see above) |
| `nvnDeviceGetSeparateSamplerHandle` | 0x5b0fe0 | 0 | same | returns samplerID << 20 (sign-extended from bit 31) |
| `nvnDeviceGetSeparateTextureHandle` | 0x5b0ff0 | 0 | same | returns the texture ID unchanged |
| `nvnDeviceGetTexelFetchHandle` | 0x5b1000 | 0 | same | returns the texture ID unchanged |
| `nvnDeviceGetTextureHandle` | 0x5b1010 | 32 | same | returns textureID | samplerID << 20 (same encoding as NVN1) |
| `nvnDeviceGetTimestampInNanoseconds` | 0x5b1020 | 2 | same | (device, const NVNcounterData *): counter+8 ticks × device+0x80 / device+0x84 (a rational tick→ns factor from NvRm GPU info +0x2e8/+0x2ec; NVN1 hard-coded 19.2 MHz) |
| `nvnDeviceGetUniformBufferHandle` | 0x5d3490 | 0 | new | NEW (device, u64 gpuAddress, u64 size) → u64: gpuAddress >> 4 | ceil(size / 16) << 45 (a bindless uniform-buffer handle) |
| `nvnDeviceGetWindowOriginMode` | 0x5b1040 | 0 | same | device+0 |
| `nvnDeviceInitialize` | 0x5b5750 | 0 | same | through the patchable slot sdk+0xd96738 (interception) to sdk+0x5b5750: zeroes 0x22c8 bytes of the device, opens NvRm GPU (sdk+0x5be8a8), copies GPU info to device+0x10, creates internal pools, applies the builder's reserved address ranges |
| `nvnDeviceInstallDebugCallback` | 0x5b0ae0 | 1 | same | no-op in this (release) driver; MKW calls it only with its debug option (main+0xcf3ddc) |
| `nvnDeviceIsExternalDebuggerAttached` | 0x5b1060 | 0 | same | returns 0 |
| `nvnDeviceRegisterFastClearColor` | 0x5b1070 | 0 | same | tail call to sdk+0x5f44f0; returns a bool |
| `nvnDeviceRegisterFastClearColorIndex` | 0x5b1080 | 0 | new | NEW: the same registration returning void [INFERENCE: (device, const float color[4], format, int index)] |
| `nvnDeviceRegisterFastClearColori` | 0x5b1090 | 0 | same | returns a bool |
| `nvnDeviceRegisterFastClearColoriIndex` | 0x5b10a0 | 0 | new | NEW, as above for int colors |
| `nvnDeviceRegisterFastClearColorui` | 0x5b10b0 | 0 | same | returns a bool |
| `nvnDeviceRegisterFastClearColoruiIndex` | 0x5b10c0 | 0 | new | NEW, as above for uint colors |
| `nvnDeviceRegisterFastClearDepth` | 0x5b10d0 | 0 | same | returns a bool |
| `nvnDeviceRegisterFastClearStencil` | 0x5b10e0 | 0 | new | NEW: stencil value variant, returns a bool |
| `nvnDeviceRelocateAccelerationStructure` | 0x5d2a00 | 0 | new | NEW (ray tracing): device+0x1df0 vtable +0x108 |
| `nvnDeviceSetAutomaticCheckpointsByDebugGroup` | 0x59ee50 | 0 | no NVN1 prototype in `nvn.h` | returns 1 (no-op) |
| `nvnDeviceSetAutomaticCheckpointsEnable` | 0x59ee20 | 0 | no NVN1 prototype in `nvn.h` | returns 1 (no-op) |
| `nvnDeviceSetAutomaticCheckpointsMask` | 0x59ee30 | 0 | no NVN1 prototype in `nvn.h` | returns 1 (no-op) |
| `nvnDeviceSetAutomaticCheckpointsSamplingInterval` | 0x59ee40 | 0 | no NVN1 prototype in `nvn.h` | returns 1 (no-op) |
| `nvnDeviceSetCheckpointCallStacksEnable` | 0x59ee60 | 0 | no NVN1 prototype in `nvn.h` | returns 1 (no-op) |
| `nvnDeviceSetCheckpointMaxCallStacksDepth` | 0x59ee70 | 0 | no NVN1 prototype in `nvn.h` | returns 1 (no-op) |
| `nvnDeviceSetDebugLabel` | 0x5b0b20 | 0 | same | no-op |
| `nvnDeviceSetDepthMode` | 0x5b10f0 | 1 | same | device+4; MKW calls it with 0 (main+0xcf3f84) |
| `nvnDeviceSetIntermediateShaderCache` | 0x5b1100 | 0 | same | no-op |
| `nvnDeviceSetWindowOriginMode` | 0x5b1110 | 0 | same | device+0 (MKW has no static call: it keeps the default, 0) |
| `nvnDeviceWalkDebugDatabase` | 0x5b1120 | 0 | same | no-op |
