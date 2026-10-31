// Shader keys (nvn_shader_key.h) against the keys shader_split.py writes into the shader map, for synthetic stages of both control layouts. The expected keys were computed with
// shader_split.skey (DM_GAME=0400C3F00006E000; nvn2_shader.CONTROL_SIZE for the NVN2 one) from the same bytes; a change of either side's key breaks this test
#include "../nvn/nvn_shader_key.h"
#include <stdio.h>

static int checks, fails;
static void check_key(const char *what, const void *ctl, const void *code, uint32_t skip_off, uint32_t skip_len, const char *want) {
    uint8_t k[8]; char hex[17];
    nvn_shader_key(ctl, code, code ? nvn_ctl_code_len(ctl) : 0, skip_off, skip_len, k);
    for (int i = 0; i < 8; i++) snprintf(hex + 2 * i, 3, "%02x", k[i]);
    checks++; if (strcmp(hex, want)) { fails++; fprintf(stderr, "FAIL: %s: key %s, shader_split.py gives %s\n", what, hex, want); }
}
#define CHECK(c, why) do { checks++; if (!(c)) { fails++; fprintf(stderr, "FAIL: %s\n", why); } } while (0)
static void put32(uint8_t *b, uint32_t at, uint32_t v) { memcpy(b + at, &v, 4); }

int main(void) {
    // NVN2: the fixed 0xd8 bytes (+0x04 = 0xc9, code length at +0x44), then a 0x40-byte debug blob the key leaves out
    static uint8_t c2[0xd8 + 0x40], k2[0x90];
    for (unsigned i = 0; i < sizeof c2; i++) c2[i] = (uint8_t)(i * 3 + 1);
    for (unsigned i = 0; i < sizeof k2; i++) k2[i] = (uint8_t)(i * 7 + 5);
    put32(c2, 0, NVN_CTL_MAGIC); put32(c2, 4, NVN2_CTL_MAJOR); put32(c2, 0x44, sizeof k2);
    CHECK(nvn_ctl_is_nvn2(c2) && nvn_ctl_key_len(c2) == 0xd8 && nvn_ctl_code_len(c2) == sizeof k2, "an NVN2 control: 0xd8 key bytes, code length at +0x44");
    check_key("NVN2 control + code", c2, k2, 0, 0, "220a6a1f3bbe5480");
    memset(c2 + 0xd8, 0, 0x40);
    check_key("NVN2 control with another debug blob", c2, k2, 0, 0, "220a6a1f3bbe5480");

    // Switch 1: a 0x900-byte section (+0x04 = 1, own length at +0x20, code length at +0x704) hashes its first 0x880 bytes; a 0x850-byte one its own length
    static uint8_t c1[0x900], k1[0x60];
    for (unsigned i = 0; i < sizeof c1; i++) c1[i] = (uint8_t)(i * 5 + 2);
    for (unsigned i = 0; i < sizeof k1; i++) k1[i] = (uint8_t)(i * 11 + 3);
    put32(c1, 0, NVN_CTL_MAGIC); put32(c1, 4, 1); put32(c1, 0x20, sizeof c1); put32(c1, 0x704, sizeof k1);
    CHECK(!nvn_ctl_is_nvn2(c1) && nvn_ctl_key_len(c1) == 0x880 && nvn_ctl_code_len(c1) == sizeof k1, "a Switch 1 control: 0x880 key bytes, code length at +0x704");
    check_key("Switch 1 control + code", c1, k1, 0, 0, "dea1bf96395f064d");
    check_key("Switch 1 control with shader_key_skip 0x770+0x78", c1, k1, 0x770, 0x78, "cf90d8f151198411");
    put32(c1, 0x20, 0x850);
    CHECK(nvn_ctl_key_len(c1) == 0x850, "a 0x850-byte Switch 1 control hashes its own length");
    check_key("Switch 1 0x850-byte control + code", c1, k1, 0, 0, "7aa0fb4bdc4ad974");

    printf("shader key: %d checks, %d failed\n", checks, fails);
    return fails != 0;
}
