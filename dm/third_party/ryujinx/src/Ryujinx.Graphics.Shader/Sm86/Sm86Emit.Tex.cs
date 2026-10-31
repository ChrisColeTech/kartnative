using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.Instructions;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using System.Collections.Generic;
using System.Numerics;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Sm86
{
    /// <summary>
    /// Texture operations with the handle in a constant buffer (Mesa NAK sm70_encode.rs OpTex / OpTld / OpTld4 / OpTmml / OpTxd / OpTxq; source
    /// packing nak_nir_lower_tex.c). Shared fields: handle word offset in bits 40-53 and its bank in 54-58 (MKW: bank 2 in graphics programs, bank 0
    /// in compute), scalar source packing 60, dimension 61-63 (0 1D, 1 2D, 2 3D, 3 cube, 4 1D array, 5 2D array, 7 cube array), component mask
    /// 72-75, FP16 results 79 (TEX, TLD, TLD4, TXD), a fault predicate 81-83. Sources: Ra on holds the A list ([array index] coordinates; TXD:
    /// coordinates [array index | offsets]), Rb on the B list. Results: the first two components to Rd, Rd+1, the next two to Rd2 (bits 64-71), Rd2+1;
    /// with FP16 results, components 0-1 are packed into Rd and 2-3 into Rd2.
    /// </summary>
    static partial class Sm86Emit
    {
        private static SamplerType TextureType(Sm86Inst inst) => inst.Field(61, 3) switch
        {
            0 => SamplerType.Texture1D,
            1 => SamplerType.Texture2D,
            2 => SamplerType.Texture3D,
            3 => SamplerType.TextureCube,
            4 => SamplerType.Texture1D | SamplerType.Array,
            5 => SamplerType.Texture2D | SamplerType.Array,
            7 => SamplerType.TextureCube | SamplerType.Array,
            _ => SamplerType.None,
        };

        private static int TextureHandle(Sm86Inst inst) => inst.Field(40, 14);

        private static int TextureBank(Sm86Inst inst) => inst.Field(54, 5);

        private static bool CheckTextureType(EmitterContext context, Sm86Inst inst, string name, SamplerType type)
        {
            if (type == SamplerType.None)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 {name} with dimension {inst.Field(61, 3)} at 0x{context.CurrOp.Address:X} is not implemented.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// TEX (0xb60): d = sample(texture). Bits: AOFFI 76, NDV 77, depth compare 78, LOD mode 87-89 (auto, zero, bias, lod, clamp, bias + clamp).
        /// Sources, as on Maxwell: A [array index] coordinates, B [lod or bias] [packed offsets, 4 bits each] [depth reference]. With the scalar bit,
        /// the same list of at most four values is split into the first two (one when there are two) from Ra on and the rest from Rb on (Mesa
        /// nak_nir_lower_tex.c, try_scalarize_tex).
        /// </summary>
        public static void Tex(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            SamplerType type = TextureType(inst);

            if (!CheckTextureType(context, inst, "TEX", type))
            {
                return;
            }

            int lodMode = inst.Field(87, 3);
            bool hasDepthCompare = inst.Bit(78);
            bool hasOffset = inst.Bit(76);

            if (lodMode >= 4)
            {
                // The IR has no minimum-LOD source (NAK packs it with the array index); MKW has no such TEX.
                context.TranslatorContext.GpuAccessor.Log($"SM86 TEX with a LOD clamp at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            bool isArray = type.HasFlag(SamplerType.Array);
            int coordsCount = type.Dimensions;
            bool hasLodValue = lodMode is 2 or 3;

            int countA = (isArray ? 1 : 0) + coordsCount;
            int countB = (hasLodValue ? 1 : 0) + (hasOffset ? 1 : 0) + (hasDepthCompare ? 1 : 0);
            Operand[] values = ReadTextureSources(context, inst, countA, countB, inst.Bit(60));

            int next = 0;
            Operand arrayIndex = isArray ? values[next++] : null;

            List<Operand> sources = [];
            TextureFlags flags = TextureFlags.None;

            for (int i = 0; i < coordsCount; i++)
            {
                sources.Add(values[next++]);
            }

            bool is1DTo2D = Add1DAs2DCoordinate(sources, ref type, ConstF(0));

            if (isArray)
            {
                sources.Add(arrayIndex);
            }

            Operand lodValue = hasLodValue ? values[next++] : ConstF(0);
            Operand packedOffsets = hasOffset ? values[next++] : null;

            if (hasDepthCompare)
            {
                sources.Add(values[next++]);
                type |= SamplerType.Shadow;
            }

            if (lodMode is 1 or 3)
            {
                sources.Add(lodValue);
                flags |= TextureFlags.LodLevel;
            }

            if (hasOffset)
            {
                AddPackedOffsets(context, sources, packedOffsets, 0, coordsCount, is1DTo2D);
                flags |= TextureFlags.Offset;
            }

            if (lodMode == 2)
            {
                sources.Add(lodValue);
                flags |= TextureFlags.LodBias;
            }

            EmitTextureSample(context, inst, Instruction.TextureSample, type, flags, sources);
        }

        /// <summary>
        /// TLD (0xb66): texel fetch with integer coordinates. LOD mode 87-89 is explicit (zero or lod), AOFFI 76, multisample 78. Sources: A [array
        /// index] coordinates, B [lod] [packed offsets] [sample index]; scalar split as TEX. A 1D fetch without other sources reads a texture buffer
        /// when the descriptor is one, as the Maxwell TLD.
        /// </summary>
        public static void Tld(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            SamplerType type = TextureType(inst);

            if (!CheckTextureType(context, inst, "TLD", type))
            {
                return;
            }

            int lodMode = inst.Field(87, 3);

            if (lodMode is not (1 or 3))
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 TLD with LOD mode {lodMode} at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            bool hasLodValue = lodMode == 3;
            bool hasOffset = inst.Bit(76);
            bool isMultisample = inst.Bit(78);
            bool isArray = type.HasFlag(SamplerType.Array);
            int coordsCount = type.Dimensions;
            int handle = TextureHandle(inst);
            int bank = TextureBank(inst);

            int countA = (isArray ? 1 : 0) + coordsCount;
            int countB = (hasLodValue ? 1 : 0) + (hasOffset ? 1 : 0) + (isMultisample ? 1 : 0);
            Operand[] values = ReadTextureSources(context, inst, countA, countB, inst.Bit(60));

            int next = 0;
            Operand arrayIndex = isArray ? values[next++] : null;
            List<Operand> sources = [];
            TextureFlags flags = TextureFlags.IntCoords;

            for (int i = 0; i < coordsCount; i++)
            {
                sources.Add(values[next++]);
            }

            bool simple1D = type == SamplerType.Texture1D && !hasLodValue && !hasOffset && !isMultisample;
            bool isBuffer = simple1D && context.TranslatorContext.GpuAccessor.QuerySamplerType(handle, bank) == SamplerType.TextureBuffer;
            bool is1DTo2D = false;

            if (isBuffer)
            {
                type = SamplerType.TextureBuffer;
            }
            else if (InstEmit.Sample1DAs2D && (type & SamplerType.Mask) == SamplerType.Texture1D)
            {
                if (simple1D)
                {
                    // As the Maxwell TLD (InstEmit.EmitTex): the 2D read wraps x into rows of 16384 texels.
                    Operand x = sources[^1];
                    sources[^1] = context.BitwiseAnd(x, Const(0x3FFF));
                    sources.Add(context.ShiftRightU32(x, Const(14)));
                    context.TranslatorContext.GpuAccessor.RecordTexelFetch1D(handle);
                }
                else
                {
                    sources.Add(Const(0));
                }

                type = SamplerType.Texture2D | (type & SamplerType.Array);
                is1DTo2D = true;
            }

            if (isArray)
            {
                sources.Add(arrayIndex);
            }

            Operand lodValue = hasLodValue ? values[next++] : Const(0);
            Operand packedOffsets = hasOffset ? values[next++] : null;

            if (!isMultisample && !isBuffer)
            {
                sources.Add(lodValue);
                flags |= TextureFlags.LodLevel;
            }

            if (hasOffset)
            {
                AddPackedOffsets(context, sources, packedOffsets, 0, coordsCount, is1DTo2D);
                flags |= TextureFlags.Offset;
            }

            if (isMultisample)
            {
                sources.Add(values[next++]);
                type |= SamplerType.Multisample;
            }

            EmitTextureSample(context, inst, Instruction.TextureSample, type, flags, sources);
        }

        /// <summary>
        /// TLD4 (0xb63): gather of component 87-88 (or a depth comparison, 78). Offset mode 76-77: AOFFI (one word, 8 bits per coordinate) or per-pixel
        /// (two words, an 8-bit x / y pair per gathered texel). Sources: A [array index] coordinates, B [offsets] [depth reference]; scalar split as TEX.
        /// The channel mask selects which gathered texels are written.
        /// </summary>
        public static void Tld4(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            SamplerType type = TextureType(inst);

            if (!CheckTextureType(context, inst, "TLD4", type))
            {
                return;
            }

            int offsetMode = inst.Field(76, 2);

            if (offsetMode == 3)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 TLD4 with offset mode 3 at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            bool hasDepthCompare = inst.Bit(78);
            bool isArray = type.HasFlag(SamplerType.Array);
            int coordsCount = type.Dimensions;
            int offsetWords = offsetMode;

            int countA = (isArray ? 1 : 0) + coordsCount;
            int countB = offsetWords + (hasDepthCompare ? 1 : 0);
            Operand[] values = ReadTextureSources(context, inst, countA, countB, inst.Bit(60));

            int next = 0;
            Operand arrayIndex = isArray ? values[next++] : null;
            List<Operand> sources = [];
            TextureFlags flags = TextureFlags.Gather;

            for (int i = 0; i < coordsCount; i++)
            {
                sources.Add(values[next++]);
            }

            bool is1DTo2D = Add1DAs2DCoordinate(sources, ref type, ConstF(0));

            if (isArray)
            {
                sources.Add(arrayIndex);
            }

            Operand[] packedOffsets = new Operand[offsetWords];

            for (int i = 0; i < offsetWords; i++)
            {
                packedOffsets[i] = values[next++];
            }

            if (hasDepthCompare)
            {
                sources.Add(values[next++]);
                type |= SamplerType.Shadow;
            }

            if (offsetWords != 0)
            {
                // As the Maxwell TLD4 (InstEmit.EmitTld4): signed 6-bit offsets in 8-bit fields, texel i's pair in word i / 2.
                int texels = offsetWords == 2 ? 4 : 1;

                for (int index = 0; index < coordsCount * texels; index++)
                {
                    sources.Add(context.BitfieldExtractS32(packedOffsets[(index >> 2) & 1], Const((index & 3) * 8), Const(6)));
                }

                if (is1DTo2D)
                {
                    for (int index = 0; index < texels; index++)
                    {
                        sources.Add(Const(0));
                    }
                }

                flags |= offsetWords == 2 ? TextureFlags.Offsets : TextureFlags.Offset;
            }

            if (!hasDepthCompare)
            {
                sources.Add(Const(inst.Field(87, 2)));
            }

            EmitTextureSample(context, inst, Instruction.TextureSample, type, flags, sources);
        }

        /// <summary>
        /// TXD (0xb6c): sample with explicit derivatives; AOFFI 76. Sources (not scalar): A coordinates [array index in bits 0-15 | packed offsets in
        /// 16-31], B the derivatives interleaved per coordinate (ddx0, ddy0, ddx1, ddy1...). MKW has none; the layout is NAK's.
        /// </summary>
        public static void Txd(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            SamplerType type = TextureType(inst);

            if (!CheckTextureType(context, inst, "TXD", type))
            {
                return;
            }

            bool hasOffset = inst.Bit(76);
            bool isArray = type.HasFlag(SamplerType.Array);
            int coordsCount = type.Dimensions;
            bool hasPacked = isArray || hasOffset;

            Operand[] values = ReadTextureSources(context, inst, coordsCount + (hasPacked ? 1 : 0), coordsCount * 2, scalar: false);

            List<Operand> sources = [];
            TextureFlags flags = TextureFlags.Derivatives;

            for (int i = 0; i < coordsCount; i++)
            {
                sources.Add(values[i]);
            }

            bool is1DTo2D = Add1DAs2DCoordinate(sources, ref type, ConstF(0));
            Operand packed = hasPacked ? values[coordsCount] : null;
            int derivatives = coordsCount + (hasPacked ? 1 : 0);

            if (isArray)
            {
                sources.Add(context.BitwiseAnd(packed, Const(0xffff)));
            }

            // The IR takes dPdx then dPdy.
            for (int axis = 0; axis < 2; axis++)
            {
                for (int i = 0; i < coordsCount; i++)
                {
                    sources.Add(values[derivatives + i * 2 + axis]);
                }

                if (is1DTo2D)
                {
                    sources.Add(ConstF(0));
                }
            }

            if (hasOffset)
            {
                AddPackedOffsets(context, sources, packed, 16, coordsCount, is1DTo2D);
                flags |= TextureFlags.Offset;
            }

            EmitTextureSample(context, inst, Instruction.TextureSample, type, flags, sources);
        }

        /// <summary>
        /// TMML (0xb69): LOD query. Sources: A [array index] coordinates. Results as on Maxwell (InstEmit.EmitTmml), 8.8 fixed point: component 0 the
        /// relative LOD, component 1 the clamped level (GLSL's order reversed); components 2-3 read 0.
        /// </summary>
        public static void Tmml(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            SamplerType type = TextureType(inst);

            if (!CheckTextureType(context, inst, "TMML", type))
            {
                return;
            }

            bool isArray = type.HasFlag(SamplerType.Array);
            int coordsCount = type.Dimensions;
            Operand[] values = ReadTextureSources(context, inst, (isArray ? 1 : 0) + coordsCount, 0, scalar: false);

            List<Operand> sources = [];

            for (int i = 0; i < coordsCount; i++)
            {
                sources.Add(values[(isArray ? 1 : 0) + i]);
            }

            Add1DAs2DCoordinate(sources, ref type, ConstF(0));

            if (isArray)
            {
                sources.Add(values[0]);
            }

            SetBindingPair setAndBinding = context.ResourceManager.GetTextureOrImageBinding(
                Instruction.Lod,
                type,
                TextureFormat.Unknown,
                TextureFlags.None,
                TextureBank(inst),
                TextureHandle(inst));

            int componentMask = inst.Field(72, 4);
            Operand[] dests = TextureDests(inst, BitOperations.PopCount((uint)componentMask));
            Operand[] sourceArray = sources.ToArray();

            for (int compMask = componentMask, compIndex = 0, outIndex = 0; compMask != 0; compMask >>= 1, compIndex++)
            {
                if ((compMask & 1) == 0)
                {
                    continue;
                }

                Operand value = compIndex >= 2
                    ? Const(0)
                    : context.FP32ConvertToS32(context.FPMultiply(context.Lod(type, TextureFlags.None, setAndBinding, compIndex ^ 1, sourceArray), ConstF(256.0f)));

                context.Copy(dests[outIndex++], value);
            }
        }

        /// <summary>
        /// TXQ (0xb6f): texture header query, kind in bits 62-63 (0 dimensions: width, height, depth, levels for the LOD in Ra; 1 texture type: the
        /// sample count in component 2). Results in the TEX destinations.
        /// </summary>
        public static void Txq(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int query = inst.Field(62, 2);

            if (query > 1)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 TXQ query {query} at 0x{context.CurrOp.Address:X} is not implemented.");
                return;
            }

            int handle = TextureHandle(inst);
            int bank = TextureBank(inst);
            SamplerType type = context.TranslatorContext.GpuAccessor.QuerySamplerType(handle, bank);
            Operand[] sources = [inst.Ra == RegisterConsts.RegisterZeroIndex ? Const(0) : context.Copy(Gpr(inst.Ra))];

            int componentMask = inst.Field(72, 4);
            Operand[] dests = TextureDests(inst, BitOperations.PopCount((uint)componentMask));

            SetBindingPair setAndBinding = context.ResourceManager.GetTextureOrImageBinding(
                query == 0 ? Instruction.TextureQuerySize : Instruction.TextureQuerySamples,
                type,
                TextureFormat.Unknown,
                TextureFlags.None,
                bank,
                handle);

            for (int compMask = componentMask, compIndex = 0, outIndex = 0; compMask != 0; compMask >>= 1, compIndex++)
            {
                if ((compMask & 1) == 0)
                {
                    continue;
                }

                Operand d = dests[outIndex++];

                if (query == 0)
                {
                    context.Copy(d, context.TextureQuerySize(type, TextureFlags.None, setAndBinding, compIndex, sources));
                }
                else if (compIndex == 2)
                {
                    context.Copy(d, context.TextureQuerySamples(type, TextureFlags.None, setAndBinding, sources));
                }
            }
        }

        /// <summary>A 1D texture read as 2D (InstEmit.Sample1DAs2D) gets a second coordinate; returns whether it did.</summary>
        private static bool Add1DAs2DCoordinate(List<Operand> sources, ref SamplerType type, Operand y)
        {
            if (InstEmit.Sample1DAs2D && (type & SamplerType.Mask) == SamplerType.Texture1D)
            {
                sources.Add(y);
                type = SamplerType.Texture2D | (type & SamplerType.Array);
                return true;
            }

            return false;
        }

        /// <summary>Signed 4-bit texel offsets packed from bit <paramref name="start"/>, one per coordinate (0 for the y of a 1D-as-2D read).</summary>
        private static void AddPackedOffsets(EmitterContext context, List<Operand> sources, Operand packed, int start, int coordsCount, bool is1DTo2D)
        {
            for (int i = 0; i < coordsCount; i++)
            {
                sources.Add(context.BitfieldExtractS32(packed, Const(start + i * 4), Const(4)));
            }

            if (is1DTo2D)
            {
                sources.Add(Const(0));
            }
        }

        /// <summary>Binds the texture c[bank][handle] and samples it into the instruction's destinations (FP16 results packed in pairs).</summary>
        private static void EmitTextureSample(EmitterContext context, Sm86Inst inst, Instruction kind, SamplerType type, TextureFlags flags, List<Operand> sources)
        {
            int componentMask = inst.Field(72, 4);
            int count = BitOperations.PopCount((uint)componentMask);

            SetBindingPair setAndBinding = context.ResourceManager.GetTextureOrImageBinding(
                kind,
                type,
                TextureFormat.Unknown,
                flags,
                TextureBank(inst),
                TextureHandle(inst));

            if (!inst.Bit(79))
            {
                context.TextureSample(type, flags, setAndBinding, componentMask, TextureDests(inst, count), sources.ToArray());
                return;
            }

            Operand[] results = new Operand[count];

            for (int i = 0; i < count; i++)
            {
                results[i] = Local();
            }

            context.TextureSample(type, flags, setAndBinding, componentMask, results, sources.ToArray());

            for (int pair = 0; pair * 2 < count; pair++)
            {
                int reg = pair == 0 ? inst.Rd : inst.Rc;

                if (reg < RegisterConsts.RegisterZeroIndex)
                {
                    Operand high = pair * 2 + 1 < count ? results[pair * 2 + 1] : ConstF(0);

                    context.Copy(Gpr(reg), context.PackHalf2x16(results[pair * 2], high));
                }
            }
        }

        /// <summary>
        /// The texture sources in order: countA values from Ra on, countB from Rb on, or with <paramref name="scalar"/> (bit 60, TEX / TLD / TLD4)
        /// and at most four values in all, the split described at Tex. RZ and beyond read 0.
        /// </summary>
        private static Operand[] ReadTextureSources(EmitterContext context, Sm86Inst inst, int countA, int countB, bool scalar)
        {
            int total = countA + countB;
            int fromA = countA;

            if (scalar && total <= 4)
            {
                fromA = total >= 3 ? 2 : total;
                fromA = total == 2 ? 1 : fromA;
            }

            Operand[] values = new Operand[total];

            for (int i = 0; i < total; i++)
            {
                int reg = i < fromA ? inst.Ra + i : inst.Rb + (i - fromA);
                int baseReg = i < fromA ? inst.Ra : inst.Rb;

                values[i] = baseReg == RegisterConsts.RegisterZeroIndex || reg > RegisterConsts.RegisterZeroIndex ? Const(0) : context.Copy(Gpr(reg));
            }

            return values;
        }

        /// <summary>Destinations of a texture result: components 0-1 in Rd, Rd+1, components 2-3 in the second destination (bits 64-71), Rd2+1.</summary>
        private static Operand[] TextureDests(Sm86Inst inst, int count)
        {
            Operand[] dests = new Operand[count];

            for (int i = 0; i < count; i++)
            {
                int baseReg = i < 2 ? inst.Rd : inst.Rc;
                int reg = baseReg + (i & 1);

                dests[i] = baseReg == RegisterConsts.RegisterZeroIndex || reg >= RegisterConsts.RegisterZeroIndex ? Local() : Gpr(reg);
            }

            return dests;
        }
    }
}
