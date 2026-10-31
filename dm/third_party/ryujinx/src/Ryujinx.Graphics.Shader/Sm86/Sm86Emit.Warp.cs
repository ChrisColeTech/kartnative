using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.Instructions;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Sm86
{
    /// <summary>
    /// Warp-wide operations and the predicate-register moves. A translation runs on a 32-wide SIMD group (the host subgroup), so warp operations map to
    /// subgroup operations over the active invocations, as the Maxwell emitters (InstEmitWarp) do.
    /// </summary>
    static partial class Sm86Emit
    {
        /// <summary>The number of P (and UP) registers a P2R / R2P / UP2UR moves: P0-P6 (bit 7 would be PT).</summary>
        private const int PredRegisterCount = 7;

        /// <summary>
        /// R2UR URd, Ra: a GPR into a uniform register. The compiler only moves warp-uniform values this way, so in a translation, where each
        /// invocation runs on its own, the invocation's own value is the uniform value. Bits 81-83 (a predicate output) are PT in the compiler's code.
        /// </summary>
        public static void R2ur(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            context.Copy(UGpr(inst.URd), Gpr(inst.Ra));
        }

        /// <summary>
        /// P2R Rd, PR, Ra, mask (and UP2UR URd, UPR, URa, mask): Rd = (Ra &amp; ~(mask &lt;&lt; s)) | (P0-P6 packed &amp; mask) &lt;&lt; s, with s = 8 * the byte
        /// selector in bits 76-77 (MKW: always 0). The mask is the B slot (an immediate in every MKW instance).
        /// </summary>
        public static void P2r(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            bool uniform = IsUniform(inst);
            int shift = inst.Field(76, 2) * 8;

            Operand packed = Const(0);

            for (int i = 0; i < PredRegisterCount; i++)
            {
                Operand pred = uniform ? UPred(i) : Pred(i);
                packed = context.BitwiseOr(packed, context.ConditionalSelect(pred, Const(1 << (i + shift)), Const(0)));
            }

            Operand mask = context.ShiftLeft(context.BitwiseAnd(Src(context, inst, Slot.B), Const(0xff)), Const(shift));
            Operand kept = context.BitwiseAnd(Src(context, inst, Slot.A), context.BitwiseNot(mask));

            SetDest(context, inst, context.BitwiseOr(kept, context.BitwiseAnd(packed, mask)));
        }

        /// <summary>
        /// R2P PR, Ra, mask: for each Pi (i &lt; 7) whose mask bit is set, Pi = bit (i + 8 * byte selector) of Ra; the byte selector is bits 76-77
        /// (MKW uses bytes 0, 2 and 3). The mask is the B slot.
        /// </summary>
        public static void R2p(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            bool uniform = IsUniform(inst);
            int shift = inst.Field(76, 2) * 8;

            Operand value = Src(context, inst, Slot.A);
            Operand mask = Src(context, inst, Slot.B);

            for (int i = 0; i < PredRegisterCount; i++)
            {
                Operand pred = uniform ? UPred(i) : Pred(i);
                Operand bit = context.ICompareNotEqual(context.BitwiseAnd(value, Const(1 << (i + shift))), Const(0));
                Operand selected = context.ICompareNotEqual(context.BitwiseAnd(mask, Const(1 << i)), Const(0));

                context.Copy(pred, context.ConditionalSelect(selected, bit, pred));
            }
        }

        /// <summary>
        /// PLOP3.LUT Pu, Pv, Pa, Pb, Pc, lut0, lut1 (Mesa NAK OpPLop3): sources Pa bits 87-89 (inverted by 90), Pb 77-79 (80), Pc 68-70 (71; bit 67
        /// makes it a UP register in the warp form); Pu (81-83) = lut0 (bits 64-66 | 72-76 &lt;&lt; 3) and Pv (84-86) = lut1 (bits 16-23) of the three,
        /// with LOP3's convention (a = 0xf0, b = 0xcc, c = 0xaa). UPLOP3 reads and writes UP registers only.
        /// </summary>
        public static void Plop3(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            bool uniform = IsUniform(inst);

            Operand a = PredSrc(context, inst.Field(87, 3), inst.Bit(90), uniform);
            Operand b = PredSrc(context, inst.Field(77, 3), inst.Bit(80), uniform);
            Operand c = PredSrc(context, inst.Field(68, 3), inst.Bit(71), uniform || inst.Bit(67));

            int lut0 = inst.Field(64, 3) | (inst.Field(72, 5) << 3);
            int lut1 = inst.Field(16, 8);

            // Both results are computed before either destination is written: a destination may also be a source.
            Operand res0 = PredLut(context, a, b, c, lut0);
            Operand res1 = PredLut(context, a, b, c, lut1);

            SetPredDest(context, inst.Field(81, 3), res0, uniform);
            SetPredDest(context, inst.Field(84, 3), res1, uniform);
        }

        /// <summary>A three-input truth table over predicates (LOP3's convention: a = 0xf0, b = 0xcc, c = 0xaa).</summary>
        private static Operand PredLut(EmitterContext context, Operand a, Operand b, Operand c, int lut)
        {
            return lut switch
            {
                0x00 => Const(IrConsts.False),
                0xff => Const(IrConsts.True),
                _ => Lop3Expression.GetFromTruthTable(context, a, b, c, lut),
            };
        }

        /// <summary>
        /// VOTE.{ALL, ANY, EQ} Rd, Pu, Pp (and VOTEU with a UR / UP destination, Mesa NAK OpVote): mode bits 72-73, Pu bits 81-83, Pp 87-89
        /// (inverted by 90); Rd = the ballot of Pp over the active invocations. MKW mostly uses VOTE.ALL Rd, PT, PT (the active mask).
        /// </summary>
        public static void Vote(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            bool uniform = IsUniform(inst);

            Operand pred = PredSrc(context, inst.Pred87, inst.Pred87Inv, false);
            int predDest = inst.Field(81, 3);
            int mode = inst.Field(72, 2);

            if (predDest != RegisterConsts.PredicateTrueIndex)
            {
                if (mode > (int)VoteMode.Eq)
                {
                    context.TranslatorContext.GpuAccessor.Log($"SM86 VOTE with mode {mode} at 0x{context.CurrOp.Address:X} is not implemented.");
                }
                else
                {
                    SetPredDest(context, predDest, InstEmit.EmitVote(context, (VoteMode)mode, pred), uniform);
                }
            }

            if ((uniform ? inst.URd : inst.Rd) != (uniform ? RegisterConsts.UniformRegisterZeroIndex : RegisterConsts.RegisterZeroIndex))
            {
                SetDest(context, inst, InstEmit.EmitBallot(context, pred));
            }
        }

        /// <summary>
        /// SHFL.{IDX, UP, DOWN, BFLY} Pd, Rd, Ra, lane, c (Mesa NAK OpShfl): mode bits 58-59, Pd 81-83 (lane in range). The lane is the register in
        /// bits 32-39 (0x389, 0x589) or the immediate in 53-57 (0x989, 0xf89); c, Maxwell's clamp / segment mask, is the register in 64-71 (0x389,
        /// 0x989) or the immediate in 40-52 (0x589, 0xf89). Ryujinx's shuffle helpers (ShufflePass) take the same c.
        /// </summary>
        public static void Shfl(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            bool laneImm = inst.Bit(11);
            bool cImm = inst.Bit(10);

            Operand value = Gpr(inst.Ra);
            Operand lane = laneImm ? Const(inst.Field(53, 5)) : Gpr(inst.Rb);
            Operand c = cImm ? Const(inst.Field(40, 13)) : Gpr(inst.Rc);

            (Operand res, Operand valid) = inst.Field(58, 2) switch
            {
                0 => context.Shuffle(value, lane, c),
                1 => context.ShuffleUp(value, lane, c),
                2 => context.ShuffleDown(value, lane, c),
                _ => context.ShuffleXor(value, lane, c),
            };

            context.Copy(Gpr(inst.Rd), res);
            SetPredDest(context, inst.Field(81, 3), valid, false);
        }

        /// <summary>
        /// MATCH.{ALL, ANY}[.U64] Rd, Pd, Ra (Mesa NAK OpMatch): bit 73 compares the register pair Ra:Ra+1, bit 79 selects ANY. ALL: Pd (81-83) = every
        /// active invocation holds the same value, Rd = the active mask then and 0 otherwise. ANY: Rd = the active invocations that hold this
        /// invocation's value. ANY reads every active lane in turn (32 shuffles); MKW uses MATCH.ALL.U64 RZ, P0 only.
        /// </summary>
        public static void Match(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            bool wide = inst.Bit(73);
            Operand lo = Gpr(inst.Ra);
            Operand hi = wide ? Gpr(inst.Ra + 1) : null;
            Operand active = InstEmit.EmitBallot(context, Const(IrConsts.True));

            Operand EqualTo(Operand lane)
            {
                Operand eq = context.ICompareEqual(lo, context.Shuffle(lo, lane));

                return wide ? context.BitwiseAnd(eq, context.ICompareEqual(hi, context.Shuffle(hi, lane))) : eq;
            }

            Operand mask;

            if (inst.Bit(79))
            {
                mask = Const(0);

                for (int i = 0; i < 32; i++)
                {
                    Operand isActive = context.ICompareNotEqual(context.BitwiseAnd(active, Const(1 << i)), Const(0));
                    Operand match = context.BitwiseAnd(isActive, EqualTo(Const(i)));

                    mask = context.BitwiseOr(mask, context.ConditionalSelect(match, Const(1 << i), Const(0)));
                }
            }
            else
            {
                Operand all = InstEmit.EmitVote(context, VoteMode.All, EqualTo(context.FindLSB(active)));

                SetPredDest(context, inst.Field(81, 3), all, false);
                mask = context.ConditionalSelect(all, active, Const(0));
            }

            context.Copy(Gpr(inst.Rd), mask);
        }

        /// <summary>
        /// REDUX.{AND, OR, XOR, SUM, MIN, MAX}[.S32] URd, Ra (Mesa NAK OpRedux): op bits 78-80, bit 73 signed min / max. URd = the reduction of Ra over
        /// the active invocations. A shuffle of an inactive lane is undefined, so the active lanes are read one by one (32 shuffles, each counted only when
        /// its lane is active in the ballot); MKW uses SUM and MIN / MAX (unsigned) in 6 compute programs.
        /// </summary>
        public static void Redux(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int op = inst.Field(78, 3);
            bool signed = inst.Bit(73);

            if (op > 5)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 REDUX with op {op} at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            Operand value = Gpr(inst.Ra);
            Operand active = InstEmit.EmitBallot(context, Const(IrConsts.True));

            Operand acc = op switch
            {
                0 => Const(-1),
                4 => Const(signed ? int.MaxValue : -1),
                5 => Const(signed ? int.MinValue : 0),
                _ => Const(0),
            };

            for (int i = 0; i < 32; i++)
            {
                Operand v = context.Shuffle(value, Const(i));

                Operand combined = op switch
                {
                    0 => context.BitwiseAnd(acc, v),
                    1 => context.BitwiseOr(acc, v),
                    2 => context.BitwiseExclusiveOr(acc, v),
                    3 => context.IAdd(acc, v),
                    4 => signed ? context.IMinimumS32(acc, v) : context.IMinimumU32(acc, v),
                    _ => signed ? context.IMaximumS32(acc, v) : context.IMaximumU32(acc, v),
                };

                Operand isActive = context.ICompareNotEqual(context.BitwiseAnd(active, Const(1 << i)), Const(0));
                acc = context.ConditionalSelect(isActive, combined, acc);
            }

            context.Copy(UGpr(inst.URd), acc);
        }
    }
}
