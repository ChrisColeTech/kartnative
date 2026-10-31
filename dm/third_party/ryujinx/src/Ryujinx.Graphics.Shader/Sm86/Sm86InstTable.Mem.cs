namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86InstTable
    {
        /// <summary>
        /// Constant, attribute, special-register, local / shared / global memory and atomic operations (wave 2 "memory + texture + surface" family).
        /// Encodings: Mesa NAK sm70_encode.rs (OpLdc, OpALd, OpASt, OpIpa, OpS2R).
        /// </summary>
        private static void AddMem()
        {
            const Sm86InstProps Rd = Sm86InstProps.Rd;
            const Sm86InstProps U = Sm86InstProps.Uniform;

            Add("S2R", 0x919, Sm86Emit.S2r, Rd);
            Add("S2UR", 0x9c3, Sm86Emit.S2r, U);
            Add("LDC", 0xb82, Sm86Emit.Ldc, Rd);
            Add("ULDC", 0xab9, Sm86Emit.Uldc, U);

            Add("ALD", 0x321, Sm86Emit.Ald, Rd | Sm86InstProps.AttrLoad);
            Add("AST", 0x322, Sm86Emit.Ast, Sm86InstProps.AttrStore);
            Add("IPA", 0x326, Sm86Emit.Ipa, Rd | Sm86InstProps.Ipa);
        }
    }
}
