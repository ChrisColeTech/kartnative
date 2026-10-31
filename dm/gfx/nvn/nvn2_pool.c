// NVN2 (Switch 2) memory pools, buffers and events: what differs from the NVN1 ones in hle_nvn.c, registered with NVN2 (nvn_impl.h) so only a
// Switch 2 build (DM_GAME_SWITCH2) uses it. Facts: docs/reference/nvn2.md#memory-model-memorypool--memorypoolbuilder--nvx--externalmemorypoolbuilder.
// GPU addresses stay the host's: a storage-backed pool's GPU address is its CPU address (as the NVN2 driver places it, inside its fixed allocation over
// the application's address range), and a virtual pool's is the start of an address range the host reserves for it (hle_nvn.c vpool_reserve): GPU-only,
// outside every pool's storage, as the driver's virtual pools lie outside the CPU range.
#include "../../host/hle.h"
#include "nvn_impl.h"
#include "nvn2_core.h"

enum { PF_CPU_NO_ACCESS = 0x1, PF_CPU_UNCACHED = 0x2, PF_CPU_CACHED = 0x4, PF_GPU_NO_ACCESS = 0x8, PF_GPU_UNCACHED = 0x10, PF_GPU_CACHED = 0x20,
       PF_COMPRESSIBLE = 0x80, PF_PHYSICAL = 0x100, PF_VIRTUAL = 0x200 };

// nvnMemoryPoolInitialize (sdk+0x5bcef0) keeps bits 6 and up and replaces the access bits 0-5 by one CPU and one GPU mode: CPU UNCACHED if asked,
// else CACHED, else NO_ACCESS, else UNCACHED, and NO_ACCESS for a virtual pool; GPU CACHED if asked, else UNCACHED, else NO_ACCESS, else CACHED, and
// NO_ACCESS for a physical pool. (The driver's device flag 0x8000 that turns CPU UNCACHED into CACHED is not set by MKW, main+0xcf3aa4.)
uint32_t nvn2_pool_flags(uint32_t f) {
    uint32_t cpu = f & PF_CPU_UNCACHED ? PF_CPU_UNCACHED : f & PF_CPU_CACHED ? PF_CPU_CACHED : f & PF_CPU_NO_ACCESS ? PF_CPU_NO_ACCESS : PF_CPU_UNCACHED;
    uint32_t gpu = f & PF_GPU_CACHED ? PF_GPU_CACHED : f & PF_GPU_UNCACHED ? PF_GPU_UNCACHED : f & PF_GPU_NO_ACCESS ? PF_GPU_NO_ACCESS : PF_GPU_CACHED;
    if (f & PF_VIRTUAL) cpu = PF_CPU_NO_ACCESS;
    if (f & PF_PHYSICAL) gpu = PF_GPU_NO_ACCESS;
    return (f & ~0x3fu) | cpu | gpu;
}
// The NVX fixed GPU addresses (SetGenericAddressNVX / SetSpecialAddressNVX) place a pool's mappings inside the device's reserved address ranges. The host's
// GPU address is the storage itself, so it can honour a generic address only when it is the storage address; the compressible ("special") mapping has no
// separate address on the host
void nvn2_pool_check(const pool_t *o, const pool_builder_t *b) {
    if (b->nvx_generic && b->nvx_generic != (uint64_t)o->mem) LOG("[RED] MemoryPoolInitialize (NVN2): fixed GPU address %#llx (SetGenericAddressNVX) differs from the pool's storage %p; the host uses the storage address", (unsigned long long)b->nvx_generic, (void *)o->mem);
    if (b->nvx_special) LOG("[RED] MemoryPoolInitialize (NVN2): fixed compressible-kind GPU address %#llx (SetSpecialAddressNVX) is not honoured; the host has one address per pool", (unsigned long long)b->nvx_special);
}

#define PB(b) ((pool_builder_t *)(b))
#define CPB(b) ((const pool_builder_t *)(b))
#define CP(p) ((const pool_t *)(p))
// SetDefaults (sdk+0x5bcdb0): memory, size, native handle and the NVX addresses 0, flags CPU_NO_ACCESS | GPU_CACHED; the device is kept
NVN2(nvnMemoryPoolBuilderSetDefaults, void, NVNmemoryPoolBuilder *b) {
    NVNdevice *d = PB(b)->dev; memset(b, 0, sizeof(pool_builder_t)); PB(b)->dev = d; PB(b)->flags = PF_CPU_NO_ACCESS | PF_GPU_CACHED;
}
NVN2(nvnMemoryPoolBuilderGetDevice, NVNdevice *, const NVNmemoryPoolBuilder *b) { return CPB(b)->dev; }
NVN2(nvnMemoryPoolBuilderGetFlags, int, const NVNmemoryPoolBuilder *b) { return (int)CPB(b)->flags; }   // as set, not normalised
NVN2(nvnMemoryPoolBuilderGetMemory, void *, const NVNmemoryPoolBuilder *b) { return CPB(b)->mem; }
NVN2(nvnMemoryPoolBuilderGetSize, size_t, const NVNmemoryPoolBuilder *b) { return (size_t)CPB(b)->size; }
// GetFlags (sdk+0x5bd330): the normalised flags without the driver-internal bits 22-31
NVN2(nvnMemoryPoolGetFlags, int, const NVNmemoryPool *p) { return (int)(CP(p)->flags & 0x3fffff); }
// GetBufferAddress (sdk+0x5bd2a0): the generic GPU address; a PHYSICAL pool has no GPU mapping (it is only reached through virtual pools), so 0
NVN2(nvnMemoryPoolGetBufferAddress, uint64_t, const NVNmemoryPool *p) { return CP(p)->flags & PF_PHYSICAL ? 0 : (uint64_t)CP(p)->mem; }

