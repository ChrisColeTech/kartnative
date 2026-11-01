"""NVN2 (Switch 2) shader binaries in shader_split.py (dm/gfx/tools/nvn2_shader.py): BNSH and BAHS holding NVN2 stages, agl's NDSR pack and sead's shader files are read,
keyed by the fixed 0xd8 control bytes + the code area, and dumped by nvn2_dump.py; Switch 1 control sections keep their keys. Synthetic bytes only, no game files."""
import hashlib
import json
import os
import struct
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(REPO / 'dm/tools/fuzz'))
sys.path.insert(0, str(REPO / 'dm/gfx/tools'))
import fuzz_shader_split as fz   # Switch 1 seeds (BNSH, BAHS) and shader_split with a profile
import nvn2_dump
import nvn2_shader as nvn2
ss = fz.ss

STAGE_ID = {'vs': 0, 'ps': 1, 'cs': 5, 'ts': 6, 'ms': 7}
NOP = struct.pack('<QQ', 0x7918, 0x000fc00000000000)   # SM86 NOP


def stage(st, n=4, c1=b'', tag=0):
    """(control section, code area) of one NVN2 stage: a 0x80-byte header with an SPH v4 (graphics only), n instructions, zeros to 0x100, then the c1 segment"""
    sph = b'' if st == 'cs' else struct.pack('<I', (2 if st == 'ps' else 1) | 4 << 5 | (5 if st == 'ps' else 1) << 10) + bytes(nvn2.HEADER_SIZE - 4)
    code = bytearray(sph + struct.pack('<QQ', 0x7802 | tag << 32, 0) + NOP * (n - 1))
    end = len(code); start = -end % 0x100 + end; code += bytes(start - end) + c1 + bytes(-len(c1) % 0x100)
    ctl = bytearray(nvn2.CONTROL_SIZE); ctl[:4] = nvn2.CTL_MAGIC
    struct.pack_into('<II', ctl, 4, nvn2.CONTROL_MAJOR, 11); struct.pack_into('<HH', ctl, 0xc, 0x170, 0xe)
    struct.pack_into('<I', ctl, 0x30, nvn2.CONTROL_SIZE); struct.pack_into('<4I', ctl, 0x38, end, len(c1), start, len(code)); struct.pack_into('<I', ctl, 0x4c, 0x20)
    ctl[0x56] = STAGE_ID[st]
    if st == 'cs': struct.pack_into('<4I', ctl, 0x58, 64, 1, 1, 0x900)
    if st == 'ps': struct.pack_into('<H', ctl, 0x62, 1)
    return bytes(ctl), bytes(code)


def key(ctl, code): return hashlib.sha256(ctl[:nvn2.CONTROL_SIZE] + code).hexdigest()[:16]


def bnsh(stages):
    """A BNSH with one variation whose program has the given {slot: (control, code)} (fz.bnsh's layout)"""
    d = bytearray(0x300); d[:8] = b'BNSH\0\0\0\0'; d[0x60:0x64] = b'grsc'
    struct.pack_into('<IQ', d, 0x7c, 1, 0x100); struct.pack_into('<Q', d, 0x110, 0x140)
    for i, (slot, (ctl, code)) in enumerate(sorted(stages.items())):
        desc = 0x200 + 0x40 * i; struct.pack_into('<Q', d, 0x148 + 8 * slot, desc)
        at = len(d); d += ctl + code
        struct.pack_into('<QQII', d, desc + 8, at, at + len(ctl), len(code), len(ctl))
    struct.pack_into('<I', d, 0x1c, len(d))
    return bytes(d)


def bahs(entries):
    """A BAHS (version 11) whose binary array holds one record per (kind, control, code): kind 0 vertex 1 fragment 3 compute 4 task 5 mesh; controls after the tables, codes in the region"""
    n = len(entries); d = bytearray(0x100 + 0x20 * n); d[:8] = b'BAHS\x0b\0\0\0'
    struct.pack_into('<II', d, 0x40, 8 + 0x20 * n, n)
    ctl_at = len(d); code = bytearray()
    for i, (kind, ctl, k) in enumerate(entries):
        rec = 0x48 + 0x20 * i; ent = rec + 0x10; c = ctl_at + nvn2.CONTROL_SIZE * i
        struct.pack_into('<II', d, rec, 0x20, kind); struct.pack_into('<IIIi', d, ent, len(code), 0, len(ctl), c - ent)
        code += k + bytes(-len(k) % 0x100)
    d += b''.join(c for _, c, _ in entries); d += bytes(-len(d) % 0x100)
    struct.pack_into('<II', d, 0x18, len(code), len(d))
    return bytes(d + code)


