using System;

namespace Ryujinx.Graphics.Shader.Translation
{
    [Flags]
    public enum TranslationFlags
    {
        None = 0,

        VertexA = 1 << 0,
        Compute = 1 << 1,
        DebugMode = 1 << 2,
        // Offline Metal: image atomics run natively on the texture (MSL 3.1), guarded for IGN bounds.
        MetalImageAtomics = 1 << 3,
        // nxnative: the code is SM70+ (Switch 2 NVN2, SM86): 16-byte instructions, read by the SM86 frontend (Sm86/). Graphics code starts with the
        // 128-byte NVN2 program header (SPH version 4), compute code without one.
        Sm86 = 1 << 4,
        // nxnative: with Sm86, an NVN2 task (Sm86Task) or mesh (Sm86Mesh) program: graphics code (the 128-byte header, then the instructions), translated
        // as a compute stage of the compute local size whose ISBE regions are shared memories (IGpuAccessor.QuerySm86Isbe*, ResourceManager.SetSm86IsbeMemories);
        // dm/gfx/tools/dmsh_mesh.py makes the Metal object / mesh function of the kernel (docs/reference/shaders-sm86.md, Task and mesh programs).
        Sm86Task = 1 << 5,
        Sm86Mesh = 1 << 6,
    }
}
