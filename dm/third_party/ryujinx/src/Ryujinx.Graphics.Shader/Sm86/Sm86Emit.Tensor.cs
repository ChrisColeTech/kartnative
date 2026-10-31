using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Sm86
{
    /// <summary>
    /// Tensor-core operations (HMMA, LDSM, MOVM), used by MKW's NDSR upscaler (Lib/agl/agl_ndsr.pck, 30 compute programs; "Ndsr" is a pass of agl's render
    /// pipeline). A warp holds each matrix as fragments spread over its 32 lanes (the layouts of PTX mma.m16n8k16 / ldmatrix / movmatrix, which the SASS
    /// instructions implement); the host has no matrix unit that SPIR-V reaches, so each operation is rebuilt from subgroup shuffles and scalar arithmetic.
    /// Correct but slow: one HMMA.16816 is 32 shuffles and 64 FMAs. Lane l is (group g = l / 4, thread t = l % 4).
    /// </summary>
    static partial class Sm86Emit
    {
        private static Operand LaneId(EmitterContext context) => context.Load(StorageKind.Input, IoVariable.SubgroupLaneId);

        /// <summary>16-bit half <paramref name="half"/> (an operand, 0 or 1) of a 32-bit value, as the low 16 bits.</summary>
        private static Operand Half16(EmitterContext context, Operand value, Operand half)
        {
            return context.BitwiseAnd(context.ShiftRightU32(value, context.ShiftLeft(half, Const(4))), Const(0xffff));
        }

        /// <summary>
        /// HMMA.{1688, 16816}.{F16, F32} Rd, Ra, Rb, Rc (Mesa NAK OpHmma): D = A * B + C with f16 A (16 x K) and B (K x 8). Size bits 75 | 78 &lt;&lt; 1
        /// (0 m16n8k8, 1 m16n8k16; 2, m16n8k4, has no documented f16 fragment layout and is not used by MKW), bit 76 an f32 D and C, bits 82-83 the
        /// source type (0 f16). Fragments (PTX mma.m16n8k16 / m16n8k8): A register 2kb + h holds row g + 8h, columns 8kb + 2t and + 1; B register kb
        /// holds rows 8kb + 2t and + 1 of column g; D / C (f16) register h holds row g + 8h, columns 2t and + 1 (f32: registers 2h and 2h + 1). Products
        /// are summed in f32 and rounded to f16 once (MKW: HMMA.16816.F16 with C = RZ only).
        /// </summary>
        public static void Hmma(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int size = (inst.Bit(75) ? 1 : 0) | (inst.Bit(78) ? 2 : 0);
            bool f32 = inst.Bit(76);

            if (size > 1 || inst.Field(82, 2) != 0)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 HMMA with size {size}, source type {inst.Field(82, 2)} at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            Operand lane = LaneId(context);
            Operand g = context.ShiftRightU32(lane, Const(2));
            Operand t = context.BitwiseAnd(lane, Const(3));

            int kBlocks = size == 1 ? 2 : 1;

            // A[h][kb][t'] = (row g + 8h, columns 8kb + 2t', + 1), from lane 4g + t'.
            var a = new Operand[2, 2, 4];
            // B[j][kb][t'] = (rows 8kb + 2t', + 1, column 2t + j), from lane 4(2t + j) + t'.
            var b = new Operand[2, 2, 4];

            for (int tp = 0; tp < 4; tp++)
            {
                Operand aLane = context.IAdd(context.ShiftLeft(g, Const(2)), Const(tp));

                for (int kb = 0; kb < kBlocks; kb++)
                {
                    for (int h = 0; h < 2; h++)
                    {
                        a[h, kb, tp] = context.Shuffle(Gpr(inst.Ra + 2 * kb + h), aLane);
                    }

                    for (int j = 0; j < 2; j++)
                    {
                        Operand bLane = context.IAdd(context.ShiftLeft(context.IAdd(context.ShiftLeft(t, Const(1)), Const(j)), Const(2)), Const(tp));

                        b[j, kb, tp] = context.Shuffle(Gpr(inst.Rb + kb), bLane);
                    }
                }
            }

            var d = new Operand[2, 2];

            for (int h = 0; h < 2; h++)
            {
                for (int j = 0; j < 2; j++)
                {
                    Operand acc = HmmaC(context, inst, f32, h, j);

                    for (int kb = 0; kb < kBlocks; kb++)
                    {
                        for (int tp = 0; tp < 4; tp++)
                        {
                            Operand av = a[h, kb, tp];
                            Operand bv = b[j, kb, tp];

                            acc = context.FPFusedMultiplyAdd(context.UnpackHalf2x16Low(av), context.UnpackHalf2x16Low(bv), acc);
                            acc = context.FPFusedMultiplyAdd(context.UnpackHalf2x16High(av), context.UnpackHalf2x16High(bv), acc);
                        }
                    }

                    d[h, j] = acc;
                }
            }

            WriteHmmaD(context, inst, f32, d);
        }

        /// <summary>Element (row g + 8h, column 2t + j) of C as an f32 value (0 when Rc is RZ).</summary>
        private static Operand HmmaC(EmitterContext context, Sm86Inst inst, bool f32, int h, int j)
        {
            if (inst.Rc == RegisterConsts.RegisterZeroIndex)
            {
                return ConstF(0f);
            }

            if (f32)
            {
                return Gpr(inst.Rc + 2 * h + j);
            }

            Operand c = Gpr(inst.Rc + h);

            return j == 0 ? context.UnpackHalf2x16Low(c) : context.UnpackHalf2x16High(c);
        }

        private static void WriteHmmaD(EmitterContext context, Sm86Inst inst, bool f32, Operand[,] d)
        {
            if (inst.Rd == RegisterConsts.RegisterZeroIndex)
            {
                return;
            }

            for (int h = 0; h < 2; h++)
            {
                if (f32)
                {
                    context.Copy(Gpr(inst.Rd + 2 * h), d[h, 0]);
                    context.Copy(Gpr(inst.Rd + 2 * h + 1), d[h, 1]);
                }
                else
                {
                    context.Copy(Gpr(inst.Rd + h), context.PackHalf2x16(d[h, 0], d[h, 1]));
                }
            }
        }

        /// <summary>
        /// MOVM.16.MT88 Rd, Ra (Mesa NAK OpMovm): transposes an 8x8 matrix of 16-bit elements; lane l holds row l / 4, columns 2(l % 4) and + 1. The result's
        /// element (l / 4, 2(l % 4) + s) is the source's (2(l % 4) + s, l / 4): half (l / 4) % 2 of lane 8(l % 4) + 4s + l / 8. Bits 78-79 other than 0
        /// (M832, M864) are not used by MKW.
        /// </summary>
        public static void Movm(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            if (inst.Field(78, 2) != 0)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 MOVM with shape {inst.Field(78, 2)} at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            Operand lane = LaneId(context);
            Operand t = context.BitwiseAnd(lane, Const(3));
            Operand baseLane = context.IAdd(context.ShiftLeft(t, Const(3)), context.ShiftRightU32(lane, Const(3)));
            Operand half = context.BitwiseAnd(context.ShiftRightU32(lane, Const(2)), Const(1));

            Operand lo = Half16(context, context.Shuffle(Gpr(inst.Ra), baseLane), half);
            Operand hi = Half16(context, context.Shuffle(Gpr(inst.Ra), context.IAdd(baseLane, Const(4))), half);

            context.Copy(Gpr(inst.Rd), context.BitwiseOr(lo, context.ShiftLeft(hi, Const(16))));
        }

        /// <summary>
        /// LDSM.16.{M88, MT88}.{1, 2, 4} Rd, [Ra + URb + offset] (Mesa NAK OpLdsm): loads 1, 2 or 4 (1 &lt;&lt; bits 72-73) 8x8 matrices of 16-bit elements from
        /// shared memory into Rd, Rd + 1...; lanes 8m..8m+7 give the addresses of the rows of matrix m. Address: Ra, plus the uniform register in bits
        /// 32-37 when bit 91 is set, plus the signed offset in bits 40-63. M88 (bits 78-79 = 0): lane l receives the 32 bits at row l / 4, byte 4(l % 4);
        /// MT88 (1), the transpose: elements (row 2(l % 4) + s, column l / 4).
        /// </summary>
        public static void Ldsm(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int countLog2 = inst.Field(72, 2);
            int shape = inst.Field(78, 2);

            if (countLog2 > 2 || shape > 1)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 LDSM with count field {countLog2}, shape {shape} at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            int id = context.ResourceManager.SharedMemoryId;
            Operand lane = LaneId(context);
            Operand t = context.BitwiseAnd(lane, Const(3));
            Operand row = context.ShiftRightU32(lane, Const(2));
            Operand offset = Const((int)inst.SignedBits(40, 24));

            if (inst.Bit(91))
            {
                offset = context.IAdd(offset, UGpr(inst.URb));
            }

            Operand RowAddress(int matrix, Operand rowIndex)
            {
                return context.IAdd(context.Shuffle(Gpr(inst.Ra), context.IAdd(rowIndex, Const(matrix * 8))), offset);
            }

            Operand LoadWord(Operand byteAddress)
            {
                return context.Load(StorageKind.SharedMemory, id, context.ShiftRightU32(byteAddress, Const(2)));
            }

            var values = new Operand[1 << countLog2];

            for (int m = 0; m < values.Length; m++)
            {
                if (shape == 0)
                {
                    values[m] = LoadWord(context.IAdd(RowAddress(m, row), context.ShiftLeft(t, Const(2))));
                }
                else
                {
                    Operand column = context.ShiftLeft(row, Const(1));
                    Operand lo = context.IAdd(RowAddress(m, context.ShiftLeft(t, Const(1))), column);
                    Operand hi = context.IAdd(RowAddress(m, context.IAdd(context.ShiftLeft(t, Const(1)), Const(1))), column);

                    Operand loValue = Half16(context, LoadWord(lo), context.BitwiseAnd(context.ShiftRightU32(lo, Const(1)), Const(1)));
                    Operand hiValue = Half16(context, LoadWord(hi), context.BitwiseAnd(context.ShiftRightU32(hi, Const(1)), Const(1)));

                    values[m] = context.BitwiseOr(loValue, context.ShiftLeft(hiValue, Const(16)));
                }
            }

            for (int m = 0; m < values.Length && inst.Rd + m < RegisterConsts.RegisterZeroIndex; m++)
            {
                context.Copy(Gpr(inst.Rd + m), values[m]);
            }
        }
    }
}
