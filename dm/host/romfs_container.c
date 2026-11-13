#include "romfs_container.h"
#include "romfs_image.h"
#include "../third_party/zstd/zstd.h"
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <pthread.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

enum { NUP_TOC_MAX = 256 << 20, NUP_READ = 1 << 20 };
enum { NUP_DIR_SIZE = 24, NUP_FILE_SIZE = 80, NUP_TITLE_SIZE = 64 };

typedef struct { uint8_t *data; uint32_t refs; uint64_t used; } cache_ent;
typedef struct { pthread_mutex_t lock; cache_ent *cache; size_t bytes, limit; uint64_t tick; } container_private;
typedef struct { uint32_t source, out, depth; } dir_job;
typedef struct { uint32_t source; const char *name; uint32_t nlen; uint8_t type; } child_ref;

static uint32_t rd32(const void *p) { uint32_t v; memcpy(&v, p, sizeof v); return v; }
static uint64_t rd64(const void *p) { uint64_t v; memcpy(&v, p, sizeof v); return v; }
static int add64(uint64_t a, uint64_t b, uint64_t *out) { *out = a + b; return *out >= a; }

static int read_full(int fd, void *buf, size_t size, uint64_t off) {
    size_t got = 0;
    if (off > INT64_MAX) { errno = EINVAL; return 0; }
    while (got < size) {
        ssize_t n = pread(fd, (char *)buf + got, size - got, (off_t)(off + got));
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) { if (!n) errno = EINVAL; return 0; }
        got += (size_t)n;
    }
    return 1;
}

static container_private *private_new(uint64_t n) {
    container_private *p = calloc(1, sizeof *p);
    if (!p || !(p->cache = calloc((size_t)n, sizeof *p->cache))) { free(p); return NULL; }
    pthread_mutex_init(&p->lock, NULL);
    p->limit = 256u << 20;
    return p;
}

static romfs_container *from_image(const char *path) {
    romfs_img *image = romfs_image_open(path);
    if (!image) return NULL;
    romfs_container *m = calloc(1, sizeof *m); size_t names_size = 0;
    if (m) m->fd = -1;
    if (!m || !(m->e = calloc((size_t)image->n, sizeof *m->e))) goto bad;
    for (uint64_t k = 0; k < image->n; k++) {
        if (SIZE_MAX - names_size < image->e[k].nlen) { errno = ENOMEM; goto bad; }
        names_size += image->e[k].nlen;
    }
    if (names_size > UINT32_MAX || !(m->names = malloc(names_size ? names_size : 1))) goto bad;
    size_t at = 0;
    for (uint64_t k = 0; k < image->n; k++) {
        const img_ent *in = &image->e[k]; romfs_ent *out = &m->e[k];
        out->a = in->a; out->b = in->b; out->stored = in->type ? in->b : 0;
        out->name = (uint32_t)at; out->nlen = in->nlen; out->type = in->type;
        memcpy(m->names + at, image->names + in->name, in->nlen); at += in->nlen;
    }
    m->fd = image->fd; image->fd = -1; m->n = image->n;
    if (!(m->private = private_new(m->n))) goto bad;
    romfs_image_close(image);
    return m;
bad: {
    int e = errno ? errno : ENOMEM;
    if (m) { if (m->fd >= 0) close(m->fd); free(m->names); free(m->e); free(m); }
    romfs_image_close(image); errno = e; return NULL;
    }
}

static int child_cmp(const void *av, const void *bv) {
    const child_ref *a = av, *b = bv; size_t n = a->nlen < b->nlen ? a->nlen : b->nlen;
    int c = memcmp(a->name, b->name, n);
    return c ? c : (a->nlen > b->nlen) - (a->nlen < b->nlen);
}

static int valid_name(const char *name, uint32_t n) {
    return n && n <= 0x300 && !memchr(name, 0, n) && !memchr(name, '/', n)
        && !(n == 1 && name[0] == '.') && !(n == 2 && !memcmp(name, "..", 2));
}

