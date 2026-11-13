"""The Switch 2 .nup packed dump: its RomFS read in place, as the RomFS image is (romfs.py).

Why: a dump packed as one .nup (mkw-recomp's --romfs reads the same files) is 20+ GB; unpacking it
beside the package doubles what the disk holds for no gain. The build and the Mac host read the
package's own files at their offsets instead, the zstd-framed ones (codec 1) decompressed on read.

Layout (little-endian; reversed from dumps by mkw-recomp, runtime/sdk/fs/nup.c — no public spec):
  0x00  "NUP\x01"; u32 @0x08 = offset of the table of contents (TOC)
  TOC   "TOC\x01", u32 title count, u64 dir count, u64 file count, u64 name-pool offset (from the
        TOC), u64 name-pool size; the whole table runs from the TOC to the end of the name pool
  TOC+0x30: titles, 0x40 bytes each: u64 title id, u32 root dir, u32 exefs dir, u32 romfs dir, u32 version
  then dirs  (24 bytes): u32 name offset, u32 name length, u32 first child, u32 child count, 8 unused
  then files (80 bytes): u32 name offset, u32 name length, u64 size, u64 absolute offset, u64 stored
        size, 32-byte hash, u8 codec @0x40 (0 raw, 1 one zstd frame), 15 unused
  then a u32 child list: an index below the dir count is a dir, else a file (index - dir count)
The last title is the game (nup.c does the same); its exefs dir is the four ExeFS files, its romfs
dir the RomFS root this module presents. Raw files are validated to sit whole inside the package.

Usage: nup = load(path) (cached by the file's identity); romfs.Path('<dump>.nup/Data/...') reads it
through romfs.py once that module's _resolve has matched the .nup suffix; nup.exefs() lists the
ExeFS entries as (name, size, reader). Decompression uses backports.zstd (compression.zstd from
Python 3.14), the window limit raised to the format's 2 GiB as the frames are written --long.
"""
import builtins
import functools
import io
import os as _os
import struct
import sys
from types import SimpleNamespace

NUP_MAGIC = b'NUP\x01'
TOC_MAGIC = b'TOC\x01'
TOC_MAX = 256 << 20        # the whole table of contents is read; Mario Kart World's is 24 MiB
READ = 8 << 20             # compressed spans are read in chunks of this many bytes
TOC_HEAD = struct.Struct('<4sIQQQQ')       # magic, titles, dirs, files, pool offset, pool size
TITLE = struct.Struct('<QIIII')            # id, root, exefs, romfs, version (0x40 bytes, 12 trailing unused)
DIR = struct.Struct('<IIII8x')             # name offset, name length, first child, child count
FILE = struct.Struct('<IIQQQ32sB15x')      # name offset, name length, size, offset, stored, hash, codec


class Error(ValueError):
    """A .nup that is not a complete package (no or bad magic, tables out of range, bad names)."""


def _zstd():
    try:
        from backports import zstd
    except ImportError:
        try:
            from compression import zstd          # Python 3.14
        except ImportError:
            raise Error('reading a .nup needs the zstd module (pip install backports.zstd)')
    return zstd


def is_nup(path):
    try:
        with builtins.open(path, 'rb') as f: return f.read(4) == NUP_MAGIC
    except (OSError, TypeError): return False


def decompress(nup, node):
    """One zstd frame, streamed: whole-file entries can be larger than the compressed span by far.
    The window limit is raised to the format's 2 GiB as the frames are written zstd --long."""
    z = _zstd()
    limit = getattr(getattr(z, 'DecompressionParameter', None), 'window_log_max', None)
    d = z.ZstdDecompressor(options={limit: 31}) if limit is not None else z.ZstdDecompressor(max_window_size=1 << 31)
    out = bytearray()
    with builtins.open(nup.path, 'rb') as f:
        f.seek(node.offset)
        left = node.stored
        while left > 0:
            chunk = f.read(min(READ, left)); left -= len(chunk)
            out += d.decompress(chunk)
        while not d.eof:                       # drain what the last chunk left in the buffer
            more = d.decompress(b'')
            if not more and d.needs_input: break
            out += more
    if len(out) != node.size:
        raise Error(f'{nup.path}: {node.name} decompresses to {len(out)} of {node.size} bytes')
    return bytes(out)


