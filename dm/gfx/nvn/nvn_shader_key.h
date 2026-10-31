// A shader stage's key: sha256(control bytes + code area)[:8], the same as dm/gfx/tools/shader_split.py skey, which names the stage in the shader map.
// Switch 1 control sections (u32 +0x04 == 1): the first 0x880 bytes, or the section's own length (+0x20) when it is 0x760..0x87f (SDK 10's 0x850-byte sections are
// followed by other data; shader_split ctl_len); the code area is +0x704 bytes long.
// Switch 2 (NVN2) control sections (+0x04 == 0xc9, dm/gfx/tools/nvn2_shader.py): the fixed 0xd8 bytes (a debug blob may follow, which the key leaves out); the code area
// is +0x44 bytes long (docs/reference/shaders.md, "Switch 2 (NVN2) shader binaries").
// skip_off / skip_len: the profile's shader_key_skip, control bytes hashed as zeros (shader_split key_ctl), applied only when they lie inside the hashed control bytes
#pragma once
#include <stdint.h>
#include <string.h>
#include <CommonCrypto/CommonDigest.h>

enum { NVN_CTL_MAGIC = 0x98761234, NVN2_CTL_MAJOR = 0xc9, NVN2_CTL_SIZE = 0xd8, NVN_CTL_KEY_MAX = 0x880 };

static inline uint32_t nvn_ctl_u32(const void *ctl, uint32_t at) { uint32_t v; memcpy(&v, (const uint8_t *)ctl + at, 4); return v; }
// An NVN2 control section: the magic, then 0xc9 (nvn2_shader.is_control)
static inline int nvn_ctl_is_nvn2(const void *ctl) { return nvn_ctl_u32(ctl, 0) == NVN_CTL_MAGIC && nvn_ctl_u32(ctl, 4) == NVN2_CTL_MAJOR; }
// The code area's length the control section gives
static inline uint32_t nvn_ctl_code_len(const void *ctl) { return nvn_ctl_u32(ctl, nvn_ctl_is_nvn2(ctl) ? 0x44 : 0x704); }
// How many control bytes the key hashes
static inline uint32_t nvn_ctl_key_len(const void *ctl) {
    if (nvn_ctl_is_nvn2(ctl)) return NVN2_CTL_SIZE;
    uint32_t own = nvn_ctl_u32(ctl, 0x20);
    return own >= 0x760 && own < NVN_CTL_KEY_MAX ? own : NVN_CTL_KEY_MAX;
}
// code may be NULL (ncode is then ignored): a stage whose code area is not known yet hashes the control bytes alone
static inline void nvn_shader_key(const void *ctl, const void *code, uint32_t ncode, uint32_t skip_off, uint32_t skip_len, uint8_t out[8]) {
    uint32_t nctl = nvn_ctl_key_len(ctl);
    uint8_t md[CC_SHA256_DIGEST_LENGTH]; CC_SHA256_CTX hc; CC_SHA256_Init(&hc);
    if (skip_len && skip_off + skip_len <= nctl) {
        uint8_t c[NVN_CTL_KEY_MAX]; memcpy(c, ctl, nctl); memset(c + skip_off, 0, skip_len);
        CC_SHA256_Update(&hc, c, nctl);
    } else CC_SHA256_Update(&hc, ctl, nctl);
    if (code) CC_SHA256_Update(&hc, code, ncode);
    CC_SHA256_Final(md, &hc); memcpy(out, md, 8);
}
