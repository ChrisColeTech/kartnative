#!/usr/bin/env python3
"""NVN2 programs from shader_split.py's packages → one file set per key for SM86 translator work, and an instruction census.
Usage: nvn2_dump.py <package dir> <shader_map.tsv> <out dir> <census.tsv> [<NAK sm70_encode.rs, for opcode names>]
Per NVN2 key in the map (Switch 1 records are left out):
  <out dir>/<key>.bin        the code area up to control +0x38: a graphics stage's 0x80-byte program header (Maxwell SPH in its first 0x50 bytes) and its SM86 instructions,
                             a compute stage's instructions only (it has no header)
  <out dir>/<key>.c1.bin     the constant segment the program reads as c[1], when it has one (control +0x40, +0x3c bytes)
  <out dir>/<key>.meta.json  stage, sizes, compute thread group and shared memory, the decoded control fields and SPH, the whole control section (hex) and where the key came from
census.tsv: per 12-bit opcode (bits 0..11; bits 9..11 are the operand form of the ALU opcodes) the instruction count, the number of programs using it, those programs by stage,
the most common modifier bits (72..90) and a program that uses it; then per-stage program counts, the task and mesh programs, and how the graphics headers decode as Maxwell SPH.
Instructions are 16 bytes (Mesa NAK's sm70_encode.rs layout); an opcode name is NAK's op for that encoding when the NAK source is given (names only, read at run time)."""
import collections, json, os, re, struct, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import nvn2_shader as nvn2


def records(path):
    """(index, control, code) of each record of a package file: 'BTCDBTCD', +0x28 control length, +0x2c code length, from +0x38 the code area then the control section"""
    d = open(path, 'rb').read()
    at, i = 0, 0
    while at < len(d):
        if d[at:at + 8] != b'BTCDBTCD': sys.exit(f'[RED] {path}: no record separator at {at:#x}')
        nctl, ncode = struct.unpack_from('<2I', d, at + 0x28)
        k = at + 0x38
        yield i, d[k + ncode:k + ncode + nctl], d[k:k + ncode]
        at = k + ncode + nctl; i += 1


def nak_names(path):
    """{12-bit opcode: name, 9-bit base opcode: name} from NAK's SM70 encoder source: each `impl SM70Op for Op<Name>`'s 3-digit hex literals"""
    names, bases = {}, collections.defaultdict(set)
    s = open(path).read()
    for b in re.split(r'\nimpl\s+SM70Op\s+for\s+', s)[1:]:
        m = re.match(r'Op(\w+)', b)
        if not m: continue
        body = b.split('\nimpl', 1)[0]
        for lit in re.findall(r'\b0x([0-9a-f]{3})\b', body[body.find('fn encode'):]):
            v = int(lit, 16); names.setdefault(v, m.group(1)); bases[v & 0x1ff].add(m.group(1))
    return names, {k: '/'.join(sorted(v)) for k, v in bases.items()}


