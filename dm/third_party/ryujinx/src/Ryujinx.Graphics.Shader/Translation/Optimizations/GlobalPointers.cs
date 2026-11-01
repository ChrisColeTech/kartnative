using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Translation.Optimizations
{
    /// <summary>
    /// nxnative: global memory accessed through a pointer the program read from memory (GlobalToStorage finds no constant buffer holding its storage
    /// buffer's base, so no storage buffer binding can stand for it). Guest GPU addresses are the CPU addresses of the memory pools the game created,
    /// and the host wraps each pool as a Metal buffer at a GPU address of its own, so the access goes through a table the host fills and binds
    /// (IGpuAccessor.QueryGlobalPointerTable; dm/gfx/nvn/dmsh_abi.h DMSH_GPTR_*, nvn_mtl.m gptr_table): the guest address is looked up, translated to
    /// the host address and accessed through a PhysicalStorageBuffer pointer (StorageKind.PhysicalMemory). An address outside every entry is refused:
    /// a load reads 0, a store or atomic does nothing, and the table's miss counter and last missed address tell the host, which logs it.
    ///
    /// The table, 32-bit words: a header of HeaderWords words, [0] = SearchSteps - log2(entries), [1] = misses, [2], [3] = the last missed address
    /// (low, high), then a power of two of entries of EntryWords words, sorted by start and not overlapping, the last one a sentinel starting at
    /// 2^64 - 1: [0..1] start, [2..3] end (exclusive), [4..5] host address - guest address (mod 2^64), [6..7] 0. The lookup is a branchless binary
    /// search of SearchSteps fixed steps (a step past the table's size adds 0), so the table holds at most 2^SearchSteps entries.
    /// </summary>
    static class GlobalPointers
    {
        public const string TableName = "dm_gptr";
        public const int HeaderWords = 4;
        public const int EntryWords = 8;
        public const int SearchSteps = 16;

        /// <summary>
        /// The helper function a pointer access calls: arguments (address low, address high[, compare], [value]) as GlobalToStorage's multi-target
        /// functions take them. Null when the access cannot be translated (a small integer atomic), after logging why.
        /// </summary>
        public static Function GenerateFunction(ResourceManager resourceManager, IGpuAccessor gpuAccessor, Operation operation, string name)
        {
            Instruction inst = operation.Inst;
            StorageKind kind = operation.StorageKind;
            bool atomic = inst.IsAtomic();
            bool store = inst == Instruction.Store;
            int bitSize = kind switch
            {
                StorageKind.GlobalMemoryS8 or StorageKind.GlobalMemoryU8 => 8,
                StorageKind.GlobalMemoryS16 or StorageKind.GlobalMemoryU16 => 16,
                _ => 32,
            };

            if (atomic && bitSize != 32)
            {
                gpuAccessor.Log($"Global memory atomic {inst} of {kind} through a pointer is not implemented.");
                return null;
            }

            int inArgumentsCount = inst == Instruction.AtomicCompareAndSwap ? 4 : store || atomic ? 3 : 2;
            bool returnsValue = operation.Dest != null;
            int table = resourceManager.GetGlobalPointerTableBinding();

            EmitterContext context = new();

            Operand addressLow = Argument(0);
            Operand addressHigh = Argument(1);
            Operand wordLow = context.BitwiseAnd(addressLow, Const(~3));

            // pos = the number of entries starting at or below the word's address
            Operand shift = context.Load(StorageKind.StorageBuffer, table, Const(0), Const(0));
            Operand pos = Const(0);

            for (int step = 0; step < SearchSteps; step++)
            {
                Operand half = context.ShiftRightU32(Const((1 << (SearchSteps - 1)) >> step), shift);
                Operand probe = context.IAdd(pos, half);
                Operand index = context.ISubtract(context.IMaximumU32(probe, Const(1)), Const(1));
                Operand atOrBelow = LessOrEqual64(context, EntryWord(context, table, index, 0), EntryWord(context, table, index, 1), wordLow, addressHigh);
                Operand take = context.BitwiseAnd(atOrBelow, context.ICompareNotEqual(half, Const(0)));
                pos = context.ConditionalSelect(take, probe, pos);
            }

            Operand entry = context.ISubtract(context.IMaximumU32(pos, Const(1)), Const(1));
            Operand inside = LessThan64(context, context.BitwiseOr(wordLow, Const(3)), addressHigh, EntryWord(context, table, entry, 2), EntryWord(context, table, entry, 3));
            Operand found = context.BitwiseAnd(context.ICompareNotEqual(pos, Const(0)), inside);

            Operand lblFound = Label();
            context.BranchIfTrue(lblFound, found);

            context.AtomicAdd(StorageKind.StorageBuffer, table, Const(0), Const(1), Const(1));
            context.Store(StorageKind.StorageBuffer, table, Const(0), Const(2), addressLow);
            context.Store(StorageKind.StorageBuffer, table, Const(0), Const(3), addressHigh);

            if (returnsValue)
            {
                context.Return(Const(0));
            }
            else
            {
                context.Return();
            }

            context.MarkLabel(lblFound);

            Operand hostLow = context.IAdd(wordLow, EntryWord(context, table, entry, 4));
            Operand carry = context.ConditionalSelect(context.ICompareLessUnsigned(hostLow, wordLow), Const(1), Const(0));
            Operand hostHigh = context.IAdd(context.IAdd(addressHigh, EntryWord(context, table, entry, 5)), carry);

            Operand result = null;

            if (atomic)
            {
                Operand value = Argument(inArgumentsCount - 1);

                result = inst switch
                {
                    Instruction.AtomicAdd => context.AtomicAdd(StorageKind.PhysicalMemory, hostLow, hostHigh, value),
                    Instruction.AtomicAnd => context.AtomicAnd(StorageKind.PhysicalMemory, hostLow, hostHigh, value),
                    Instruction.AtomicCompareAndSwap => context.AtomicCompareAndSwap(StorageKind.PhysicalMemory, hostLow, hostHigh, Argument(2), value),
                    Instruction.AtomicMaxS32 => context.AtomicMaxS32(StorageKind.PhysicalMemory, hostLow, hostHigh, value),
                    Instruction.AtomicMaxU32 => context.AtomicMaxU32(StorageKind.PhysicalMemory, hostLow, hostHigh, value),
                    Instruction.AtomicMinS32 => context.AtomicMinS32(StorageKind.PhysicalMemory, hostLow, hostHigh, value),
                    Instruction.AtomicMinU32 => context.AtomicMinU32(StorageKind.PhysicalMemory, hostLow, hostHigh, value),
                    Instruction.AtomicOr => context.AtomicOr(StorageKind.PhysicalMemory, hostLow, hostHigh, value),
                    Instruction.AtomicSwap => context.AtomicSwap(StorageKind.PhysicalMemory, hostLow, hostHigh, value),
                    Instruction.AtomicXor => context.AtomicXor(StorageKind.PhysicalMemory, hostLow, hostHigh, value),
                    _ => null,
                };

                if (result == null)
                {
                    gpuAccessor.Log($"Global memory atomic {inst} through a pointer is not implemented.");
                    return null;
                }
            }
            else if (store)
            {
                Operand value = Argument(2);

                if (bitSize < 32)
                {
                    // as GlobalToStorage's storage buffer stores: the word's other bytes may change meanwhile, so a compare-and-swap loop
                    Operand bitOffset = HelperFunctionManager.GetBitOffset(context, addressLow);
                    Operand lblLoop = Label();

                    context.MarkLabel(lblLoop);

                    Operand oldValue = context.Load(StorageKind.PhysicalMemory, hostLow, hostHigh);
                    Operand newValue = context.BitfieldInsert(oldValue, value, bitOffset, Const(bitSize));
                    Operand casResult = context.AtomicCompareAndSwap(StorageKind.PhysicalMemory, hostLow, hostHigh, oldValue, newValue);

                    context.BranchIfTrue(lblLoop, context.ICompareNotEqual(casResult, oldValue));
                }
                else
                {
                    context.Store(StorageKind.PhysicalMemory, hostLow, hostHigh, value);
                }
            }
            else
            {
                result = context.Load(StorageKind.PhysicalMemory, hostLow, hostHigh);

                if (bitSize < 32)
                {
                    Operand bitOffset = HelperFunctionManager.GetBitOffset(context, addressLow);

                    result = kind switch
                    {
                        StorageKind.GlobalMemoryS8 => context.BitfieldExtractS32(context.ShiftRightS32(result, bitOffset), Const(0), Const(8)),
                        StorageKind.GlobalMemoryS16 => context.BitfieldExtractS32(context.ShiftRightS32(result, bitOffset), Const(0), Const(16)),
                        StorageKind.GlobalMemoryU8 => context.BitwiseAnd(context.ShiftRightU32(result, bitOffset), Const(byte.MaxValue)),
                        _ => context.BitwiseAnd(context.ShiftRightU32(result, bitOffset), Const(ushort.MaxValue)),
                    };
                }
            }

            if (returnsValue)
            {
                context.Return(result ?? Const(0));
            }
            else
            {
                context.Return();
            }

            return new Function(ControlFlowGraph.Create(context.GetOperations()).Blocks, name, returnsValue, inArgumentsCount, 0);
        }

        // Word w of entry index
        private static Operand EntryWord(EmitterContext context, int table, Operand index, int w)
        {
            return context.Load(StorageKind.StorageBuffer, table, Const(0), context.IAdd(context.ShiftLeft(index, Const(3)), Const(HeaderWords + w)));
        }

        // Unsigned 64-bit a <= b and a < b on (low, high) pairs
        private static Operand LessOrEqual64(EmitterContext context, Operand aLow, Operand aHigh, Operand bLow, Operand bHigh)
        {
            return context.BitwiseOr(context.ICompareLessUnsigned(aHigh, bHigh), context.BitwiseAnd(context.ICompareEqual(aHigh, bHigh), context.ICompareLessOrEqualUnsigned(aLow, bLow)));
        }

        private static Operand LessThan64(EmitterContext context, Operand aLow, Operand aHigh, Operand bLow, Operand bHigh)
        {
            return context.BitwiseOr(context.ICompareLessUnsigned(aHigh, bHigh), context.BitwiseAnd(context.ICompareEqual(aHigh, bHigh), context.ICompareLessUnsigned(aLow, bLow)));
        }
    }
}
