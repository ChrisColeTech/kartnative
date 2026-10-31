using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.Instructions;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ryujinx.Graphics.Shader.Sm86
{
    /// <summary>
    /// Builds Ryujinx's decoded program (functions of address-sorted blocks of InstOp) from SM86 code, as Decoder does for Maxwell: blocks end at
    /// control flow (Sm86InstProps.BlockEnd), branch targets start blocks, calls start functions, BRX targets come from the jump table in c[1].
    /// Addresses are byte offsets from the first instruction (after the 128-byte program header of graphics stages).
    /// </summary>
    static class Sm86Decoder
    {
        public const int InstSize = 16;

        /// <summary>Graphics programs start with the 128-byte NVN2 program header (SPH version 4; its first 0x50 bytes are the Maxwell SPH layout).</summary>
        public const int GraphicsHeaderSize = 0x80;

        public static DecodedProgram Decode(ShaderDefinitions definitions, IGpuAccessor gpuAccessor, ulong startAddress)
        {
            Decoder.Context context = new(gpuAccessor);
            Queue<DecodedFunction> functionsQueue = new();
            Dictionary<ulong, DecodedFunction> functionsVisited = new();

            DecodedFunction EnqueueFunction(ulong address)
            {
                if (!functionsVisited.TryGetValue(address, out DecodedFunction function))
                {
                    functionsVisited.Add(address, function = new DecodedFunction(address));
                    functionsQueue.Enqueue(function);
                }

                return function;
            }

            DecodedFunction mainFunction = EnqueueFunction(0);

            while (functionsQueue.TryDequeue(out DecodedFunction currentFunction))
            {
                List<Block> blocks = [];
                Queue<Block> workQueue = new();
                Dictionary<ulong, Block> visited = new();

                Block GetBlock(ulong blkAddress)
                {
                    if (!visited.TryGetValue(blkAddress, out Block block))
                    {
                        block = new Block(blkAddress);

                        workQueue.Enqueue(block);
                        visited.Add(blkAddress, block);
                    }

                    return block;
                }

                GetBlock(currentFunction.Address);

                bool hasNewTarget;

                do
                {
                    while (workQueue.TryDequeue(out Block currBlock))
                    {
                        if (Decoder.BinarySearch(blocks, currBlock.Address, out int nBlkIndex))
                        {
                            Block nBlock = blocks[nBlkIndex];

                            if (nBlock.Address == currBlock.Address)
                            {
                                throw new InvalidOperationException("Found duplicate block address on the list.");
                            }

                            if (currBlock.Address % InstSize != nBlock.Address % InstSize)
                            {
                                throw new InvalidOperationException($"SM86 branch target 0x{currBlock.Address:x} is not on an instruction boundary.");
                            }

                            nBlock.Split(currBlock);
                            blocks.Insert(nBlkIndex + 1, currBlock);

                            continue;
                        }

                        ulong limitAddress = ulong.MaxValue;

                        if (nBlkIndex != blocks.Count)
                        {
                            Block nBlock = blocks[nBlkIndex];

                            int nextIndex = nBlkIndex + 1;

                            if (nBlock.Address < currBlock.Address && nextIndex < blocks.Count)
                            {
                                limitAddress = blocks[nextIndex].Address;
                            }
                            else if (nBlock.Address > currBlock.Address)
                            {
                                limitAddress = blocks[nBlkIndex].Address;
                            }
                        }

                        FillBlock(definitions, gpuAccessor, context, currBlock, limitAddress, startAddress);

                        if (currBlock.OpCodes.Count != 0)
                        {
                            InstOp lastOp = currBlock.GetLastOp();
                            Sm86Inst last = Inst(lastOp);
                            Sm86InstProps props = Sm86InstTable.Get(last.Opcode).Props;

                            if (!last.NeverExecutes)
                            {
                                if (props.HasFlag(Sm86InstProps.Call) && lastOp.Emitter != Sm86Emit.CallAsJump)
                                {
                                    EnqueueFunction(BranchTarget(lastOp)).AddCaller(currentFunction);
                                }
                                else if (props.HasFlag(Sm86InstProps.Branch) || props.HasFlag(Sm86InstProps.Call))
                                {
                                    Block succBlock = GetBlock(BranchTarget(lastOp));
                                    currBlock.Successors.Add(succBlock);
                                    succBlock.Predecessors.Add(currBlock);
                                }
                            }

                            if (HasNext(currBlock))
                            {
                                Block succBlock = GetBlock(currBlock.EndAddress);
                                currBlock.Successors.Insert(0, succBlock);
                                succBlock.Predecessors.Add(currBlock);
                            }
                        }

                        if (blocks.Count != 0)
                        {
                            Block nBlock = blocks[nBlkIndex];

                            blocks.Insert(nBlkIndex + (nBlock.Address < currBlock.Address ? 1 : 0), currBlock);
                        }
                        else
                        {
                            blocks.Add(currBlock);
                        }
                    }

                    hasNewTarget = Sm86Flow.FindBrxTargets(context, gpuAccessor, blocks, GetBlock);
                }
                while (hasNewTarget);

                currentFunction.SetBlocks(blocks.ToArray());
            }

            return new DecodedProgram(
                mainFunction,
                functionsVisited,
                context.AttributeUsage,
                context.UsedFeatures,
                context.ClipDistancesWritten,
                context.Cb1DataSize);
        }

        public static Sm86Inst Inst(InstOp op) => new(op.RawOpCode, op.RawOpCodeHi);

        /// <summary>Target of a relative branch (BRA, CALL, BRX base, RET base): the next instruction plus the signed word offset in bits 34-81.</summary>
        public static ulong BranchTarget(InstOp op)
        {
            return (ulong)((long)op.Address + InstSize + Inst(op).SignedBits(34, 48) * 4);
        }

        /// <summary>True when the guard and the second condition (bits 87-90) are both PT.</summary>
        public static bool IsUnconditional(Sm86Inst inst)
        {
            return inst.Pred == 7 && !inst.PredInv && inst.Pred87 == 7 && !inst.Pred87Inv;
        }

        /// <summary>Whether execution can continue at the block's end address (the SM86 counterpart of Block.HasNext).</summary>
        public static bool HasNext(Block block)
        {
            if (block.OpCodes.Count == 0)
            {
                return false;
            }

            InstOp lastOp = block.GetLastOp();
            Sm86Inst inst = Inst(lastOp);
            Sm86InstProps props = Sm86InstTable.Get(inst.Opcode).Props;

            if (inst.NeverExecutes || !IsUnconditional(inst))
            {
                return true;
            }

            if (props.HasFlag(Sm86InstProps.Call))
            {
                // A function call returns to the next instruction; a call the decoder made a jump does not.
                return lastOp.Emitter != Sm86Emit.CallAsJump;
            }

            return (props & (Sm86InstProps.Branch | Sm86InstProps.Exit | Sm86InstProps.Ret | Sm86InstProps.Brx)) == 0;
        }

        private static void FillBlock(
            ShaderDefinitions definitions,
            IGpuAccessor gpuAccessor,
            Decoder.Context context,
            Block block,
            ulong limitAddress,
            ulong startAddress)
        {
            ulong address = block.Address;

            while (address + InstSize <= limitAddress)
            {
                ReadOnlySpan<ulong> words = gpuAccessor.GetCode(startAddress + address, InstSize);

                if (words.Length < 2)
                {
                    // The code ends here (a dump that stops after the last instruction the compiler wrote).
                    break;
                }

                Sm86Inst inst = new(words[0], words[1]);
                Sm86InstInfo info = Sm86InstTable.Get(inst.Opcode);
                InstEmitter emitter = info.Emitter;

                if (!inst.NeverExecutes)
                {
                    context.SetUsedFeature(info.Features);
                    RecordAttributeUse(definitions, context, inst, info.Props);

                    if (info.Props.HasFlag(Sm86InstProps.Call) && !Sm86Flow.IsFunctionCall(gpuAccessor, startAddress, address, inst))
                    {
                        emitter = Sm86Emit.CallAsJump;
                    }
                }

                block.OpCodes.Add(new InstOp(address, inst.Lo, inst.Hi, emitter));

                address += InstSize;

                if (!inst.NeverExecutes && (info.Props & Sm86InstProps.BlockEnd) != 0)
                {
                    break;
                }
            }

            block.EndAddress = address;
        }

        private static void RecordAttributeUse(ShaderDefinitions definitions, Decoder.Context context, Sm86Inst inst, Sm86InstProps props)
        {
            // ALD / AST: attribute byte address in bits 40-49, component count - 1 in 74-75, per-patch 76, physical (indexed) 77, ALD output 79.
            if (props.HasFlag(Sm86InstProps.AttrLoad))
            {
                Decoder.SetUserAttributeUses(definitions, context, inst.Field(40, 10), inst.Field(74, 2) + 1, inst.Bit(79), inst.Bit(77), inst.Bit(76));
            }
            else if (props.HasFlag(Sm86InstProps.AttrStore))
            {
                Decoder.SetUserAttributeUses(definitions, context, inst.Field(40, 10), inst.Field(74, 2) + 1, true, inst.Bit(77), inst.Bit(76));
            }
            else if (props.HasFlag(Sm86InstProps.Ipa))
            {
                // IPA: attribute word address in bits 64-71; bit 91 adds the uniform register in bits 24-29 (an indexed access, opcode 0x526).
                Decoder.SetUserAttributeUses(definitions, context, inst.Field(64, 8) * 4, 1, false, inst.Bit(91), false);
            }
        }
    }
}
