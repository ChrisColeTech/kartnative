# NVN2 (Switch 2 NVN)

The NVN API of Switch 2 games (NVN2), as Mario Kart World 1.0.0 (`0400C3F00006E000`, SDK `NintendoSDK_NVN-19_3_3`) uses it: how the game reaches the driver, every function it fetches, object sizes and layouts, and the paths a Metal implementation has to replace (shader programs, memory pools, present, syncs, DLSS). It is the reference for reimplementing NVN2 on Metal; the host does not implement NVN2 yet (Switch 1 NVN: [nvn-objects.md](nvn-objects.md), [rendering.md](rendering.md)).

Evidence is given as module offsets in this game's build: `sdk+0x…` in the sdk module (build ID `90ebba182862b9ecdc21fcb87b999012`, which contains the NVN2 driver) and `main+0x…` in main (`9e244438c3d8ef1c703a6a85ef99c4f0`). Offsets are into the loaded image (`dm/tools/nso.py <module> <out.bin>`; Ghidra on `dm/tools/nso2elf.py` output adds 0x100000). "NVN1" means the NVN 55 interface in `dm/gfx/nvn/include/nvn.h` that the host implements; "same ABI" means the driver uses the arguments `nvn.h` declares, with the same types. Statements marked [INFERENCE] were not checked against code.

## Entry points

### How MKW fetches NVN2 functions

- main imports two sdk symbols: the function `nvnBootstrapLoader` (sdk+0x6129c0) and the data word `nvnPrivate0` (u32, sdk+0xedb150; main reaches it through its GOT at main+0x32f40e8). The real `nvnDeviceGetProcAddress` sets it to 1 when it answers a device lookup from the driver's own dispatch table (sdk+0x5b6704); `nvnDeviceFinalize` clears it (sdk+0x5b0c7c). `nvnInstallGlobalDebugCallback` (sdk+0x5b0af0) is an empty function in this driver. main tests it at 797 places before its inlined header fast paths, which write `NVNcommandBuffer` internals (command and control pointers, command-handle words) directly; with 0 every such site calls the fetched function instead. **A host implementation must leave `nvnPrivate0` at 0** (it is sdk bss, zero at load, and only the sdk's own lookup writes it), so all NVN2 calls reach the host's functions.
- At start-up (main+0xcf3a3c..0xcf3a84) main calls `nvnBootstrapLoader("nvnDeviceInitialize")` and `nvnBootstrapLoader("nvnDeviceGetProcAddress")`, then its C proc loader main+0xcf5560 `(NULL, getProcAddress)`, which calls `getProcAddress(device, name)` for **698 names** and stores each result in a function-pointer slot reached through the GOT (the pattern `dm/tools/nvn_slots_got.py` reads). After `nvnDeviceInitialize` it runs the loader again with the device (main+0xcf3ae8).
- Static use sites: 260 of the 698 slots are loaded somewhere outside the loader (3,651 sites). The other 438 are fetched and never referenced by code that `nvn_slots_got.py` can follow; treat them as callable anyway.
- 169 of the 698 names are new compared with the host's NVN1 table (`dm/gfx/nvn/names.txt` before Switch 2); 529 existed. The driver also answers 32 extra names outside the sorted table (below). All of them are in `names.txt`, so `nvnBootstrapLoader` / `nvnDeviceGetProcAddress` return a counting thunk for each (unimplemented ones log `[RED] NVN unimplemented function called` once).

### The driver's lookup

- `nvnBootstrapLoader(name)` (sdk+0x6129c0) gets the driver interface object (sdk+0x5b6850 returns sdk+0xd6a0d8) and tail-calls its first entry, `nvnDeviceGetProcAddress(NULL, name)`.
- `nvnDeviceGetProcAddress` (dispatch entry sdk+0x5b0fd0 → patchable slot sdk+0xd96748 → sdk+0x5b6610): for names starting with `nvn`, a binary search of the 698 names without their prefix (sorted, u32 string offsets at sdk+0xb79b90 into the string block at sdk+0xb74c26); hit k returns entry k of the active dispatch table (default sdk+0xd95168, 698 × 8 bytes; an interception layer can replace it). Misses go to a list of 32 extra names (sdk+0x5b5b40): `nvnCommandBufferCopyTextureToTextureWithCopyEngineNVX`, `nvnInstallGlobalDebugCallback`, `nvnQueueSetTimesliceNVX`, `nvnSyncGetSyncpointsNVX`, `nvnSyncInitializeFromNvRmSyncNVX`, `nvnSyncGetNvRmSyncNVX`, `nvnMemoryPoolBuilderSet/GetNativeHandleNVX`, `nvnMemoryPoolGetNativeHandleNVX`, `nvnQueueSetTimeoutNVX`, `nvnCUDAInteropFunction`, `nvnSetGLInteropNVX`, two obfuscated names (`H3ikr9ix`, `yK1NUEJH`), `nvnSetExternalDebuggerEnabledNVX`, `nvnMemoryPoolBuilderGetReservedAddressesNVX`, `nvnProgramGetDebugDataHashesNVX` and the `nvnInterception*NVX` family. MKW looks none of these up.
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

Every item not computed from the device comes from a static table (sdk+0x59bf50); items it does not list answer 0. Bold values differ from the NVN1 table the host reports today. Items 0x2e–0x31 are the accepted shader GPU-code versions: major 201, minor 0–11 — the major version in a control section's +0x4, the minor its +0x8 (see the program section) — not GLSLC's 1.x, so the host's GLSLC-based override of 0x2e / 0x30 must not apply to a Switch 2 game. Device-dependent items come from the GPU description (values depend on the NvRm answer; the host chooses them).

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
| `nvnDeviceGetTextureHandle` | 0x5b1010 | 32 | same | returns textureID OR (samplerID << 20) (same encoding as NVN1) |
| `nvnDeviceGetTimestampInNanoseconds` | 0x5b1020 | 2 | same | (device, const NVNcounterData *): counter+8 ticks × device+0x80 / device+0x84 (a rational tick→ns factor from NvRm GPU info +0x2e8/+0x2ec; NVN1 hard-coded 19.2 MHz) |
| `nvnDeviceGetUniformBufferHandle` | 0x5d3490 | 0 | new | NEW (device, u64 gpuAddress, u64 size) → u64: (gpuAddress >> 4) OR (ceil(size / 16) << 45) (a bindless uniform-buffer handle) |
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

## Command buffers

The family has 202 dispatch-table entries; `nvnCommandBufferCopyTextureToTextureWithCopyEngineNVX` is no longer one of them (NVX lookup, see the end).

### Summary of what changed against NVN1

