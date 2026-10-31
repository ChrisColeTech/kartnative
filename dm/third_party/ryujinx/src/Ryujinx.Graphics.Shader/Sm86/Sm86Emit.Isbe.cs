using Ryujinx.Graphics.Shader.Decoders;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.Translation;
using static Ryujinx.Graphics.Shader.IntermediateRepresentation.OperandHelper;

namespace Ryujinx.Graphics.Shader.Sm86
{
    static partial class Sm86Emit
    {
        // ISBERD / ISBEWR fields (Mesa NAK OpIsberd / OpIsbewr): the address is Ra + the signed offset in bits 40-55, the type in 74-75, the region in 76-77,
        // SKEW in 78 and the output space in 79; ISBEWR's data register is in bits 32-39.
        private const int IsbeU8 = 0, IsbeB32 = 2;
        private const int IsbeMap = 0, IsbeAttribute = 3;
        private static readonly string[] IsbeRegions = { "map", "patch", "primitive", "attribute" };

        /// <summary>The byte address of an ISBE access: Ra (0 for RZ) + the signed offset.</summary>
        private static Operand IsbeAddress(Sm86Inst inst)
        {
            int offset = (short)inst.Field(40, 16);

            return inst.Ra == RegisterConsts.RegisterZeroIndex ? Const(offset) : null;
        }

        private static Operand IsbeAddress(EmitterContext context, Sm86Inst inst)
        {
            return IsbeAddress(inst) ?? context.IAdd(Gpr(inst.Ra), Const((short)inst.Field(40, 16)));
        }

        private static bool IsbeStage(EmitterContext context, out bool mesh)
        {
            TranslationFlags flags = context.TranslatorContext.Options.Flags;
            mesh = flags.HasFlag(TranslationFlags.Sm86Mesh);

            return mesh || flags.HasFlag(TranslationFlags.Sm86Task);
        }

        private static void IsbeNotImplemented(EmitterContext context, string name, Sm86Inst inst)
        {
            context.TranslatorContext.GpuAccessor.Log($"SM86 {name} type {inst.Field(74, 2)} {IsbeRegions[inst.Field(76, 2)]}{(inst.Bit(78) ? " skew" : "")} " +
                $"{(inst.Bit(79) ? "output" : "input")} at 0x{context.CurrOp.Address:X} is not implemented.");
        }

        /// <summary>
        /// ISBERD d, [Ra + offset] (0x923): a task or mesh program's input attribute region, as Mesa NVK's ISBE layout and mesh notes describe it. The word at 0
        /// is the workgroup index (the VERTEX_ID read with SKEW in a task program or a mesh program without one, read without SKEW the mesh workgroup's index
        /// among those its task launched); from 0x10 a mesh program reads the task payload (MKW: B32 at task-chosen offsets). Both are words of isbe_in, which
        /// dmsh_mesh.py fills before the program runs. Other forms (patch or primitive regions, output space, 8- / 16-bit reads, SKEW at another address) do not
        /// occur in MKW and are logged.
        /// </summary>
        public static void Isberd(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            bool skew = inst.Bit(78);
            Operand constant = IsbeAddress(inst);

            if (!IsbeStage(context, out _) || inst.Field(74, 2) != IsbeB32 || inst.Field(76, 2) != IsbeAttribute || inst.Bit(79) ||
                (skew && (constant == null || constant.Value != 0)))
            {
                IsbeNotImplemented(context, "ISBERD", inst);
                return;
            }

            Operand word = constant != null ? Const(constant.Value >> 2) : context.ShiftRightU32(IsbeAddress(context, inst), Const(2));

            SetDest(context, inst, context.Load(StorageKind.SharedMemory, Const(context.ResourceManager.IsbeInputId), word));
        }

        /// <summary>
        /// ISBEWR [Ra + offset], Rb (0x927): a task or mesh program's output regions (Mesa NVK's ISBE layout). B32 attribute stores go to isbe_out at their byte
        /// address: a mesh program's per-vertex attributes in 128-byte lines of 32 values each (SKEW), then its per-primitive ones; a task program's mesh
        /// workgroup count at 4 and its payload from 0x10. Map stores (mesh): an 8-bit vertex index at byte address a goes to isbe_map[a] (indices from 4), the
        /// B32 primitive count at address 3 (the map is written in reverse: it is the word at 0) to isbe_map[0]. Other forms are logged.
        /// </summary>
        public static void Isbewr(EmitterContext context)
        {
            Sm86Inst inst = Inst(context);
            int type = inst.Field(74, 2), region = inst.Field(76, 2);
            Operand constant = IsbeAddress(inst);

            if (!IsbeStage(context, out bool mesh) || !inst.Bit(79))
            {
                IsbeNotImplemented(context, "ISBEWR", inst);
                return;
            }

            Operand value = inst.Rb == RegisterConsts.RegisterZeroIndex ? Const(0) : context.Copy(Gpr(inst.Rb));

            if (region == IsbeAttribute && type == IsbeB32)
            {
                Operand word = constant != null ? Const(constant.Value >> 2) : context.ShiftRightU32(IsbeAddress(context, inst), Const(2));

                context.Store(StorageKind.SharedMemory, Const(context.ResourceManager.IsbeOutputId), word, value);
            }
            else if (mesh && region == IsbeMap && type == IsbeU8)
            {
                context.Store(StorageKind.SharedMemory, Const(context.ResourceManager.IsbeMapId), IsbeAddress(context, inst), context.BitwiseAnd(value, Const(0xff)));
            }
            else if (mesh && region == IsbeMap && type == IsbeB32 && constant != null && constant.Value == 3)
            {
                context.Store(StorageKind.SharedMemory, Const(context.ResourceManager.IsbeMapId), Const(0), value);
            }
            else
            {
                IsbeNotImplemented(context, "ISBEWR", inst);
            }
        }
    }
}