def read_whole(nup, node):
    if node.codec: return decompress(nup, node)
    with builtins.open(nup.path, 'rb') as f:
        f.seek(node.offset)
        data = f.read(node.size)
    if len(data) != node.size: raise Error(f'{nup.path}: {node.name} cut short')
    return data


class NupFile(io.RawIOBase):
    """One entry: raw entries pread the package at the entry's offset (mmap goes through the same
    fd, so archive scans work as on an image entry); zstd entries decompress whole on open, and
    have no fileno: mmap would silently map the package's compressed bytes, so it fails instead."""
    def __init__(self, nup, k):
        self.nup, self.pos, self.fd, self.data = nup, 0, -1, None
        e = nup.e[k]
        if not e.is_file: raise IsADirectoryError(e.name)
        self.node = e
        if e.codec == 0:
            self.fd = _os.open(nup.path, _os.O_RDONLY)
            try:                                   # so romfs.mmap maps the entry, not the package
                import romfs
                romfs._fd_ranges[self.fd] = (e.offset, e.size)
            except ImportError: pass               # standalone use (the __main__ probe) reads whole
        else:
            self.data = decompress(nup, e)
    def readable(self): return True
    def seekable(self): return True
    def fileno(self):
        if self.fd < 0: raise io.UnsupportedOperation('a .nup zstd entry has no fd; read it whole')
        return self.fd
    def tell(self): return self.pos
    def seek(self, offset, whence=0):
        pos = offset + (self.pos if whence == 1 else self.node.size if whence == 2 else 0)
        if whence not in (0, 1, 2) or pos < 0: raise ValueError('invalid seek')
        self.pos = pos; return pos
    def readinto(self, buffer):
        size = min(len(buffer), max(0, self.node.size - self.pos))
        data = _os.pread(self.fd, size, self.node.offset + self.pos) if self.fd >= 0 \
            else self.data[self.pos:self.pos + size]
        buffer[:len(data)] = data; self.pos += len(data)
        return len(data)
    def close(self):
        if not self.closed and self.fd >= 0:
            try:
                import romfs
                romfs._fd_ranges.pop(self.fd, None)
            except ImportError: pass
            _os.close(self.fd)
        super().close()


