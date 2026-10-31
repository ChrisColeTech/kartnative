using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.StructuredIr;
using Ryujinx.Graphics.Shader.Translation;
using System.Collections.Generic;
using System.Numerics;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Sm86
{
    /// <summary>
    /// Surface (image) operations with the handle in a constant buffer: the forms one below NAK's register-handle opcodes (OpSuLd 0x998 -> 0xb97,
    /// OpSuSt 0x99c -> 0xb9b, OpSuAtom 0x394 -> 0x593, its reduction 0x3a0 -> 0x59f), as the texture opcodes are (OpTex 0x361 -> 0xb60). Fields: handle
    /// word offset in bits 40-53 and its bank in 54-58 (MKW: bank 0 in compute, bank 2 in fragment programs), dimension 61-63 (1D, 1D buffer, 1D array,
    /// 2D, 2D array, 3D), coordinates from Ra on (x [y] [z] [layer]), memory order 77-80 and eviction 84-86 (cache hints; a strong GPU or system
    /// order makes the access coherent). Out-of-bounds accesses are ignored on SM70+ (no clamp field), so the operations carry Maxwell's IGN bounds.
    /// </summary>
    static partial class Sm86Emit
    {
        private static SamplerType SurfaceType(Sm86Inst inst) => inst.Field(61, 3) switch
        {
            0 => SamplerType.Texture1D,
            1 => SamplerType.TextureBuffer,
            2 => SamplerType.Texture1D | SamplerType.Array,
            3 => SamplerType.Texture2D,
            4 => SamplerType.Texture2D | SamplerType.Array,
            5 => SamplerType.Texture3D,
            _ => SamplerType.None,
        };

        /// <summary>The image coordinates (and array index) from Ra on; a 1D image read as 2D gets y = 0.</summary>
        private static List<Operand> SurfaceCoordinates(EmitterContext context, Sm86Inst inst, ref SamplerType type)
        {
            int count = type.Dimensions + (type.HasFlag(SamplerType.Array) ? 1 : 0);
            Operand[] values = ReadData(context, inst.Ra, count);
            List<Operand> sources = [];

            for (int i = 0; i < type.Dimensions; i++)
            {
                sources.Add(values[i]);
            }

            if (Instructions.InstEmit.Sample1DAs2D && (type & SamplerType.Mask) == SamplerType.Texture1D)
            {
                sources.Add(Const(0));
                type = SamplerType.Texture2D | (type & SamplerType.Array);
            }

            if (type.HasFlag(SamplerType.Array))
            {
                sources.Add(values[^1]);
            }

            return sources;
        }

        private static bool CheckSurface(EmitterContext context, Sm86Inst inst, string name, SamplerType type)
        {
            if (type == SamplerType.None)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 {name} with dimension {inst.Field(61, 3)} at 0x{context.CurrOp.Address:X} is not implemented.");
                return false;
            }

            return true;
        }

        private static TextureFlags SurfaceCoherence(Sm86Inst inst) => inst.Field(77, 4) is 0x7 or 0xa ? TextureFlags.Coherent : TextureFlags.None;

        /// <summary>SULD (0xb97, formatted): d = the components of channel mask 72-75 (1, 3 or 15) read with the image's format, from Rd on.</summary>
        public static void Suld(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            SamplerType type = SurfaceType(inst);

            if (!CheckSurface(context, inst, "SULD", type))
            {
                return;
            }

            int handle = TextureHandle(inst);
            int bank = TextureBank(inst);
            int componentMask = inst.Field(72, 4);
            Operand[] sources = SurfaceCoordinates(context, inst, ref type).ToArray();
            TextureFlags flags = SurfaceCoherence(inst);
            TextureFormat format = ShaderProperties.GetTextureFormat(context.TranslatorContext.GpuAccessor, handle, bank);

            Operand[] dests = new Operand[BitOperations.PopCount((uint)componentMask)];

            for (int i = 0; i < dests.Length; i++)
            {
                dests[i] = inst.Rd + i < RegisterConsts.RegisterZeroIndex ? Gpr(inst.Rd + i) : Local();
            }

            SetBindingPair setAndBinding = context.ResourceManager.GetTextureOrImageBinding(Instruction.ImageLoad, type, format, flags, bank, handle);

            context.ImageLoad(type, format, flags, setAndBinding, componentMask, dests, sources);
            WriteFault(context, inst);
        }

        /// <summary>SUST (0xb9b, formatted): writes the components of channel mask 72-75 from Rb on (bits 32-39) with the image's format.</summary>
        public static void Sust(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            SamplerType type = SurfaceType(inst);

            if (!CheckSurface(context, inst, "SUST", type))
            {
                return;
            }

            int handle = TextureHandle(inst);
            int bank = TextureBank(inst);
            List<Operand> sources = SurfaceCoordinates(context, inst, ref type);

            sources.AddRange(ReadData(context, inst.Rb, BitOperations.PopCount((uint)inst.Field(72, 4))));

            TextureFlags flags = SurfaceCoherence(inst);
            TextureFormat format = ShaderProperties.GetTextureFormat(context.TranslatorContext.GpuAccessor, handle, bank);
            SetBindingPair setAndBinding = context.ResourceManager.GetTextureOrImageBinding(Instruction.ImageStore, type, format, flags, bank, handle);

            context.ImageStore(type, format, flags, setAndBinding, sources.ToArray());
        }

        /// <summary>
        /// SUATOM d (0x593) and SURED (0x59f, no destination): image atomic of operation 87-90 (87-89 for SURED; numbering as ATOMG) and type 73-75
        /// (U32, S32) with the data in Rb.
        /// </summary>
        public static void Suatom(EmitterContext context) => EmitSurfaceAtomic(context, reduction: false);

        public static void Sured(EmitterContext context) => EmitSurfaceAtomic(context, reduction: true);

        private static void EmitSurfaceAtomic(EmitterContext context, bool reduction)
        {
            Sm86Inst inst = Inst(context);
            SamplerType type = SurfaceType(inst);
            string name = reduction ? "SURED" : "SUATOM";
            int atomType = inst.Field(73, 3);
            int op = reduction ? inst.Field(87, 3) : inst.Field(87, 4);

            if (!CheckSurface(context, inst, name, type))
            {
                return;
            }

            if (atomType is not ((int)AtomSize.U32 or (int)AtomSize.S32) || op > (int)AtomOp.Exch)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 {name} {(AtomOp)op} of type {atomType} at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            List<Operand> sources = SurfaceCoordinates(context, inst, ref type);

            sources.Add(ReadData(context, inst.Rb, 1)[0]);

            TextureFormat format = atomType == (int)AtomSize.U32 ? TextureFormat.R32Uint : TextureFormat.R32Sint;
            TextureFlags flags = (TextureFlags)(op << 16) | SurfaceCoherence(inst);
            bool discard = reduction || inst.Rd == RegisterConsts.RegisterZeroIndex;

            if (context.TranslatorContext.Options.Flags.HasFlag(TranslationFlags.MetalImageAtomics))
            {
                flags |= TextureFlags.AtomicMetal;
            }

            if (discard)
            {
                flags |= TextureFlags.AtomicDiscardResult;
            }

            SetBindingPair setAndBinding = context.ResourceManager.GetTextureOrImageBinding(
                Instruction.ImageAtomic,
                type,
                format,
                flags,
                TextureBank(inst),
                TextureHandle(inst));

            Operand res = context.ImageAtomic(type, format, flags, setAndBinding, sources.ToArray());

            if (!discard)
            {
                context.Copy(Gpr(inst.Rd), res);
                WriteFault(context, inst);
            }
        }
    }
}