// NVX: the NvRm memory handle of a pool. The host has no NvRm memory objects: a pool cannot be imported from one, and none has a handle (0)
NVN2(nvnMemoryPoolBuilderSetNativeHandleNVX, void, NVNmemoryPoolBuilder *b, uint32_t h) {
    LOG("[RED] nvnMemoryPoolBuilderSetNativeHandleNVX(%#x): the host cannot import NvRm memory; the pool keeps the builder's storage", h);
    (void)b;
}
NVN2(nvnMemoryPoolBuilderGetNativeHandleNVX, uint32_t, const NVNmemoryPoolBuilder *b) { (void)b; return 0; }
NVN2(nvnMemoryPoolGetNativeHandleNVX, uint32_t, const NVNmemoryPool *p) { (void)p; return 0; }
NVN2(nvnMemoryPoolBuilderSetGenericAddressNVX, void, NVNmemoryPoolBuilder *b, uint64_t a) { PB(b)->nvx_generic = a; }
NVN2(nvnMemoryPoolBuilderSetSpecialAddressNVX, void, NVNmemoryPoolBuilder *b, uint64_t a) { PB(b)->nvx_special = a; }
NVN2(nvnMemoryPoolBuilderGetReservedAddressesNVX, void, const NVNmemoryPoolBuilder *b, uint64_t *generic, uint64_t *special) {
    if (generic) *generic = CPB(b)->nvx_generic;
    if (special) *special = CPB(b)->nvx_special;
}
NVN2(nvnMemoryPoolGetGenericAddressNVX, uint64_t, const NVNmemoryPool *p) { return h2_nvnMemoryPoolGetBufferAddress(p); }   // = GetBufferAddress (pool+0x18)
// The compressible-kind address (0 for other pools): on the host the same memory has one address
NVN2(nvnMemoryPoolGetSpecialAddressNVX, uint64_t, const NVNmemoryPool *p) { return CP(p)->flags & PF_COMPRESSIBLE ? (uint64_t)CP(p)->mem : 0; }

// ExternalMemoryPoolBuilder (sdk+0x612e30..0x612e70; the driver does not hand these out through GetProcAddress, and nothing consumes the builder):
// +0x0 device, +0x8 nn::gs::NativeMemoryHandle
typedef struct { NVNdevice *dev; uint32_t handle; } ext_pool_builder_t;
NVN2(nvnExternalMemoryPoolBuilderSetDefaults, void, void *b) { ((ext_pool_builder_t *)b)->handle = 0; }
NVN2(nvnExternalMemoryPoolBuilderSetDevice, void, void *b, NVNdevice *d) { ((ext_pool_builder_t *)b)->dev = d; }
NVN2(nvnExternalMemoryPoolBuilderSetNativeMemoryHandle, void, void *b, uint32_t h) { ((ext_pool_builder_t *)b)->handle = h; }
NVN2(nvnExternalMemoryPoolBuilderGetDevice, NVNdevice *, const void *b) { return ((const ext_pool_builder_t *)b)->dev; }
NVN2(nvnExternalMemoryPoolBuilderGetNativeMemoryHandle, uint32_t, const void *b) { return ((const ext_pool_builder_t *)b)->handle; }

// Buffers (sdk+0x59e1a0, 0x59e300): the builder's device; debug IDs are a stub returning 0
NVN2(nvnBufferBuilderGetDevice, NVNdevice *, const NVNbufferBuilder *b) { return *(NVNdevice *const *)b; }
NVN2(nvnBufferGetDebugID, uint64_t, const NVNbuffer *b) { (void)b; return 0; }

// Events (sdk+0x5bb5f0..0x5bb6a0): the storage of a builder and of an event
NVN2(nvnEventBuilderGetStorage, const NVNmemoryPool *, const NVNeventBuilder *b, int64_t *off) {
    const ev_builder_t *o = (const ev_builder_t *)b; if (off) *off = o->off; return (const NVNmemoryPool *)o->pool;
}
NVN2(nvnEventBuilderGetMemoryPool, const NVNmemoryPool *, const NVNeventBuilder *b) { return (const NVNmemoryPool *)((const ev_builder_t *)b)->pool; }
NVN2(nvnEventBuilderGetMemoryOffset, int64_t, const NVNeventBuilder *b) { return ((const ev_builder_t *)b)->off; }
NVN2(nvnEventGetMemoryPool, const NVNmemoryPool *, const NVNevent *e) { return (const NVNmemoryPool *)((const ev_t *)e)->pool; }
NVN2(nvnEventGetMemoryOffset, int64_t, const NVNevent *e) { return ((const ev_t *)e)->off; }
