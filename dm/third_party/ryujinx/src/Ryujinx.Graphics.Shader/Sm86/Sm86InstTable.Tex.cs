namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86InstTable
    {
        /// <summary>
        /// Texture and surface operations (wave 2 "memory + texture + surface" family). Encodings: Mesa NAK sm70_encode.rs (OpTex and the texture
        /// helpers), source packing nak_nir_lower_tex.c.
        /// </summary>
        private static void AddTex()
        {
            Add("TEX", 0xb60, Sm86Emit.Tex, Sm86InstProps.Rd);
        }
    }
}
