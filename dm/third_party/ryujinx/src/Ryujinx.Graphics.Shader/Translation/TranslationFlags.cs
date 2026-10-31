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
    }
}
