using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Sm86
{
    /// <summary>Emits one decoded SM86 block into the IR (the SM86 counterpart of Translator.EmitOps): guards, then each instruction's emitter.</summary>
    static class Sm86Translator
    {
        public static void EmitOps(EmitterContext context, Block block)
        {
            bool debug = context.TranslatorContext.Options.Flags.HasFlag(TranslationFlags.DebugMode);

            for (int opIndex = 0; opIndex < block.OpCodes.Count; opIndex++)
            {
                InstOp op = block.OpCodes[opIndex];
                Sm86Inst inst = Sm86Decoder.Inst(op);
                Sm86InstInfo info = Sm86InstTable.Get(inst.Opcode);

                if (debug)
                {
                    context.Add(new CommentNode($"0x{op.Address:X6}: {inst} {info.Name ?? "???"}"));
                }

                if (inst.NeverExecutes)
                {
                    continue;
                }

                if (op.Emitter == null)
                {
                    context.TranslatorContext.GpuAccessor.Log($"Unsupported SM86 instruction 0x{inst.Opcode:x3} at 0x{op.Address:X} ({inst}).");
                    continue;
                }

                Operand predSkipLbl = null;

                if (inst.Pred != RegisterConsts.PredicateTrueIndex && !info.Props.HasFlag(Sm86InstProps.NoPred))
                {
                    Operand label;

                    if (opIndex == block.OpCodes.Count - 1 && Sm86Decoder.HasNext(block))
                    {
                        label = context.GetLabel(block.EndAddress);
                    }
                    else
                    {
                        label = Label();

                        predSkipLbl = label;
                    }

                    Operand pred = Sm86Emit.Guard(inst, info.Props);

                    if (inst.PredInv)
                    {
                        context.BranchIfTrue(label, pred);
                    }
                    else
                    {
                        context.BranchIfFalse(label, pred);
                    }
                }

                context.CurrOp = op;

                op.Emitter(context);

                if (predSkipLbl != null)
                {
                    context.MarkLabel(predSkipLbl);
                }
            }
        }
    }
}
