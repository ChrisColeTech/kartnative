namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86InstTable
    {
        /// <summary>
        /// Warp-wide and uniform-datapath helpers (wave 2 "flow + warp + uniform" family): VOTE / VOTEU, SHFL, MATCH, REDUX, R2UR, PLOP3 / UPLOP3,
        /// UP2UR / R2P / P2R, BMOV and the barrier registers. Encodings: Mesa NAK sm70_encode.rs (OpVote, OpShfl, OpMatch, OpRedux, OpR2UR, OpPLop3,
        /// OpBMov).
        /// </summary>
        private static void AddWarp()
        {
            Add("R2UR", 0x3c2, Sm86Emit.R2ur, Sm86InstProps.Uniform);
        }
    }
}
