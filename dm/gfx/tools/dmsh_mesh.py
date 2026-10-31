"""Passthrough geometry as one Metal mesh shader (<vertex key>_bm<geometry key>).

A lean passthrough kernel (dmsh_geometry.lean) only decides, per input primitive, whether
the rasterizer gets it. A mesh threadgroup takes PRIMS triangles of a list draw: each thread
runs the vertex shader's pulling entry (dmsh_pull) for one index position and writes the
vertex out, keeps the words the kernel reads in threadgroup memory, and the first PRIMS
threads run the kernel body on them and cull their triangle when viewport 0's mask bit is
clear. No capture pass, no compute pass, no index compaction, no render pass break.

The kernel's own storage writes (and the subgroup operations that order them) run as in the
compute kernel: one triangle per thread, 32 consecutive triangles per SIMD group.
"""
import re
import dmsh_geometry, dmsh_pull

PRIMS = 32
DRAW_SLOT, GS_SLOT = 12, 13   # argument-table slots the vertex stage leaves free in a pulling pipeline
_BUILTIN_V = {'vertex_id': 'dm_vid', 'instance_id': 'dm_iid', 'base_instance': 'dm_bi', 'base_vertex': 'dm_bv'}
_BUILTIN_G = {'thread_position_in_grid': 'uint3(dm_p0 + dm_t, 0u, 0u)', 'thread_index_in_simdgroup': 'dm_sg'}


def _params(s):
    """Split a parameter list at top-level commas."""
    out, depth, cur = [], 0, ''
    for ch in s:
        depth += ch in '<([' ; depth -= ch in '>)]'
        if ch == ',' and not depth: out.append(cur.strip()); cur = ''
        else: cur += ch
    return out + ([cur.strip()] if cur.strip() else [])


def _strip_head(src):
    keep = [l for l in src.split('\n') if not re.match(r'\s*#include\b|\s*using namespace metal;', l)]
    return '\n'.join(keep)


def _entry(src, kind, name):
    m = re.search(r'\n%s (\w+) %s\(([^\n]*)\)\n\{\n' % (kind, name), src)
    if not m: raise ValueError(f'mesh: no {kind} {name}')
    return m


