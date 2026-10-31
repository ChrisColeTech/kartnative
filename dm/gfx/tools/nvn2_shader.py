#!/usr/bin/env python3
"""NVN2 (Switch 2) shader binaries: the control section and code area of each stage, as Mario Kart World's containers hold them (docs/reference/shaders.md,
"Switch 2 (NVN2) shader binaries"). shader_split.py keys and packages them like Switch 1 shaders.

Control section (0xd8 bytes; u32 unless noted; every field below was read on Mario Kart World 1.0.0's 11,567 distinct programs):
  +0x00 magic 0x98761234 (as on Switch 1)           +0x04 0xc9 (Switch 1: 1): what tells an NVN2 control from a Switch 1 one
  +0x08 version (7 or 11; the driver takes 0..11)   +0x0c u16, +0x0e u16: GPU architecture ids the driver compares with the device's (0x0170, 0x000e)
  +0x20 16 bytes of debug hashes (BNSH and sead copies; zeros in BAHS)   +0x30 control length including a debug blob (0xd8 without one; 0 in BAHS)
  +0x38 end of the instructions in the code area (header included)     +0x3c size of the constant segment (bank 1, c1)
  +0x40 start of the constant segment                                   +0x44 code area length
  +0x4c register count                                                  +0x56 u8 stage: 0 vertex 1 fragment 2 geometry 3 tess control 4 tess evaluation 5 compute 6 task 7 mesh
  stage union from +0x58: compute +0x58 / +0x5c / +0x60 thread group x y z, +0x64 shared memory bytes; fragment +0x62 u16 colour targets written
Code area: graphics stages (task and mesh too) start with a 0x80-byte program header whose first 0x50 bytes are a Maxwell SPH (version 4), then the SM86
instructions (16 bytes each); compute has no header. After the last instruction (+0x38) the area is zero up to +0x40; [+0x40, +0x44) holds the constant
segment (its first +0x3c bytes) or, in 11 of the 18 mesh programs, a second SPH (geometry type, output topology 1, at most 1 output vertex)."""
import struct

CTL_MAGIC = bytes.fromhex('34127698')
CONTROL_MAJOR = 0xc9   # control +0x04; Switch 1's control sections have 1 there
CONTROL_SIZE = 0xd8    # the fixed part the driver parses for every version from 3 on (Nvn2RE: the 3–11 parser class); key bytes (shader_split ctl_len)
HEADER_SIZE = 0x80     # program header of the graphics stages; its first 0x50 bytes decode as a Maxwell SPH
SPH_SIZE = 0x50
STAGE = {0: 'vs', 1: 'ps', 2: 'gs', 3: 'hs', 4: 'ds', 5: 'cs', 6: 'ts', 7: 'ms'}   # control +0x56 → shader_split's stage codes ('ts' task, 'ms' mesh)
NAME = {'vs': 'vertex', 'ps': 'fragment', 'gs': 'geometry', 'hs': 'tess_ctrl', 'ds': 'tess_eval', 'cs': 'compute', 'ts': 'task', 'ms': 'mesh'}
SPH_TYPE = {1: 'vs', 2: 'hs', 3: 'ds', 4: 'gs', 5: 'ps'}   # Maxwell SPH word 0 bits 10..13 (shader type)


def u32(b, at): return struct.unpack_from('<I', b, at)[0]


def is_control(b):
    """b starts an NVN2 control section (magic, then CONTROL_MAJOR)"""
    return len(b) >= 8 and b[:4] == CTL_MAGIC and u32(b, 4) == CONTROL_MAJOR


def stage(ctl):
    """shader_split stage code of a control section (+0x56), None for a value that is no stage"""
    return STAGE.get(ctl[0x56]) if len(ctl) >= CONTROL_SIZE else None


def control(ctl):
    """The fields of a control section that tools use (see the module docstring)"""
    f = {'version': u32(ctl, 8), 'arch': [struct.unpack_from('<H', ctl, 0xc)[0], struct.unpack_from('<H', ctl, 0xe)[0]], 'length': u32(ctl, 0x30),
         'code_end': u32(ctl, 0x38), 'c1_size': u32(ctl, 0x3c), 'c1_start': u32(ctl, 0x40), 'code_size': u32(ctl, 0x44), 'registers': u32(ctl, 0x4c),
         'stage': stage(ctl), 'debug_hash': ctl[0x20:0x30].hex()}
    if f['stage'] == 'cs': f['local'] = list(struct.unpack_from('<3I', ctl, 0x58)); f['shared'] = u32(ctl, 0x64)
    if f['stage'] == 'ps': f['targets'] = struct.unpack_from('<H', ctl, 0x62)[0]
    return f


def check(st, ctl, code):
    """The reason a stage's control section and code area are not consistent, None when they are: the stage the container gives (st) is the control's,
    the code area is as long as control +0x44 says and holds the instructions and the constant segment, and a graphics stage starts with a version 4 SPH"""
    if len(ctl) < CONTROL_SIZE: return f'NVN2 control section of {len(ctl):#x} bytes, shorter than {CONTROL_SIZE:#x}'
    if stage(ctl) is None: return f'NVN2 control +0x56 stage {ctl[0x56]} is not a stage'
    if st != stage(ctl): return f'the container says {st} but the NVN2 control +0x56 says {stage(ctl)}'
    f = control(ctl)
    if f['code_size'] != len(code): return f'NVN2 control +0x44 says {f["code_size"]:#x} code bytes, the code area has {len(code):#x}'
    if not f['code_end'] <= f['c1_start'] <= f['code_size'] or f['c1_start'] + f['c1_size'] > f['code_size']: return 'NVN2 control +0x38 / +0x3c / +0x40 do not fit the code area'
    if st != 'cs':
        if f['code_end'] < HEADER_SIZE: return 'NVN2 graphics code area shorter than its program header'
        if sph(code)['version'] != 4 or sph(code)['type'] not in (1, 2): return 'NVN2 graphics code area does not start with a version 4 SPH'
    return None


