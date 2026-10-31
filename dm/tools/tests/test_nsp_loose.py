"""NSP script: a loose dump (a folder with exefs/ holding main and main.npdm, and romfs/; Mario Kart World's dump) is built from where it is: its title ID from main.npdm,
its version from the profile's build rows, its files linked where an extraction would be, no keys needed. A folder of packages is not a loose dump. No game files or keys needed."""
import collections
import importlib.util
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest import mock

REPO = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location('build_ipa_from_nsp', REPO / 'scripts/build_ipa_from_nsp.py')
if spec is None or spec.loader is None: raise ImportError('scripts/build_ipa_from_nsp.py')
nsp = importlib.util.module_from_spec(spec); spec.loader.exec_module(nsp)
sys.path.insert(0, str(REPO / 'dm/games')); sys.path.insert(0, str(Path(__file__).resolve().parent))
import profile_guess   # noqa: E402
from test_profile_guess import npdm_bytes   # noqa: E402

APP = 0x0400C3F00006E000
MAIN, SDK = '9e244438c3d8ef1c703a6a85ef99c4f0', '90ebba182862b9ecdc21fcb87b999012'


def nso(path, bid):   # enough of an NSO for build_id: the magic and the build ID at 0x40
    path.write_bytes(b'NSO0' + bytes(0x3c) + bytes.fromhex(bid) + bytes(0x60))


class Profile:   # what loose_version reads of a game profile
    def __init__(self, rows): self.rows = rows
    def builds(self): return self.rows


