namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86InstTable
    {
        /// <summary>
        /// Integer, float, logic, comparison and conversion operations (wave 2 "ALU + conversion" family). Base opcodes and modifier bits: Mesa NAK
        /// sm70_encode.rs (OpMov, OpIAdd3, OpIMad, OpFFma, OpFAdd, OpFMul, OpLop3, OpISetP, OpFSetP, OpMuFu); uniform variants carry Uniform.
        /// </summary>
        private static void AddAlu()
        {
            const Sm86InstProps Rd = Sm86InstProps.Rd;
            const Sm86InstProps U = Sm86InstProps.Uniform;

            Add("MOV", 0x002, Sm86Forms.BSlot, Sm86Emit.Mov, Rd);
            Add("UMOV", 0x082, Sm86Forms.I | Sm86Forms.U, Sm86Emit.Mov, U);

            Add("IADD3", 0x010, Sm86Forms.All, Sm86Emit.Iadd3, Rd);
            Add("UIADD3", 0x090, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Iadd3, U);
            Add("IMAD", 0x024, Sm86Forms.All, Sm86Emit.Imad, Rd);
            Add("UIMAD", 0x0a4, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Imad, U);
            Add("LOP3", 0x012, Sm86Forms.All, Sm86Emit.Lop3, Rd);
            Add("ULOP3", 0x092, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Lop3, U);
            Add("ISETP", 0x00c, Sm86Forms.All, Sm86Emit.Isetp);
            Add("UISETP", 0x08c, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Isetp, U);
            Add("SHF", 0x019, Sm86Forms.All, Sm86Emit.Shf, Rd);
            Add("USHF", 0x099, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Shf, U);
            Add("IMNMX", 0x017, Sm86Forms.All, Sm86Emit.Imnmx, Rd);
            Add("SEL", 0x007, Sm86Forms.All, Sm86Emit.Sel, Rd);
            Add("USEL", 0x087, Sm86Forms.R | Sm86Forms.IC | Sm86Forms.I, Sm86Emit.Sel, U);
            Add("CS2R", 0x805, Sm86Emit.Cs2r, Rd);

            Add("FADD", 0x021, Sm86Forms.All, Sm86Emit.Fadd, Rd);
            Add("FMUL", 0x020, Sm86Forms.All, Sm86Emit.Fmul, Rd);
            Add("FFMA", 0x023, Sm86Forms.All, Sm86Emit.Ffma, Rd);
            Add("FSETP", 0x00b, Sm86Forms.All, Sm86Emit.Fsetp);
            Add("MUFU", 0x108, Sm86Forms.BSlot, Sm86Emit.Mufu, Rd);
            Add("FMNMX", 0x009, Sm86Forms.All, Sm86Emit.Fmnmx, Rd);
            Add("FSEL", 0x008, Sm86Forms.All, Sm86Emit.Fsel, Rd);
        }
    }
}