static romfs_container *from_nup(const char *path) {
    int fd = open(path, O_RDONLY); struct stat st; uint8_t head[48]; uint8_t *table = NULL;
    romfs_container *m = NULL; uint8_t *seen_dir = NULL, *seen_file = NULL; dir_job *jobs = NULL;
    if (fd < 0) return NULL;
    if (fstat(fd, &st) || st.st_size < 48 || !read_full(fd, head, sizeof head, 0)) goto bad;
    errno = EINVAL;
    if (memcmp(head, "NUP\1", 4)) goto bad;
    uint64_t toc_at = rd32(head + 8);
    if (toc_at > (uint64_t)st.st_size - 48 || !read_full(fd, head, sizeof head, toc_at) || memcmp(head, "TOC\1", 4)) goto bad;
    uint64_t nt = rd32(head + 4), nd = rd64(head + 8), nf = rd64(head + 16), pool_at = rd64(head + 24), pool_size = rd64(head + 32), table_size;
    if (!nt || nt > 4096 || !nd || nd > UINT32_MAX || nf > UINT32_MAX || pool_at > NUP_TOC_MAX || pool_size > NUP_TOC_MAX
        || !add64(pool_at, pool_size, &table_size) || table_size > NUP_TOC_MAX || table_size > (uint64_t)st.st_size - toc_at || table_size > SIZE_MAX) goto bad;
    uint64_t dirs_at, files_at, children_at;
    if (!add64(48, nt * NUP_TITLE_SIZE, &dirs_at) || !add64(dirs_at, nd * NUP_DIR_SIZE, &files_at)
        || !add64(files_at, nf * NUP_FILE_SIZE, &children_at) || children_at > pool_at) goto bad;
    if (!(table = malloc((size_t)table_size)) || !read_full(fd, table, (size_t)table_size, toc_at)) goto bad;
    uint64_t title_at = 48 + (nt - 1) * NUP_TITLE_SIZE; uint32_t root = rd32(table + title_at + 16);
    if (root >= nd || nd + nf > UINT32_MAX || !(m = calloc(1, sizeof *m))
        || !(m->e = calloc((size_t)(nd + nf), sizeof *m->e)) || !(m->names = malloc(pool_size ? (size_t)pool_size : 1))
        || !(seen_dir = calloc((size_t)nd, 1)) || (nf && !(seen_file = calloc((size_t)nf, 1)))
        || !(jobs = malloc((size_t)nd * sizeof *jobs))) goto bad;
    m->fd = fd; m->n = 1; m->nup = 1; seen_dir[root] = 1;
    uint64_t njob = 1, next_job = 0, names_used = 0, names_cap = pool_size ? pool_size : 1;
    jobs[0] = (dir_job){root, 0, 0};
    while (next_job < njob) {
        dir_job job = jobs[next_job++]; const uint8_t *d = table + dirs_at + (uint64_t)job.source * NUP_DIR_SIZE;
        uint32_t first = rd32(d + 8), count = rd32(d + 12);
        if (job.depth > 64 || first > (pool_at - children_at) / 4 || count > (pool_at - children_at) / 4 - first) goto bad;
        child_ref *children = count ? malloc((size_t)count * sizeof *children) : NULL;
        if (count && !children) goto bad;
        int children_ok = 1;
        for (uint32_t j = 0; j < count; j++) {
            uint32_t source = rd32(table + children_at + (uint64_t)(first + j) * 4); uint8_t type = source >= nd;
            uint64_t index = type ? source - nd : source;
            if ((!type && index >= nd) || (type && index >= nf)) { children_ok = 0; break; }
            const uint8_t *row = table + (type ? files_at + index * NUP_FILE_SIZE : dirs_at + index * NUP_DIR_SIZE);
            uint32_t no = rd32(row), nl = rd32(row + 4);
            if (no > pool_size || nl > pool_size - no || !valid_name((const char *)table + pool_at + no, nl)) { children_ok = 0; break; }
            children[j] = (child_ref){source, (const char *)table + pool_at + no, nl, type};
        }
        if (!children_ok) { free(children); goto bad; }
        qsort(children, count, sizeof *children, child_cmp);
        for (uint32_t j = 1; j < count; j++) if (!child_cmp(&children[j - 1], &children[j])) { free(children); goto bad; }
        if (m->n > nd + nf - count) { free(children); goto bad; }
        m->e[job.out].a = m->n; m->e[job.out].b = count;
        for (uint32_t j = 0; j < count; j++) {
            child_ref *c = &children[j]; uint32_t out = (uint32_t)m->n++; romfs_ent *x = &m->e[out];
            if (names_used > UINT32_MAX - c->nlen) { free(children); goto bad; }
            if (names_used + c->nlen > names_cap) {
                uint64_t cap = names_cap;
                while (cap < names_used + c->nlen) cap = cap < UINT32_MAX / 2 ? cap * 2 : UINT32_MAX;
                char *grown = realloc(m->names, (size_t)cap);
                if (!grown) { free(children); goto bad; }
                m->names = grown; names_cap = cap;
            }
            x->name = (uint32_t)names_used; x->nlen = (uint16_t)c->nlen; x->type = c->type;
            memcpy(m->names + names_used, c->name, c->nlen); names_used += c->nlen;
            if (!c->type) {
                if (seen_dir[c->source] || njob >= nd) { free(children); goto bad; }
                seen_dir[c->source] = 1; jobs[njob++] = (dir_job){c->source, out, job.depth + 1};
            } else {
                uint32_t fi = c->source - (uint32_t)nd; const uint8_t *f = table + files_at + (uint64_t)fi * NUP_FILE_SIZE;
                if (seen_file[fi]) { free(children); goto bad; } seen_file[fi] = 1;
                x->b = rd64(f + 8); x->a = rd64(f + 16); x->stored = rd64(f + 24); x->codec = f[64];
                if (x->codec > 1 || (!x->codec && x->stored != x->b) || x->b > INT64_MAX || x->b > SIZE_MAX
                    || x->a > (uint64_t)st.st_size || x->stored > (uint64_t)st.st_size - x->a) { free(children); goto bad; }
            }
        }
        free(children);
    }
    if (!(m->private = private_new(m->n))) goto bad;
    free(jobs); free(seen_file); free(seen_dir); free(table);
    return m;
bad: {
    int e = errno ? errno : EINVAL;
    free(jobs); free(seen_file); free(seen_dir); free(table);
    if (m) { free(m->names); free(m->e); free(m); }
    close(fd); errno = e; return NULL;
    }
}

