"""The .nup packed dump read as the RomFS image is: parsing, the virtual views of romfs.py, and
what a damaged package refuses (built here, no game data: the writer is the reader's mirror)."""
import struct
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
sys.path[:0] = [str(REPO / 'dm/tools')]
import nup
import romfs


def write_nup(path, files, exefs=(), compress=()):
    """A package from {relative path: bytes}; exefs is names or {name: bytes}, the names in compress
    are stored as one zstd frame (codec 1). Layout as nup.py documents it: header, then the table
    of contents (its header, the title, the directory and file entries, the child lists, the name
    pool), then the data. Names are pooled first so every region's offset is known before it is
    written; the reader (nup.py, and mkw-recomp's nup.c before it) is this writer's mirror."""
    from backports import zstd
    tree = {'': set()}
    for name in files:
        parts = name.split('/')
        for i in range(1, len(parts)):                        # every directory on the path
            tree.setdefault('/'.join(parts[:i]), set())
            tree['/'.join(parts[:i - 1]) if i > 1 else ''].add(parts[i - 1])
        tree['/'.join(parts[:-1])].add(parts[-1])             # the file itself
    romfs_files, exefs_files = sorted(files), sorted(exefs)
    exefs_data = exefs if isinstance(exefs, dict) else {name: b'' for name in exefs_files}
    dirs = sorted(tree)
    exefs_dir = len(dirs)                      # the ExeFS root is a directory of its own
    nd = len(dirs) + 1
    all_files = romfs_files + exefs_files
    file_index = {name: nd + i for i, name in enumerate(all_files)}
    nf = len(all_files)

    def entries(d):
        if d == exefs_dir: return [file_index[n] for n in exefs_files]
        out = []
        for kid in sorted(tree[dirs[d]]):
            full = f'{dirs[d]}/{kid}'.lstrip('/')
            out.append(dirs.index(full) if full in dirs else file_index[full])
        return out

    pool = bytearray()

    def pooled(name):
        raw = name.encode(); at = len(pool); pool.extend(raw); return at, len(raw)

    dir_names = [pooled('' if d == exefs_dir else dirs[d].rpartition('/')[2]) for d in range(nd)]
    file_names = {name: pooled(name.rpartition('/')[2]) for name in all_files}
    dirs_at, files_at = 0x30 + 0x40, 0x30 + 0x40 + nd * 24          # offsets from the TOC
    children_at = files_at + nf * 80
    pool_at = children_at + 4 * sum(len(entries(d)) for d in range(nd))
    data_at = 0x40 + pool_at + len(pool)
    data = bytearray()
    rows = {}
    for name in all_files:
        payload = files[name] if name in files else exefs_data[name]
        codec = 1 if name in compress else 0
        stored = zstd.compress(payload) if codec else payload
        rows[name] = (len(payload), data_at + len(data), len(stored), codec)
        data += stored
    dirs_blob, child_lists = bytearray(), bytearray()
    for d in range(nd):
        kids = entries(d)
        no, nl = dir_names[d]
        dirs_blob += struct.pack('<IIII8x', no, nl, len(child_lists) // 4, len(kids))
        child_lists += b''.join(struct.pack('<I', e) for e in kids)
    files_blob = bytearray()
    for name in all_files:
        no, nl = file_names[name]
        size, offset, stored_len, codec = rows[name]
        files_blob += struct.pack('<IIQQQ32sB15x', no, nl, size, offset, stored_len, b'\0' * 32, codec)
    toc = struct.pack('<4sIQQQQ', b'TOC\x01', 1, nd, nf, pool_at, len(pool)) + bytearray(8)
    title = struct.pack('<QIIII', 0x0400c3f00006e000, 0, exefs_dir, 0, 0) + bytearray(0x28)
    header = struct.pack('<4s4xI', b'NUP\x01', 0x40) + bytearray(0x34)
    Path(path).write_bytes(header + toc + title + dirs_blob + files_blob + child_lists + pool + data)
    return path


class ReadNup(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.raw = b'\x03\x02\x23\x07' + b'ABC\0' * 20000
        self.packed = bytes(range(256)) * 300
        self.files = {'a': b'prefix!', 'data/archive': self.raw, 'data/zero': b'',
                      'data/packed.module': self.packed, 'data/test.vert': b'#version 320 es\nvoid main() {}'}
        self.path = self.root / 'dump.nup'
        write_nup(self.path, self.files, exefs=('main', 'main.npdm'), compress=('data/packed.module',))
        self.virtual = romfs.Path(self.path)
        self.nup = nup.load(str(self.path))

    def test_metadata_paths_and_normal_output_paths(self):
        self.assertTrue(self.virtual.is_dir()); self.assertFalse(self.virtual.is_file())
        self.assertEqual((self.virtual / 'data/archive').read_bytes(), self.raw)
        self.assertEqual((self.virtual / 'data/packed.module').read_bytes(), self.packed)
        self.assertEqual((self.virtual / 'data/test.vert').read_text(), '#version 320 es\nvoid main() {}')
        self.assertEqual(sorted(p.name for p in self.virtual.iterdir()), ['a', 'data'])
        self.assertFalse((self.virtual / 'missing').exists())
        with self.assertRaises(FileNotFoundError): (self.virtual / 'missing').read_bytes()
        with self.assertRaises(OSError): (self.virtual / 'data/archive').open('wb')
        with romfs.open(self.root / 'output', 'wb') as f: f.write(b'normal output')
        self.assertEqual((self.root / 'output').read_bytes(), b'normal output')

    def test_seek_read_and_mmap(self):
        with romfs.open(self.virtual / 'data/archive', 'rb') as f:
            f.seek(-5, 2); self.assertEqual(f.read(100), self.raw[-5:])
            f.seek(len(self.raw) + 10); self.assertEqual(f.read(), b'')
            with romfs.mmap.mmap(f.fileno(), 0, access=romfs.mmap.ACCESS_READ) as m:
                self.assertEqual(m[:], self.raw); self.assertEqual(m.find(b'ABC'), 4)
        with romfs.open(self.virtual / 'data/zero', 'rb') as f: self.assertEqual(f.read(), b'')
        with romfs.open(self.virtual / 'data/packed.module', 'rb') as f:   # zstd: read, never mmap
            self.assertEqual(f.read(), self.packed)
            self.assertRaises(OSError, f.fileno)   # no fd: mmap would map the package, not the file

    def test_exefs_and_title(self):
        self.assertEqual(self.nup.title[0], 0x0400c3f00006e000)
        self.assertEqual([name for name, _, _ in self.nup.exefs()], ['main', 'main.npdm'])
        for name, size, reader in self.nup.exefs(): self.assertEqual(reader(), b'')

    def test_a_damaged_package_is_refused(self):
        bad = self.root / 'bad.nup'
        bad.write_bytes(b'XXXX' + self.path.read_bytes()[4:])
        self.assertFalse(nup.is_nup(str(bad)))
        short = self.root / 'short.nup'
        short.write_bytes(self.path.read_bytes()[:-8])   # cuts the data, not the table
        with self.assertRaises(nup.Error): nup.Nup(str(short))
        with self.assertRaises(OSError): nup.Nup(str(self.root / 'missing.nup'))


if __name__ == '__main__': unittest.main()
