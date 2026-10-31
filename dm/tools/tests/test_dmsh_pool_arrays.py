"""dmsh_build.py pool_arrays and reflect on synthetic MSL: Ryujinx's arrays for a handle traced to no slot, sized to the whole texture / sampler ID ranges (Program.cs), become pointers to
the pools the replay keeps (nvn_mtl.m pool_gpu), declared with one element and read through a pointer to it with the run-time bound; the argument IDs are renumbered as the host lays the buffer out; 32-entry slot arrays stay; members with
attributes after their id (raster_order_group) are read; a member or a use the rewrite cannot read stops the shader instead of leaving the host's argument buffer longer than the reflection.
An NVN2 compute program's handles and storage buffer addresses resolve to bind slots through its own packed c0 (nvn2_shader.compute_c0), which the reflection carries for the host."""
import ast
from pathlib import Path
import re
import sys
import types
import unittest

TOOLS = Path(__file__).resolve().parents[2] / 'gfx/tools'
sys.path.insert(0, str(TOOLS))
import dmsh_abi as abi   # noqa: E402

FUNCS = {'decode_tex', 'const_sampler', 'decode_ubo_tex', 'c2_array', 'pool_arrays', 'reflect', 'c0p_json', 'c0p_slot', 'decode_tex_c0p', 'shared_spill'}
NAMES = {'POOL_MIN', 'TEX_BASE', 'SSBO', 'C0P_TABLES'}


def load():
    tree = ast.parse((TOOLS / 'dmsh_build.py').read_text())
    nodes: list[ast.stmt] = [n for n in tree.body if (isinstance(n, ast.FunctionDef) and n.name in FUNCS)
             or (isinstance(n, ast.Assign) and any(isinstance(t, (ast.Name, ast.Tuple)) and {getattr(e, 'id', None) for e in (t.elts if isinstance(t, ast.Tuple) else [t])} & NAMES for t in n.targets))]
    ns = {'re': re, 'abi': abi, 'dmsh_geometry': types.SimpleNamespace(HELPERS={})}
    exec(compile(ast.Module(body=nodes, type_ignores=[]), str(TOOLS / 'dmsh_build.py'), 'exec'), ns)
    assert FUNCS <= set(ns), FUNCS - set(ns)
    return ns


NS = load()
MSL = '''struct spvDescriptorSetBuffer0
{
    constant _10* support_buffer [[id(0)]];
    constant _16* fp_c5 [[id(1)]];
    array<texture2d<float>, 1048576> fp_t2d_cb0_40000000 [[id(2)]];
    array<sampler, 4096> fp_s_cb0_40000000 [[id(1048578)]];
    array<texture2d<float>, 32> fp_t2d_cb14_0 [[id(1052674)]];
    texture2d<uint> fp_i_cb2_48_rg32ui [[id(1052706), raster_order_group(0)]];
};

fragment main0_out main0(constant spvDescriptorSetBuffer0& spvDescriptorSet0 [[buffer(0)]])
{
    float4 a = spvDescriptorSet0.fp_t2d_cb0_40000000[int(_9[2u])].sample(spvDescriptorSet0.fp_s_cb0_40000000[int(_7)], uv);
}
'''


class PoolArrays(unittest.TestCase):
    def test_rewrite(self):
        out = NS['pool_arrays'](MSL)
        self.assertIn('struct spvPool_fp_t2d_cb0_40000000\n{\n    uint n;\n    array<texture2d<float>, 1> e;\n};', out)
        self.assertIn('struct spvPool_fp_s_cb0_40000000\n{\n    uint n;\n    array<sampler, 1> e;\n};', out)
        self.assertLess(out.index('struct spvPool_'), out.index('struct spvDescriptorSetBuffer0'))
        ids = re.findall(r'(\w+) \[\[id\((\d+)\)', out)
        self.assertEqual(ids, [('support_buffer', '0'), ('fp_c5', '1'), ('fp_t2d_cb0_40000000', '2'), ('fp_s_cb0_40000000', '3'), ('fp_t2d_cb14_0', '4'), ('fp_i_cb2_48_rg32ui', '36')])
        self.assertIn('constant spvPool_fp_t2d_cb0_40000000* fp_t2d_cb0_40000000 [[id(2)]];', out)
        self.assertIn('fp_i_cb2_48_rg32ui [[id(36), raster_order_group(0)]];', out)
        self.assertIn('(&spvDescriptorSet0.fp_t2d_cb0_40000000->e[0])[min(uint(int(_9[2u])), spvDescriptorSet0.fp_t2d_cb0_40000000->n)]', out)
        self.assertIn('.sample((&spvDescriptorSet0.fp_s_cb0_40000000->e[0])[min(uint(int(_7)), spvDescriptorSet0.fp_s_cb0_40000000->n)], uv)', out)

    def test_slot_arrays_stay(self):
        src = MSL.replace('1048576', '32').replace('4096', '32')
        self.assertEqual(NS['pool_arrays'](src), src)

    def test_uncovered_use(self):
        with self.assertRaises(ValueError): NS['pool_arrays'](MSL.replace('    float4 a', '    f(spvDescriptorSet0.fp_t2d_cb0_40000000);\n    float4 a'))


