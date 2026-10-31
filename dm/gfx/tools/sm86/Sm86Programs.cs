// Hand-encoded SM86 test programs for the harness's hand command. The field layouts are Mesa NAK's encoder (src/nouveau/compiler/nak/sm70_encode.rs,
// MIT), written here from scratch; they exercise what real dumps may lack in one small program: uniform registers and predicates, CALL / RET with a
// link register, BRX through a c[1] jump table, BSSY / BSYNC, the ALU source forms and the scalar TEX packing.
#if !BASELINE
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

sealed class Sm86Asm
{
    public const int PT = 7, RZ = 255, URZ = 63;

    readonly Dictionary<string, int> _labels = new();
    readonly List<(int Index, string Label, int Start, int Count)> _fixups = new();

    public int Count => _pending.Count;

    public sealed class Inst
    {
        public ulong Lo, Hi;

        public Inst F(int offset, int count, long value)
        {
            ulong mask = (count == 64 ? ulong.MaxValue : (1UL << count) - 1);
            ulong v = (ulong)value & mask;
            for (int i = 0; i < count; i++)
            {
                int bit = offset + i;
                ulong b = (v >> i) & 1;
                if (bit < 64) Lo = (Lo & ~(1UL << bit)) | (b << bit);
                else Hi = (Hi & ~(1UL << (bit - 64))) | (b << (bit - 64));
            }
            return this;
        }

        public Inst Guard(int pred, bool inv = false) => F(12, 3, pred).F(15, 1, inv ? 1 : 0);
    }

    public Inst Emit(int opcode)
    {
        var i = new Inst();
        i.F(0, 12, opcode).F(12, 3, PT);
        _pending.Add(i);
        return i;
    }

    readonly List<Inst> _pending = new();

    public void Label(string name) => _labels[name] = _pending.Count;

    /// <summary>A branch-style relative target: words (4 bytes) from the next instruction, in bits 34-81.</summary>
    public Inst Target(Inst i, string label)
    {
        _fixups.Add((_pending.IndexOf(i), label, 34, 48));
        return i;
    }

    public byte[] Assemble(byte[] header)
    {
        foreach (var (index, label, start, count) in _fixups)
        {
            long rel = (_labels[label] - (index + 1)) * 4L;
            _pending[index].F(start, count, rel);
        }

        using var ms = new MemoryStream();
        ms.Write(header);
        foreach (Inst i in _pending)
        {
            ms.Write(BitConverter.GetBytes(i.Lo));
            ms.Write(BitConverter.GetBytes(i.Hi));
        }
        return ms.ToArray();
    }

    public int AddressOf(string label) => _labels[label] * 16;

    // ALU: base opcode (bits 0-8) | form << 9, destination, A, the 32-bit slot, the C register (bits 64-71).
    public Inst Alu(int baseOp, int form, int rd, int ra, long slot32, int rc)
    {
        return Emit(baseOp | (form << 9)).F(16, 8, rd).F(24, 8, ra).F(32, 32, slot32).F(64, 8, rc);
    }

    public static long Cbuf(int bank, int byteOffset) => ((long)byteOffset << 6) | ((long)bank << 22);

    public static long F32(float f) => BitConverter.SingleToInt32Bits(f) & 0xffffffffL;

    // No predicate input (!PT in 87-90 / 77-80), no predicate outputs (PT in 81-86).
    public static Inst NoPreds(Inst i) => i.F(87, 3, PT).F(90, 1, 1).F(77, 3, PT).F(80, 1, 1).F(81, 3, PT).F(84, 3, PT);
}

static class Sm86Programs
{
    // SPH word 0 as the MKW dump shows them (version 4): vertex type 1 / stage 1, pixel type 2 / stage 5.
    const uint VsWord0 = 0x020c0481, PsWord0 = 0x000c5482;

    static byte[] Header(uint word0, Action<int[]> fill = null)
    {
        int[] words = new int[32];
        words[0] = (int)word0;
        fill?.Invoke(words);
        byte[] header = new byte[0x80];
        Buffer.BlockCopy(words, 0, header, 0, 0x80);
        return header;
    }

    public static int Write(string outDir)
    {
        Directory.CreateDirectory(outDir);
        Save(outDir, "hand_vs_basic", "vertex", VsBasic());
        Save(outDir, "hand_ps_tex", "fragment", PsTex());
        (byte[] alu, byte[] c1) = PsAlu();
        Save(outDir, "hand_ps_alu", "fragment", alu);
        File.WriteAllBytes(Path.Combine(outDir, "hand_ps_alu.c1.bin"), c1);
        Console.WriteLine($"wrote 3 hand-encoded SM86 programs to {outDir}");
        return 0;
    }

