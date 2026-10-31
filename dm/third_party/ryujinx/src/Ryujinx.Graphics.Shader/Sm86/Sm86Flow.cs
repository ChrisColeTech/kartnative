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
        public const int OpBraU = 0x547;
        public const int OpBrxu = 0x958;
        public const int OpUldcIndexed = 0xabb;
        public const int OpUshfImm = 0x899;
        public const int OpUimadImm = 0x8a4;
        public const int OpUselImm = 0x887;
        public const int OpUisetpImm = 0x88c;

        /// <summary>BRXU: BRX with the index in the uniform register in bits 24-29.</summary>
        public static bool IsBrxu(Sm86Inst inst) => inst.Opcode == OpBrxu;

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
        /// and of BRXU, whose chain is uniform (MKW's one BRXU program, 112ae71940e57e09):
        ///   UISETP.LT.U32 UPp, URy, UpperBound; USEL URx, URy, UpperBound, UPp  (count = UpperBound + 1)
        ///   USHF.L.U32 URz, URx, 0x2, URZ  (or UIMAD URz, URx, 0x4, URZ)
        ///   ULDC URw, c[0x1][URz + Base]  (0xabb)
        ///   BRXU URw + base
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

                (int cbOffset, int count) = IsBrxu(brx) ? FindUniformTableRange(block, brx.URa) : FindTableRange(block, brx.Ra);

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

        /// <summary>BRXU's chain (see FindBrxTargets): ULDC c[0x1][URz + Base] &lt;- USHF.L / UIMAD by 4 &lt;- USEL clamp of a UISETP.LT/LE.U32.</summary>
        private static (int, int) FindUniformTableRange(Block block, int brxReg)
        {
            HashSet<Block> visited = [];

            (Block ldcBlock, int ldcIndex) = FindFirstWrite(visited, block, block.OpCodes.Count - 1, inst => WritesUr(inst, brxReg));

            if (ldcBlock == null)
            {
                return (0, 0);
            }

            Sm86Inst ldc = Sm86Decoder.Inst(ldcBlock.OpCodes[ldcIndex]);

            // ULDC.32 c[0x1][URa + offset].
            if (ldc.Opcode != OpUldcIndexed || ldc.Field(54, 5) != 1 || ldc.Field(73, 3) != 4)
            {
                return (0, 0);
            }

            int cbOffset = (int)ldc.SignedBits(38, 16);

            if (ldc.URa == RegisterConsts.UniformRegisterZeroIndex)
            {
                return (cbOffset, 1);
            }

            (Block shlBlock, int shlIndex) = FindFirstWrite(visited, ldcBlock, ldcIndex, inst => WritesUr(inst, ldc.URa));

            if (shlBlock == null)
            {
                return (0, 0);
            }

            Sm86Inst shl = Sm86Decoder.Inst(shlBlock.OpCodes[shlIndex]);

            bool isImadShl = shl.Opcode == OpUimadImm && shl.Imm32 == 4;
            bool isShfShl = shl.Opcode == OpUshfImm && shl.Imm32 == 2 && !shl.Bit(76) && !shl.Bit(80);

            if (!(isImadShl || isShfShl) || shl.URc != RegisterConsts.UniformRegisterZeroIndex)
            {
                return (0, 0);
            }

            (Block selBlock, int selIndex) = FindFirstWrite(visited, shlBlock, shlIndex, inst => WritesUr(inst, shl.URa));

            if (selBlock == null)
            {
                return (0, 0);
            }

            Sm86Inst sel = Sm86Decoder.Inst(selBlock.OpCodes[selIndex]);

            // USEL URx, URy, Bound, UPp: URy when UPp, else the bound.
            if (sel.Opcode != OpUselImm || sel.Pred87Inv || sel.Pred87 == 7)
            {
                return (0, 0);
            }

            int bound = (int)sel.Imm32;
            (Block cmpBlock, int cmpIndex) = FindFirstWrite([], selBlock, selIndex, inst => inst.Opcode == OpUisetpImm && inst.Field(81, 3) == sel.Pred87);

            if (cmpBlock == null)
            {
                return (0, 0);
            }

            Sm86Inst cmp = Sm86Decoder.Inst(cmpBlock.OpCodes[cmpIndex]);
            int cmpOp = cmp.Field(76, 3);

            // UISETP.LT.U32 or .LE.U32 UPp, URy, Bound, combined with UPT (bits 87-90) by AND (bits 74-75).
            if (cmp.URa != sel.URa || cmp.Imm32 != (uint)bound || cmp.Bit(73) || (cmpOp != 1 && cmpOp != 3) ||
                cmp.Field(74, 2) != 0 || cmp.Pred87 != 7 || cmp.Pred87Inv)
            {
                return (0, 0);
            }

            return (cbOffset, bound + 1);
        }

        /// <summary>Whether an instruction writes uniform register <paramref name="index"/> (a uniform-datapath Rd row, R2UR, REDUX).</summary>
        private static bool WritesUr(Sm86Inst inst, int index)
        {
            Sm86InstProps props = Sm86InstTable.Get(inst.Opcode).Props;
            bool writes = props.HasFlag(Sm86InstProps.URd) || (props.HasFlag(Sm86InstProps.Uniform) && props.HasFlag(Sm86InstProps.Rd));

            return writes && inst.URd == index;
        }

        private static (Block, int) FindFirstRegWrite(HashSet<Block> visited, Block block, int index, int regIndex)
        {
            return FindFirstWrite(visited, block, index, inst =>
                !Sm86InstTable.Get(inst.Opcode).Props.HasFlag(Sm86InstProps.Uniform) &&
                Sm86InstTable.Get(inst.Opcode).Props.HasFlag(Sm86InstProps.Rd) &&
                inst.Rd == regIndex);
        }

        /// <summary>The last instruction before <paramref name="index"/> in <paramref name="block"/>, or in its predecessors, that <paramref name="writes"/> matches.</summary>
        private static (Block, int) FindFirstWrite(HashSet<Block> visited, Block block, int index, Func<Sm86Inst, bool> writes)
        {
            Queue<(Block, int)> toVisit = new();
            toVisit.Enqueue((block, index));
            visited.Add(block);

            while (toVisit.TryDequeue(out (Block Block, int Index) current))
            {
                for (int i = current.Index - 1; i >= 0; i--)
                {
                    Sm86Inst inst = Sm86Decoder.Inst(current.Block.OpCodes[i]);

                    if (!inst.NeverExecutes && writes(inst))
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