- **Object size:** MKW allocates `NVNcommandBuffer` as **0xC0** bytes (main+0xcf42a0: `alloc(0xc0, heap, 8)`, result passed straight to `nvnCommandBufferInitialize` at main+0xcf42c4). The driver initializes and uses **0x00–0xAF** (Initialize zeroes 22 qwords; the last field written is the byte at +0xA8). NVN1's `nvn.h` reserves 0xA0, so the NVN1 reservation is **too small** for NVN2. The host's `cmd_t` (0x50 bytes) still fits.
- **Header-inline fast paths gated on `nvnPrivate0`:** the game inlines parts of the NVN header (BeginRecording, CallCommands on compact handles, deferred-bind tokens) and writes driver-internal fields of the command buffer and of control memory directly, but only when `nvnPrivate0` is non-zero ([How MKW fetches NVN2 functions](#how-mkw-fetches-nvn2-functions)); with 0 it calls the fetched functions (checked at main+0x2b624 and main+0x2b71c).
- **Ungated direct write:** after `AddCommandMemory` MKW stores the byte 1 at `NVNcommandBuffer+0x10` without any gate (main+0x2b614, 0x1ebbdc, 0x221d90, 0x222bf8, 0x223564) — the same quirk the host already reserves for NVN1 games (`cmd_t.nvn_flag10`). The NVN2 driver never reads +0x10.
- **ABI breaks vs nvn.h:** `PopDebugGroup` now takes `(cmd, uint32_t domainId)`; `InsertDebugMarker` now takes `(cmd, uint32_t domainId, const char*)` (NVN1 had separate Static/Dynamic/Id variants, which are gone). `CopyBufferToBuffer` keeps 5 arguments but ignores the flags. Copy row/image strides are stored and returned as 32-bit values.
- **New recording state:** `SetRecordingMode` (MKW: 17 uses, modes 0/1/2) selects the engine a recording targets (graphics, compute, copy) and changes which commands become GPU methods and which become queue-processed control tokens.
- **Handle contract changed shape:** `NVNcommandHandle` is a tagged 64-bit value (low 2 bits): 0 = CPU pointer to a control-token stream, 1 = CPU pointer to a gather-record array with the count in bits 48–63, 2 = a single GPU segment (address in bits 2–39, word count in bits 40–63).
- **New in MKW's code path:** DrawMeshTasks (9), SetGeometryProcessingMode (9), ShadingRateImagePalette (15), BindShadingRateImage (7), SetShadingRatePaletteMode (2), PushGraphicsConstants (1), SetComputeOverlapMode (3), SetRecordingMode (17), plus NVN1 names that `nvn.h` lacks: SetCommandMemoryCallbackEnabled (5), SetColorReductionEnable/Thresholds (2 each), SetStencilCullCriteria (1), BindTexturesDeferred (1).
- Ray tracing, device-generated commands, device message buffers, DLSS, exclusive scissor, representative fragment test, conservative raster mode, conditional compute/copy enables exist but MKW never calls them (0 static uses).

### NVNcommandBuffer

**Size.** NVN2: 0xC0 reserved by the game (main+0xcf42a0), 0xB0 used by the driver (Initialize sdk+0x59ee80 zeroes `param_1[0..0x15]`, writes byte +0xA8). Getters/setters never exceed +0xA8. NVN1 `nvn.h`: 0xA0 (too small). Host `cmd_t` (nvn_obj.h): 0x50 bytes, `FITS` holds against 0xA0 and therefore 0xC0; +0x10..+0x17 already reserved for the game's byte write. The Queue embeds the same structure at queue+0x2be8 (its +0x2bf0 / +0x2c50 accesses are +0x08 / +0x68 of this layout; e.g. sdk+0x5d1510).

| offset | type | meaning | evidence |
|---|---|---|---|
| +0x00 | u8* | command write pointer (CPU) | every emitter; GetCommandMemoryUsed sdk+0x59f260 |
| +0x08 | u8* | soft limit = min(+0x20, segment start + 0x7FFFFC − 4·reserve); exceeding it calls the grow routine | AddCommandMemory sdk+0x59ef70, grow sdk+0x59f600 |
| +0x10 | u8 | not used by the driver; MKW writes 1 after AddCommandMemory | main+0x2b614 etc. |
| +0x11 | u8 | recording (IsRecording) | sdk+0x5a12d0, BeginRecording/EndRecording |
| +0x12 | u8 | “handle must be a token stream”: set by AddControlMemory during recording and by empty recordings | sdk+0x59f160, sdk+0x59f390 |
| +0x13 | u8 | flags: bit0 = last recording empty; bit3 = device+0x1c28 bit0 (texture/UBO binds add an extra method 0x8000068b) | Initialize; BindTexture sdk+0x5de120 |
| +0x14 | u8 | command-memory callback enabled (init 1) | SetCommandMemoryCallbackEnabled sdk+0x59ef60, grow sdk+0x59f600 |
| +0x15 | u8 | control-memory callback enabled (init 1) | SetControlMemoryCallbackEnabled sdk+0x59f150 |
| +0x18 | u32 | recording mode 0–4 | SetRecordingMode sdk+0x59f2c0 |
| +0x20 | u8* | command memory end (CPU) | AddCommandMemory, GetCommandMemorySize/Free |
| +0x28 | u8* | control write pointer | GetControlMemoryUsed sdk+0x59f2a0 |
| +0x30 | u8* | control end − 0x10 (16 bytes kept for the chaining token) | AddControlMemory |
| +0x38 | u8* | command memory start (CPU, = pool CPU address + offset) | AddCommandMemory |
| +0x40 | u8* | control memory start | AddControlMemory |
| +0x48 | u8* | start of the command segment not yet described by a gather record | AddCommandMemory, BeginRecording |
| +0x50 | u8* | control address of the current recording's header | BeginRecording sdk+0x59f2d0, EndRecording |
| +0x58 | u64* | current gather group header (0 = none open) | sdk+0x59f600 |
| +0x60 | u64 | command memory start GPU address (pool+0x18 + offset) | AddCommandMemory |
| +0x68 | u32 | reserved words at the end of each segment (0 for application buffers; used by the queue's internal buffer) | sdk+0x59f600 |
| +0x70 | NVNdevice* | device | Initialize; EvaluateDlss uses it |
| +0x78 | callback | memory callback | SetMemoryCallback sdk+0x59ef20 |
| +0x80 | void* | callback data | SetMemoryCallbackData sdk+0x59ef30 |
| +0x88 | — | zeroed, no family reader found | Initialize |
| +0x90 | i32 | copy row stride | SetCopyRowStride sdk+0x5b0730 |
| +0x94 | i32 | copy image stride | SetCopyImageStride sdk+0x5b0740 |
| +0x98 | ptr | lazily allocated block, freed by Finalize through device+0x1dd8 (vtable +0x140); no writer in the family | Finalize sdk+0x59eee0 |
| +0xA0 | — | zeroed, no reader found | Initialize |
| +0xA8 | u8 | device+0x08 bit18: compute-stage binds are written as GPU methods immediately (else as control tokens) | Initialize; BindTexture/BindUniformBuffer stage 5 |

### Command memory and control memory

**Command memory** holds raw GPU pushbuffer methods (NVIDIA method headers: e.g. 0x20048100 = incrementing, 4 words, subchannel 4, method 0x100). Subchannel 0 = 3D, 1 = compute, 4 = copy engine (Ampere classes 0xC797 / 0xC7C0 / 0xC7B5 per the E2 report). One segment is never longer than 0x7FFFFC bytes (the 21-bit word count of a GPFIFO entry). When an emitter would pass the soft limit, sdk+0x59f600 closes the segment into a gather record and, if the current memory is exhausted and +0x14 is set, calls the callback with `(cmd, NVN_COMMAND_BUFFER_MEMORY_EVENT_OUT_OF_COMMAND_MEMORY = 0, max(needed, 0x200), data)`; with no callback it aborts with “CommandBuffer has run out of command memory with no out-of-memory callback.”; if the callback added too little: “…didn't add enough command memory.”

**Control memory** is CPU-only. It holds an 8-byte-aligned token stream that the queue walks at submit time (sdk+0x5c5280) and that CallCommands/CopyCommands replay (sdk+0x59fd50). Out of control memory (needed 0x10–0x30 bytes, +0x15 set) → callback `(cmd, NVN_COMMAND_BUFFER_MEMORY_EVENT_OUT_OF_CONTROL_MEMORY = 1, max(needed, 0x100), data)`; abort strings as above with “control”.

**Memory callback** signature is unchanged from NVN1: `void (*)(NVNcommandBuffer*, NVNcommandBufferMemoryEvent event, size_t minSize, void *callbackData)` (callers: grow routine sdk+0x59f600, control-grow sdk+0x59f7fc, and the inline copies in AddCommandMemory sdk+0x59ef70, AddControlMemory sdk+0x59f160, BeginRecording, EndRecording). The callback is expected to call AddCommandMemory / AddControlMemory.

Control-stream layout (low byte of the first word = token type):

| token | size | content | producer |
|---|---|---|---|
| (header) | 16 | `{u8 1, 0xFF×7, u64 0}` written at the recording start; not part of the handle stream | BeginRecording (constant at sdk+0xac65a0), inlined at main+0x2b65c |
| 2 | 8 | end of stream | EndRecording |
| 3 | 8 + 0x18·n | gather group: `{u32 3, u32 n}` then n records `{u64 gpuAddress, u64 cpuAddress, u32 words, u8 flags, u8 pad[3]}` | segment close (sdk+0x59f600 and inline copies) |
| 4 | 16 | `{u8 4, …, u64 next}`: continue at another control chunk | AddControlMemory while recording |
| 5 | 16 | FenceSync `{u8 5, u8 condition, u8 mode∈{1,4}, u8 mode==2, u32 flags, NVNsync*}` | FenceSync sdk+0x5a0900 |
| 6 / 0x5B | 16 | WaitSync (0x5B in modes 1/4) `{type, …, NVNsync*}` | WaitSync sdk+0x5a0b90 |
| 7 | 16 | SignalEvent | sdk+0x5bbc30 |
| 8, 9–0x10 | 16/24 | compute-stage program/bind/dispatch (when not inline) | BindProgram, Bind* stage 5, DispatchCompute(Indirect) |
| 0x11–0x15 | 16+ | scratch memory, subroutines, program extra data, texture pool, sampler pool | SetShaderScratchMemory, SetProgramSubroutines, BindProgram, SetTexturePool, SetSamplerPool |
| 0x17–0x1F | 16 | deferred binds/pushes `{u8 type, u8 stage, u8 first, u8 count (or u32 size), u64 array}` | Bind*Deferred, Push*ConstantsDeferred |
| 0x20–0x27 | 8–24 | render-target bookkeeping, ClearDepthStencil, ZCull F32, compute push constants, ReportCounter/Value (compute/copy modes), overlap mode | |
| 0x40–0x4E | 16–0x60 | ray-tracing program/binds/build/copy/compact/relocate/dispatch | RT functions |
| 0x55–0x5A | 8–0x120 | dirty tile map, buffer copy/clear (compute mode), indirect-data order, DLSS | |
| 0x61–0x63 | 16 | device message buffer bind/bind deferred/reset | |

Gather record flags (byte +0x14): bit0 = segment lies in this buffer's command memory (may be merged with the next contiguous segment, and CopyCommands may copy its words); bit1 = the queue may flush at this entry; bit2 = GPFIFO sync bit (entry word1 bit31). The queue turns each record into one 8-byte GPFIFO entry: `{gpuAddress & ~3, (gpuAddress>>32 & 0xFF) | words<<10 | 0x200 | sync<<31}` (sdk+0x5ce250). Observed values: 3 ordinary closed segment, 1 segment closed before inline-fetched data, 2 data fetched from a user buffer (indirect mesh arguments, device commands), 7 segment before device-command end, 0 raw 0x7FFFFC chunks of device commands.

### Recording, NVNcommandHandle and submission

- `BeginRecording` (sdk+0x59f2d0): header at the control write pointer (+0x50 = its address), +0x48 = command write pointer, +0x11 = 1, +0x12 = 0, +0x58 = 0.
- `EndRecording` (sdk+0x59f390): closes the pending command segment, appends token 2, sets +0x13 bit0 if nothing was recorded, clears +0x11, returns:
  - modes 3/4 only (sdk+0x5b6a2c): if the recording is empty → handle **2**; if it is exactly one gather record no larger than device integer 0xA6 (0x10000 bytes in the static table sdk+0x59bf50) → **`gpuAddress | words << 40 | 2`**;
  - otherwise, if +0x12 is clear and the group count and the record pointer fit in 16/48 bits → **`(header + 0x18) | count << 48 | 1`** (pointer to the first gather record; header + 0x10 holds `{3, count}`, and the records are followed by the end token 2 — main checks that byte before inlining, main+0x2b738);
  - else **`header + 0x10`** (tag 0: pointer to the token stream).
- `nvnQueueSubmitCommands` (sdk+0x5c5bb0) dispatches on `handle & 3`: 1 → appends the records directly as GPFIFO entries; 2 → one synthetic record; 0 → walks the token stream (sdk+0x5c5280). The handle therefore stays valid only as long as the control memory (and the command memory it references) is untouched.
- The handle is **not** a GPU address of control memory; only tag 2 carries a GPU address (of command memory).

MKW's setup sequence (main+0x2b5b4..0x2b6d0, repeated at main+0x1ebb7c, 0x221d30, 0x222b98, 0x223504):
`Initialize(cmd, dev)` → `AddControlMemory(cmd, mem, size)` → `AddCommandMemory(cmd, pool, offset, max(size, 0x200))` → **byte store +0x10 = 1** → `BeginRecording` (inlined when `nvnPrivate0 != 0`, else the proc at main+0x2c680) → optionally `SetMemoryCallback` / `SetMemoryCallbackData` → `SetCommandMemoryCallbackEnabled(cmd, false)`. Elsewhere main+0x195100 re-arms a buffer with `AddCommandMemory(cmd, pool, 0, size)`, `AddControlMemory`, `BeginRecording`. `GetCommandMemoryUsed` (14 uses) feeds emptiness tests (`cmp x0, #0`) and high-water marks (main+0x194f5c, 0x226dafc).

### Recording modes (SetRecordingMode, +0x18)

No names in the binary; behaviour by value:

| mode | engine | effects observed |
|---|---|---|
| 0 | 3D/graphics (default) | GPU methods on subchannel 0; copies use the copy engine with prefix 0x91040002 / suffix 0x91000002 |
| 1 | compute | compute-subchannel variants (BeginDeviceCommands, Barrier → sdk+0x5ec720, debug markers); CopyBufferToBuffer → token 0x56, ClearBuffer → 0x57, ReportCounter/Value → 0x25, WaitSync → 0x5B; FenceSync/SignalEvent flag “compute” |
| 2 | copy | Barrier is a no-op, ReportCounter → 0x26, copies use the copy engine directly (prefix 0x91048040), FenceSync/SignalEvent flag “copy” |
| 3 | as 0 | plus EndRecording may return a tag-2 single-segment handle |
| 4 | as 1 | plus tag-2 handles |

Evidence: prefix/suffix tables sdk+0xb74b14 / sdk+0xb74b64, FenceSync sdk+0x5a0900, WaitSync sdk+0x5a0b90, ReportCounter sdk+0x5fa3f0, Barrier sdk+0x5f9fc0, EndRecording sdk+0x59f390. MKW sets 0, 1 and 2 on command buffers embedded at +0x188 of its contexts (main+0x10dd08..0x10de60, main+0x83352c, main+0x2613008).

### Function table

Verdicts compare the driver's register use with `dm/gfx/nvn/include/nvn.h`. “not in nvn.h” = the name existed in NVN1's names.txt but nvn.h has no prototype; the signature given is what the driver reads. Uses = static loads of the proc slot in main.

| function | sdk impl | main uses | NVN1 ABI | semantics |
|---|---|---|---|---|
| AddCommandMemory | 0x59ef70 | 6 | same | Closes the pending command segment into a gather record, then sets start/write/end from pool CPU address + offset (+0x38/+0x00/+0x20), soft limit +0x08 = min(end, start + 0x7FFFFC) − reserve, GPU base +0x60 = pool+0x18 + offset. |
| AddControlMemory | 0x59f160 | 6 | same | While recording, writes a 16-byte “continue at” token (type 4, pointer) at the old control write pointer, sets +0x12; then control start/write = memory (+0x40/+0x28), control end +0x30 = memory + size − 0x10. |
| Barrier | 0x5f9fc0 | 84 | same (more bits) | Bits as NVN1 plus 0x100 (extra cache operation sdk+0x5fde90), 0x200 (extra methods), 0x400 (waits like ORDER_INDIRECT_DATA); bit 3 also emits token 0x58; mode 2 (copy) → no-op; modes 1/4 → compute path sdk+0x5ec720; bit 31 clear → a trace method with the bits. MKW uses 0x101. |
| BeginDeviceCommands | 0x5b6b40 | 0 | new | `(cmd, const NVNdeviceCommandBuffer*)`: starts GPU-generated commands into the object's storage. |
| BeginRecording | 0x59f2d0 | 7 | same | Writes the 16-byte recording header at the control write pointer, remembers it (+0x50), segment start +0x48 = write pointer, +0x11 = 1, +0x12 = 0, +0x58 = 0. |
| BeginTransformFeedback | 0x5e5bc0 | 0 | same | GPU methods. |
| BindAccelerationStructure | 0x5dc700 | 0 | new | `(cmd, NVNshaderStage, int index, uint64_t asAddress)`: graphics → methods, compute → inline (address>>8) or token 0x48, ray stage → token 0x47. |
| BindAccelerationStructures | 0x5dcb50 | 0 | new | `(cmd, stage, first, count, const uint64_t*)`: loop. |
| BindAccelerationStructuresDeferred | 0x5dcbc0 | 0 | new | Token 0x1D, array read at submit. |
| BindBlendState | 0x5fc820 | 10 | same | GPU methods from the blend state. |
| BindChannelMaskState | 0x5da530 | 10 | same | GPU methods from the channel mask state. |
| BindColorState | 0x5da4b0 | 10 | same | GPU methods from the color state. |
| BindCoverageModulationTable | 0x5e76c0 | 0 | same | GPU methods from the float table. |
| BindDepthStencilState | 0x5da5b0 | 11 | same | GPU methods from the depth/stencil state. |
| BindDeviceMessageBuffer | 0x5b7c00 | 0 | new | `(cmd, NVNshaderStage, int index, const NVNdeviceMessageBuffer*)`: graphics stages (not 5) → methods; compute → inline or token 0x61; stage 8 → token 0x61. |
| BindDeviceMessageBufferDeferred | 0x5b7f60 | 0 | new | Token 0x62 (stage, index, object pointer). |
| BindImage | 0x5e0fa0 | 6 | same | As BindTexture (tokens 0xC / 0x44). |
| BindImages | 0x5e13f0 | 0 | same | Loop. |
| BindImagesDeferred | 0x5e1890 | 0 | not in nvn.h; deferred shape | Token 0x1C, array read at submit. |
| BindMultisampleState | 0x5da6b0 | 3 | same | GPU methods from the multisample state. |
| BindPolygonState | 0x5da630 | 11 | same | GPU methods from the polygon state. |
| BindProgram | 0x5daa80 | 22 | same | Copies the program's precomputed method block (count at program+0x14, pointer at +0x18) into command memory; compute stage → control token 8 (unless inline compute, +0xA8), ray-tracing program (program+0x80 bit8) → token 0x40, extra per-stage data (program+0x80 bits 9–13) → token 0x13; unbinds stages not in the program (sdk+0x5f8910). |
| BindSeparateSampler | 0x5df4f0 | 0 | same | As BindTexture (tokens 0xB / 0x43). |
| BindSeparateSamplers | 0x5df940 | 0 | same | Loop. |
| BindSeparateSamplersDeferred | 0x5dfde0 | 0 | not in nvn.h; deferred shape | Token 0x19, array read at submit. |
| BindSeparateTexture | 0x5de570 | 0 | same | As BindTexture (tokens 0xA / 0x42). |
| BindSeparateTextures | 0x5de9c0 | 0 | same | Loop. |
| BindSeparateTexturesDeferred | 0x5dee60 | 0 | not in nvn.h; deferred shape | Token 0x18, array read at submit. |
| BindShadingRateImage | 0x5fe860 | 7 | new | `(cmd, const NVNtexture*, const NVNtextureView*)`; NULL texture unbinds. MKW: 7 uses, all seen with NULL, NULL. |
| BindStorageBuffer | 0x5dd080 | 345 | same | As BindUniformBuffer (tokens 0xE / 0x46). |
| BindStorageBuffers | 0x5dd4d0 | 0 | same | Loop over NVNbufferRange. |
| BindStorageBuffersDeferred | 0x5dda10 | 0 | not in nvn.h; same shape as BindUniformBuffersDeferred | Token 0x1B, array read at submit. |
| BindTexture | 0x5de120 | 122 | same | Graphics: GPU methods writing the low 32 bits of the handle into the stage's handle table (slot·8 + 0x20); compute: inline if +0xA8 else token 9; ray stage: token 0x41. |
| BindTextures | 0x5e0470 | 1 | same | Loop of BindTexture semantics. |
| BindTexturesDeferred | 0x5e0910 | 1 | not in nvn.h; `(cmd, NVNshaderStage, int first, int count, const NVNtextureHandle*)` | Token 0x17, array read at submit. MKW: 1 use (main+0x22c6e50 area; its inline variant writes the token directly when nvnPrivate0 ≠ 0). |
| BindTransformFeedbackBuffer | 0x5e57e0 | 0 | same | GPU methods. |
| BindTransformFeedbackBuffers | 0x5e58c0 | 0 | same | Loop. |
| BindUniformBuffer | 0x5db4c0 | 725 | same | Graphics stages: GPU methods (size rounded to 256 B); compute (5): inline methods if +0xA8 else token 0xD; ray stage (8): token 0x45. |
| BindUniformBuffers | 0x5db930 | 0 | same | Loop over NVNbufferRange {u64 address, u64 size}; same per-stage paths. |
| BindUniformBuffersDeferred | 0x5dbf30 | 0 | not in nvn.h; `(cmd, NVNshaderStage, int first, int count, const NVNbufferRange*)` | Token 0x1A {u8 type, u8 stage, u8 first, u8 count, …, u64 array pointer}: the array is read when the queue processes the token, not now. |
| BindVertexAttribState | 0x5f8240 | 81 | same | (count, array) — GPU methods per attribute. |
| BindVertexBuffer | 0x5db190 | 63 | same | GPU methods (address, size). |
| BindVertexBuffers | 0x5db230 | 0 | same | Loop over NVNbufferRange. |
| BindVertexStreamState | 0x5f84c0 | 81 | same | (count, array) — GPU methods per stream. |
| BuildAccelerationStructures | 0x5cff80 | 0 | new | `(cmd, count, const BuildInfo[count] (0x40 each), postCount, const PostBuildInfo*)`: token 0x4B; the queue runs the build through the device ray-tracing interface (device+0x1df0, +0xD8). |
| CallCommands | 0x5a0840 | 23 | same | For each handle, appends references (gather records) to the handle's command segments and replays its control tokens into this buffer (sdk+0x59fd50 with copy = 0). |
| ClearBuffer | 0x5e7aa0 | 12 | same | Modes 1/4 → control token 0x57 {u32 value, address, size}; otherwise copy-engine fill methods with mode-dependent prefix/suffix. |
| ClearColor | 0x5e3d50 | 17 | same | GPU methods. |
| ClearColori | 0x5e3eb0 | 0 | same | GPU methods. |
| ClearColorui | 0x5e4010 | 0 | same | GPU methods. |
| ClearDepthStencil | 0x5e43c0 | 11 | same | Control token 0x21 {float depth, u8 depthMask, int stencil, int stencilMask}. |
| ClearTexture | 0x5fb4e0 | 1 | same | Texture clear with a float color. |
| ClearTexturei | 0x5fb940 | 0 | same | Integer variant. |
| ClearTextureui | 0x5fbd90 | 0 | same | Unsigned variant. |
| CompactAccelerationStructure | 0x5d0a40 | 0 | new | `(cmd, uint64_t, uint64_t)`: token 0x4D. |
| ComputeDirtyTileMap | 0x5af7e0 | 0 | new | `(cmd, const NVNtexture*, uint64_t, const int region[6], int, uint64_t, int rowStride, int imageStride)`: token 0x55; the queue runs an internal compute pass. |
| CopyAccelerationStructure | 0x5d0700 | 0 | new | `(cmd, uint64_t, uint64_t)`: token 0x4C (device RT interface +0xF0). |
| CopyBufferToBuffer | 0x5af4c0 | 0 | differs: 5th argument (copy flags) is ignored | x4 is never read (sdk+0x5af4c0). Modes 1/4 → token 0x56 {src, dst, size}; else copy-engine methods in chunks of ≤ 0x3FFFFF bytes, wrapped in mode-dependent prefix/suffix words. |
| CopyBufferToTexture | 0x5ae9f0 | 2 | same | Copy-engine methods (uses copy row/image stride +0x90/+0x94). |
| CopyCommands | 0x5a08a0 | 0 | same | Same walk with copy = 1: command words of CPU-visible segments (record flag bit0) are copied into this buffer's command memory; other records are referenced. |
| CopyTextureToBuffer | 0x5af140 | 1 | same | Copy-engine methods. |
| CopyTextureToTexture | 0x5afa70 | 9 | same | Copy-engine or 3D path (mode 2 forces the copy engine). |
| DiscardColor | 0x5f9ed0 | 0 | same | GPU methods. |
| DiscardDepthStencil | 0x5f9f50 | 0 | same | GPU methods. |
| DispatchCompute | 0x5ea000 | 44 | same | Always control token 0xF {x, y, z}; the queue builds the dispatch at submit. |
| DispatchComputeIndirect | 0x5ea1d0 | 0 | same | Control token 0x10 {address}. |
| DispatchRays | 0x5d12f0 | 0 | new | `(cmd, uint64_t raygen, const Region* miss, const Region* hit, const Region* callable, int w, int h, int d)`: token 0x49 (0x60 bytes). |
| DispatchRaysIndirect | 0x5d1e80 | 0 | new | Same tables, dimensions from a buffer address: token 0x4A. |
| Downsample | 0x5b0710 | 1 | same | Tail call of the shared downsample routine sdk+0x5b0520 with NULL views. |
| DownsampleTextureView | 0x5b0720 | 0 | same | Branch to sdk+0x5b0520 (Ghidra shows `void`: tail call; 5 arguments as NVN1). |
| DrawArrays | 0x5e1f20 | 7 | same | GPU methods. |
| DrawArraysIndirect | 0x5e22d0 | 0 | same | GPU methods; the indirect record is read by the GPU. |
| DrawArraysInstanced | 0x5e2150 | 1 | same | GPU methods. |
| DrawElements | 0x5e1fa0 | 69 | same | GPU methods. |
| DrawElementsBaseVertex | 0x5e2030 | 9 | same | GPU methods. |
| DrawElementsIndirect | 0x5e24e0 | 19 | same | GPU methods; the indirect record is read by the GPU. |
| DrawElementsInstanced | 0x5e21f0 | 15 | same | GPU methods. |
| DrawMeshTasks | 0x5e2e80 | 9 | new | Mesh-shader draw of `count` task workgroups starting at `first` (method 0xEA2 data {count, first}). MKW: 9 uses, first = 0. |
| DrawMeshTasks3D | 0x5e3320 | 0 | new | Mesh draw with a 3-D task grid (x, y, z) (method 0xEC8). |
| DrawMeshTasks3DIndirect | 0x5e33a0 | 0 | new | GPU fetches 3 words {x, y, z} from the buffer (12 bytes). |
| DrawMeshTasksIndirect | 0x5e2f00 | 0 | new | GPU fetches 2 words from `indirectBuffer` as the draw arguments (gather record with flag 2, 8 bytes). |
| DrawTexture | 0x5e7460 | 0 | same | GPU methods. |
| DrawTransformFeedback | 0x5e61c0 | 0 | same | GPU methods + token 0x58 (queue-side ordering). |
| EnableExclusiveScissor | 0x5fe390 | 0 | new | `(cmd, NVNboolean)`: writes the enable for all viewports (constant method block). |
| EndDeviceCommands | 0x5b6c50 | 0 | new | `(cmd, const NVNdeviceCommandBuffer*)`: closes them (gather record flag 7 = sync, then a GPU-fetched 2-word record). |
| EndRecording | 0x59f390 | 10 | same | Flushes the pending segment, appends the 8-byte end token (type 2), clears +0x11, returns the NVNcommandHandle (see Handle). |
| EndTransformFeedback | 0x5e5c30 | 0 | same | GPU methods. |
| EvaluateDlss | 0x5b93d0 | 0 | new | `(cmd, NVNdlssContext*, const NVNdlssEvaluateInfo*)`: token 0x59 (0x120 bytes) + texture descriptors written into the context; see Dlss family. |
| FenceSync | 0x5a0900 | 0 | same | Control token 5 {u8 5, u8 condition, u8 computeMode, u8 copyMode, u32 flags, NVNsync*} — the queue signals the sync. |
| Finalize | 0x59eee0 | 6 | same | Frees the lazily allocated block at +0x98 (device+0x1dd8 allocator, vtable +0x140) if any; nothing else. |
| GetCommandMemoryFree | 0x59f270 | 0 | same | +0x20 − +0x00. |
| GetCommandMemorySize | 0x59f250 | 0 | same | +0x20 − +0x38. |
| GetCommandMemoryUsed | 0x59f260 | 14 | same | +0x00 − +0x38 (bytes written since the last AddCommandMemory). |
| GetControlMemoryFree | 0x59f2b0 | 0 | same | +0x30 − +0x28 + 0x10. |
| GetControlMemorySize | 0x59f280 | 0 | same | +0x30 − +0x40 + 0x10. |
| GetControlMemoryUsed | 0x59f2a0 | 13 | same | +0x28 − +0x40. |
| GetCopyImageStride | 0x5b0760 | 0 | same (returns 32-bit) | ldr w0, [x0,#0x94]. |
| GetCopyRowStride | 0x5b0750 | 0 | same (returns 32-bit) | ldr w0, [x0,#0x90]: zero-extended, negative strides come back as large positive values. |
| GetMemoryCallback | 0x59ef40 | 0 | same | Returns +0x78. |
| GetMemoryCallbackData | 0x59ef50 | 0 | same | Returns +0x80. |
| Initialize | 0x59ee80 | 6 | same | Zeroes 0x00–0xAF, stores the device at +0x70, both out-of-memory callbacks enabled (+0x14/+0x15 = 1), +0x13 bit3 from device+0x1c28 bit0, +0xA8 from device+0x08 bit18; returns true. |
| InsertCheckpointDynamic | 0x59ee10 | 0 | not in nvn.h; no-op | `mov w0,#1; ret`: returns true, arguments ignored. |
| InsertCheckpointStatic | 0x59ee00 | 0 | not in nvn.h; no-op | `mov w0,#1; ret`: returns true, arguments ignored. |
| InsertDebugMarker | 0x5e8b70 | 0 | differs: `(cmd, uint32_t domainId, const char*)` | NVN1 nvn.h: (cmd, const char*). NVN2 calls strlen on x2 and stores w1 (sdk+0x5e8b70) — NVN1's InsertDebugMarkerStatic/Dynamic shape. |
| InsertDebugMarkerCompute | 0x5e8910 | 0 | new | InsertDebugMarker (compute). |
| InsertDebugMarkerGraphics | 0x5e86b0 | 0 | new | InsertDebugMarker (3D). |
| IsRecording | 0x5a12d0 | 0 | same | Returns byte +0x11. |
| MultiDrawArraysIndirectCount | 0x5e2720 | 0 | same | Macro call (sdk+0x5e2830): record size 16, count from the parameter buffer, maxDrawCount, stride. |
| MultiDrawElementsIndirectCount | 0x5e2c90 | 1 | same | Same macro, record size 20. MKW: stride 0x14 (main+0x34fa38). |
| MultiDrawMeshTasks3DIndirect | 0x5e35f0 | 0 | new | 3-D records of 12 bytes, `drawCount`, `stride` (≤ 0 → 12). |
| MultiDrawMeshTasks3DIndirectCount | 0x5e36f0 | 0 | new | 3-D records with count buffer and maxDrawCount. |
| MultiDrawMeshTasksIndirect | 0x5e3110 | 0 | new | `drawCount` records of 8 bytes from `indirectBuffer`, `stride` (≤ 0 → 8). |
| MultiDrawMeshTasksIndirectCount | 0x5e3210 | 0 | new | As above with the count read from `parameterBuffer`, capped at `maxDrawCount`. |
| OverrideSubtileSize | 0x5e9300 | 0 | not in nvn.h; `(cmd, NVNboolean enable, int size)` | One immediate method; size clamped to 1–128. |
| PauseTransformFeedback | 0x5e5d10 | 0 | same | GPU methods. |
| PopDebugGroup | 0x5e9090 | 0 | differs: takes `uint32_t domainId` | NVN1 nvn.h: (cmd). NVN2 reads w1 into the marker method (sdk+0x5e9090) — this is NVN1's PopDebugGroupId shape. |
| PopDebugGroupCompute | 0x5e9020 | 0 | new | PopDebugGroup (compute). |
| PopDebugGroupGraphics | 0x5e8fb0 | 0 | new | PopDebugGroup (3D). |
| PushComputeConstants | 0x5d3a30 | 0 | new | Same arguments for compute: inline if +0xA8 else control token 0x24 carrying a copy of the data. |
| PushComputeConstantsDeferred | 0x5d3df0 | 0 | new | Token 0x1F, data read at submit. |
| PushDebugGroup | 0x5e8270 | 0 | same | (cmd, uint32_t domainId, const char*): the string is copied into command memory; compute subchannel in modes 1/4, 3D otherwise. |
| PushDebugGroupCompute | 0x5e8010 | 0 | new | PushDebugGroup forced to the compute subchannel. |
| PushDebugGroupGraphics | 0x5e7db0 | 0 | new | PushDebugGroup forced to the 3D subchannel. |
| PushGraphicsConstants | 0x5d34b0 | 1 | new | `(cmd, int slot, int offset, int size, const void* data)`: size in bytes, clamped to 256, < 4 → no-op; data copied into command memory. MKW: (0, 0, 16, float[4]) at main+0x16d72e8. |
| PushGraphicsConstantsDeferred | 0x5d3670 | 0 | new | Token 0x1E {u8 slot, u8 offset, u32 size, data pointer}: data read at submit. |
| RelocateAccelerationStructure | 0x5d0d80 | 0 | new | `(cmd, uint64_t)`: token 0x4E (RT interface +0x100). |
| ReportCounter | 0x5fa3f0 | 26 | same (types 0x00–0x11) | TIMESTAMP_TOP (0x10) special; ZCULL_STATS (0xF) writes 4 words; type 0x11 is new; modes 1/4 → token 0x25, mode 2 → token 0x26. |
| ReportValue | 0x5fa810 | 9 | same | Semaphore-release methods; tokens 0x25/0x26 in compute/copy modes. |
| ResetCounter | 0x5fa910 | 1 | same | One method from a per-type table. |
| ResetDeviceMessageBuffer | 0x5b8340 | 0 | new | `(cmd, const NVNdeviceMessageBuffer*)`: branch to sdk+0x5b8350 (Ghidra shows `void`). |
| ResetDeviceMessageBufferDeferred | 0x5b84d0 | 0 | new | Token 0x63 {object pointer}. |
| ResolveDepthBuffer | 0x5e7a40 | 0 | same | GPU methods. |
| RestoreZCullData | 0x5e70a0 | 0 | same | GPU methods. |
| ResumeTransformFeedback | 0x5e5e20 | 0 | same | GPU methods. |
| SaveZCullData | 0x5e6ef0 | 0 | same | GPU methods. |
| SetAlphaRef | 0x5e3c70 | 0 | same | GPU method. |
| SetBlendColor | 0x5e3a40 | 8 | same | GPU methods. |
| SetColorReductionEnable | 0x5e9150 | 2 | not in nvn.h; `(cmd, NVNboolean)` | GPU method. MKW: 2 uses. |
| SetColorReductionThresholds | 0x5e91c0 | 2 | not in nvn.h; `(cmd, NVNformatClass, int conservative, int aggressive)` | Per-format-class method (classes 0–5; values clamped to 0x3F for class 3, else 0xFF; negative → 0). |
| SetCommandMemoryCallbackEnabled | 0x59ef60 | 5 | not in nvn.h; `(NVNcommandBuffer*, NVNboolean)` | Byte +0x14. When 0, running out of command memory is not reported (no callback, no abort). MKW passes false after setup (main+0x2b6c4, 0x1ebc78, 0x221e2c, 0x222c94, 0x223604). |
| SetComputeEnable | 0x5eceb0 | 0 | new | `(cmd, NVNboolean)`: compute-dispatch analogue of SetRenderEnable. |
| SetComputeEnableConditional | 0x5ecf20 | 0 | new | `(cmd, NVNconditionalRenderMode, NVNbufferAddress)`: conditional compute enable from a 64-bit value in memory. |
| SetComputeOverlapMode | 0x5e9780 | 3 | new | `(cmd, int mode)`: control token 0x27; the queue (sdk+0x5e96c0) applies it only if the queue supports it and the mode changed. |
| SetConservativeRasterDilate | 0x5e6d30 | 0 | same | GPU method (float). |
| SetConservativeRasterEnable | 0x5e6cc0 | 2 | same | GPU method. |
| SetConservativeRasterMode | 0x5e6dc0 | 0 | new | `(cmd, int mode, int param)`: data = (param & 0xF)<<16 \| (mode 0: 0, mode 2: 0x1110, else 0x1100). |
| SetControlMemoryCallbackEnabled | 0x59f150 | 0 | not in nvn.h; `(NVNcommandBuffer*, NVNboolean)` | Byte +0x15, same for control memory. |
| SetCopyEnable | 0x5b08f0 | 0 | new | `(cmd, NVNboolean)`: enables/disables copy-engine work (also mirrored on the 3D subchannel in modes 0/3). |
| SetCopyEnableConditional | 0x5b0990 | 0 | new | `(cmd, NVNconditionalRenderMode, NVNbufferAddress)`: mode 0 → condition 3, mode 1 → 4, other modes write nothing. |
| SetCopyImageStride | 0x5b0740 | 0 | same (stored 32-bit) | str w1 → +0x94. |
| SetCopyRowStride | 0x5b0730 | 0 | same (stored 32-bit) | str w1 → +0x90; the ptrdiff_t is truncated to 32 bits. |
| SetDebugLabel | 0x5b0b10 | 0 | same (no-op) | `ret` only (sdk+0x5b0b10). |
| SetDepthBounds | 0x5e51a0 | 2 | same | (cmd, enable, min, max): floats in s0/s1. |
| SetDepthClamp | 0x5e6540 | 8 | same | GPU method. |
| SetDepthRange | 0x5e4e50 | 20 | same | Wrapper. |
| SetDepthRanges | 0x5e4a40 | 2 | same | GPU methods. |
| SetExclusiveScissor | 0x5fe440 | 0 | new | `(cmd, int x, int y, int w, int h)` → SetExclusiveScissors(cmd, 0, 1, rect). |
| SetExclusiveScissors | 0x5fe480 | 0 | new | `(cmd, int first, int count, const int* rects)` rects of 4 ints {x, y, w, h}; 3 words per scissor. |
| SetGeometryProcessingMode | 0x5e2e10 | 9 | new | One immediate method (0xE26 data = mode) plus 0xE3E; selects vertex-pipeline vs mesh-pipeline geometry processing. MKW: modes 0, 1, 2 (9 uses). |
| SetInnerTessellationLevels | 0x5e56d0 | 0 | same | GPU methods (2 floats). |
| SetLineWidth | 0x5e3b50 | 4 | same | GPU method. |
| SetMemoryCallback | 0x59ef20 | 6 | same | Stores the callback at +0x78. |
| SetMemoryCallbackData | 0x59ef30 | 6 | same | Stores the callback data at +0x80. |
| SetOuterTessellationLevels | 0x5e5750 | 0 | same | GPU methods (4 floats). |
| SetPatchSize | 0x5e5660 | 1 | same | GPU method. |
| SetPointSize | 0x5e3ad0 | 5 | same | GPU method. |
| SetPolygonOffsetClamp | 0x5e3bd0 | 10 | same | GPU methods. |
| SetPrimitiveRestart | 0x5e3800 | 7 | same | GPU methods. |
| SetProgramSubroutines | 0x5a1040 | 0 | same | Control token 0x12 carrying a copy of the subroutine index array. |
| SetProvokingVertexMode | 0x5e9650 | 0 | not in nvn.h; `(cmd, NVNprovokingVertexMode)` | One immediate method (0x5A1 data = mode). |
| SetRasterizerDiscard | 0x5e64d0 | 2 | same | GPU method. |
| SetRecordingMode | 0x59f2c0 | 17 | new | Stores a 32-bit mode at +0x18 (0–4); selects the engine the recording targets (see Recording modes). MKW uses 0, 1 and 2. |
| SetRenderEnable | 0x5fa990 | 24 | same | GPU method. |
| SetRenderEnableConditional | 0x5faa00 | 21 | same | GPU methods. |
| SetRenderTargets | 0x5f92f0 | 12 | same | GPU methods + control token 0x20 (attachment bookkeeping for the queue). |
| SetRepresentativeFragmentTest | 0x5f88a0 | 0 | new | `(cmd, NVNboolean)`: one immediate method (0x0AF). |
| SetSampleMask | 0x5e3ce0 | 2 | same | GPU method. |
| SetSamplerPool | 0x5e6b10 | 1 | same | Control token 0x15 (pool pointer); applied by the queue at submit. |
| SetScissor | 0x5e5620 | 22 | same | Wrapper over SetScissors. |
| SetScissors | 0x5e5240 | 2 | same | GPU methods per scissor. |
| SetShaderScratchMemory | 0x5a0de0 | 2 | same | Control token 0x11 (pool, offset, size); applied at submit. |
| SetShadingRatePaletteMode | 0x5fe9a0 | 2 | new | `(cmd, int mode)`: one immediate method, data = (mode != 1). MKW: mode 1 (2 uses). |
| SetStencilCullCriteria | 0x5e9270 | 1 | not in nvn.h; `(cmd, NVNstencilFunc func, int ref, int mask)` | One method: (func − 1) \| ref<<16 \| mask<<24. MKW: (3, 0xFF, 0xFF) at main+0x16d73f8. |
| SetStencilMask | 0x5e3920 | 10 | same | GPU method. |
| SetStencilRef | 0x5e39b0 | 10 | same | GPU method. |
| SetStencilValueMask | 0x5e3890 | 10 | same | GPU method. |
| SetSubpixelPrecisionBias | 0x5e6e40 | 0 | same | GPU methods. |
| SetTexturePool | 0x5e6890 | 2 | same | Control token 0x14 (pool pointer); applied by the queue at submit. |
| SetTiledCacheAction | 0x5e65c0 | 4 | same | GPU methods. |
| SetTiledCacheTileSize | 0x5e6690 | 2 | same | GPU methods. |
| SetViewport | 0x5e49f0 | 20 | same | Wrapper over SetViewports. |
| SetViewportSwizzles | 0x5e4e90 | 0 | same | GPU methods. |
| SetViewports | 0x5e45a0 | 2 | same | GPU methods per viewport (x, y, w, h floats). |
| SetZCullZF32CompressionEnable | 0x5e9490 | 0 | not in nvn.h; `(cmd, NVNboolean)` | Control token 0x23. |
| ShadingRateImagePalette | 0x5fe0a0 | 15 | new | `(cmd, int index, int count, const int* rates)`: packs up to 16 4-bit rates (missing entries = 5); NULL rates → palette disabled. MKW: (0, 16, ptr) and (0, 0, NULL). |
| SignalEvent | 0x5bbc30 | 11 | same | Control token 7 {mode, location, flags, value, event*}. |
| SubmitDeviceCommands | 0x5b6fc0 | 0 | new | `(cmd, const NVNdeviceCommandBuffer*)`: inserts gather records that execute the generated commands (storage + 0x100, chunks of 0x7FFFFC bytes). |
| TiledDownsample | 0x5fb4c0 | 0 | same | Tail call of sdk+0x5faee0 with NULL views. |
| TiledDownsampleTextureView | 0x5fb4d0 | 0 | same | Branch to sdk+0x5faee0 (5 arguments). |
| UpdateUniformBuffer | 0x5e7280 | 26 | same | Inline constant-buffer update with the data copied into command memory. |
| WaitEvent | 0x5bb6d0 | 10 | same | GPU methods (trace method + semaphore acquire on the event's memory, mode-dependent prefix); reads the event's first qword. |
| WaitSync | 0x5a0b90 | 0 | same | Control token 6 (0x5B in modes 1/4) {NVNsync*}. |

### Deep dives

**Shader stages.** Stage-indexed functions accept 0–8. Stage 5 = compute (inline methods if +0xA8, else control tokens 9–0xE). Stage 8 is a non-graphics stage always recorded as tokens 0x41–0x48 and bound together with ray-tracing programs (token 0x40) → ray-tracing stages [INFERENCE]. Stages 6 and 7 are graphics stages with their own hardware slots (UBO slot table sdk+0xb855d8: 0→8, 1→4, 2→3, 3→1, 4→0xA, 6→9, 7→0xB) → task and mesh [INFERENCE; order not established]. BindProgram uses 9 stage bits (program+0x80 mask, `~mask | 0xFFFFFE00`), bit 8 = ray tracing.

**Mesh/task draws.** `DrawMeshTasks(cmd, int first, int count)` → method 0xEA2 `{count, first}` (sdk+0x5e2e80; MKW calls with first = 0, count = n/32, main+0x2792b0). `DrawMeshTasks3D(cmd, int x, int y, int z)` → method 0xEC8 `{x, y, z}`. Indirect forms insert a gather record (flag 2) pointing at the user buffer so the GPU feeds the record words as method data: 2 words for DrawMeshTasksIndirect (method 0xE62), 3 for DrawMeshTasks3DIndirect (0xED4). Record layouts `{u32 count, u32 first}` and `{u32 x, y, z}` [INFERENCE from the direct methods]. Multi-draw forms call the macro helper sdk+0x5e2830 with (method, default record size 8 or 12, indirect address, count address or 0, drawCount/maxDrawCount, stride, isCount): `MultiDrawMeshTasksIndirect(cmd, NVNbufferAddress indirect, int drawCount, int stride)`, `…IndirectCount(cmd, indirect, NVNbufferAddress count, int maxDrawCount, int stride)`; stride ≤ 0 → record size. Mesh pipeline selection: `SetGeometryProcessingMode(cmd, mode)` (MKW 0/1/2) [meaning of 1 vs 2 not established].

**Push constants.** `PushGraphicsConstants(cmd, int slot, int offset, int size, const void *data)`: size in bytes, clamped to 256, below 4 → nothing; one method header `0xA0010141 + (words << 16)` then `slot & 7 | (offset & 0xFF) << 8` then the data (sdk+0x5d34b0). `PushComputeConstants` (sdk+0x5d3a30): same arguments; data lands at constant word `slot·64 + offset/4 + 0x48` of the compute constant area (inline if +0xA8, else token 0x24 with a copy of the data). Deferred variants record only the pointer (tokens 0x1E/0x1F, read at submit). Whether `slot` is a stage or a block index is not established (graphics masks it to 3 bits).

**Exclusive scissor.** `SetExclusiveScissors(cmd, int first, int count, const int *rects)` reads `{x, y, w, h}` per scissor and writes 3 words each (sdk+0x5fe480); `SetExclusiveScissor(cmd, x, y, w, h)` = `SetExclusiveScissors(cmd, 0, 1, …)` (sdk+0x5fe440); `EnableExclusiveScissor(cmd, NVNboolean)` writes a constant block covering all viewports (sdk+0x5fe390).

**Shading-rate image.** `BindShadingRateImage(cmd, const NVNtexture*, const NVNtextureView*)` builds a descriptor through sdk+0x5c9dec and writes method 0x4AA (8 words) + 0x09F; NULL texture writes zeros (unbind) (sdk+0x5fe860). `ShadingRateImagePalette(cmd, int index, int count, const int *rates)`: up to 16 rates packed 4 bits each into 2 words, unspecified entries = 5, control word `index & 0xF << 4 | enable` with enable = (rates != NULL) (sdk+0x5fe0a0); MKW: `(0, 16, table)` and `(0, 0, NULL)`. `SetShadingRatePaletteMode(cmd, int mode)`: data `mode != 1` (sdk+0x5fe9a0); MKW passes 1.

**Representative fragment test.** `SetRepresentativeFragmentTest(cmd, NVNboolean)` → immediate method 0x0AF (sdk+0x5f88a0).

**Conservative raster mode.** `SetConservativeRasterMode(cmd, int mode, int param)` → method 0xE60 data `(param & 0xF) << 16 | (mode == 0 ? 0 : mode == 2 ? 0x1110 : 0x1100)` (sdk+0x5e6dc0). Enum names not established.

**Conditional compute/copy.** `SetComputeEnable(cmd, NVNboolean)` → compute-subchannel immediate 0xE12 (sdk+0x5eceb0). `SetComputeEnableConditional(cmd, NVNconditionalRenderMode, NVNbufferAddress)` → 0xE14 `{mode == 0, addrHi, addrLo}` (same encoding as SetRenderEnableConditional). `SetCopyEnable(cmd, NVNboolean)` → copy-engine method (+ 3D mirror in modes 0/3) (sdk+0x5b08f0). `SetCopyEnableConditional(cmd, mode, address)` → mode 0 → condition 3, 1 → 4, anything else writes nothing (sdk+0x5b0990).

**Compute overlap.** `SetComputeOverlapMode(cmd, int mode)` → token 0x27; the queue (sdk+0x5e96c0) emits a compute method only if the queue has the capability bit (queue+0x168 bit3) and the mode changed, data = (mode == 0). MKW: 3 uses (main+0x1c9e58, 0x1c9f00, 0x1eb3a4).

**Debug groups/markers.** All write marker methods into command memory (strings copied, length word, domain id). Base functions pick the compute subchannel in modes 1/4, the `…Graphics` / `…Compute` variants force it. Signatures: `PushDebugGroup*(cmd, uint32_t domainId, const char*)`, `PopDebugGroup*(cmd, uint32_t domainId)`, `InsertDebugMarker*(cmd, uint32_t domainId, const char*)`. NVN1's Static/Dynamic/Id names are gone from NVN2's table.

**Device-generated commands.** `NVNdeviceCommandBuffer` (DeviceCommandBuffer family): +0x00 device, +0x08 memory pool, +0x10 offset, +0x18 storage size, +0x20 command memory size, +0x28 compute memory size; storage GPU address = pool+0x18 + offset (nvnDeviceCommandBufferGetHandle sdk+0x5b6b20). `BeginDeviceCommands(cmd, dcb)` writes one 8-word method (0xE9A on subchannel 0, or 1 in modes 1/4) with the storage address, 0x100, the rounded command/compute sizes and the 0x7FFFFC chunk count (sdk+0x5b6b40). `EndDeviceCommands(cmd, dcb)` closes the current segment with sync flag 7, writes method 0xE9C `{address, commandSize + 0x100}` and a 2-word GPU-fetched record from the storage (sdk+0x5b6c50). `SubmitDeviceCommands(cmd, dcb)` appends gather records over storage + 0x100 for the command size, in 0x7FFFFC-byte chunks (sdk+0x5b6fc0). Semantics: GPU work between Begin and End writes commands into the storage; Submit executes them [INFERENCE].

**Device message buffers.** `BindDeviceMessageBuffer(cmd, NVNshaderStage, int index, const NVNdeviceMessageBuffer*)` reads object +0x08 (pool) and +0x10 (i32 offset); graphics stages 0–4/6/7 → methods, 5 → inline or token 0x61, 8 → token 0x61 (sdk+0x5b7c00). `ResetDeviceMessageBuffer(cmd, dmb)` (branch to sdk+0x5b8350) also reads +0x14. Deferred variants: tokens 0x62/0x63.

**Ray tracing** (all recorded as tokens, executed by the queue through the device's ray-tracing interface at device+0x1df0):
- `DispatchRays(cmd, uint64_t raygen, const Region *miss, const Region *hit, const Region *callable, int w, int h, int d)`: token 0x49, 0x60 bytes (`+8 raygen, +0x10/+0x28/+0x40 the three 0x18-byte regions, +4 w, +0x58 h, +0x5C d`), sdk+0x5d12f0; executed by sdk+0x5d1510. Region = `{u64 @0, u64 @8, u32 @0x10}`, passed to the RT interface as (address, u32, u64) — likely `{address, size, stride}` [INFERENCE]. Calling the first argument “raygen record address” is an inference from Vulkan's 4-table layout.
- `DispatchRaysIndirect(cmd, raygen, miss, hit, callable, NVNbufferAddress dims)`: token 0x4A.
- `BuildAccelerationStructures(cmd, uint64_t count, const BuildInfo *infos, uint64_t postCount, const PostBuild *posts)`: token 0x4B; executor sdk+0x5d0150 iterates `count` infos of 0x40 bytes: +0x00 u32 flags (bit28 split off), +0x04 u32 type (1 = …), +0x08 geometry array pointer (0x40 bytes per geometry, converted by sdk+0x5cfdb0), +0x10 i32 geometry count, +0x18..+0x3F five u64 (addresses passed to the builder). `posts` entries are 0x10 bytes `{u32 kind (<5), u64 address}`; per info the address is advanced by `size[kind] · i` with sizes {8, 0, 0, 8, 0x18} (sdk+0xb7ab90).
- `CopyAccelerationStructure(cmd, a, b)` / `CompactAccelerationStructure(cmd, a, b)` / `RelocateAccelerationStructure(cmd, a)`: tokens 0x4C/0x4D/0x4E. Which of a/b is the destination is not established.
- `BindAccelerationStructure(cmd, stage, int index, uint64_t address)`: graphics writes the 64-bit address into the stage's table (slot·8 + 0x668); compute writes address >> 8.

**ComputeDirtyTileMap(cmd, const NVNtexture *tex, uint64_t a, const int region[6], int b, uint64_t c, int rowStride, int imageStride)**: token 0x55 (0x48 bytes; sdk+0x5af7e0). Reads region[0..5] = {x, y, z, width, height, depth}; rowStride 0 → ceil(width/128), imageStride 0 → ceil(height/16)·rowStride (only if depth > 1); copies texture+0x54 format bits. The queue runs an internal compute pass over ceil(width/256)×ceil(height/256) groups (sdk+0x5ec5d0). Roles of a/b/c not established.

**EvaluateDlss(cmd, context, const info*)** (sdk+0x5b93d0): token 0x59 (0x120 bytes). The info struct (≥ 0xB0 bytes) holds six `{const NVNtexture*, const NVNtextureView*}` pairs at +0x00, +0x10, +0x20, +0x30, +0x40, +0x68, 32-bit fields at +0x50..+0x64 and +0x7C..+0xAC, a byte at +0x78; textures are converted to descriptors written into the context's descriptor memory (context+0x10 pool, slot indices at context+0x18..+0x2C). Details belong to the Dlss family.

### nvnCommandBufferCopyTextureToTextureWithCopyEngineNVX (NVX)

Not in the 698-name table any more (it was in NVN1's names.txt); implemented at sdk+0x5b0770 (NVX lookup), 0 uses in main. Signature read by the driver: `(cmd, const NVNtexture *src, const NVNcopyRegion *srcRegion, const NVNtexture *dst, const NVNcopyRegion *dstRegion)`. It builds source/destination surface descriptions from texture fields +0x28, +0x48, +0x54, +0x60, +0x68..+0x72 and calls the 2D copy-engine routine sdk+0x5ae530 with srcRegion {x, y, —, width, height} and dstRegion {x, y}.

### Notes for the host (nxnative)

- Keep `nvnPrivate0` at 0 (never let the real GetProcAddress set it); then MKW only uses the proc pointers and never touches command/control memory internals itself. Only the byte at +0x10 is written regardless.
- `cmd_t` still fits; the handle format is the host's own as long as `nvnPrivate0 == 0` (main only decodes handles in gated code, e.g. main+0x2b720).
- Fix the NVN1 prototypes for `PopDebugGroup` and `InsertDebugMarker` if they are ever implemented beyond no-ops (currently no-ops in hle_nvn.c, so safe).
- Deferred binds/pushes must read their array **at submit time**, not at record time.
- `SetCommandMemoryCallbackEnabled(false)` (MKW does this) means: when command memory runs out, the driver neither calls back nor aborts.

### Open questions

- Meaning of geometry processing modes 1 vs 2, conservative raster mode values, shading-rate palette mode, `ShadingRateImagePalette` first argument (viewport index?), compute overlap mode values.
- Whether NVNshaderStage 6/7 are task/mesh in that order; meaning of stage 8 (ray tracing assumed).
- `PushGraphicsConstants` second argument: stage or block index.
- RT argument roles (raygen/miss/hit/callable naming, Region field order, copy/compact destination order, BuildInfo field names).
- `ComputeDirtyTileMap` roles of its three address/int arguments; `BindDeviceMessageBuffer` third argument (index vs size).
- Who allocates +0x98 (freed by Finalize); fields +0x88 / +0xA0.
- Whether any **ungated** main code reads driver fields other than writing +0x10 (only the AddCommandMemory sites were checked; the 797 gated sites were not each verified to have a proc fallback, only main+0x2b624 and main+0x2b71c).
- Indirect mesh record word order is inferred, not observed.

## Queue, window, sync and event sizes

| Object | NVN1 reserved (nvn.h) | NVN2: bytes the driver touches | NVN2: what MKW allocates (likely reserved size) | Host struct (nvn_obj.h) still fits? |
|---|---|---|---|---|
| NVNqueue | 0x2000 | 0x2cc0 (memset in the internal init sdk+0x5c2fc0) | 0x3000 (main+0x8f7210, main+0xcf4160: allocator(0x3000, align 8)) | yes (`queue_t` is tiny) |
| NVNqueueBuilder | 0x40 | 0x48 (SetDefaults writes +0x10..+0x47; GetQueuePriority reads +0x44) | at most 0x50 on the stack (main+0xcf403c: builder at sp+0x50, frame record at sp+0xa0) | `queue_builder_t` is 0x40 ≤ 0x48: yes |
| NVNwindow | 384 (0x180) | 0x218 (memset in nvnWindowInitialize sdk+0x5cbeb0) | 0x280 (main+0xe2ba60: allocator(0x280, align 8)) | yes |
| NVNwindowBuilder | 0x40 | 0x60 (SetDefaults writes +0x10..+0x5f) | 0x80 (two stack builders, each followed by texture builders 0x80 bytes later: main+0x2626a0 sp+8 → textures at sp+0x88; main+0xe2ba78 sp+0x18 → sp+0x98) | `win_builder_t` fits in 0x60 |
| NVNsync | 0x40 | 0x38 (16 embedded syncs in the queue at stride 0x38, +0x2128..+0x24a8; nvnQueueFinish uses a 0x38-byte stack sync) | 0x40 (main+0xe2ba38 and main+0xcf4530: allocator(0x40); main+0x8f6fbc: 5 syncs at stride 0x40) | yes |
| NVNevent | 0x40 | 0x28 (nvnEventInitialize writes +0x00..+0x27) | 0x40 (main+0x8f7314: events at stride 0x40 starting at obj+0x1b0) | yes |
| NVNeventBuilder | 0x20 | 0x10 | not established (stack) | yes |

The host's "store state in place" scheme of nvn_obj.h stays valid for all of these objects: NVN2's reserved sizes are greater than or equal to NVN1's.

## QueueBuilder

### NVNqueueBuilder (driver layout, 0x48 bytes)

| Offset | Type | Meaning | Evidence |
|---|---|---|---|
| +0x00 | 8 bytes | not written by SetDefaults or any setter | sdk+0x5c2b70 |
| +0x08 | NVNdevice* | device | SetDevice sdk+0x5c2b60, GetDevice sdk+0x5c2ed0 |
| +0x10 | void* | queue memory (SetQueueMemory) | sdk+0x5c2c00, GetMemory sdk+0x5c2f50 |
| +0x18 | NVNqueue* | primary queue (new: SetPrimaryQueue) | sdk+0x5c2fb0; read by the internal init sdk+0x5c2fc0 and the channel creation at sdk+0x5ce818 |
| +0x20 | u32 | flags | SetFlags sdk+0x5c2ba0 |
| +0x24 | u8 | command memory size was set | SetCommandMemorySize sdk+0x5c2bb0 |
| +0x25 | u8 | compute memory size was set | sdk+0x5c2bc0 |
| +0x26 | u8 | control memory size was set | sdk+0x5c2bd0 |
| +0x27 | u8 | ray-tracing memory size was set (new) | sdk+0x5c2be0 |
| +0x28 | u8 | command flush threshold was set | sdk+0x5c2bf0 |
| +0x2c | s32 | command memory size | sdk+0x5c2bb0 |
| +0x30 | s32 | compute memory size | sdk+0x5c2bc0 |
| +0x34 | s32 | control memory size | sdk+0x5c2bd0 |
| +0x38 | s32 | ray-tracing memory size (new) | sdk+0x5c2be0 |
| +0x3c | s32 | queue memory size | SetQueueMemory sdk+0x5c2c00, GetMemorySize sdk+0x5c2f40 |
| +0x40 | s32 | command flush threshold | sdk+0x5c2bf0 |
| +0x44 | s32 | queue priority, default 100 | SetDefaults sdk+0x5c2b70 writes 100; SetQueuePriority sdk+0x5c2f60 |

SetDefaults zeroes +0x10..+0x43 and writes 100 at +0x44. It does not touch the device at +0x08.

Defaults applied when a size is not set (sdk+0x5c2e30, sdk+0x5c2fc0, sdk+0x5c3b64): command memory 0x10000, control memory 0x4000, compute memory 0x40000. When the device's extension interface at device+0x1df0 is non-null, the ray-tracing memory defaults to device integer 0x98; otherwise it is 0. The flush threshold defaults to command size / 8. When it is set, it is clamped to command size − 4 (sdk+0x5c2fc0, stored at queue+0x2540).

Size validation (sdk+0x5c2c10, called from the init): the command size must be a multiple of device integer 0x50 (QUEUE_COMMAND_MEMORY_GRANULARITY) and at least device integer 0x51 (…_MIN_SIZE). The other sizes are checked the same way (not fully transcribed).

### Queue builder flags (bits of +0x20)

| Bit | Evidence | Meaning |
|---|---|---|
| 0x1 | GetQueueMemorySize/sdk+0x5c3b64: a buffer of (device+0xb4 × device+0xbc × 4 + 0x30ff) & ~0xff is reserved only when (flags & 0x15) == 0 | NVN1 NO_FRAGMENT_INTERLOCK: no fragment-interlock buffer [INFERENCE: name kept from NVN1] |
| 0x2 | sdk+0x5c3b64: the device zcull region (device+0x10c/+0x110) is skipped when this bit is set | NVN1 NO_ZCULL |
| 0x4 | init sdk+0x5c2fc0 and sdk+0x5ce818: requires the primary queue (+0x18). The channel is created as a TSG subcontext of the primary's TSG (`NvRmGpuTSGSubcontextCreate(primary+0x68, …)`) | new: secondary/subcontext queue [name INFERENCE] |
| 0x8 | init fails when flag 0x4 is set and the primary queue lacks bit 0x8 (sdk+0x5c2fc0 checks primary+0x168 bit 3). It adds 0x39100 bytes of queue memory (sdk+0x5c3b64) | new: primary queue that allows subcontext queues [name INFERENCE] |
| 0x10 | no TSG; the channel uses a different engine class (device info +0x27c instead of +0x264); no queue context memory; queue kind 2 (queue+0x2c00); priority/timeslice not applied; the fence path uses a different method encoder (sdk+0x5fdde0) | new: a non-graphics (copy/async) queue [INFERENCE] |

MKW (main+0xcf405c) creates its present/graphics queue with flags **9** (0x1 | 0x8). Its secondary queue loop (main+0x8f70c4..0x8f7320) handles 5 slots at stride 0x50, each gated by a bit at slot+0x50: slot 0 uses flags 1; slots 1–2 use SetPrimaryQueue(obj+8) and flags 4; slots 3–4 use flags 0x10.

### Function table

| Function | sdk impl | main uses | NVN1 ABI | Semantics |
|---|---|---|---|---|
| nvnQueueBuilderSetDevice | 0x5c2b60 | 2 | same | +0x08 = device |
| nvnQueueBuilderSetDefaults | 0x5c2b70 | 2 | same (layout changed) | zero +0x10..+0x43, priority 100 |
| nvnQueueBuilderSetFlags | 0x5c2ba0 | 2 | same | +0x20 = flags (new bits 0x4/0x8/0x10, see above) |
| nvnQueueBuilderSetCommandMemorySize | 0x5c2bb0 | 2 | nvn.h says size_t; the driver stores w1 (32-bit) | +0x2c = size, +0x24 = 1 |
| nvnQueueBuilderSetComputeMemorySize | 0x5c2bc0 | 2 | same note (32-bit stored) | +0x30, +0x25 |
| nvnQueueBuilderSetControlMemorySize | 0x5c2bd0 | 2 | same note | +0x34, +0x26 |
| nvnQueueBuilderSetRayTracingMemorySize | 0x5c2be0 | 0 | new | +0x38, +0x27 |
| nvnQueueBuilderSetCommandFlushThreshold | 0x5c2bf0 | 0 | same (32-bit stored) | +0x40, +0x28 |
| nvnQueueBuilderSetQueueMemory | 0x5c2c00 | 2 | same (size stored as 32-bit) | +0x10 = memory, +0x3c = size |
| nvnQueueBuilderGetQueueMemorySize | 0x5c2e30 | 2 | same | control (default 0x4000) + command (default 0x10000) + (flag 0x10 ? 0 : 4 KiB-aligned context size from sdk+0x5c3b64) |
| nvnQueueBuilderGetDevice | 0x5c2ed0 | 0 | same | returns +0x08 |
| nvnQueueBuilderGetFlags | 0x5c2ee0 | 0 | same | returns +0x20 |
| nvnQueueBuilderGetCommandMemorySize | 0x5c2ef0 | 0 | not in nvn.h; driver: `NVNboolean (const builder*, int *size)` | *size = +0x2c; returns the "set" byte +0x24 |
| nvnQueueBuilderGetComputeMemorySize | 0x5c2f00 | 0 | same shape | +0x30 / +0x25 |
| nvnQueueBuilderGetControlMemorySize | 0x5c2f10 | 0 | same shape | +0x34 / +0x26 |
| nvnQueueBuilderGetRayTracingMemorySize | 0x5c2f20 | 0 | new, same shape | +0x38 / +0x27 |
| nvnQueueBuilderGetCommandFlushThreshold | 0x5c2f30 | 0 | same shape | +0x40 / +0x28 |
| nvnQueueBuilderGetMemorySize | 0x5c2f40 | 0 | not in nvn.h | returns +0x3c (int) |
| nvnQueueBuilderGetMemory | 0x5c2f50 | 0 | not in nvn.h | returns +0x10 |
| nvnQueueBuilderSetQueuePriority | 0x5c2f60 | 0 | not in nvn.h | +0x44 = priority (int) |
| nvnQueueBuilderGetQueuePriority | 0x5c2fa0 | 0 | not in nvn.h | returns +0x44 |
| nvnQueueBuilderSetPrimaryQueue | 0x5c2fb0 | 1 (main+0x8f7104) | new | +0x18 = primary NVNqueue* |

Priority → TSG timeslice (sdk+0x5cef88, applied at init when flags & 0x14 == 0): priority 1000 → `NvRmGpuTaskSchedulingGroupSetTimeslice(tsg, 0x3fc00)`, priority 10 → 0x80, anything else (the default 100) → 0x400. [INFERENCE: these are the NVN LOW=10 / NORMAL=100 / HIGH=1000 levels; the timeslice unit is µs.]

## Queue

### NVNqueue (driver layout, 0x2cc0 bytes; reserve 0x3000)

nvnQueueInitialize (sdk+0x5c41a0) calls the internal init sdk+0x5c2fc0(queue, builder, 0, 0, 0). The init takes device+0x920 (mutex) and the global queue mutex, validates the builder, and memsets the queue to 0x2cc0 bytes.

| Offset | Type | Meaning | Evidence |
|---|---|---|---|
| +0x000 | u64 | control (GPFIFO) bytes submitted, for GetTotalControlMemoryUsed | sdk+0x5c5bb0 adds n×8; sdk+0x5c5d70 |
| +0x008 | u64 | compute memory used | GetTotalComputeMemoryUsed sdk+0x5c5db0 |
| +0x010 | NVNdevice* | device | init |
| +0x018 | u8 | GPU fault seen (set by GetError when the error notifier is non-zero); Flush backs off 100 µs per ms | sdk+0x5c4590, sdk+0x5c5d20 |
| +0x020 | u64 | time of the last back-off | sdk+0x5c5d20 |
| +0x028 | object (vtable from sdk GOT 0xd7b200, +0x10) | GPU channel wrapper. Relative to +0x28: +0x08 NvRmGpuChannel handle (queue+0x30), +0x10 channel info, +0x40 TSG handle (queue+0x68), +0x70 syncpoint id (queue+0x98), +0x90 subcontext (queue+0xb8), +0xc0 error-notifier storage pointer (queue+0xe8), +0xd0 owning queue, +0xd8 channel slot, +0xe8 NvRm device (device+0x9d0), +0xf0 NvRmMem handle of the 0x1000-byte notifier, +0xf8 its CPU mapping (queue+0x120), +0x104/+0x108 syncpoint base and increment count | sdk+0x5ce818, sdk+0x5cee88, sdk+0x5cedcc |
| +0x140 | s32 | channel slot index 0..255 (taken from a global 256-bit bitmap). Indexes device+0x120+slot×8 (queue registry) and device+0xbe8+slot×0x10 (64-bit CPU fence counter) | sdk+0x5ce818, sdk+0x5c8000, sdk+0x5c4520 |
| +0x164 | s32 | cached device generation (device+0xa00) | sdk+0x5c47e8 |
| +0x168 | u32 | builder flags | init |
| +0x1ed8 | u8 | abort on GPU fault (env `NVN_DEBUG_ABORT_ON_GPU_FAULT` == 1) | init |
| +0x1ef0 | NVNqueue* | primary queue (flag 4) | init |
| +0x1f00 | Sync (0x38) | internal sync | init, Finalize |
| +0x1f40 | MemoryPool | command-memory pool (owned when +0x2040 is set) | init sdk+0x5c3874(…, 0x12) |
| +0x2058..+0x20e8 | — | command-memory ring allocator (vtable 0xe6c608) | init |
| +0x20f0 | ring | throttle ring of 16 syncs | init |
| +0x2128 | Sync[16] | at stride 0x38 | init, sdk+0x5c3680 |
| +0x2530 | u64 | command memory size | init |
| +0x2538 | u64 | auto-flush granularity = command size / 16 | init |
| +0x2540 | u64 | command flush threshold | init |
| +0x25b0 | 0x200 bytes | internal control memory | init |
| +0x2a08 | MemoryPool | queue context memory (flags 0x21, size +0x2b1c) | init |
| +0x2b78 | — | subcontext / secondary-queue helper | sdk+0x5ecfac, sdk+0x5ed3ec |
| +0x2be8 | NVNcommandBuffer | internal command buffer (Begin/End markers, fences, waits) | init: nvnCommandBufferInitialize(queue+0x2be8) |
| +0x2c00 | s32 | queue kind: 0 normal, 1 subcontext (flag 4), 2 flag-0x10 queue | init |
| +0x2c98/+0x2ca0/+0x2ca8 | u64 | command / control usage accumulators | sdk+0x5c5d30, sdk+0x5c5dd0 |
| +0x2cb0 | NVNqueue* | self | init |
| +0x2cb8 / +0x2cb9 / +0x2cba | u8 | dirty (flush needed) / submission suspended / initialized | sdk+0x5c5d20, init |
| +0x2838 | u64 | ray-tracing memory used | sdk+0x5c5dc0 |

What the init allocates (sdk+0x5c2fc0 → sdk+0x5ce818):
1. `NvRmMemHandleAllocAttr`: a 0x1000-byte error-notifier buffer, mapped with `NvRmMemMap` and zeroed.
2. One of:
   - a normal queue (flags & 0x14 == 0): `NvRmGpuTaskSchedulingGroupCreate` (a new TSG), then the timeslice from the priority;
   - flag 4: `NvRmGpuTSGSubcontextCreate` on the primary queue's TSG;
   - flag 0x10: no TSG.
3. `NvRmGpuChannelCreate` with a GPFIFO of (control memory size / 8) entries (8 bytes each), placed in the builder's queue memory when it was given, otherwise allocated. Then `NvRmGpuChannelGetInfo`; for subcontexts also `NvRmGpuTSGSubcontextGetInfo`.
4. A channel slot from a 256-bit global bitmap. Slot i owns 16 bytes at device semaphore memory (the pool at device+0x1bf0) + i×0x10, and the mirror at +0x1000 + i×0x10, which is zeroed. This matches the "two-page progress region" in report E2.
5. The command-memory pool (+0x1f40), the internal NVNcommandBuffer (+0x2be8) with 0x200 bytes of internal control memory, 17 Syncs, and the queue context memory pool (+0x2a08; zcull bind via `NvRmGpuChannelZcullBind` at sdk+0x5ced80).
6. Registration in device+0x120[slot]. For flag 0x4|0x8 queues, nvnQueueFinish runs once at the end of init.

### Function table

| Function | sdk impl | main uses | NVN1 ABI | Semantics |
|---|---|---|---|---|
| nvnQueueInitialize | 0x5c41a0 | 2 (main+0x8f7228, main+0xcf417c) | same | see above; returns NVNboolean |
| nvnQueueFinalize | 0x5c43c0 | 0 | same | (subcontext: emits a final method) Finish; take the global mutex; finalize the internal sync, contexts, 16 syncs, command buffer and pools; free the slot |
| nvnQueueSetDebugLabel | 0x5b0b50 | 0 | same | **no-op** (`ret`) |
| nvnQueueSubmitCommands | 0x5c5bb0 | 6 (+ more) | same | walks n NVNcommandHandle values (see handle format) |
| nvnQueueFlush | 0x5c5d20 | 4 | same | if dirty or the internal command buffer has new words: close the internal segment, submit the pending GPFIFO entries to the channel, rotate command memory (throttle ring of 16 syncs) |
| nvnQueueFinish | 0x5c40c0 | 2 | same | stack Sync(0x38); internal fence with flush (sdk+0x5c8000(q, s, 0, 1, 0, 0)); flush; nvnSyncWait(s, ~0); finalize |
| nvnQueueGetError | 0x5c4590 | 0 | same arity `(queue, NVNqueueErrorInfo*)`; nvn.h leaves the info struct undefined. The driver writes +0 u64 and +8 u32, only for MMU faults, and only when the pointer is non-NULL | reads the channel error notifier; once faulted, queries `NvRmGpuChannelGetErrorInfo`: type 0→0 NO_ERROR, 2→2 MMU_FAULT (info: u64 fault address, u32 access type 0/1 at +8), 3→4 ENGINE_EXCEPTION, 4→3 PBDMA_EXCEPTION, 5→5 TIMEOUT, other/failure→1 UNKNOWN |
| nvnQueueGetTotalCommandMemoryUsed | 0x5c5d30 | 0 | same | accumulated + current − baseline |
| nvnQueueGetTotalComputeMemoryUsed | 0x5c5db0 | 0 | same | queue+0x08 |
| nvnQueueGetTotalControlMemoryUsed | 0x5c5d70 | 0 | same | (queue+0 − baseline) + pending entries × 8 |
| nvnQueueGetTotalRayTracingMemoryUsed | 0x5c5dc0 | 0 | new | queue+0x2838 |
| nvnQueueResetMemoryUsageCounts | 0x5c5dd0 | 0 | same | zero the counters, rebase |
| nvnQueueFenceSync | 0x5c82f0 | 1 (main+0x262244) | same arity: (queue, sync, condition, flags) | tail call sdk+0x5c8140(q, s, cond, flags, 0, 0); see Sync |
| nvnQueueWaitSync | 0x5c8850 | 0 | same | tail call sdk+0x5c86d0(q, s, 0): GPU semaphore acquire; returns 0 if the sync was never fenced (type 0), else 1 |
| nvnQueuePresentTexture | 0x5cc4a0 | 2 (main+0x2623b4, main+0x226e0f0, through a copy of the function pointer that `nvn_slots_got.py` does not follow) | same | see the present path |
| nvnQueueAcquireTexture | — | — | **removed** | the name existed in NVN1 (names.txt) and is not in NVN2's 698-name table |
| nvnQueueSetTimeoutNVX | 0x5c5e40 | 0 | NVX (`void (NVNqueue*, uint64_t ns)`) | `NvRmGpuChannelSetTimeout(queue+0x30, ns / 1000000 ms)` (sdk+0x5cef40) |
| nvnQueueSetTimesliceNVX | 0x5c2f70 | 0 | NVX (`NVNboolean (NVNqueue*, uint32_t)`) | scales the value by device-info +0x2ec / +0x2e8 (`NvRmGpuDeviceGetInfo`), then `NvRmGpuTaskSchedulingGroupSetTimeslice(queue+0x68, …)`; returns 1 (sdk+0x5cefac) |


### SubmitCommands handle format (sdk+0x5c5bb0)

Each 64-bit NVNcommandHandle is tagged by bits 1:0:
- tag 1: pointer = h & 0xFFFF_FFFF_FFFC; n = h >> 48. If n ≠ 0, the queue flushes pending work, appends n ready-made GPFIFO entries (8 bytes each) from the pointer (sdk+0x5ce37c), marks itself dirty and adds n×8 to the control counter. If n = 0, the pointer is a control-token stream.
- tag 2: a single command-memory segment at address h & 0xFF_FFFF_FFFC, length (h >> 40) words. The queue wraps it as a one-entry GPFIFO token.
- tag 0 or 3: pointer to a control-token stream. The interpreter (sdk+0x5c5280) executes tokens: op 3 GPFIFO entries, 4 jump, 5 FenceSync, 6/0x5b WaitSync, 7 event signal/wait, and so on (see [Command buffers](#command-buffers)).

### MKW call sites

- Frame end (main+0x262214..0x262260): if a command buffer is pending, nvnQueueSubmitCommands(queue, 1, &handle); then nvnQueueFenceSync(queue, frameSync, 0 /*ALL_GPU_COMMANDS_COMPLETE*/, 0); then nvnQueueFlush(queue).
- Queue creation (main+0xcf4034..0xcf4184): SetDefaults → SetFlags(9) → SetDevice → SetComputeMemorySize(cfg or DeviceGetInteger(0x55)) → SetCommandMemorySize(cfg or 0x52) → SetControlMemorySize(cfg or 0x5c) → GetQueueMemorySize → SetQueueMemory(alloc aligned 0x1000, size) → allocate 0x3000 → nvnQueueInitialize.

## WindowBuilder

### NVNwindowBuilder (driver layout, 0x60 bytes; MKW reserves 0x80)

| Offset | Type | Meaning | Evidence |
|---|---|---|---|
| +0x00 | 8 bytes | untouched | SetDefaults sdk+0x5cbce0 |
| +0x08 | NVNdevice* | device | SetDevice sdk+0x5cbcd0 |
| +0x10 | s32 | number of textures | SetTextures sdk+0x5cbd20 |
| +0x14 | s32 | number of active textures (0 = all) | sdk+0x5cbd40, Initialize |
| +0x18 | NVNnativeWindow (void*) | native window (`nn::vi::GetNativeWindow` result) | sdk+0x5cbd10 |
| +0x20 | s32 | present interval − 1 (Set stores n−1, Get returns +1; default 0 means interval 1) | sdk+0x5cbd30, sdk+0x5cbdf0 |
| +0x24 | s32 | color space (new) — **not consumed by nvnWindowInitialize** | sdk+0x5cbd50; Initialize sdk+0x5cbeb0 never reads it |
| +0x28 | NVNtexture* const* | texture array pointer (not copied) | sdk+0x5cbd20, GetTexture sdk+0x5cbdd0 |
| +0x30 | float[10] | HDR display metadata; defaults Rec.709/D65: 0.64, 0.33, 0.30, 0.60, 0.15, 0.06, 0.3127, 0.329, 0.03, 1000.0 | sdk+0x5cbd60; constants sdk+0xac7e20 / +0xac6210 / +0xac8780 |
| +0x58 | float[2] | HDR content metadata; defaults 200.0, 60.0 | sdk+0x5cbda0 |

[INFERENCE] The display metadata is ordered: red x/y, green x/y, blue x/y, white x/y, min luminance, max luminance. The content pair is MaxCLL / MaxFALL-like.

### Function table

| Function | sdk impl | main uses | NVN1 ABI | Semantics |
|---|---|---|---|---|
| nvnWindowBuilderSetDevice | 0x5cbcd0 | 2 | same | +0x08 |
| nvnWindowBuilderSetDefaults | 0x5cbce0 | 2 | same (layout bigger) | zero +0x10..+0x2f, HDR defaults |
| nvnWindowBuilderSetNativeWindow | 0x5cbd10 | 2 | same | +0x18 |
| nvnWindowBuilderSetTextures | 0x5cbd20 | 2 | same `(builder, int n, NVNtexture* const*)` | +0x10 = n, +0x28 = array |
| nvnWindowBuilderSetPresentInterval | 0x5cbd30 | 2 | same | +0x20 = n − 1 |
| nvnWindowBuilderSetNumActiveTextures | 0x5cbd40 | 2 | not in nvn.h (`void (builder, int)`) | +0x14 |
| nvnWindowBuilderSetColorSpace | 0x5cbd50 | 0 | new `void (builder, int)` | +0x24 (unused by Initialize) |
| nvnWindowBuilderSetHdrDisplayMetadata | 0x5cbd60 | 0 | new `void (builder, float × 10)`: 8 in s0–s7, 9th at [sp], 10th at [sp+8] | +0x30..+0x57 |
| nvnWindowBuilderSetHdrContentMetadata | 0x5cbda0 | 0 | new `void (builder, float, float)` | +0x58, +0x5c |
| nvnWindowBuilderGetDevice | 0x5cbdb0 | 0 | same | |
| nvnWindowBuilderGetNumTextures | 0x5cbdc0 | 0 | not in nvn.h | returns +0x10 |
| nvnWindowBuilderGetTexture | 0x5cbdd0 | 0 | not in nvn.h `NVNtexture* (builder, int)` | NULL when out of range |
| nvnWindowBuilderGetPresentInterval | 0x5cbdf0 | 0 | same | +0x20 + 1 |
| nvnWindowBuilderGetNumActiveTextures | 0x5cbe00 | 0 | not in nvn.h | +0x14 |
| nvnWindowBuilderGetNativeWindow | 0x5cbe10 | 0 | same | +0x18 |
| nvnWindowBuilderGetColorSpace | 0x5cbe20 | 0 | new | +0x24 |
| nvnWindowBuilderGetHdrDisplayMetadata | 0x5cbe30 | 0 | new `void (const builder*, float* × 10)` | |
| nvnWindowBuilderGetHdrContentMetadata | 0x5cbe90 | 0 | new `void (const builder*, float*, float*)` | |

## Window

### NVNwindow (driver layout, 0x218 bytes; MKW reserves 0x280)

| Offset | Type | Meaning | Evidence |
|---|---|---|---|
| +0x000 | NVNdevice* | device. Device+0 (u32) is the window origin mode (nvnDeviceSetWindowOriginMode sdk+0x5b1110 writes device+0); it goes to the native window as `SetNativeWindowNextOrigin` | Initialize; backend sdk+0x5be3f0 |
| +0x008 | void* | native window | GetNativeWindow sdk+0x5cc170 |
| +0x010 | s32[4] | crop x, y, w, h (default 0, 0, tex0 width, tex0 height) | Initialize, SetCrop sdk+0x5cc2a0, GetCrop sdk+0x5cc2d0 |
| +0x020 | s32 | number of textures (clamped to ≤ 4) | Initialize |
| +0x024 | s32 | number of active textures (builder +0x14, or all) | Initialize, sdk+0x5cc060 |
| +0x028 | s32 | present interval (−1 before init) | Initialize, SetPresentInterval sdk+0x5cc070 |
| +0x02c | s32 | acquired texture index (−1 = none) | AcquireTexture, PresentTexture |
| +0x030 | s32 | last presented index | PresentTexture |
| +0x038 | float[10] | HDR display metadata | sdk+0x5cc190 / sdk+0x5cc220 |
| +0x060 | float[2] | HDR content metadata | sdk+0x5cc1f0 / sdk+0x5cc280 |
| +0x068 | NVNtexture*[4] | textures. The driver also writes texture+0x30 = window | Initialize, Finalize |
| +0x088 | SyncPayload[4] (0x20 each) | acquire fence of each texture: {device, type, payload}. Initialized "signaled" (type 1) | Initialize, backend acquire sdk+0x5be760 |
| +0x108 | SyncPayload[4] | previous acquire fence of each texture (type 1 at init) | sdk+0x5be760 |
| +0x190 | SyncPayload[4] | GPU-completion fence of the last present of each texture | PresentTexture |

### Function table

| Function | sdk impl | main uses | NVN1 ABI | Semantics |
|---|---|---|---|---|
| nvnWindowInitialize | 0x5cbeb0 | 2 (main+0x262744, main+0xe2bb1c) | same | creates the global window backend once (sdk+0x5cf380); memset 0x218; copies the builder; backend init (→ wsi); then applies the present interval. Returns false when wsi rejects the format |
| nvnWindowFinalize | 0x5cc0d0 | 1 | same | backend finalize (→ `nn::wsi::nv::OnWindowFinalize`); clears texture+0x30 and the fence payloads |
| nvnWindowSetDebugLabel | 0x5b0b90 | 0 | same | **no-op** |
| nvnWindowAcquireTexture | 0x5cc420 | 3 | same `(window, NVNsync*, int*)` | see the acquire path; always returns 0 (SUCCESS); wsi aborts on failure |
| nvnWindowGetNativeWindow | 0x5cc170 | 0 | same | +0x08 |
| nvnWindowGetPresentInterval | 0x5cc180 | 0 | same | +0x28 |
| nvnWindowSetPresentInterval | 0x5cc070 | 1 (main+0xcf45f8) | same | if changed: backend slot 0x20 (always returns 1) → +0x28. Takes effect at the next present (SetNativeWindowNextSwapInterval) |
| nvnWindowSetCrop | 0x5cc2a0 | 3 | same `(window, x, y, w, h)` | w == 0 or h == 0 resets to (0, 0, tex0 width, tex0 height). Applied at present |
| nvnWindowGetCrop | 0x5cc2d0 | 0 | same `(window, NVNrectangle*)` | |
| nvnWindowSetNumActiveTextures | 0x5cc060 | 1 (main+0x226e154) | not in nvn.h `void (window, int)` | +0x24 |
| nvnWindowGetNumActiveTextures | 0x5cc310 | 0 | not in nvn.h | +0x24 |
| nvnWindowGetNumTextures | 0x5cc300 | 0 | not in nvn.h | +0x20 |
| nvnWindowSetHdrDisplayMetadata | 0x5cc190 | 0 | new `void (window, float × 10)` | stores +0x38..+0x5f; backend slot 0x30 → `nn::wsi::nv::OnDisplayMetadataUpdate`, which is a **no-op** (`ret`, sdk+0x541b40) |
| nvnWindowGetHdrDisplayMetadata | 0x5cc220 | 0 | new | |
| nvnWindowSetHdrContentMetadata | 0x5cc1f0 | 0 | new `void (window, float, float)` | stores +0x60/+0x64; slot 0x38 → `OnContentMetadataUpdate`, a **no-op** (sdk+0x541b50) |
| nvnWindowGetHdrContentMetadata | 0x5cc280 | 0 | new | |

MKW handles HDR itself through `nn::vi::SetLayerNextHdrStandardContentLuminanceRangeMetadata` / `…ExtendedContent…` (main+0x2622cc / main+0x2622ec), `QueryDisplayHdrStatus` and `QueryDisplayHdrPaperWhite`. It never calls the NVN HDR metadata functions.

### Window backend ("display plugin") object

A singleton at sdk GOT 0xd7b238 → storage at sdk 0xedb338. It is created by sdk+0x5cf380 (field +8 = 3, +0xc = 1; vtable = sdk GOT 0xd7b1e0 target + 0x10). The builder argument passed to it is ignored.

| Slot | Impl | Role |
|---|---|---|
| 0x00 | sdk+0x5be3f0 | init: fills 4 × `TextureInfoV1` (0x30 bytes each) and calls `nn::wsi::nv::OnWindowInitialize(pluginWindow, infos, n, "NVN")`. For formats 0x29/0x71/0x3d it then pushes the HDR metadata (slots 0x38, 0x30) |
| 0x08 | sdk+0x5be664 | `nn::wsi::nv::OnWindowFinalize` |
| 0x10 | sdk+0x5be760 | acquire |
| 0x18 | sdk+0x5be6c4 | CPU wait (pacing) |
| 0x20 | sdk+0x5be89c | set interval: returns 1, no-op |
| 0x28 | sdk+0x5be6e8 | present |
| 0x30 / 0x38 | sdk+0x5be828 / sdk+0x5be868 | HDR metadata |
| 0x40 | sdk+0x5be8a4 | no-op |

`NVNdisplayPluginWindowV2` (built on the stack by each slot):

| Offset | Meaning |
|---|---|
| +0x00 | native window |
| +0x08 | crop x, y, w, h |
| +0x18 | origin mode (device+0) |
| +0x1c | present interval (window+0x28) |

`TextureInfoV1` (0x30 bytes):

| Offset | Meaning | Source |
|---|---|---|
| +0x00 | width | texture+0x20 |
| +0x04 | height | u16 texture+0x24 |
| +0x08 | format | |
| +0x0c | tiling/layout word | |
| +0x10 | GPU buffer address of the texture's memory pool | |
| +0x18 | NvRmMem fd of the pool | `NvRmMemGetFd` |
| +0x1c | pool size | |
| +0x20 | texture offset in the pool | texture+0x18 |
| +0x24 | computed image size | |
| +0x28 | u32 | texture+0x71 |
| +0x2c | bool (compressible/linear-ish) | |

Unused entries have fd = −1.

### Present path (driver → nn::)

Acquire, `nvnWindowAcquireTexture(window, sync, &index)` (sdk+0x5cc420):
1. backend slot 0x10 (sdk+0x5be760) → `nn::wsi::nv::OnAcquireTexture` (sdk+0x541890) → `nn::wsi::ounce::native::NativeWindow::Acquire` (sdk+0x542088) → **`nn::gs::sdk::AcquireNativeWindowBuffer(u32* index, NativeSyncHandle*, void* nativeWindow)` (sdk+0x3f5510)**, a virtual call (slot 0x68) on the vi2 layer native window (`nn::vi2::impl2::TLayerNativeWindow`, vtable sdk+0xd66c60; slot target sdk+0x53d2e8). The handle is converted with `nn::gs::external::GetNvRmFenceFromNativeSyncHandle` (sdk+0x3f5c30) into an NvRm fence (syncpoint id, value).
2. window+0x2c = index. The fence payload A[index] (+0x88) moves to B[index] (+0x108). A[index] = {type 5, count 1, fence}.
3. backend slot 0x18 (sdk+0x5be6c4): `nvnSyncWait(B[k−1], ~0)` with k = index (or the active-texture count when index is 0). Then `nvnSyncWait(C[index] (+0x190), ~0)`, i.e. the CPU blocks until the GPU finished the previous present of this texture. [INFERENCE: this is the frame pacing.]
4. The user's NVNsync receives the first 0x20 bytes of A[index] (a type-5 syncpoint sync). `*index` is set. The function returns 0.

Present, `nvnQueuePresentTexture(queue, window, textureIndex)` (sdk+0x5cc4a0 → sdk+0x5b5980):
1. cur = window+0x2c. textureIndex only selects the texture used in step 2. The buffer flipped is wsi's acquired index (`NativeWindow::GetIndex`).
2. If backend+0xc (= 1) and texture bit 3 of the 48-bit field at texture+0x28 are set, the queue's internal command buffer gets a texture "make displayable" operation (sdk+0x5c9dec + sdk+0x5faa90). [INFERENCE: compression resolve.]
3. Internal fence into C[cur]: sdk+0x5c8000(queue, C[cur], 0, (texture->+0x10->+0x10 >> 5) & 1, 0, 0). Then `nvnQueueFlush(queue)`.
4. backend slot 0x28 (sdk+0x5be6e8) builds `NVNdisplayPluginSync` {type 2 = NvRm fence, fence = C[cur]+0x18 (syncpoint id, value)} → **`nn::wsi::nv::OnPresentTexture`** (sdk+0x5419d0):
   - `nn::gs::external::GetNativeSyncHandleFromNvRmFence` (sdk+0x3f5c70);
   - `NativeWindow::UpdateCrop` → `nn::gs::sdk::SetNativeWindowNextCrop(win, x, y, w, h)` (sdk+0x3f5590);
   - `NativeWindow::UpdatePresentInterval` → `nn::gs::sdk::SetNativeWindowNextSwapInterval(win, interval)` (sdk+0x3f5570);
   - `NativeWindow::Present` (sdk+0x5420f8): `nn::os::GetSystemTick` (stored at NativeWindow+0xa0; this is the only present timestamp), the optional `ITimestampRecorderPool` callback, then **`nn::gs::sdk::PresentNativeWindowBuffer(void* win, u32 index, NativeSyncHandle const&)` (sdk+0x3f5530)**, a virtual slot 0x70 on the vi2 native window (target sdk+0x53d354). This ends in vi2 TIPC 229 PresentClientBuffer per report E3.
5. window+0x30 = cur; window+0x2c = −1. Backend+8 is 3, not 0, so C[cur] is not copied into A[cur].

Window init chain (backend slot 0 → `nn::wsi::nv::OnWindowInitialize`, sdk+0x5413b0). It picks one of 8 static NativeWindow slots (sdk 0xf16290 + k×0xd8) and calls `NativeWindow::Initialize` (sdk+0x541b60):
- `nn::gs::sdk::ConnectNativeWindow(win, "NVN", attr)` (sdk+0x3f5460).
- Format check, which also selects pixel kind / color space / linear luminance:

  | Format | Pixel kind | Color space (arg) | Linear luminance |
  |---|---|---|---|
  | RGBA8 0x25, RGBA8_SRGB 0x38 | 1 | 0 | — |
  | RGBX8_SRGB 0x37, RGBX8 0x6d | 3 | 0 | — |
  | BGRA8 0x9a, BGRA8_SRGB 0x9c | 2 | 0 | — |
  | RGB10A2 0x3d | 4 | 1 | — |
  | RGBA16F 0x29 | 5 | 2 | 80 |
  | RGBX16F 0x71 | 6 | 2 | 80 |

  Any other format → `DisconnectNativeWindow` and init fails.
- `SetNativeWindowColorSpace`, `SetNativeWindowLinearUnitLuminance`.
- `nn::gpu::sdk::MakeRgbaBlockImagePreference(w, h, …)`.
- Per texture (`InsertTextureImpl`, sdk+0x541d50): `CalculateNativeImageSize`, `gs::external::CreateFromFd(pool fd)`, `MapNvRmMem`, `nn::gpu::sdk::CreateNativeImage` (the texture memory becomes a GpuOunce native image), `gs::sdk::CreateNativeWindowAliasImagePool(win, image, 1)`, `gs::sdk::RegisterNativeWindowAliasBuffer(win, i, image)`.
- `SetNativeWindowNextOrigin(win, originMode)`.

The window textures are therefore registered with the compositor as alias buffers of the game's own texture memory: no copy.

Native window source: MKW imports `nn::vi::Initialize`, `OpenDefaultDisplay` (main+0xcf3a1c), `CreateLayer` (main+0xcf3a28), `GetDisplayVsyncEvent` (main+0xcf3a34) and `nn::vi::GetNativeWindow(void**, Layer*)` (main+0xcf3bf4; sdk impl sdk+0x536450). The resulting pointer is the NVNnativeWindow handed to `nvnWindowBuilderSetNativeWindow`. It is a vi2 `TLayerNativeWindow` implementing `nn::gs::sdk::INativeWindow`.

Hooks for nxnative:
- **If NVN2 is reimplemented at the API level**, nvnQueuePresentTexture / nvnWindowAcquireTexture are the host's own functions, and only the `nn::vi` layer calls MKW makes directly need hooks:
  - `nn::vi::OpenDefaultDisplay`, `CreateLayer`, `GetNativeWindow` (return a token);
  - `GetDisplayVsyncEvent` (60 Hz event);
  - `SetLayerExtendedSwapInterval`, `ClearLayerExtendedSwapInterval`, `SetDisplayVrrCapability`, `QueryDisplayVrrSupported` (main+0x226ce00..0x226ce38);
  - HDR queries and metadata setters.
- **If the driver's NVN2 is kept and only present is replaced**, hook the display-plugin boundary (sdk exports, above IPC): `nn::wsi::nv::OnWindowInitialize` (sdk+0x5413b0), `OnAcquireTexture` (sdk+0x541890), `OnPresentTexture` (sdk+0x5419d0), `OnWindowFinalize` (sdk+0x5416b0). One level lower, hook `nn::gs::sdk::AcquireNativeWindowBuffer` / `PresentNativeWindowBuffer` / `RegisterNativeWindowAliasBuffer` / `SetNativeWindowNextCrop` / `SetNativeWindowNextSwapInterval`.

### MKW window setup (display class: ctor main+0xcf4678, vtable main+0x3260ce0, object 0xd0 bytes allocated at main+0xcf41a0)

Constructor defaults:

| Field | Default |
|---|---|
| format (+0x68) | 0x25 RGBA8 |
| present interval (+0xa8) | 1 |
| texture count (+0xa9) | 2 |
| native window (+0xa0) | from `nn::vi::GetNativeWindow` |
| crop (+0xac..+0xb8) | 0 |

Setter main+0xcf45bc: texture count = 3 if the triple-buffer flag is set, else 2. Setter main+0xcf45d4: present interval, which calls nvnWindowSetPresentInterval when the window exists. When the HDR layer data exists, alternate formats are registered: 0x3d (RGB10A2) or 0x29 (RGBA16F) (main+0xcf41ec). The format is switched at runtime between 0x25 and 0x29/0x3d (main+0x26227c).

Size: floats at display+8/+0xc, from the config's u32 width/height (main+0xcf3a38). Default config 2560×1440: `movk #0x5a0 / #0xa00` at main+0xcf3574. The resolution table {640×480, 720×480, 1280×720, 1600×900, 1920×1080, 2560×1440, 3840×2160} at main+0x2a16174 is referenced from main+0xe33e50 and main+0x2323270. BootHost's runtime saw 3 textures of 2560×1440, format 0x25, interval 1. Report E3 (facts only) says MKW keeps docked-size images and draws into the top-left. The crop is what selects the presented rectangle.

Creation sequence (vtable +0x10 = main+0xe2b674):

| Step | Call(s) | Values / where |
|---|---|---|
| 1 | per texture: TextureBuilder SetDevice → SetDefaults → SetSize2D(w, h) → SetTarget(1) → SetFormat(fmt) → SetFlags(1) | |
| 2 | MemoryPoolBuilder SetFlags(0x21), storage = Σ aligned sizes rounded to 4 KiB | pool object 0x100 bytes |
| 3 | TextureInitialize ×n | |
| 4 | nvnSyncInitialize | sync allocated as 0x40 bytes |
| 5 | allocate the window | 0x280 bytes |
| 6 | WindowBuilder SetDefaults → SetDevice → SetNativeWindow(display+0xa0) → SetTextures(n, display+0x88) → SetNumActiveTextures(n) → SetPresentInterval(display+0xa8) | |
| 7 | nvnWindowInitialize | |
| 8 | nvnWindowAcquireTexture(window, sync, &display+0x80) | main+0xe2bb30 |
| 9 | crop = (0, 0, w, h) | |

Per frame:
- main+0x262360: `nvnQueuePresentTexture(G->queue (G+0x68), window, display+0x80)`.
- Resize check main+0x262424, when +0xc8 is set: nvnSyncWait(sync, ~0), nvnQueueFinish, nvnWindowFinalize, nvnTextureFinalize ×n; rebuild the textures with the new size/format; WindowBuilder again; nvnWindowInitialize; nvnWindowSetCrop(display+0xac..).
- Then `nvnWindowAcquireTexture(window, sync, &display+0x80)` (tail call).
- Before rendering into it: `nvnSyncWait(sync, UINT64_MAX)` (vtable methods at main+0x226db44 / main+0x226e130). MKW waits on the CPU, not with nvnQueueWaitSync.
- Dynamic crop: main+0x226e184 calls `nvnWindowSetCrop(window, x, y, w, h)` from display+0xac..+0xb8 when the dirty flag at +0xaa is set.
- Vsync / VRR: MKW computes its frame period from the configured interval (main+0xcf3c64: (interval + c) / 60 × tick frequency, stored at cfg+0x120). It uses `nn::vi::SetLayerExtendedSwapInterval` and `SetDisplayVrrCapability` directly (main+0x226ce00..0x226ce38).

## Sync

### NVNsync (driver layout, 0x38 bytes; reserve 0x40)

| Offset | Type | Meaning | Evidence |
|---|---|---|---|
| +0x00 | NVNdevice* | device | nvnSyncInitialize sdk+0x5c7c00 |
| +0x08 | u32 | type: 0 never fenced, 1 signaled, 2 queue fence, 3 CUDA-interop semaphore, 4 GL-interop sync, 5 NvRm syncpoints | sdk+0x5c8300, sdk+0x5c8000, sdk+0x5cc4f0 (case 6 sets type 3), sdk+0x5c8860, sdk+0x5c7cc0 |
| +0x0c | s32 | type 5: syncpoint count (≤ 4) | sdk+0x5c7cc0 |
| +0x10 | u32 / u64 / ptr | type 2: channel slot. Type 3: byte offset in the device pool at device+0x1bf8. Type 4: pointer to a heap record {u64 cpu ptr, u64 gpu addr, u32 value}. Type 5: syncpoint[0] {u32 id, u32 value} | sdk+0x5c8300, sdk+0x5c8950 |
| +0x14 | u32 | type 2: fence value (low 32 bits of the 64-bit counter at device+0xbe8+slot×0x10) | sdk+0x5c8000 |
| +0x18 | {u32 id, u32 value} | type 2: the channel syncpoint fence (queue+0x98 id, base + increments). Type 3: value. Type 5: syncpoint[1] | sdk+0x5cee88, sdk+0x5c7d30 |
| +0x10..+0x2f | syncpoint[4] | type 5 | sdk+0x5c7e60 |

nvnSyncInitialize writes only +0x00..+0x1f (device, type 0) and returns 1. nvnSyncFinalize only frees the type-4 GL record (through allocator callback +0x18 at sdk GOT 0xd7b018).

FenceSync implementation (sdk+0x5c8140 → sdk+0x5c8000):
- `condition` is only encoded into a begin/end debug marker (sdk+0x5fed4c, op 0, 0xc, …). The fence itself is the same for ALL_GPU_COMMANDS_COMPLETE and GRAPHICS_WORLD_SPACE_COMPLETE.
- `flags & 1` (FLUSH_FOR_CPU) inserts a flush/wait-idle into the internal command buffer (sdk+0x5fde90(cmdbuf, 0, 1)).
- The 64-bit per-slot counter device+0xbe8+slot×0x10 is incremented. The sync becomes {type 2, slot, value}, and a semaphore release of the value is written to the device semaphore memory (device+0x1bf0 pool: +0x18 GPU base, +0x30 CPU base) + slot×0x10 (sdk+0x5fdc40). Subcontext queues go through sdk+0x5ed258 instead.
- Normal queues also increment the channel syncpoint (sdk+0x5cee88) and store {syncpoint id, threshold} at +0x18.
- A nvnCommandBufferFenceSync recorded in a command buffer becomes token op 5 and runs the same code at submit (sdk+0x5c5280).

nvnSyncWait(sync, timeoutNs) (sdk+0x5c8300 → sdk+0x5c8310). Results are the NVN1 values: 0 ALREADY_SIGNALED, 1 CONDITION_SATISFIED, 2 TIMEOUT_EXPIRED, 3 FAILED.

| Type | Behavior |
|---|---|
| 0 or unknown | 3 |
| 1 | 0 |
| 2 / 3 / 4 | Read the 32-bit semaphore word (type 2: semaphore CPU base + slot×0x10; type 3: the +0x1bf8 pool CPU base + (offset & ~0xf); type 4: record ptr). If (int)(word − value) ≥ 0, return 0. Otherwise loop until the deadline (timeout / 1000 added to a µs clock; ~0 = forever): type 2 blocks in `NvRmSyncWait` on the syncpoint fence at +0x18, in ≤ 100 ms slices (sdk+0x5befbc: `NvRmSyncCreateFromSyncpointsWithAttr` → `NvRmSyncWait(ms)` → `NvRmSyncClose`); types 3/4 sleep through a host callback. Every 100 ms it runs nvnQueueGetError on every registered queue (sdk+0x5c4520). Returns 1 when satisfied, 2 on timeout, 3 on NvRm failure |
| 5 | `NvRmSyncWait` on all syncpoints: 0 if already reached, 2 if timeout is 0 or expires, 1 when reached |

nvnQueueWaitSync / CommandBufferWaitSync (sdk+0x5c85d0) insert a GPU acquire into the queue's internal command buffer (sdk+0x5fdbc0):

| Type | GPU wait |
|---|---|
| 2 | semaphore GPU address + slot×0x10 ≥ value |
| 3 | pool GPU address + offset |
| 4 | the record's GPU address |
| 5 | per syncpoint, a semaphore wait on the read-only syncpoint shim address from `NvRmGpuAddressSpaceGetSyncpointShimRoAddress` (sdk+0x5cee1c) |
| 0 | returns 0 without waiting |
| 1 | returns 1 without waiting |

The window acquire fence is type 5 and can be waited on by the GPU.

### Function table

| Function | sdk impl | main uses | NVN1 ABI | Semantics |
|---|---|---|---|---|
| nvnSyncInitialize | 0x5c7c00 | 3 | same | {device, type 0}; returns 1 |
| nvnSyncFinalize | 0x5c7c70 | 1 | same | frees the type-4 record only |
| nvnSyncSetDebugLabel | 0x5b0b70 | 0 | same | **no-op** |
| nvnSyncWait | 0x5c8300 | 4 | same `(const NVNsync*, uint64_t ns)` | see above |
| nvnQueueFenceSync | 0x5c82f0 | 1 | same | see above |
| nvnQueueWaitSync | 0x5c8850 | 0 | same | see above |
| nvnSyncInitializeFromFencedGLSync | 0x5c8860 | 0 | not in nvn.h: `NVNboolean (NVNsync*, NVNdevice*, GLsync-ish u32/handle, int *error)` | through the GL-interop interface (sdk GOT 0xfdb160, set by nvnSetGLInteropNVX [INFERENCE]); type 4; *error: 0 ok, 1, 3, 4 = no interop |
| nvnSyncCreateGLSync | 0x5c8950 | 0 | not in nvn.h: `int (const NVNsync*, uint64_t *glSync)` | 0 ok; 2 for type 1; 4 for unsupported types or failure; 1 for interop result 1 |
| nvnSyncInitializeFromNvRmSyncNVX | 0x5c7cc0 | 0 | NVX `NVNboolean (NVNsync*, NVNdevice*, NvRmSync*)` | extracts ≤ 4 syncpoints (sdk+0x5cc320: `NvRmSyncIsSyncpoints` / `IsFd` / `GetNumSyncpoints` / `ExtractSyncpoint`) → type 5 |
| nvnSyncGetNvRmSyncNVX | 0x5c7d30 | 0 | NVX `NVNboolean (const NVNsync*, NvRmSync* inplace)` | types 0/1: `NvRmSyncClose(arg)`, true. Type 2: one syncpoint (+0x18). Type 5: count syncpoints (+0x10). Both via `NvRmSyncCreateFromSyncpointsWithAttr` with arg in the attribute block. Other types: false |
| nvnSyncGetSyncpointsCountNVX | 0x5c7dc0 | 0 | NVX `NVNboolean (const NVNsync*, int*)` | type 5 only |
| nvnSyncGetSyncpointsNVX | 0x5c7df0 | 0 | NVX `NVNboolean (const NVNsync*, int count, uint32_t (*out)[2])` | type 5 and count == stored count: copy {id, value} pairs |
| nvnSyncSetSyncpointsNVX | 0x5c7e60 | 0 | NVX `NVNboolean (NVNsync*, int count, const uint32_t (*in)[2])` | type 5 and same count: overwrite the pairs |

SDK-side helper (not in the proc table): `nvnSyncInitializeFromNativeSyncHandle(NVNsync*, NVNdevice*, nn::gs::NativeSyncHandle)` (sdk+0x612bc0) converts the gs handle → NvRm fence → `NvRmSyncCreateFromSyncpoints(…, 1)`, then calls nvnSyncInitializeFromNvRmSyncNVX through a bootstrap-loaded pointer.

## Event / EventBuilder

### NVNeventBuilder (0x10 used)

| Offset | Meaning |
|---|---|
| +0x00 | const NVNmemoryPool* |
| +0x08 | int64 offset |

Evidence: SetDefaults sdk+0x5bb5d0, SetStorage sdk+0x5bb5e0.

### NVNevent (0x28 used; reserve 0x40)

| Offset | Type | Meaning |
|---|---|---|
| +0x00 | u64 | GPU address = pool+0x18 (GPU base) + offset |
| +0x08 | u32* | CPU pointer = pool+0x30 + offset, only when pool flags (pool+0x10) bit 0 is clear (CPU-accessible), else NULL |
| +0x10 | NVNmemoryPool* | pool |
| +0x18 | int64 | offset |
| +0x20 | u64 | zeroed |

Evidence: nvnEventInitialize sdk+0x5bb620.

| Function | sdk impl | main uses | NVN1 ABI | Semantics |
|---|---|---|---|---|
| nvnEventBuilderSetDefaults | 0x5bb5d0 | 2 | same | pool = NULL, offset = 0 |
| nvnEventBuilderSetStorage | 0x5bb5e0 | 1 (+1 via slot) | same `(builder, const NVNmemoryPool*, int64_t)` | |
| nvnEventBuilderGetStorage | 0x5bb5f0 | 0 | not in nvn.h `const NVNmemoryPool* (const builder*, int64_t*)` | |
| nvnEventBuilderGetMemoryPool | 0x5bb600 | 0 | not in nvn.h | |
| nvnEventBuilderGetMemoryOffset | 0x5bb610 | 0 | not in nvn.h | |
| nvnEventInitialize | 0x5bb620 | 2 | same | layout above; returns 1 |
| nvnEventFinalize | 0x5bb670 | 0 | same | **no-op** |
| nvnEventGetValue | 0x5bb680 | 0 | same | `*cpuPtr` (crashes on CPU-no-access pools) |
| nvnEventGetMemoryPool | 0x5bb690 | 0 | not in nvn.h | +0x10 |
| nvnEventGetMemoryOffset | 0x5bb6a0 | 0 | not in nvn.h | +0x18 |
| nvnEventSignal | 0x5bb6b0 | 3 | same `(event, NVNeventSignalMode, uint32_t)` | mode 1 (ADD): *p += v; otherwise *p = v; then `dmb ish` |

No new Event functions exist in NVN2. GPU-side signal/wait (nvnCommandBufferSignalEvent sdk+0x5bbc30, WaitEvent sdk+0x5bb6d0) are in [Command buffers](#command-buffers).

MKW (main+0x8f7044..0x8f70a4): EventBuilderSetDefaults → SetStorage(pool, offset k×4) → nvnEventInitialize(obj+0x1b0+k×0x40) for each of 5 slots.

## Open questions: queues, windows, syncs and events

- Official names of queue flags 0x4 / 0x8 / 0x10. The behavior is established; the names are guesses. Is 0x10 the copy-engine queue? Device info +0x27c is presumably that engine's class.
- MKW's queue loop (main+0x8f70c4) passes flags 1 to slot 0, while slots 1–2 use flag 4 with SetPrimaryQueue(obj+8). The init requires the primary to have flag 0x8. Is obj+8 the flags-9 queue made at main+0xcf4034, or are slots 1–2 never enabled? (Each slot is gated by a bit at slot+0x50.)
- The units of the timeslice values (0x80 / 0x400 / 0x3fc00) and of the nvnQueueSetTimesliceNVX argument before scaling by device-info +0x2ec/+0x2e8.
- Whether the NVN2 header's reserved sizes are exactly MKW's allocation sizes (queue 0x3000, window 0x280, window builder 0x80, sync 0x40, event 0x40). Queue builder reserve is 0x48..0x50; event builder reserve was not established.
- Window builder +0x24 (color space) is stored but not read by nvnWindowInitialize. The wsi color space follows the texture format instead. Is it read elsewhere?
- Exact meaning of the texture flag that triggers the present-time "make displayable" pass (texture+0x28 bit 3, backend+0xc = 1).
- The final presented size MKW uses docked: static default 2560×1440 (main+0xcf3574) versus report E3's 3840×2160 images cropped to 2560×1440. The resolution table at main+0x2a16174 suggests a runtime choice; trace main+0xe33e50 / main+0x2323270 to settle it.
- nvnSyncGetNvRmSyncNVX: the exact meaning of its second argument (passed in the attribute block of `NvRmSyncCreateFromSyncpointsWithAttr`, closed for unfenced syncs).