def main():
    pkg, mapf, out, census = sys.argv[1:5]
    names, bases = nak_names(sys.argv[5]) if len(sys.argv) > 5 else ({}, {})
    os.makedirs(out, exist_ok=True)
    rows = [ln.rstrip('\n').split('\t') for ln in open(mapf) if not ln.startswith('#') and not ln.startswith('package\t')]
    byfile = collections.defaultdict(dict)
    for name, idx, _stage, key, src in rows: byfile[name][int(idx)] = (key, src)
    ops = collections.Counter(); progs = collections.defaultdict(set); stage_progs = collections.defaultdict(collections.Counter)
    mods = collections.defaultdict(collections.Counter); first = {}; per_stage = collections.Counter(); taskmesh = []; sph_facts = collections.Counter()
    n = 0
    for name in sorted(byfile):
        for i, ctl, code in records(os.path.join(pkg, name)):
            key, src = byfile[name][i]
            if not nvn2.is_control(ctl): continue
            f = nvn2.control(ctl); st = f['stage']; n += 1
            per_stage[nvn2.NAME[st]] += 1
            if st in ('ts', 'ms'): taskmesh.append((nvn2.NAME[st], key, src))
            meta = {'key': key, 'stage': nvn2.NAME[st], 'source': src, 'header_size': 0 if st == 'cs' else nvn2.HEADER_SIZE,
                    'code_size': f['code_end'] - (0 if st == 'cs' else nvn2.HEADER_SIZE), 'code_area_size': f['code_size'], 'c1_size': f['c1_size'],
                    'registers': f['registers'], 'control': f, 'control_hex': ctl[:nvn2.CONTROL_SIZE].hex()}
            if len(ctl) > nvn2.CONTROL_SIZE: meta['control_debug_blob_size'] = len(ctl) - nvn2.CONTROL_SIZE
            if st == 'cs': meta['local_size'], meta['shared_size'], meta['local_mem_size'] = f['local'], f['shared'], None   # compute local memory: no field identified yet
            else:
                h = nvn2.sph(code); meta['sph'] = h; meta['local_mem_size'] = h['local_low'] + h['local_high']
                ok = h['version'] == 4 and h['type'] == (2 if st == 'ps' else 1)
                sph_facts[(nvn2.NAME[st], f'SPH type {h["type"]} version {h["version"]} shader type {h["stage"]}', 'consistent' if ok else 'unexpected')] += 1
                if st == 'ms' and f['c1_start'] < f['code_size'] and not f['c1_size']: meta['mesh_tail_sph'] = nvn2.sph(code[f['c1_start']:])
            open(os.path.join(out, key + '.bin'), 'wb').write(code[:f['code_end']])
            if f['c1_size']: open(os.path.join(out, key + '.c1.bin'), 'wb').write(nvn2.c1(ctl, code))
            with open(os.path.join(out, key + '.meta.json'), 'w') as fj: json.dump(meta, fj, indent=1, sort_keys=True)
            ins = nvn2.instructions(ctl, code)
            for o in range(0, len(ins) - 15, 16):
                lo, hi = struct.unpack_from('<QQ', ins, o)
                op = lo & 0xfff
                ops[op] += 1; mods[op][hi >> 8 & 0x7ffff] += 1
                if key not in progs[op]: progs[op].add(key); stage_progs[op][nvn2.NAME[st]] += 1; first.setdefault(op, key)
    stages = ['vertex', 'fragment', 'geometry', 'tess_ctrl', 'tess_eval', 'compute', 'task', 'mesh']
    with open(census, 'w') as f:
        f.write(f'# SM86 instruction census of {n} distinct NVN2 programs (dm/gfx/tools/nvn2_dump.py over shader_split.py packages)\n')
        f.write('# opcode = bits 0..11 (form = bits 9..11, base = bits 0..8); mods = most common values of bits 72..90 (hex) with counts; name: Mesa NAK sm70_encode.rs (exact 12-bit literal, else ~ by base)\n')
        f.write('opcode\tbase\tform\tname\tcount\tprograms\t' + '\t'.join(stages) + '\tmods\texample\n')
        for op in sorted(ops, key=lambda o: (-len(progs[o]), o)):
            nm = names.get(op) or ('~' + bases[op & 0x1ff] if op & 0x1ff in bases else '')
            m = ' '.join(f'{v:x}:{c}' for v, c in mods[op].most_common(4))
            f.write(f'{op:03x}\t{op & 0x1ff:03x}\t{op >> 9}\t{nm}\t{ops[op]}\t{len(progs[op])}\t' + '\t'.join(str(stage_progs[op][s]) for s in stages) + f'\t{m}\t{first[op]}\n')
        f.write('\n# programs per stage\n')
        for s in stages:
            if per_stage[s]: f.write(f'#stage\t{s}\t{per_stage[s]}\n')
        f.write('\n# task and mesh programs: stage, key, source\n')
        for t in taskmesh: f.write('#taskmesh\t' + '\t'.join(t) + '\n')
        f.write('\n# graphics program headers read as a Maxwell SPH (first 0x50 bytes): stage, decoded type, programs\n')
        for (s, desc, ok), c in sorted(sph_facts.items()): f.write(f'#sph\t{s}\t{desc}\t{ok}\t{c}\n')
    print(f'{n} NVN2 programs (' + ', '.join(f'{s} {per_stage[s]}' for s in stages if per_stage[s]) + f'), {len(ops)} opcodes, {sum(ops.values())} instructions → {out}, {census}')


if __name__ == '__main__':
    main()