romfs_container *romfs_container_open(const char *path) {
    uint8_t magic[8]; int fd = open(path, O_RDONLY);
    if (fd < 0) return NULL;
    int ok = read_full(fd, magic, sizeof magic, 0); int e = errno; close(fd);
    if (!ok) { errno = e; return NULL; }
    if (!memcmp(magic, "DMROMFS1", 8)) return from_image(path);
    if (!memcmp(magic, "NUP\1", 4)) return from_nup(path);
    errno = EINVAL; return NULL;
}

void romfs_container_close(romfs_container *m) {
    if (!m) return;
    container_private *p = m->private;
    if (p) {
        for (uint64_t k = 0; k < m->n; k++) free(p->cache[k].data);
        pthread_mutex_destroy(&p->lock); free(p->cache); free(p);
    }
    if (m->fd >= 0) close(m->fd); free(m->names); free(m->e); free(m);
}

static uint8_t *decompress_entry(romfs_container *m, const romfs_ent *x) {
    uint8_t *out = malloc(x->b ? (size_t)x->b : 1), *in = malloc(NUP_READ);
    ZSTD_DCtx *dctx = ZSTD_createDCtx();
    if (!out || !in || !dctx) { free(out); free(in); ZSTD_freeDCtx(dctx); errno = ENOMEM; return NULL; }
    if (ZSTD_isError(ZSTD_DCtx_setParameter(dctx, ZSTD_d_windowLogMax, 31))) goto invalid;
    ZSTD_inBuffer input = {in, 0, 0}; ZSTD_outBuffer output = {out, (size_t)x->b, 0};
    uint64_t left = x->stored, at = x->a; size_t hint = 1;
    while (hint) {
        if (input.pos == input.size && left) {
            input.size = left < NUP_READ ? (size_t)left : NUP_READ; input.pos = 0;
            if (!read_full(m->fd, in, input.size, at)) goto fail;
            at += input.size; left -= input.size;
        }
        size_t ip = input.pos, op = output.pos;
        hint = ZSTD_decompressStream(dctx, &output, &input);
        if (ZSTD_isError(hint) || (input.pos == ip && output.pos == op)) goto invalid;
        if (!left && input.pos == input.size && hint) goto invalid;
    }
    if (left || input.pos != input.size || output.pos != output.size) goto invalid;
    free(in); ZSTD_freeDCtx(dctx); return out;
invalid:
    errno = EINVAL;
fail:
    free(out); free(in); ZSTD_freeDCtx(dctx); return NULL;
}

ssize_t romfs_container_pread(romfs_container *m, uint32_t entry, void *buf, size_t size, uint64_t offset) {
    if (!m || entry >= m->n || !m->e[entry].type) { errno = EINVAL; return -1; }
    const romfs_ent *x = &m->e[entry];
    if (offset >= x->b) return 0;
    if (size > x->b - offset) size = (size_t)(x->b - offset);
    if (!x->codec) return pread(m->fd, buf, size, (off_t)(x->a + offset));
    container_private *p = m->private; uint8_t *data = NULL, *decoded = NULL; int cached = 0;
    pthread_mutex_lock(&p->lock);
    cache_ent *ce = &p->cache[entry];
    if (ce->data) { data = ce->data; ce->refs++; ce->used = ++p->tick; cached = 1; }
    pthread_mutex_unlock(&p->lock);
    if (!data && !(decoded = decompress_entry(m, x))) return -1;
    if (!data) {
        pthread_mutex_lock(&p->lock);
        if (ce->data) { data = ce->data; ce->refs++; ce->used = ++p->tick; cached = 1; }
        else if (x->b <= p->limit) {
            while (p->bytes > p->limit - (size_t)x->b) {
                uint64_t oldest = UINT64_MAX, victim = UINT64_MAX;
                for (uint64_t k = 0; k < m->n; k++) if (p->cache[k].data && !p->cache[k].refs && p->cache[k].used < oldest) { oldest = p->cache[k].used; victim = k; }
                if (victim == UINT64_MAX) break;
                p->bytes -= (size_t)m->e[victim].b; free(p->cache[victim].data); p->cache[victim].data = NULL;
            }
            if (p->bytes <= p->limit - (size_t)x->b) {
                ce->data = data = decoded; decoded = NULL; ce->refs = 1; ce->used = ++p->tick; p->bytes += (size_t)x->b; cached = 1;
            }
        }
        pthread_mutex_unlock(&p->lock);
        if (!data) data = decoded;
    }
    memcpy(buf, data + offset, size);
    if (cached) { pthread_mutex_lock(&p->lock); ce->refs--; pthread_mutex_unlock(&p->lock); }
    free(decoded);
    return (ssize_t)size;
}