def compose(vs_src, gs_src, reads):
    """vs_src: the vertex shader's library source (<key>_b.metal); gs_src: the lean kernel
    (<geometry key>_bl3.metal) and the capture words it reads, in its record order."""
    if 2 in reads or not reads: raise ValueError('mesh: the kernel reads clip z or nothing')
    pulled, info = dmsh_pull.variant(vs_src)   # keeps main0 / main0_z only
    if pulled is None or not isinstance(info, dict): raise ValueError(f'mesh: no pulling entry: {info}')
    words = dmsh_geometry.output_words(pulled)
    pulled = pulled.replace(' [[point_size]];', ';')   # a triangle mesh has no point size
    # The vertex entries as functions; their resource parameters become the mesh entry's
    entries, res = {}, None
    for name in [info['entry']] + ([info['entry_z']] if info.get('entry_z') else []):
        m = _entry(pulled, 'vertex', name)
        args, fparams, rparams = [], [], []
        for p in _params(m[2]):
            a = re.fullmatch(r'(.+?) (\w+) \[\[(\w+)(?:\((\d+)\))?\]\]', p)
            if not a: raise ValueError('mesh: vertex parameter ' + p)
            fparams.append(f'{a[1]} {a[2]}')
            if a[3] in _BUILTIN_V: args.append(_BUILTIN_V[a[3]])
            elif a[3] == 'buffer' and int(a[4]) not in (DRAW_SLOT, GS_SLOT): args.append(a[2]); rparams.append(p)
            else: raise ValueError('mesh: vertex parameter ' + p)
        if res is not None and res != rparams: raise ValueError('mesh: entries differ in resources')
        res = rparams
        pulled = pulled[:m.start()] + f'\nstatic inline main0_out dm_vs_{name}(' + ', '.join(fparams) + ')\n{\n' + pulled[m.end():]
        entries[name] = args
    # The kernel as a function: capture reads from threadgroup memory, the topology map as the identity
    # (triangle t's vertices are 3t..3t+2), the mask store into dm_mask, index stores dropped
    k = _entry(gs_src, 'kernel', 'main0')
    gargs = []
    for p in _params(k[2]):
        a = re.fullmatch(r'(.+?) (\w+) \[\[(\w+)(?:\((\d+)\))?\]\]', p)
        if a and a[3] in _BUILTIN_G: gargs.append(_BUILTIN_G[a[3]])
        elif a and a[0] == 'constant spvDescriptorSetBuffer0& spvDescriptorSet0 [[buffer(0)]]': gargs.append('dm_gset')
        else: raise ValueError('mesh: kernel parameter ' + p)
    fparams = [re.sub(r' \[\[.*\]\]$', '', p) for p in _params(k[2])]
    body = gs_src[k.end():]
    if re.search(r'\bthreadgroup\b|\bthreadgroup_barrier\b', body): raise ValueError('mesh: kernel uses threadgroup memory')
    # GeometryToCompute uses GlobalId.x both for the guest PrimitiveId and for
    # addressing its capture. Keep that ID draw-wide; only the capture address
    # is relative to this mesh group's records. Replacing GlobalId with the
    # local lane silently repeats guest IDs every PRIMS primitives.
    body = re.sub(r'\(\*spvDescriptorSet0\.vertex_output\)\._m0\[(\w+)\]',
                  r'dm_rec[uint(\1) - dm_capture_base]', body)
    prefix = 'spvDescriptorSet0.trb_data['
    while prefix in body:
        s = body.index(prefix); at = e = s + len(prefix); depth = 1
        while depth:
            depth += (body[e] == '[') - (body[e] == ']'); e += 1
        body = body[:s] + 'uint(' + body[at:e - 1] + ')' + body[e:]
    body, n = re.subn(r'\(\*spvDescriptorSet0\.geometry_vb_output\)\._m0\[\w+\] = (\w+);', r'dm_mask = as_type<uint>(\1);', body)
    if not n: raise ValueError('mesh: no mask store')
    body = re.sub(r'\n\s*\(\*spvDescriptorSet0\.geometry_ib_output\)\._m0\[\w+\] = [^;]+;', '', body)
    if re.search(r'vertex_output|geometry_vb_output|geometry_ib_output|trb_data\[', body): raise ValueError('mesh: kernel output not understood')
    gfn = gs_src[:k.start()] + '\nstatic inline void dm_gs(' + ', '.join(fparams + ['threadgroup float* dm_rec', 'uint dm_capture_base', 'thread uint& dm_mask']) + ')\n{\n' + body
    # The words the kernel reads, from the vertex's outputs (missing ones as the capture leaves them: 0, w and point size 1)
    def word(w):
        if w in words: return 'dm_o.' + words[w][4:]
        return '0x3f800000u' if w in (3, 4) or (w >= 19 and w % 4 == 3) else '0u'
    n = len(reads)
    stores = ' '.join(f'dm_rec[dm_t * {n}u + {i}u] = as_type<float>({word(w)});' for i, w in enumerate(reads))
    out = ['#include <metal_stdlib>', '#include <simd/simd.h>', '#include <metal_atomic>', 'using namespace metal;',
           'namespace dm_v {', _strip_head(pulled), '}', 'namespace dm_g {', _strip_head(gfn), '}', 'using namespace dm_v;',
           'struct dm_mesh_draw { device const uchar* ib; uint itype; uint first; uint count; int base_vertex; uint base_instance; };',
           'struct dm_mesh_prim { bool culled [[primitive_culled]]; };']
    if res is None: raise ValueError('mesh: no vertex entries')
    for name, args in entries.items():
        out.append(f'''[[mesh]] void dm_mesh{name[5:]}(metal::mesh<main0_out, dm_mesh_prim, {3 * PRIMS}, {PRIMS}, metal::topology::triangle> dm_m, {', '.join(res)},
    constant dm_mesh_draw& dm_d [[buffer({DRAW_SLOT})]], constant dm_g::spvDescriptorSetBuffer0& dm_gset [[buffer({GS_SLOT})]],
    uint dm_t [[thread_index_in_threadgroup]], uint dm_tg [[threadgroup_position_in_grid]], uint dm_sg [[thread_index_in_simdgroup]])
{{
    threadgroup float dm_rec[{3 * PRIMS * n}];
    uint dm_p0 = dm_tg * {PRIMS}u, dm_np = min({PRIMS}u, dm_d.count / 3u - dm_p0);
    if (dm_t < dm_np * 3u) {{
        uint dm_i = dm_d.first + dm_p0 * 3u + dm_t;
        uint dm_ix = dm_d.itype == 0u ? dm_i : dm_d.itype == 1u ? uint(dm_d.ib[dm_i]) : dm_d.itype == 2u ? uint(((device const ushort*)dm_d.ib)[dm_i]) : ((device const uint*)dm_d.ib)[dm_i];
        uint dm_vid = dm_ix + uint(dm_d.base_vertex), dm_bv = uint(dm_d.base_vertex), dm_bi = dm_d.base_instance, dm_iid = dm_bi;
        main0_out dm_o = dm_vs_{name}({', '.join(args)});
        dm_m.set_vertex(dm_t, dm_o);
        {stores}
    }}
    if (dm_t == 0u) dm_m.set_primitive_count(dm_np);
    threadgroup_barrier(mem_flags::mem_threadgroup);
    if (dm_t < dm_np) {{
        uint dm_mask = 0u;
        dm_g::dm_gs({', '.join(gargs)}, dm_rec, dm_p0 * {3 * n}u, dm_mask);
        dm_m.set_index(dm_t * 3u, dm_t * 3u); dm_m.set_index(dm_t * 3u + 1u, dm_t * 3u + 1u); dm_m.set_index(dm_t * 3u + 2u, dm_t * 3u + 2u);
        dm_m.set_primitive(dm_t, dm_mesh_prim{{(dm_mask & 1u) == 0u}});
    }}
}}''')
    return '\n'.join(out) + '\n'


