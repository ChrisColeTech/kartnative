using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using System;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Sm86
{
    /// <summary>
    /// Conversions of the ALU family: F2I, I2F / I2FP, F2F, FRND, F2FP. Field layouts are Mesa NAK's encoder (sm70_encode.rs: OpF2I, OpI2F, OpF2F,
    /// OpFRnd, OpF2FP); results follow the SM70+ ISA (NAK's hardware-tested folds, PTX cvt). Shared conventions:
    ///   operand sizes: destination bits 75-76, source bits 84-85, 0 = 8, 1 = 16, 2 = 32, 3 = 64 bits (64-bit operands use the 0x110-0x113 opcodes);
    ///   rounding, bits 78-79: 0 nearest even, 1 towards -inf, 2 towards +inf, 3 towards zero;
    ///   the source is the B slot; an F16 source is the half that bits 60-61 select (0 low, 1 high), an 8- or 16-bit integer source the byte or half.
    /// The IR has no 64-bit integers: 64-bit operands are register pairs (low word first) handled as two 32-bit words.
    /// </summary>
    static partial class Sm86Emit
    {
        private const int RoundNearest = 0;
        private const int RoundDown = 1;
        private const int RoundUp = 2;

        private static void LogNotImplemented(EmitterContext context, string what)
        {
            context.TranslatorContext.GpuAccessor.Log($"SM86 {what} at 0x{context.CurrOp.Address:X} is not implemented.");
        }

        /// <summary>
        /// Logs modifier bits 72-90 that the emitter does not model: <paramref name="known"/> is a mask of bits 72-90 (bit 0 = instruction bit 72).
        /// Every combination the MKW dump uses is modelled; this flags encodings met later.
        /// </summary>
        private static void CheckModifiers(EmitterContext context, Sm86Inst inst, string name, uint known)
        {
            uint unknown = (uint)inst.Bits(72, 19) & ~known;

            if (unknown != 0)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 {name} at 0x{context.CurrOp.Address:X}: modifier bits 0x{unknown:x5} (from bit 72) are not modelled.");
            }
        }

        /// <summary>A mask of instruction bits 72-90 for <see cref="CheckModifiers"/>.</summary>
        private static uint ModMask(params int[] bits)
        {
            uint mask = 0;

            foreach (int bit in bits)
            {
                mask |= 1u << (bit - 72);
            }

            return mask;
        }

        /// <summary>A register pair (GPR or UR) starting at <paramref name="index"/>; an RZ / URZ pair reads 0 in both words.</summary>
        private static (Operand Lo, Operand Hi) RegPair(int index, bool uniform)
        {
            int zero = uniform ? RegisterConsts.UniformGprsCount : RegisterConsts.RegisterZeroIndex;

            index = uniform ? index & 0x3f : index;

            return index == zero ? (Reg(index, uniform), Reg(index, uniform)) : (Reg(index, uniform), Reg(index + 1, uniform));
        }

        /// <summary>A 64-bit source: the register pair (GPR or UR) of the slot, or two constant-buffer words. 64-bit immediates are not modelled.</summary>
        private static (Operand Lo, Operand Hi) SrcPair(EmitterContext context, Sm86Inst inst, Slot slot)
        {
            bool uniform = IsUniform(inst);

            if (slot == Slot.A)
            {
                return RegPair(inst.Ra, uniform);
            }

            bool inSlot32 = (slot == Slot.B) != CInHighSlot(inst);

            if (!inSlot32)
            {
                return RegPair(inst.Rc, uniform);
            }

            switch (inst.Form)
            {
                case 1:
                    return RegPair(inst.Rb, uniform);
                case 6:
                case 7:
                    return RegPair(inst.URb, true);
                case 3:
                case 5:
                    return (SrcCbuf(context, inst), SrcCbuf(context, inst, 4));
                default:
                    LogNotImplemented(context, $"64-bit immediate operand of opcode 0x{inst.Opcode:x3}");
                    return (Src(context, inst, slot), Const(0));
            }
        }

        /// <summary>Writes a 64-bit result to the destination pair (Rd, Rd + 1, or URd, URd + 1); an RZ / URZ destination drops it.</summary>
        private static void SetDestPair(EmitterContext context, Sm86Inst inst, Operand lo, Operand hi)
        {
            bool uniform = IsUniform(inst);
            int rd = uniform ? inst.URd : inst.Rd;

            if (rd == (uniform ? RegisterConsts.UniformGprsCount : RegisterConsts.RegisterZeroIndex))
            {
                return;
            }

            context.Copy(Reg(rd, uniform), lo);
            context.Copy(Reg(rd + 1, uniform), hi);
        }

        private static Operand FpConst(EmitterContext context, double value, Instruction fpType)
        {
            return fpType == Instruction.FP64 ? context.PackDouble2x32(value) : ConstF((float)value);
        }

        /// <summary>
        /// The float source of a conversion (the B slot) with its abs (62) / neg (63) modifiers: FP32 for 16- and 32-bit sizes (an F16 source is unpacked;
        /// an immediate holds it in its low half), FP64 for size 3.
        /// </summary>
        private static Operand ConvFloatSrc(EmitterContext context, Sm86Inst inst, int size, out Instruction fpType)
        {
            fpType = size == 3 ? Instruction.FP64 : Instruction.FP32;

            Operand value;

            if (size == 3)
            {
                (Operand lo, Operand hi) = SrcPair(context, inst, Slot.B);
                value = context.PackDouble2x32(lo, hi);
            }
            else
            {
                value = Src(context, inst, Slot.B);

                if (size == 1)
                {
                    value = inst.Form != 4 && inst.Field(60, 2) == 1 ? context.UnpackHalf2x16High(value) : context.UnpackHalf2x16Low(value);
                }
            }

            return context.FPAbsNeg(value, Abs(inst, Slot.B), Neg(inst, Slot.B), fpType);
        }

        /// <summary>Writes a float result of type <paramref name="fpType"/> as an F16 (low half, high half 0), F32 or F64 (register pair) destination.</summary>
        private static void SetFloatDest(EmitterContext context, Sm86Inst inst, Operand value, Instruction fpType, int dstSize)
        {
            switch (dstSize)
            {
                case 1:
                    Operand f32 = fpType == Instruction.FP64 ? context.FP64ConvertToFP32(value) : value;
                    SetDest(context, inst, context.PackHalf2x16(f32, ConstF(0)));
                    break;
                case 2:
                    SetDest(context, inst, fpType == Instruction.FP64 ? context.FP64ConvertToFP32(value) : value);
                    break;
                case 3:
                    Operand f64 = fpType == Instruction.FP64 ? value : context.FP32ConvertToFP64(value);
                    SetDestPair(context, inst, context.UnpackDouble2x32Low(f64), context.UnpackDouble2x32High(f64));
                    break;
                default:
                    LogNotImplemented(context, "8-bit float result");
                    break;
            }
        }

        /// <summary>Rounds to an integral value by the bits 78-79 mode.</summary>
        private static Operand RoundToIntegral(EmitterContext context, Operand value, int mode, Instruction fpType)
        {
            return mode switch
            {
                RoundNearest => context.FPRound(value, fpType),
                RoundDown => context.FPFloor(value, fpType),
                RoundUp => context.FPCeiling(value, fpType),
                _ => context.FPTruncate(value, fpType),
            };
        }

        private static readonly uint _f2iKnown = ModMask(72, 75, 76, 77, 78, 79, 80, 84, 85);

        /// <summary>
        /// F2I d = the B float converted to an integer: signed with bit 72, rounding bits 78-79, sizes 75-76 / 84-85. Out-of-range values saturate to the
        /// destination range; NaN gives 0 with .NTZ (bit 77), which every MKW F2I carries (without it the hardware takes the NaN result, 0 or
        /// 0x80000000, from a per-dispatch setting; 0 is used). An 8- or 16-bit result is sign- or zero-extended to 32 bits, a 64-bit one is a pair.
        /// FTZ (bit 80) only changes a directed rounding of a denormal and is not modelled.
        /// </summary>
        public static void F2i(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int dstSize = inst.Field(75, 2);
            int srcSize = inst.Field(84, 2);
            bool signed = inst.Bit(72);

            CheckModifiers(context, inst, "F2I", _f2iKnown);

            if (srcSize == 0)
            {
                LogNotImplemented(context, "F2I from an 8-bit float");
                return;
            }

            Operand value = ConvFloatSrc(context, inst, srcSize, out Instruction fpType);
            Operand rounded = RoundToIntegral(context, value, inst.Field(78, 2), fpType);
            Operand nan = context.IsNan(value, fpType);

            if (dstSize == 3)
            {
                (Operand lo, Operand hi) = FloatToInt64(context, rounded, signed, fpType);

                SetDestPair(context, inst, context.ConditionalSelect(nan, Const(0), lo), context.ConditionalSelect(nan, Const(0), hi));
                return;
            }

            int bits = 8 << dstSize;
            double min = signed ? -Math.Pow(2, bits - 1) : 0;
            double limit = signed ? Math.Pow(2, bits - 1) : Math.Pow(2, bits);

            // 2^31 - 1 and 2^32 - 1 are not FP32 values: clamp below the limit to the largest one, then saturate what reached the limit.
            bool fp32Wide = bits == 32 && fpType == Instruction.FP32;
            double maxBelow = fp32Wide ? (signed ? 2147483520.0 : 4294967040.0) : limit - 1;

            Operand clamped = context.FPMinimum(context.FPMaximum(rounded, FpConst(context, min, fpType), fpType), FpConst(context, maxBelow, fpType), fpType);
            Operand res = fpType == Instruction.FP64
                ? (signed ? context.FP64ConvertToS32(clamped) : context.FP64ConvertToU32(clamped))
                : (signed ? context.FP32ConvertToS32(clamped) : context.FP32ConvertToU32(clamped));

            if (fp32Wide)
            {
                Operand over = context.BitwiseNot(context.FPCompareLess(rounded, ConstF((float)limit)));
                res = context.ConditionalSelect(over, Const(signed ? int.MaxValue : -1), res);
            }

            SetDest(context, inst, context.ConditionalSelect(nan, Const(0), res));
        }

        /// <summary>
        /// A saturating conversion of an integral float to a 64-bit integer pair (NaN not handled): the magnitude splits exactly into its high and low words
        /// (the scale is a power of two and the remainder has no more significant bits than the value), then a negative value is negated as 64 bits.
        /// </summary>
        private static (Operand Lo, Operand Hi) FloatToInt64(EmitterContext context, Operand value, bool signed, Instruction fpType)
        {
            bool fp64 = fpType == Instruction.FP64;
            double limit = signed ? Math.Pow(2, 63) : Math.Pow(2, 64);
            double maxBelow = limit - Math.Pow(2, (signed ? 63 : 64) - (fp64 ? 53 : 24));

            Operand clamped = context.FPMinimum(context.FPMaximum(value, FpConst(context, signed ? -limit : 0, fpType), fpType), FpConst(context, maxBelow, fpType), fpType);
            Operand magnitude = context.FPAbsolute(clamped, fpType);
            Operand hiF = context.FPFloor(context.FPMultiply(magnitude, FpConst(context, Math.Pow(2, -32), fpType), fpType), fpType);
            Operand loF = context.FPFusedMultiplyAdd(hiF, FpConst(context, -Math.Pow(2, 32), fpType), magnitude, fpType);

            Operand hi = fp64 ? context.FP64ConvertToU32(hiF) : context.FP32ConvertToU32(hiF);
            Operand lo = fp64 ? context.FP64ConvertToU32(loF) : context.FP32ConvertToU32(loF);

            if (signed)
            {
                // -(hi:lo) = (~hi + (lo == 0)) : -lo.
                Operand negative = context.FPCompareLess(clamped, FpConst(context, 0, fpType), fpType);
                Operand negHi = context.IAdd(context.BitwiseNot(hi), context.ConditionalSelect(context.ICompareEqual(lo, Const(0)), Const(1), Const(0)));

                hi = context.ConditionalSelect(negative, negHi, hi);
                lo = context.ConditionalSelect(negative, context.INegate(lo), lo);
            }

            Operand over = context.BitwiseNot(context.FPCompareLess(value, FpConst(context, limit, fpType), fpType));

            return (context.ConditionalSelect(over, Const(-1), lo), context.ConditionalSelect(over, Const(signed ? int.MaxValue : -1), hi));
        }

        private static readonly uint _i2fKnown = ModMask(74, 75, 76, 78, 79, 84, 85);

        /// <summary>
        /// I2F d = the B integer converted to a float: signed with bit 74, rounding bits 78-79, sizes 75-76 / 84-85; an 8- or 16-bit source is the byte or
        /// half that bits 60-61 select, in units of its size (an immediate source is its low part). I2FP (0x045) is the same conversion on the FP32 pipe
        /// (32-bit integer to F32; MKW: .S32 / .U32 and .U32.RZ). Conversions to F32 round exactly in every mode; an F16 result rounds to nearest even.
        /// </summary>
        public static void I2f(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int dstSize = inst.Field(75, 2);
            int srcSize = inst.Field(84, 2);
            int mode = inst.Field(78, 2);
            bool signed = inst.Bit(74);

            CheckModifiers(context, inst, "I2F", _i2fKnown);

            if (dstSize == 0)
            {
                LogNotImplemented(context, "I2F to an 8-bit float");
                return;
            }

            Operand lo;
            Operand hi = null;

            if (srcSize == 3)
            {
                (lo, hi) = SrcPair(context, inst, Slot.B);
            }
            else
            {
                lo = Src(context, inst, Slot.B);

                if (srcSize < 2)
                {
                    int width = 8 << srcSize;
                    int position = inst.Form == 4 ? 0 : inst.Field(60, 2) * width & 31;

                    lo = signed
                        ? context.BitfieldExtractS32(lo, Const(position), Const(width))
                        : context.BitfieldExtractU32(lo, Const(position), Const(width));
                }
            }

            if (dstSize == 3)
            {
                if (srcSize == 3)
                {
                    LogNotImplemented(context, "I2F from a 64-bit integer to F64");
                    return;
                }

                Operand f64 = signed ? context.IConvertS32ToFP64(lo) : context.IConvertU32ToFP64(lo);

                SetDestPair(context, inst, context.UnpackDouble2x32Low(f64), context.UnpackDouble2x32High(f64));
                return;
            }

            Operand res;

            if (srcSize == 3)
            {
                res = Int64ToF32(context, lo, hi, signed, mode);
            }
            else if (mode == RoundNearest || srcSize < 2)
            {
                // Exact below 2^24 (every 8- and 16-bit value), otherwise rounded to nearest even as the mode asks.
                res = signed ? context.IConvertS32ToFP32(lo) : context.IConvertU32ToFP32(lo);
            }
            else
            {
                res = Int32ToF32Directed(context, lo, signed, mode);
            }

            if (dstSize == 1)
            {
                if (mode != RoundNearest && srcSize > 0)
                {
                    LogNotImplemented(context, "I2F to F16 with a directed rounding (rounded to nearest)");
                }

                res = context.PackHalf2x16(res, ConstF(0));
            }

            SetDest(context, inst, res);
        }

        /// <summary>Whether a value of sign <paramref name="negative"/> rounds away from zero in a directed mode (down for negatives, up for positives).</summary>
        private static Operand RoundsAwayFromZero(EmitterContext context, Operand negative, int mode)
        {
            return mode switch
            {
                RoundUp => context.BitwiseNot(negative),
                RoundDown => negative,
                _ => Const(IrConsts.False),
            };
        }

        /// <summary>
        /// A 32-bit integer to F32 with a directed rounding: the magnitude truncated to its 24 most significant bits converts exactly, then one unit in
        /// its last place is added when the mode rounds away from zero and bits were dropped (the sum is exact: at most a power of two).
        /// </summary>
        private static Operand Int32ToF32Directed(EmitterContext context, Operand value, bool signed, int mode)
        {
            Operand negative = signed ? context.ICompareLess(value, Const(0)) : Const(IrConsts.False);
            Operand magnitude = signed ? context.IAbsolute(value) : value;

            Operand shift = context.IMaximumS32(context.ISubtract(context.FindMSBU32(magnitude), Const(23)), Const(0));
            Operand dropMask = context.ISubtract(context.ShiftLeft(Const(1), shift), Const(1));
            Operand truncated = context.BitwiseAnd(magnitude, context.BitwiseNot(dropMask));

            Operand res = context.IConvertU32ToFP32(truncated);
            Operand dropped = context.ICompareNotEqual(context.BitwiseAnd(magnitude, dropMask), Const(0));
            Operand up = context.BitwiseAnd(dropped, RoundsAwayFromZero(context, negative, mode));

            res = context.FPAdd(res, context.ConditionalSelect(up, context.IConvertU32ToFP32(context.ShiftLeft(Const(1), shift)), ConstF(0)));

            return signed ? context.ConditionalSelect(negative, context.FPNegate(res), res) : res;
        }

        /// <summary>
        /// A 64-bit integer pair to F32, rounded exactly in every mode: the magnitude's 24 most significant bits (shifted down by up to 40) convert
        /// exactly, the dropped bits decide the rounding (against the half unit for nearest even), and the result scales back by a power of two.
        /// </summary>
        private static Operand Int64ToF32(EmitterContext context, Operand lo, Operand hi, bool signed, int mode)
        {
            Operand zero = Const(0);
            Operand negative = signed ? context.ICompareLess(hi, zero) : Const(IrConsts.False);
            Operand mlo = lo;
            Operand mhi = hi;

            if (signed)
            {
                Operand negHi = context.IAdd(context.BitwiseNot(hi), context.ConditionalSelect(context.ICompareEqual(lo, zero), Const(1), zero));

                mlo = context.ConditionalSelect(negative, context.INegate(lo), lo);
                mhi = context.ConditionalSelect(negative, negHi, hi);
            }

            Operand hiNonZero = context.ICompareNotEqual(mhi, zero);
            Operand msb = context.ConditionalSelect(hiNonZero, context.IAdd(context.FindMSBU32(mhi), Const(32)), context.FindMSBU32(mlo));
            Operand shift = context.IMaximumS32(context.ISubtract(msb, Const(23)), zero); // 0 to 40
            Operand below32 = context.ICompareLess(shift, Const(32));
            Operand s32 = context.BitwiseAnd(shift, Const(31));

            // kept = (mhi:mlo) >> shift; both shifted words stay in 0-31 bit shifts.
            Operand carryIn = context.ConditionalSelect(context.ICompareEqual(s32, zero), zero, context.ShiftLeft(mhi, context.ISubtract(Const(32), s32)));
            Operand keptLow = context.BitwiseOr(context.ShiftRightU32(mlo, s32), carryIn);
            Operand kept = context.ConditionalSelect(below32, keptLow, context.ShiftRightU32(mhi, s32));

            // The dropped bits (rest) and the half unit (half), as word pairs.
            Operand lowMask = context.ISubtract(context.ShiftLeft(Const(1), s32), Const(1));
            Operand restLo = context.ConditionalSelect(below32, context.BitwiseAnd(mlo, lowMask), mlo);
            Operand restHi = context.ConditionalSelect(below32, zero, context.BitwiseAnd(mhi, lowMask));

            Operand res;

            if (mode == RoundNearest)
            {
                Operand halfBit = context.ShiftLeft(Const(1), context.BitwiseAnd(context.ISubtract(shift, Const(1)), Const(31)));
                Operand halfInLow = context.BitwiseAnd(context.ICompareGreater(shift, zero), context.ICompareLessOrEqual(shift, Const(32)));
                Operand halfLo = context.ConditionalSelect(halfInLow, halfBit, zero);
                Operand halfHi = context.ConditionalSelect(context.ICompareGreater(shift, Const(32)), halfBit, zero);

                Operand hiEqual = context.ICompareEqual(restHi, halfHi);
                Operand greater = context.BitwiseOr(
                    context.ICompareGreaterUnsigned(restHi, halfHi),
                    context.BitwiseAnd(hiEqual, context.ICompareGreaterUnsigned(restLo, halfLo)));
                Operand equal = context.BitwiseAnd(context.BitwiseAnd(hiEqual, context.ICompareEqual(restLo, halfLo)), context.ICompareGreater(shift, zero));
                Operand odd = context.ICompareNotEqual(context.BitwiseAnd(kept, Const(1)), zero);

                res = context.ConditionalSelect(context.BitwiseOr(greater, context.BitwiseAnd(equal, odd)), Const(1), zero);
            }
            else
            {
                Operand dropped = context.ICompareNotEqual(context.BitwiseOr(restLo, restHi), zero);

                res = context.ConditionalSelect(context.BitwiseAnd(dropped, RoundsAwayFromZero(context, negative, mode)), Const(1), zero);
            }

            // kept + 1 is at most 2^24, exact in F32; the scale 2^shift is built from its exponent bits.
            Operand f = context.IConvertU32ToFP32(context.IAdd(kept, res));
            Operand scale = context.ShiftLeft(context.IAdd(shift, Const(127)), Const(23));

            f = context.FPMultiply(f, scale);

            return signed ? context.ConditionalSelect(negative, context.FPNegate(f), f) : f;
        }

        private static readonly uint _frndKnown = ModMask(75, 76, 78, 79, 80, 84, 85);

        /// <summary>FRND d = B rounded to an integral value by bits 78-79 (F16, F32 or F64 by bits 84-85, the result by 75-76), abs / neg 62 / 63.</summary>
        public static void Frnd(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int srcSize = inst.Field(84, 2);

            CheckModifiers(context, inst, "FRND", _frndKnown);

            if (srcSize == 0)
            {
                LogNotImplemented(context, "FRND of an 8-bit float");
                return;
            }

            Operand value = ConvFloatSrc(context, inst, srcSize, out Instruction fpType);

            SetFloatDest(context, inst, RoundToIntegral(context, value, inst.Field(78, 2), fpType), fpType, inst.Field(75, 2));
        }

        private static readonly uint _f2fKnown = ModMask(75, 76, 78, 79, 80, 84, 85);

        /// <summary>
        /// F2F d = B converted between F16, F32 and F64 (sizes 84-85 to 75-76), abs / neg 62 / 63. A narrowing conversion rounds by bits 78-79; only
        /// nearest even is modelled (MKW has no F2F).
        /// </summary>
        public static void F2f(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int dstSize = inst.Field(75, 2);
            int srcSize = inst.Field(84, 2);

            CheckModifiers(context, inst, "F2F", _f2fKnown);

            if (srcSize == 0)
            {
                LogNotImplemented(context, "F2F from an 8-bit float");
                return;
            }

            if (dstSize < srcSize && inst.Field(78, 2) != RoundNearest)
            {
                LogNotImplemented(context, "F2F narrowing with a directed rounding (rounded to nearest)");
            }

            Operand value = ConvFloatSrc(context, inst, srcSize, out Instruction fpType);

            SetFloatDest(context, inst, value, fpType, dstSize);
        }

        private static readonly uint _f2fpKnown = ModMask(72, 78, 79, 80);

        /// <summary>
        /// F2FP.PACK_AB d = F16(A) in the high half, F16(B) in the low half; .MERGE_C (bit 78): the high half is C's half that bit 72 selects. Rounding
        /// bits 79-80; only nearest even is modelled (all MKW F2FPs).
        /// </summary>
        public static void F2fp(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            CheckModifiers(context, inst, "F2FP", _f2fpKnown);

            if (inst.Field(79, 2) != RoundNearest)
            {
                LogNotImplemented(context, "F2FP with a directed rounding (rounded to nearest)");
            }

            Operand low = Src(context, inst, Slot.B);
            Operand res;

            if (inst.Bit(78))
            {
                Operand c = Src(context, inst, Slot.C);
                Operand high = inst.Bit(72) ? context.ShiftRightU32(c, Const(16)) : context.BitwiseAnd(c, Const(0xffff));

                res = context.BitwiseOr(context.PackHalf2x16(low, ConstF(0)), context.ShiftLeft(high, Const(16)));
            }
            else
            {
                res = context.PackHalf2x16(low, Src(context, inst, Slot.A));
            }

            SetDest(context, inst, res);
        }
    }
}
