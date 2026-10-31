using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.Instructions;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using System;
using static Ryujinx.Graphics.Shader.Instructions.InstEmitAluHelper;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86Emit
    {
        /// <summary>MOV / UMOV d, B (bits 72-75 are the quad-lane mask, all lanes for a translation).</summary>
        public static void Mov(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            SetDest(context, inst, Src(context, inst, Slot.B));
        }

        /// <summary>
        /// IADD3 / UIADD3 d = A + B + C, each source optionally negated (bits 72, 63 / 75). Carry-outs: bits 81-83 and 84-86; .X (bit 74) adds the
        /// carry-in predicates in bits 87-90 and 77-80. Two carry outputs because three sources and two carries can carry twice; their sum is the carry.
        /// </summary>
        public static void Iadd3(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            bool uniform = IsUniform(inst);
            bool extended = inst.Bit(74);
            int carryOut0 = inst.Field(81, 3);
            int carryOut1 = inst.Field(84, 3);

            Operand a = IntSrc(context, inst, Slot.A);
            Operand b = IntSrc(context, inst, Slot.B);
            Operand c = IntSrc(context, inst, Slot.C);

            if (!extended && carryOut0 == RegisterConsts.PredicateTrueIndex && carryOut1 == RegisterConsts.PredicateTrueIndex)
            {
                SetDest(context, inst, context.IAdd(context.IAdd(a, b), c));
                return;
            }

            // With carries a negated source is ~x plus a carry-in of one (the adder's two's complement), so that subtractions borrow as on the GPU.
            Operand one = Const(1);
            Operand ra = context.BitwiseNot(Src(context, inst, Slot.A), Neg(inst, Slot.A));
            Operand rb = context.BitwiseNot(Src(context, inst, Slot.B), Neg(inst, Slot.B));
            Operand rc = context.BitwiseNot(Src(context, inst, Slot.C), Neg(inst, Slot.C));

            Operand sum = AddCarry(context, ra, rb, out Operand carry0);

            if (Neg(inst, Slot.A) || Neg(inst, Slot.B))
            {
                int ones = (Neg(inst, Slot.A) ? 1 : 0) + (Neg(inst, Slot.B) ? 1 : 0);
                sum = AddCarry(context, sum, Const(ones), out Operand c1);
                carry0 = context.BitwiseOr(carry0, c1);
            }

            if (extended)
            {
                Operand cin0 = context.ConditionalSelect(PredSrc(context, inst.Pred87, inst.Pred87Inv, uniform), one, Const(0));
                sum = AddCarry(context, sum, cin0, out Operand c2);
                carry0 = context.BitwiseOr(carry0, c2);
            }

            sum = AddCarry(context, sum, rc, out Operand carry1);

            if (Neg(inst, Slot.C))
            {
                sum = AddCarry(context, sum, one, out Operand c3);
                carry1 = context.BitwiseOr(carry1, c3);
            }

            if (extended)
            {
                Operand cin1 = context.ConditionalSelect(PredSrc(context, inst.Field(77, 3), inst.Bit(80), uniform), one, Const(0));
                sum = AddCarry(context, sum, cin1, out Operand c4);
                carry1 = context.BitwiseOr(carry1, c4);
            }

            SetDest(context, inst, sum);
            SetPredDest(context, carryOut0, carry0, uniform);
            SetPredDest(context, carryOut1, carry1, uniform);
        }

        /// <summary>32-bit add whose carry-out is a boolean (the result is below an operand when the add wrapped).</summary>
        private static Operand AddCarry(EmitterContext context, Operand lhs, Operand rhs, out Operand carry)
        {
            Operand result = context.IAdd(lhs, rhs);
            carry = context.ICompareLessUnsigned(result, lhs);
            return result;
        }

        /// <summary>IMAD / UIMAD d = A * B + C, low 32 bits (bit 73 signed does not change them); negations as IADD3.</summary>
        public static void Imad(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            if (inst.Bit(74))
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 IMAD.X at 0x{context.CurrOp.Address:X} is not implemented.");
            }

            Operand a = IntSrc(context, inst, Slot.A);
            Operand b = IntSrc(context, inst, Slot.B);
            Operand c = IntSrc(context, inst, Slot.C);

            SetDest(context, inst, context.IAdd(context.IMultiply(a, b), c));
        }

        /// <summary>LOP3.LUT / ULOP3.LUT d = LUT(A, B, C), table in bits 72-79; predicate output (result != 0) in bits 81-83.</summary>
        public static void Lop3(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            bool uniform = IsUniform(inst);

            Operand res = Lop3Expression.GetFromTruthTable(
                context,
                Src(context, inst, Slot.A),
                Src(context, inst, Slot.B),
                Src(context, inst, Slot.C),
                inst.Field(72, 8));

            SetDest(context, inst, res);

            int predOut = inst.Field(81, 3);

            if (predOut != RegisterConsts.PredicateTrueIndex)
            {
                if (inst.Bit(80) || inst.Pred87 != RegisterConsts.PredicateTrueIndex || !inst.Pred87Inv)
                {
                    context.TranslatorContext.GpuAccessor.Log($"SM86 LOP3 predicate output with .PAND or a predicate input at 0x{context.CurrOp.Address:X} is not implemented.");
                }

                SetPredDest(context, predOut, context.ICompareNotEqual(res, Const(0)), uniform);
            }
        }

        /// <summary>
        /// ISETP / UISETP: compare A with the second source (bits 76-78, signed with bit 73), combine with the predicate in bits 87-90 (bits 74-75:
        /// AND, OR, XOR) into bits 81-83, and the inverted comparison into 84-86. .EX (bit 72) compares the high words of a 64-bit pair, the low
        /// words' result being the predicate in bits 68-71.
        /// </summary>
        public static void Isetp(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            bool uniform = IsUniform(inst);

            Operand a = Src(context, inst, Slot.A);
            Operand b = Src(context, inst, SecondSlot(inst));
            IComp cmp = (IComp)inst.Field(76, 3);
            bool signed = inst.Bit(73);

            Operand res;

            if (inst.Bit(72))
            {
                Operand low = PredSrc(context, inst.Field(68, 3), inst.Bit(71), uniform);
                Operand equal = context.ICompareEqual(a, b);

                res = cmp switch
                {
                    IComp.Eq => context.BitwiseAnd(equal, low),
                    IComp.Ne => context.BitwiseOr(context.ICompareNotEqual(a, b), low),
                    IComp.Lt or IComp.Le => context.BitwiseOr(InstEmit.GetIntComparison(context, IComp.Lt, a, b, signed), context.BitwiseAnd(equal, low)),
                    IComp.Gt or IComp.Ge => context.BitwiseOr(InstEmit.GetIntComparison(context, IComp.Gt, a, b, signed), context.BitwiseAnd(equal, low)),
                    _ => InstEmit.GetIntComparison(context, cmp, a, b, signed),
                };
            }
            else
            {
                res = InstEmit.GetIntComparison(context, cmp, a, b, signed);
            }

            EmitSetp(context, inst, res, uniform);
        }

        /// <summary>FSETP: as ISETP with the float comparison in bits 76-79 and the operands' abs / neg modifiers.</summary>
        public static void Fsetp(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            Operand a = FpSrc(context, inst, Slot.A);
            Operand b = FpSrc(context, inst, SecondSlot(inst));

            EmitSetp(context, inst, InstEmit.GetFPComparison(context, (FComp)inst.Field(76, 4), a, b), IsUniform(inst));
        }

        private static void EmitSetp(EmitterContext context, Sm86Inst inst, Operand res, bool uniform)
        {
            BoolOp logicOp = (BoolOp)inst.Field(74, 2);
            Operand accum = PredSrc(context, inst.Pred87, inst.Pred87Inv, uniform);

            SetPredDest(context, inst.Field(81, 3), GetPredLogicalOp(context, logicOp, res, accum), uniform);
            SetPredDest(context, inst.Field(84, 3), GetPredLogicalOp(context, logicOp, context.BitwiseNot(res), accum), uniform);
        }

        /// <summary>FADD d = A + the second source; saturate bit 77 (rounding 78-79 and FTZ 80 are not modelled, as on Maxwell).</summary>
        public static void Fadd(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            Operand res = context.FPAdd(FpSrc(context, inst, Slot.A), FpSrc(context, inst, SecondSlot(inst)));

            SetDest(context, inst, context.FPSaturate(res, inst.Bit(77)));
        }

        /// <summary>FMUL d = A * the second source, scaled by bits 84-86 (1-3: /8, /4, /2; 4: none; 5-7: *2, *4, *8); saturate bit 77.</summary>
        public static void Fmul(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            Operand a = FpSrc(context, inst, Slot.A);
            int scale = inst.Field(84, 3);

            if (scale != 4)
            {
                if (scale == 0)
                {
                    context.TranslatorContext.GpuAccessor.Log($"SM86 FMUL with invalid scale 0 at 0x{context.CurrOp.Address:X}.");
                }
                else
                {
                    a = context.FPMultiply(a, ConstF(scale < 4 ? 1f / (1 << (4 - scale)) : 1 << (scale - 4)));
                }
            }

            Operand res = context.FPMultiply(a, FpSrc(context, inst, SecondSlot(inst)));

            SetDest(context, inst, context.FPSaturate(res, inst.Bit(77)));
        }

        /// <summary>FFMA d = A * B + C; saturate bit 77.</summary>
        public static void Ffma(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            Operand res = context.FPFusedMultiplyAdd(FpSrc(context, inst, Slot.A), FpSrc(context, inst, Slot.B), FpSrc(context, inst, Slot.C));

            SetDest(context, inst, context.FPSaturate(res, inst.Bit(77)));
        }

        /// <summary>
        /// MUFU d = f(B), function in bits 74-79 (0 COS, 1 SIN, 2 EX2, 3 LG2, 4 RCP, 5 RSQ, 6 RCP64H, 7 RSQ64H, 8 SQRT). SM70+ SIN and COS take the
        /// angle in turns (Maxwell's RRO did that division), so the argument is scaled by 2 pi.
        /// </summary>
        public static void Mufu(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            if (inst.Bit(73))
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 MUFU.F16 at 0x{context.CurrOp.Address:X} is not implemented.");
            }

            Operand src = FpSrc(context, inst, Slot.B);
            Operand res;

            switch (inst.Field(74, 6))
            {
                case 0:
                    res = context.FPCosine(context.FPMultiply(src, ConstF(2 * MathF.PI)));
                    break;
                case 1:
                    res = context.FPSine(context.FPMultiply(src, ConstF(2 * MathF.PI)));
                    break;
                case 2:
                    res = context.FPExponentB2(src);
                    break;
                case 3:
                    res = context.FPLogarithmB2(src);
                    break;
                case 4:
                    res = context.FPReciprocal(src);
                    break;
                case 5:
                    res = context.FPReciprocalSquareRoot(src);
                    break;
                case 6:
                    res = context.UnpackDouble2x32High(context.FPReciprocal(context.PackDouble2x32(Const(0), src), Instruction.FP64));
                    break;
                case 7:
                    res = context.UnpackDouble2x32High(context.FPReciprocalSquareRoot(context.PackDouble2x32(Const(0), src), Instruction.FP64));
                    break;
                case 8:
                    res = context.FPSquareRoot(src);
                    break;
                default:
                    context.TranslatorContext.GpuAccessor.Log($"SM86 MUFU function {inst.Field(74, 6)} at 0x{context.CurrOp.Address:X} is not implemented.");
                    res = src;
                    break;
            }

            SetDest(context, inst, res);
        }

        /// <summary>
        /// SHF / USHF: funnel shift of the 64-bit pair C:A (high:low) by B. Type in bits 73-74 (0 I64, 1 U64, 2 I32, 3 U32; signed types shift right
        /// arithmetically), wrap the amount modulo the width (bit 75) or clamp it to the width, right (bit 76), and the high (bit 80) or low half of the
        /// result. SM70+ semantics as Mesa NAK's constant folding (ir.rs, OpShf).
        /// </summary>
        public static void Shf(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            int type = inst.Field(73, 2);
            bool signed = type is 0 or 2;
            int bits = type < 2 ? 64 : 32;
            bool right = inst.Bit(76);

            Operand lo = Src(context, inst, Slot.A);
            Operand s = Src(context, inst, Slot.B);
            Operand hi = Src(context, inst, Slot.C);

            s = inst.Bit(75) ? context.BitwiseAnd(s, Const(bits - 1)) : context.IMinimumU32(s, Const(bits));

            Operand below32 = context.ICompareLessUnsigned(s, Const(32));
            Operand atLeast64 = context.ICompareEqual(s, Const(64));
            Operand s32 = context.BitwiseAnd(s, Const(31));
            Operand isZero = context.ICompareEqual(s32, Const(0));
            Operand inverse = context.ISubtract(Const(32), s32);
            Operand res;

            if (right)
            {
                Operand fill = signed ? context.ShiftRightS32(hi, Const(31)) : Const(0);
                Operand hiShifted = signed ? context.ShiftRightS32(hi, s32) : context.ShiftRightU32(hi, s32);

                if (inst.Bit(80))
                {
                    res = context.ConditionalSelect(below32, hiShifted, fill);
                }
                else
                {
                    Operand carry = context.ConditionalSelect(isZero, Const(0), context.ShiftLeft(hi, inverse));
                    Operand low = context.BitwiseOr(context.ShiftRightU32(lo, s32), carry);
                    res = context.ConditionalSelect(below32, low, context.ConditionalSelect(atLeast64, fill, hiShifted));
                }
            }
            else
            {
                Operand loShifted = context.ShiftLeft(lo, s32);

                if (inst.Bit(80))
                {
                    Operand carry = context.ConditionalSelect(isZero, Const(0), context.ShiftRightU32(lo, inverse));
                    Operand high = context.BitwiseOr(context.ShiftLeft(hi, s32), carry);
                    res = context.ConditionalSelect(below32, high, context.ConditionalSelect(atLeast64, Const(0), loShifted));
                }
                else
                {
                    res = context.ConditionalSelect(below32, loShifted, Const(0));
                }
            }

            SetDest(context, inst, res);
        }

        /// <summary>IMNMX d = the minimum (predicate bits 87-90 true) or maximum of A and the second source; signed with bit 73.</summary>
        public static void Imnmx(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            Operand a = Src(context, inst, Slot.A);
            Operand b = Src(context, inst, SecondSlot(inst));
            bool signed = inst.Bit(73);

            Operand min = signed ? context.IMinimumS32(a, b) : context.IMinimumU32(a, b);
            Operand max = signed ? context.IMaximumS32(a, b) : context.IMaximumU32(a, b);

            SetDest(context, inst, SelectByPred87(context, inst, min, max));
        }

        /// <summary>FMNMX d = the minimum (predicate bits 87-90 true) or maximum of A and the second source, with their abs / neg modifiers.</summary>
        public static void Fmnmx(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            Operand a = FpSrc(context, inst, Slot.A);
            Operand b = FpSrc(context, inst, SecondSlot(inst));

            SetDest(context, inst, SelectByPred87(context, inst, context.FPMinimum(a, b), context.FPMaximum(a, b)));
        }

        /// <summary>SEL / USEL d = predicate (bits 87-90) ? A : the second source.</summary>
        public static void Sel(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            SetDest(context, inst, SelectByPred87(context, inst, Src(context, inst, Slot.A), Src(context, inst, SecondSlot(inst))));
        }

        /// <summary>FSEL d = predicate (bits 87-90) ? A : the second source, with their abs / neg modifiers.</summary>
        public static void Fsel(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            SetDest(context, inst, SelectByPred87(context, inst, FpSrc(context, inst, Slot.A), FpSrc(context, inst, SecondSlot(inst))));
        }

        private static Operand SelectByPred87(EmitterContext context, Sm86Inst inst, Operand ifTrue, Operand ifFalse)
        {
            if (inst.Pred87 == RegisterConsts.PredicateTrueIndex)
            {
                return inst.Pred87Inv ? ifFalse : ifTrue;
            }

            return context.ConditionalSelect(PredSrc(context, inst.Pred87, inst.Pred87Inv, IsUniform(inst)), ifTrue, ifFalse);
        }

        /// <summary>CS2R d = special register (bits 72-79, 0xff SRZ reads 0); bit 80 reads 64 bits into d, d+1.</summary>
        public static void Cs2r(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int sreg = inst.Field(72, 8);
            bool wide = inst.Bit(80);

            if (sreg != 0xff && wide)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 CS2R.64 of special register 0x{sreg:x} at 0x{context.CurrOp.Address:X}: high word read as 0.");
            }

            Operand value = sreg == 0xff ? Const(0) : InstEmit.EmitLoadSpecialRegister(context, (SReg)sreg);

            context.Copy(Gpr(inst.Rd), value);

            if (wide && inst.Rd + 1 < RegisterConsts.RegisterZeroIndex)
            {
                context.Copy(Gpr(inst.Rd + 1), Const(0));
            }
        }

        /// <summary>Writes a P (or, uniform, UP) destination; index 7 (PT) discards the value.</summary>
        public static void SetPredDest(EmitterContext context, int index, Operand value, bool uniform)
        {
            if (index != RegisterConsts.PredicateTrueIndex)
            {
                context.Copy(uniform ? UPred(index) : Pred(index), value);
            }
        }
    }
}
