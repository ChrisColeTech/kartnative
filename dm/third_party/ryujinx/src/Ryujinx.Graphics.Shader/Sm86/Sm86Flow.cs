using Ryujinx.Graphics.Shader.Decoders;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Shader.Sm86
{
    /// <summary>Decode-time control-flow analysis of SM86 code: which CALLs are function calls, and the targets of BRX jump tables.</summary>
    static class Sm86Flow
    {
        public const int OpMovImm = 0x802;
        public const int OpLdc = 0xb82;
        public const int OpImadImm = 0x824;
        public const int OpImnmxImm = 0x817;
        public const int OpShfImm = 0x819;

        /// <summary>
        /// CALL.REL (bit 86 clear) pushes a return address: a function call. CALL.REL.NOINC (bit 86 set) only jumps; the compiler uses it as a call when the
        /// instruction before it moves the return address (the next instruction's byte offset) into the link register that the callee's RET.REL.NODEC reads,
        /// and as a plain forward jump otherwise (the loop-exit pattern "@P CALL.REL.NOINC +16; BRA ..."). Only the first becomes a Ryujinx function.
        /// </summary>
        public static bool IsFunctionCall(IGpuAccessor gpuAccessor, ulong startAddress, ulong address, Sm86Inst call)
        {
            if (!call.Bit(86))
            {
                return true;
            }

            if (address < Sm86Decoder.InstSize)
            {
                return false;
            }

            ReadOnlySpan<ulong> words = gpuAccessor.GetCode(startAddress + address - Sm86Decoder.InstSize, Sm86Decoder.InstSize);

            if (words.Length < 2)
            {
                return false;
            }

            Sm86Inst prev = new(words[0], words[1]);

            return prev.Opcode == OpMovImm &&
                   prev.Pred == 7 && !prev.PredInv &&
                   prev.Rd != RegisterConsts.RegisterZeroIndex &&
                   prev.Imm32 == address + Sm86Decoder.InstSize;
        }

        /// <summary>
        /// Adds the targets of BRX instructions whose block has none yet, from the jump table the index is loaded from:
        ///   IMNMX.U32 Rx, Ry, UpperBound  (count = UpperBound + 1)
        ///   SHF.L.U32 Rz, Rx, 0x2, RZ    (or IMAD.SHL.U32 Rz, Rx, 0x4, RZ)
        ///   LDC Rw, c[0x1][Rz + Base]
        ///   BRX Rw + base
        /// Table entries are byte offsets added to the BRX base (the compiler makes the base the program start).
        /// </summary>
        public static bool FindBrxTargets(Decoder.Context context, IGpuAccessor gpuAccessor, List<Block> blocks, Func<ulong, Block> getBlock)
        {
            bool hasNewTarget = false;

            foreach (Block block in blocks.ToArray())
            {
                if (block.OpCodes.Count == 0)
                {
                    continue;
                }

                InstOp lastOp = block.GetLastOp();
                Sm86Inst brx = Sm86Decoder.Inst(lastOp);

                if (brx.NeverExecutes || !Sm86InstTable.Get(brx.Opcode).Props.HasFlag(Sm86InstProps.Brx))
                {
                    continue;
                }

                bool hasNext = Sm86Decoder.HasNext(block);

                if (block.Successors.Count != (hasNext ? 1 : 0))
                {
                    continue;
                }

                (int cbOffset, int count) = FindTableRange(block, brx.Ra);

                if (count == 0)
                {
                    gpuAccessor.Log($"SM86 BRX at 0x{lastOp.Address:X}: jump table not found.");
                    continue;
                }

                hasNewTarget = true;

                ulong baseAddress = Sm86Decoder.BranchTarget(lastOp);
                HashSet<ulong> visited = [];

                for (int i = 0; i < count; i++)
                {
                    ulong target = baseAddress + context.ConstantBuffer1Read(cbOffset + i * 4);

                    if (visited.Add(target))
                    {
                        Block targetBlock = getBlock(target);
                        targetBlock.Predecessors.Add(block);
                        block.Successors.Add(targetBlock);
                    }
                }
            }

            return hasNewTarget;
        }

        private static (int, int) FindTableRange(Block block, int brxReg)
        {
            HashSet<Block> visited = [];

            (Block ldcBlock, int ldcIndex) = FindFirstRegWrite(visited, block, block.OpCodes.Count - 1, brxReg);

            if (ldcBlock == null)
            {
                return (0, 0);
            }

            Sm86Inst ldc = Sm86Decoder.Inst(ldcBlock.OpCodes[ldcIndex]);

            // LDC.32, bound bank 1, no segmented/linear mode.
            if (ldc.Opcode != OpLdc || ldc.Bit(91) || ldc.Field(54, 5) != 1 || ldc.Field(73, 3) != 4 || ldc.Field(78, 2) != 0)
            {
                return (0, 0);
            }

            int cbOffset = (int)ldc.SignedBits(38, 16);

            if (ldc.Ra == RegisterConsts.RegisterZeroIndex)
            {
                return (cbOffset, 1);
            }

            (Block shlBlock, int shlIndex) = FindFirstRegWrite(visited, ldcBlock, ldcIndex, ldc.Ra);

            if (shlBlock == null)
            {
                return (0, 0);
            }

            Sm86Inst shl = Sm86Decoder.Inst(shlBlock.OpCodes[shlIndex]);

            bool isImadShl = shl.Opcode == OpImadImm && shl.Imm32 == 4;
            bool isShfShl = shl.Opcode == OpShfImm && shl.Imm32 == 2 && !shl.Bit(76) && !shl.Bit(80);

            if (!(isImadShl || isShfShl) || shl.Rc != RegisterConsts.RegisterZeroIndex)
            {
                return (0, 0);
            }

            (Block minBlock, int minIndex) = FindFirstRegWrite(visited, shlBlock, shlIndex, shl.Ra);

            if (minBlock == null)
            {
                return (0, 0);
            }

            Sm86Inst min = Sm86Decoder.Inst(minBlock.OpCodes[minIndex]);

            // IMNMX.U32 with PT (minimum): bit 73 clear is unsigned.
            if (min.Opcode != OpImnmxImm || min.Bit(73) || min.Pred87 != 7 || min.Pred87Inv)
            {
                return (0, 0);
            }

            return (cbOffset, (int)min.Imm32 + 1);
        }

        private static (Block, int) FindFirstRegWrite(HashSet<Block> visited, Block block, int index, int regIndex)
        {
            Queue<(Block, int)> toVisit = new();
            toVisit.Enqueue((block, index));
            visited.Add(block);

            while (toVisit.TryDequeue(out (Block Block, int Index) current))
            {
                for (int i = current.Index - 1; i >= 0; i--)
                {
                    Sm86Inst inst = Sm86Decoder.Inst(current.Block.OpCodes[i]);

                    if (!inst.NeverExecutes && Sm86InstTable.Get(inst.Opcode).Props.HasFlag(Sm86InstProps.Rd) && inst.Rd == regIndex)
                    {
                        return (current.Block, i);
                    }
                }

                foreach (Block predecessor in current.Block.Predecessors)
                {
                    if (visited.Add(predecessor))
                    {
                        toVisit.Enqueue((predecessor, predecessor.OpCodes.Count));
                    }
                }
            }

            return (null, 0);
        }
    }
}
