using Ryujinx.Graphics.Shader.Instructions;

namespace Ryujinx.Graphics.Shader.Decoders
{
    readonly struct InstOp
    {
        public readonly ulong Address;
        public readonly ulong RawOpCode;
        // nxnative: bits 64-127 of a 128-bit SM70+ instruction (the SM86 frontend, Sm86/); 0 for Maxwell.
        public readonly ulong RawOpCodeHi;
        public readonly InstEmitter Emitter;
        public readonly InstProps Props;
        public readonly InstName Name;

        public InstOp(ulong address, ulong rawOpCode, InstName name, InstEmitter emitter, InstProps props)
        {
            Address = address;
            RawOpCode = rawOpCode;
            RawOpCodeHi = 0;
            Name = name;
            Emitter = emitter;
            Props = props;
        }

        public InstOp(ulong address, ulong rawOpCode, ulong rawOpCodeHi, InstEmitter emitter)
        {
            Address = address;
            RawOpCode = rawOpCode;
            RawOpCodeHi = rawOpCodeHi;
            Name = InstName.Invalid;
            Emitter = emitter;
            Props = InstProps.None;
        }

        public ulong GetAbsoluteAddress()
        {
            return (ulong)((long)Address + (((int)(RawOpCode >> 20) << 8) >> 8) + 8);
        }
    }
}
