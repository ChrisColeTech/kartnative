using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.Instructions;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86Emit
    {
        public static void Nop(EmitterContext context)
        {
        }

        /// <summary>
        /// The condition of a flow instruction: its guard (bits 12-15) and its predicate source (bits 87-89, inverted by 90). Null when both are PT.
        /// </summary>
        private static Operand FlowCondition(EmitterContext context, Sm86Inst inst, bool withGuard)
        {
            Operand cond = null;

            if (withGuard && inst.Pred != RegisterConsts.PredicateTrueIndex)
            {
                cond = PredSrc(context, inst.Pred, inst.PredInv, IsUniform(inst));
            }
            else if (withGuard && inst.PredInv)
            {
                cond = Const(IrConsts.False);
            }

            if (inst.Pred87 != RegisterConsts.PredicateTrueIndex || inst.Pred87Inv)
            {
                Operand pred = PredSrc(context, inst.Pred87, inst.Pred87Inv, false);

                cond = cond == null ? pred : context.BitwiseAnd(cond, pred);
            }

            return cond;
        }

        /// <summary>BRA: relative branch, target = next instruction + 4 * bits 34-81 (signed).</summary>
        public static void Bra(EmitterContext context)
        {
            EmitBranch(context, Sm86Decoder.BranchTarget(context.CurrOp));
        }

        /// <summary>
        /// BRA.U UPp: a branch on a uniform predicate (Mesa NAK OpBra, SM80+): UPp in bits 24-26, inverted by 27, with the guard; bits 87-89 are PT.
        /// </summary>
        public static void BraU(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            ulong target = Sm86Decoder.BranchTarget(context.CurrOp);

            if (target == context.CurrOp.Address + Sm86Decoder.InstSize)
            {
                return;
            }

            Operand cond = PredSrc(context, inst.Field(24, 3), inst.Bit(27), true);
            Operand guard = FlowCondition(context, inst, withGuard: true);

            context.BranchIfTrue(context.GetLabel(target), guard == null ? cond : context.BitwiseAnd(guard, cond));
        }

        private static void EmitBranch(EmitterContext context, ulong target)
        {
            Sm86Inst inst = Inst(context);

            if (target == context.CurrOp.Address + Sm86Decoder.InstSize)
            {
                return;
            }

            Operand label = context.GetLabel(target);
            Operand cond = FlowCondition(context, inst, withGuard: true);

            if (cond == null)
            {
                context.Branch(label);
            }
            else
            {
                context.BranchIfTrue(label, cond);
            }
        }

        /// <summary>
        /// CALL.REL / CALL.REL.NOINC that the decoder found to be a function call (Sm86Flow.IsFunctionCall): a call to the Ryujinx function at the target.
        /// </summary>
        public static void Call(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            ulong target = Sm86Decoder.BranchTarget(context.CurrOp);
            DecodedFunction function = context.Program.GetFunctionByAddress(target);

            if (function == null)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 CALL at 0x{context.CurrOp.Address:X}: no function at 0x{target:X}.");
                return;
            }

            Operand cond = FlowCondition(context, inst, withGuard: true);
            Operand lblSkip = null;

            if (cond != null)
            {
                lblSkip = Label();
                context.BranchIfFalse(lblSkip, cond);
            }

            context.Call(function.Id, false);

            if (lblSkip != null)
            {
                context.MarkLabel(lblSkip);
            }
        }

        /// <summary>CALL.REL.NOINC without a return address: a branch (the decoder installs this emitter in place of Call).</summary>
        public static void CallAsJump(EmitterContext context)
        {
            EmitBranch(context, Sm86Decoder.BranchTarget(context.CurrOp));
        }

        /// <summary>RET.REL(.NODEC): the return of a function call. Its link register (bits 24-31) holds the return address the call's MOV set.</summary>
        public static void Ret(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            if (!context.IsNonMain)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 RET at 0x{context.CurrOp.Address:X} in the main function is not implemented.");
                return;
            }

            EmitConditional(context, FlowCondition(context, inst, withGuard: false), () => context.Return());
        }

        /// <summary>
        /// EXIT. A Ryujinx function cannot end the invocation, so an EXIT inside a called function is not translated (no MKW program has one: every MKW
        /// callee returns with RET); the note makes the chain drop the program rather than run it on.
        /// </summary>
        public static void Exit(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            if (context.IsNonMain)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 EXIT at 0x{context.CurrOp.Address:X} in a called function is not implemented.");
                return;
            }

            EmitConditional(context, FlowCondition(context, inst, withGuard: false), () =>
            {
                if (context.PrepareForReturn())
                {
                    context.Return();
                }
            });
        }

        public static void Kill(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            EmitConditional(context, FlowCondition(context, inst, withGuard: false), () => context.Discard());
        }

        /// <summary>
        /// BRX Ra (BRXU URa, 0x958): branch to (next instruction + 4 * bits 34-81) + Ra, the targets found by Sm86Flow.FindBrxTargets. BRXU takes the
        /// index from the uniform register in bits 24-29.
        /// </summary>
        public static void Brx(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int startIndex = Sm86Decoder.HasNext(context.CurrBlock) ? 1 : 0;

            if (context.CurrBlock.Successors.Count <= startIndex)
            {
                context.TranslatorContext.GpuAccessor.Log($"Failed to find targets for SM86 BRX instruction at 0x{context.CurrOp.Address:X}.");
                return;
            }

            Operand index = Sm86Flow.IsBrxu(inst) ? UGpr(inst.URa) : Gpr(inst.Ra);
            Operand address = context.IAdd(index, Const((int)Sm86Decoder.BranchTarget(context.CurrOp)));

            Operand cond = FlowCondition(context, inst, withGuard: false);
            Operand lblSkip = null;

            if (cond != null)
            {
                lblSkip = Label();
                context.BranchIfFalse(lblSkip, cond);
            }

            InstEmit.EmitBrx(context, address, startIndex);

            if (lblSkip != null)
            {
                context.MarkLabel(lblSkip);
            }
        }

        private static void EmitConditional(EmitterContext context, Operand cond, System.Action emit)
        {
            if (cond == null)
            {
                emit();
                return;
            }

            Operand lblSkip = Label();
            context.BranchIfFalse(lblSkip, cond);
            emit();
            context.MarkLabel(lblSkip);
        }
    }
}