# ---------- NVN2 task and mesh programs (Switch 2) ----------
# The translator runs an SM86 task or mesh program as a kernel whose ISBE regions are threadgroup arrays (TranslationFlags.Sm86Task / Sm86Mesh,
# Sm86Emit.Isbe.cs): isbe_in (input: the workgroup index at word 0, a mesh program's task payload after it), isbe_out (output attributes) and isbe_map
# (a mesh program's primitive count and 8-bit vertex indices, one word per byte address). taskmesh() turns that kernel into a Metal object or mesh function:
# a prologue fills isbe_in, the kernel body runs as a function, and an epilogue hands isbe_out / isbe_map to Metal. The layout follows Mesa NVK's ISBE notes
# (docs/drivers/nvk/isbe_layout.rst, mesh_shading_notes.rst, MIT) and Mario Kart World's programs (docs/reference/shaders-sm86.md, Task and mesh programs).
SPH_OMAP_BIT0 = 0x190   # bit of output attribute slot 0 (byte address 0) in a program header: slot s is attribute address 4 s (position x at 0x70 is slot 28)
SPH_OMAP_END = 0x50 * 8
LINE = 128              # an ISBE memory line: 32 values of one attribute word
TOPOLOGY = {3: ('triangle', 3)}   # control +0x5f (mesh): the output primitive (MKW: 3, triangles; other values not seen)