    static void Save(string dir, string key, string stage, byte[] code)
    {
        File.WriteAllBytes(Path.Combine(dir, key + ".bin"), code);
        File.WriteAllText(Path.Combine(dir, key + ".meta.json"), new JsonObject { ["stage"] = stage, ["code_size"] = code.Length - 0x80, ["header_size"] = 0x80 }.ToJsonString());
    }

    // ALD / AST / ULDC / FMUL with a uniform register / FFMA with a constant buffer / MOV imm / EXIT.
    static byte[] VsBasic()
    {
        var a = new Sm86Asm();
        a.Emit(0x321).F(16, 8, 0).F(24, 8, Sm86Asm.RZ).F(32, 8, Sm86Asm.RZ).F(40, 10, 0x80).F(74, 2, 3);  // ALD R0..R3, a[0x80]
        a.Emit(0xab9).F(16, 6, 4).F(32, 32, Sm86Asm.Cbuf(0, 0x10)).F(73, 3, 5);                              // ULDC.64 UR4, c[0][0x10]
        a.Alu(0x020, 6, 0, 0, 4, Sm86Asm.RZ).F(84, 3, 4).F(91, 1, 1);                                       // FMUL R0, R0, UR4
        a.Alu(0x020, 6, 1, 1, 5, Sm86Asm.RZ).F(84, 3, 4).F(91, 1, 1);                                       // FMUL R1, R1, UR5
        a.Alu(0x023, 5, 2, 2, Sm86Asm.Cbuf(0, 0x18), 2);                                                    // FFMA R2, R2, c[0][0x18], R2
        a.Alu(0x002, 4, 3, Sm86Asm.RZ, Sm86Asm.F32(1f), Sm86Asm.RZ).F(72, 4, 0xf);                          // MOV R3, 1.0
        a.Emit(0x322).F(24, 8, Sm86Asm.RZ).F(32, 8, 0).F(64, 8, Sm86Asm.RZ).F(40, 10, 0x70).F(74, 2, 3);  // AST a[0x70], R0..R3 (position)
        a.Emit(0x321).F(16, 8, 4).F(24, 8, Sm86Asm.RZ).F(32, 8, Sm86Asm.RZ).F(40, 10, 0x90).F(74, 2, 1);  // ALD R4, R5, a[0x90]
        a.Emit(0x322).F(24, 8, Sm86Asm.RZ).F(32, 8, 4).F(64, 8, Sm86Asm.RZ).F(40, 10, 0x80).F(74, 2, 1);  // AST a[0x80], R4, R5
        a.Emit(0x94d).F(87, 3, Sm86Asm.PT);                                                                  // EXIT
        return a.Assemble(Header(VsWord0));
    }

    // IPA with perspective correction, MUFU.RCP, scalar TEX with two destinations, FMUL from c[0], MOVs into the colour outputs.
    static byte[] PsTex()
    {
        var a = new Sm86Asm();
        a.Emit(0x326).F(16, 8, 0).F(32, 8, Sm86Asm.RZ).F(64, 8, 0x7c >> 2).F(81, 3, Sm86Asm.PT);        // IPA R0, a[0x7c] (position w)
        a.Alu(0x108, 1, 0, Sm86Asm.RZ, 0, Sm86Asm.RZ).F(74, 6, 4);                                          // MUFU.RCP R0, R0
        a.Emit(0x326).F(16, 8, 1).F(32, 8, Sm86Asm.RZ).F(64, 8, 0x80 >> 2).F(81, 3, Sm86Asm.PT);        // IPA R1, a[0x80]
        a.Emit(0x326).F(16, 8, 2).F(32, 8, Sm86Asm.RZ).F(64, 8, 0x84 >> 2).F(81, 3, Sm86Asm.PT);        // IPA R2, a[0x84]
        a.Alu(0x020, 1, 1, 1, 0, Sm86Asm.RZ).F(84, 3, 4);                                                    // FMUL R1, R1, R0
        a.Alu(0x020, 1, 2, 2, 0, Sm86Asm.RZ).F(84, 3, 4);                                                    // FMUL R2, R2, R0
        a.Emit(0xb60).F(16, 8, 4).F(64, 8, 6).F(24, 8, 1).F(32, 8, 2).F(40, 14, 0x20 / 4).F(54, 5, 2)      // TEX.SCALAR R4, R6, R1, R2, c[2][0x20], 2D, RGBA
            .F(60, 1, 1).F(61, 3, 1).F(72, 4, 0xf).F(81, 3, Sm86Asm.PT).F(84, 3, 1);
        a.Alu(0x020, 5, 0, 4, Sm86Asm.Cbuf(0, 0x20), Sm86Asm.RZ).F(84, 3, 4);                              // FMUL R0, R4, c[0][0x20]
        a.Alu(0x002, 1, 1, Sm86Asm.RZ, 5, Sm86Asm.RZ).F(72, 4, 0xf);                                        // MOV R1, R5
        a.Alu(0x002, 1, 2, Sm86Asm.RZ, 6, Sm86Asm.RZ).F(72, 4, 0xf);                                        // MOV R2, R6
        a.Alu(0x002, 1, 3, Sm86Asm.RZ, 7, Sm86Asm.RZ).F(72, 4, 0xf);                                        // MOV R3, R7
        a.Emit(0x94d).F(87, 3, Sm86Asm.PT);                                                                  // EXIT
        return a.Assemble(Header(PsWord0, PixelHeader));
    }