def scan(d, label='/fuzz'):
    out = []
    ss.scan_blob(d, label, out)
    ss.BAHS_VAR.clear()
    return out


class Control(unittest.TestCase):
    def test_fields(self):
        f = nvn2.control(stage('cs', c1=b'\1' * 0x30)[0])
        self.assertEqual((f['stage'], f['local'], f['shared'], f['c1_size'], f['code_size']), ('cs', [64, 1, 1], 0x900, 0x30, 0x200))
        self.assertEqual(nvn2.control(stage('ps')[0])['targets'], 1)

    def test_check(self):
        ctl, code = stage('vs')
        self.assertIsNone(nvn2.check('vs', ctl, code))
        self.assertIn('+0x56', nvn2.check('ps', ctl, code) or '')
        self.assertIn('+0x44', nvn2.check('vs', ctl, code[:-0x10]) or '')
        self.assertIn('SPH', nvn2.check('vs', ctl, bytes(0x80) + code[0x80:]) or '')

    def test_c1_and_instructions(self):
        ctl, code = stage('vs', n=3, c1=b'\x11' * 0x30)
        self.assertEqual(nvn2.c1(ctl, code), b'\x11' * 0x30)
        self.assertEqual(len(nvn2.instructions(ctl, code)), 3 * 16)
        self.assertEqual(len(nvn2.instructions(*stage('cs', n=3))), 3 * 16)   # compute: no header

    def test_switch1_control_is_not_nvn2(self):
        ctl, code = fz.VS
        self.assertFalse(nvn2.is_control(ctl))
        self.assertEqual(ss.ctl_len(ctl), 0x880)
        self.assertEqual(ss.skey(ctl, code), hashlib.sha256(ctl[:0x880] + code).hexdigest()[:16])

    def test_compute_c0(self):   # the driver's packing (sdk+0x5ebbb0) of two Mario Kart World layouts: agl NDSR's (push block 0, 3 storage, 1 texture, 2 images) and a 4-uniform one
        ctl = bytearray(stage('cs')[0]); ctl[0xa8] = 1
        ctl[0xa9] = 8; ctl[0xb2] = 3; ctl[0xb3] = 1; ctl[0xb6] = 2; struct.pack_into('<H', ctl, 0xba, 0x78)
        self.assertEqual(nvn2.compute_c0(bytes(ctl)), (0x78, [('push_block', 0, 0x10, 8), ('storage', 0, 0x30, 3), ('texture', 0, 0x60, 1), ('image', 0, 0x68, 2)]))
        ctl[0xa9] = 0; ctl[0xb1] = 4; ctl[0xb2] = 2; ctl[0xb6] = 0; struct.pack_into('<H', ctl, 0xba, 0x78)
        self.assertEqual(nvn2.compute_c0(bytes(ctl)), (0x78, [('uniform', 0, 0x10, 4), ('storage', 0, 0x50, 2), ('texture', 0, 0x70, 1)]))
        ctl[0xa9] = 3   # 3 push words take a 16-byte row: the tables no longer end at +0xba
        with self.assertRaises(ValueError): nvn2.compute_c0(bytes(ctl))
        ctl[0xa8] = 0
        self.assertIsNone(nvn2.compute_c0(bytes(ctl)))
        self.assertIsNone(nvn2.compute_c0(stage('ps')[0]))