def _omap(sph):
    """The output attribute slots a program header marks, in ascending order (the ISBE attribute order)"""
    return [s for s in range(SPH_OMAP_END - SPH_OMAP_BIT0) if sph[(SPH_OMAP_BIT0 + s) // 8] >> ((SPH_OMAP_BIT0 + s) % 8) & 1]


def layout(ctl, code):
    """A task or mesh program's facts from its NVN2 control section and whole code area (with the second header a mesh program with per-primitive outputs
    carries at control +0x64), or None for another stage. Control: +0x56 stage (6 task, 7 mesh), +0x58 u16 threads, +0x5a u16 the task payload bytes (the
    payload's shared lines; a task program writes up to it, a mesh program under a task reads it), +0x5f the output topology, +0x60 / +0x62 u16 the maximum
    vertices / primitives, +0x64 u32 the second header's offset in the code area (0: none). A mesh program's per-vertex outputs are the first header's
    output slots, its per-primitive ones the second header's. +0x5e is 1 for a mesh program that runs under a task program (MKW: exactly the ones that read their
    workgroup index without SKEW and read a payload), 0 for one drawn alone."""
    import struct
    stage = {6: 'task', 7: 'mesh'}.get(ctl[0x56])
    if not stage: return None
    threads, payload = struct.unpack_from('<2H', ctl, 0x58)
    if not 1 <= threads <= 32: raise ValueError(f'{stage}: {threads} threads (the hardware runs at most 32)')
    if stage == 'task':
        return {'stage': stage, 'local': [threads, 1, 1], 'payload': max(16, payload), 'isbe_out': max(16, payload), 'isbe_map': 0, 'isbe_in': 4}
    mv, mp = struct.unpack_from('<2H', ctl, 0x60)
    if ctl[0x5f] not in TOPOLOGY: raise ValueError(f'mesh: output topology {ctl[0x5f]} not known')
    sph2 = struct.unpack_from('<I', ctl, 0x64)[0]
    if sph2 and sph2 + 0x50 > len(code): raise ValueError('mesh: the second program header lies past the code area')
    vw, pw = _omap(code[:0x50]), _omap(code[sph2:sph2 + 0x50]) if sph2 else []
    if not 1 <= mv <= 256 or not 1 <= mp <= 512: raise ValueError(f'mesh: {mv} vertices / {mp} primitives (Metal: at most 256 / 512)')
    vbytes = (mv + 31) // 32 * len(vw) * LINE
    return {'stage': stage, 'local': [threads, 1, 1], 'payload': max(16, payload), 'task': bool(ctl[0x5e]), 'topology': ctl[0x5f], 'max_vertices': mv,
            'max_primitives': mp, 'vertex_slots': vw, 'primitive_slots': pw, 'primitive_base': vbytes,
            'isbe_out': vbytes + (mp + 31) // 32 * len(pw) * LINE, 'isbe_map': 4 + TOPOLOGY[ctl[0x5f]][1] * mp, 'isbe_in': max(16, payload)}


TM_DRAW_SLOT = 12   # the draw's {first} block, beside the argument buffer (buffer 0) in the object and mesh stages (nvn2_mesh.inc NVN2_MESH_DRAW_SLOT)
_TM_BUILTINS = {'thread_index_in_threadgroup': 'uint', 'threadgroup_position_in_grid': 'uint3'}
# Output slots Metal has a member for: position x..w (0x70..0x7c), point size (0x6c), layer (0x64) and viewport (0x68) (per primitive: the last two), generic
# vector i at 0x80 + 16 i (user(locn i), a float4 as the fragment stage reads it)
_POS, _PSIZE, _LAYER, _VIEWPORT, _GENERIC0 = 28, 27, 25, 26, 32


def _members(slots, per_primitive):
    """[(declaration, [(component expression slot or None)...])] for the output struct, from the attribute word slots in ISBE order"""
    have = {s: i for i, s in enumerate(slots)}
    out, done = [], set()
    for s in slots:
        if s in done: continue
        if _POS <= s < _POS + 4 and not per_primitive:
            comps = [have.get(_POS + c) for c in range(4)]; done.update(range(_POS, _POS + 4))
            out.append(('float4 gl_Position [[position]]', 'gl_Position', comps, 'float4'))
        elif s >= _GENERIC0 and s < _GENERIC0 + 4 * 32:
            g = (s - _GENERIC0) // 4; base = _GENERIC0 + 4 * g
            comps = [have.get(base + c) for c in range(4)]; done.update(range(base, base + 4))
            out.append((f'float4 m{g} [[user(locn{g})]]', f'm{g}', comps, 'float4'))
        elif s == _PSIZE and not per_primitive:
            done.add(s); out.append(('float gl_PointSize [[point_size]]', 'gl_PointSize', [have[s]], 'float'))
        elif s in (_LAYER, _VIEWPORT) and per_primitive:
            done.add(s); name = 'gl_Layer' if s == _LAYER else 'gl_ViewportIndex'
            out.append((f'uint {name} [[{"render_target_array_index" if s == _LAYER else "viewport_array_index"}]]', name, [have[s]], 'uint'))
        else: raise ValueError(f'mesh: {"per-primitive" if per_primitive else "per-vertex"} output attribute 0x{4 * s:x} has no Metal counterpart here')
    return out


def _stores(var, members, word):
    """Statements filling struct var from the ISBE words; word(i) is the expression of attribute word i (an unwritten component reads 0, w 1)"""
    st = []
    for _, name, comps, ty in members:
        vals = [f'as_type<float>({word(c)})' if c is not None else ('1.0' if k == 3 else '0.0') for k, c in enumerate(comps)]
        if ty == 'uint': st.append(f'{var}.{name} = {word(comps[0])};')
        elif ty == 'float': st.append(f'{var}.{name} = {vals[0]};')
        else: st.append(f'{var}.{name} = float4({", ".join(vals)});')
    return st


def taskmesh(src, lay):
    """The translator's kernel for a task or mesh program (src: its MSL from spirv-cross) as a Metal object or mesh function main0, per layout() (lay).
    The kernel body becomes dm_body, called by all threads between a prologue and an epilogue with the ISBE arrays:
      task: isbe_in[0] = the workgroup index (first + the threadgroup's position); after the body the threads copy isbe_out (the payload, with the mesh
            workgroup count at word 1) into the object payload, and thread 0 launches that many mesh threadgroups.
      mesh: isbe_in = the workgroup index (under a task: the position among the task's mesh threadgroups; else first + the position), then the task payload;
            after the body the vertices (each attribute word i of vertex v at (v / 32) * vertex words * 32 + i * 32 + v % 32), the primitive count (isbe_map[0])
            and the indices (isbe_map[4..]) and per-primitive words go to Metal. Two entries, as a vertex shader's (dmsh_build.py vs_depth): main0 with the GL
            convention's z' = z / 2 + w / 2, main0_z with z as written.
    The draw's first workgroup comes in a constant block at buffer TM_DRAW_SLOT."""
    m = re.search(r'\nkernel void main0\(([^\n]*)\)\n\{\n', src)
    if not m: raise ValueError(f'{lay["stage"]}: no kernel main0 in the translation')
    head, body = src[:m.start()], src[m.end():]
    arrays = {}
    for d in re.finditer(r'^    threadgroup spvUnsafeArray<uint, (\d+)> (isbe_\w+);\n', body, re.M): arrays[d.group(2)] = int(d.group(1))
    body = re.sub(r'^    threadgroup spvUnsafeArray<uint, \d+> isbe_\w+;\n', '', body, flags=re.M)
    if re.search(r'^\s*threadgroup ', body, re.M): raise ValueError(f'{lay["stage"]}: the kernel has threadgroup memory of its own')
    want = {'isbe_out': lay['isbe_out'] // 4, 'isbe_in': lay['isbe_in'] // 4, **({'isbe_map': lay['isbe_map']} if lay['stage'] == 'mesh' else {})}
    for n, size in arrays.items():
        if want.get(n) != size: raise ValueError(f'{lay["stage"]}: {n} of {size} words, the layout says {want.get(n)}')
    params, call, attrs = [], [], {}
    for p in _params(m.group(1)):
        a = re.fullmatch(r'(.+?) (\w+) \[\[(\w+)(?:\((\d+)\))?\]\]', p)
        if not a: raise ValueError(f'{lay["stage"]}: kernel parameter {p}')
        params.append(p); call.append(a.group(2)); attrs[a.group(3)] = a.group(2)
        if a.group(3) == 'buffer' and int(a.group(4)) == TM_DRAW_SLOT: raise ValueError(f'{lay["stage"]}: the kernel uses buffer {TM_DRAW_SLOT}')
    fparams = [re.sub(r' \[\[.*\]\]$', '', p) for p in params] + [f'threadgroup spvUnsafeArray<uint, {want[n]}>& {n}' for n in sorted(want)]
    fn = head + '\nstatic inline void dm_body(' + ', '.join(fparams) + ')\n{\n' + body
    # Entry parameters: the kernel's, plus the thread index and threadgroup position when it has none
    extra = []
    for attr, ty in _TM_BUILTINS.items():
        if attr not in attrs: attrs[attr] = 'dm_' + attr; extra.append(f'{ty} dm_{attr} [[{attr}]]')
    t, tg, threads = attrs['thread_index_in_threadgroup'], attrs['threadgroup_position_in_grid'], lay['local'][0]
    tgx = tg + '.x' if _TM_BUILTINS['threadgroup_position_in_grid'] == 'uint3' else tg
    decl = ''.join(f'    threadgroup spvUnsafeArray<uint, {want[n]}> {n};\n' for n in sorted(want))
    args = ', '.join(call + sorted(want))
    pl_words = lay['payload'] // 4
    out = [fn, 'struct dm_tm_draw { uint first, pad0, pad1, pad2; };', f'struct dm_payload {{ uint w[{pl_words}]; }};']
    eparams = ', '.join(params + extra + [f'constant dm_tm_draw& dm_draw [[buffer({TM_DRAW_SLOT})]]'])
    if lay['stage'] == 'task':
        out.append(f'''[[object]] void main0({eparams}, object_data dm_payload& dm_pl [[payload]], mesh_grid_properties dm_mgp)
{{
{decl}    if ({t} == 0u) {{ isbe_in[0] = {tgx} + dm_draw.first; isbe_out[1] = 0u; }}
    threadgroup_barrier(mem_flags::mem_threadgroup);
    dm_body({args});
    threadgroup_barrier(mem_flags::mem_threadgroup);
    for (uint dm_i = {t}; dm_i < {pl_words}u; dm_i += {threads}u) dm_pl.w[dm_i] = isbe_out[dm_i];
    if ({t} == 0u) dm_mgp.set_threadgroups_per_grid(uint3(isbe_out[1], 1u, 1u));
}}''')
        return '\n'.join(out) + '\n'
    mv, mp, nv, npw, nind = lay['max_vertices'], lay['max_primitives'], len(lay['vertex_slots']), len(lay['primitive_slots']), TOPOLOGY[lay['topology']][1]
    vm, pm = _members(lay['vertex_slots'], False), _members(lay['primitive_slots'], True)
    if not any(n == 'gl_Position' for _, n, _, _ in vm): raise ValueError('mesh: no position output')
    out.append('struct dm_vout {\n' + ''.join(f'    {d};\n' for d, _, _, _ in vm) + '};')
    if pm: out.append('struct dm_pout {\n' + ''.join(f'    {d};\n' for d, _, _, _ in pm) + '};')
    out.append(f'using dm_mesh_t = metal::mesh<dm_vout, {"dm_pout" if pm else "void"}, {mv}, {mp}, metal::topology::{TOPOLOGY[lay["topology"]][0]}>;')
    pb = lay['primitive_base'] // 4
    vstores = _stores('dm_o', vm, lambda i: f'isbe_out[dm_b + {32 * i}u]')
    pstores = _stores('dm_q', pm, lambda i: f'isbe_out[dm_c + {32 * i}u]')
    payload_param = ', const object_data dm_payload& dm_pl [[payload]]' if lay['task'] else ''
    fill = (f'for (uint dm_i = {t}; dm_i < {want["isbe_in"]}u; dm_i += {threads}u) isbe_in[dm_i] = dm_i == 0u ? {tgx} : dm_pl.w[min(dm_i, {pl_words - 1}u)];'
            if lay['task'] else f'if ({t} == 0u) isbe_in[0] = {tgx} + dm_draw.first;')
    vsrc = '\n        '.join(vstores)
    for entry, gl in (('main0', True), ('main0_z', False)):
        zfix = '\n        dm_o.gl_Position.z = fma(dm_o.gl_Position.z, 0.5, dm_o.gl_Position.w * 0.5);' if gl else ''
        idx = ' '.join(f'dm_m.set_index(dm_p * {nind}u + {k}u, uchar(isbe_map[4u + dm_p * {nind}u + {k}u]));' for k in range(nind))
        prim = (f'\n        dm_pout dm_q; uint dm_c = {pb}u + (dm_p >> 5) * {32 * npw}u + (dm_p & 31u);\n        ' + '\n        '.join(pstores) +
                '\n        dm_m.set_primitive(dm_p, dm_q);') if pm else ''
        out.append(f'''[[mesh]] void {entry}(dm_mesh_t dm_m, {eparams}{payload_param})
{{
{decl}    {fill}
    if ({t} == 0u) isbe_map[0] = 0u;
    threadgroup_barrier(mem_flags::mem_threadgroup);
    dm_body({args});
    threadgroup_barrier(mem_flags::mem_threadgroup);
    uint dm_np = min(isbe_map[0], {mp}u);
    if ({t} == 0u) dm_m.set_primitive_count(dm_np);
    for (uint dm_v = {t}; dm_v < {mv}u; dm_v += {threads}u)
    {{
        dm_vout dm_o; uint dm_b = (dm_v >> 5) * {32 * nv}u + (dm_v & 31u);
        {vsrc}{zfix}
        dm_m.set_vertex(dm_v, dm_o);
    }}
    for (uint dm_p = {t}; dm_p < dm_np; dm_p += {threads}u)
    {{
        {idx}{prim}
    }}
}}''')
    return '\n'.join(out) + '\n'


if __name__ == '__main__':
    import json, sys
    if sys.argv[1] == 'layout':   # layout <meta.json with control_hex> <code area>: the layout as JSON (the SM86 harness, dm/gfx/tools/sm86)
        print(json.dumps(layout(bytes.fromhex(json.load(open(sys.argv[2]))['control_hex']), open(sys.argv[3], 'rb').read())))
    elif sys.argv[1] == 'taskmesh':   # taskmesh <kernel.metal> <layout.json> <out.metal>
        src = open(sys.argv[2]).read(); lay = json.load(open(sys.argv[3]))
        open(sys.argv[4], 'w').write(taskmesh(src, lay))
    else:
        vs, gs, out = sys.argv[1:4]
        reads = json.load(open(gs[:-len('.metal')] + '.json'))['geometry']['capture_reads']
        open(out, 'w').write(compose(open(vs).read(), open(gs).read(), reads))
