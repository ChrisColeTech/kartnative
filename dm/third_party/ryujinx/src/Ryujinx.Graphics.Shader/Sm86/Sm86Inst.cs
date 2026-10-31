using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Shader.Sm86
{
    /// <summary>
    /// One SM70+ (Volta to Ampere, the Switch 2's SM86) instruction: 128 bits, little-endian, bit n of the instruction is bit n % 64 of word n / 64.
    /// Bits 0-104 are the operation, 105-127 the scheduling control (stall count, yield, barriers, reuse), which a translation does not need.
    /// Field positions follow Mesa NAK's encoder (src/nouveau/compiler/nak/sm70_encode.rs, MIT); see docs/reference/shaders-sm86.md.
    /// </summary>
    readonly struct Sm86Inst
    {
        public readonly ulong Lo;
        public readonly ulong Hi;

        public Sm86Inst(ulong lo, ulong hi)
        {
            Lo = lo;
            Hi = hi;
        }

        /// <summary>Unsigned field of <paramref name="count"/> (1-64) bits at bit <paramref name="offset"/> (0-127), across the two words.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Bits(int offset, int count)
        {
            ulong mask = count == 64 ? ulong.MaxValue : (1UL << count) - 1;

            if (offset >= 64)
            {
                return (Hi >> (offset - 64)) & mask;
            }

            if (offset + count <= 64)
            {
                return (Lo >> offset) & mask;
            }

            return ((Lo >> offset) | (Hi << (64 - offset))) & mask;
        }

        public int Field(int offset, int count) => (int)Bits(offset, count);

        /// <summary>Signed (two's complement) field.</summary>
        public long SignedBits(int offset, int count)
        {
            ulong v = Bits(offset, count);
            int shift = 64 - count;
            return (long)(v << shift) >> shift;
        }

        public bool Bit(int offset) => Bits(offset, 1) != 0;

        /// <summary>The 12-bit opcode, form included (bits 9-11 of the ALU encodings).</summary>
        public int Opcode => (int)(Lo & 0xfff);

        /// <summary>ALU source form, bits 9-11: 1 R-R, 2 imm32 in the Rc slot... see Sm86Operands.</summary>
        public int Form => (int)((Lo >> 9) & 7);

        /// <summary>Guard predicate index (7 = PT); a P register, or a UP register for uniform-datapath operations.</summary>
        public int Pred => (int)((Lo >> 12) & 7);

        public bool PredInv => ((Lo >> 15) & 1) != 0;

        /// <summary>@!PT: the instruction never runs (the compiler leaves them as padding).</summary>
        public bool NeverExecutes => Pred == 7 && PredInv;

        public int Rd => (int)((Lo >> 16) & 0xff);
        public int Ra => (int)((Lo >> 24) & 0xff);
        public int Rb => (int)((Lo >> 32) & 0xff);
        public int Rc => (int)(Hi & 0xff);

        /// <summary>Uniform register fields are 6 bits on SM75-SM90 (63 = URZ).</summary>
        public int URd => (int)((Lo >> 16) & 0x3f);
        public int URa => (int)((Lo >> 24) & 0x3f);
        public int URb => (int)((Lo >> 32) & 0x3f);
        public int URc => (int)(Hi & 0x3f);

        public uint Imm32 => (uint)(Lo >> 32);

        /// <summary>Byte offset of an ALU constant-buffer operand, bits 38-53.</summary>
        public int CbufOffset => Field(38, 16);

        /// <summary>Bank of an ALU constant-buffer operand, bits 54-58.</summary>
        public int CbufSlot => Field(54, 5);

        /// <summary>Second predicate source of many operations (87-89, negated by 90).</summary>
        public int Pred87 => Field(87, 3);
        public bool Pred87Inv => Bit(90);

        public override string ToString() => $"{Hi:x16}{Lo:x16}";
    }
}
