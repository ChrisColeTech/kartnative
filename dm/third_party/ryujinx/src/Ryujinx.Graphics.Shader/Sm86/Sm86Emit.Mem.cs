using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.Instructions;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86Emit
    {
        /// <summary>S2R / S2UR d = special register (bits 72-79, Maxwell's numbering: 0x21 TID.X, 0x25 CTAID.X...).</summary>
        public static void S2r(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            SetDest(context, inst, InstEmit.EmitLoadSpecialRegister(context, (SReg)inst.Field(72, 8)));
        }

        /// <summary>
        /// LDC d, c[bank][Ra + offset]: bank in bits 54-58, signed byte offset in 38-53, size in 73-75 (U8, S8, U16, S16, B32, B64), address mode in
        /// 78-79 (as Maxwell's: indexed, linear, segmented, segmented linear). Bit 91 (a bindless bank, NAK's 0x582 form) does not occur in MKW.
        /// </summary>
        public static void Ldc(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            if (inst.Bit(91))
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 LDC with a bindless bank at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            InstEmit.EmitLdc(context, new InstEmit.LdcFields(
                inst.Rd,
                inst.Ra,
                inst.Field(54, 5),
                inst.Field(38, 16),
                (LsSize2)inst.Field(73, 3),
                (AddressMode)inst.Field(78, 2)));
        }

        /// <summary>
        /// ULDC URd, c[bank][offset] (0xab9; bits 24-31 are zero) and ULDC URd, c[bank][URa + offset] (0xabb, the index uniform register in bits 24-29):
        /// bank in bits 54-58, byte offset in 38-53 (unsigned without an index, as NAK's u16 CBufRef offset; signed with one, as LDC's), size in 73-75
        /// (U8, S8, U16, S16, B32, B64). Bit 91 is set on every 0xabb.
        /// </summary>
        public static void Uldc(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int size = inst.Field(73, 3);

            if (size > (int)LsSize2.B64)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 ULDC of size {size} at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            Operand slot = Const(inst.Field(54, 5));
            bool indexed = inst.Opcode == 0xabb;
            int offset = indexed ? (short)inst.Field(38, 16) : inst.Field(38, 16);
            int count = size == (int)LsSize2.B64 ? 2 : 1;

            Operand[] values = new Operand[count];

            if (!indexed)
            {
                // Constant offset: the loads fold to constant-buffer operands, which the storage-buffer tracking (GlobalToStorage) follows.
                for (int index = 0; index < count; index++)
                {
                    values[index] = InstEmit.EmitLoadConstant(context, slot, Const((offset >> 2) + index));
                }

                if (size < (int)LsSize2.B32)
                {
                    values[0] = ExtractSmallInt(context, size, Const((offset & 3) * 8), values[0]);
                }
            }
            else
            {
                Operand address = context.IAdd(UGpr(inst.URa), Const(offset));
                Operand wordOffset = context.ShiftRightU32(address, Const(2));

                for (int index = 0; index < count; index++)
                {
                    values[index] = InstEmit.EmitLoadConstant(context, slot, index == 0 ? wordOffset : context.IAdd(wordOffset, Const(index)));
                }

                if (size < (int)LsSize2.B32)
                {
                    values[0] = ExtractSmallInt(context, size, context.ShiftLeft(context.BitwiseAnd(address, Const(3)), Const(3)), values[0]);
                }
            }

            for (int index = 0; index < count && inst.URd + index < RegisterConsts.UniformRegisterZeroIndex; index++)
            {
                context.Copy(UGpr(inst.URd + index), values[index]);
            }
        }

        /// <summary>The U8 / S8 / U16 / S16 value at <paramref name="bitOffset"/> of a loaded word, zero- or sign-extended.</summary>
        private static Operand ExtractSmallInt(EmitterContext context, int size, Operand bitOffset, Operand word)
        {
            Operand bits = Const(size <= (int)LsSize2.S8 ? 8 : 16);

            return (size & 1) != 0 ? context.BitfieldExtractS32(word, bitOffset, bits) : context.BitfieldExtractU32(word, bitOffset, bits);
        }

        /// <summary>ALD d, a[Ra + addr][vertex Rb]: address bits 40-49, components - 1 in 74-75, per-patch 76, physical 77, output 79.</summary>
        public static void Ald(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            InstEmit.EmitAld(context, new InstEmit.AldFields(inst.Rd, inst.Ra, inst.Rb, inst.Field(40, 10), inst.Field(74, 2), inst.Bit(76), inst.Bit(79), inst.Bit(77)));
        }

        /// <summary>The SM70+ variable pixel rate shading table index (NAK_ATTR_VPRS_TABLE_INDEX), a per-primitive output of the last vertex stage.</summary>
        private const int AttributeVprsTableIndex = 0x5c;

        /// <summary>
        /// AST a[Ra + addr], data Rb (bits 32-39); vertex Rc (64-71) unused; fields as ALD. A store to a[0x5c], the shading-rate table index (MKW's
        /// vertex programs write 1, palette entry 1), is dropped: Metal has no per-primitive shading rate (its rasterization rate maps are per screen
        /// region), so those primitives shade at full rate.
        /// </summary>
        public static void Ast(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int address = inst.Field(40, 10);
            int countMinusOne = inst.Field(74, 2);
            int data = inst.Rb;

            if (address == AttributeVprsTableIndex && !inst.Bit(77))
            {
                context.Add(new CommentNode("SM86 AST to the shading-rate table index a[0x5c]: no per-primitive shading rate on Metal"));

                if (countMinusOne == 0)
                {
                    return;
                }

                address += 4;
                countMinusOne--;
                data = data == RegisterConsts.RegisterZeroIndex ? data : data + 1;
            }

            InstEmit.EmitAst(context, new InstEmit.AstFields(inst.Ra, data, address, countMinusOne, inst.Bit(76), inst.Bit(77)));
        }

        /// <summary>
        /// IPA d, a[addr] (0x326) and IPA d, a[URa + addr] (0x526, bit 91, the index uniform register in bits 24-29): attribute word address in bits
        /// 64-71, location in 76-77 (default, centroid, offset by Rb), frequency in 78-79 (pass, constant, state). As on Maxwell, a pass read of a
        /// perspective attribute is the attribute times 1/w (the program multiplies by w itself). The location is not modelled: the translator
        /// declares inputs without a centroid or offset qualifier, so a centroid read differs only on multisampled edges.
        /// </summary>
        public static void Ipa(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            if (inst.Field(76, 2) == 2)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 IPA at an offset at 0x{context.CurrOp.Address:X}: interpolated at the pixel centre.");
            }

            IpaOp op = inst.Field(78, 2) switch
            {
                1 => IpaOp.Constant,
                2 => IpaOp.Sc,
                _ => IpaOp.Pass,
            };

            int address = inst.Field(64, 8) * 4;

            if (!inst.Bit(91))
            {
                InstEmit.EmitIpa(context, new InstEmit.IpaFields(inst.Rd, RegisterConsts.RegisterZeroIndex, RegisterConsts.RegisterZeroIndex, address, false, op, false));
                return;
            }

            // Indexed: as Maxwell's IPA.IDX (InstEmit.EmitIpa), with the byte address URa + addr into the user attributes.
            Operand offset = context.IAdd(UGpr(inst.URa), Const(address - AttributeConsts.UserAttributeBase));
            Operand vecIndex = context.ShiftRightU32(offset, Const(4));
            Operand elemIndex = context.BitwiseAnd(context.ShiftRightU32(offset, Const(2)), Const(3));

            Operand res = context.Load(StorageKind.Input, IoVariable.UserDefined, null, vecIndex, elemIndex);

            if (op == IpaOp.Pass)
            {
                res = context.FPMultiply(res, context.Load(StorageKind.Input, IoVariable.FragmentCoord, null, Const(3)));
            }

            context.Copy(Gpr(inst.Rd), res);
        }

        // Memory. Sizes in bits 73-75 (U8, S8, U16, S16, B32, B64, B128), signed byte offset in 40-63. Global addresses are 64-bit: Ra (bits 24-31,
        // a register pair when its .E bit is set) plus, with bit 91, a uniform register (pair when bit 72 is set). Without bit 91, bit 72 is Ra's .E
        // bit (Mesa NAK sm70_encode.rs, OpLd / OpSt / OpAtom). The uniform register is the storage buffer's base address, which MKW loads from the
        // driver constant buffer (ULDC.64 URx, c[0][...]); GlobalToStorage follows it to the storage buffer.

        /// <summary>A global address operand layout: where the uniform register is and which bits make Ra and the uniform register 64-bit.</summary>
        private readonly record struct GlobalAddressFields(int UrStart, int RaWideBit, int UrWideBit);

        private static GlobalAddressFields GlobalFields(Sm86Inst inst, int urStart, int raWideBitWithUr)
        {
            return inst.Bit(91) ? new GlobalAddressFields(urStart, raWideBitWithUr, 72) : new GlobalAddressFields(-1, 72, -1);
        }

        /// <summary>The 64-bit address Ra + UR (without the immediate offset) as low and high words.</summary>
        private static (Operand Low, Operand High) GlobalBase(EmitterContext context, Sm86Inst inst, GlobalAddressFields fields)
        {
            Operand low = null;
            Operand high = Const(0);

            if (inst.Ra != RegisterConsts.RegisterZeroIndex)
            {
                low = context.Copy(Gpr(inst.Ra));
                high = inst.Bit(fields.RaWideBit) ? context.Copy(Gpr(inst.Ra + 1)) : Const(0);
            }

            int ur = fields.UrStart >= 0 ? inst.Field(fields.UrStart, 6) : RegisterConsts.UniformRegisterZeroIndex;

            if (ur != RegisterConsts.UniformRegisterZeroIndex)
            {
                Operand urLow = UGpr(ur);
                Operand urHigh = inst.Bit(fields.UrWideBit) ? UGpr(ur + 1) : Const(0);

                if (low == null)
                {
                    low = context.Copy(urLow);
                    high = context.Copy(urHigh);
                }
                else
                {
                    // Ra first and the base second: the operand order GlobalToStorage matches (as Maxwell's address + c[x][y]).
                    Operand sum = context.IAdd(low, urLow);
                    Operand carry = context.ConditionalSelect(context.ICompareLessUnsigned(sum, urLow), Const(1), Const(0));

                    high = context.IAdd(context.IAdd(high, urHigh), carry);
                    low = sum;
                }
            }

            return (low ?? Const(0), high);
        }

        /// <summary>Base + a signed immediate, with the carry (or borrow) into the high word.</summary>
        private static (Operand Low, Operand High) AddOffset(EmitterContext context, (Operand Low, Operand High) address, int offset)
        {
            if (offset == 0)
            {
                return address;
            }

            Operand low = context.IAdd(address.Low, Const(offset));
            Operand carry = context.ConditionalSelect(context.ICompareLessUnsigned(low, address.Low), Const(1), Const(0));
            Operand high = context.IAdd(address.High, offset < 0 ? context.ISubtract(carry, Const(1)) : carry);

            return (low, high);
        }

        private static int MemOffset(Sm86Inst inst) => (int)inst.SignedBits(40, 24);

        private static int WordCount(int size) => size switch
        {
            (int)LsSize2.B64 => 2,
            (int)LsSize2.B128 => 4,
            _ => 1,
        };

        private static StorageKind GlobalKind(int size) => size switch
        {
            (int)LsSize2.U8 => StorageKind.GlobalMemoryU8,
            (int)LsSize2.S8 => StorageKind.GlobalMemoryS8,
            (int)LsSize2.U16 => StorageKind.GlobalMemoryU16,
            (int)LsSize2.S16 => StorageKind.GlobalMemoryS16,
            _ => StorageKind.GlobalMemory,
        };

        private static bool CheckSize(EmitterContext context, string name, int size)
        {
            if (size > (int)LsSize2.B128)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 {name} of size {size} at 0x{context.CurrOp.Address:X} is not implemented.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// LDG d, [Ra + UR + offset] (0x981: UR in bits 32-37, Ra .E bit 90; 0x381: no UR, .E bit 72). With bit 91, bits 64-66 are a predicate
        /// stored backwards (7 - index; 0 = PT), inverted by 67: when it is false the load returns 0. Bits 77-80 (order), 84-86 (eviction) are cache
        /// hints; bits 81-83 a fault predicate (PT in MKW; set false otherwise).
        /// </summary>
        public static void Ldg(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int size = inst.Field(73, 3);

            if (!CheckSize(context, "LDG", size))
            {
                return;
            }

            GlobalAddressFields fields = inst.Opcode == 0x981 ? GlobalFields(inst, 32, 90) : new GlobalAddressFields(-1, 72, -1);
            var address = GlobalBase(context, inst, fields);
            int offset = MemOffset(inst);
            int count = WordCount(size);
            Operand[] values = new Operand[count];

            for (int index = 0; index < count; index++)
            {
                var wordAddress = AddOffset(context, address, offset + index * 4);

                values[index] = context.Load(GlobalKind(size), wordAddress.Low, wordAddress.High);
            }

            bool hasPred = inst.Opcode == 0x981 && inst.Bit(91);
            int predField = hasPred ? inst.Field(64, 3) : 0;

            if (hasPred && (predField != 0 || inst.Bit(67)))
            {
                Operand pred = PredSrc(context, 7 - predField, inst.Bit(67), false);

                for (int index = 0; index < count; index++)
                {
                    values[index] = context.ConditionalSelect(pred, values[index], Const(0));
                }
            }

            WriteFault(context, inst);
            WriteDests(context, inst.Rd, values);
        }

        /// <summary>STG [Ra + UR + offset], Rb (0x986: UR in bits 64-69, Ra .E bit 90; 0x386: no UR, .E bit 72).</summary>
        public static void Stg(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int size = inst.Field(73, 3);

            if (!CheckSize(context, "STG", size))
            {
                return;
            }

            GlobalAddressFields fields = inst.Opcode == 0x986 ? GlobalFields(inst, 64, 90) : new GlobalAddressFields(-1, 72, -1);
            var address = GlobalBase(context, inst, fields);
            int offset = MemOffset(inst);
            Operand[] data = ReadData(context, inst.Rb, WordCount(size));

            for (int index = 0; index < data.Length; index++)
            {
                var wordAddress = AddOffset(context, address, offset + index * 4);

                context.Store(GlobalKind(size), wordAddress.Low, wordAddress.High, data[index]);
            }
        }

        /// <summary>
        /// LDS d, [Ra * stride + UR + offset] (0x984; UR in bits 32-37 with bit 91) and STS [...], Rb (0x388 without UR, 0x988 with UR in bits 64-69):
        /// stride in bits 78-79 (x1, x4, x8, x16), compute programs only.
        /// </summary>
        public static void Lds(EmitterContext context) => EmitLocalOrShared(context, StorageKind.SharedMemory, "LDS", load: true, urStart: 32);

        public static void Sts(EmitterContext context) => EmitLocalOrShared(context, StorageKind.SharedMemory, "STS", load: false, urStart: 64);

        /// <summary>LDL d, [Ra + UR + offset] (0x983, UR in bits 32-37 with bit 91) and STL [...], Rb (0x387, 0x987 with UR in bits 64-69).</summary>
        public static void Ldl(EmitterContext context) => EmitLocalOrShared(context, StorageKind.LocalMemory, "LDL", load: true, urStart: 32);

        public static void Stl(EmitterContext context) => EmitLocalOrShared(context, StorageKind.LocalMemory, "STL", load: false, urStart: 64);

        private static void EmitLocalOrShared(EmitterContext context, StorageKind kind, string name, bool load, int urStart)
        {
            Sm86Inst inst = Inst(context);
            int size = inst.Field(73, 3);

            if (!CheckSize(context, name, size))
            {
                return;
            }

            if (kind == StorageKind.SharedMemory && context.TranslatorContext.Definitions.Stage != ShaderStage.Compute)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 {name} in a {context.TranslatorContext.Definitions.Stage} program at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            Operand address = LocalOrSharedAddress(context, inst, kind == StorageKind.SharedMemory ? inst.Field(78, 2) : 0, urStart);

            if (load)
            {
                InstEmit.EmitLoad(context, kind, (LsSize2)size, address, inst.Rd, MemOffset(inst));
            }
            else
            {
                InstEmit.EmitStore(context, kind, (LsSize2)size, address, inst.Rb, MemOffset(inst));
            }
        }

        /// <summary>Ra (scaled by the shared-memory stride 1, 4, 8 or 16) plus, with bit 91, the uniform register at <paramref name="urStart"/>.</summary>
        private static Operand LocalOrSharedAddress(EmitterContext context, Sm86Inst inst, int stride, int urStart)
        {
            Operand address = context.Copy(Gpr(inst.Ra));

            if (stride != 0)
            {
                address = context.ShiftLeft(address, Const(stride + 1));
            }

            if (inst.Bit(91) && inst.Field(urStart, 6) != RegisterConsts.UniformRegisterZeroIndex)
            {
                address = context.IAdd(address, UGpr(inst.Field(urStart, 6)));
            }

            return address;
        }

        // Atomics: type in bits 73-75 (U32, S32, U64, F32, F16x2, S64, F64; the same numbering as Maxwell's AtomSize), operation in 87-90 (ADD, MIN,
        // MAX, INC, DEC, AND, OR, XOR, EXCH; as Maxwell's AtomOp). The IR has 32-bit integer atomics only.

        private static bool CheckAtomic(EmitterContext context, string name, int type, int op)
        {
            bool supported = type is (int)AtomSize.U32 or (int)AtomSize.S32 &&
                op is (int)AtomOp.Add or (int)AtomOp.Min or (int)AtomOp.Max or (int)AtomOp.And or (int)AtomOp.Or or (int)AtomOp.Xor or (int)AtomOp.Exch;

            if (!supported)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 {name} {(AtomOp)op} of type {type} at 0x{context.CurrOp.Address:X} is not implemented.");
            }

            return supported;
        }

        /// <summary>
        /// ATOMG d, [Ra + UR + offset], Rb (0x9a8: UR in bits 64-69, Ra .E bit 70; 0x3a8: no UR, .E bit 72), operation in 87-90; RED [...], Rb
        /// (0x98e: the same without a destination, UR in 64-69 and Ra .E bit 90 with bit 91, operation in 87-89).
        /// </summary>
        public static void Atomg(EmitterContext context) => EmitGlobalAtomic(context, reduction: false);

        public static void Red(EmitterContext context) => EmitGlobalAtomic(context, reduction: true);

        private static void EmitGlobalAtomic(EmitterContext context, bool reduction)
        {
            Sm86Inst inst = Inst(context);
            int type = inst.Field(73, 3);
            int op = reduction ? inst.Field(87, 3) : inst.Field(87, 4);
            string name = reduction ? "RED" : "ATOMG";

            if (!CheckAtomic(context, name, type, op))
            {
                return;
            }

            GlobalAddressFields fields = GlobalFields(inst, 64, reduction ? 90 : 70);
            var address = AddOffset(context, GlobalBase(context, inst, fields), MemOffset(inst));

            Operand res = InstEmit.EmitAtomicOp(context, StorageKind.GlobalMemory, (AtomOp)op, (AtomSize)type, address.Low, address.High, context.Copy(Gpr(inst.Rb)));

            if (!reduction)
            {
                WriteFault(context, inst);
                context.Copy(Gpr(inst.Rd), res);
            }
        }

        /// <summary>ATOMS d, [Ra * stride + UR + offset], Rb (0x38c; 0x98c with UR in bits 64-69 and bit 91), operation in 87-90.</summary>
        public static void Atoms(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int type = inst.Field(73, 3);
            int op = inst.Field(87, 4);

            if (!CheckAtomic(context, "ATOMS", type, op))
            {
                return;
            }

            if (context.TranslatorContext.Definitions.Stage != ShaderStage.Compute)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 ATOMS in a {context.TranslatorContext.Definitions.Stage} program at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            Operand address = context.IAdd(LocalOrSharedAddress(context, inst, inst.Field(78, 2), 64), Const(MemOffset(inst)));
            Operand wordOffset = context.ShiftRightU32(address, Const(2));
            Operand id = Const(context.ResourceManager.SharedMemoryId);

            context.Copy(Gpr(inst.Rd), InstEmit.EmitAtomicOp(context, StorageKind.SharedMemory, (AtomOp)op, (AtomSize)type, id, wordOffset, context.Copy(Gpr(inst.Rb))));
        }

        /// <summary>The fault predicate in bits 81-83 of a global load or atomic: no access faults on the host.</summary>
        private static void WriteFault(EmitterContext context, Sm86Inst inst)
        {
            SetPredDest(context, inst.Field(81, 3), Const(IrConsts.False), false);
        }

        /// <summary>Reads <paramref name="count"/> consecutive data registers (RZ and registers past it read 0).</summary>
        private static Operand[] ReadData(EmitterContext context, int reg, int count)
        {
            Operand[] data = new Operand[count];

            for (int index = 0; index < count; index++)
            {
                data[index] = reg == RegisterConsts.RegisterZeroIndex || reg + index >= RegisterConsts.RegisterZeroIndex ? Const(0) : context.Copy(Gpr(reg + index));
            }

            return data;
        }

        /// <summary>Writes values to consecutive registers from <paramref name="reg"/> (dropped at RZ).</summary>
        private static void WriteDests(EmitterContext context, int reg, Operand[] values)
        {
            for (int index = 0; index < values.Length && reg + index < RegisterConsts.RegisterZeroIndex; index++)
            {
                context.Copy(Gpr(reg + index), values[index]);
            }
        }

        // Barriers.

        /// <summary>
        /// MEMBAR: scope in bits 76-77 (CTA, SM, GPU, system); MKW uses 0x50 in bits 72-79 before a shared-memory barrier and 0x20 (GPU). CTA and SM
        /// scopes are a workgroup memory barrier, wider ones a device memory barrier.
        /// </summary>
        public static void Membar(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            if (inst.Field(76, 2) <= 1)
            {
                context.GroupMemoryBarrier();
            }
            else
            {
                context.MemoryBarrier();
            }
        }

        /// <summary>BAR.SYNC (0xb1d with bits 72-90 zero, MKW's only form): a workgroup execution barrier. Other modes (ARV, RED, SCAN) are logged.</summary>
        public static void Bar(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);

            if (inst.Field(77, 2) != 0)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 BAR mode {inst.Field(77, 2)} at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            context.Barrier();
        }
    }
}
