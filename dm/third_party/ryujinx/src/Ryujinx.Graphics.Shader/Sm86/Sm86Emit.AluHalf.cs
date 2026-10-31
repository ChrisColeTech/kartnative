using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.Instructions;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using static Ryujinx.Graphics.Shader.Instructions.InstEmitAluHelper;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Sm86
{
    /// <summary>
    /// F16x2 operations of the ALU family: HADD2, HMUL2, HFMA2, HSET2, HSETP2, HMNMX2. Field layouts: Mesa NAK sm70_encode.rs (encode_fp16v2_alu). Each
    /// source has a swizzle (A bits 74-75, the 32-bit slot 60-61, the C register 81-82): 0 the two halves, 1 a 32-bit float in both lanes, 2 the low
    /// half in both, 3 the high half in both (the order of the Maxwell HalfSwizzle); an immediate is two halves. The C register's abs / neg move to bits
    /// 83 / 84 (bits 74 / 75 are A's swizzle). Two-source operations take the second source from the 32-bit slot in every form (B in forms 1, 4, 5, 6,
    /// C in forms 2, 3, 7). Lanes are computed in FP32, as the Maxwell frontend does, and packed back to F16 with rounding to nearest even.
    /// </summary>
    static partial class Sm86Emit
    {
        /// <summary>The two lanes of an F16x2 source as FP32 values, with the slot's swizzle and abs / neg modifiers.</summary>
        private static Operand[] HalfSrc(EmitterContext context, Sm86Inst inst, Slot slot)
        {
            Operand raw = Src(context, inst, slot);
            int swizzleBit;
            int absBit;
            int negBit;

            if (slot == Slot.A)
            {
                (swizzleBit, absBit, negBit) = (74, 73, 72);
            }
            else if ((slot == Slot.B) == CInHighSlot(inst))
            {
                (swizzleBit, absBit, negBit) = (81, 83, 84);
            }
            else if (inst.Form is 2 or 4)
            {
                (swizzleBit, absBit, negBit) = (-1, -1, -1);
            }
            else
            {
                (swizzleBit, absBit, negBit) = (60, 62, 63);
            }

            HalfSwizzle swizzle = swizzleBit < 0 ? HalfSwizzle.F16 : (HalfSwizzle)inst.Field(swizzleBit, 2);
            Operand[] lanes = InstEmitHelper.GetHalfUnpacked(context, raw, swizzle);

            bool abs = absBit >= 0 && inst.Bit(absBit);
            bool neg = negBit >= 0 && inst.Bit(negBit);

            for (int lane = 0; lane < lanes.Length; lane++)
            {
                lanes[lane] = context.FPAbsNeg(lanes[lane], abs, neg);
            }

            return lanes;
        }

        /// <summary>Writes two FP32 lanes as an F16x2 result or, with .F32 (bit 78), lane 0 as an F32 result.</summary>
        private static void SetHalfDest(EmitterContext context, Sm86Inst inst, Operand[] res)
        {
            SetDest(context, inst, inst.Bit(78) ? res[0] : context.PackHalf2x16(res[0], res[1]));
        }

        /// <summary>Saturate (bit 77) and, on SM86, .RELU (bit 79: negative results become 0, NaN stays) per lane.</summary>
        private static Operand HalfClamp(EmitterContext context, Sm86Inst inst, Operand value, bool hasRelu)
        {
            value = context.FPSaturate(value, inst.Bit(77));

            if (hasRelu && inst.Bit(79))
            {
                value = context.ConditionalSelect(context.FPCompareLess(value, ConstF(0)), ConstF(0), value);
            }

            return value;
        }

        private static readonly uint _hadd2Known = ModMask(72, 73, 74, 75, 77, 78, 80);

        /// <summary>
        /// HADD2 d = A + the second source per lane; saturate bit 77, .F32 (bit 78) writes lane 0 as an F32 (MKW: HADD2.F32 d, -RZ, x.H0_H0, the half to
        /// float conversion), FTZ (80) not modelled.
        /// </summary>
        public static void Hadd2(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            CheckModifiers(context, inst, "HADD2", _hadd2Known);

            Operand[] a = HalfSrc(context, inst, Slot.A);
            Operand[] b = HalfSrc(context, inst, SecondSlot(inst));
            Operand[] res = new Operand[2];

            for (int lane = 0; lane < 2; lane++)
            {
                res[lane] = HalfClamp(context, inst, context.FPAdd(a[lane], b[lane]), false);
            }

            SetHalfDest(context, inst, res);
        }

        private static readonly uint _hmul2Known = ModMask(72, 73, 74, 75, 76, 77, 78, 79, 80);

        /// <summary>HMUL2 d = A * the second source per lane; saturate bit 77, .RELU 79, .F32 78; DNZ (76) and FTZ (80) not modelled.</summary>
        public static void Hmul2(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            CheckModifiers(context, inst, "HMUL2", _hmul2Known);

            Operand[] a = HalfSrc(context, inst, Slot.A);
            Operand[] b = HalfSrc(context, inst, SecondSlot(inst));
            Operand[] res = new Operand[2];

            for (int lane = 0; lane < 2; lane++)
            {
                res[lane] = HalfClamp(context, inst, context.FPMultiply(a[lane], b[lane]), true);
            }

            SetHalfDest(context, inst, res);
        }

        private static readonly uint _hfma2Known = ModMask(72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84);

        /// <summary>HFMA2 d = A * B + C per lane; saturate bit 77, .RELU 79, .F32 78; DNZ (76) and FTZ (80) not modelled.</summary>
        public static void Hfma2(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            CheckModifiers(context, inst, "HFMA2", _hfma2Known);

            Operand[] a = HalfSrc(context, inst, Slot.A);
            Operand[] b = HalfSrc(context, inst, Slot.B);
            Operand[] c = HalfSrc(context, inst, Slot.C);
            Operand[] res = new Operand[2];

            for (int lane = 0; lane < 2; lane++)
            {
                res[lane] = HalfClamp(context, inst, context.FPFusedMultiplyAdd(a[lane], b[lane], c[lane]), true);
            }

            SetHalfDest(context, inst, res);
        }

        /// <summary>The per-lane comparisons (bits 76-79) of A and the second source of HSET2 / HSETP2.</summary>
        private static Operand[] HalfCompare(EmitterContext context, Sm86Inst inst)
        {
            Operand[] a = HalfSrc(context, inst, Slot.A);
            Operand[] b = HalfSrc(context, inst, SecondSlot(inst));
            FComp cmp = (FComp)inst.Field(76, 4);

            return [InstEmit.GetFPComparison(context, cmp, a[0], b[0]), InstEmit.GetFPComparison(context, cmp, a[1], b[1])];
        }

        private static readonly uint _hsetp2Known = ModMask(72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90);

        /// <summary>
        /// HSETP2: per lane, compare A with the second source (bits 76-79), combine with the predicate in bits 87-90 by bits 69-70 (AND, OR, XOR) and
        /// write lane 0 to bits 81-83, lane 1 to 84-86. .H_AND (bit 71) ANDs the two lanes into the first output and its inverse into the second, as
        /// the Maxwell frontend does. FTZ (80) not modelled.
        /// </summary>
        public static void Hsetp2(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            bool uniform = IsUniform(inst);

            CheckModifiers(context, inst, "HSETP2", _hsetp2Known);
            CheckHalfFlags(context, inst, "HSETP2");

            Operand[] res = HalfCompare(context, inst);

            if (inst.Bit(71))
            {
                res[0] = context.BitwiseAnd(res[0], res[1]);
                res[1] = context.BitwiseNot(res[0]);
            }

            BoolOp logicOp = (BoolOp)inst.Field(69, 2);
            Operand accum = PredSrc(context, inst.Pred87, inst.Pred87Inv, uniform);

            SetPredDest(context, inst.Field(81, 3), GetPredLogicalOp(context, logicOp, res[0], accum), uniform);
            SetPredDest(context, inst.Field(84, 3), GetPredLogicalOp(context, logicOp, res[1], accum), uniform);
        }

        private static readonly uint _hset2Known = ModMask(72, 73, 74, 75, 76, 77, 78, 79, 80, 87, 88, 89, 90);

        /// <summary>
        /// HSET2 d = per lane, the comparison of A with the second source (bits 76-79) combined with the predicate in bits 87-90 by bits 69-70: 1.0 or
        /// 0.0 with .BF (bit 71), else an all-ones or zero half. Not in MKW.
        /// </summary>
        public static void Hset2(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            CheckModifiers(context, inst, "HSET2", _hset2Known);
            CheckHalfFlags(context, inst, "HSET2");

            Operand[] res = HalfCompare(context, inst);
            BoolOp logicOp = (BoolOp)inst.Field(69, 2);
            Operand accum = PredSrc(context, inst.Pred87, inst.Pred87Inv, IsUniform(inst));
            Operand value;

            for (int lane = 0; lane < 2; lane++)
            {
                res[lane] = GetPredLogicalOp(context, logicOp, res[lane], accum);
            }

            if (inst.Bit(71))
            {
                value = context.PackHalf2x16(
                    context.ConditionalSelect(res[0], ConstF(1), ConstF(0)),
                    context.ConditionalSelect(res[1], ConstF(1), ConstF(0)));
            }
            else
            {
                value = context.BitwiseOr(
                    context.ConditionalSelect(res[0], Const(0xffff), Const(0)),
                    context.ConditionalSelect(res[1], Const(unchecked((int)0xffff0000)), Const(0)));
            }

            SetDest(context, inst, value);
        }

        /// <summary>HSET2 / HSETP2 keep flags in bits 64-71 (the second source is never there); bits 64-68 (65: .BF16_V2) are not modelled.</summary>
        private static void CheckHalfFlags(EmitterContext context, Sm86Inst inst, string name)
        {
            if (inst.Field(64, 5) != 0)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 {name} at 0x{context.CurrOp.Address:X}: flag bits 0x{inst.Field(64, 5):x} (from bit 64) are not modelled.");
            }
        }

        private static readonly uint _hmnmx2Known = ModMask(72, 73, 74, 75, 80, 81, 82, 87, 88, 89, 90);

        /// <summary>
        /// HMNMX2 d = per lane the minimum (predicate bits 87-90 true) or maximum of A and the second source; a NaN operand gives the other one, or the
        /// canonical NaN 0x7fff with .NAN (bit 81, PTX min / max .NaN); .XORSIGN (bit 82) takes the magnitudes and gives the result the XOR of the
        /// operands' signs (PTX min / max .xorsign.abs).
        /// MKW: HMNMX2 d, x, RZ.H0_H0 (max with 0) and its min form. .F32 (78) and FTZ (80) are not modelled.
        /// </summary>
        public static void Hmnmx2(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            CheckModifiers(context, inst, "HMNMX2", _hmnmx2Known);

            Operand[] a = HalfSrc(context, inst, Slot.A);
            Operand[] b = HalfSrc(context, inst, SecondSlot(inst));
            Operand[] res = new Operand[2];
            Operand[] nan = new Operand[2];

            for (int lane = 0; lane < 2; lane++)
            {
                Operand x = a[lane];
                Operand y = b[lane];

                if (inst.Bit(82))
                {
                    x = context.FPAbsolute(x);
                    y = context.FPAbsolute(y);
                }

                Operand value = SelectByPred87(context, inst, context.FPMinimum(x, y), context.FPMaximum(x, y));

                if (inst.Bit(82))
                {
                    Operand signDiffers = context.ICompareLess(context.BitwiseExclusiveOr(a[lane], b[lane]), Const(0));

                    value = context.ConditionalSelect(signDiffers, context.FPNegate(value), value);
                }

                res[lane] = value;
                nan[lane] = context.BitwiseOr(context.IsNan(a[lane]), context.IsNan(b[lane]));
            }

            Operand packed = context.PackHalf2x16(res[0], res[1]);

            if (inst.Bit(81))
            {
                // The canonical NaN is written as bits: a float NaN's payload would not survive the conversion to F16.
                packed = context.ConditionalSelect(nan[0], context.BitwiseOr(context.BitwiseAnd(packed, Const(unchecked((int)0xffff0000))), Const(0x7fff)), packed);
                packed = context.ConditionalSelect(nan[1], context.BitwiseOr(context.BitwiseAnd(packed, Const(0xffff)), Const(0x7fff << 16)), packed);
            }

            SetDest(context, inst, packed);
        }
    }
}
