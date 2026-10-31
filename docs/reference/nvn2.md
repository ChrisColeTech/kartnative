# NVN2 (Switch 2 NVN)

The NVN API of Switch 2 games (NVN2), as Mario Kart World 1.0.0 (`0400C3F00006E000`, SDK `NintendoSDK_NVN-19_3_3`) uses it: how the game reaches the driver, every function it fetches, object sizes and layouts, and the paths a Metal implementation has to replace (shader programs, memory pools, present, syncs, DLSS). It is the reference for reimplementing NVN2 on Metal; what the host implements so far is in [Host implementation](#host-implementation) (Switch 1 NVN: [nvn-objects.md](nvn-objects.md), [rendering.md](rendering.md)).

Evidence is given as module offsets in this game's build: `sdk+0x…` in the sdk module (build ID `90ebba182862b9ecdc21fcb87b999012`, which contains the NVN2 driver) and `main+0x…` in main (`9e244438c3d8ef1c703a6a85ef99c4f0`). Offsets are into the loaded image (`dm/tools/nso.py <module> <out.bin>`; Ghidra on `dm/tools/nso2elf.py` output adds 0x100000). "NVN1" means the NVN 55 interface in `dm/gfx/nvn/include/nvn.h` that the host implements; "same ABI" means the driver uses the arguments `nvn.h` declares, with the same types. Statements marked [INFERENCE] were not checked against code.

Clean-room C declarations of these facts (object sizes, enums the binary shows, prototypes of every new function and of every function whose ABI changed) are in [`dm/gfx/nvn/include/nvn2.h`](../../dm/gfx/nvn/include/nvn2.h).

## Host implementation

The host keeps one NVN layer for both consoles: the NVN1 implementations in `dm/gfx/nvn/hle_nvn.c` serve NVN2 wherever this page says "same ABI", and the differences live in `dm/gfx/nvn/nvn2_*.c`, registered with `NVN2(name, ret, args...)` ([`nvn_impl.h`](../../dm/gfx/nvn/nvn_impl.h)). Those entries go to their own section (`__DATA,swnvn2`), which `nvn_init_slots` applies over the NVN1 table only in a Switch 2 build (`DM_GAME_SWITCH2`, profile `switch2 on`); they also take precedence over hle_nvn.c's empty-implementation list. A Switch 1 build never applies them, and every NVN2-only branch inside hle_nvn.c tests `DM_GAME_SWITCH2`. Core behaviour (device, pools, queues, syncs, events, handles):

| Area | Host behaviour | File |
|---|---|---|
| Version and device information | 231.16 and the driver's table for items 0x00–0xb0 ([Device information](#device-information)); 0x2e–0x31 = 201.0–201.11, without the NVN1 GLSLC override. Device-dependent items: texture limits from the driver's constants (32768 / 16384 3D / 2048 layers), the SM version 0x808 → item 0x7d = 8, and host choices where the driver asks NvRm (L2 1 MiB, scratch factors 12 / 384 / 384, item 0x62 = 0, item 0x9a = 0). `DM_NVN_API=major.minor` still overrides the version | `nvn2_device.c` |
| `nvnPrivate0` | read at `nvnDeviceInitialize` through the sdk export; logged `[RED]` if non-zero or missing (MKW: 0) | `nvn2_device.c` |
| Device builder and device | 0x18-byte builder; flags, NVX reserved address ranges stored (a non-empty list is `[RED]`: the host reserves no GPU ranges); `GetUniformBufferHandle`, `GetCommandHandleMemorySize`, fast-clear registrations (accepted, the host has no compression), checkpoint and debug functions as the release driver answers them | `nvn2_device.c` |
| Memory pools | GPU address = storage address for every storage-backed pool (as the driver maps them, fixed inside its allocation over the application range); a virtual pool gets a host-reserved address range of its own, outside every storage, which MapVirtual aliases or tracks page by page (hle_nvn.c `vpool_reserve`). Flags are normalised as `nvnMemoryPoolInitialize` does and `GetFlags` hides bits 22–31; builder defaults 0x21; NVX: no NvRm handles (import is `[RED]`), generic address = storage, special address = storage for COMPRESSIBLE pools, fixed addresses other than the storage are `[RED]` | `nvn2_pool.c`, `hle_nvn.c` |
| Buffers, events | NVN1 implementations; builder device, debug ID 0, event storage getters | `nvn2_pool.c` |
| Queue builder and queues | NVN2 builder layout (32-bit sizes and their "set" flags, primary queue, ray-tracing size, priority 100); queue memory = control + command size; every queue kind (flags 0x4 / 0x8 / 0x10) replays on the host's one Metal queue in submission order, so primary/subcontext queues need nothing more; usage counters 0; NVX timeout / timeslice accepted | `nvn2_sync.c` |
| Syncs | Metal fence serials as for NVN1; a sync never fenced waits as FAILED and `QueueWaitSync` returns 0 for it (sdk+0x5c8310, sdk+0x5c8850); GL interop answers "no interop" (4); the NVX syncpoint functions are `[RED]` and false (the host has no NvRm syncpoints) | `nvn2_sync.c`, `hle_nvn.c` |
| Command handles | the host's handles are 8-byte-aligned pointers to its first record, which is NVN2's tag 0; SubmitCommands / CallCommands / CopyCommands drop a handle with tag 1–3 with one `[RED]` (only main's `nvnPrivate0`-gated code could build one) | `nvn2_sync.c` |
| Windows | `SetNumActiveTextures` is honoured: acquire cycles through the first n textures (MKW: 3 built, 2 or 3 active) | `hle_nvn.c` |

Verified on MKW (headless Mac, `$W/run/lockrun.sh`): `[nvn] NVN2: 121 Switch 2 implementations`, `nvnPrivate0 is 0`, `device API 231.16 (NVN2)`; main then creates its device, five queues (one with flags 9, its secondary queues with `SetPrimaryQueue`), its window (2560×1440, 3 textures), 3,165 memory pools in 40 s (26 GB of pool address space, 21.9 GB of it in 42 virtual pools with flags 0x2a1), records and submits command buffers and presents about 200 frames, with no `[RED]` from the core functions. The pools themselves wrap about 4.1 GB of storage and 1–2.6 GB of mapped virtual runs in Metal buffers. What stops the run is outside the core: Metal reports "Insufficient Memory" on upload command buffers around frame 150–200, because textures in the sparse 0x2a1 pools are uploaded whole (single MTLTextures of 389 MB, 1,451 MB and 5,803 MB, each with a staging buffer and a CPU gather copy of the same size; open, textures).

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

## Object sizes at a glance

What MKW allocates for each NVN2 object (the reserved size its SDK headers give), what the driver itself touches, and NVN1's reserved size in `nvn.h`. Every host record in `nvn_obj.h` still fits; the details and evidence are in each object's section.

| Object | NVN1 (`nvn.h`) | NVN2: MKW allocates | NVN2: driver touches |
|---|---|---|---|
| NVNdevice | 0x3000 | 0x3000 | 0x22c8 |
| NVNdeviceBuilder | 0x40 | stack | 0x18 |
| NVNqueue | 0x2000 | **0x3000** | 0x2cc0 |
| NVNqueueBuilder | 0x40 | ≤ 0x50 (stack) | 0x48 |
| NVNcommandBuffer | 0xA0 | **0xC0** | 0xB0 |
| NVNmemoryPool | 0x100 | 0x100 | 0x100 |
| NVNmemoryPoolBuilder | 0x40 | ≤ 0x40 (stack) | 0x38 |
| NVNbuffer | 0x30 | 0x30 | 0x30 |
| NVNbufferBuilder | 0x40 | stack | 0x28 |
| NVNtexture | 0xC0 | **0x80** | 0x80 |
| NVNtextureBuilder | 0x80 | 0x80 | 0x68 |
| NVNtextureView | 0x28 | **0x10** | 0x10 |
| NVNtexturePool / NVNsamplerPool | 0x20 | 0x20 | 0x20 |
| NVNsampler | 0x30 | **0x40** | 0x40 |
| NVNsamplerBuilder | 0x60 | **0x40** | 0x30 |
| NVNprogram | 0xC0 | **0xA0** | 0xA0 |
| NVNprogramBuilder | (new) | 0x80 | 0x64 |
| NVNwindow | 0x180 | **0x280** | 0x218 |
| NVNwindowBuilder | 0x40 | **0x80** | 0x60 |
| NVNsync | 0x40 | 0x40 | 0x38 |
| NVNevent | 0x40 | 0x40 | 0x28 |
| NVNeventBuilder | 0x20 | stack | 0x10 |
| State objects (blend 8, channel mask 4, color 4, depth-stencil 8, multisample 0x18, polygon 4, vertex attrib 4, vertex stream 8) | same | same | same |
| NVNdlssNetworkBuilder / NVNdlssNetwork / NVNdlssParameters | (new) | not used | 0xC0 / 0x218 / 0xC8 |
| NVNdeviceCommandBuffer / NVNdeviceMessageBuffer | (new) | not used | 0x30 / 0x28 |

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
| +0x8 | device flags (bits 9 and 10 / mask 0x600 start optional driver layers; bit 10 loads the separate "dlss" module, see [Dlss](#dlss)) | sdk+0x5b54e0, sdk+0x5b0c50 |
| +0x10..+0x11c | GPU description copied from `NvRmGpuDeviceGetInfo` (sdk+0x5bf090): +0x54 / +0x58 GPU architecture and implementation (0x170 / 0xe: every MKW control section carries these at +0xc / +0xe, which must match), +0x80 / +0x84 timestamp ns numerator / denominator, +0xb0 / +0xb4 / +0xbc scratch-memory factors, +0xd8 memory-pool page size (the GPU's value, at least 64 KiB; item 0x3a) | sdk+0x5bf090, sdk+0x5b57c0 |
| +0x968 / +0x970 / +0x980 | array and count of initialized memory pools, and their mutex: the driver turns a GPU address into a CPU pointer by searching it | sdk+0x5bf8b0 |
| +0x9a0 / +0x9a8 / +0x9b0 | list and mutex of programs | sdk+0x5bf2c0 |
| +0x9d0 | NvRm GPU context (device, address space, channel) | sdk+0x5b4450 |
| +0x1df0 | ray-tracing backend object (vtable) | sdk+0x5d29c0 |

### Device information

Every item not computed from the device comes from a static table (sdk+0x59bf50); items it does not list answer 0. Bold values differ from the NVN1 table the host reports today. Items 0x2e–0x31 are the accepted shader GPU-code versions: major 201, minor 0–11 — the major version in a control section's +0x4, the minor its +0x8 ([Program and ProgramBuilder](#program-and-programbuilder)) — not GLSLC's 1.x, so the host's GLSLC-based override of 0x2e / 0x30 must not apply to a Switch 2 game. Device-dependent items come from the GPU description (values depend on the NvRm answer; the host chooses them).

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

## Resources: memory pools, buffers, textures, samplers and state objects

Device-object offsets (`device+…`) refer to the driver's NVNdevice (zeroed over 0x22c8 bytes at sdk+0x5b447c; main allocates 0x3000).

### Summary of changes versus NVN1 (nvn.h / nvn_obj.h)

| object | NVN1 reserved (nvn.h) | NVN2 size | evidence | host struct (nvn_obj.h / hle_nvn.c) fits? |
|---|---|---|---|---|
| NVNmemoryPoolBuilder | 0x40 | driver uses 0x38 (SetDefaults writes +0x4..+0x38); game reserves ≤0x40 | sdk+0x5bcdb0; main+0x2480f0 frame (builder at sp, saved regs at sp+0x40) | pool_builder_t 0x20: yes |
| NVNmemoryPool | 0x100 | **0x100** | Initialize zeroes 32 qwords (sdk+0x5bcef0); main embeds it at wrapper+0x40..+0x140 (main+0x2482c8, wrapper 0x190 bytes) | pool_t 0x28: yes |
| NVNexternalMemoryPoolBuilder | — (new) | ≥0xc, [INFERENCE] 0x10 | sdk+0x612e30..0x612e70 touch +0x0 (u64) and +0x8 (u32) | n/a |
| NVNbufferBuilder | 0x40 | driver uses 0x28 | sdk+0x59e180/0x59e190 | buf_builder_t 0x20: yes |
| NVNbuffer | 0x30 | **0x30** | Initialize zeroes +0..+0x30 (sdk+0x59e1e0) | buf_t 0x28: yes (but its field offsets differ from the driver's, irrelevant unless the game reads fields) |
| NVNtextureBuilder | 0x80 | driver uses 0x68; game reserves **0x80** | getters/setters up to +0x60 (sdk+0x5c8c60..0x5c9500); main iterates builders with stride 0x80 (main+0x2625f0, main+0xe2b9fc) | tex_builder_t: asserted ≤0x80, yes |
| NVNtexture | 0xC0 | **0x80** | Initialize zeroes 16 qwords (sdk+0x5ca240); main memsets 0x80 before TextureInitialize (main+0x130388..0x130394) | tex_t ≤0x80 (already asserted): yes |
| NVNtextureView | 0x28 | **0x10** | SetDefaults zeroes 16 bytes (sdk+0x5c9560); all fields within 0..0xf | view_t 16: yes |
| NVNtexturePool / NVNsamplerPool | 0x20 / 0x20 | **0x20 / 0x20** | Initialize writes +0..+0x20 (sdk+0x5cad10, sdk+0x5c6690); main places them 0x20 apart (obj+0xd0, obj+0xf0, sampler at obj+0x110; main+0xcee088..0xcee130) | dpool_t 0x18: yes |
| NVNsamplerBuilder | 0x60 | driver uses 0x30; game reserves **0x40** [INFERENCE] | fields up to +0x2c (sdk+0x5c6220); main zeroes exactly 0x40 bytes of a stack builder before SetDevice (main+0x1675080..0x16750a8) | samp_builder_t 0x30: yes |
| NVNsampler | 0x30 | **0x40 (grew)** | Initialize zeroes 0x40 bytes and copies the builder to +0x8..+0x38 (sdk+0x5c6500); main zeroes 0x40 bytes before SamplerInitialize (main+0x1c5890..0x1c58ac) | samp_t 0x2c: yes |
| NVNblendState | 0x8 | 0x8 | SetDefaults writes one u64 (sdk+0x5c69a0) | blend_st 8: yes |
| NVNchannelMaskState | 0x4 | 0x4 | sdk+0x5c6b80 | yes |
| NVNcolorState | 0x4 | 0x4 | sdk+0x5c6c10 | yes |
| NVNdepthStencilState | 0x8 | 0x8 | sdk+0x5c6da0 | yes |
| NVNmultisampleState | 0x18 | 0x18 | SetDefaults writes +0..+0x18 (sdk+0x5c7050) | ms_st 10 bytes: yes (needs one more bit for the new coverage-truncation flag) |
| NVNpolygonState | 0x4 | 0x4 | sdk+0x5c6f80 | yes |
| NVNvertexAttribState | 0x4 | 0x4 | sdk+0x5c6cd0 | yes |
| NVNvertexStreamState | 0x8 | 0x8 | sdk+0x5c6d50 | yes |

Function-level ABI: none of the 263 functions of these families (all present in the NVN2 GetProcAddress table) changed its argument count or argument types. Exactly two names are new: `nvnMultisampleStateSetCoverageTruncationEnable` / `GetCoverageTruncationEnable`. Behaviour changes that matter:
- `nvnSamplerInitialize` writes **0x40** bytes (NVN1: 0x30). The host only writes its own 0x2c-byte record, so nothing breaks, but code that sizes samplers from nvn.h must use 0x40.
- `nvnTextureBuilderSetPackagedTextureLayout` reads only the first **2 bytes** (12 bits) of the 8-byte layout.
- Strides are 32-bit: `nvnTextureBuilderSetStride`/`GetStride`, `nvnTextureGetStride`, `nvnVertexStreamStateSetStride`/`GetStride` store and return 32 bits (`str w1` / `ldr w0`). The register ABI is unchanged, but the top 32 bits are dropped.
- Flush/Invalidate of memory pools, buffers and texels are **no-ops** (sdk+0x5bd3c0 / 0x5bd3c4 are bare `ret`). `MemoryPoolFlushMappedRange` is MKW's most-called function of this family after GetFlags (92 call sites).
- The texture flags kept by the texture are 16 bits wide. Two flag bits that NVN1 does not know are used: 0x800 and 0x1000 (see Textures).
- The format enum adds 6 formats, 0x9d–0xa2 (format table has 0xa3 entries, see Textures).
- Debug labels / debug IDs are stubs: every `*SetDebugLabel` is `ret`, every `*GetDebugID` returns 0.

## Memory model (MemoryPool / MemoryPoolBuilder / NVX / ExternalMemoryPoolBuilder)

### MemoryPoolBuilder layout (0x38 used, 0x40 reserved)

| offset | type | meaning | evidence |
|---|---|---|---|
| +0x0 | u32 | unused (SetDefaults never writes it) | sdk+0x5bcdb0 |
| +0x4 | u32 | flags (default 0x21 = CPU_NO_ACCESS\|GPU_CACHED) | SetDefaults/SetFlags/GetFlags sdk+0x5bcdb0/0x5bcea0/0x5bcee0 |
| +0x8 | NVNdevice* | device | sdk+0x5bcda0, 0x5bceb0 |
| +0x10 | void* | storage (CPU memory) | SetStorage sdk+0x5bcdd0 |
| +0x18 | u64 | size | SetStorage |
| +0x20 | u32 | NvRmMem handle to import (NVX); SetStorage clears it, SetNativeHandleNVX clears +0x10/+0x18 | sdk+0x5bcdd0, sdk+0x5bcde0 |
| +0x28 | u64 | NVX "generic address" (fixed GPU VA); in reserved-list mode (flag 0x20000000): u32 count | sdk+0x5bce60; physical init sdk+0x5ccd38 |
| +0x30 | u64 | NVX "special address" (fixed GPU VA of the compressible mapping); in reserved-list mode: pointer to an array of {u32 type; u64 gpuVa; u64 size} (0x18 bytes each) | sdk+0x5bce80; sdk+0x5ccd38 |

### MemoryPool layout (0x100, zeroed by Initialize)

| offset | type | meaning | evidence |
|---|---|---|---|
| +0x0 | u32 | implementation kind: 0 = normal/physical, 1 = virtual (flags VIRTUAL 0x200) | ctor sdk+0x5bd348 called by sdk+0x5cccf4 (0) / sdk+0x5cd518 (1); dispatchers sdk+0x5bd370/0x5bd3ac/0x5bd3c8/0x5bd3ec |
| +0x8 | NVNdevice* | device | sdk+0x5bd348 |
| +0x10 | u32 | normalised flags; bits 22–31 are driver-internal, hidden by GetFlags (& 0x3fffff) | sdk+0x5bd330 |
| +0x18 | u64 | **generic GPU VA** (GetBufferAddress, GetGenericAddressNVX); for a virtual pool the VA-range base | sdk+0x5bd2a0, sdk+0x5cd254, sdk+0x5cd560 |
| +0x20 | u64 | second generic-kind GPU VA, only when DeviceBuilder flag 0x10000 (device byte +0xa bit 0) is set or a reserved-list entry of type 5 exists; used instead of +0x18 for block-linear generic-kind textures | sdk+0x5cd254, sdk+0x5cca2c |
| +0x28 | u64 | **special (compressible-kind) GPU VA**, only for COMPRESSIBLE pools (GetSpecialAddressNVX) | sdk+0x5cd254 (`flags byte < 0` branch), sdk+0x5bce90 |
| +0x30 | void* | CPU address (= storage; or = +0x18 when the pool was imported from a handle) | sdk+0x5ccd38, sdk+0x5cd254 |
| +0x38 | u64 | size | sdk+0x5bd320 |
| +0x40..+0x57 | | zeroed by ctor, not used by the functions examined | sdk+0x5bd348 |
| +0x58 | u32 | NvRmMem handle (GetNativeHandleNVX) | sdk+0x5bd340, sdk+0x5ccd38 |
| +0x60 | ptr | normal: NvRmGpuMapping of +0x18; virtual: NvRmGpuAddressSpaceAllocation | sdk+0x5cd498, sdk+0x5cd560 |
| +0x68 | ptr / u32,u32 | normal: mapping of +0x20; virtual: u32 page size (device+0xd4), u32 log2(page size) at +0x6c | same |
| +0x70 | ptr / u32 | normal: mapping of +0x28; virtual: u32 remap batch size = min(GPU info +0xc0, 0x200) | same |
| +0x74 | u8 | virtual: owns the VA allocation (0 when placed in a device reserved range) | sdk+0x5cd518, sdk+0x5cd560 |
| +0x78 | u8 | normal: owns the NvRmMem handle (0 when imported) | sdk+0x5cd254, sdk+0x5cd498 |

### Flags

User-visible flags are the NVN1 values (`NVN_MEMORY_POOL_FLAGS_*`: CPU_NO_ACCESS 0x1, CPU_UNCACHED 0x2, CPU_CACHED 0x4, GPU_NO_ACCESS 0x8, GPU_UNCACHED 0x10, GPU_CACHED 0x20, SHADER_CODE 0x40, COMPRESSIBLE 0x80, PHYSICAL 0x100, VIRTUAL 0x200). Initialize (sdk+0x5bcef0) normalises bits 0–5 and keeps every bit from 6 up unchanged:
- CPU access: UNCACHED if bit 0x2; else CACHED if bit 0x4; else NO_ACCESS if bit 0x1; else UNCACHED. If the result is UNCACHED and DeviceBuilder flag 0x8000 (device byte +0x9 bit 7) is set, CACHED is used instead. VIRTUAL forces CPU_NO_ACCESS.
- GPU access: CACHED if 0x20; else UNCACHED if 0x10; else NO_ACCESS if 0x8; else CACHED. PHYSICAL forces GPU_NO_ACCESS.
- GPU_CACHED becomes the "cacheable" attribute of every GPU mapping (`flags >> 5 & 1`, sdk+0x5ccbc0). CPU_UNCACHED selects NvRm coherency attribute 2, everything else 1 (sdk+0x5ccd38).
- Bits above 0x3fffff are driver-internal (GetFlags masks them): **0x8000000** = a compressible texture has re-kinded part of the special mapping (set at sdk+0x5cca2c; E8 saw pool flags 0x080000a1); **0x10000000** = driver-owned pool (the device's internal 0x2000-byte pool, sdk+0x5b54e0); **0x20000000** = reserved-address-list mode (set on success at sdk+0x5ccd38 when the builder flags had it).
- 0x400: used only by the driver's internal pool (flags 0x412 = CPU_UNCACHED\|GPU_UNCACHED\|0x400, sdk+0x5b54e0). No test of it was found in the pool code [open].
- MKW's constant flags (SetFlags call sites): 0x21, 0x22 (texture/sampler descriptor memory, main+0xcee030), 0x24, 0x62, 0x64 (SHADER_CODE pools, main+0x9c5dac/0x9a7e10), **0xa1** (COMPRESSIBLE render-target pool, main+0x226dec4). Other sites pass a value computed by the game, masked with 0x0fffffff (main+0x248120).

### How GPU addresses are assigned (answer: GPU VA == CPU VA for storage-backed pools)

1. At device creation (sdk+0x5b44ac → sdk+0x5be8a8 → sdk+0x5be960) the driver opens the NvRm GPU, creates an address space covering VA [0x8000000, 0xC000000000) (constants at sdk data 0xac4f40). Unless DeviceBuilder flags bit 31 is set, it also **reserves a fixed allocation over [0x8000000, 0x8000000000)** (0x7ff8000000 / page-size pages, fixed address 0x8000000, constants at 0xac65c0), which is the whole 39-bit application CPU range. The allocation is stored at device+0x9f0. `nvnDeviceGetInteger(0x9a)` returns whether it exists (sdk+0x5b57c0). MKW passes DeviceBuilder flags 0 (main+0xcf3aa4), so the allocation exists. This matches E2 (AllocAsEx va_range 0x8000000..0xC000000000, "NVN then reserves 4 KiB VA fixed up to 512 GiB").
2. Normal pool with storage (sdk+0x5ccd38 → sdk+0x5cd254 → sdk+0x5ccbc0): storage must be non-NULL and 4 KiB-aligned, size non-zero and a multiple of 4 KiB. PHYSICAL pools also need the size to be a multiple of device+0xd8 (= MEMORY_POOL_PAGE_SIZE, `GetInteger(0x3a)` returns device+0xd8). The storage is wrapped into an NvRmMem handle (`NvRmMemHandleAllocAttr`), then, unless PHYSICAL:
   - the generic mapping is created **fixed at the CPU address inside the device+0x9f0 allocation** (`NvRmGpuMappingCreateFixed`) → **pool+0x18 == storage address**. Without that allocation it is placed freely (`NvRmGpuMappingCreate`), and GPU VA ≠ CPU VA.
   - DeviceBuilder flag 0x10000 only: a second generic mapping at a free VA → +0x20.
   - COMPRESSIBLE only: a second mapping with a compressible PTE kind at a **free GPU-only VA** (or at the NVX special address) → +0x28. E8 observed such a VA at 0x815f0d0000, above the CPU range.
   - The size is rounded up to device+0xd4 (the big page) for the mappings.
   - PHYSICAL pools get no GPU mapping at all: GetBufferAddress = 0.
3. Pool from an NvRm handle (NVX SetNativeHandleNVX; memory = NULL, size = 0): the size comes from `NvRmMemGetSize`, the generic mapping is placed freely, and the CPU address is set to the GPU address and mapped there with `NvRmMemMap(handle, 0, size, 0x43)` (sdk+0x5cd254), so again CPU VA == GPU VA. The handle is not owned (not freed at Finalize).
4. Virtual pool (VIRTUAL 0x200, sdk+0x5cd560): storage must be NULL, size non-zero and a multiple of device+0xd8. A GPU VA range of size >> log2(device+0xd4) pages is allocated (`NvRmGpuAddressSpaceAllocationCreate`, attribute word 1 = [INFERENCE] sparse) → +0x18. With device reserved ranges / NVX addresses it is placed at builder+0x28 (or the first reserved-list entry). The VA is GPU-only: E3/E8 saw gpu-214 sparse regions at 0x81_xxxx_xxxx. `Map` returns NULL (CPU_NO_ACCESS forced).
5. Every pool is appended to the device pool list (device+0x968 pointer array, +0x970 count, +0x974 capacity 0x20, +0x978 grow step 0x20, +0x97c flags, +0x980 mutex; sdk+0x5bcef0, sdk+0x5bc310). The driver walks this list to translate GPU addresses to CPU pointers. Finalize removes the pool keeping the order (sdk+0x5bd190).

**Consequence for nxnative:** keeping "GPU address = CPU address of the pool storage" (hle_nvn.c) matches the real driver for every storage-backed, non-virtual pool and for buffers (BufferGetAddress = pool+0x18 + offset). It does **not** hold for (a) virtual pools: their VAs lie outside the CPU range, so the host must hand out non-overlapping fake VAs (e.g. ≥ 0x8000000000) and keep them out of the CPU-pointer path; (b) compressible-kind textures: their texture address is pool+0x28 + offset, a second VA of the same memory. The host never needs to expose that one, because nvnTextureGetTextureAddress has no MKW call site.

### Alignment and granularity

| quantity | value / source |
|---|---|
| storage alignment / size granularity (normal pool) | 4 KiB / 4 KiB (`& 0xfff` checks, sdk+0x5ccd38) |
| PHYSICAL and VIRTUAL size granularity, MapVirtual offset/size alignment | device+0xd8 = `GetInteger(NVN_DEVICE_INFO_MEMORY_POOL_PAGE_SIZE 0x3a)`, copied from NvRm GPU info +0x2c4 at device init (sdk+0x5bf090, which also copies info +0x2c0 → device+0xd4); the static default table gives 0x10000 (sdk+0x59bf50) [the runtime value was not observed] |
| mapping granularity / big page | device+0xd4 (GPU info +0x2c0); virtual-pool remap unit; compressible-kind texture offset and size must be multiples of it |
| texture storage alignment | see Textures |

### MapVirtual and NVNmappingRequest

NVNmappingRequest (0x28 bytes, same as NVN1's documented layout), from sdk+0x5cd730 and MKW's request at main+0x521640..0x521648 and main+0x5b5898..0x5b58a4:

| offset | type | meaning |
|---|---|---|
| +0x0 | NVNmemoryPool* | physical pool (NULL = unmap the range) |
| +0x8 | s64 | physical offset |
| +0x10 | s64 | virtual offset (relative to the virtual pool's VA) |
| +0x18 | u64 | size |
| +0x20 | u32 | storage class: 0x100 = the device generic kind (device+0x1da0); otherwise `value & ~0x300` = PTE kind, and bit 0x200 is passed as an extra per-entry flag |

Semantics (sdk+0x5bd2b0 → sdk+0x5cd730): global mutex; fails unless the pool is virtual; physical offset, virtual offset and size must all be multiples of device+0xd8, else false. Each request becomes one remap entry {handle (pool+0x58), kind, physical page = physOffset >> shift, virtual page = (poolVA + virtOffset) >> shift, page count, cacheable = virtual pool GPU_CACHED, flag 0x200}. A NULL pool gives an entry that only sets virtual page and count (unmapped). Entries go to `NvRmGpuAddressSpaceAllocationRemap` in batches of pool+0x70. Returns true when every batch succeeds. Finalize remaps the whole pool to unmapped. MKW builds the storage class from a table filled with `nvnTextureBuilderGetStorageClass` (call sites main+0x8f6290/0x8f630c/0x8f63b0) and uses MapVirtual at 13 sites (sparse texture arrays, cf. E8 "sparse regions").

### FlushMappedRange / InvalidateMappedRange

Both are **no-ops** in NVN2 (sdk+0x5bd280 → `ret`, sdk+0x5bd290 → `ret`). This holds for pools, buffers (sdk+0x59e2b0/0x59e2c0), texels (sdk+0x5cb860/0x5cb8f0) and the descriptor flushes in TexturePool/SamplerPool. Like the host, the driver assumes coherent CPU/GPU memory.

### NVX extensions and ExternalMemoryPoolBuilder

The NVX functions are not in the 698-name sorted table. `nvnDeviceGetProcAddress` reaches them through a strcmp fallback chain (sdk+0x5b5b40, [The driver's lookup](#the-drivers-lookup)). MKW's main references neither the NVX nor the External names (no strings, no imports).

| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnMemoryPoolBuilderSetNativeHandleNVX` | sdk+0x5bcde0 | 0 | new (GetProcAddress fallback list sdk+0x5b5b40) | (builder, uint32_t nvrmHandle): +0x20 = handle and clears memory/size (+0x10/+0x18) → Initialize imports the handle |
| `nvnMemoryPoolBuilderGetNativeHandleNVX` | sdk+0x5bcdf0 | 0 | new | returns u32 builder+0x20 |
| `nvnMemoryPoolGetNativeHandleNVX` | sdk+0x5bd340 | 0 | new | returns u32 pool+0x58 (the NvRmMem handle of the pool, owned or imported) |
| `nvnMemoryPoolBuilderGetReservedAddressesNVX` | sdk+0x5bce40 | 0 | new | (builder, uint64_t *generic, uint64_t *special): *generic = +0x28, *special = +0x30 |
| `nvnMemoryPoolBuilderSetGenericAddressNVX` | sdk+0x5bce60 | 0 | new | builder+0x28 = fixed GPU VA for the generic mapping (used only when the device has reserved ranges, DeviceBuilderSetReservedAddressRangesNVX); see reserved-list mode |
| `nvnMemoryPoolGetGenericAddressNVX` | sdk+0x5bce70 | 0 | new | returns pool+0x18 (same as GetBufferAddress) |
| `nvnMemoryPoolBuilderSetSpecialAddressNVX` | sdk+0x5bce80 | 0 | new | builder+0x30 = fixed GPU VA for the compressible ("special") mapping |
| `nvnMemoryPoolGetSpecialAddressNVX` | sdk+0x5bce90 | 0 | new | returns pool+0x28 (compressible-kind GPU VA, 0 if the pool is not COMPRESSIBLE) |

Reserved-list mode [partly INFERENCE]: when builder flags bit 29 (0x20000000) is set, physical Initialize reads builder+0x28 as a u32 count and builder+0x30 as an array of {u32 type; u64 gpuVa; u64 size}. Type 1 creates the generic mapping fixed at gpuVa (→ +0x18), type 2 the compressible mapping (→ +0x28), type 5 the second generic mapping (→ +0x20); any other type fails. Each gpuVa must lie inside one of the device's reserved ranges (lookup sdk+0x5bdde0 over device+0x1d98). No public setter for the list exists other than reusing the Generic/Special address setters [INFERENCE: the C++ header provides an inline wrapper].

ExternalMemoryPoolBuilder: plain C exports of the sdk (not via GetProcAddress), with C++ wrappers `nvn::ExternalMemoryPoolBuilder::*` at sdk+0x613794..0x613810 that call through PLT stubs. Layout: +0x0 NVNdevice*, +0x8 u32 `nn::gs::NativeMemoryHandle`.
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnExternalMemoryPoolBuilderSetDefaults` | sdk+0x612e30 | 0 | new (plain sdk export, not in GetProcAddress) | +0x8 = 0 (handle); device untouched |
| `nvnExternalMemoryPoolBuilderSetDevice` | sdk+0x612e40 | 0 | new | +0x0 = device |
| `nvnExternalMemoryPoolBuilderSetNativeMemoryHandle` | sdk+0x612e50 | 0 | new | (builder, nn::gs::NativeMemoryHandle (u32)) → +0x8 |
| `nvnExternalMemoryPoolBuilderGetDevice` | sdk+0x612e60 | 0 | new | returns +0x0 |
| `nvnExternalMemoryPoolBuilderGetNativeMemoryHandle` | sdk+0x612e70 | 0 | new | returns u32 +0x8 |

No sdk function consumes an ExternalMemoryPoolBuilder: there is no `nvnMemoryPoolInitialize*External*` export, string or GetProcAddress entry. How a native memory handle turns into a pool is open. The likely route is NvRmMem id → `SetNativeHandleNVX` [INFERENCE] (cf. `nn::gs::sdk::QueryNativeMemoryNvsNvRmMemIdForDebug`, `NvRmMemGetNativeMemoryHandle` sdk+0x556490).

### Function table: MemoryPoolBuilder
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnMemoryPoolBuilderSetStorage` | sdk+0x5bcdd0 | 18 | same | builder+0x10 = memory, +0x18 = size, and clears the NVX native handle (+0x20 = 0) |
| `nvnMemoryPoolBuilderSetFlags` | sdk+0x5bcea0 | 17 | same | builder+0x4 = flags (u32, stored verbatim; normalisation happens in Initialize) |
| `nvnMemoryPoolBuilderSetDefaults` | sdk+0x5bcdb0 | 15 | same | memory/size/native handle/generic/special address = 0 (+0x10..+0x38); flags (+0x4) = 0x21 (CPU_NO_ACCESS\|GPU_CACHED); device (+0x8) untouched |
| `nvnMemoryPoolBuilderSetDevice` | sdk+0x5bcda0 | 10 | same | builder+0x8 = device |
| `nvnMemoryPoolBuilderGetDevice` | sdk+0x5bceb0 | 0 | same (no NVN1 prototype) | returns builder+0x8 |
| `nvnMemoryPoolBuilderGetFlags` | sdk+0x5bcee0 | 0 | same | returns builder+0x4 (unnormalised) |
| `nvnMemoryPoolBuilderGetMemory` | sdk+0x5bcec0 | 0 | same (nvn.h declares a void return by mistake; it returns void*) | returns builder+0x10 |
| `nvnMemoryPoolBuilderGetSize` | sdk+0x5bced0 | 0 | same | returns builder+0x18 |

### Function table: MemoryPool
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnMemoryPoolGetFlags` | sdk+0x5bd330 | 198 | same | returns pool+0x10 & 0x3fffff (driver-internal bits 22..31 hidden) |
| `nvnMemoryPoolFlushMappedRange` | sdk+0x5bd280 | 92 | same | **no-op** (tail-calls an empty function sdk+0x5bd3c0) |
| `nvnMemoryPoolInitialize` | sdk+0x5bcef0 | 16 | same | zeroes 0x100 bytes, normalises flags, creates the NvRm memory handle and GPU mappings, adds the pool to the device pool list (device+0x968); returns false (and finalizes) on failure. See memory model |
| `nvnMemoryPoolFinalize` | sdk+0x5bd190 | 15 | same | removes the pool from the device pool list, calls the interception hook (sdk+0x5cc960), then unmaps (virtual: remaps the whole range to unmapped and closes the VA allocation; normal: closes the 3 mappings, frees the NvRm handle if owned) |
| `nvnMemoryPoolMapVirtual` | sdk+0x5bd2b0 | 13 | same (NVNmappingRequest layout = 0x28 bytes, see below) | under a global mutex (pointer at sdk+0xd7b040); only virtual pools (pool+0 == 1), else returns false; remaps each request through NvRmGpuAddressSpaceAllocationRemap in batches |
| `nvnMemoryPoolMap` | sdk+0x5bd260 | 6 | same | returns pool+0x30 (CPU address) unless the normalised flags have CPU_NO_ACCESS (bit 0) → NULL |
| `nvnMemoryPoolGetBufferAddress` | sdk+0x5bd2a0 | 4 | same | returns pool+0x18 (generic GPU VA) |
| `nvnMemoryPoolInvalidateMappedRange` | sdk+0x5bd290 | 4 | same | **no-op** (tail-calls empty sdk+0x5bd3c4) |
| `nvnMemoryPoolGetSize` | sdk+0x5bd320 | 1 | same | returns pool+0x38 |
| `nvnMemoryPoolSetDebugLabel` | sdk+0x5b0b30 | 1 | same | **no-op** (ret) |

### MKW call sequences (memory pools)
- Generic pool (main+0x248114..0x2481a8, also main+0x2482f0, main+0x248444): `MemoryPoolBuilderSetDefaults(sp)` → `SetDevice(sp, dev)` → `SetFlags(sp, flags & 0x0fffffff)` → `SetStorage(sp, mem, size)` → `MemoryPoolInitialize(wrapper+0x40, sp)` (the wrapper is 0x190 bytes) → `MemoryPoolMap`.
- Descriptor memory (main+0xcedfd8..0xcee0c8): size = round_up(texCount × texDescSize + sampCount × sampDescSize, 4 KiB); flags 0x22; storage from a 4 KiB-aligned allocator (main+0x5527b0); then SamplerPoolInitialize(obj+0xf0, pool, 0, sampCount) and TexturePoolInitialize(obj+0xd0, pool, sampCount × descSize, texCount).
- Compressible render targets (main+0x226de94..0x226df74): size from TextureBuilderGetStorageSize/GetStorageAlignment, flags 0xa1, 4 KiB-aligned storage, `MemoryPoolInitialize`, then `TextureBuilderSetStorage(pool, 0)` and `TextureInitialize` at aligned offsets.
- MapVirtual (main+0x52164c): one request {pool NULL, 0, virtual offset 0, size, storage class from a game table}. Unmap/reset of a sparse range. main+0x5b58a8: {physical pool, physical offset, virtual offset, size, class}.

## Buffers

### BufferBuilder (0x28 used; NVN1 reserves 0x40)
| offset | type | meaning | evidence |
|---|---|---|---|
| +0x8 | NVNdevice* | device | sdk+0x59e170 |
| +0x10 | NVNmemoryPool* | pool | sdk+0x59e190 |
| +0x18 | s64 | offset | sdk+0x59e190 |
| +0x20 | u64 | size | sdk+0x59e190 |

### Buffer (0x30)
| offset | type | meaning | evidence |
|---|---|---|---|
| +0x0 | — | zero | sdk+0x59e1e0 |
| +0x8 | NVNdevice* | device | sdk+0x59e1e0 |
| +0x10 | NVNmemoryPool* | pool | sdk+0x59e2d0 |
| +0x18 | s64 | offset | sdk+0x59e2e0 |
| +0x20 | u64 | size | sdk+0x59e2f0 |
| +0x28 | — | zero | |

GetAddress = pool+0x18 + offset; Map = pool+0x30 + offset unless the pool is CPU_NO_ACCESS or has no CPU address. A buffer cannot be built on a PHYSICAL pool. The driver creates no GPU object for a buffer (Finalize is `ret`).

### Function table: BufferBuilder
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnBufferBuilderSetStorage` | sdk+0x59e190 | 30 | same | +0x10 pool, +0x18 offset, +0x20 size |
| `nvnBufferBuilderSetDefaults` | sdk+0x59e180 | 29 | same | zeroes +0x10..+0x28 (pool, offset, size); device untouched |
| `nvnBufferBuilderSetDevice` | sdk+0x59e170 | 20 | same | builder+0x8 = device |
| `nvnBufferBuilderGetDevice` | sdk+0x59e1a0 | 0 | same (no NVN1 prototype) | returns +0x8 |
| `nvnBufferBuilderGetMemoryOffset` | sdk+0x59e1c0 | 0 | same | returns +0x18 |
| `nvnBufferBuilderGetMemoryPool` | sdk+0x59e1b0 | 0 | same (nvn.h says it returns NVNmemoryPool by value; it returns NVNmemoryPool*) | returns +0x10 |
| `nvnBufferBuilderGetSize` | sdk+0x59e1d0 | 0 | same | returns +0x20 |

### Function table: Buffer
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnBufferGetAddress` | sdk+0x59e2a0 | 147 | same | pool+0x18 (generic GPU VA) + offset |
| `nvnBufferInitialize` | sdk+0x59e1e0 | 28 | same | zeroes 0x30 bytes; fails (returns false) if the pool is PHYSICAL (pool flags bit 8); copies device/pool/offset/size to +0x8/+0x10/+0x18/+0x20 |
| `nvnBufferFinalize` | sdk+0x59e230 | 22 | same | **no-op** (ret) |
| `nvnBufferMap` | sdk+0x59e270 | 5 | same | pool CPU address (pool+0x30) + offset, or NULL if the pool is CPU_NO_ACCESS or has no CPU address |
| `nvnBufferFlushMappedRange` | sdk+0x59e2b0 | 3 | same | no-op (pool flush with offset+buffer offset) |
| `nvnBufferInvalidateMappedRange` | sdk+0x59e2c0 | 2 | same | no-op |
| `nvnBufferSetDebugLabel` | sdk+0x5b0b00 | 1 | same | **no-op** |
| `nvnBufferGetDebugID` | sdk+0x59e300 | 0 | same | **stub: returns 0** |
| `nvnBufferGetMemoryOffset` | sdk+0x59e2e0 | 0 | same | returns +0x18 |
| `nvnBufferGetMemoryPool` | sdk+0x59e2d0 | 0 | same | returns +0x10 |
| `nvnBufferGetSize` | sdk+0x59e2f0 | 0 | same | returns +0x20 |

## Textures

### TextureBuilder (0x68 used, 0x80 reserved by the game)
| offset | type | meaning (default) | evidence |
|---|---|---|---|
| +0x0 | — | unused | |
| +0x8 | NVNdevice* | device | sdk+0x5c8c50 |
| +0x10 | NVNmemoryPool* | pool (NULL) | SetStorage sdk+0x5c8d70 |
| +0x18 | s64 | offset (0); the texture keeps 48 bits | sdk+0x5c8d70, sdk+0x5cb990 |
| +0x20 | u32 | flags (0) | sdk+0x5c8ca0 |
| +0x24 | u32 | target (1 = 2D) | sdk+0x5c8cb0; default constant sdk data 0xac67b0 |
| +0x28 / +0x2c / +0x30 | s32 | width / height / depth (0 / 0 / 0) | sdk+0x5c8cc0..0x5c8d10 |
| +0x34 | s32 | levels (1) | sdk+0x5c8d20; default 0xac4740 |
| +0x38 | s32 | format (0 = NONE) | sdk+0x5c8d30 |
| +0x3c | s32 | samples (0) | sdk+0x5c8d40 |
| +0x40..+0x4c | 4×u32 | swizzle r,g,b,a (2,3,4,5 = R,G,B,A) | sdk+0x5c8d50; defaults 0xac4740/0xac5840 |
| +0x50 | u32 | depth-stencil mode (0 = DEPTH) | sdk+0x5c8d60 |
| +0x54 | u16 | packaged layout: bits 0-3 "set" (1), bits 4-15 three 4-bit fields from the caller's layout (0) | sdk+0x5c9500, sdk+0x5c8e80 |
| +0x58 | u32 | stride (0) | sdk+0x5c8d90 |
| +0x5c | u32 | GL texture name (0) | sdk+0x5c8d80 |
| +0x60 | const void* | packaged texture data (NULL); neither TextureInitialize nor the layout helper sdk+0x5c9800 reads it | sdk+0x5c9490 |

### Texture (0x80)
| offset | type | meaning | evidence |
|---|---|---|---|
| +0x0 | u64 | zero | sdk+0x5ca240 |
| +0x8 | NVNdevice* | device | sdk+0x5ca240 |
| +0x10 | NVNmemoryPool* | pool | sdk+0x5cb980 |
| +0x18 | u64 | bits 0-47 pool offset, 48-55 levels (u8 @+0x1e), 56-63 samples (u8 @+0x1f) | sdk+0x5cb990, 0x5cac30, 0x5cac70 |
| +0x20 | u32 | width | sdk+0x5cac00 |
| +0x24 / +0x26 | u16 / u16 | height / depth (or layers) | sdk+0x5cac10 / 0x5cac20 |
| +0x28 | u32 | bits 0-15 flags, 16-24 format (9 bits), 25-31 target (7 bits) | sdk+0x5cabc0/0x5cac40/0x5cabd0 |
| +0x2c | u16 | swizzle r(0-2) g(3-5) b(6-8) a(9-11), depth-stencil mode (12-13), format has depth (14), has stencil (15) | sdk+0x5cac80, 0x5cacc0; Initialize |
| +0x30..+0x47 | | zero (used only by the GL-interop path) | |
| +0x48 | u8,u8,u48 | block width, block height, bits 16-63 = layer stride in bytes | layout helper sdk+0x5c9800; WriteTexels sdk+0x5cb2a0; FlushTexels sdk+0x5cb860 |
| +0x50 | u32 | row stride (linear textures) | sdk+0x5cacf0, sdk+0x5c9800 |
| +0x54 | u8 | bytes per block; +0x55 bits 0-6 log2(bytes per block); bits 16-26 of the u32 at +0x54 = sample-pattern code (0x48000 for 1 sample, 0x450000 for 2, 0x890000 for 4, 0x10a0000 for 8, 0x2120000 for 16), bits 27-31 = levels used by the layout | sdk+0x5c9800 |
| +0x58 | u8 | layout byte from sdk+0x59c0ac [unknown] | sdk+0x5c9800 |
| +0x59 | u8 | PTE kind (storage class low byte) | sdk+0x5cab00, sdk+0x5c9800 |
| +0x5a | u48 | total storage size; GetStorageSize returns its low 32 bits | sdk+0x5cb9a0 |
| +0x60 | u64 | texture GPU address | sdk+0x5cad00 |
| +0x68 / +0x6c / +0x6e | u32 / u16 / u16 | layout width / height / depth-or-layers (cube = 6 per layer) | sdk+0x5c9800 |
| +0x70..+0x72 | 3×u8 | block-linear block-size parameters (from the packaged layout or computed, sdk+0x59c154) [INFERENCE: log2 GOBs per block in z / x / y] | sdk+0x5c9800 |
| +0x78 | u16 | slot in a device table for DEPTH32F / DEPTH32F_STENCIL8 textures (device+0x1e30, mutex device+0x1e48), 0xffff = none; released by Finalize | sdk+0x5ca240, sdk+0x5ca9c0 |

Host check: `tex_t` (≤0x80) still fits. Note that `nvnTextureGetFlags` returns only 16 bits.

### Texture flags (builder u32, texture keeps 16 bits)
NVN1 values unchanged (DISPLAY 0x1, VIDEO_DECODE 0x2, IMAGE 0x4, COMPRESSIBLE 0x8, LINEAR 0x10, SPARSE 0x20, LINEAR_RENDER_TARGET 0x40, ADAPTIVE_ZCULL 0x80, DEPTH16_PREFER_FAST_CLEAR 0x100, MINIMAL_LAYOUT 0x200, ZCULL_SUPPORT_STENCIL 0x400 (tested in the zcull helper sdk+0x5c9c10)). Two new bits:
- **0x800**: in the mip-chain computation (sdk+0x59c2e0) the next level becomes `1 + ((x−1) >> 1)` (round up) instead of `x >> 1`. It is also copied into texture-descriptor word 2 (bit 0x800 → descriptor bit 20, sdk+0x5cadb0) [INFERENCE: "round mip sizes up" layout flag].
- **0x1000**: passed as a boolean to the PTE-kind chooser for colour formats only (sdk+0x5beddc arg 5 → `NvRmGpuDeviceChooseKind` attribute) [meaning unknown].

### Storage size, alignment, class (MKW uses GetStorageSize ×8, GetStorageAlignment ×7, GetStorageClass ×3)
- `GetStorageSize` (sdk+0x5c92e0): 0 when a GL name is set; otherwise the layout from sdk+0x5c9800 (Tegra block-linear: 64-byte × 8-row GOBs, per-level block heights, layer stride padded). Buffer target: width × bytes per pixel. LINEAR/LINEAR_RT: stride × height. Returns 0 for formats whose format-table caps bit 0 is clear.
- `GetStorageAlignment` (sdk+0x5c8f10): start = 0x80 if LINEAR_RENDER_TARGET, else 0x20 if LINEAR, else 1; BUFFER target: max(that, bytes per pixel), with 4 for 12-byte formats; DISPLAY: max(that, `GetInteger(0xb0)`, 0x1000 in the static table). Block-linear: if the chosen kind ≠ device generic kind (compressible), raise to the big page device+0xd4. Finally a result of 1 becomes **0x200**. So: block-linear uncompressed 0x200, compressible big page.
- Kind choice: LINEAR/LINEAR_RT/BUFFER → device generic kind (device+0x1da0, set to 6 at device init) [INFERENCE: 6 = generic 16Bx2]. Otherwise `NvRmGpuDeviceChooseKind(format hw word, samples, compressible, flag 0x1000)` (sdk+0x5beddc). "Compressible" = flag COMPRESSIBLE and device compression enabled (device byte +0x1da4). Depth/stencil formats also need device+0x1c24 ≠ 0x76165656; colour formats need block width ≤ 1 (no block-compressed formats) and device+0x1c20 ≠ 0x76165656 [INFERENCE: debug overrides that turn compression off].
- `GetStorageClass` (sdk+0x5c9360 / texture sdk+0x5cab00): 0x100 when kind == generic kind; otherwise `0x100 | kind`, or `0x300 | kind` when DeviceBuilder flag 0x10000 is set and the texture is block-linear. MapVirtual decodes the same encoding (0x100 → generic, `& ~0x300` → kind, 0x200 → extra flag).
- `GetRawStorageClass`: 1 for linear textures; else 2 for colour formats and DEPTH32F, 3 for STENCIL8/DEPTH16, 4 for DEPTH24/DEPTH24_STENCIL8, 5 for DEPTH32F_STENCIL8 (format jump table at sdk data 0xb7aa71; formats > 0x36 → 2).

### Texture GPU address (TextureInitialize sdk+0x5ca240 → sdk+0x5bd3ac)
- LINEAR / LINEAR_RT / BUFFER target, or any texture in a virtual pool: `pool+0x18 + offset` (sdk+0x5cd904 masks the offset to 48 bits).
- Block-linear with the generic kind: `(pool+0x20 ? pool+0x20 : pool+0x18) + offset`. In MKW pool+0x20 is 0, so this is the CPU address of the storage.
- Compressible kind: the pool must be COMPRESSIBLE; offset and size must be multiples of device+0xd4. The driver then calls `NvRmGpuMappingModify(pool+0x70 mapping, range, kind)` (E8: gpu sub-session 212 `NormalModifyMemoryKind4K`), sets pool flag 0x8000000, and the address is **`pool+0x28 + offset`** (the GPU-only compressible VA). Any failure makes Initialize return false. E8 confirms that without mapping-modify support the texture address stays 0.
- Initialize also fails for a PHYSICAL pool, an unsupported format (caps bit 0), or COMPRESSIBLE on a pool that is neither COMPRESSIBLE nor VIRTUAL (`pool flags & 0x280`).

### Texture and sampler descriptors (handles)
- Descriptor size 0x20 for textures and for samplers (`GetInteger(0xf)` = `GetInteger(0x10)` = 0x20, static table sdk+0x59bf50). RegisterTexture/RegisterImage store at `CPU(pool) + poolOffset + id×0x20`, with id sign-extended (sdk+0x5cadb0/0x5caf40). Samplers likewise (sdk+0x5c67d0, `sbfiz #5`).
- Reserved descriptors: `GetInteger(0x11)` = `GetInteger(0x12)` = 0x100 (static table). The driver itself writes only texture entry 0, a null descriptor (sdk+0x5cad10), and sampler entries 0, 1, 2 (three internal clamp-to-edge samplers, sdk+0x5c6690). MAX_TEXTURE_POOL_SIZE (0x24) = 0x100000, MAX_SAMPLER_POOL_SIZE (0x25) = 0x1000, MEMPOOL_TEXTURE_OBJECT_PAGE_ALIGNMENT (0x27) = 0x1000.
- The texture descriptor is built by sdk+0x5fd0b0 (device, texture, view, out, isImage). Word 2 additionally receives texture flags COMPRESSIBLE → bit 18, SPARSE → bit 17, 0x800 → bit 20; the low 3 bits of the view's min layer → bits 29-31 (the rest in word 7, byte 3); bit 24 only when DeviceBuilder flag 0x10000 is set and the texture is block-linear and not a buffer; RegisterImage adds 0x80000. The hardware encoding is not needed by the host and was not decoded.

### NVN2 format table and new formats
The format table (sdk+0xd934b8, pointer at sdk+0xd7aff0) has 0x2c-byte entries indexed by NVNformat. Each entry holds caps flags (+0x0; bit 0 = supported), class (+0x4), block w/h (+0x8/+0x9), component bits (+0xa..+0xd), depth/stencil bits (+0xe/+0xf), bytes per block (+0x10), integer (+0x11), sRGB (+0x12), normalised (+0x13), and the hardware texture-format word (+0x24). It has **0xa3 entries**: values 0x00–0x9c match the NVN1 enum one-to-one (spot-checked by block size, bpp and sRGB for every DXT/RGTC/BPTC/ASTC/BGR entry). New:

| value | block | bytes/block | sRGB | int | caps | class | hardware word (same as) |
|---|---|---|---|---|---|---|---|
| 0x9d | 4×4 | 8 | no | - | 0x51 | 0x1d | 0x78d24924 (RGB_DXT1, RGB_DXT1_SRGB) |
| 0x9e | 4×4 | 16 | no | - | 0x51 | 0x1e | 0x58d24925 (RGBA_DXT3, RGBA_DXT3_SRGB) |
| 0x9f | 4×4 | 8 | yes | - | 0x51 | 0x1d | 0x78d24924 (RGB_DXT1, RGB_DXT1_SRGB) |
| 0xa0 | 4×4 | 16 | yes | - | 0x51 | 0x1e | 0x58d24925 (RGBA_DXT3, RGBA_DXT3_SRGB) |
| 0xa1 | 1×1 | 8 | no | int | 0x19 | 0x4 | 0x60d36d84 (RG32I) |
| 0xa2 | 1×1 | 8 | no | int | 0x19 | 0x4 | 0x60d49204 (RG32UI) |

[INFERENCE] 0x9d–0xa0 reuse the BC1-RGB / BC2 hardware formats under a new class (0x1d/0x1e) and caps bit 0x40. 0xa1/0xa2 are 64-bit single-channel integer formats (signed/unsigned R64) sampled as RG32I/RG32UI. Their NVN names are not visible in the binary.

### TextureView (0x10)
| offset | type | meaning | evidence |
|---|---|---|---|
| +0x0 | s16 | min layer | sdk+0x5c95a0/0x5c9640 |
| +0x2 | u16 | layer count | same |
| +0x4 / +0x5 | u8 / u8 | base level / level count | sdk+0x5c9570/0x5c9620 |
| +0x6 | u8 | target | sdk+0x5c92c0/0x5c96d0 |
| +0x7 | bits | bit 56 levels set, 57 layers set, 58 format set, 59 swizzle set, 60 depth-stencil mode set, 61 target set, 62-63 depth-stencil mode | setters above, sdk+0x5c9600 |
| +0x8 | u32 | format | sdk+0x5c95c0 |
| +0xc..+0xf | 4×u8 | swizzle r,g,b,a | sdk+0x5c95e0 |

The level and layer counts are stored as u8 / u16. Getters return the "set" bit as NVNboolean.

### MKW call sequences (textures)
- main+0x130374..0x1303ac: `TextureBuilderGetStorageSize` → `memset(texture, 0, 0x80)` → `TextureInitialize`.
- main+0x2625fc..: for up to 4 builders (stack array, stride 0x80): GetStorageAlignment / GetStorageSize → SetStorage(pool, aligned offset) → TextureInitialize. main+0xe2b940..0xe2ba08: the same over an array of builders with stride 0x80.
- main+0x226df14..: compressible render-target pool, described above.
- main+0x524514..0x52453c: TextureFinalize then TextureInitialize of the same object (re-create in place).

### Function table: TextureBuilder
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnTextureBuilderSetDevice` | sdk+0x5c8c50 | 13 | same | +0x8 = device |
| `nvnTextureBuilderSetDefaults` | sdk+0x5c8c60 | 10 | same | +0x10/+0x18 pool/offset = 0, flags 0, target 2D(1), width/height/depth 0, levels 1, format NONE(0), samples 0, swizzle R,G,B,A (2,3,4,5), depth-stencil mode DEPTH(0), packaged layout +0x54 = 0, stride/GL name/packaged data (+0x58..+0x68) = 0; device untouched |
| `nvnTextureBuilderSetFormat` | sdk+0x5c8d30 | 10 | same | +0x38 |
| `nvnTextureBuilderSetTarget` | sdk+0x5c8cb0 | 10 | same | +0x24 |
| `nvnTextureBuilderGetStorageSize` | sdk+0x5c92e0 | 8 | same | 0 if a GL name is set; else computes the layout (sdk+0x5c9800) and returns the total size in bytes (0 for formats whose caps bit 0 is clear = unsupported) |
| `nvnTextureBuilderSetFlags` | sdk+0x5c8ca0 | 8 | same | +0x20 = flags (u32 here, but the texture keeps only 16 bits) |
| `nvnTextureBuilderSetStorage` | sdk+0x5c8d70 | 8 | same | +0x10 pool, +0x18 offset |
| `nvnTextureBuilderGetStorageAlignment` | sdk+0x5c8f10 | 7 | same | see Textures: 0x200 for block-linear generic kind, big-page size (device+0xd4) for compressible kinds, 0x20 LINEAR, 0x80 LINEAR_RENDER_TARGET, bpp for buffer textures, raised to GetInteger(0xb0) for DISPLAY |
| `nvnTextureBuilderSetLevels` | sdk+0x5c8d20 | 6 | same | +0x34 |
| `nvnTextureBuilderSetSwizzle` | sdk+0x5c8d50 | 6 | same | +0x40/+0x44/+0x48/+0x4c (r,g,b,a, u32 each) |
| `nvnTextureBuilderSetSamples` | sdk+0x5c8d40 | 5 | same | +0x3c |
| `nvnTextureBuilderSetSize2D` | sdk+0x5c8d00 | 5 | same | +0x28 = w, +0x2c = h, depth = 1 |
| `nvnTextureBuilderSetWidth` | sdk+0x5c8cc0 | 5 | same | +0x28 |
| `nvnTextureBuilderSetDepth` | sdk+0x5c8ce0 | 4 | same | +0x30 |
| `nvnTextureBuilderSetDepthStencilMode` | sdk+0x5c8d60 | 4 | same | +0x50 |
| `nvnTextureBuilderSetHeight` | sdk+0x5c8cd0 | 4 | same | +0x2c |
| `nvnTextureBuilderGetStorageClass` | sdk+0x5c9360 | 3 | same | 0x100 if the chosen PTE kind equals the device generic kind (device+0x1da0); else kind \| 0x100, or kind \| 0x300 when DeviceBuilder flag 0x10000 and the texture is block-linear |
| `nvnTextureBuilderGetZCullStorageSize` | sdk+0x5caa30 | 2 | same | ZCull save/restore size from the zcull helper sdk+0x5c9c10 (0 if not supported) |
| `nvnTextureBuilderSetPackagedTextureLayout` | sdk+0x5c9500 | 2 | **differs**: reads only the first 16 bits of the 8-byte NVNpackagedTextureLayout | +0x54 (u16): bits 0-3 = 1 ("layout given"), bits 4-15 = the low 12 bits of the layout's first u16 (three 4-bit fields copied into the texture layout at +0x29/+0x2a/+0x28 of the layout block) |
| `nvnTextureBuilderSetStride` | sdk+0x5c8d90 | 2 | register ABI same; **stored as 32 bits** (str w1) although nvn.h says ptrdiff_t | +0x58 (u32) |
| `nvnTextureBuilderGetFlags` | sdk+0x5c8db0 | 1 | same | +0x20 |
| `nvnTextureBuilderGetDepth` | sdk+0x5c8df0 | 0 | same | +0x30 |
| `nvnTextureBuilderGetDepthStencilMode` | sdk+0x5c8e60 | 0 | same | +0x50 |
| `nvnTextureBuilderGetDevice` | sdk+0x5c8da0 | 0 | same (no NVN1 prototype) | +0x8 |
| `nvnTextureBuilderGetFormat` | sdk+0x5c8e10 | 0 | same | +0x38 |
| `nvnTextureBuilderGetGLTextureName` | sdk+0x5c9480 | 0 | same | +0x5c |
| `nvnTextureBuilderGetHeight` | sdk+0x5c8de0 | 0 | same | +0x2c |
| `nvnTextureBuilderGetLevels` | sdk+0x5c8e00 | 0 | same | +0x34 |
| `nvnTextureBuilderGetMemoryOffset` | sdk+0x5c8f00 | 0 | same | +0x18 |
| `nvnTextureBuilderGetMemoryPool` | sdk+0x5c8ef0 | 0 | same (nvn.h: returns NVNmemoryPool by value; it is a pointer) | +0x10 |
| `nvnTextureBuilderGetPackagedTextureData` | sdk+0x5c8e70 | 0 | same | +0x60 |
| `nvnTextureBuilderGetPackagedTextureLayout` | sdk+0x5c8e80 | 0 | same (no NVN1 prototype; (const builder*, NVNpackagedTextureLayout*) → NVNboolean) | writes the 12 stored bits back as a u32 into *out (rest zeroed) and returns +0x54 & 0xf ("set") |
| `nvnTextureBuilderGetRawStorageClass` | sdk+0x5c94a0 | 0 | same (no NVN1 prototype; returns int) | format category: 1 linear/linear-RT; 2 colour (and DEPTH32F); 3 STENCIL8/DEPTH16; 4 DEPTH24/DEPTH24_STENCIL8; 5 DEPTH32F_STENCIL8; 2 for every format > 0x36; NONE → 0 |
| `nvnTextureBuilderGetSamples` | sdk+0x5c8e20 | 0 | same | +0x3c |
| `nvnTextureBuilderGetSparseTileLayout` | sdk+0x5cb100 | 0 | same | fills NVNtextureSparseTileLayout {numTiledLevels, tileW, tileH, tileD} for 64 KiB tiles; all zero unless flags has SPARSE (0x20) and the texture is not 1D/1D-array/buffer, not VIDEO_DECODE/LINEAR, not a 12-byte format |
| `nvnTextureBuilderGetStride` | sdk+0x5c8ee0 | 0 | returns 32 bits (ldr w0) — zero-extended ptrdiff_t | +0x58 |
| `nvnTextureBuilderGetSwizzle` | sdk+0x5c8e30 | 0 | same | +0x40..+0x4c |
| `nvnTextureBuilderGetTarget` | sdk+0x5c8dc0 | 0 | same | +0x24 |
| `nvnTextureBuilderGetViewOffset` | sdk+0x5c90b0 | 0 | same name, no NVN1 prototype: (const NVNtextureBuilder*, const NVNtextureView*) → ptrdiff_t | byte offset of the view's first level/layer from the texture start, computed from the builder |
| `nvnTextureBuilderGetWidth` | sdk+0x5c8dd0 | 0 | same | +0x28 |
| `nvnTextureBuilderSetGLTextureName` | sdk+0x5c8d80 | 0 | same | +0x5c; a non-zero name makes GetStorageSize return 0 and Initialize take the GL-interop path |
| `nvnTextureBuilderSetPackagedTextureData` | sdk+0x5c9490 | 0 | same | +0x60 |
| `nvnTextureBuilderSetSize1D` | sdk+0x5c8cf0 | 0 | same | +0x28 = w, height = depth = 1 |
| `nvnTextureBuilderSetSize3D` | sdk+0x5c8d10 | 0 | same | +0x28/+0x2c/+0x30 |

### Function table: Texture
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnTextureGetFlags` | sdk+0x5cabc0 | 9 | same | sign-extended 16 bits of +0x28 |
| `nvnTextureInitialize` | sdk+0x5ca240 | 7 | same | zeroes 0x80 bytes, sets +0x78 = 0xffff, packs the builder (see layout), computes the layout, picks the PTE kind and GPU address (+0x60); fails if: pool PHYSICAL, unsupported format, COMPRESSIBLE flag on a pool that is neither COMPRESSIBLE nor VIRTUAL, non-generic kind not big-page aligned, or mapping-modify fails. DEPTH32F/DEPTH32F_STENCIL8 textures take a slot in a device table (device+0x1e30, mutex +0x1e48) stored at +0x78 |
| `nvnTextureFinalize` | sdk+0x5ca9c0 | 5 | same | releases the +0x78 device slot if any; nothing else (no GPU unmapping) |
| `nvnTextureGetHeight` | sdk+0x5cac10 | 5 | same | u16 +0x24 |
| `nvnTextureGetTarget` | sdk+0x5cabd0 | 5 | same | bits 25-31 of +0x28 |
| `nvnTextureGetDepth` | sdk+0x5cac20 | 3 | same | u16 +0x26 |
| `nvnTextureGetFormat` | sdk+0x5cac40 | 3 | same | bits 16-24 of +0x28 |
| `nvnTextureGetWidth` | sdk+0x5cac00 | 3 | same | u32 +0x20 |
| `nvnTextureGetLevels` | sdk+0x5cac30 | 2 | same | u8 +0x1e |
| `nvnTextureGetSamples` | sdk+0x5cac70 | 2 | same | u8 +0x1f |
| `nvnTextureGetSwizzle` | sdk+0x5cac80 | 2 | same | 3-bit fields of u16 +0x2c |
| `nvnTextureFlushTexels` | sdk+0x5cb860 | 1 | same | computes the byte range of the view layers and calls pool flush = **no-op** |
| `nvnTextureGetMemoryOffset` | sdk+0x5cb990 | 1 | same | +0x18 & 0xffffffffffff (48-bit) |
| `nvnTextureGetMemoryPool` | sdk+0x5cb980 | 1 | same | +0x10 |
| `nvnTextureSetDebugLabel` | sdk+0x5b0b80 | 1 | same | **no-op** |
| `nvnTextureWriteTexels` | sdk+0x5cb570 | 1 | same | = WriteTexelsStrided(…, 0, 0) |
| `nvnTextureCompare` | sdk+0x5cb9b0 | 0 | same | true if device, pool, 48-bit offset, flags, target, format, sizes, ... are equal |
| `nvnTextureGetDebugID` | sdk+0x5cbb30 | 0 | same | **stub: returns 0** |
| `nvnTextureGetDepthStencilMode` | sdk+0x5cacc0 | 0 | same | bits 12-13 of u16 +0x2c |
| `nvnTextureGetRawStorageClass` | sdk+0x5cbb40 | 0 | same (no NVN1 prototype) | 1 if LINEAR/LINEAR_RT, else per-format category (as builder) |
| `nvnTextureGetSparseTileLayout` | sdk+0x5cb1b0 | 0 | same | as the builder version, from the stored texture |
| `nvnTextureGetStorageClass` | sdk+0x5cab00 | 0 | same | same rule as the builder getter, from the stored kind byte +0x59 |
| `nvnTextureGetStorageSize` | sdk+0x5cb9a0 | 0 | same (int) | u32 at +0x5a (unaligned) |
| `nvnTextureGetStride` | sdk+0x5cacf0 | 0 | returns 32 bits | u32 +0x50 |
| `nvnTextureGetTextureAddress` | sdk+0x5cad00 | 0 | same | u64 +0x60 (GPU VA chosen at Initialize) |
| `nvnTextureGetViewOffset` | sdk+0x5cab70 | 0 | same | (view first level/layer GPU address via sdk+0x5c9dec) − texture address |
| `nvnTextureGetZCullStorageSize` | sdk+0x5caa80 | 0 | same | ZCull size from the texture fields |
| `nvnTextureInvalidateTexels` | sdk+0x5cb8f0 | 0 | same | same range computation, pool invalidate = **no-op** |
| `nvnTextureReadTexels` | sdk+0x5cb850 | 0 | same | = ReadTexelsStrided(…, 0, 0) |
| `nvnTextureReadTexelsStrided` | sdk+0x5cb580 | 0 | same | CPU de-swizzle copy from the pool CPU mapping |
| `nvnTextureWriteTexelsStrided` | sdk+0x5cb2a0 | 0 | same | CPU copy into the pool CPU mapping with the block-linear (64-byte×8-row GOB) swizzle; region/view/strides as NVN1 |

### Function table: TextureView
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnTextureViewSetDefaults` | sdk+0x5c9560 | 49 | same (struct now 16 bytes) | zeroes 16 bytes |
| `nvnTextureViewSetLevels` | sdk+0x5c9570 | 32 | same | byte 4 = base level, byte 5 = level count, bit 56 set |
| `nvnTextureViewSetLayers` | sdk+0x5c95a0 | 22 | same | u16 0 = min layer, u16 2 = layer count, bit 57 set |
| `nvnTextureViewSetSwizzle` | sdk+0x5c95e0 | 20 | same | bytes +0xc..+0xf = r,g,b,a, bit 59 set |
| `nvnTextureViewSetTarget` | sdk+0x5c92c0 | 5 | same | byte 6 = target, bit 61 set |
| `nvnTextureViewSetFormat` | sdk+0x5c95c0 | 2 | same | u32 +8 = format, bit 58 set |
| `nvnTextureViewGetLayers` | sdk+0x5c9640 | 1 | same | outputs u16 0 / u16 2, returns bit 57 |
| `nvnTextureViewGetLevels` | sdk+0x5c9620 | 1 | same | outputs bytes 4/5, returns bit 56 |
| `nvnTextureViewGetTarget` | sdk+0x5c96d0 | 1 | same | outputs byte 6, returns bit 61 |
| `nvnTextureViewSetDepthStencilMode` | sdk+0x5c9600 | 1 | same | bits 62-63 = mode, bit 60 set |
| `nvnTextureViewCompare` | sdk+0x5c96f0 | 0 | same | compares set-bits and the fields that are set |
| `nvnTextureViewGetDepthStencilMode` | sdk+0x5c96b0 | 0 | same | outputs bits 62-63, returns bit 60 |
| `nvnTextureViewGetFormat` | sdk+0x5c9660 | 0 | same | outputs +8, returns bit 58 |
| `nvnTextureViewGetSwizzle` | sdk+0x5c9680 | 0 | same | outputs +0xc..+0xf, returns bit 59 |

## Texture and sampler pools

TexturePool / SamplerPool (0x20 each): +0x0 `const NVNmemoryPool*`, +0x8 s64 offset, +0x10 s32 descriptor count, +0x18 u64 zero (sdk+0x5cad10, sdk+0x5c6690). Initialize writes descriptors through the pool's CPU mapping: entry 0 of a texture pool, entries 0–2 of a sampler pool. A CPU_NO_ACCESS pool has no CPU pointer, so the write would go to address 0 + offset [INFERENCE: callers must use CPU-visible descriptor memory; MKW uses flags 0x22].

### Function table: TexturePool
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnTexturePoolInitialize` | sdk+0x5cad10 | 1 | same | +0 pool, +8 offset, +0x10 count, +0x18 = 0; writes a null descriptor (0x20 bytes: u32 0x24900, 0, 0x600000, 0…) at entry 0 through the pool CPU mapping; always returns true |
| `nvnTexturePoolRegisterTexture` | sdk+0x5cadb0 | 1 | same | encodes the 0x20-byte texture descriptor (sdk+0x5fd0b0, image=0) and stores it at CPU(pool)+offset+id*0x20 (id sign-extended) |
| `nvnTexturePoolFinalize` | sdk+0x5cada0 | 0 | same | **no-op** |
| `nvnTexturePoolGetMemoryOffset` | sdk+0x5cb0e0 | 0 | same | +8 |
| `nvnTexturePoolGetMemoryPool` | sdk+0x5cb0d0 | 0 | same | +0 |
| `nvnTexturePoolGetSize` | sdk+0x5cb0f0 | 0 | same | +0x10 |
| `nvnTexturePoolRegisterImage` | sdk+0x5caf40 | 0 | same | same with image=1 and bit 0x80000 set in descriptor word 2 |
| `nvnTexturePoolSetDebugLabel` | sdk+0x5b0ba0 | 0 | same | **no-op** |

### Function table: SamplerPool
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnSamplerPoolInitialize` | sdk+0x5c6690 | 1 | same | +0 pool, +8 offset, +0x10 count, +0x18 = 0; writes three driver samplers at ids 0,1,2 (all CLAMP_TO_EDGE, LOD 0..1000; 0: linear aniso 1; 1: linear aniso 2.0; 2: nearest aniso 2.0); returns true |
| `nvnSamplerPoolRegisterSampler` | sdk+0x5c67a0 | 1 | same | encodes from sampler+8 (the builder copy) via sdk+0x5c67d0 into CPU(pool)+offset+id*0x20 |
| `nvnSamplerPoolFinalize` | sdk+0x5c67b0 | 0 | same | **no-op** |
| `nvnSamplerPoolGetMemoryOffset` | sdk+0x5c68c0 | 0 | same | +8 |
| `nvnSamplerPoolGetMemoryPool` | sdk+0x5c68b0 | 0 | same | +0 |
| `nvnSamplerPoolGetSize` | sdk+0x5c68d0 | 0 | same | +0x10 |
| `nvnSamplerPoolRegisterSamplerBuilder` | sdk+0x5c67c0 | 0 | same | same, directly from the builder |
| `nvnSamplerPoolSetDebugLabel` | sdk+0x5b0bb0 | 0 | same | **no-op** |

## Samplers

### SamplerBuilder (0x30 used; game reserves 0x40; NVN1 header 0x60)
| offset | type | meaning (default) | evidence |
|---|---|---|---|
| +0x0 | NVNdevice* | device | sdk+0x5c6210 |
| +0x8 / +0xc | f32 / f32 | LOD clamp min / max (0.0 / 1000.0) | sdk+0x5c6290; defaults sdk data 0xac6cb0 |
| +0x10 | f32 | LOD bias (0) | sdk+0x5c62a0 |
| +0x14 | f32 | max anisotropy (1.0) | sdk+0x5c6360 |
| +0x18 | f32 | LOD snap (0) | sdk+0x5c6380 |
| +0x1c..+0x28 | 4×u32 | border colour (float / int / uint share storage) (0) | sdk+0x5c62d0..0x5c6330 |
| +0x2c | u32 | packed: wrap S 0-3, T 4-7, R 8-11, reduction 12-15, min filter 16-19, mag filter 20-23, compare mode 24-25, compare func 26-31. Default 0x08110111 = REPEAT ×3, AVERAGE, LINEAR, LINEAR, NONE, LESS | sdk+0x5c6220..0x5c6370 |

No new sampler fields relative to NVN1's API (reduction, LOD snap and border colours already existed). The getters sign-extend the packed fields (`sbfx`), so a 4-bit field reads back correctly only for values ≤ 7. Every NVN wrap/filter/reduction enum fits, and the compare function has 6 bits.

### Sampler (0x40, grew from 0x30)
+0x0 zero; +0x8..+0x38 = copy of builder bytes 0..0x30 (device at +0x8, LOD min/max +0x10/+0x14, bias +0x18, aniso +0x1c, LOD snap +0x20, border +0x24..+0x30, packed +0x34); +0x38..+0x40 zero (sdk+0x5c6500 and the getters sdk+0x5c6540..0x5c6680). RegisterSampler encodes from sampler+0x8, so a sampler is a builder snapshot.

MKW (main+0xcee0e4..0xcee144): SamplerBuilderSetDefaults → SetMinMagFilter(LINEAR_MIPMAP_NEAREST, LINEAR) → SetDevice → SamplerInitialize(obj+0x110). main+0x9a7cb0..: wrap CLAMP_TO_EDGE ×3, aniso 1.0, compare (NONE, ALWAYS), then SamplerInitialize into an array.

### Function table: SamplerBuilder
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnSamplerBuilderSetDefaults` | sdk+0x5c6220 | 4 | same | LOD min 0, max 1000.0, bias 0, max aniso 1.0, LOD snap 0, border 0,0,0,0, packed +0x2c = 0x08110111 (wrap S/T/R REPEAT, reduction AVERAGE, min LINEAR, mag LINEAR, compare NONE, func LESS); device untouched |
| `nvnSamplerBuilderSetMinMagFilter` | sdk+0x5c6250 | 4 | same | byte +0x2e = min \| mag<<4 (bits 16-23 of the packed word) |
| `nvnSamplerBuilderSetDevice` | sdk+0x5c6210 | 3 | same | +0 |
| `nvnSamplerBuilderSetMaxAnisotropy` | sdk+0x5c6360 | 3 | same | +0x14 |
| `nvnSamplerBuilderSetCompare` | sdk+0x5c62b0 | 2 | same | packed bits 24-25 mode, 26-31 func |
| `nvnSamplerBuilderSetLodBias` | sdk+0x5c62a0 | 2 | same | +0x10 |
| `nvnSamplerBuilderSetLodClamp` | sdk+0x5c6290 | 2 | same | +0x8 min, +0xc max (float) |
| `nvnSamplerBuilderSetWrapMode` | sdk+0x5c6270 | 2 | same | packed bits 0-3 / 4-7 / 8-11 |
| `nvnSamplerBuilderSetBorderColor` | sdk+0x5c62d0 | 1 | same | +0x1c..+0x28 (4 floats) |
| `nvnSamplerBuilderSetReductionFilter` | sdk+0x5c6370 | 1 | same | packed bits 12-15 |
| `nvnSamplerBuilderGetBorderColor` | sdk+0x5c6440 | 0 | same | +0x1c.. |
| `nvnSamplerBuilderGetBorderColori` | sdk+0x5c6470 | 0 | same | +0x1c.. |
| `nvnSamplerBuilderGetBorderColorui` | sdk+0x5c64a0 | 0 | same | +0x1c.. |
| `nvnSamplerBuilderGetCompare` | sdk+0x5c6420 | 0 | same | packed bits 24-25 / 26-31 |
| `nvnSamplerBuilderGetDevice` | sdk+0x5c6390 | 0 | same (no NVN1 prototype) | +0 |
| `nvnSamplerBuilderGetLodBias` | sdk+0x5c6410 | 0 | same | +0x10 |
| `nvnSamplerBuilderGetLodClamp` | sdk+0x5c63f0 | 0 | same | +0x8 / +0xc |
| `nvnSamplerBuilderGetLodSnap` | sdk+0x5c64f0 | 0 | same | +0x18 |
| `nvnSamplerBuilderGetMaxAnisotropy` | sdk+0x5c64d0 | 0 | same | +0x14 |
| `nvnSamplerBuilderGetMinMagFilter` | sdk+0x5c63a0 | 0 | same | packed bits 16-19 / 20-23 |
| `nvnSamplerBuilderGetReductionFilter` | sdk+0x5c64e0 | 0 | same | packed bits 12-15 |
| `nvnSamplerBuilderGetWrapMode` | sdk+0x5c63c0 | 0 | same | packed nibbles 0..2 |
| `nvnSamplerBuilderSetBorderColori` | sdk+0x5c6300 | 0 | same | same storage, raw ints |
| `nvnSamplerBuilderSetBorderColorui` | sdk+0x5c6330 | 0 | same | same storage, raw uints |
| `nvnSamplerBuilderSetLodSnap` | sdk+0x5c6380 | 0 | same | +0x18 |

### Function table: Sampler
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnSamplerInitialize` | sdk+0x5c6500 | 4 | same, **object grew to 0x40** | zeroes 0x40 bytes and copies builder bytes 0..0x30 to sampler+0x8..+0x38; returns true |
| `nvnSamplerFinalize` | sdk+0x5c6530 | 3 | same | **no-op** |
| `nvnSamplerGetCompare` | sdk+0x5c65c0 | 1 | same | +0x34 bits 24-31 |
| `nvnSamplerGetLodBias` | sdk+0x5c65b0 | 1 | same | +0x18 |
| `nvnSamplerGetMaxAnisotropy` | sdk+0x5c6670 | 1 | same | +0x1c |
| `nvnSamplerGetMinMagFilter` | sdk+0x5c6540 | 1 | same | packed word +0x34 |
| `nvnSamplerSetDebugLabel` | sdk+0x5b0b60 | 1 | same | **no-op** |
| `nvnSamplerCompare` | sdk+0x5c68e0 | 0 | same | device, wrap/filter bits, LOD clamp, bias, border, aniso, reduction |
| `nvnSamplerGetBorderColor` | sdk+0x5c65e0 | 0 | same | +0x24..+0x30 |
| `nvnSamplerGetBorderColori` | sdk+0x5c6610 | 0 | same | +0x24.. |
| `nvnSamplerGetBorderColorui` | sdk+0x5c6640 | 0 | same | +0x24.. |
| `nvnSamplerGetDebugID` | sdk+0x5c6990 | 0 | same | **stub: returns 0** |
| `nvnSamplerGetLodClamp` | sdk+0x5c6590 | 0 | same | +0x10 / +0x14 |
| `nvnSamplerGetReductionFilter` | sdk+0x5c6680 | 0 | same | +0x34 bits 12-15 |
| `nvnSamplerGetWrapMode` | sdk+0x5c6560 | 0 | same | +0x34 |

## State objects

All are plain bit-packed values that the game copies by value. The driver reads them at bind time. Layouts (driver bit positions) and SetDefaults values:

### BlendState (8 bytes)
word0 (+0): bits 0-2 blend target, 8-13 advanced mode, 16-17 advanced overlap, 18 advanced premultiplied src, 19 advanced normalized dst, 24-26 RGB equation, 28-30 alpha equation. word1 (+4): src RGB 0-6, dst RGB 8-14, src alpha 16-22, dst alpha 24-30.
Default (sdk+0x5c69a0): `0x01020102_110c0000` = target 0, advanced mode NONE, overlap UNCORRELATED, premultiplied src **true**, normalized dst **true**, equations ADD/ADD, funcs ONE/ZERO/ONE/ZERO.

### ChannelMaskState (4 bytes)
4 bits per colour target at bit 4×i (r=1, g=2, b=4, a=8). Default 0xffffffff (sdk+0x5c6b80).

### ColorState (4 bytes)
byte 1 = blend enable per target (bit i), byte 2 = logic op, bits 24-27 = alpha-test function; byte 0 unused. Default 0x08030000 = no blending, COPY, ALWAYS (sdk+0x5c6c10).

### DepthStencilState (8 bytes)
word0: bit 0 depth test, bit 1 depth write, bit 2 stencil test, bits 4-7 depth func. word1: front stencil func 0-3, fail 4-7, depth-pass 8-11, depth-fail 12-15; back stencil func 16-19, fail 20-23, depth-pass 24-27, depth-fail 28-31. Face argument: bit 0 front, bit 1 back; getters use back only for face == BACK (2).
Default (sdk+0x5c6da0): `0x11181118_00000022` = depth test off, **depth write on**, stencil off, LESS; stencil ALWAYS, KEEP/KEEP/KEEP on both faces.

### MultisampleState (0x18 bytes)
word0: bit 0 multisample enable, 1 alpha-to-coverage, 2 alpha-to-coverage dither, 3 sample-locations enable, 4 sample-locations grid enable, **5 coverage truncation (new)**, bits 8-12 samples, 13-14 coverage modulation mode, 15-19 raster samples, 20 coverage-to-colour enable, 21-23 coverage-to-colour output. Bytes +4..+0x13: programmable sample locations, one byte per sample, x and y as 4-bit fixed point [INFERENCE from the 0x88 default = (0.5, 0.5); SetSampleLocations clamps each float to [0,1] first]. +0x14 u32 zero.
Default (sdk+0x5c7050): word0 = 5 (multisample on, dither on, samples 0), locations all 0x88.

### PolygonState (4 bytes)
bits 0-1 cull face, bit 2 front face, bits 3-4 polygon mode, bits 15-18 polygon-offset enables. Default 0x14 = cull NONE, CCW, FILL, no offset (sdk+0x5c6f80).

### VertexAttribState (4 bytes)
bits 0-4 stream index, bit 6 "format set" (enabled), bits 7-20 relative offset (14 bits, max 16383), bits 21-30 format (10 bits); bits 5 and 31 are kept as they are. Default 0 (sdk+0x5c6cd0, 0x5c6ce0).

### VertexStreamState (8 bytes)
+0 u32 stride, +4 u32 divisor. Default 0/0 (sdk+0x5c6d50).

All setters store the argument without range checks (no masking of the enum arguments beyond the shift/mask shown). The host's `nvn_obj.h` bit layouts are its own and all fit the sizes above. The only host gap is MultisampleState coverage truncation: `ms_st` has spare bytes for it.

### Function table: BlendState
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnBlendStateSetDefaults` | sdk+0x5c69a0 | 9 | same | u64 = 0x01020102_110c0000 → see bit layout (target 0, src ONE / dst ZERO, eq ADD, premultiplied src = 1, normalized dst = 1) |
| `nvnBlendStateSetBlendEquation` | sdk+0x5c6a20 | 8 | same | word0 bits 24-26 RGB, 28-30 alpha |
| `nvnBlendStateSetBlendFunc` | sdk+0x5c69e0 | 8 | same | word1: srcRGB bits 0-6, dstRGB 8-14, srcA 16-22, dstA 24-30 |
| `nvnBlendStateSetBlendTarget` | sdk+0x5c69c0 | 8 | same | word0 bits 0-2 (no masking of the argument) |
| `nvnBlendStateGetAdvancedMode` | sdk+0x5c6b40 | 0 | same | bits 8-13 |
| `nvnBlendStateGetAdvancedNormalizedDst` | sdk+0x5c6b70 | 0 | same | bit 19 |
| `nvnBlendStateGetAdvancedOverlap` | sdk+0x5c6b50 | 0 | same | bits 16-17 |
| `nvnBlendStateGetAdvancedPremultipliedSrc` | sdk+0x5c6b60 | 0 | same | bit 18 |
| `nvnBlendStateGetBlendEquation` | sdk+0x5c6b20 | 0 | same | bits 24-26 / 28-30 |
| `nvnBlendStateGetBlendFunc` | sdk+0x5c6ae0 | 0 | same | word1 fields |
| `nvnBlendStateGetBlendTarget` | sdk+0x5c6ad0 | 0 | same | bits 0-2 |
| `nvnBlendStateSetAdvancedMode` | sdk+0x5c6a50 | 0 | same | word0 bits 8-13 |
| `nvnBlendStateSetAdvancedNormalizedDst` | sdk+0x5c6ab0 | 0 | same | word0 bit 19 |
| `nvnBlendStateSetAdvancedOverlap` | sdk+0x5c6a70 | 0 | same | word0 bits 16-17 |
| `nvnBlendStateSetAdvancedPremultipliedSrc` | sdk+0x5c6a90 | 0 | same | word0 bit 18 |

### Function table: ChannelMaskState
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnChannelMaskStateSetChannelMask` | sdk+0x5c6b90 | 7 | same | 4 bits per target at bit 4*index: r=1 g=2 b=4 a=8 |
| `nvnChannelMaskStateSetDefaults` | sdk+0x5c6b80 | 2 | same | 0xffffffff (all channels of all 8 targets enabled) |
| `nvnChannelMaskStateGetChannelMask` | sdk+0x5c6be0 | 0 | same | reads the nibble back as 4 booleans |

### Function table: ColorState
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnColorStateSetDefaults` | sdk+0x5c6c10 | 9 | same | 0x08030000: blend enables 0, logic op COPY (3), alpha test ALWAYS (8) |
| `nvnColorStateSetLogicOp` | sdk+0x5c6c60 | 8 | same | byte2 |
| `nvnColorStateSetBlendEnable` | sdk+0x5c6c20 | 7 | same | byte1 bit index (8 targets) |
| `nvnColorStateSetAlphaTest` | sdk+0x5c6c80 | 2 | same | bits 24-27 |
| `nvnColorStateGetAlphaTest` | sdk+0x5c6cc0 | 0 | same | byte3 & 0xf |
| `nvnColorStateGetBlendEnable` | sdk+0x5c6ca0 | 0 | same | byte1 >> index & 1 |
| `nvnColorStateGetLogicOp` | sdk+0x5c6cb0 | 0 | same | byte2 |

### Function table: DepthStencilState
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnDepthStencilStateSetStencilFunc` | sdk+0x5c6e40 | 10 | same | face bit0 → word1 bits 0-3, face bit1 → bits 16-19 |
| `nvnDepthStencilStateSetStencilOp` | sdk+0x5c6e80 | 10 | same | front: fail 4-7, depthFail 12-15, depthPass 8-11; back: fail 20-23, depthFail 28-31, depthPass 24-27 |
| `nvnDepthStencilStateSetDepthTestEnable` | sdk+0x5c6dc0 | 9 | same | word0 bit 0 |
| `nvnDepthStencilStateSetDepthWriteEnable` | sdk+0x5c6de0 | 9 | same | word0 bit 1 |
| `nvnDepthStencilStateSetStencilTestEnable` | sdk+0x5c6e20 | 9 | same | word0 bit 2 |
| `nvnDepthStencilStateSetDepthFunc` | sdk+0x5c6e00 | 8 | same | word0 bits 4-7 |
| `nvnDepthStencilStateSetDefaults` | sdk+0x5c6da0 | 2 | same | 0x11181118_00000022: depth test off, depth write on, stencil test off, depth func LESS; both faces stencil func ALWAYS, ops KEEP/KEEP/KEEP |
| `nvnDepthStencilStateGetDepthFunc` | sdk+0x5c6ef0 | 0 | same | bits 4-7 |
| `nvnDepthStencilStateGetDepthTestEnable` | sdk+0x5c6ed0 | 0 | same | bit 0 |
| `nvnDepthStencilStateGetDepthWriteEnable` | sdk+0x5c6ee0 | 0 | same | bit 1 |
| `nvnDepthStencilStateGetStencilFunc` | sdk+0x5c6f10 | 0 | same | BACK (2) → back field, any other face → front |
| `nvnDepthStencilStateGetStencilOp` | sdk+0x5c6f30 | 0 | same | BACK (2) → back fields, else front |
| `nvnDepthStencilStateGetStencilTestEnable` | sdk+0x5c6f00 | 0 | same | bit 2 |

### Function table: MultisampleState
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnMultisampleStateSetDefaults` | sdk+0x5c7050 | 1 | same | word0 = 5 (multisample on, alpha-to-coverage dither on, samples 0), sample locations bytes +4..+0x13 = 0x88 (all centred), +0x14 = 0 |
| `nvnMultisampleStateGetAlphaToCoverageDither` | sdk+0x5c7120 | 0 | same | bit 2 |
| `nvnMultisampleStateGetAlphaToCoverageEnable` | sdk+0x5c7110 | 0 | same | bit 1 |
| `nvnMultisampleStateGetCoverageModulationMode` | sdk+0x5c7180 | 0 | same | bits 13-14 |
| `nvnMultisampleStateGetCoverageToColorEnable` | sdk+0x5c71e0 | 0 | same | bit 20 |
| `nvnMultisampleStateGetCoverageToColorOutput` | sdk+0x5c7210 | 0 | same | bits 21-23 |
| `nvnMultisampleStateGetCoverageTruncationEnable` | sdk+0x5c71b0 | 0 | **new**: (const NVNmultisampleState*) → NVNboolean | bit 5 |
| `nvnMultisampleStateGetMultisampleEnable` | sdk+0x5c70f0 | 0 | same | bit 0 |
| `nvnMultisampleStateGetRasterSamples` | sdk+0x5c7150 | 0 | same | bits 15-19 |
| `nvnMultisampleStateGetSampleLocationsEnable` | sdk+0x5c7240 | 0 | same | bit 3 |
| `nvnMultisampleStateGetSampleLocationsGrid` | sdk+0x5c7250 | 0 | same | grid width/height from max(samples, raster samples) via a table (sdk data 0xb7aa50) |
| `nvnMultisampleStateGetSampleLocationsGridEnable` | sdk+0x5c72e0 | 0 | same | bit 4 |
| `nvnMultisampleStateGetSamples` | sdk+0x5c7100 | 0 | same | bits 8-12 |
| `nvnMultisampleStateSetAlphaToCoverageDither` | sdk+0x5c70b0 | 0 | same | bit 2 |
| `nvnMultisampleStateSetAlphaToCoverageEnable` | sdk+0x5c7090 | 0 | same | bit 1 |
| `nvnMultisampleStateSetCoverageModulationMode` | sdk+0x5c7160 | 0 | same | bits 13-14 |
| `nvnMultisampleStateSetCoverageToColorEnable` | sdk+0x5c71c0 | 0 | same | bit 20 |
| `nvnMultisampleStateSetCoverageToColorOutput` | sdk+0x5c71f0 | 0 | same | bits 21-23 |
| `nvnMultisampleStateSetCoverageTruncationEnable` | sdk+0x5c7190 | 0 | **new**: (NVNmultisampleState*, NVNboolean) | bit 5 |
| `nvnMultisampleStateSetMultisampleEnable` | sdk+0x5c7070 | 0 | same | bit 0 |
| `nvnMultisampleStateSetRasterSamples` | sdk+0x5c7130 | 0 | same | bits 15-19 |
| `nvnMultisampleStateSetSampleLocations` | sdk+0x5c72f0 | 0 | same | (state, first, count, const float *xy): clamps each coordinate to [0,1] and packs it as a 4-bit value into bytes +4..+0x13 [INFERENCE: 16 samples × (x,y) nibbles] |
| `nvnMultisampleStateSetSampleLocationsEnable` | sdk+0x5c7220 | 0 | same | bit 3 |
| `nvnMultisampleStateSetSampleLocationsGridEnable` | sdk+0x5c72c0 | 0 | same | bit 4 |
| `nvnMultisampleStateSetSamples` | sdk+0x5c70d0 | 0 | same | bits 8-12 |

### Function table: PolygonState
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnPolygonStateSetFrontFace` | sdk+0x5c6fb0 | 9 | same | bit 2 |
| `nvnPolygonStateSetPolygonMode` | sdk+0x5c6fd0 | 9 | same | bits 3-4 |
| `nvnPolygonStateSetCullFace` | sdk+0x5c6f90 | 8 | same | bits 0-1 |
| `nvnPolygonStateSetPolygonOffsetEnables` | sdk+0x5c6ff0 | 7 | same | bits 15-18 |
| `nvnPolygonStateSetDefaults` | sdk+0x5c6f80 | 2 | same | 0x14: cull NONE, front CCW, mode FILL, offset enables 0 |
| `nvnPolygonStateGetCullFace` | sdk+0x5c7010 | 0 | same | bits 0-1 |
| `nvnPolygonStateGetFrontFace` | sdk+0x5c7020 | 0 | same | bit 2 |
| `nvnPolygonStateGetPolygonMode` | sdk+0x5c7030 | 0 | same | bits 3-4 |
| `nvnPolygonStateGetPolygonOffsetEnables` | sdk+0x5c7040 | 0 | same | bits 15-18 |

### Function table: VertexAttribState
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnVertexAttribStateSetDefaults` | sdk+0x5c6cd0 | 5 | same | 0 |
| `nvnVertexAttribStateSetFormat` | sdk+0x5c6ce0 | 4 | same (relative offset kept in 14 bits) | bits 21-30 format (10 bits), bits 7-20 relative offset, bit 6 = "enabled" set |
| `nvnVertexAttribStateSetStreamIndex` | sdk+0x5c6d00 | 3 | same | bits 0-4 |
| `nvnVertexAttribStateGetFormat` | sdk+0x5c6d20 | 0 | same | format bits 21-30, offset bits 7-20 |
| `nvnVertexAttribStateGetStreamIndex` | sdk+0x5c6d40 | 0 | same | bits 0-4 |

### Function table: VertexStreamState
| function | sdk impl | main uses | NVN1 ABI | semantics (evidence: impl offset) |
|---|---|---|---|---|
| `nvnVertexStreamStateSetDefaults` | sdk+0x5c6d50 | 3 | same | stride 0, divisor 0 |
| `nvnVertexStreamStateSetStride` | sdk+0x5c6d60 | 3 | register ABI same; stored as 32 bits | u32 +0 |
| `nvnVertexStreamStateSetDivisor` | sdk+0x5c6d70 | 2 | same | u32 +4 |
| `nvnVertexStreamStateGetDivisor` | sdk+0x5c6d90 | 0 | same | u32 +4 |
| `nvnVertexStreamStateGetStride` | sdk+0x5c6d80 | 0 | returns 32 bits | u32 +0 |

## Open questions: resources
- Runtime values of device+0xd4 (big page / mapping granularity) and device+0xd8 (`MEMORY_POOL_PAGE_SIZE`). Both come from NvRm GPU info +0x2c0/+0x2c4 and were not observed. The static table says 0x10000 for 0x3a. E2 reports the AS is created with big_page_size 0.
- Meaning of memory-pool flag 0x400 (only in the driver's internal pool 0x412); of DeviceBuilder flags 0x8000 (upgrade CPU_UNCACHED to cached), 0x10000 (extra generic mapping +0x20, storage-class bit 0x200, descriptor bit 24) and bit 31 (no CPU-identity VA reservation). These names belong to the Device family.
- Texture flags 0x800 (mip rounding?) and 0x1000 (kind-chooser hint) have no NVN-side name. Formats 0x9d–0xa2 are unnamed.
- How ExternalMemoryPoolBuilder is consumed: no driver function reads it. Likely a C++ inline in the NVN2 header or a later SDK.
- Reserved-list mode (builder flag 0x20000000, {type, va, size} list at +0x28/+0x30): no public setter found.
- Texture layout bytes +0x58 and +0x70..+0x72 (block-linear parameters) were not fully decoded. The host computes its own layout, so this only matters if the game reads them.
- Whether MKW reads any NVN object field directly (inline header code, as NVN1 did for command buffers) was not checked for these objects. All MKW accesses examined go through the proc table.
- The descriptor encoders (sdk+0x5fd0b0 texture, sdk+0x5c67d0 sampler) were not decoded: nxnative does not need hardware TIC/TSC words.

## Program and ProgramBuilder

Sources: the decompiled driver, the control-section parser vtables at sdk+0xd6b980 / +0xd6bda8 / +0xd6c1d0 (decoded one getter at a time), and statistics over the 11,567 control sections in Mario Kart World's RomFS (split by `dm/gfx/tools/shader_split.py`). "S" below is the driver's 0x6f0-byte per-stage scratch record built by sdk+0x5bf6d4; "U" is its sub-record at S+0x640 filled by sdk+0x5c2078; "P" is the 0x90-byte per-stage record built by sdk+0x5c0598.

### NVNprogram (0xA0 bytes in NVN2; NVN1 reserved 0xC0)

Size evidence:
- `nvnProgramInitialize` (sdk+0x5bf2c0) and `nvnProgramInitializeFromBuilder` (sdk+0x5c1730) zero exactly 20 qwords (0x00..0x9f) and touch nothing above.
- MKW keeps programs in 0xA0-byte slots: the object built at main+0x16d6cf0 initialises three programs at object+0x18, +0xb8 and +0x158 (stride 0xa0), each with `ProgramBuilderSetDefaults`/`SetDevice`/`SetShaders(2)`/`InitializeFromBuilder`.
- The host's `prog_t` (`nvn_obj.h`: magic, nstage, `NVNshaderData sd[6]`, serial = 0x70 bytes) still fits, but its FITS assert compares against nvn.h's 0xC0. For NVN2 the bound is **0xA0**: an 8-stage `sd[8]` variant (0x90) still fits; anything above 0xA0 overwrites the next program in MKW's arrays.

| Offset | Type | Meaning | Evidence |
|---|---|---|---|
| +0x00 | u8 | value returned by `nvnProgramGetSubtileSize`; cleared by every SetShaders; no writer found | sdk+0x5c0910 (`ldrb w0,[x0]`), sdk+0x5bf8b0 clears it |
| +0x08 | NVNdevice* | owning device | Initialize, InitializeFromBuilder (builder+8) |
| +0x10 | u32 | program serial: device+0x1db0 atomically incremented on every SetShaders / ray-tracing link (bind-change detection [INFERENCE]) | sdk+0x5bf8b0, sdk+0x5c0af0 |
| +0x14 | u32 | word count of the prebuilt bind command stream at +0x18 | sdk+0x5f6b60 |
| +0x18 | u32* (heap) | prebuilt GPU method stream that BindProgram replays; for compute it starts with a 0x70-byte compute descriptor (its +0x20 = control+0xa8, +0x32 = control+0xba, +0x60 = control+0x10) | sdk+0x5f6b60, sdk+0x5c1820, sdk+0x5c09b0 |
| +0x20 | heap, 8 × 0x38 | per-graphics-stage table (entry +0x20 register count, +0x24 word offset into +0x18, +0x28 control+0x10, +0x30 flags); 0 for compute programs | sdk+0x5f6b60 |
| +0x28..+0x50 | ptr[6] | subroutine linkage map per stage 0..5 (`program + 0x28 + 8*stage`); +0x58..+0x68 are also cleared by SetShaders | sdk+0x5c0500, sdk+0x5bf8b0 |
| +0x78 | heap 16 B | {debugger shader table ptr (set elsewhere), array of {ptr, u32 size} copies of each stage's control debug blob} | Initialize, sdk+0x5bf8b0, sdk+0x609910 |
| +0x80 | u64 | bits 0–8: stage mask (bit = control+0x56 stage, bit 8 = ray tracing); 9–17: stages that use subroutines (control+0x94); 18–26: stages with a debug blob (control+0xcc ≠ 0); 27: shaders accepted; 28–31: sample-shading mode; 32–36: written by sdk+0x5f6b60 (meaning not established) | sdk+0x5bf8b0, sdk+0x5c08f0, sdk+0x5c0500 |
| +0x88/+0x90 | list links | membership in device+0x9a0 program list (device mutex +0x9b0) | Initialize, Finalize |
| +0x98 | heap 0xa8 | ray-tracing pipeline object (only when the builder had ray-tracing modules) | sdk+0x5c0af0 |

### NVNprogramBuilder (0x80 bytes, new in NVN2)

Size: SetDefaults (sdk+0x5c0920) writes +0x18..+0x63; getters read up to +0x60; MKW zeroes 0x80 bytes of stack before SetDefaults at both builder sites (main+0x1675cb4: sp+0x150..0x1cf; main+0x16d6d10: sp+0x20..0x9f). SetDefaults does not clear +0x08 (device).

| Offset | Type | Meaning | Default | Evidence |
|---|---|---|---|---|
| +0x08 | NVNdevice* | device | (not cleared) | SetDevice sdk+0x5c0950 |
| +0x18 | int | sample shading mode (copied into program+0x80 bits 28–31) | 0 | sdk+0x5c0970, sdk+0x5c0af0 |
| +0x1c | int | debug-data-hash count | 0 | sdk+0x5c0990 |
| +0x20 | ptr | array of 24-byte debug-data-hash records, record *i* belongs to shader *i* | 0 | sdk+0x5c0990, sdk+0x5c0af0 |
| +0x28 | int | shader count | 0 | SetShaders sdk+0x5c09d0 |
| +0x30 | const NVNshaderData* | shaders (16-byte {code GPU address, control CPU pointer}) | 0 | sdk+0x5c09d0 |
| +0x38 | int | ray-tracing module count | 0 | sdk+0x5c0a00 |
| +0x40 | const NVNshaderData* | ray-tracing modules (16-byte records; their controls have stage 8) | 0 | sdk+0x5c0a00, sdk+0x5c0af0 |
| +0x48 | int | subroutine linkage count | 0 | sdk+0x5c0a10 |
| +0x50 | const NVNsubroutineLinkageMapPtr* | linkage maps | 0 | sdk+0x5c0a10 |
| +0x58 | int | max trace recursion depth | 2 | sdk+0x5d2970 |
| +0x5c | int | max callable recursion depth | 2 | sdk+0x5d2990 |
| +0x60 | u32 | program ray-tracing flags (low 2 bits used) | 0 | sdk+0x5d29b0, sdk+0x5c0af0 |

The 24-byte debug-data-hash record matches GLSLC's `GLSLCdebugDataHash` shape: 16-byte build ID followed by the 64-bit debug hash (lo32, hi32) [INFERENCE from the size and from use: the driver only uses the third qword]. The same record type is what the third qword of NVN's 24-byte `NVNshaderDataExt` points to (sdk+0x5c03f0 splits the ext records into a 16-byte `NVNshaderData` array and an array of these pointers).

### Function table: Program

| Function | sdk impl | main uses | NVN1 ABI | Semantics |
|---|---|---|---|---|
| nvnProgramInitialize | 0x5bf2c0 | 2 (main+0xd121dc, +0x1675304) | same `(NVNprogram*, NVNdevice*)`, returns true | zero 0xa0, store device, link into device program list, allocate the 16-byte +0x78 record |
| nvnProgramInitializeFromBuilder | 0x5c1730 | 3 (main+0x1675d2c, +0x16d6da8, +0x16d7ad0) | new `(NVNprogram*, const NVNprogramBuilder*)` → NVNboolean | Initialize, then sample shading, then either ray-tracing link (when builder+0x40 ≠ 0; graphics shaders are then ignored) or the SetShaders core with builder shaders + per-shader hash records, then subroutine linkage like SetSubroutineLinkage (sdk+0x5c0af0) |
| nvnProgramFinalize | 0x5bf370 | 5 (main+0x6f16f0, +0x1675dc4, +0x16d7198, +0x16d7c90, +0x2281f8c) | same | under global mutex sdk+0xd96758: free +0x18/+0x20, unlink from device list, free debug-blob copies and +0x78, destroy the ray-tracing object through the device ray-tracing backend (device+0x1df0 vtable +0x140) |
| nvnProgramSetShaders | 0x5bf8a0 | 3 (main+0x6f1548, +0xd12320, +0x1675484) | same `(NVNprogram*, int, const NVNshaderData*)` → NVNboolean | clears x3 and tail-calls the core sdk+0x5bf8b0 without hash records |
| nvnProgramSetShadersExt | 0x5c03f0 | 1 (main+0x16756e0) | same name as NVN1 (no nvn.h prototype): `(NVNprogram*, int, const NVNshaderDataExt*)` → NVNboolean; record = {u64 code, const void *control, const debugDataHash *hash}, at most 9 records (stack arrays) | splits the records and calls the core with the hash pointers |
| nvnProgramSetSubroutineLinkage | 0x5c0500 | 0 | same `(NVNprogram*, int, const NVNsubroutineLinkageMapPtr*)` | each map's int at +4 is a stage ≤ 5 that must be in the "uses subroutines" mask; stored at program+0x28+8·stage; true only when every subroutine stage got a map |
| nvnProgramSetSampleShading | 0x5c08f0 | 0 | NVN1 name (no nvn.h prototype): `(NVNprogram*, NVNsampleShadingMode)` | stores mode in program+0x80 bits 28–31; takes effect at the next SetShaders: 1 forces per-sample fragment shading on, 2 forces it off, 0 keeps the control's flag (control+0x5a) |
| nvnProgramGetSubtileSize | 0x5c0910 | 0 | NVN1 name, no nvn.h prototype; NVN2 implementation takes only the program and returns one byte | returns program+0x00 (0 after every SetShaders; no writer found) |
| nvnProgramSetDebugLabel | 0x5b0b40 | 0 | same; **no-op** (`ret`) | — |
| nvnProgramGetDeviceComputeMemorySize | 0x5c1820 | 0 | new `(const NVNprogram*)` → int | compute programs whose control+0xa8 == 1: `(control+0xba u16 + 0x22f) & 0x1ff00`; otherwise 0 (memory a device-side compute launch needs, see DeviceCommandBuffer [INFERENCE]) |
| nvnProgramGetScratchBytesPerWarp | 0x5d11d0 | 0 | new `(const NVNprogram*)` → int | 0 without a ray-tracing object; otherwise `max(alignDown(max(stack,0x400)·32 + 0x65e0, 0x200), DeviceGetInteger(info 0x94, or 0xa0 when device+0x1e00 ≠ 0))`, stack = backend attribute 9 |
| nvnProgramGetShaderGroupHandle | 0x5d10b0 | 0 | new `(const NVNprogram*, {int i0,i1,i2} by value in x1:x2, void *handle32)` | ray-tracing only: looks up up to three module indices (−1 = none) in RT object +0x38, asks the backend (device+0x1df0 vtable +0x128) for a 32-byte shader-group handle, caches it in a map under RT object mutex +0x88 |
| nvnProgramGetDebugDataHashesNVX | 0x5c09b0 | 0 | new `(const NVNprogram*)` → u64 | compute programs only: the u64 at compute descriptor +0x60, which is control+0x10; 0 otherwise |

### Function table: ProgramBuilder (all new in NVN2, 0 main uses unless noted)

| Function | sdk impl | main uses | Semantics |
|---|---|---|---|
| SetDefaults | 0x5c0920 | 3 (main+0x1675cdc, +0x16d6d5c, +0x16d7a84) | see layout defaults |
| SetDevice / GetDevice | 0x5c0950 / 0x5c0960 | 3 / 0 | +0x08 |
| SetShaders / GetShaders / GetShaderCount | 0x5c09d0 / 0x5c09e0 / 0x5c09f0 | 3 / 0 / 0 | `(builder, int count, const NVNshaderData*)`; getters return +0x30 / +0x28 |
| SetSampleShading / GetSampleShading | 0x5c0970 / 0x5c0980 | 0 | +0x18 |
| SetDebugDataHashes / GetDebugDataHashes | 0x5c0990 / 0x5c09a0 | 0 | `(builder, int count, const hash24*)`; getter returns only the pointer (+0x20) |
| SetRayTracingModules | 0x5c0a00 | 0 | `(builder, int count, const NVNshaderData*)` (no getter exists) |
| SetSubroutineLinkage / GetSubroutineLinkage / GetSubroutineLinkageCount | 0x5c0a10 / 0x5c0a20 / 0x5c0a30 | 0 | +0x48/+0x50 |
| SetMaxTraceRecursionDepth / Get… | 0x5d2970 / 0x5d2960 | 0 | +0x58 (default 2) |
| SetMaxCallableRecursionDepth / Get… | 0x5d2990 / 0x5d2980 | 0 | +0x5c (default 2) |
| SetProgramRayTracingFlags / Get… | 0x5d29b0 / 0x5d29a0 | 0 | +0x60; bits 0–1 go to bits 8–9 of the link descriptor word |

`SetDefaults` writing `0x200000002` at +0x58 is two ints: max trace recursion depth 2, max callable recursion depth 2.

### MKW's program call sequences

- Builder path (3 sites): zero 0x80 bytes → `ProgramBuilderSetDefaults(b)` → `SetDevice(b, dev)` → `SetShaders(b, n, sd)` (n = 2 vertex+fragment at main+0x16d6d94; n = w22 at main+0x1675d10) → `InitializeFromBuilder(prog, b)`, result compared with 1 (main+0x1675d38). The source records store {control, code} and main swaps them into {code, control}.
- Legacy path: main+0x1675200 first checks the container's binary version with `DeviceGetInteger` 0x2f/0x2e (min/max major, both 0xc9) and 0x31/0x30 (min minor 0, max minor 11; sdk+0x5b57c0 jump table), then `ProgramInitialize(prog, dev)` → collects per-stage records for stage slots 0..8 (main+0x1675610 loop) → `SetShadersExt` (records {code, control, record+0x30 as hash pointer}) when the container's flag bit 1 is set, else `SetShaders` (16-byte records). main+0xd122f0: two shaders, code = `MemoryPoolGetBufferAddress(pool) + offset`, control = CPU pointer into the file.
- MKW never calls GetShaderGroupHandle, the ray-tracing builder setters, SetSubroutineLinkage or SetSampleShading.

### SetShaders core (sdk+0x5bf8b0): what the driver checks and does

1. Program serial +0x10 ← ++device+0x1db0; frees the previous debug-blob copies, the bind stream and per-stage table; clears +0x00, +0x28..+0x68, +0x80 bits 0–26.
2. For each shader: a parser object is created from its control (sdk+0x5bf500): control+0x04 must be 0xc9, control+0x08 selects the class (0–1 → vtable sdk+0xd6b980, 2 → +0xd6bda8, 3–11 → +0xd6c1d0; anything else fails). Stage = control+0x56 (u8).
3. Fails (returns false) when: a parser can't be made; stage == 8; a stage repeats; control is null or not 8-byte aligned; any code address is 0; control+0x04 ≠ 0xc9 or version > 11; version < 5 with control+0xd1 ≠ 0; a compute shader with control+0xa7 == 0 on a device whose flag bit 18 (device byte +0x0a bit 2) is set; or any stage whose control u16 +0x0c / +0x0e differs from device u32 +0x54 / +0x58.
4. device+0x54 / +0x58 are the GPU architecture and implementation ids: they are words +0x44 / +0x48 of the 0x10c-byte GPU-characteristics block at device+0x10, which sdk+0x5bf090 fills from `NvRmGpuDeviceGetInfo()` (info +0xd0 and +0xd4) and sdk+0x5b4450 copies in (`memcpy(device+0x10, block, 0x10c)`). Every MKW control carries 0x0170 / 0x000e there, so a working console reports arch 0x170 (Ampere family), impl 0xe [values from the data; the hardware query itself not observed].
5. Code GPU address → CPU pointer through the device pool table (device+0x968/+0x970, mutex +0x980). Debug blobs (control+0xc8 offset, +0xcc size) are copied to the heap.
6. Sample-shading override for the fragment stage, then the per-stage record P (sdk+0x5c0598), the debugger shader table entry (sdk+0x609910), a compute scratch override (sdk+0x6098a0, only when debug globals sdk+0xd97470/+0xd97474 are set), and the bind command stream (sdk+0x5f6b60) built from the last pre-raster stage (geometry, else tess eval, else tess control, else vertex) plus all stage records.
7. **In-place code patch (real driver only):** when the CPU mapping exists, the driver walks backwards from `code + (control+0x38 & ~0xf)`; if it finds ≥ 8 NOPs (opcode low 9 bits 0x118) preceded by a BRA (0x147), it rewrites the last 8 instruction slots with `MOV RZ, imm32` no-ops carrying 0x12345678, control+0x38, hash.lo32, hash.hi32, stage, 0, 0, 0 (constants at sdk+0xac4e10 / +0xac6ca0 / +0xac4520). The ray-tracing path does the same per module. Semantically a no-op, but GPU memory after a real driver run differs from the file bytes in those 0x80 bytes.

### Control section (fixed header 0xd8 bytes for versions 3–11, 0xd0 for 0–2)

The class's slot 0 returns the header size (0xd0 classes 0/1, 0xd8 class 2). Every other slot is a 3-instruction getter reading one field. Only the slots below are called by the driver (sdk+0x5bf6d4, +0x5c1f10, +0x5c2078, +0x5bf8b0, +0x5c0af0). "MKW" = values seen in the 11,567 MKW controls (versions 7 and 11 only).

| Offset | Type | Getter slot | Driver use | MKW values / meaning |
|---|---|---|---|---|
| +0x00 | u32 | 0x08 | copied to S+0, not checked | 0x98761234 magic |
| +0x04 | u32 | 0x10 | must be 0xc9 | 0xc9 |
| +0x08 | u32 | 0x18 | parser class, ≤ 11 | 7, 11 |
| +0x0c | u16 | 0x20 | must equal device+0x54 | 0x170 |
| +0x0e | u16 | 0x28 | must equal device+0x58 | 0xe |
| +0x10 | u64 | 0x30 | U+0x68 → P+0x38 → per-stage table +0x28; compute: returned by GetDebugDataHashesNVX | 0 |
| +0x18 | u64 | 0x38 | U+0x78 → P+0x80 | 0 (BAHS) or flag-like values such as 0x00288040_00220014 (meaning not established) |
| +0x20 | 16 B | 0x40/0x48 | **not read** | debug hash (0 in BAHS copies) |
| +0x30 | u32 | 0x50 | not read | total control length incl. trailing blobs (0xd8, 0x750, …) |
| +0x38 | u32 | 0x60 | P+0x18; end of the code patch window | end of instructions within the code area (graphics: header included) |
| +0x3c | u32 | 0x68 | P+0x1c | c1 constant-segment size |
| +0x40 | u32 | 0x70 | P+0x14 | c1 constant-segment start |
| +0x44 | u32 | 0x78 | P+0x30 | code-area size |
| +0x48 | u32 | 0x80 | P+0x10 → program address = code + this | 0 (entry offset) |
| +0x4c | u32 | 0x88 | P+0x28 → register count in the bind stream (clamped to device+0x1d20) | register count |
| +0x50 | u32 | 0x90 | P+0x2c | local (scratch) memory size; 0 / 0x200 / 0x2600 / 0x4000 |
| +0x54 | u8 | 0x98 | transform-feedback buffer count (≤ 4) | 0 |
| +0x55 | u8 | 0xa0 | copied next to it (S+0x621; count at S+0x620) | 0 |
| +0x56 | u8 | 0xa8 | stage | 0 vertex 1 fragment 2 geometry 3 tess control 4 tess eval 5 compute 6 task 7 mesh 8 ray tracing |
| +0x58..+0x77 | union | 0xb0..0x2e8, 0x248 | per stage, below | |
| +0x7c..+0x88 | u32[4] | 0x2f0(i) | U+0x58..+0x64 | 0 |
| +0x8c | u32 | 0x2f8 | U+0x70 | 0 |
| +0x90, +0x91 | u8 | 0x300, 0x308 | P+0x40, P+0x41 | 0 |
| +0x92, +0x93 | bool | 0x310, 0x318 | S+0x6d0, S+0x6d1 | 0 |
| +0x94 | bool | 0x320 | stage uses subroutines (program+0x80 bit 9+stage) | 0 |
| +0x98, +0x9c | u32 | 0x328, 0x330 | not read | +0x98 = control length or 0 |
| +0xa0, +0xa1 | bool | 0x338, 0x340 (= a0 && a1) | ray-tracing path | 0 |
| +0xa2..+0xa6 | bool | 0x348..0x368 | U+0x81..0x85 → P+0x88 bits 0–2; RT object +0x50 bits 0–4 | +0xa3 = 1 in 319 controls; +0xa6 = 1 in 91 compute controls |
| +0xa7 | bool | 0x370 | selects the transform-feedback encoding; compute must have it when device flag bit 18 is set | 1 in all 11,567 |
| +0xa8 | u8 | 0x378 (compute) | U+0x40; ==1 enables GetDeviceComputeMemorySize | 1 in all 728 compute controls |
| +0xa9..+0xb0 | u8[8] | 0x380(i) (compute) | U+0x41..0x48 | +0xa9 = 8 in 30 compute controls, else 0 |
| +0xb1..+0xb8 | u8 | 0x388..0x3c0 (compute) | U+0x49..0x50 | |
| +0xba | u16 | 0x3c8 (compute) | U+0x52 → GetDeviceComputeMemorySize | 0x38..0x80 |
| +0xbc, +0xc0 | u32 | 0x3d0, 0x3d8 | not read | offset/size of a trailing blob (0xd8 / 0x260..0xdc0) |
| +0xc4 | u32 | 0x3e0 | offset of the transform-feedback blob (read only when +0x54 ≠ 0) | 0xd8 |
| +0xc8, +0xcc | u32 | 0x3e8, 0x3f0 | debug blob offset / size (copied when size ≠ 0) | offset 0xd8 / size 0 |
| +0xd0 | u8 | 0x3f8 (v3+) | U+0x86; compute U+0x51 | 0 |
| +0xd1 | u8 | 0x400 (v3+, must be 0 below v5) | U+0x87 | 0 |

Stage union (+0x58..+0x77):

| Stage | Fields the driver reads | MKW observation |
|---|---|---|
| 0 vertex | u32 +0x58 +0x5c +0x60 +0x64 +0x68 +0x6c. When +0x5c ≠ 0 and +0x68 ≠ 0 the driver builds a second program record: first part entry +0x58 / end +0x5c, second part entry +0x64 / end +0x68 / registers +0x6c (sdk+0x5c0598) | all 0 |
| 1 fragment | bools +0x58..+0x60, u8 +0x61, u8 +0x62, 16 bytes +0x64; **+0x5a = per-sample shading** (overridden by SetSampleShading) | +0x62 ∈ {1,2,3,6} (colour-target mask [INFERENCE]); +0x64.. floats 1.0 / 0.75 in 30 shaders |
| 2 geometry | u8 +0x58, bools +0x59 +0x5a, 32 bytes +0x5c | none in MKW |
| 3/4 tessellation | u32 +0x58..+0x74 | none in MKW |
| 5 compute | u32 +0x58 +0x5c +0x60 workgroup size x y z, +0x64 shared-memory bytes, +0x68 +0x6c +0x70 (inputs of the scratch override: scratch = +0x70 + device[+0xc4]·(+0x68 + +0x6c)), +0x74 | x ∈ {4..128}, shared 0..0xc000, +0x74 ∈ {0,1} |
| 6 task | u16 +0x58 +0x5a +0x5c | +0x58 = 32 (workgroup size), +0x5a = 0x100 or 0x580 (payload bytes [INFERENCE]) |
| 7 mesh | u16 +0x58 +0x5a +0x5c, u8 +0x5e +0x5f, u16 +0x60 +0x62 +0x68, u32 +0x64 | +0x58 workgroup size (8/32), +0x5a = matching task payload size, +0x5f = 3, +0x60/+0x62 = 128/64, 32/16, 256/192 (max vertices / primitives [INFERENCE]), +0x64 shared bytes |
| 8 ray tracing | u32 +0x58 (link flags), +0x5c size and +0x60 offset of a blob inside the control, +0x64 code end, u8 +0x6c..+0x6f, u8 +0x70..+0x76 | only via ProgramBuilderSetRayTracingModules |

Transform-feedback blob (control + [+0xc4], only when +0x54 ≠ 0): u32 format = 1, then per buffer u16 stride, u8 stream, u8 varying count, then per buffer three byte arrays of that count; sdk+0x5f6b60 turns it into the 3D-engine transform-feedback layout methods (byte method 0x700 + 0x10·buffer) and per-buffer varying locations. MKW never sets it.

### Which bytes the host can key a translated shader on

The key the shader split uses, **sha256(control[0:0xd8] + code[0 : control+0x44])[:16], is confirmed** for MKW:
- 0xd8 is exactly the fixed header the driver parses for versions 3–11 (class slot 0); MKW has only versions 7 and 11.
- The code bytes start at `NVNshaderData.data` (the code-area start: the 0x80-byte program header for graphics stages, the first instruction for compute); every code offset the driver uses (+0x38, +0x40, +0x44, +0x48) is relative to that address.
- Bytes in the key the driver ignores: +0x20..+0x2f (debug hash) and +0x30/+0x98/+0xbc..+0xcc (blob bookkeeping). The same program copied in a BNSH and a BAHS container therefore gets two keys; harmless because the offline split emits every copy.
- Bytes outside the key that change behaviour: only the transform-feedback blob (control+0x54 ≠ 0), absent in MKW. If a title uses it, add `control[+0xc4 : …]` to the key.
- Hash at SetShaders/InitializeFromBuilder time, on the bytes the game passes. The real driver patches the last 0x80 bytes before control+0x38 (see core step 7); dumps of GPU memory taken after a real driver ran will not match file-based keys. nxnative replaces the driver, so nothing patches.

### Ray-tracing link (InitializeFromBuilder with modules)

RT object (0xa8 bytes at program+0x98): +0x00 backend pipeline handle; +0x30 u32 module count; +0x38 per-module handles (count+1)·8; +0x40 u32 count; +0x48 module info array (0x28 each: blob size = control+0x5c, blob pointer = control + [control+0x60], code end = control+0x64, CPU and GPU code addresses); +0x58 concatenated 8-aligned blobs; +0x60 per-module backend data (count × backend attribute 0x19); +0x68 per-module debug hash (record +0x10); +0x70/+0x71/+0x72 control bytes +0x6c/+0x6d/+0x6e of module 0; +0x73 max trace recursion depth; +0x74 link flags (control+0x58 with bits 8–9 = builder RT flags, bit 0 set); +0x78 max callable recursion depth; +0x80 group-handle cache, +0x88 mutex. The link goes through the device's ray-tracing backend (device+0x1df0 vtable, object device+0x1df8; +0x138 link, +0x150/+0x158/+0x148 queries, symbol "descriptorBaseArray"). Program+0x80 bit 8 marks it. [Metal: MTLLinkedFunctions / intersection function tables would be the counterpart [INFERENCE]; MKW does not use it.]

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

- Keep `nvnPrivate0` at 0 (never let the real GetProcAddress set it); then MKW only uses the proc pointers and never touches command/control memory internals itself. Only the byte at +0x10 is written regardless. The host checks it at device creation (`nvn2_device.c`).
- `cmd_t` still fits; the handle format is the host's own as long as `nvnPrivate0 == 0` (main only decodes handles in gated code, e.g. main+0x2b720). The host's handles are tag 0 ([Host implementation](#host-implementation)).
- `PopDebugGroup` and `InsertDebugMarker` are no-ops in hle_nvn.c, so their changed ABI is safe; an NVN2 implementation of either would replace the no-op (`NVN2()`).
- Deferred binds/pushes must read their array **at submit time**, not at record time.
- `SetCommandMemoryCallbackEnabled(false)` (MKW does this) means: when command memory runs out, the driver neither calls back nor aborts. The host continues such a recording in a host-lent segment (`cmd_t.cb_off`).
- `PushGraphicsConstants(cmd, slot, offset, size, data)` writes Ampere methods 0x504 / 0x508 (SET_ROOT_TABLE_SELECTOR / LOAD_ROOT_TABLE, header 0xA0010141); SM86 graphics programs read slot k as constant bank 24 + k. MKW's one call (main+0x16d72e8) pushes {1/W, 1/H, W, H} into slot 0, offset 0, 16 bytes, and the five MKW programs that read bank 24 read exactly bytes 0..0xC ([rendering.md](rendering.md)).
- `SetGeometryProcessingMode` brackets mesh draws: 1 or 2 before `DrawMeshTasks`, 0 after (main+0x5d6308..0x5d647c, main+0x403d0c..0x403db0). `ShadingRateImagePalette(cmd, viewport, count, rates)`: main+0x403480..0x4034f0 sets viewports 0–4 with 16 entries in NV_shading_rate_image order (5 = 1×1); at run time MKW binds a non-NULL shading-rate image and uses rate 8 (2×2).

### Open questions

- Meaning of geometry processing modes 1 vs 2, conservative raster mode values, shading-rate palette mode, compute overlap mode values.
- Whether NVNshaderStage 6/7 are task/mesh in that order; meaning of stage 8 (ray tracing assumed).
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
- MKW's queue loop (main+0x8f70c4) passes flags 1 to slot 0, while slots 1–2 use flag 4 with SetPrimaryQueue(obj+8). The init requires the primary to have flag 0x8. At run time main calls `SetPrimaryQueue` and creates five queues in all, so subcontext queues are in use; which queue obj+8 is (the flags-9 queue of main+0xcf4034?) was not traced.
- The units of the timeslice values (0x80 / 0x400 / 0x3fc00) and of the nvnQueueSetTimesliceNVX argument before scaling by device-info +0x2ec/+0x2e8.
- Whether the NVN2 header's reserved sizes are exactly MKW's allocation sizes (queue 0x3000, window 0x280, window builder 0x80, sync 0x40, event 0x40). Queue builder reserve is 0x48..0x50; event builder reserve was not established.
- Window builder +0x24 (color space) is stored but not read by nvnWindowInitialize. The wsi color space follows the texture format instead. Is it read elsewhere?
- Exact meaning of the texture flag that triggers the present-time "make displayable" pass (texture+0x28 bit 3, backend+0xc = 1).
- The final presented size MKW uses docked: static default 2560×1440 (main+0xcf3574) versus report E3's 3840×2160 images cropped to 2560×1440. The resolution table at main+0x2a16174 suggests a runtime choice; trace main+0xe33e50 / main+0x2323270 to settle it.
- nvnSyncGetNvRmSyncNVX: the exact meaning of its second argument (passed in the attribute block of `NvRmSyncCreateFromSyncpointsWithAttr`, closed for unfenced syncs).

## Dlss

All 54 `nvnDlss*` functions and `nvnCommandBufferEvaluateDlss` have **0 static uses in main**, and their main GOT entries (main+0x3305268..+0x3305418) are referenced only by the relocation table. MKW's `DeviceBuilderSetFlags` value is 0 or one of {0x1, 0x4, 0x10, 0x20, 0x40} (main+0xcf3aa4, table main+0x292b750 via main+0x226d8f8), never bit 10 (0x400): device init (sdk+0x5b54e0) only loads the DLSS backend when device flags bit 10 is set (sdk+0x5b869c loads module "dlss" (sdk+0xab1dff) and calls its "Handshake" export; the backend object lives at sdk+0xedb1d8). **MKW does not use DLSS.** Without the backend, Initialize/Evaluate would dereference a null backend.

Backend vtable: +0x00 init(device), +0x08 shutdown, +0x10 flush/finish, +0x18 optimal settings, +0x20 create feature, +0x28 destroy feature, +0x30 evaluate, +0x38 post-init. Calls are serialised by mutex sdk+0xedb180 with the current network/parameters/command buffer published in globals sdk+0xedb1a0..+0xedb1b8.

### NVNdlssNetworkBuilder (0xC0 bytes)

SetDefaults (sdk+0x5b9aa0) writes +0x08..+0xbf (device at +0x00 is not cleared).

| Offset | Type | Meaning | Default |
|---|---|---|---|
| +0x00 | NVNdevice* | device | — |
| +0x08 | const NVNtexturePool* | pool that receives DLSS's descriptors (SetTextureIDs arg 2) | 0 |
| +0x10 / +0x18 | void* / size_t | storage memory / size | 0 |
| +0x20 | u32 | flags (bit 0 inverted into the feature flags; bits 1,2,3,6 passed through; bit 28 → output subrect base honoured; bit 29 changes the storage formula) | 0 |
| +0x24 | int | method (1 vs other changes storage size; passed to optimal settings) [quality/perf mode, INFERENCE] | 0 |
| +0x28 | int | preset (8 needs more storage) | 0 |
| +0x2c / +0x30 | int | output width / height | 0 |
| +0x34 | int | max evaluation entries (each reserves 0x10000 bytes of storage) | 1 |
| +0x38..+0xb7 | int[32] | texture IDs (only IDs 0–5 are used by Evaluate: input colour, depth, motion vectors, bias mask, output, exposure) | 0 |
| +0xb8 | u64 | cleared, unused | 0 |

### NVNdlssNetwork (0x218 bytes)

`nvnDlssNetworkInitialize` (sdk+0x5b8bd0) clears exactly 0x218 bytes. +0x08 device, +0x10 texture pool, +0x18..+0x97 texture IDs, +0x98 = 0x3f, +0xa0 backend feature handle, +0xa8 device, +0xb0/+0xb8 storage cursor/remaining, +0xc8 an embedded NVNmemoryPool (0x100 bytes, flags 0x21) over the first page-aligned `maxEntries·0x10000` bytes of the storage, +0x1c8 max entries, +0x1d4/+0x1dc method, +0x1d8 feature flags, +0x1e0 preset, +0x1e4/+0x1e8 and +0x1f8..+0x204 six optimal-settings outputs, +0x1ec/+0x1f0 and +0x208/+0x20c output size, +0x1f4 flags bit 28, +0x210 builder flags. Storage is zero-filled at Initialize.

### NVNdlssParameters (0xC8 bytes)

SetDefaults (sdk+0x5b9c20) writes 0..0xc7: all 0 except +0x58/+0x5c = 1.0f, +0x60 = 1.0f, +0xb0..+0xbf = −1.

| Offset | Field (setter) |
|---|---|
| +0x00/+0x08 | input colour (texture, view) |
| +0x10/+0x18 | output colour (texture, view) |
| +0x20/+0x28 | motion vectors (texture, view) |
| +0x30/+0x38 | depth (texture, view) |
| +0x40/+0x48 | bias-current-colour mask (texture, view) |
| +0x50/+0x54 | jitter offset x, y (float) |
| +0x58/+0x5c | motion-vector scale x, y (float, default 1) |
| +0x60 | pre-exposure (float, default 1) |
| +0x68/+0x70 | exposure (texture, view) |
| +0x78 | history reset (bool) |
| +0x7c | evaluation index |
| +0x80/+0x84 | input subrect size (0,0 → input texture's width +0x20 / height u16 +0x24) |
| +0x88, +0x90, +0x98, +0xa0, +0xa8 | subrect bases (x, y) of input colour, depth, motion vectors, bias mask, output |
| +0xb0, +0xb4, +0xb8, +0xbc | overlay debug layer, full screen, show NaNs, jitter debug (default −1) |
| +0xc0 | u64, cleared, no setter |

The (texture, view) pairs go to sdk+0x6fd0b0, which builds a texture descriptor from both [INFERENCE: `const NVNtexture*`, `const NVNtextureView*`].

### Evaluation (`nvnCommandBufferEvaluateDlss`, sdk+0x5b93d0)

`(NVNcommandBuffer*, const NVNdlssNetwork*, const NVNdlssParameters*)`. Recorded into the command buffer, not a device call:
1. a 0x120-byte capture record (opcode 0x59, closed by 0x5a);
2. for each non-null texture: a descriptor is built and written by the command buffer (sdk+0x6ecbf0, 0x20 bytes) into texture-pool slot `ID[k]` at `pool memory GPU address + pool offset + 32·ID`;
3. marker method 0xa0032e8a with render size and output size (skipped in command-buffer mode 2);
4. backend evaluate (vtable +0x30), which records DLSS's compute passes into the same command buffer, then +0x10;
5. closing marker 0x80002e8a.
Exposure texture is used only when feature flags bits 0 and 6 are clear.

Metal mapping [INFERENCE]: a network ≈ `MTLFXTemporalScaler` created with input size (optimal render size), output size, colour/depth/motion formats; Parameters map to the scaler's colour/depth/motion/output/exposure textures, `jitterOffsetX/Y`, `motionVectorScaleX/Y`, `preExposure`, `reset`, `inputContentWidth/Height` (subrect size); the subrect bases need a blit or texture views. Encode it at the position of the EvaluateDlss call.

### Function table: Dlss (all new, 0 main uses)

| Function | sdk impl | Semantics |
|---|---|---|
| nvnCommandBufferEvaluateDlss | 0x5b93d0 | see above |
| nvnDlssNetworkInitialize | 0x5b8bd0 | `(network, const builder*)` → bool (backend handle ≠ 0) |
| nvnDlssNetworkFinalize | 0x5b8e40 | destroy feature, finalize embedded pool |
| NetworkBuilderSetDefaults | 0x5b9aa0 | layout above |
| NetworkBuilderSetDevice / GetDevice | 0x5b9ae0 / 0x5b9af0 | +0x00 |
| NetworkBuilderSetFlags / GetFlags | 0x5b9b00 / 0x5b9b10 | +0x20 |
| NetworkBuilderSetOutputResolution / Get… | 0x5b9b20 / 0x5b9b30 | `(b, int w, int h)` / `(b, int*, int*)` |
| NetworkBuilderSetMethod / GetMethod | 0x5b9b50 / 0x5b9b60 | +0x24 |
| NetworkBuilderSetPreset / GetPreset | 0x5b9b70 / 0x5b9b80 | +0x28 |
| NetworkBuilderSetStorage | 0x5b9b90 | `(b, void*, size_t)` |
| NetworkBuilderGetMemory / GetMemorySize | 0x5b9ba0 / 0x5b9bb0 | +0x10 / +0x18 |
| NetworkBuilderSetTextureIDs | 0x5b9bc0 | `(b, const NVNtexturePool*, const int ids[32])` |
| NetworkBuilderSetMaxEvaluationEntries / Get… | 0x5b9c00 / 0x5b9c10 | +0x34 |
| NetworkBuilderGetOptimalSettings | 0x5b8de0 | `(b, int* ×6)` → backend +0x18 with output size and method [outputs: optimal, then two more render-size pairs, INFERENCE] |
| NetworkBuilderGetStorageSize | 0x5b8ee0 | size_t from output size, preset, method, flags bit 29, max entries; needs the backend loaded (reads backend+8 bit 1) |
| ParametersSetDefaults | 0x5b9c20 | layout above |
| ParametersSetInputColor / SetOutputColor / SetMotionVectors / SetDepth / SetBiasCurrentColorMask / SetExposureTexture | 0x5b9c60 / 0x5b9c70 / 0x5b9cc0 / 0x5b9cd0 / 0x5b9ce0 / 0x5b9d40 | `(p, texture, view)` |
| ParametersSetOverlayDebugLayer / FullScreen / ShowNans / JitterDebug | 0x5b9c80 / 0x5b9c90 / 0x5b9ca0 / 0x5b9cb0 | int at +0xb0/+0xb4/+0xb8/+0xbc |
| ParametersSetJitterOffset / Get | 0x5b9cf0 / 0x5b9d00 | `(p, float, float)` (floats in s0/s1) / `(p, float*, float*)` |
| ParametersSetPreExposure / Get | 0x5b9d20 / 0x5b9d30 | `(p, float)` |
| ParametersSetHistoryReset / Get | 0x5b9d50 / 0x5b9d60 | bool +0x78 |
| ParametersSetMotionVectorScale / Get | 0x5b9d70 / 0x5b9d80 | `(p, float, float)` |
| ParametersSetEvaluationIndex / Get | 0x5b9da0 / 0x5b9db0 | int +0x7c |
| ParametersSetInputSubrectangleSize / Get | 0x5b9dc0 / 0x5b9dd0 | `(p, int, int)` |
| Parameters{Set,Get}InputColorSubrectangleBase | 0x5b9df0 / 0x5b9e00 | +0x88 |
| Parameters{Set,Get}DepthSubrectangleBase | 0x5b9e20 / 0x5b9e30 | +0x90 |
| Parameters{Set,Get}MotionVectorSubrectangleBase | 0x5b9e50 / 0x5b9e60 | +0x98 |
| Parameters{Set,Get}BiasCurrentColorSubrectangleBase | 0x5b9e80 / 0x5b9e90 | +0xa0 |
| Parameters{Set,Get}OutputSubrectangleBase | 0x5b9eb0 / 0x5b9ec0 | +0xa8 |

## DeviceCommandBuffer and DeviceMessageBuffer

No main uses for any of these (main GOT entries +0x3305450..+0x33054e8 referenced only by relocations).

### NVNdeviceCommandBuffer (≥ 0x30 bytes; driver touches 0x00..0x2f)

Memory the GPU itself writes commands into ("device-generated commands"), later executed as pushbuffer segments.

| Offset | Type | Meaning |
|---|---|---|
| +0x00 | NVNdevice* | SetDevice (sdk+0x5b6ae0) |
| +0x08 | const NVNmemoryPool* | SetStorage arg 2 |
| +0x10 | i64 | offset in the pool |
| +0x18 | u64 | size |
| +0x20 | u64 | command memory size (SetCommandMemorySize) |
| +0x28 | u64 | compute memory size (SetComputeMemorySize) |

- `GetHandle` (sdk+0x5b6b20): pool GPU address (pool+0x18) + offset, 0 without a pool.
- `CommandBufferBeginDeviceCommands(cmd, dcb)` (sdk+0x5b6b40): emits a 9-word method (0xa008?e9a family) with the handle, a 0x100-byte header, `align(cmdSize+0x1ff, 0x100)`, `cmdSize+0x100`, the larger of command and compute area ends, the segment count `ceil(cmdSize/0x7ffffc)` and 0x7ffffc.
- `CommandBufferEndDeviceCommands` (sdk+0x5b6c50): closes the generation phase (a wait/flush word chosen by command-buffer mode).
- `CommandBufferSubmitDeviceCommands` (sdk+0x5b6fc0): appends control-memory entries that execute `handle+0x100` as pushbuffer segments of at most 0x7ffffc bytes.
- `ProgramGetDeviceComputeMemorySize` gives the per-program compute memory this mechanism needs.
Host [INFERENCE]: implementing it means interpreting GPU-written NVN2 method streams; MKW never does it.

### NVNdeviceMessageBuffer (≥ 0x28 bytes): shader printf

| Offset | Type | Meaning |
|---|---|---|
| +0x00 | NVNdevice* | SetDevice (sdk+0x5b7370) |
| +0x08 | const NVNmemoryPool* | SetStorage arg 2 (sdk+0x5b7380) |
| +0x10 | int | offset in the pool |
| +0x14 | int | size |
| +0x18 | ptr | read base (BeginMessages) |
| +0x20 | u32 | read cursor (starts at 0xc) |
| +0x24 | u32 | read end (header word 2 if non-zero, else word 0) |

Buffer memory (pool CPU address + offset): header {u32 write offset, u32 capacity, u32 wrap end}; `Reset` (sdk+0x5b7390) writes {0xc, size, 0}. Records from +0xc: u32 record size, u16 source stage code, i16 at +6, four u32 at +8..+0x14, a NUL-terminated printf format at +0x18, then 4-byte-aligned arguments up to the record end. `GetNextMessage(dmb, int info[6], char *text, size_t textSize)` → bool (sdk+0x5b7430): info[0] = the i16, info[1] = NVN stage (source codes 0→0, 4→1, 3→2, 1→3, 2→4, 5→5, 0x1493→6, 0x1494→7, 0x14c1..0x14c6→8, else −1), info[2..5] = the four u32; formats `%d %i %u %x %X %o %f %e %g %a %lX…`, `%llu`, and vectors `%v2f`..`%v4f` (joined by ", "). `EndMessages` clears the cursor. Command-buffer side (CommandBuffer family): `BindDeviceMessageBuffer(cmd, stage, int, dmb)` binds the buffer's GPU address per stage (compute and stage 8 through deferred records), `ResetDeviceMessageBuffer[Deferred]`.

Host [INFERENCE]: a Metal implementation can keep the buffer empty (Reset header, GetNextMessage false); MKW never binds one.

| Function | sdk impl | Semantics |
|---|---|---|
| nvnDeviceCommandBufferSetDefaults | 0x5b6ac0 | clear +0x08..+0x2f |
| nvnDeviceCommandBufferSetDevice | 0x5b6ae0 | +0x00 |
| nvnDeviceCommandBufferSetStorage | 0x5b6af0 | `(dcb, pool, i64 offset, u64 size)` |
| nvnDeviceCommandBufferSetCommandMemorySize | 0x5b6b00 | +0x20 |
| nvnDeviceCommandBufferSetComputeMemorySize | 0x5b6b10 | +0x28 |
| nvnDeviceCommandBufferGetHandle | 0x5b6b20 | GPU address |
| nvnDeviceMessageBufferSetDefaults | 0x5b7360 | clear +0x08..+0x27 |
| nvnDeviceMessageBufferSetDevice | 0x5b7370 | +0x00 |
| nvnDeviceMessageBufferSetStorage | 0x5b7380 | `(dmb, pool, int offset, int size)` |
| nvnDeviceMessageBufferReset | 0x5b7390 | CPU-side header reset |
| nvnDeviceMessageBufferBeginMessages | 0x5b73d0 | start reading |
| nvnDeviceMessageBufferGetNextMessage | 0x5b7430 | format next record |
| nvnDeviceMessageBufferEndMessages | 0x5b7420 | stop reading |

## Interception, interop and other NVX names

None has a main use.

| Function | sdk impl | NVN1 | Semantics |
|---|---|---|---|
| nvnInterceptionInitializeNVX | 0x5b61c0 | new | `(void *table)`: clears a 0x15d0-byte table (698 pointers, sorted-name order), bumps refcount sdk+0xedb170; first caller sets up the pristine table and the default dispatch-reset callback (sdk+0x5b6958); returns true |
| nvnInterceptionSetProcAddressNVX | 0x5b6240 | new | `(table, const char *name, fn, PFN *original)`: name must start "nvn"; binary search of the 698 names; stores fn in the caller's table, returns the pristine entry in *original; false for an unknown name |
| nvnInterceptionSetDispatchResetCallbackNVX | 0x5b6350 | new | `(?, cb, *oldCb)`: replaces the global callback (first argument unused) |
| nvnInterceptionTryAcquireNVX | 0x5b6390 | new | `(table)`: atomically becomes owner (sdk+0xedb168) unless someone else is; rebuilds the active dispatch table (sdk+0xed9b48) = pristine table (sdk+0xedc940) overlaid with the caller's non-null entries |
| nvnInterceptionOwnedNVX | 0x5b6540 | new | owner == table |
| nvnInterceptionReleaseNVX | 0x5b6560 | new | owner → 0, active table restored from pristine |
| nvnInterceptionFinalizeNVX | 0x5b65e0 | new | refcount−− |
| nvnSetExternalDebuggerEnabledNVX | 0x5b61b0 | NVN1 name | tail-jumps through the pointer at sdk+0xd96740, whose default is a `ret` (sdk+0x5b6830): **no-op** unless a tool patches it |
| nvnSetGLInteropNVX | 0x5b5f60 | new | stores one pointer in sdk+0xedb160 (read back by sdk+0x5b6840) |
| nvnCUDAInteropFunction | 0x5cc4f0 | new | one entry point for CUDA interop: argument struct {u16 version (must be 1), u16, u32 result @+4, u32 opcode @+8 (0–9), args* @+0x10}; writes {1,1} into the first two u16; result 0 ok, 1 version mismatch, 2 failure, 3 unsupported (device+0x168 bit 3 clear for ops 7/8), 4 opcode > 9. Ops: 0 create the CUDA memory pool (device+0x1bf8, flags 0x412), 1 destroy it, 2/4 export buffer or pool addresses, 3 export a texture's layout, 5/6 sync objects, 7/8 queries, 9 a flag bit. Host: return 4 or 3 |
| H3ikr9ix | 0x5b5f70 | new (obfuscated) | `(fn)`: overwrites the default dispatch-table entry for `TexturePoolRegisterTexture` (sdk+0xd95168 table) with fn; affects later GetProcAddress results only |
| yK1NUEJH | 0x5b6020 | new (obfuscated) | `(const NVNtexturePool*, ptrdiff_t byteOffset, const uint32_t *words, size_t bytes)`: copies raw descriptor words into the pool's memory at pool CPU + pool offset + byteOffset; flushes 0x20 bytes when the pool is CPU-cached (pool flag bit 2). Companion of H3ikr9ix (raw descriptor registration) |
| nvnCommandBufferCopyTextureToTextureWithCopyEngineNVX | 0x5b0770 | new | `(cmd, const NVNtexture *src, const NVNcopyRegion *srcRegion, const NVNtexture *dst, const NVNcopyRegion *dstRegion)`: 2D copy with the copy (DMA) engine using src x,y,width,height and dst x,y (sdk+0x5ae530); no views, no flags. Metal: blit copy |
| nvnDeviceBuilderSetReservedAddressRangesNVX / Get… | 0x5b4230 / 0x5b4240 | new | `(builder, int count, const range*)` at builder+0x08/+0x10; Get returns the count and the pointer. Range = 24 bytes {u64 address, u64 size, u32 flags}; device init reserves each range in the GPU address space (sdk+0x5b54e0) |
| nvnProgramGetDebugDataHashesNVX | 0x5c09b0 | new | see Program table |

## Open questions: programs, DLSS, device commands and NVX names

- Who writes program+0x00 (`GetSubtileSize`); no writer in the decompiled set, so it reads 0 after SetShaders.
- Meaning of control +0x18 (u64, non-zero in BNSH copies), fragment bools +0x58..+0x60 other than +0x5a, fragment floats +0x64..+0x73, +0x55, +0x90..+0x93, +0xa0..+0xa6, and the compute bytes +0xa9..+0xb8. They feed the bind stream built by sdk+0x5f6b60, which was not decoded method by method.
- The exact NVN1 prototype of `nvnProgramGetSubtileSize` and `nvnProgramSetShadersExt` (not in nvn.h); NVN2's behaviour is above.
- DLSS method/preset/flag enum values and the order of the six optimal-settings outputs.
- `nvnInterceptionSetDispatchResetCallbackNVX` first argument (unused by the driver).
- The u16 at message record +6 and the four u32 at +8..+0x14 (likely invocation coordinates [INFERENCE]).