def sph(code):
    """Maxwell SPH fields of a graphics code area's first 0x50 bytes (Ryujinx ShaderHeader's layout; the omap fields are a fragment shader's)"""
    w = struct.unpack_from('<20I', code, 0)
    return {'type': w[0] & 0x1f, 'version': w[0] >> 5 & 0x1f, 'stage': SPH_TYPE.get(w[0] >> 10 & 0xf), 'kills_pixels': w[0] >> 15 & 1, 'global_store': w[0] >> 16 & 1,
            'sass_version': w[0] >> 17 & 0xf, 'load_store': w[0] >> 26 & 1, 'fp64': w[0] >> 27 & 1, 'local_low': w[1] & 0xffffff, 'local_high': w[2] & 0xffffff,
            'local_crs': w[3] & 0xffffff, 'output_topology': w[3] >> 24 & 0xf, 'max_output_vertices': w[4] & 0xfff, 'omap_targets': w[18], 'omap_sample_mask': w[19] & 1,
            'omap_depth': w[19] >> 1 & 1}


def instructions(ctl, code):
    """The SM86 instructions of a stage: the code area after the program header (none for compute) up to control +0x38"""
    return code[0 if stage(ctl) == 'cs' else HEADER_SIZE:u32(ctl, 0x38)]


def c1(ctl, code):
    """The constant segment the program reads as c[1] (control +0x40, +0x3c bytes; b'' without one)"""
    return code[u32(ctl, 0x40):u32(ctl, 0x40) + u32(ctl, 0x3c)]


def _red(msg): raise SystemExit('[RED] ' + msg)


def ndsr_loose(d):
    """An agl NDSR pack holding NVN2 control sections (ndsr_shaders)"""
    return d[:4] == b'NDSR' and CTL_MAGIC + struct.pack('<I', CONTROL_MAJOR) in d


def ndsr_shaders(d, label):
    """agl's NDSR pack (Lib/agl/agl_resource…sarc/agl_ndsr.pck: 3 vertex + fragment programs, and 30 compute stages that all use HMMA tensor-core instructions, and
    their data): 'NDSR', u16 +0x04 version 0x300, BOM, u16 +0x0a entry count, then from +0x10 one 8-byte entry each {u32 offset, u32 size << 4 | type}. Types: 0 / 3 / 6
    control section of a vertex / fragment / compute stage, each followed by the entry of its code area (type + 1); 9–12 data (12: a 0x50-byte parameter block,
    11: 0x3000 bytes shared by several compute stages). A vertex stage and the fragment stage after it are one program"""
    if len(d) < 0x10: _red(f'{label}: NDSR header cut short')
    n = struct.unpack_from('<H', d, 0xa)[0]
    if 0x10 + 8 * n > len(d): _red(f'{label}: NDSR entry table of {n} entries runs past the {len(d):#x} bytes of the file')
    ents = [struct.unpack_from('<II', d, 0x10 + 8 * i) for i in range(n)]
    out, prog = [], 0
    for i, (off, w) in enumerate(ents):
        kind, size = w & 0xf, w >> 4
        if kind not in (0, 3, 6): continue
        if i + 1 >= n or ents[i + 1][1] & 0xf != kind + 1: _red(f'{label}: NDSR entry {i} (control section, type {kind}) is not followed by its code area')
        coff, csize = ents[i + 1][0], ents[i + 1][1] >> 4
        if off + size > len(d) or coff + csize > len(d): _red(f'{label}: NDSR entry {i} lies outside the file')
        ctl, code = d[off:off + size], d[coff:coff + csize]
        st = {0: 'vs', 3: 'ps', 6: 'cs'}[kind]
        if not is_control(ctl): _red(f'{label}: NDSR entry {i} is not an NVN2 control section')
        why = check(st, ctl, code)
        if why: _red(f'{label}: NDSR entry {i} ({st}): {why}')
        if st == 'vs': prog += 1
        out.append((f'{label}#NDSR@{off:#x}' if st == 'cs' else f'{label}#NDSR#var{prog}', st, ctl, code))
    return out


def sead_loose(d):
    """A sead shader file (Lib/sead/…/*_nvn2_shader*.bin): four u32 {vertex control, fragment control, vertex code, fragment code} offsets, the first control at 0x10"""
    return len(d) >= 0x18 and u32(d, 0) == 0x10 and is_control(d[0x10:0x18])


def sead_shaders(d, label):
    """The vertex and fragment stage of a sead shader file (sead_loose). Its control sections carry a debug blob after the fixed 0xd8 bytes (+0x30 = whole
    length): from +0xd8 a header (magic 0x37f20897, then 0xc9 and the version), and the program's SPIR-V module from +0x128"""
    vc, pc, vk, pk = struct.unpack_from('<4I', d, 0)
    out = []
    for st, c, k in (('vs', vc, vk), ('ps', pc, pk)):
        if c + CONTROL_SIZE > len(d) or not is_control(d[c:c + 8]): _red(f'{label}: sead shader {st} control section at {c:#x} is not an NVN2 control')
        n = u32(d, c + 0x30) or CONTROL_SIZE
        ctl = d[c:c + n]; code = d[k:k + u32(d, c + 0x44)]
        why = check(st, ctl, code)
        if why: _red(f'{label}: sead shader {st}: {why}')
        out.append((f'{label}#SEAD#var0', st, ctl, code))
    return out