class Nup:
    """The package: e[0] is the game's RomFS root; each node carries k, its index in e, so the
    lookups romfs.py makes are O(log children) with no search over the 200k-entry list."""
    def __init__(self, path):
        self.path = _os.path.abspath(path)
        self.stat = _os.stat(self.path)
        with builtins.open(path, 'rb') as f:
            head = f.read(12)
            if len(head) < 12 or head[:4] != NUP_MAGIC: raise Error(f'{path}: no NUP magic')
            toc_at, = struct.unpack_from('<I', head, 8)
            f.seek(toc_at); toc = f.read(TOC_HEAD.size)
            if len(toc) < TOC_HEAD.size or toc[:4] != TOC_MAGIC: raise Error(f'{path}: no TOC')
            _, nt, nd, nf, pool, pool_size = TOC_HEAD.unpack(toc)
            if not nt or nt > 4096 or nd > TOC_MAX or nf > TOC_MAX or pool > TOC_MAX or pool_size > TOC_MAX \
                    or pool + pool_size > TOC_MAX or toc_at + pool + pool_size > self.stat.st_size:
                raise Error(f'{path}: table of contents out of range')
            f.seek(toc_at)                             # the table runs from the TOC to the pool's end
            table = f.read(pool + pool_size)
        if len(table) != pool + pool_size: raise Error(f'{path}: table of contents unreadable')
        self.table, self.nd, self.nf = table, nd, nf
        self.dirs_at = 0x30 + nt * 0x40
        self.files_at = self.dirs_at + nd * DIR.size
        self.children_at = self.files_at + nf * FILE.size
        self.pool_at = pool
        self.title = TITLE.unpack_from(table, 0x30 + (nt - 1) * 0x40)
        self.e = []
        self.root = self._tree(self.title[3], 0, 64)     # the RomFS the virtual layer presents
        self.exefs_root = self._tree(self.title[2], 0, 64)   # the ExeFS the build materializes

    def _name(self, off, length):
        if not length or off + length > len(self.table) - self.pool_at:
            raise Error(f'{self.path}: bad name in the table of contents')
        raw = self.table[self.pool_at + off:self.pool_at + off + length]
        if b'/' in raw or b'\0' in raw: raise Error(f'{self.path}: bad name in the table of contents')
        return raw.decode()

    def _tree(self, dir_index, depth, limit):
        """The subtree at a directory, its children sorted by name (bytes) so a lookup is one
        binary search per component, as in an image (hle_fs.c img_find does the same)."""
        if dir_index >= self.nd or depth > limit: raise Error(f'{self.path}: bad directory')
        node = SimpleNamespace(name='', is_file=False, kids=[], k=len(self.e)); self.e.append(node)
        first, count = DIR.unpack_from(self.table, self.dirs_at + dir_index * DIR.size)[2:4]
        if first + count > (len(self.table) - self.children_at) // 4:
            raise Error(f'{self.path}: child list out of range')
        kids = []
        for j in range(first, first + count):
            c, = struct.unpack_from('<I', self.table, self.children_at + j * 4)
            is_dir = c < self.nd
            if not is_dir and c - self.nd >= self.nf: raise Error(f'{self.path}: file index out of range')
            at = self.dirs_at + c * DIR.size if is_dir else self.files_at + (c - self.nd) * FILE.size
            no, nl = struct.unpack_from('<II', self.table, at)
            name = self._name(no, nl)
            if is_dir:
                sub = self._tree(c, depth + 1, limit)
                sub.name = name; kids.append(sub)
            else:
                _, _, size, offset, stored, _, codec = FILE.unpack_from(self.table, at)
                if codec > 1: raise Error(f'{self.path}: unknown codec {codec} for {name}')
                if codec == 0 and stored != size:
                    raise Error(f'{self.path}: raw file {name} with a stored size unlike its size')
                if offset > self.stat.st_size or stored > self.stat.st_size - offset:
                    raise Error(f'{self.path}: file {name} past the end')
                kid = SimpleNamespace(name=name, is_file=True, size=size, offset=offset,
                                      stored=stored, codec=codec, kids=(), k=len(self.e))
                self.e.append(kid); kids.append(kid)
        node.kids = sorted(kids, key=lambda kid: kid.name.encode())
        return node

    # ---- the surface romfs.py's virtual layer calls on an image ----
    def entry(self, k):
        e = self.e[k]
        return (getattr(e, 'offset', 0), getattr(e, 'size', 0), 0, len(e.name.encode()),
                1 if e.is_file else 0, 0)
    def find(self, relative):
        node = self.root
        for part in relative.split('/'):
            if not part or part == '.': continue
            if node.is_file: raise NotADirectoryError(relative)
            kids, lo, hi, want = node.kids, 0, len(node.kids), part.encode()
            while lo < hi:
                mid = (lo + hi) // 2
                if kids[mid].name.encode() < want: lo = mid + 1
                else: hi = mid
            if lo == len(kids) or kids[lo].name != part: raise FileNotFoundError(relative)
            node = kids[lo]
        return node.k
    def children(self, k):
        node = self.e[k]
        if node.is_file: raise NotADirectoryError(self.path)
        return [(kid.name, kid.k) for kid in node.kids]
    def file(self, k): return NupFile(self, k)

    # ---- the build's own reads ----
    def exefs(self):
        """The game's ExeFS entries as (name, size, reader): main, main.npdm, sdk, subsdk0."""
        return [(kid.name, kid.size, functools.partial(read_whole, self, kid))
                for kid in self.exefs_root.kids if kid.is_file]


@functools.lru_cache(maxsize=8)
def _load(path, identity):
    return Nup(path) if is_nup(path) else None


def load(path):
    """The package at path, or None when it is not a .nup (cached by the file's identity)."""
    st = _os.stat(path)
    return _load(_os.path.abspath(_os.fsdecode(path)), (st.st_ino, st.st_size, st.st_mtime_ns, st.st_ctime_ns))


if __name__ == '__main__':   # what the package holds: python3 nup.py <dump>.nup [romfs file...]
    try:
        nup = Nup(sys.argv[1])
        print(f'title {nup.title[0]:016x} v{nup.title[4]}, {nup.nd} dirs, {nup.nf} files')
        for name, size, _ in nup.exefs(): print(f'exefs {name} {size}')
        for want in sys.argv[2:]:
            k = nup.find(want)
            print(f'{want}: {"dir" if not nup.e[k].is_file else nup.e[k].size} bytes')
    except (Error, OSError, struct.error) as error: sys.exit(f'[RED] {error}')
