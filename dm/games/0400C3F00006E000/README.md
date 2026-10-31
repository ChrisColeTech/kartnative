# Mario Kart World

Switch 2, `0400C3F00006E000`, 1.0.0 (`ResourceSizeTable.Product.100`). Bring-up in progress: headless on the Mac it boots through `nninitStartup`, `nnMain` and the SDK's initialization to the NVN2 device, where it stops (NVN2 is not implemented yet).

## Build

| Module | Build ID | Notes |
|---|---|---|
| `main` | `9e244438c3d8ef1c703a6a85ef99c4f0` | 42.1 MB text, no SVCs; 502 `mrs tpidr_el0`, 29,446 x18 uses |
| `sdk` | `90ebba182862b9ecdc21fcb87b999012` | SDK 19.3.3 (`NintendoSdk_nnSdk-Generic_19_3_3`, `NintendoSDK_NVN-19_3_3`), NVN2; SVCs 0x01-0x5f, 0x70, 0x72, 0x80, 0x81, 0x82 |
| `subsdk0` | `d39a7a3d44530bb61b35690e5b0383d5` | Mono (`mono_jit_init`, `nn::ecli` allocator hooks; 40 exports, main imports `nn::ecli::Allocate` / `Deallocate`); the movie player is `nn::movie` in `sdk` |

`main.npdm`: META flags 0x37 (64-bit, 39-bit address space), main thread priority 44 on core 0 with a 1 MB stack, 64 MB of system resource (META +0x14; +0x10 holds 0xc000000), a Switch 2 ACID (0x300-byte signature area). ACI0 kernel capabilities: cores 0..5, priorities 28..59, svcs 0x01-0x29, 0x2c, 0x2d, 0x32-0x36 and 0x80 (not 0x70, 0x72, 0x81, 0x82), kernel version 9.1, 512 handles.

All three modules have `DT_INIT`, which runs their `DT_INIT_ARRAY` (main 2,866 entries, sdk 86, subsdk0 4): the loader needs nothing for an init array without `DT_INIT`.

## What the profile needs and why

- `switch2 on`: from `main.npdm` (`profile_guess.py` `switch2`). The host then answers as a Switch 2: 31.25 MHz counter, six cores, the Switch 2 kernel's `GetInfo` ids and svc 0x80.
- Memory: the Switch 2's 12 GB layout. `app_pool 0x265100000` (9809 MiB, the application pool); `nninitStartup` (`main+0xc387b4`) sets a heap of (total − used) rounded down to 2 MB less 0x60400000, 0x200c00000 here, which the 8.25 GiB heap region of `mem_reserve 0x348000000` holds. `sys_resource 0x4000000` from the NPDM. The alias region (`mem_alias 0x118000000`) has held everything the boot maps so far.
- `shader_groups`: where its NVN2 shaders live (`Shader/`, `Layout/`, `Effect/`, `Lib/`), for `shader_split.py`.

## Boot sequence (headless, Mac)

1. `nnosInitialize`: `GetInfo` 29 / 30 (remote region), 33 (page granule), 28 (0); `VammManager` maps one granule of its node area.
2. `nninitInitializeSdkModule`, then `nninitStartup`: `GetInfo` 22 / 21, `SetHeapSize(0x200c00000)`, `AllocateMemoryBlock(0x3600000)` for malloc, the init arrays.
3. `nnMain` (`main+0xcf2370`): mounts the RomFS, reads `System/HeapSize/Default.Nin_Ounce_NVN2.HeapDef.byml.zs` (`ProductME…` first, absent), `nv::InitializeGraphics(…, 0)`, `vi::OpenDefaultDisplay` / `CreateLayer` / `GetDisplayVsyncEvent`, then the NVN2 device (below).
4. With a working device (seen only through the look-ahead below): the Presentation, Prepare, Vibration and gear::Prepare threads; `RegionLangMask.txt`, `AddressTable/Product.100…atbl.byml.zs`; hid; `oe::Initialize`, `oe::SetFocusHandlingMode 2` and the graphics scale calls; the sound setup (aal, Opus); the save data managers (five save commits at first boot); Pia; job pools of one worker per core 0-5 (`ModuleSystemWorker`, `BaseProcExecutor`, `UIJob`, `phive::DepWorker`); resource loading (`Model/…bfres.zs`, `RSDB/…`).

