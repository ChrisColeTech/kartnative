using Ryujinx.Graphics.Shader.Translation;

namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86Emit
    {
        /// <summary>
        /// R2UR URd, Ra: a GPR into a uniform register. The compiler only moves warp-uniform values this way, so in a translation, where each
        /// invocation runs on its own, the invocation's own value is the uniform value. Bits 81-83 (a predicate output) are PT in the compiler's code.
        /// </summary>
        public static void R2ur(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            context.Copy(UGpr(inst.URd), Gpr(inst.Ra));
        }
    }
}