    // Integer ALU forms, predicates, a uniform chain, BSSY / BSYNC, a conditional branch, a BRX jump table in c[1] and a CALL.REL.NOINC / RET pair.
    static (byte[], byte[]) PsAlu()
    {
        const int RZ = Sm86Asm.RZ, PT = Sm86Asm.PT;
        var a = new Sm86Asm();
        a.Emit(0x326).F(16, 8, 0).F(32, 8, RZ).F(64, 8, 0x80 >> 2).F(81, 3, PT).F(78, 2, 1);              // IPA.CONSTANT R0, a[0x80]
        a.Alu(0x024, 4, 1, 0, 3, RZ).F(81, 3, PT).F(87, 3, PT).F(90, 1, 1);                                  // IMAD R1, R0, 3, RZ
        Sm86Asm.NoPreds(a.Alu(0x010, 4, 2, 1, 0x10, RZ)).F(81, 3, 1);                                       // IADD3 R2, P1, R1, 0x10, RZ
        a.Alu(0x012, 4, 3, 2, 0xff, RZ).F(72, 8, 0xc0).F(81, 3, 2).F(87, 3, PT).F(90, 1, 1);               // LOP3.LUT P2, R3, R2, 0xff, RZ, 0xc0, !PT
        a.Alu(0x019, 4, 4, 3, 2, RZ).F(73, 2, 3);                                                            // SHF.L.U32 R4, R3, 0x2, RZ
        a.Alu(0x00c, 4, 0, 3, 0x80, 0).F(76, 3, 6).F(74, 2, 0).F(81, 3, 0).F(84, 3, PT).F(87, 3, PT);       // ISETP.GE.U32.AND P0, PT, R3, 0x80, PT
        a.Emit(0x945).F(16, 4, 0).F(87, 3, PT).F(90, 1, 1);                                                  // BSSY B0, join (target unused)
        var bra = a.Emit(0x947).F(87, 3, PT).Guard(0);                                                       // @P0 BRA big
        a.Target(bra, "big");
        a.Alu(0x021, 4, 5, 0, Sm86Asm.F32(1f), RZ);                                                          // FADD R5, R0, 1.0
        var bra2 = a.Emit(0x947).F(87, 3, PT);                                                               // BRA join
        a.Target(bra2, "join");
        a.Label("big");
        a.Alu(0x009, 4, 5, 0, Sm86Asm.F32(0.5f), RZ).F(87, 3, PT);                                           // FMNMX R5, R0, 0.5, PT (min)
        a.Label("join");
        a.Emit(0x941).F(16, 4, 0).F(87, 3, PT);                                                              // BSYNC B0
        a.Emit(0xab9).F(16, 6, 4).F(32, 32, Sm86Asm.Cbuf(0, 0x30)).F(73, 3, 4);                              // ULDC UR4, c[0][0x30]
        Sm86Asm.NoPreds(a.Alu(0x090, 4, 5, 4, 1, Sm86Asm.URZ)).F(91, 1, 1);                                  // UIADD3 UR5, UR4, 0x1, URZ
        a.Alu(0x002, 6, 6, RZ, 5, RZ).F(72, 4, 0xf).F(91, 1, 1);                                             // MOV R6, UR5
        a.Alu(0x007, 1, 7, 4, 2, RZ).F(87, 3, 0);                                                            // SEL R7, R4, R2, P0

        // switch (R3 & 3) through a jump table: IMNMX.U32 / SHF.L.U32 / LDC c[1] / BRX
        a.Alu(0x012, 4, 11, 3, 3, RZ).F(72, 8, 0xc0).F(81, 3, PT).F(87, 3, PT).F(90, 1, 1);                // LOP3.LUT R11, R3, 0x3, RZ, 0xc0, !PT
        a.Alu(0x017, 4, 12, 11, 2, RZ).F(87, 3, PT);                                                         // IMNMX.U32 R12, R11, 0x2, PT
        a.Alu(0x019, 4, 13, 12, 2, RZ).F(73, 2, 3);                                                          // SHF.L.U32 R13, R12, 0x2, RZ
        a.Emit(0xb82).F(16, 8, 14).F(24, 8, 13).F(32, 32, Sm86Asm.Cbuf(1, 0)).F(73, 3, 4);                   // LDC R14, c[1][R13]
        var brx = a.Emit(0x949).F(24, 8, 14).F(87, 3, PT);                                                   // BRX R14 (base: program start)
        long brxRel = -(a.Count) * 16L;                                                                      // next instruction + rel = 0
        brx.F(32, 50, brxRel);
        a.Label("case0");
        a.Alu(0x002, 4, 15, RZ, Sm86Asm.F32(0.25f), RZ).F(72, 4, 0xf);
        var j0 = a.Emit(0x947).F(87, 3, PT); a.Target(j0, "cases");
        a.Label("case1");
        a.Alu(0x002, 4, 15, RZ, Sm86Asm.F32(0.5f), RZ).F(72, 4, 0xf);
        var j1 = a.Emit(0x947).F(87, 3, PT); a.Target(j1, "cases");
        a.Label("case2");
        a.Alu(0x002, 4, 15, RZ, Sm86Asm.F32(0.75f), RZ).F(72, 4, 0xf);
        a.Label("cases");

        // CALL.REL.NOINC with the return address in R10, the callee returns with RET.REL.NODEC R10.
        var mov = a.Alu(0x002, 4, 10, RZ, 0, RZ).F(72, 4, 0xf);                                             // MOV R10, return address
        var call = a.Emit(0x944).F(86, 1, 1).F(87, 3, PT);                                                   // CALL.REL.NOINC func
        a.Target(call, "func");
        mov.F(32, 32, a.Count * 16);
        a.Alu(0x002, 1, 0, RZ, 5, RZ).F(72, 4, 0xf);                                                         // MOV R0, R5
        a.Alu(0x002, 1, 1, RZ, 6, RZ).F(72, 4, 0xf);                                                         // MOV R1, R6
        a.Alu(0x002, 1, 2, RZ, 7, RZ).F(72, 4, 0xf);                                                         // MOV R2, R7
        a.Alu(0x002, 1, 3, RZ, 15, RZ).F(72, 4, 0xf);                                                        // MOV R3, R15
        a.Emit(0x94d).F(87, 3, PT);                                                                          // EXIT
        a.Label("func");
        a.Alu(0x020, 4, 5, 5, Sm86Asm.F32(2f), RZ).F(84, 3, 4);                                              // FMUL R5, R5, 2.0
        var ret = a.Emit(0x950).F(24, 8, 10).F(86, 1, 1).F(87, 3, PT);                                       // RET.REL.NODEC R10 (base: program start)
        ret.F(32, 50, -(a.Count) * 16L);

        byte[] code = a.Assemble(Header(PsWord0, PixelHeader));
        byte[] c1 = new byte[12];
        BitConverter.GetBytes(a.AddressOf("case0")).CopyTo(c1, 0);
        BitConverter.GetBytes(a.AddressOf("case1")).CopyTo(c1, 4);
        BitConverter.GetBytes(a.AddressOf("case2")).CopyTo(c1, 8);
        return (code, c1);
    }

    // Pixel SPH: generic attribute 0 x, y, z, w perspective (IMAP word 6 byte 0), colour target 0 RGBA (word 18).
    static void PixelHeader(int[] words)
    {
        words[6] = 0xaa;
        words[18] = 0xf;
    }
}
#endif