## The NVN2 boundary (current stop)

`nnMain`'s graphics setup (`main+0xcf3a14` on) makes these NVN calls first:

1. `nvnBootstrapLoader("nvnDeviceInitialize")` (`main+0xcf3a5c`) and `nvnBootstrapLoader("nvnDeviceGetProcAddress")` (`main+0xcf3a6c`).
2. Its C proc loader (`main+0xcf5560`) with a null device: every name it uses through `nvnDeviceGetProcAddress` (698 names; all in `names.txt` now).
3. The device builder's `SetDefaults` and `SetFlags(0)` (through main's pointers at `main+0x3304378` / `+0x3304380`) on a builder at `sp+0x10`; a 0x3000-byte device; `nvnDeviceInitialize(device, builder)`, which must return true.
4. The proc loader again with the device.
5. `nvnDeviceGetInteger(device, 0)` and `(device, 1)`: main requires API major 0xE7 (231) and minor 0x10 (16) or more (`main+0xcf3b20`).

The host's NVN reports 55.15, so `nnMain` leaves its graphics setup at `main+0xcf3d54` without an error and without creating its 0x260-byte graphics object (stored at `main+0x33dcfb0`); `sead::MethodTreeMgr`'s next call reads it (`main+0xe56e70`, from `main+0xc205f4`) and the boot ends with `CRASH sig=11 addr=0x20`. This is where NVN2 has to start: the same names as NVN1 do not prove the same ABI, so the NVN1 implementations are not adapted to it.

### Look-ahead past the device check

`DM_NVN_API=231.16` (an existing diagnostic switch, no code change) makes the NVN1 implementations answer the version check, to see what comes after it. That runs NVN2 calls through NVN1 implementations of unverified ABI: what it shows about graphics is a list of names, not behaviour. The calls that followed, in order: `nvnDeviceGetInteger` items 0x2a, 0x5a, 0xf, 0x10, 0x11, 0x12, 0x55, 0x14, 0x19, 0x1a, 0x18; a 0x423000-byte memory pool (flags 0x22); a sampler pool of 4096 and a texture pool; `nvnQueueInitialize` (after `nvnQueueBuilderSetPrimaryQueue`, unimplemented); a 0x2d00000-byte pool and three 2560x1440 textures (format 0x25) for a three-texture window at interval 1; buffers; `nvnProgramBuilderSetDefaults` / `SetDevice` / `SetShaders` and `nvnProgramInitializeFromBuilder` (unimplemented); then from the frame threads `nvnCommandBufferSetRecordingMode`, `SetGeometryProcessingMode`, `SetStencilCullCriteria`, `ShadingRateImagePalette`, `BindShadingRateImage`, `PushGraphicsConstants`, `SetComputeOverlapMode` and `DrawMeshTasks` (all unimplemented). Every non-graphics call on that path was answered; the run ended in the host's shader hashing (`keys_hash` from `nvnQueueSubmitCommands`, reading a program through the NVN1 layout).

## Found on this game

