using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.Instructions;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using System.Collections.Generic;
using System.Numerics;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86Emit
    {
        /// <summary>
        /// TEX (0xb60, handle in a constant buffer): d = sample(texture c[bank][handle]). Fields: handle word offset bits 40-53, bank 54-58,
        /// scalar source packing 60, dimension 61-63 (0 1D, 1 2D, 2 3D, 3 cube, 4 1D array, 5 2D array, 7 cube array), component mask 72-75,
        /// AOFFI 76, NDV 77, depth compare 78, FP16 results 79, LOD mode 87-89 (auto, zero, bias, lod, clamp, bias + clamp).
        /// Sources, as on Maxwell: Ra holds [array index] coordinates, Rb [lod or bias] [packed offsets, 4 bits each] [depth reference]. With the
        /// scalar bit, the same list of at most four values is split into the first two (one when there are two) from Ra on and the rest from Rb on
        /// (Mesa nak_nir_lower_tex.c, try_scalarize_tex). Results: the first two components to Rd, Rd+1, the next two to Rd2 (bits 64-71), Rd2+1.
        /// </summary>
        public static void Tex(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            ulong address = context.CurrOp.Address;

            SamplerType type = inst.Field(61, 3) switch
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

            if (type == SamplerType.None)
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 TEX with dimension {inst.Field(61, 3)} at 0x{address:X} is not implemented.");
                return;
            }

            int lodMode = inst.Field(87, 3);
            bool hasDepthCompare = inst.Bit(78);
            bool hasOffset = inst.Bit(76);

            if (lodMode >= 4 || inst.Bit(79))
            {
                context.TranslatorContext.GpuAccessor.Log($"SM86 TEX with LOD clamp or FP16 results at 0x{address:X} is not implemented.");
                return;
            }

            bool isArray = type.HasFlag(SamplerType.Array);
            int coordsCount = type.Dimensions;
            bool hasLodValue = lodMode is 2 or 3;

            int countA = (isArray ? 1 : 0) + coordsCount;
            int countB = (hasLodValue ? 1 : 0) + (hasOffset ? 1 : 0) + (hasDepthCompare ? 1 : 0);
            Operand[] values = ReadTextureSources(context, inst, countA, countB);

            int next = 0;
            Operand arrayIndex = isArray ? values[next++] : null;

            List<Operand> sources = [];
            TextureFlags flags = TextureFlags.None;

            for (int i = 0; i < coordsCount; i++)
            {
                sources.Add(values[next++]);
            }

            bool is1DTo2D = false;

            if (InstEmit.Sample1DAs2D && (type & SamplerType.Mask) == SamplerType.Texture1D)
            {
                sources.Add(ConstF(0));
                type = SamplerType.Texture2D | (type & SamplerType.Array);
                is1DTo2D = true;
            }

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
                for (int i = 0; i < coordsCount; i++)
                {
                    sources.Add(context.BitfieldExtractS32(packedOffsets, Const(i * 4), Const(4)));
                }

                if (is1DTo2D)
                {
                    sources.Add(Const(0));
                }

                flags |= TextureFlags.Offset;
            }

            if (lodMode == 2)
            {
                sources.Add(lodValue);
                flags |= TextureFlags.LodBias;
            }

            int componentMask = inst.Field(72, 4);
            Operand[] dests = TextureDests(inst, BitOperations.PopCount((uint)componentMask));

            SetBindingPair setAndBinding = context.ResourceManager.GetTextureOrImageBinding(
                Instruction.TextureSample,
                type,
                TextureFormat.Unknown,
                flags,
                inst.Field(54, 5),
                inst.Field(40, 14));

            context.TextureSample(type, flags, setAndBinding, componentMask, dests, sources.ToArray());
        }

        /// <summary>The texture sources in order: countA values from Ra on, countB from Rb on, or the scalar split (see Tex). RZ and beyond read 0.</summary>
        private static Operand[] ReadTextureSources(EmitterContext context, Sm86Inst inst, int countA, int countB)
        {
            int total = countA + countB;
            int fromA = countA;

            if (inst.Bit(60) && total <= 4)
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
