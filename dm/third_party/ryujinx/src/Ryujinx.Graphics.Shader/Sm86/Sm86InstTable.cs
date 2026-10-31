using Ryujinx.Graphics.Shader.Instructions;
using Ryujinx.Graphics.Shader.Translation;
using System;

namespace Ryujinx.Graphics.Shader.Sm86
{
    [Flags]
    enum Sm86InstProps : uint
    {
        None = 0,

        /// <summary>Uniform datapath: the guard is a UP register (bits 12-15) and the destination a UR register.</summary>
        Uniform = 1 << 0,

        /// <summary>The emitter applies the guard itself (branches fold it into their condition).</summary>
        NoPred = 1 << 1,

        // Control flow, read by the decoder (Sm86Decoder); each one ends a block.
        Branch = 1 << 2,
        Exit = 1 << 3,
        Call = 1 << 4,
        Ret = 1 << 5,
        Brx = 1 << 6,

        /// <summary>Writes the GPR in bits 16-23 (the BRX jump-table tracker follows these).</summary>
        Rd = 1 << 8,

        // Attribute accesses, whose attribute usage the decoder records (as Decoder does for ALD / AST / IPA).
        AttrLoad = 1 << 9,
        AttrStore = 1 << 10,
        Ipa = 1 << 11,

        BlockEnd = Branch | Exit | Call | Ret | Brx,
    }

    /// <summary>ALU source forms, the opcode's bits 9-11 (Mesa NAK sm70_encode.rs encode_alu_base).</summary>
    [Flags]
    enum Sm86Forms : byte
    {
        /// <summary>Form 1: A, B and C registers.</summary>
        R = 1 << 1,

        /// <summary>Form 2: 32-bit immediate in the C slot (bits 32-63), the B register in bits 64-71.</summary>
        IC = 1 << 2,

        /// <summary>Form 3: constant buffer in the C slot, the B register in bits 64-71.</summary>
        CC = 1 << 3,

        /// <summary>Form 4: 32-bit immediate as B.</summary>
        I = 1 << 4,

        /// <summary>Form 5: constant buffer as B.</summary>
        C = 1 << 5,

        /// <summary>Form 6: uniform register as B.</summary>
        U = 1 << 6,

        /// <summary>Form 7: uniform register in the C slot, the B register in bits 64-71.</summary>
        UC = 1 << 7,

        BSlot = R | I | C | U,
        All = R | IC | CC | I | C | U | UC,
    }

    readonly struct Sm86InstInfo
    {
        public readonly string Name;
        public readonly InstEmitter Emitter;
        public readonly Sm86InstProps Props;
        public readonly FeatureFlags Features;

        public Sm86InstInfo(string name, InstEmitter emitter, Sm86InstProps props, FeatureFlags features)
        {
            Name = name;
            Emitter = emitter;
            Props = props;
            Features = features;
        }

        public bool IsValid => Name != null;
    }

    /// <summary>
    /// The SM86 opcode table, indexed by the 12-bit opcode (bits 0-11). Each instruction family registers its rows in its own partial file
    /// (Sm86InstTable.Alu.cs, .Mem.cs, .Tex.cs, .Flow.cs, .Warp.cs) and emits in the matching Sm86Emit file, so families can grow independently;
    /// a second row for the same opcode throws at start-up.
    /// </summary>
    static partial class Sm86InstTable
    {
        private static readonly Sm86InstInfo[] _table = new Sm86InstInfo[1 << 12];

        static Sm86InstTable()
        {
            AddFlow();
        }

        public static Sm86InstInfo Get(int opcode) => _table[opcode & 0xfff];

        /// <summary>Registers an ALU-style operation: <paramref name="baseOpcode"/> is bits 0-8, one row per form in <paramref name="forms"/>.</summary>
        private static void Add(string name, int baseOpcode, Sm86Forms forms, InstEmitter emitter, Sm86InstProps props = Sm86InstProps.None, FeatureFlags features = FeatureFlags.None)
        {
            if ((baseOpcode & ~0x1ff) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(baseOpcode), $"{name}: base opcode 0x{baseOpcode:x} has form bits");
            }

            for (int form = 1; form < 8; form++)
            {
                if (((int)forms & (1 << form)) != 0)
                {
                    Add(name, baseOpcode | (form << 9), emitter, props, features);
                }
            }
        }

        /// <summary>Registers one exact 12-bit opcode.</summary>
        private static void Add(string name, int opcode, InstEmitter emitter, Sm86InstProps props = Sm86InstProps.None, FeatureFlags features = FeatureFlags.None)
        {
            if (_table[opcode].IsValid)
            {
                throw new InvalidOperationException($"SM86 opcode 0x{opcode:x3} registered twice ({_table[opcode].Name}, {name})");
            }

            _table[opcode] = new Sm86InstInfo(name, emitter, props, features);
        }
    }
}
