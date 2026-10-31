namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86InstTable
    {
        /// <summary>
        /// Control flow and convergence (wave 2 "flow + warp + uniform" family). Encodings: Mesa NAK sm70_encode.rs (OpBra, OpExit, OpBSSy, OpBSync,
        /// OpBreak, OpWarpSync, OpKill, OpNop); CALL / RET / BRX from the Switch 2 reports (docs/reference/shaders-sm86.md).
        /// </summary>
        private static void AddFlow()
        {
            Add("BRA", 0x947, Sm86Emit.Bra, Sm86InstProps.Branch | Sm86InstProps.NoPred);
            Add("BRX", 0x949, Sm86Emit.Brx, Sm86InstProps.Brx);
            Add("CALL", 0x944, Sm86Emit.Call, Sm86InstProps.Call | Sm86InstProps.NoPred);
            Add("RET", 0x950, Sm86Emit.Ret, Sm86InstProps.Ret);
            Add("EXIT", 0x94d, Sm86Emit.Exit, Sm86InstProps.Exit);
            // KILL ends the invocation like EXIT (the IR's discard terminates its block), so it ends a decoded block too.
            Add("KILL", 0x95b, Sm86Emit.Kill, Sm86InstProps.Exit);

            // Convergence barriers and warp synchronisation: a translation runs each invocation on its own, so they do nothing.
            Add("BSSY", 0x945, Sm86Emit.Nop);
            Add("BSYNC", 0x941, Sm86Emit.Nop);
            Add("BREAK", 0x942, Sm86Emit.Nop);
            Add("WARPSYNC", 0x148, Sm86Forms.BSlot, Sm86Emit.Nop);
            Add("NOP", 0x918, Sm86Emit.Nop);
            Add("DEPBAR", 0x91a, Sm86Emit.Nop);
        }
    }
}
