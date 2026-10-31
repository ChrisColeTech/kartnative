using Ryujinx.Graphics.Shader.Translation;

namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86InstTable
    {
        /// <summary>
        /// Texture and surface operations (wave 2 "memory + texture + surface" family). Encodings: Mesa NAK sm70_encode.rs (OpTex, OpTld, OpTld4,
        /// OpTmml, OpTxd, OpTxq, OpSuLd, OpSuSt, OpSuAtom and the texture helpers), source packing nak_nir_lower_tex.c. Only the forms with the handle
        /// in a constant buffer are registered: the bindless forms (handle in a register: 0x361, 0x367, 0x364, 0x36a, 0x36d, 0x370, 0x998, 0x99a,
        /// 0x99c, 0x99e, 0x394, 0x396, 0x3a0) and the binary surface forms (SULD.B / SUST.B) do not occur in MKW.
        /// </summary>
        private static void AddTex()
        {
            const Sm86InstProps Rd = Sm86InstProps.Rd;

            Add("TEX", 0xb60, Sm86Emit.Tex, Rd);
            Add("TLD", 0xb66, Sm86Emit.Tld, Rd);
            Add("TLD4", 0xb63, Sm86Emit.Tld4, Rd);
            Add("TMML", 0xb69, Sm86Emit.Tmml, Rd);
            Add("TXD", 0xb6c, Sm86Emit.Txd, Rd);
            Add("TXQ", 0xb6f, Sm86Emit.Txq, Rd);

            Add("SULD", 0xb97, Sm86Emit.Suld, Rd);
            Add("SUST", 0xb9b, Sm86Emit.Sust, features: FeatureFlags.Store);
            Add("SUATOM", 0x593, Sm86Emit.Suatom, Rd, FeatureFlags.Store);
            Add("SURED", 0x59f, Sm86Emit.Sured, features: FeatureFlags.Store);
        }
    }
}
