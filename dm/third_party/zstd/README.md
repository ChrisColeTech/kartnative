# zstd decompressor

The decompression library from zstd v1.5.7 (upstream commit `f8745da6ff1ad1e7bab384bd1f9d742439278e99`), unmodified. Upstream: <https://github.com/facebook/zstd/releases/tag/v1.5.7> (source archive: <https://github.com/facebook/zstd/releases/download/v1.5.7/zstd-1.5.7.tar.gz>). License: BSD 3-Clause, see `LICENSE`.

This subset is for host-side `.nup` reads. It contains the nine C sources named by upstream's decompressor-only amalgamation, `xxhash.c` (required when those sources are compiled separately), the headers they include, and the public `zstd.h` and `zstd_errors.h` headers. Compression, dictionary building, legacy-format decoders, multithreading, and the optional amd64 assembly loop are omitted.

Build with `XXH_NAMESPACE=ZSTD_`, as upstream's library build does, and `ZSTD_DISABLE_ASM=1`, because the optional amd64 assembly source is not included. `DEBUGLEVEL` and `ZSTD_LEGACY_SUPPORT` both default to 0 in these sources.
