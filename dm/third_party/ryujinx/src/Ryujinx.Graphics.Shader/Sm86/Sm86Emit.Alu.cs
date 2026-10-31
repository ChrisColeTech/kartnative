using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.Instructions;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
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
        /// IADD3 / UIADD3 d = A + B + C, each source negated with bits 72, 63 / 75. .X (bit 74) adds the carry-in predicates in bits 87-90 and 77-80, and
        /// its modifier bits invert a source (~x) instead of negating it. The carry outputs, bits 81-83 and 84-86, are the exact sum's bits above 31: at
        /// least one carry, and at least two (a negated source counts as ~x + 1). Semantics: NAK's hardware-tested folds (OpIAdd3, OpIAdd3X).
        /// </summary>
        public static void Iadd3(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            bool uniform = IsUniform(inst);
            bool extended = inst.Bit(74);
            int carryOut0 = inst.Field(81, 3);
            int carryOut1 = inst.Field(84, 3);

            if (!extended && carryOut0 == RegisterConsts.PredicateTrueIndex && carryOut1 == RegisterConsts.PredicateTrueIndex)
            {
                Operand a = IntSrc(context, inst, Slot.A);
                Operand b = IntSrc(context, inst, Slot.B);
                Operand c = IntSrc(context, inst, Slot.C);

                SetDest(context, inst, context.IAdd(context.IAdd(a, b), c));
                return;
            }

            var terms = new List<Operand>();
            int ones = 0;

            foreach (Slot slot in new[] { Slot.A, Slot.B, Slot.C })
            {
                bool neg = Neg(inst, slot);

                terms.Add(context.BitwiseNot(Src(context, inst, slot), neg));
                ones += neg && !extended ? 1 : 0;
            }

            if (ones != 0)
            {
                terms.Add(Const(ones));
            }

            if (extended)
            {
                terms.Add(PredAsInt(context, PredSrc(context, inst.Pred87, inst.Pred87Inv, uniform)));
                terms.Add(PredAsInt(context, PredSrc(context, inst.Field(77, 3), inst.Bit(80), uniform)));
            }

            Operand sum = SumWithCarries(context, terms, out Operand carries);

            SetDest(context, inst, sum);
            SetPredDest(context, carryOut0, context.ICompareNotEqual(carries, Const(0)), uniform);
            SetPredDest(context, carryOut1, context.ICompareGreaterUnsigned(carries, Const(1)), uniform);
        }

        /// <summary>1 for a true predicate, 0 for a false one.</summary>
        private static Operand PredAsInt(EmitterContext context, Operand pred)
        {
            return context.ConditionalSelect(pred, Const(1), Const(0));
        }

        /// <summary>The 32-bit sum of <paramref name="terms"/> and the number of carries out of bit 31 (the exact sum's bits above 31).</summary>
        private static Operand SumWithCarries(EmitterContext context, IReadOnlyList<Operand> terms, out Operand carries)
        {
            Operand sum = terms[0];

            carries = Const(0);

            for (int index = 1; index < terms.Count; index++)
            {
                Operand next = context.IAdd(sum, terms[index]);

                carries = context.IAdd(carries, PredAsInt(context, context.ICompareLessUnsigned(next, sum)));
                sum = next;
            }

            return sum;
        }

        /// <summary>32-bit add whose carry-out is a boolean (the result is below an operand when the add wrapped).</summary>
        private static Operand AddCarry(EmitterContext context, Operand lhs, Operand rhs, out Operand carry)
        {
            Operand result = context.IAdd(lhs, rhs);
            carry = context.ICompareLessUnsigned(result, lhs);
            return result;
        }

        /// <summary>
        /// IMAD / UIMAD d = A * B + C, low 32 bits (bit 73 signed does not change them), sources negated with bits 72, 63 / 75. .X (bit 74) adds the carry-in
        /// predicate in bits 87-90 and inverts C (~C) instead of negating it: the high word of a 64-bit add (MKW: IMAD.X d, RZ, RZ, c[..], P1 after an
        /// IADD3 carry). The carry output (bits 81-83, PT in every MKW IMAD) is not modelled.
        /// </summary>
        public static void Imad(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            if (inst.Field(81, 3) != RegisterConsts.PredicateTrueIndex)
            {
                LogNotImplemented(context, "IMAD carry output");
            }

            Operand a = IntSrc(context, inst, Slot.A);
            Operand b = IntSrc(context, inst, Slot.B);
            Operand product = context.IMultiply(a, b);

            if (inst.Bit(74))
            {
                Operand c = context.BitwiseNot(Src(context, inst, Slot.C), Neg(inst, Slot.C));
                Operand carryIn = PredAsInt(context, PredSrc(context, inst.Pred87, inst.Pred87Inv, IsUniform(inst)));

                SetDest(context, inst, context.IAdd(context.IAdd(product, c), carryIn));
                return;
            }

            SetDest(context, inst, context.IAdd(product, IntSrc(context, inst, Slot.C)));
        }

        /// <summary>IMAD.HI d = the high 32 bits of A * B (signed with bit 73) + C: PTX mad.hi, equal to (A * B + (C &lt;&lt; 32)) &gt;&gt; 32.</summary>
        public static void ImadHi(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            CheckModifiers(context, inst, "IMAD.HI", ModMask(73, 75, 81, 82, 83, 87, 88, 89, 90));

            Operand a = Src(context, inst, Slot.A);
            Operand b = Src(context, inst, Slot.B);
            Operand high = inst.Bit(73) ? context.MultiplyHighS32(a, b) : context.MultiplyHighU32(a, b);

            SetDest(context, inst, context.IAdd(high, IntSrc(context, inst, Slot.C)));
        }

        /// <summary>
        /// IMAD.WIDE / UIMAD.WIDE d:d+1 = A * B (the 64-bit product, signed with bit 73) + the 64-bit pair C:C+1, negated with C's neg bit. Negated
        /// A / B and .X are not modelled (not in MKW).
        /// </summary>
        public static void ImadWide(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            if (Neg(inst, Slot.A) || Neg(inst, Slot.B) || inst.Bit(74) || inst.Field(81, 3) != RegisterConsts.PredicateTrueIndex)
            {
                LogNotImplemented(context, "IMAD.WIDE with a negated factor, .X or a carry output");
            }

            Operand a = Src(context, inst, Slot.A);
            Operand b = Src(context, inst, Slot.B);
            (Operand cLo, Operand cHi) = SrcPair(context, inst, Slot.C);

            if (Neg(inst, Slot.C))
            {
                cHi = context.IAdd(context.BitwiseNot(cHi), PredAsInt(context, context.ICompareEqual(cLo, Const(0))));
                cLo = context.INegate(cLo);
            }

            Operand lo = AddCarry(context, context.IMultiply(a, b), cLo, out Operand carry);
            Operand high = inst.Bit(73) ? context.MultiplyHighS32(a, b) : context.MultiplyHighU32(a, b);
            Operand hi = context.IAdd(context.IAdd(high, cHi), PredAsInt(context, carry));

            SetDestPair(context, inst, lo, hi);
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

        /// <summary>
        /// FSET.BF d = 1.0 when the comparison (bits 76-79) of A and the second source, with their abs / neg modifiers, combined with the predicate in
        /// bits 87-90 by bits 74-75 holds, else 0.0. These bits clear are FSET.BF (NAK's nvdisasm tests); FTZ (bit 80) is not modelled.
        /// </summary>
        public static void Fset(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            bool uniform = IsUniform(inst);

            CheckModifiers(context, inst, "FSET", ModMask(72, 73, 74, 75, 76, 77, 78, 79, 80, 87, 88, 89, 90));

            Operand a = FpSrc(context, inst, Slot.A);
            Operand b = FpSrc(context, inst, SecondSlot(inst));
            Operand res = InstEmit.GetFPComparison(context, (FComp)inst.Field(76, 4), a, b);

            res = GetPredLogicalOp(context, (BoolOp)inst.Field(74, 2), res, PredSrc(context, inst.Pred87, inst.Pred87Inv, uniform));

            SetDest(context, inst, context.ConditionalSelect(res, ConstF(1), ConstF(0)));
        }

        /// <summary>
        /// FSWZADD d = per quad lane l, an operation code from bits 32-39 (lane 0 in bits 38-39, lane 3 in 32-33): 0 A + B, 1 A - B, 2 B - A, 3 B, with
        /// A in bits 24-31 and B the register in 64-71 (NAK OpFSwzAdd and its derivative lowering: lane 0 of ddx is A - B with A the neighbour).
        /// Ryujinx's SwizzleAdd takes lane 0 in its low bits and codes 1 / 2 the other way round. Rounding (78-79) and FTZ (80) are not modelled.
        /// </summary>
        public static void Fswzadd(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            CheckModifiers(context, inst, "FSWZADD", ModMask(77, 78, 79, 80));

            int ops = inst.Field(32, 8);
            int mask = 0;

            for (int lane = 0; lane < 4; lane++)
            {
                int code = (ops >> (6 - lane * 2)) & 3;

                mask |= (code is 1 or 2 ? code ^ 3 : code) << (lane * 2);
            }

            SetDest(context, inst, context.FPSwizzleAdd(Gpr(inst.Ra), Gpr(inst.Rc), mask));
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
        /// MUFU d = f(B), function in bits 74-79 (0 COS, 1 SIN, 2 EX2, 3 LG2, 4 RCP, 5 RSQ, 6 RCP64H, 7 RSQ64H, 8 SQRT, 9 TANH). SM70+ SIN and COS take
        /// the angle in turns (Maxwell's RRO did that division; the compiler emits FMUL.RZ by 1 / 2 pi before them), so the argument is scaled by 2 pi.
        /// .F16 (bit 73): the source is the half that bits 60-61 select and the result an F16 in the low half (NAK OpMuFu). MKW uses functions 0-5 and
        /// 8 on F32 only.
        /// </summary>
        public static void Mufu(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            bool f16 = inst.Bit(73);

            CheckModifiers(context, inst, "MUFU", ModMask(73, 74, 75, 76, 77, 78, 79));

            Operand src = ConvFloatSrc(context, inst, f16 ? 1 : 2, out _);
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
                case 9:
                    // tanh(x) = 1 - 2 / (e^2x + 1): exact at both infinities (e^2x overflowing to +inf gives 1, underflowing to 0 gives -1).
                    Operand e2x = context.FPExponentB2(context.FPMultiply(src, ConstF(2 * 1.442695041f)));
                    res = context.FPSubtract(ConstF(1), context.FPDivide(ConstF(2), context.FPAdd(e2x, ConstF(1))));
                    break;
                default:
                    context.TranslatorContext.GpuAccessor.Log($"SM86 MUFU function {inst.Field(74, 6)} at 0x{context.CurrOp.Address:X} is not implemented.");
                    res = src;
                    break;
            }

            SetDest(context, inst, f16 ? context.PackHalf2x16(res, ConstF(0)) : res);
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

        /// <summary>The B source of a bit operation (FLO, POPC), inverted with bit 63 when B is not an immediate (whose bits 32-63 are all value).</summary>
        private static Operand NotSrcB(EmitterContext context, Sm86Inst inst)
        {
            return context.BitwiseNot(Src(context, inst, Slot.B), inst.Form != 4 && inst.Bit(63));
        }

        /// <summary>IABS / UIABS d = |B| (|INT_MIN| = INT_MIN); semantics: NAK's hardware-tested fold (OpIAbs).</summary>
        public static void Iabs(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            CheckModifiers(context, inst, "IABS", 0);
            SetDest(context, inst, context.IAbsolute(Src(context, inst, Slot.B)));
        }

        /// <summary>
        /// FLO / UFLO d = the position of the most significant bit of B (inverted with bit 63) that differs from the sign bit when signed (bit 73), or
        /// with .SH (bit 74) its distance from bit 31. No such bit gives 0xffffffff in both forms (PTX bfind); otherwise as NAK's hardware-tested fold.
        /// </summary>
        public static void Flo(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            CheckModifiers(context, inst, "FLO", ModMask(73, 74, 81, 82, 83));

            if (inst.Field(81, 3) != RegisterConsts.PredicateTrueIndex)
            {
                LogNotImplemented(context, "FLO predicate output");
            }

            Operand src = NotSrcB(context, inst);
            Operand msb = inst.Bit(73) ? context.FindMSBS32(src) : context.FindMSBU32(src);

            if (inst.Bit(74))
            {
                msb = context.ConditionalSelect(context.ICompareLess(msb, Const(0)), msb, context.ISubtract(Const(31), msb));
            }

            SetDest(context, inst, msb);
        }

        /// <summary>POPC / UPOPC d = the number of set bits of B, inverted with bit 63 (NAK's hardware-tested fold).</summary>
        public static void Popc(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            CheckModifiers(context, inst, "POPC", 0);
            SetDest(context, inst, context.BitCount(NotSrcB(context, inst)));
        }

        /// <summary>BREV / UBREV d = B with its bits reversed.</summary>
        public static void Brev(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            CheckModifiers(context, inst, "BREV", 0);
            SetDest(context, inst, context.BitfieldReverse(Src(context, inst, Slot.B)));
        }

        /// <summary>
        /// SGXT / USGXT d = A's low B bits, sign-extended (bit 73) or zero-extended to 32 bits. B is clamped to 32 or, with .W (bit 75), taken modulo 32;
        /// 0 bits give 0, 32 give A (NAK's hardware-tested fold, OpSgxt).
        /// </summary>
        public static void Sgxt(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            CheckModifiers(context, inst, "SGXT", ModMask(73, 75));

            Operand a = Src(context, inst, Slot.A);
            Operand bits = Src(context, inst, Slot.B);

            bits = inst.Bit(75) ? context.BitwiseAnd(bits, Const(31)) : context.IMinimumU32(bits, Const(32));

            Operand shift = context.BitwiseAnd(context.ISubtract(Const(32), bits), Const(31));
            Operand up = context.ShiftLeft(a, shift);
            Operand res = inst.Bit(73) ? context.ShiftRightS32(up, shift) : context.ShiftRightU32(up, shift);

            SetDest(context, inst, context.ConditionalSelect(context.ICompareEqual(bits, Const(0)), Const(0), res));
        }

        /// <summary>
        /// BMSK / UBMSK d = a mask of B ones starting at bit A: both clamped to 32 or, with .W (bit 75), taken modulo 32; bits past 31 are dropped (PTX
        /// bmsk). Not in MKW.
        /// </summary>
        public static void Bmsk(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            CheckModifiers(context, inst, "BMSK", ModMask(75));

            Operand pos = Src(context, inst, Slot.A);
            Operand width = Src(context, inst, Slot.B);

            if (inst.Bit(75))
            {
                pos = context.BitwiseAnd(pos, Const(31));
                width = context.BitwiseAnd(width, Const(31));
            }
            else
            {
                pos = context.IMinimumU32(pos, Const(32));
                width = context.IMinimumU32(width, Const(32));
            }

            Operand ones = context.ConditionalSelect(
                context.ICompareEqual(width, Const(32)),
                Const(-1),
                context.ISubtract(context.ShiftLeft(Const(1), context.BitwiseAnd(width, Const(31))), Const(1)));
            Operand res = context.ShiftLeft(ones, context.BitwiseAnd(pos, Const(31)));

            SetDest(context, inst, context.ConditionalSelect(context.ICompareEqual(pos, Const(32)), Const(0), res));
        }

        /// <summary>
        /// LEA / ULEA d = (A &lt;&lt; shift) + B, shift in bits 75-79. .HI (bit 80) shifts the 64-bit C:A (A sign-extended instead with .SX32, bit 73)
        /// and keeps its high word. The shifted value is negated with bit 72 and B with its slot's neg bit (none in forms 2, 3 and 7, where bit 75 is the
        /// shift); the carry out (the exact sum's bit 32, a negation of 0 carrying) goes to bits 81-83. .X (bit 74) adds the carry-in predicate (bits
        /// 87-90), and its modifier bits invert instead of negate. Semantics: NAK's hardware-tested folds (OpLea, OpLeaX).
        /// </summary>
        public static void Lea(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            bool uniform = IsUniform(inst);
            bool extended = inst.Bit(74);
            int shift = inst.Field(75, 5);
            int carryOut = inst.Field(81, 3);

            Operand a = Src(context, inst, Slot.A);
            Operand b = Src(context, inst, Slot.B);
            Operand shifted;

            if (inst.Bit(80))
            {
                Operand high = inst.Bit(73) ? context.ShiftRightS32(a, Const(31)) : Src(context, inst, Slot.C);

                shifted = shift == 0 ? high : context.BitwiseOr(context.ShiftRightU32(a, Const(32 - shift)), context.ShiftLeft(high, Const(shift)));
            }
            else
            {
                if (inst.Bit(73))
                {
                    LogNotImplemented(context, "LEA.SX32 without .HI");
                }

                shifted = context.ShiftLeft(a, Const(shift));
            }

            bool negShifted = inst.Bit(72);
            bool negB = inst.Form is 1 or 5 or 6 && inst.Bit(63);

            if (!extended && carryOut == RegisterConsts.PredicateTrueIndex)
            {
                SetDest(context, inst, context.IAdd(context.INegate(shifted, negShifted), context.INegate(b, negB)));
                return;
            }

            var terms = new List<Operand>
            {
                context.BitwiseNot(shifted, negShifted),
                context.BitwiseNot(b, negB),
            };

            if (extended)
            {
                terms.Add(PredAsInt(context, PredSrc(context, inst.Pred87, inst.Pred87Inv, uniform)));
            }
            else if (negShifted || negB)
            {
                terms.Add(Const((negShifted ? 1 : 0) + (negB ? 1 : 0)));
            }

            Operand sum = SumWithCarries(context, terms, out Operand carries);

            SetDest(context, inst, sum);
            SetPredDest(context, carryOut, context.ICompareNotEqual(carries, Const(0)), uniform);
        }

        /// <summary>
        /// PRMT / UPRMT d = 4 bytes picked from the pair C:A (bytes 0-3 from A, 4-7 from C) by the selector B and the mode in bits 72-74. Mode 0
        /// (index): result byte i takes B's nibble i, bits 0-2 the byte and bit 3 to replicate that byte's sign (NAK's hardware-tested fold). Modes
        /// 1-6 (f4e, b4e, rc8, ecl, ecr, rc16) use B's bits 0-1 as PTX prmt. A constant selector (every MKW PRMT) becomes shifts and masks.
        /// </summary>
        public static void Prmt(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int mode = inst.Field(72, 3);

            CheckModifiers(context, inst, "PRMT", ModMask(72, 73, 74));

            if (mode == 7)
            {
                LogNotImplemented(context, "PRMT mode 7");
                return;
            }

            Operand a = Src(context, inst, Slot.A);
            Operand sel = Src(context, inst, Slot.B);
            Operand c = Src(context, inst, Slot.C);

            SetDest(context, inst, sel.Type == OperandType.Constant
                ? PermuteStatic(context, a, c, sel.Value, mode)
                : PermuteDynamic(context, a, c, sel, mode));
        }

        /// <summary>The byte index (0-7 of C:A) a non-index PRMT mode gives result byte <paramref name="i"/> for selector bits <paramref name="n"/>.</summary>
        private static int PrmtModeIndex(int mode, int n, int i)
        {
            return mode switch
            {
                1 => (n + i) & 7,
                2 => (n - i) & 7,
                3 => n,
                4 => Math.Max(i, n),
                5 => Math.Min(i, n),
                _ => (n & 1) * 2 + (i & 1),
            };
        }

        private static Operand PermuteStatic(EmitterContext context, Operand a, Operand c, int sel, int mode)
        {
            Operand res = null;

            void Or(Operand part) => res = res == null ? part : context.BitwiseOr(res, part);

            int i = 0;

            while (i < 4)
            {
                int nibble = mode == 0 ? (sel >> (i * 4)) & 0xf : PrmtModeIndex(mode, sel & 3, i);
                int index = nibble & 7;
                Operand src = index < 4 ? a : c;
                int srcByte = index & 3;

                if ((nibble & 8) != 0)
                {
                    // The byte's sign, replicated over the result byte.
                    Operand sign = context.ShiftRightS32(context.ShiftLeft(src, Const(24 - srcByte * 8)), Const(31));

                    Or(context.BitwiseAnd(sign, Const(0xff << (i * 8))));
                    i++;
                    continue;
                }

                // A run of result bytes that come from consecutive bytes of the same source moves with one shift.
                int count = 1;

                while (i + count < 4)
                {
                    int next = mode == 0 ? (sel >> ((i + count) * 4)) & 0xf : PrmtModeIndex(mode, sel & 3, i + count);

                    if ((next & 8) != 0 || (next & 4) != (index & 4) || (next & 3) != srcByte + count)
                    {
                        break;
                    }

                    count++;
                }

                int delta = (i - srcByte) * 8;
                Operand moved = delta > 0 ? context.ShiftLeft(src, Const(delta)) : delta < 0 ? context.ShiftRightU32(src, Const(-delta)) : src;
                uint mask = (count == 4 ? uint.MaxValue : (1u << (count * 8)) - 1) << (i * 8);

                Or(mask == uint.MaxValue ? moved : context.BitwiseAnd(moved, Const((int)mask)));
                i += count;
            }

            return res;
        }

        private static Operand PermuteDynamic(EmitterContext context, Operand a, Operand c, Operand sel, int mode)
        {
            Operand res = Const(0);
            Operand n = context.BitwiseAnd(sel, Const(3));

            for (int i = 0; i < 4; i++)
            {
                Operand nibble = mode switch
                {
                    0 => context.BitwiseAnd(context.ShiftRightU32(sel, Const(i * 4)), Const(0xf)),
                    1 => context.BitwiseAnd(context.IAdd(n, Const(i)), Const(7)),
                    2 => context.BitwiseAnd(context.ISubtract(n, Const(i)), Const(7)),
                    3 => n,
                    4 => context.IMaximumU32(n, Const(i)),
                    5 => context.IMinimumU32(n, Const(i)),
                    _ => context.IAdd(context.ShiftLeft(context.BitwiseAnd(n, Const(1)), Const(1)), Const(i & 1)),
                };

                Operand src = context.ConditionalSelect(context.ICompareNotEqual(context.BitwiseAnd(nibble, Const(4)), Const(0)), c, a);
                Operand position = context.ShiftLeft(context.BitwiseAnd(nibble, Const(3)), Const(3));
                Operand value = context.BitfieldExtractU32(src, position, Const(8));

                if (mode == 0)
                {
                    Operand sign = context.BitwiseAnd(context.BitfieldExtractS32(src, context.IAdd(position, Const(7)), Const(1)), Const(0xff));

                    value = context.ConditionalSelect(context.ICompareNotEqual(context.BitwiseAnd(nibble, Const(8)), Const(0)), sign, value);
                }

                res = context.BitwiseOr(res, context.ShiftLeft(value, Const(i * 8)));
            }

            return res;
        }

        /// <summary>
        /// IDP.4A d = the dot product of A's and B's four bytes (signed with bits 73 and 74) + C (PTX dp4a; NAK OpIDp4). Not in MKW.
        /// </summary>
        public static void Idp4a(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            CheckModifiers(context, inst, "IDP.4A", ModMask(73, 74));

            Operand a = Src(context, inst, Slot.A);
            Operand b = Src(context, inst, Slot.B);
            Operand res = Src(context, inst, Slot.C);

            for (int i = 0; i < 4; i++)
            {
                Operand ab = inst.Bit(73) ? context.BitfieldExtractS32(a, Const(i * 8), Const(8)) : context.BitfieldExtractU32(a, Const(i * 8), Const(8));
                Operand bb = inst.Bit(74) ? context.BitfieldExtractS32(b, Const(i * 8), Const(8)) : context.BitfieldExtractU32(b, Const(i * 8), Const(8));

                res = context.IAdd(res, context.IMultiply(ab, bb));
            }

            SetDest(context, inst, res);
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
