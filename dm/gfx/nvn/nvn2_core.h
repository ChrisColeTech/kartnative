// NVN2 (Switch 2) core: what hle_nvn.c calls into the NVN2 files for, only in a Switch 2 build (DM_GAME_SWITCH2). nvn2_device.c, nvn2_pool.c, nvn2_sync.c.
#pragma once
#include <stdint.h>
#include "nvn_obj.h"

void nvn2_device_init(void);                                    // nvnDeviceInitialize: checks that nvnPrivate0 is 0 (nvn2_device.c)
uint32_t nvn2_pool_flags(uint32_t flags);                       // the flags nvnMemoryPoolInitialize keeps, normalised as the NVN2 driver does (nvn2_pool.c)
void nvn2_pool_check(const pool_t *o, const pool_builder_t *b); // nvnMemoryPoolInitialize: NVX fixed addresses the host cannot honour (nvn2_pool.c)
int nvn2_handles_ok(int n, const NVNcommandHandle *h);          // command handles the host made (tag 0), else [RED] once and 0 (nvn2_sync.c)
