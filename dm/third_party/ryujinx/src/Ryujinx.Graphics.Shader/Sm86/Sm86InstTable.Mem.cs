using Ryujinx.Graphics.Shader.Translation;

namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86InstTable
    {
        /// <summary>
        /// Constant, attribute, special-register, local / shared / global memory, atomic and barrier operations (wave 2 "memory + texture + surface"
        /// family). Encodings: Mesa NAK sm70_encode.rs (OpLdc, OpALd, OpASt, OpIpa, OpS2R, OpLd, OpSt, OpAtom, OpMemBar, OpBar, OpCCtl).
        /// </summary>
        private static void AddMem()
        {
            const Sm86InstProps Rd = Sm86InstProps.Rd;
            const Sm86InstProps U = Sm86InstProps.Uniform;

            Add("S2R", 0x919, Sm86Emit.S2r, Rd);
            Add("S2UR", 0x9c3, Sm86Emit.S2r, Rd | U);
            Add("LDC", 0xb82, Sm86Emit.Ldc, Rd);
            Add("ULDC", 0xab9, Sm86Emit.Uldc, Rd | U);
            Add("ULDC", 0xabb, Sm86Emit.Uldc, Rd | U);

            Add("ALD", 0x321, Sm86Emit.Ald, Rd | Sm86InstProps.AttrLoad);
            Add("AST", 0x322, Sm86Emit.Ast, Sm86InstProps.AttrStore);
            Add("IPA", 0x326, Sm86Emit.Ipa, Rd | Sm86InstProps.Ipa);
            Add("IPA", 0x526, Sm86Emit.Ipa, Rd | Sm86InstProps.Ipa);
            // Task and mesh programs' ISBE accesses (Sm86Emit.Isbe.cs)
            Add("ISBERD", 0x923, Sm86Emit.Isberd, Rd);
            Add("ISBEWR", 0x927, Sm86Emit.Isbewr);

            Add("LDG", 0x981, Sm86Emit.Ldg, Rd);
            Add("LDG", 0x381, Sm86Emit.Ldg, Rd);
            Add("STG", 0x986, Sm86Emit.Stg, features: FeatureFlags.Store);
            Add("STG", 0x386, Sm86Emit.Stg, features: FeatureFlags.Store);
            Add("LDS", 0x984, Sm86Emit.Lds, Rd, FeatureFlags.SharedMemory);
            Add("STS", 0x388, Sm86Emit.Sts, features: FeatureFlags.SharedMemory);
            Add("STS", 0x988, Sm86Emit.Sts, features: FeatureFlags.SharedMemory);
            Add("LDL", 0x983, Sm86Emit.Ldl, Rd, FeatureFlags.LocalMemory);
            Add("STL", 0x387, Sm86Emit.Stl, features: FeatureFlags.LocalMemory);
            Add("STL", 0x987, Sm86Emit.Stl, features: FeatureFlags.LocalMemory);

            Add("ATOMG", 0x9a8, Sm86Emit.Atomg, Rd, FeatureFlags.Store);
            Add("ATOMG", 0x3a8, Sm86Emit.Atomg, Rd, FeatureFlags.Store);
            Add("RED", 0x98e, Sm86Emit.Red, features: FeatureFlags.Store);
            Add("ATOMS", 0x38c, Sm86Emit.Atoms, Rd, FeatureFlags.SharedMemory);
            Add("ATOMS", 0x98c, Sm86Emit.Atoms, Rd, FeatureFlags.SharedMemory);

            // MEMBAR orders memory; ERRBAR (0x9ab, after MEMBAR in MKW) waits for prior memory errors and CCTL controls the L1 cache: nothing to
            // emit beside the memory barriers Metal provides.
            Add("MEMBAR", 0x992, Sm86Emit.Membar);
            Add("ERRBAR", 0x9ab, Sm86Emit.Nop);
            Add("CCTL", 0x98f, Sm86Emit.Nop);
            Add("BAR", 0xb1d, Sm86Emit.Bar);
        }
    }
}
