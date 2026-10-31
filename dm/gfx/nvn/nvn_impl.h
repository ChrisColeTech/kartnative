// How an NVN implementation is registered, shared by hle_nvn.c and the NVN2 files (nvn2_*.c).
// NVN(name, ret, args...) defines h_<name> and checks it against the prototype gen_protos.py generated from nvn.h (nvn_protos.h);
// NVN_NOPROTO is the same without the check (names nvn.h has no prototype for). Both go into the section __DATA,swnvn, which
// nvn_init_slots (hle_nvn.c) turns into the slot table; a name registered twice there is a build error at start-up (die).
// NVN2(name, ret, args...) registers into __DATA,swnvn2: applied only in a Switch 2 build (DM_GAME_SWITCH2), after and over the swnvn
// entries, so an NVN2 implementation of a name whose ABI or semantics changed replaces the NVN1 one there and nowhere else. Its signature is
// the caller's to check (the NVN2 prototypes in include/nvn2.h use their own object types); a name registered twice in swnvn2 also dies. An NVN2
// implementation also replaces a name of hle_nvn.c's empty-implementation list (g_nops).
#pragma once
#include "nvn_protos.h"

typedef struct { const char *name; void *fn; } nvn_impl;
#define NVN(name, ret, ...)                                                                       \
    static ret h_##name(__VA_ARGS__);                                                             \
    static const NVNT_##name chk_##name __attribute__((unused)) = h_##name;                       \
    __attribute__((used, section("__DATA,swnvn"))) static const nvn_impl e_##name = {#name, (void *)h_##name}; \
    static ret h_##name(__VA_ARGS__)
#define NVN_NOPROTO(name, ret, ...)                                                               \
    static ret h_##name(__VA_ARGS__);                                                             \
    __attribute__((used, section("__DATA,swnvn"))) static const nvn_impl e_##name = {#name, (void *)h_##name}; \
    static ret h_##name(__VA_ARGS__)
#define NVN2(name, ret, ...)                                                                      \
    static ret h2_##name(__VA_ARGS__);                                                            \
    __attribute__((used, section("__DATA,swnvn2"))) static const nvn_impl e2_##name = {#name, (void *)h2_##name}; \
    static ret h2_##name(__VA_ARGS__)

// Boolean parameters: Switch callers do not extend them, only the low 8 bits are valid
#define B(x) ((x) & 0xff)

// Appends one command record (op from nvn_obj.h, zeroed payload of `payload` bytes) to a command buffer's recording; returns the payload (hle_nvn.c)
void *nvn_cmd_rec(NVNcommandBuffer *c, uint32_t op, size_t payload);
