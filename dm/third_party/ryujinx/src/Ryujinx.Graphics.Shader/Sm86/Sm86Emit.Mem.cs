using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.Instructions;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86Emit
    {
        /// <summary>S2R / S2UR d = special register (bits 72-79, Maxwell's numbering: 0x21 TID.X, 0x25 CTAID.X...).</summary>
        public static void S2r(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            SetDest(context, inst, InstEmit.EmitLoadSpecialRegister(context, (SReg)inst.Field(72, 8)));
        }

        /// <summary>
        /// LDC d, c[bank][Ra + offset]: bank in bits 54-58, signed byte offset in 38-53, size in 73-75 (U8, S8, U16, S16, B32, B64), address mode in
        /// 78-79 (as Maxwell's: indexed, linear, segmented, segmented linear). Bit 91 (bank from a uniform register) is not implemented.
        /// </summary>
        public static void Ldc(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            if (inst.Bit(91))
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 LDC with a uniform-register bank at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            InstEmit.EmitLdc(context, new InstEmit.LdcFields(
                inst.Rd,
                inst.Ra,
                inst.Field(54, 5),
                inst.Field(38, 16),
                (LsSize2)inst.Field(73, 3),
                (AddressMode)inst.Field(78, 2)));
        }

        /// <summary>ULDC URd, c[bank][offset]: as LDC without an index register (bits 24-31 are zero), 32 or 64 bits into uniform registers.</summary>
        public static void Uldc(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int size = inst.Field(73, 3);

            if (size is not ((int)LsSize2.B32 or (int)LsSize2.B64))
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 ULDC of size {size} at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            Operand slot = Const(inst.Field(54, 5));
            int wordOffset = inst.Field(38, 16) >> 2;
            int count = size == (int)LsSize2.B64 ? 2 : 1;

            for (int index = 0; index < count; index++)
            {
                int dest = inst.URd + index;

                if (dest >= RegisterConsts.UniformRegisterZeroIndex)
                {
                    break;
                }

                context.Copy(UGpr(dest), InstEmit.EmitLoadConstant(context, slot, Const(wordOffset + index)));
            }
        }

        /// <summary>ALD d, a[Ra + addr][vertex Rb]: address bits 40-49, components - 1 in 74-75, per-patch 76, physical 77, output 79.</summary>
        public static void Ald(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            InstEmit.EmitAld(context, new InstEmit.AldFields(inst.Rd, inst.Ra, inst.Rb, inst.Field(40, 10), inst.Field(74, 2), inst.Bit(76), inst.Bit(79), inst.Bit(77)));
        }

        /// <summary>AST a[Ra + addr], data Rb (bits 32-39); vertex Rc (64-71) unused; fields as ALD.</summary>
        public static void Ast(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            InstEmit.EmitAst(context, new InstEmit.AstFields(inst.Ra, inst.Rb, inst.Field(40, 10), inst.Field(74, 2), inst.Bit(76), inst.Bit(77)));
        }

        /// <summary>
        /// IPA d, a[addr]: attribute word address in bits 64-71, location in 76-77 (default, centroid, offset by Rb), frequency in 78-79 (pass,
        /// constant, state). As on Maxwell, a pass read of a perspective attribute is the attribute times 1/w (the program multiplies by w itself).
        /// </summary>
        public static void Ipa(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            if (inst.Field(76, 2) == 2)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 IPA at an offset at 0x{context.CurrOp.Address:X}: interpolated at the pixel centre.");
            }

            IpaOp op = inst.Field(78, 2) switch
            {
                1 => IpaOp.Constant,
                2 => IpaOp.Sc,
                _ => IpaOp.Pass,
            };

            InstEmit.EmitIpa(context, new InstEmit.IpaFields(inst.Rd, RegisterConsts.RegisterZeroIndex, RegisterConsts.RegisterZeroIndex, inst.Field(64, 8) * 4, false, op, false));
        }
    }
}
