// NVN2 (Switch 2) textures: what differs from NVN1 for the host's texture code. The NVN1 texture, sampler and pool implementations in hle_nvn.c
// serve NVN2 unchanged (same function ABI, every host record fits the NVN2 objects: docs/reference/nvn2.md#textures); what changes is the
// memory layout of block-linear texels, which the deswizzle (dm/gfx/tex) reads from nvn_gob_s2. Facts: docs/reference/nvn-objects.md#switch-2-block-linear-gobs
#include "game_profile.h"
#include "../tex/nvn_deswizzle.h"

// Set before main runs, so before any texture exists; a Switch 1 build leaves the Switch 1 GOB.
__attribute__((constructor)) static void nvn2_tex_init(void) { nvn_gob_s2 = DM_GAME_SWITCH2; }
