namespace Ryujinx.Graphics.Shader.Decoders
{
    enum RegisterType
    {
        Flag,
        Gpr,
        Predicate,
        // nxnative: SM75+ uniform datapath (the SM86 frontend, Sm86/): UR0-UR62 (UR63 is URZ) and UP0-UP6 (UP7 is UPT).
        UniformGpr,
        UniformPredicate,
    }
}
