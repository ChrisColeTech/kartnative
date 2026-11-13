"""NSP script: a selected NUP reads RomFS in place and materializes only ExeFS."""
import importlib.util
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest import mock

REPO = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(Path(__file__).resolve().parent))
from test_nup_read import write_nup   # noqa: E402

spec = importlib.util.spec_from_file_location('build_ipa_from_nsp_nup', REPO / 'scripts/build_ipa_from_nsp.py')
if spec is None or spec.loader is None: raise ImportError('scripts/build_ipa_from_nsp.py')
script = importlib.util.module_from_spec(spec); spec.loader.exec_module(script)

APP = 0x0400C3F00006E000
MAIN = '9e244438c3d8ef1c703a6a85ef99c4f0'
SDK = '90ebba182862b9ecdc21fcb87b999012'


def nso(build): return b'NSO0' + bytes(0x3c) + bytes.fromhex(build) + bytes(0x60)


class Profile:
    def builds(self): return {'v0': {'display': '1.0.0', 'main': MAIN, 'sdk': SDK}}


class NupInput(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp()); self.addCleanup(shutil.rmtree, self.tmp)
        self.package = self.tmp / 'game.nup'
        self.files = {'System/marker': b'marker', 'Shader/packed': b'packed' * 100}
        write_nup(self.package, self.files, exefs={'main': nso(MAIN), 'main.npdm': bytes(0x100), 'sdk': nso(SDK)}, compress=('Shader/packed',))
        cm = mock.patch.object(script, 'PROG', mock.MagicMock()); cm.__enter__(); self.addCleanup(cm.__exit__, None, None, None)

    def test_detect_version_and_link_in_place(self):
        dump = script.nup_dump(self.package)
        self.assertEqual(script.loose_title(dump), APP)
        self.assertEqual(script.loose_version(dump, Profile(), ''), 'v0')
        self.assertEqual(script.nup_build_id(dump, 'main'), MAIN)
        dst = script.link_nup(self.tmp / 'build/extract', 'v0', dump)
        self.assertEqual((dst / 'ExeFS/main').read_bytes(), nso(MAIN))
        self.assertTrue((dst / 'RomFS.nup').is_symlink())
        self.assertEqual((script.rfs.source(dst) / 'Shader/packed').read_bytes(), self.files['Shader/packed'])
        self.assertEqual((dst / 'RomFS.nup').resolve(), self.package.resolve())
        self.assertEqual(script.link_nup(self.tmp / 'build/extract', 'v0', dump), dst)

    def test_refuses_ambiguous_or_non_nup_inputs(self):
        (self.tmp / 'second.nup').write_bytes(self.package.read_bytes())
        with self.assertRaisesRegex(script.BuildError, 'pass the full path'): script.nup_dump(self.tmp)
        self.assertEqual(script.nup_dump(self.package).path, self.package)
        (self.tmp / 'second.nup').unlink(); self.package.write_bytes(b'not a package')
        with self.assertRaises(script.BuildError): script.nup_dump(self.tmp)

    def test_refuses_an_explicit_non_nup_file(self):
        other = self.tmp / 'game.nsp'; other.write_bytes(b'not a NUP')
        with self.assertRaisesRegex(script.BuildError, 'not a .nup file'): script.nup_dump(other)


if __name__ == '__main__': unittest.main()
