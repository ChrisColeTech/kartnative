#pragma once
#include <stdint.h>
#include <stddef.h>
#include <sys/types.h>

typedef struct {
    uint64_t a, b;       // directory: first child/count; file: package offset/logical size
    uint64_t stored;     // file bytes in the container (b for an uncompressed file)
    uint32_t name;
    uint16_t nlen;
    uint8_t type, codec; // type: directory 0/file 1; codec: raw 0/zstd 1
} romfs_ent;

typedef struct romfs_container {
    int fd;
    uint64_t n;
    romfs_ent *e;
    char *names;
    void *private;
    int nup;
} romfs_container;

romfs_container *romfs_container_open(const char *path);
void romfs_container_close(romfs_container *m);
ssize_t romfs_container_pread(romfs_container *m, uint32_t entry, void *buf, size_t size, uint64_t offset);