class Reflect(unittest.TestCase):
    def test_pools_and_attributes(self):
        b = {e['msl_name']: e for e in NS['reflect']('k', 'ps', NS['pool_arrays'](MSL), {'sbufs': []})}
        self.assertEqual((b['fp_t2d_cb0_40000000']['kind'], b['fp_t2d_cb0_40000000']['elem'], b['fp_t2d_cb0_40000000']['arg_id']), (abi.KIND_TEX_POOL, 'texture2d<float>', 2))
        self.assertEqual((b['fp_s_cb0_40000000']['kind'], b['fp_s_cb0_40000000']['arg_id']), (abi.KIND_SAMP_POOL, 3))
        self.assertEqual((b['fp_i_cb2_48_rg32ui']['kind'], b['fp_i_cb2_48_rg32ui']['reg'], b['fp_i_cb2_48_rg32ui']['arg_id']), (abi.KIND_IMG, 0, 36))

    def test_unreadable_member(self):
        with self.assertRaises(ValueError): NS['reflect']('k', 'ps', MSL.replace('raster_order_group(0)', 'raster_order_group'), {'sbufs': []})

    def test_nvn2_compute_c0(self):   # Mario Kart World's 13-uniform layout: uniform 6 read through global memory, storage 1, texture 9 with its sampler, image 1
        pack = (0x218, [('uniform', 0, 0x10, 13), ('storage', 0, 0xe0, 9), ('texture', 0, 0x170, 19), ('image', 0, 0x208, 2)])
        msl = ('struct spvDescriptorSetBuffer0\n{\n    constant _16* cp_c0 [[id(0)]];\n    device _21* cp_s0 [[id(1)]];\n    device _21* cp_s1 [[id(2)]];\n'
               '    texture2d<float> cp_t_cb0_6E [[id(3)]];\n    sampler cp_t_cb0_6ESmplr [[id(4)]];\n    texture2d<float, access::write> cp_i_cb0_84_r32f [[id(5)]];\n};\n')
        res = {'sbufs': [{'binding': 0, 'cb_slot': 0, 'cb_offset': 0x70 // 4, 'flags': 0}, {'binding': 1, 'cb_slot': 0, 'cb_offset': 0xf0 // 4, 'flags': 1}]}
        b = {e['msl_name']: (e['kind'], e.get('reg')) for e in NS['reflect']('k', 'cs', msl, res, pack)}
        self.assertEqual(b, {'cp_c0': (abi.KIND_DRVCB, None), 'cp_s0': (abi.KIND_UBO_PTR, 6), 'cp_s1': (abi.KIND_SBUF, 1), 'cp_t_cb0_6E': (abi.KIND_TEX, 9),
                             'cp_t_cb0_6ESmplr': (abi.KIND_SAMP_TEX, 9), 'cp_i_cb0_84_r32f': (abi.KIND_IMG, 1)})
        self.assertEqual(NS['c0p_json'](pack), {'size': 0x218, 'rows': [[abi.KIND_CBUF, 0, 0x10, 13], [abi.KIND_SBUF, 0, 0xe0, 9], [abi.KIND_TEX, 0, 0x170, 19], [abi.KIND_IMG, 0, 0x208, 2]]})
        self.assertIsNone(NS['decode_tex_c0p']('cp_t_cb0_6F', pack)[0], 'a word inside an entry is no slot')


class SharedSpill(unittest.TestCase):   # threadgroup memory past Metal's limit (dmsh_abi.h DMSH_SHARED_MAX): Mario Kart World's 48 KB compute programs
    K = ('kernel void main0(constant spvDescriptorSetBuffer0& spvDescriptorSet0 [[buffer(0)]], uint3 gl_LocalInvocationID [[thread_position_in_threadgroup]])\n{\n'
         '    threadgroup spvUnsafeArray<uint, 12288> _39;\n    threadgroup spvUnsafeArray<float, 64> _40;\n    _39[gl_LocalInvocationID.x] = 1u;\n'
         '    threadgroup_barrier(mem_flags::mem_threadgroup);\n}\n')

    def test_moves_the_largest_array(self):
        out, n = NS['shared_spill'](self.K)
        self.assertEqual(n, 12288 * 4)
        self.assertIn('device spvUnsafeArray<uint, 12288>& _39 = *(device spvUnsafeArray<uint, 12288>*)(dm_spill + dm_tg * 49152u + 0u);', out)
        self.assertIn('threadgroup spvUnsafeArray<float, 64> _40;', out)
        self.assertIn(f'device uchar* dm_spill [[buffer({abi.SPILL_BUFFER})]], uint3 dm_tgs [[threadgroups_per_grid]], uint3 dm_tgp [[threadgroup_position_in_grid]])', out)
        self.assertIn('uint dm_tg = (dm_tgp.z * dm_tgs.y + dm_tgp.y) * dm_tgs.x + dm_tgp.x;', out)
        self.assertIn('threadgroup_barrier(mem_flags::mem_threadgroup | mem_flags::mem_device);', out)

    def test_fits(self):
        small = self.K.replace('12288', '4096')
        self.assertEqual(NS['shared_spill'](small), (small, 0))

    def test_atomic_stops(self):
        with self.assertRaises(ValueError): NS['shared_spill'](self.K.replace('    _39[gl', '    atomic_fetch_add_explicit((threadgroup atomic_uint*)&_39[0], 1u, memory_order_relaxed);\n    _39[gl'))


if __name__ == '__main__':
    unittest.main()
