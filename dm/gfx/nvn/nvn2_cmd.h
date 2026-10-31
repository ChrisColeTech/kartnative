// NVN2 (Switch 2) command records: the payloads of OP_PUSH_CONST and OP_MESH (nvn_obj.h), written by nvn2_cmd.c and read by the replay (nvn_mtl.m) and the capture walk (nvn_capture.c).
// docs/reference/nvn2.md (Command buffers) has the driver's semantics, docs/reference/rendering.md (NVN2 command buffers) what the host makes of them.
#pragma once
#include <stdint.h>
#include <string.h>
#include "dmsh_abi.h"   // DMSH_PUSH_BANK0 / DMSH_PUSH_SLOTS

// PushGraphicsConstants / PushComputeConstants (and their Deferred forms): size bytes at byte offset of push block slot (0-7). A graphics block is Ampere's root table
// (methods 0x504 / 0x508), which an SM86 graphics program reads as constant bank DMSH_PUSH_BANK0 + slot. data holds the bytes, or for a deferred push the 8-byte address
// they are read from when the replay reaches the record (the driver reads them at submit). size is a multiple of 4, at most PUSH_BLOCK (the driver clamps it to 256 bytes,
// sdk+0x5d34b0); offset is a byte offset below 256 for graphics (the driver keeps its low 8 bits), a word-aligned one for compute (the driver keeps offset / 4)
enum { PUSH_COMPUTE = 1, PUSH_DEFERRED = 2, PUSH_BLOCK = 256 };
typedef struct { uint8_t flags, slot; uint16_t offset; uint32_t size; uint8_t data[]; } p_push_const;

// The DrawMeshTasks family: kind picks the form, the fields the arguments it has (the rest 0). ind: the indirect records' address; cnt: the draw count's address;
// n: drawCount (multi-draw) or maxDrawCount (count forms); stride: bytes between records (<= 0: the record size, 8 or 12)
enum { MESH_TASKS, MESH_TASKS_3D, MESH_INDIRECT, MESH_3D_INDIRECT, MESH_MULTI_INDIRECT, MESH_3D_MULTI_INDIRECT, MESH_MULTI_INDIRECT_COUNT, MESH_3D_MULTI_INDIRECT_COUNT, MESH_KINDS };
typedef struct { int32_t kind, x, y, z; uint64_t ind, cnt; int32_t n, stride; } p_mesh;   // MESH_TASKS: x = first, y = count; MESH_TASKS_3D: the grid x, y, z
// A direct draw's record stops before ind (nvn2_cmd.c mesh); read a record with mesh_rec, which gives the missing fields as 0
static inline const p_mesh *mesh_rec(const void *a, uint32_t size, p_mesh *t) { if (size - 8 >= sizeof *t) return a; memset(t, 0, sizeof *t); memcpy(t, a, size - 8); return t; }
