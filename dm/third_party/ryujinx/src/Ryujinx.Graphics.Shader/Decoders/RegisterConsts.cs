namespace Ryujinx.Graphics.Shader.Decoders
{
    static class RegisterConsts
    {
        public const int GprsCount = 255;
        public const int PredsCount = 7;
        public const int FlagsCount = 4;

        // nxnative: uniform registers of the SM86 frontend. Their SSA keys follow the flags, so Maxwell programs keep their keys.
        public const int UniformGprsCount = 63;
        public const int UniformPredsCount = 7;

        public const int TotalCount = GprsCount + PredsCount + FlagsCount + UniformGprsCount + UniformPredsCount;

        public const int RegisterZeroIndex = GprsCount;
        public const int PredicateTrueIndex = PredsCount;
        public const int UniformRegisterZeroIndex = UniformGprsCount;
        public const int UniformPredicateTrueIndex = UniformPredsCount;
    }
}
