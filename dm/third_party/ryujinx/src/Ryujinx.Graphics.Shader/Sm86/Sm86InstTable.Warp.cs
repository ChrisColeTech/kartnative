using Ryujinx.Graphics.Shader.Translation;

namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86InstTable
    {
        /// <summary>
        /// Warp-wide and uniform-datapath helpers (wave 2 "flow + warp + uniform" family): VOTE / VOTEU, SHFL, MATCH, REDUX, R2UR, PLOP3 / UPLOP3,
        /// UP2UR / R2P / P2R. Encodings: Mesa NAK sm70_encode.rs (OpVote, OpShfl, OpMatch, OpRedux, OpR2UR, OpPLop3); P2R 0x003, R2P 0x004 and UP2UR
        /// 0x083 (base opcodes, B-slot forms, byte selector in bits 76-77) from the MKW dump (docs/reference/shaders-sm86.md).
        /// </summary>
        private static void AddWarp()
        {
            const Sm86InstProps Rd = Sm86InstProps.Rd;
            const Sm86InstProps U = Sm86InstProps.Uniform;

            Add("R2UR", 0x3c2, Sm86Emit.R2ur, Sm86InstProps.URd);

            Add("P2R", 0x003, Sm86Forms.BSlot, Sm86Emit.P2r, Rd);
            Add("UP2UR", 0x083, Sm86Forms.R | Sm86Forms.I, Sm86Emit.P2r, Rd | U);
            Add("R2P", 0x004, Sm86Forms.BSlot, Sm86Emit.R2p);
            Add("PLOP3", 0x81c, Sm86Emit.Plop3);
            Add("UPLOP3", 0x89c, Sm86Emit.Plop3, U);

            Add("VOTE", 0x806, Sm86Emit.Vote, Rd);
            Add("VOTEU", 0x886, Sm86Emit.Vote, Rd | U);
            Add("SHFL", 0x389, Sm86Emit.Shfl, Rd, FeatureFlags.Shuffle);
            Add("SHFL", 0x589, Sm86Emit.Shfl, Rd, FeatureFlags.Shuffle);
            Add("SHFL", 0x989, Sm86Emit.Shfl, Rd, FeatureFlags.Shuffle);
            Add("SHFL", 0xf89, Sm86Emit.Shfl, Rd, FeatureFlags.Shuffle);
            Add("MATCH", 0x3a1, Sm86Emit.Match, Rd);
            Add("REDUX", 0x3c4, Sm86Emit.Redux, Sm86InstProps.URd);
        }
    }
}