- [A vector register taken for the jump table index](../../../docs/reference/rewriter.md#a-vector-register-taken-for-the-jump-table-index): the rewrite of `main` stopped at `main+0xea4a8`.
- [Switch 2 code: the instructions capstone 5 leaves undecoded](../../../docs/reference/rewriter.md#switch-2-code-the-instructions-capstone-5-leaves-undecoded). The modules read only `cntpct_el0` among the counters (40 gates in `sdk`), no `cntfrq` / `cntvct` / ID registers; one MTE `stg` in `sdk` (`sdk+0x74c160`, libunwind's stack retag, behind a flag).
- [The Switch 2's 31.25 MHz system counter](../../../docs/reference/services.md#the-switch-2s-3125-mhz-system-counter) (found from the sdk before the first boot).
- [The Switch 2 SDK's version string](../../../docs/reference/services.md#the-switch-2-sdks-version-string): a red line at load.
- [The Switch 2 kernel's GetInfo ids 29 to 33](../../../docs/reference/memory.md#the-switch-2-kernels-getinfo-ids-29-to-33): an abort in `nninitInitializeSdkModule` (`MapPhysicalMemory` of size 0).
- [Six cores on the Switch 2](../../../docs/reference/threads.md#six-cores-on-the-switch-2-svcgetinfo-type-0).
- [The Switch 2's 12 GB layout](../../../docs/reference/memory.md#the-switch-2s-12-gb-layout): `SetHeapSize` past a 128 MB heap region, then a crash in `nninitStartup`.
- [The Switch 2's `svcSetGpuMemoryAttribute` (svc 0x80)](../../../docs/reference/memory.md#the-switch-2s-svcsetgpumemoryattribute-svc-0x80) (from its modules; not met yet).
- [The Switch 2's graphics scale (`oe`)](../../../docs/reference/services.md#the-switch-2s-graphics-scale-oe): a null read on the Prepare Thread (look-ahead run).
- [Audio device calls of the Switch 2 SDK](../../../docs/reference/audio.md#audio-device-calls-of-the-switch-2-sdk): an abort opening `aud:u` (look-ahead run).
- `vi` HDR and VRR calls (`QueryDisplayHdrStatus`, `QueryDisplayVrrSupported`, `QueryDisplayHdrPaperWhite`, `SetDisplayVrrCapability`): the SDK answers 0x272 (or nothing) for a display that is not its own `DefaultDisplay` (`sdk+0xe151f8`), and the host's is not, so main takes its no-VRR, no-HDR path (`main+0x226cce4`) without reaching the vi2 service. Not hooked.

## How to run it on the Mac

Rewrite with `dm/tools/patch_nso.py` into `$W/patched` (BootTools), then `make -C dm/host GAME=0400C3F00006E000 dmhost` and, headless:

```
cd $W/run && DM_ROMFS=/Users/emanuel/Games/Switch/base/romfs DM_SAVE=$W/run/save DM_TEMP=$W/run/temp \
  perl -e 'alarm shift; exec @ARGV' 60 dmhost $W/patched
```

`DM_SVC=1 DM_THR=1 DM_FS=1 DM_NVN_LOG=1` for the boot details; `DM_NVN_API=231.16` for the look-ahead (it ran about 4 s of loading before its crash).

## Open

- NVN2: the device and everything after it (Nvn2RE; `docs/reference/nvn2.md`). Values the game queries: `nvnDeviceGetInteger` items listed above.
- SM86 shaders (none translated yet: every program logs "not in the shader map" in the look-ahead).
- Not yet met, unhooked in main's imports: `nn::syncpt::sdk` events (Create / Request / TryWait / Cancel / Destroy `SyncPointEvent`, `InitializeMultiWaitHolder`: NVN2's GPU fences, to be answered with the host's NVN2 sync), the audio virtual output device and output stream calls, `nn::codec` video decoders (`video::Initialize`, `Register{H264,Vp8,Vp9,Av1}DecoderBuffer`, `GetDecoderImagePoolNativeImageHandle`: native images of `nn::gs`; the Mushroom Cup opening is a 2560x1440 VP9 movie), the `nn::socket` calls behind Pia, `nn::ssl::Context::Create`.
- Movies: main uses the Switch 2 movie API, not the `nn::movie::Decoder` the host takes over (`hle_movie.c`): `nn::movie::MediaExtractor` (41 `nn::movie` imports, none hooked) feeding the `nn::codec` video decoders above, whose pictures are `nn::gs` native images. The host's movie path (libvpx) has to be put behind those calls.
- `nvnPrivate0` (a u32 in sdk's bss, `sdk+0xedb150`, imported by main) gates main's inlined NVN2 command-buffer paths; it must stay 0, which it does as long as the sdk's own `nvnDeviceGetProcAddress` never runs (its only sdk callers, `nvnSyncInitializeFromNativeSyncHandle` and `nvnNativeImageInitialize`, reach `nvnBootstrapLoader` through the PLT, which the host takes over).
- The Switch 2's display: `oe::GetDefaultDisplayResolution` answers the Switch's 1920x1080 / 1280x720; MKW does not import it.