class Containers(unittest.TestCase):
    def test_bnsh(self):
        vs, ps = stage('vs'), stage('ps', tag=1)
        found = scan(bnsh({0: vs, 4: ps}))
        self.assertEqual([(st, c, k) for _, st, c, k in found], [('vs', *vs), ('ps', *ps)])
        uniq = ss.uniq_keys(found)
        self.assertEqual(set(uniq), {key(*vs), key(*ps)})
        self.assertEqual(ss.var_pairs(found), {(key(*vs), key(*ps))})

    def test_bnsh_slot_must_match_the_control(self):
        with self.assertRaises(SystemExit) as e: scan(bnsh({0: stage('ps')}))
        self.assertIn('+0x56', str(e.exception))

    def test_bahs(self):
        vs, ps, cs, ts = stage('vs'), stage('ps', tag=1), stage('cs', c1=b'\2' * 8), stage('ts', tag=2)
        found = scan(bahs([(0, *vs), (1, *ps), (3, *cs), (4, *ts)]))
        self.assertEqual([(st, c, k) for _, st, c, k in found], [('vs', *vs), ('ps', *ps), ('cs', *cs), ('ts', *ts)])

    def test_switch1_bahs_and_bnsh_keep_their_keys(self):
        for d in (fz.bnsh(), fz.bahs()):
            found = scan(d)
            self.assertEqual([st for _, st, _, _ in found], ['vs', 'ps'])
            self.assertEqual(set(ss.uniq_keys(found)), {hashlib.sha256(c[:0x880] + k).hexdigest()[:16] for c, k in (fz.VS, fz.PS)})

    def test_ndsr(self):
        parts = [(0, *stage('vs')), (3, *stage('ps', tag=1)), (6, *stage('cs', tag=2))]
        n = 2 * len(parts) + 1; d = bytearray(b'NDSR\0\3\0\0\xff\xfe' + struct.pack('<H', n) + bytes(4) + bytes(8 * n)); d += bytes(-len(d) % 0x100)
        for i, (t, ctl, code) in enumerate(parts):
            c = len(d); d += ctl; k = len(d); d += code
            struct.pack_into('<4I', d, 0x10 + 0x10 * i, c, len(ctl) << 4 | t, k, len(code) << 4 | (t + 1))
        struct.pack_into('<II', d, 0x10 + 0x10 * len(parts), 0, 0x50 << 4 | 12)   # a data entry
        found = scan(bytes(d), '/agl_ndsr.pck')
        self.assertEqual([st for _, st, _, _ in found], ['vs', 'ps', 'cs'])
        self.assertEqual(ss.var_pairs(found), {(key(*parts[0][1:]), key(*parts[1][1:]))})

    def test_sead_debug_blob_is_not_in_the_key(self):
        def sead(blob):
            (vc, vk), (pc, pk) = stage('vs'), stage('ps', tag=1)
            vc = bytearray(vc + blob); struct.pack_into('<I', vc, 0x30, len(vc))
            d = bytearray(0x10) + vc; p = len(d); d += pc; v = len(d); d += vk; f = len(d); d += pk
            struct.pack_into('<4I', d, 0, 0x10, p, v, f)
            return bytes(d)
        a, b = scan(sead(b'\x97\x08\xf2\x37' + bytes(0x44))), scan(sead(b'\x97\x08\xf2\x37' + b'\1' * 0x44))
        self.assertEqual(len(a[0][2]), nvn2.CONTROL_SIZE + 0x48)   # the package keeps the whole control section
        ka, kb = ss.uniq_keys(a), ss.uniq_keys(b)
        self.assertEqual(set(ka), set(kb))
        self.assertTrue(all(ss.same_shader(ka[k][2:4], kb[k][2:4]) for k in ka))


class Dump(unittest.TestCase):
    def test_dump_and_census(self):
        shaders = {'vs': stage('vs', c1=b'\3' * 0x10), 'cs': stage('cs', n=2)}
        with tempfile.TemporaryDirectory() as t:
            pkg = os.path.join(t, 'pkg'); os.makedirs(pkg); rows = []
            for st, (ctl, code) in shaders.items():
                rec = bytearray(b'BTCDBTCD' + bytes(0x30)); struct.pack_into('<2I', rec, 0x28, len(ctl), len(code))
                open(os.path.join(pkg, f'{st}_x.bin'), 'wb').write(bytes(rec) + code + ctl); rows.append(f'{st}_x.bin\t0\t{st}\t{key(ctl, code)}\t/src')
            open(os.path.join(t, 'map.tsv'), 'w').write('# x\npackage\tindex\tstage\tdm_ctl_sha16\tsource\n' + '\n'.join(rows) + '\n')
            with mock.patch.object(sys, 'argv', ['nvn2_dump.py', pkg, os.path.join(t, 'map.tsv'), os.path.join(t, 'out'), os.path.join(t, 'census.tsv')]): nvn2_dump.main()
            vk, ck = key(*shaders['vs']), key(*shaders['cs'])
            self.assertEqual(open(os.path.join(t, 'out', vk + '.bin'), 'rb').read(), shaders['vs'][1][:nvn2.HEADER_SIZE + 4 * 16])
            self.assertEqual(open(os.path.join(t, 'out', vk + '.c1.bin'), 'rb').read(), b'\3' * 0x10)
            m = json.load(open(os.path.join(t, 'out', ck + '.meta.json')))
            self.assertEqual((m['stage'], m['code_size'], m['header_size'], m['local_size'], m['shared_size']), ('compute', 32, 0, [64, 1, 1], 0x900))
            census = open(os.path.join(t, 'census.tsv')).read()
            self.assertIn('918\t118\t4\t\t4\t2\t1\t0\t0\t0\t0\t1\t0\t0', census)   # NOP: 3 + 1 instructions in 2 programs
            self.assertIn('#sph\tvertex\tSPH type 1 version 4 shader type vs\tconsistent\t1', census)


if __name__ == '__main__':
    unittest.main()