class Loose(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp()); self.addCleanup(shutil.rmtree, self.tmp)
        self.said = []
        for target, value in (('say', self.said.append), ('pg', profile_guess), ('PROG', mock.MagicMock())):
            cm = mock.patch.object(nsp, target, value, create=True); cm.__enter__(); self.addCleanup(cm.__exit__, None, None, None)
        self.dump = self.tmp / 'base'
        (self.dump / 'exefs').mkdir(parents=True); (self.dump / 'romfs' / 'Shader').mkdir(parents=True)
        nso(self.dump / 'exefs/main', MAIN); nso(self.dump / 'exefs/sdk', SDK)
        (self.dump / 'exefs/main.npdm').write_bytes(npdm_bytes(0x300, program=APP))
        (self.dump / 'romfs/Shader/a.bfsha.zs').write_bytes(b'x')

    def test_detects_a_loose_dump(self):
        d = nsp.loose_dump(self.dump)
        self.assertEqual((d.exefs, d.romfs, d.nacp), (self.dump / 'exefs', self.dump / 'romfs', None))
        self.assertEqual(nsp.loose_title(d), APP)   # the program ID in main.npdm's ACI0
        self.assertTrue(profile_guess.switch2(d.exefs / 'main.npdm'))

    def test_folder_names_in_any_case(self):
        (self.dump / 'exefs').rename(self.dump / 'ExeFS'); (self.dump / 'romfs').rename(self.dump / 'RomFS')
        d = nsp.loose_dump(self.dump)
        self.assertEqual((d.exefs.name, d.romfs.name), ('ExeFS', 'RomFS'))

    def test_control_data_beside_it(self):
        (self.dump / 'control.nacp').write_bytes(bytes(0x4000))
        self.assertEqual(nsp.loose_dump(self.dump).nacp, self.dump / 'control.nacp')

    def test_a_package_folder_is_not_one(self):
        pkg = self.tmp / 'nsps'; pkg.mkdir(); (pkg / f'Game [{APP:016X}][v0].nsp').write_bytes(b'PFS0' + bytes(12))
        self.assertIsNone(nsp.loose_dump(pkg))

    def test_exefs_without_romfs(self):
        shutil.rmtree(self.dump / 'romfs')
        with self.assertRaises(nsp.BuildError): nsp.loose_dump(self.dump)

    def test_version_from_the_build_rows(self):
        d = nsp.loose_dump(self.dump)
        g = Profile({'v0': {'display': '1.0.0', 'main': MAIN, 'sdk': SDK}, 'v65536': {'display': '1.1.0', 'main': 'ff' * 16, 'sdk': SDK}})
        self.assertEqual(nsp.loose_version(d, g, ''), 'v0')
        self.assertEqual(nsp.loose_version(d, g, 'v65536'), 'v65536')   # -p names it
        self.assertEqual(nsp.loose_version(d, Profile({'v65536': {'display': '1.1.0', 'main': 'ff' * 16}}), ''), 'v0')   # no row matches: v0
        self.assertEqual(nsp.loose_version(d, None, ''), 'v0')   # no profile yet (the survey)

    def test_linked_where_an_extraction_would_be(self):
        d = nsp.loose_dump(self.dump); ex = self.tmp / 'build/extract'
        for _ in range(2):   # a second build keeps the links
            dst = nsp.link_loose(ex, 'v0', d)
            self.assertEqual(dst, ex / 'v0')
            self.assertEqual((dst / 'ExeFS').readlink(), self.dump / 'exefs')
            self.assertEqual(nsp.rfs.source(dst), dst / 'RomFS')
            self.assertTrue((nsp.rfs.source(dst) / 'Shader/a.bfsha.zs').is_file())
            self.assertFalse((dst / 'control.nacp').exists())
            self.assertEqual(nsp.build_mismatch(dst, 'v0', mock.Mock(builds=lambda: {'v0': {'display': '1.0.0', 'main': MAIN, 'sdk': SDK}})), [])

    def test_never_over_an_extraction(self):
        d = nsp.loose_dump(self.dump); ex = self.tmp / 'build/extract'
        (ex / 'v0').mkdir(parents=True); (ex / 'v0/RomFS.img').write_bytes(b'')
        with self.assertRaises(nsp.BuildError): nsp.link_loose(ex, 'v0', d)
        (ex / 'v0/RomFS.img').unlink(); (ex / 'v0/ExeFS').mkdir()
        with self.assertRaises(nsp.BuildError): nsp.link_loose(ex, 'v0', d)
        self.assertTrue((self.dump / 'exefs/main').is_file())   # the dump is never touched

    def test_provisional_profile_has_the_switch2_key(self):
        F = {'sdk': 19, 'program': APP, 'mods': {'main': {'bid': MAIN}, 'sdk': {'bid': SDK}},
             'romfs': {'dirs': collections.Counter({'Shader': 1}), 'extb': {}, 'exts': {}, 'extc': {}, 'biggest': (1, 'Shader/a.bfsha.zs')}}
        G = {'modules': ('main sdk', ''), 'switch2': ('on', 'main.npdm\'s ACID has the Switch 2 layout')}
        with mock.patch.object(profile_guess, 'guess', lambda _: G):
            nsp.write_profile(self.tmp / 'profile' / f'{APP:016X}', APP, {'F': F, 'name': '', 'display': '?', 'version': 'v0', 'survey': 'survey.md'})
        rows = (self.tmp / 'profile' / f'{APP:016X}' / 'game.tsv').read_text().splitlines()
        self.assertIn('switch2\ton', rows)
        self.assertIn(f'build\tv0\t?\tmain={MAIN}\tsdk={SDK}', rows)
        G.pop('switch2'); (self.tmp / 'profile' / f'{APP:016X}' / 'game.tsv').unlink()
        with mock.patch.object(profile_guess, 'guess', lambda _: G):   # a Switch 1 program has no such row
            nsp.write_profile(self.tmp / 'profile' / f'{APP:016X}', APP, {'F': F, 'name': '', 'display': '?', 'version': 'v0', 'survey': 'survey.md'})
        self.assertFalse(any(r.startswith('switch2') for r in (self.tmp / 'profile' / f'{APP:016X}' / 'game.tsv').read_text().splitlines()))


if __name__ == '__main__':
    unittest.main()
