using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.Instructions;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Sm86
{
    /// <summary>
    /// SM86 instruction emitters. This file holds the operand helpers every family shares; each family's emitters live in their own partial file
    /// (Sm86Emit.Alu.cs, .Mem.cs, .Tex.cs, .Flow.cs, .Warp.cs) beside the matching Sm86InstTable file.
    ///
    /// ALU operand slots (Mesa NAK sm70_encode.rs, encode_alu_base): A is the register in bits 24-31. The form (bits 9-11) places B and C:
    ///   forms 1, 4, 5, 6: B in bits 32-63 (register 32-39, imm32, constant buffer, or uniform register 32-37), C the register in bits 64-71;
    ///   forms 2, 3, 7:    C in bits 32-63 (imm32, constant buffer, uniform register), B the register in bits 64-71.
    /// Modifier bits belong to the slot: bits 24-31 abs 73 / neg 72, bits 32-63 abs 62 / neg 63 (not for an immediate), bits 64-71 abs 74 / neg 75.
    /// Uniform-datapath operations (Sm86InstProps.Uniform) use the same slots with uniform registers.
    /// </summary>
    static partial class Sm86Emit
    {
        public static Sm86Inst Inst(EmitterContext context) => new(context.CurrOp.RawOpCode, context.CurrOp.RawOpCodeHi);

        public static Sm86InstProps Props(Sm86Inst inst) => Sm86InstTable.Get(inst.Opcode).Props;

        public static bool IsUniform(Sm86Inst inst) => Props(inst).HasFlag(Sm86InstProps.Uniform);

        // Registers. RZ / URZ read as 0 and PT / UPT as true (OperandHelper.Register); a write to them is dropped.

        public static Operand Gpr(int index) => Register(index, RegisterType.Gpr);

        public static Operand UGpr(int index) => Register(index, RegisterType.UniformGpr);

        /// <summary>A GPR or, in a uniform-datapath operation, a uniform register.</summary>
        public static Operand Reg(int index, bool uniform) => uniform ? UGpr(index & 0x3f) : Gpr(index);

        public static Operand Pred(int index) => Register(index, RegisterType.Predicate);

        public static Operand UPred(int index) => Register(index, RegisterType.UniformPredicate);

        /// <summary>A predicate source: 3-bit index, optionally inverted; uniform selects UP0-UP6 / UPT.</summary>
        public static Operand PredSrc(EmitterContext context, int index, bool invert, bool uniform)
        {
            Operand pred = uniform ? UPred(index) : Pred(index);

            return invert ? context.BitwiseNot(pred) : pred;
        }

        /// <summary>The guard of an instruction: P or, for uniform-datapath operations, UP (not inverted; the caller applies bit 15).</summary>
        public static Operand Guard(Sm86Inst inst, Sm86InstProps props)
        {
            return props.HasFlag(Sm86InstProps.Uniform) ? UPred(inst.Pred) : Pred(inst.Pred);
        }

        /// <summary>The destination in bits 16-23 (a uniform register for uniform-datapath operations).</summary>
        public static Operand Dest(Sm86Inst inst) => IsUniform(inst) ? UGpr(inst.URd) : Gpr(inst.Rd);

        public static void SetDest(EmitterContext context, Sm86Inst inst, Operand value)
        {
            context.Copy(Dest(inst), value);
        }

        // ALU sources.

        public enum Slot
        {
            A,
            B,
            C,
        }

        /// <summary>Forms 2, 3 and 7 put C in bits 32-63 and B in bits 64-71.</summary>
        public static bool CInHighSlot(Sm86Inst inst) => inst.Form is 2 or 3 or 7;

        /// <summary>The raw value of an ALU source slot, without modifiers.</summary>
        public static Operand Src(EmitterContext context, Sm86Inst inst, Slot slot)
        {
            bool uniform = IsUniform(inst);

            if (slot == Slot.A)
            {
                return Reg(inst.Ra, uniform);
            }

            bool inSlot32 = (slot == Slot.B) != CInHighSlot(inst);

            if (!inSlot32)
            {
                return Reg(inst.Rc, uniform);
            }

            return inst.Form switch
            {
                1 => Reg(inst.Rb, uniform),
                2 or 4 => Const((int)inst.Imm32),
                3 or 5 => SrcCbuf(context, inst),
                _ => UGpr(inst.URb), // forms 6 and 7
            };
        }

        /// <summary>
        /// The second source of a two-source operation (FADD, FMUL, ISETP, FSETP...): B in forms 1, 4, 5 and 6, C in forms 2, 3 and 7
        /// (the compiler then leaves RZ in bits 64-71).
        /// </summary>
        public static Slot SecondSlot(Sm86Inst inst) => CInHighSlot(inst) ? Slot.C : Slot.B;

        /// <summary>Bit positions of a slot's abs and neg modifiers; (-1, -1) for an immediate, which has none.</summary>
        public static (int Abs, int Neg) ModifierBits(Sm86Inst inst, Slot slot)
        {
            if (slot == Slot.A)
            {
                return (73, 72);
            }

            bool inSlot32 = (slot == Slot.B) != CInHighSlot(inst);

            if (!inSlot32)
            {
                return (74, 75);
            }

            return inst.Form is 2 or 4 ? (-1, -1) : (62, 63);
        }

        public static bool Abs(Sm86Inst inst, Slot slot)
        {
            int bit = ModifierBits(inst, slot).Abs;
            return bit >= 0 && inst.Bit(bit);
        }

        public static bool Neg(Sm86Inst inst, Slot slot)
        {
            int bit = ModifierBits(inst, slot).Neg;
            return bit >= 0 && inst.Bit(bit);
        }

        /// <summary>A float source with its abs / neg modifiers applied.</summary>
        public static Operand FpSrc(EmitterContext context, Sm86Inst inst, Slot slot)
        {
            return context.FPAbsNeg(Src(context, inst, slot), Abs(inst, slot), Neg(inst, slot));
        }

        /// <summary>An integer source with its neg modifier applied (two's complement).</summary>
        public static Operand IntSrc(EmitterContext context, Sm86Inst inst, Slot slot)
        {
            return context.INegate(Src(context, inst, slot), Neg(inst, slot));
        }

        /// <summary>
        /// The constant-buffer operand of forms 3 and 5: byte offset in bits 38-53, bank in 54-58; with bit 91 set the bank is the uniform register in
        /// bits 32-37 (c[URx][offset]).
        /// </summary>
        public static Operand SrcCbuf(EmitterContext context, Sm86Inst inst)
        {
            int offset = inst.CbufOffset;

            if (inst.Bit(91) && !IsUniform(inst))
            {
                return InstEmit.EmitLoadConstant(context, UGpr(inst.URb), Const(offset >> 2));
            }

            if ((offset & 3) != 0)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 constant-buffer operand at unaligned offset c[{inst.CbufSlot}][0x{offset:x}].");
            }

            return Cbuf(inst.CbufSlot, offset >> 2);
        }
    }
}
