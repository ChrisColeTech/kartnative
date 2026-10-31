# NVN2 (Switch 2 NVN)

The NVN API of Switch 2 games (NVN2), as Mario Kart World 1.0.0 (`0400C3F00006E000`, SDK `NintendoSDK_NVN-19_3_3`) uses it: how the game reaches the driver, every function it fetches, object sizes and layouts, and the paths a Metal implementation has to replace (shader programs, memory pools, present, syncs, DLSS). It is the reference for reimplementing NVN2 on Metal; the host does not implement NVN2 yet (Switch 1 NVN: [nvn-objects.md](nvn-objects.md), [rendering.md](rendering.md)).

Evidence is given as module offsets in this game's build: `sdk+0x…` in the sdk module (build ID `90ebba182862b9ecdc21fcb87b999012`, which contains the NVN2 driver) and `main+0x…` in main (`9e244438c3d8ef1c703a6a85ef99c4f0`). Offsets are into the loaded image (`dm/tools/nso.py <module> <out.bin>`; Ghidra on `dm/tools/nso2elf.py` output adds 0x100000). "NVN1" means the NVN 55 interface in `dm/gfx/nvn/include/nvn.h` that the host implements; "same ABI" means the driver uses the arguments `nvn.h` declares, with the same types. Statements marked [INFERENCE] were not checked against code.

## Entry points

### How MKW fetches NVN2 functions

- main imports two sdk symbols: the function `nvnBootstrapLoader` (sdk+0x6129c0) and the data word `nvnPrivate0` (u32, sdk+0xedb150; main reaches it through its GOT at main+0x32f40e8). The real `nvnDeviceGetProcAddress` sets it to 1 when it answers a device lookup from the driver's own dispatch table (sdk+0x5b6704); `nvnDeviceFinalize` clears it (sdk+0x5b0c7c). `nvnInstallGlobalDebugCallback` (sdk+0x5b0af0) is an empty function in this driver. main tests it at 797 places before its inlined header fast paths, which write `NVNcommandBuffer` internals (command and control pointers, command-handle words) directly; with 0 every such site calls the fetched function instead. **A host implementation must leave `nvnPrivate0` at 0** (it is sdk bss, zero at load, and only the sdk's own lookup writes it), so all NVN2 calls reach the host's functions.
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
- tag 0 or 3: pointer to a control-token stream. The interpreter (sdk+0x5c5280) executes tokens: op 3 GPFIFO entries, 4 jump, 5 FenceSync, 6/0x5b WaitSync, 7 event signal/wait, and so on (see the command-buffer section).

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

No new Event functions exist in NVN2. GPU-side signal/wait (nvnCommandBufferSignalEvent sdk+0x5bbc30, WaitEvent sdk+0x5bb6d0) are in the command-buffer section.

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
