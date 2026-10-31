namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86InstTable
    {
        /// <summary>
        /// Integer, float, logic, comparison, F16x2 and conversion operations (wave 2 "ALU + conversion" family; emitters in Sm86Emit.Alu.cs,
        /// .AluHalf.cs and .AluConv.cs). Base opcodes and modifier bits: Mesa NAK sm70_encode.rs; uniform variants carry Uniform.
        /// </summary>
        private static void AddAlu()
        {
            const Sm86InstProps Rd = Sm86InstProps.Rd;
            const Sm86InstProps U = Sm86InstProps.Uniform;

            Add("MOV", 0x002, Sm86Forms.BSlot, Sm86Emit.Mov, Rd);
            Add("UMOV", 0x082, Sm86Forms.I | Sm86Forms.U, Sm86Emit.Mov, Rd | U);

            Add("IADD3", 0x010, Sm86Forms.All, Sm86Emit.Iadd3, Rd);
            Add("UIADD3", 0x090, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Iadd3, Rd | U);
            Add("IMAD", 0x024, Sm86Forms.All, Sm86Emit.Imad, Rd);
            Add("UIMAD", 0x0a4, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Imad, Rd | U);
            Add("LOP3", 0x012, Sm86Forms.All, Sm86Emit.Lop3, Rd);
            Add("ULOP3", 0x092, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Lop3, Rd | U);
            Add("ISETP", 0x00c, Sm86Forms.All, Sm86Emit.Isetp);
            Add("UISETP", 0x08c, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Isetp, U);
            Add("SHF", 0x019, Sm86Forms.All, Sm86Emit.Shf, Rd);
            Add("USHF", 0x099, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Shf, Rd | U);
            Add("IMNMX", 0x017, Sm86Forms.All, Sm86Emit.Imnmx, Rd);
            Add("SEL", 0x007, Sm86Forms.All, Sm86Emit.Sel, Rd);
            Add("USEL", 0x087, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Sel, Rd | U);
            Add("CS2R", 0x805, Sm86Emit.Cs2r, Rd);
            Add("IMAD.WIDE", 0x025, Sm86Forms.All, Sm86Emit.ImadWide, Rd);
            Add("UIMAD.WIDE", 0x0a5, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.ImadWide, Rd | U);
            Add("IMAD.HI", 0x027, Sm86Forms.All, Sm86Emit.ImadHi, Rd);
            Add("IDP.4A", 0x026, Sm86Forms.All, Sm86Emit.Idp4a, Rd);
            Add("LEA", 0x011, Sm86Forms.All, Sm86Emit.Lea, Rd);
            Add("ULEA", 0x091, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Lea, Rd | U);
            Add("IABS", 0x013, Sm86Forms.BSlot, Sm86Emit.Iabs, Rd);
            Add("PRMT", 0x016, Sm86Forms.All, Sm86Emit.Prmt, Rd);
            Add("UPRMT", 0x096, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Prmt, Rd | U);
            Add("SGXT", 0x01a, Sm86Forms.All, Sm86Emit.Sgxt, Rd);
            Add("USGXT", 0x09a, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Sgxt, Rd | U);
            Add("BMSK", 0x01b, Sm86Forms.All, Sm86Emit.Bmsk, Rd);
            Add("UBMSK", 0x09b, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Bmsk, Rd | U);
            Add("FLO", 0x100, Sm86Forms.BSlot, Sm86Emit.Flo, Rd);
            Add("UFLO", 0x0bd, Sm86Forms.R | Sm86Forms.I, Sm86Emit.Flo, Rd | U);
            Add("BREV", 0x101, Sm86Forms.BSlot, Sm86Emit.Brev, Rd);
            Add("UBREV", 0x0be, Sm86Forms.R | Sm86Forms.I, Sm86Emit.Brev, Rd | U);
            Add("POPC", 0x109, Sm86Forms.BSlot, Sm86Emit.Popc, Rd);
            Add("UPOPC", 0x0bf, Sm86Forms.R | Sm86Forms.I, Sm86Emit.Popc, Rd | U);

            Add("FADD", 0x021, Sm86Forms.All, Sm86Emit.Fadd, Rd);
            Add("FMUL", 0x020, Sm86Forms.All, Sm86Emit.Fmul, Rd);
            Add("FFMA", 0x023, Sm86Forms.All, Sm86Emit.Ffma, Rd);
            Add("FSETP", 0x00b, Sm86Forms.All, Sm86Emit.Fsetp);
            Add("FSET", 0x00a, Sm86Forms.All, Sm86Emit.Fset, Rd);
            Add("MUFU", 0x108, Sm86Forms.BSlot, Sm86Emit.Mufu, Rd);
            Add("FMNMX", 0x009, Sm86Forms.All, Sm86Emit.Fmnmx, Rd);
            Add("FSEL", 0x008, Sm86Forms.All, Sm86Emit.Fsel, Rd);
            Add("FSWZADD", 0x822, Sm86Emit.Fswzadd, Rd);

            Add("HADD2", 0x030, Sm86Forms.All, Sm86Emit.Hadd2, Rd);
            Add("HFMA2", 0x031, Sm86Forms.All, Sm86Emit.Hfma2, Rd);
            Add("HMUL2", 0x032, Sm86Forms.All, Sm86Emit.Hmul2, Rd);
            Add("HSET2", 0x033, Sm86Forms.All, Sm86Emit.Hset2, Rd);
            Add("HSETP2", 0x034, Sm86Forms.All, Sm86Emit.Hsetp2);
            Add("HMNMX2", 0x040, Sm86Forms.All, Sm86Emit.Hmnmx2, Rd);

            // 0x110-0x113 are the conversions with a 64-bit operand.
            Add("F2F", 0x104, Sm86Forms.BSlot, Sm86Emit.F2f, Rd);
            Add("F2F.64", 0x110, Sm86Forms.BSlot, Sm86Emit.F2f, Rd);
            Add("F2I", 0x105, Sm86Forms.BSlot, Sm86Emit.F2i, Rd);
            Add("F2I.64", 0x111, Sm86Forms.BSlot, Sm86Emit.F2i, Rd);
            Add("I2F", 0x106, Sm86Forms.BSlot, Sm86Emit.I2f, Rd);
            Add("I2F.64", 0x112, Sm86Forms.BSlot, Sm86Emit.I2f, Rd);
            Add("I2FP", 0x045, Sm86Forms.BSlot, Sm86Emit.I2f, Rd);
            Add("FRND", 0x107, Sm86Forms.BSlot, Sm86Emit.Frnd, Rd);
            Add("FRND.64", 0x113, Sm86Forms.BSlot, Sm86Emit.Frnd, Rd);
            Add("F2FP", 0x03e, Sm86Forms.All, Sm86Emit.F2fp, Rd);
        }
    }
}
