// Warp and tensor tests for the SM86 frontend (docs/reference/shaders-sm86.md, Tests): hand-encoded fragment programs, translated like the chain does
// (SPIR-V, spirv-cross MSL), rendered on the Mac GPU by alu_run (alu_run.swift) into an 8x4 RGBA32Uint target: 32 fragment invocations, which the GPU
// runs as one 32-wide SIMD group (the test checks that every lane id appears once). Each program writes three results to R0-R2 and its lane id to R3;
// the C# model of the warp gives the expected values. Encodings: Mesa NAK sm70_encode.rs and docs/reference/shaders-sm86.md.
#if !BASELINE
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

static class WarpTests
{
    const int RZ = Sm86Asm.RZ, PT = Sm86Asm.PT, URZ = Sm86Asm.URZ;
    const int Lanes = 32;

    // Registers every test uses: R20 = lane id, R21 = lane * 32 (the lane's 8 words of c[0]).
    const int RLane = 20, RLaneOffset = 21;

    sealed class Test
    {
        public string Name;
        public Sm86Asm Asm;
        public uint[] Data;                 // c[0], 8 words per lane
        public Func<int, uint[]> Expected;  // lane -> R0, R1, R2
    }

    public static int Run(string outDir, string aluRun)
    {
        Directory.CreateDirectory(outDir);
        int failed = 0, count = 0;

        foreach (Test test in Tests())
        {
            // Pixel SPH word 0 as the MKW dump shows it; colour target 0 RGBA (word 18).
            int[] words = new int[32];
            words[0] = 0x000c5482;
            words[18] = 0xf;
            byte[] header = new byte[0x80];
            Buffer.BlockCopy(words, 0, header, 0, 0x80);
            test.Asm.Emit(0x94d).F(87, 3, PT);                                                               // EXIT
            byte[] code = test.Asm.Assemble(header);

            foreach (bool debug in new[] { true, false })
            {
                count++;
                failed += RunOne(test, code, debug, outDir, aluRun) ? 0 : 1;
            }
        }

        Console.WriteLine(failed == 0 ? $"warp tests: all {count} passed" : $"warp tests: {failed} of {count} failed");
        return failed == 0 ? 0 : 1;
    }

    static bool RunOne(Test test, byte[] code, bool debug, string outDir, string aluRun)
    {
        string name = test.Name + (debug ? "" : "_opt");
        var acc = new Accessor(code) { UintColor0 = true };
        string spv = Path.Combine(outDir, name + ".spv");
        string msl = Path.Combine(outDir, name + ".metal");
        string output = Path.Combine(outDir, name + ".out.bin");

        try
        {
            // The chain translates with DebugMode by default (dmsh_xlate's job field "debug": false turns it off and runs the AST optimizer).
            TranslationFlags flags = TranslationFlags.Sm86 | (debug ? TranslationFlags.DebugMode : 0);
            ShaderProgram program = Translator.CreateContext(0, acc, new TranslationOptions(TargetLanguage.Spirv, TargetApi.Vulkan, flags)).Translate();
            File.WriteAllBytes(spv, program.BinaryCode);
            Harness.Run(Harness.FindSpirvCross(), new[] { "--msl", "--msl-version", "20300", "--msl-argument-buffers", "--msl-texture-buffer-native", "--msl-pad-fragment-output", spv, "--output", msl });

            var runArgs = new List<string> { msl, output, "--size", "8", "4", "--format", "u32" };

            // spirv-cross numbers argument-buffer members itself: c[0] is the member named fp_c0 ("constant ... fp_c0 [[id(N)]]").
            var cbuf0 = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(msl), @"\bfp_c0 \[\[id\((\d+)\)\]\]");

            if (test.Data != null && cbuf0.Success)
            {
                string data = Path.Combine(outDir, name + ".c0.bin");
                File.WriteAllBytes(data, test.Data.SelectMany(BitConverter.GetBytes).ToArray());
                runArgs.AddRange(new[] { "--buf", cbuf0.Groups[1].Value, data });
            }

            Harness.Run(aluRun, runArgs.ToArray());
        }
        catch (Exception e)
        {
            Console.WriteLine($"[RED] {name}: {e.GetType().Name}: {e.Message.Split('\n')[0]}");

            if (Environment.GetEnvironmentVariable("SM86_STACK") != null)
            {
                Console.WriteLine(e.StackTrace);
            }

            return false;
        }

        if (acc.Notes.Count != 0)
        {
            Console.WriteLine($"[RED] {name}: translator notes: {string.Join(" | ", acc.Notes)}");
            return false;
        }

        byte[] raw = File.ReadAllBytes(output);
        var byLane = new Dictionary<uint, uint[]>();

        for (int pixel = 0; pixel < Lanes; pixel++)
        {
            uint[] r = Enumerable.Range(0, 4).Select(i => BitConverter.ToUInt32(raw, pixel * 16 + i * 4)).ToArray();
            byLane[r[3]] = r;
        }

        if (byLane.Count != Lanes || byLane.Keys.Any(l => l >= Lanes))
        {
            Console.WriteLine($"[RED] {name}: the 32 pixels did not run as one SIMD group (lane ids {string.Join(",", byLane.Keys.OrderBy(k => k))})");
            return false;
        }

        int bad = 0;

        for (int lane = 0; lane < Lanes; lane++)
        {
            uint[] want = test.Expected(lane);
            uint[] got = byLane[(uint)lane];

            for (int i = 0; i < 3; i++)
            {
                if (got[i] != want[i])
                {
                    if (bad++ < 6)
                    {
                        Console.WriteLine($"[RED] {name}: lane {lane} R{i} = 0x{got[i]:x8}, expected 0x{want[i]:x8}");
                    }
                }
            }
        }

        Console.WriteLine(bad == 0 ? $"{name}: ok" : $"[RED] {name}: {bad} wrong values");
        return bad == 0;
    }

    // Helpers.

    static Sm86Asm Prologue()
    {
        var a = new Sm86Asm();
        a.Emit(0x919).F(16, 8, RLane).F(72, 8, 0x00);                                                        // S2R R20, SR_LANEID
        a.Alu(0x019, 4, RLaneOffset, RLane, 5, RZ).F(73, 2, 3);                                              // SHF.L.U32 R21, R20, 0x5, RZ
        a.Alu(0x010, 4, 5, RLane, 100, RZ).F(81, 3, PT).F(84, 3, PT).F(87, 3, PT).F(90, 1, 1)               // IADD3 R5, R20, 100, RZ
            .F(77, 3, PT).F(80, 1, 1);
        return a;
    }

    static void Mov(Sm86Asm a, int rd, int rs) => a.Alu(0x002, 1, rd, RZ, rs, RZ).F(72, 4, 0xf);
    static void MovImm(Sm86Asm a, int rd, uint imm) => a.Alu(0x002, 4, rd, RZ, imm, RZ).F(72, 4, 0xf);
    static void MovUr(Sm86Asm a, int rd, int ur) => a.Alu(0x002, 6, rd, RZ, ur, RZ).F(72, 4, 0xf).F(91, 1, 1);
    static void Lop3Imm(Sm86Asm a, int rd, int ra, uint imm, int lut) => a.Alu(0x012, 4, rd, ra, imm, RZ).F(72, 8, lut).F(81, 3, PT).F(87, 3, PT).F(90, 1, 1);
    static void Ldc(Sm86Asm a, int rd, int offset) => a.Emit(0xb82).F(16, 8, rd).F(24, 8, RLaneOffset).F(32, 32, Sm86Asm.Cbuf(0, offset)).F(73, 3, 4);

    // ISETP.<op>.U32.AND Pd, PT, Ra, imm, PT (op: 1 LT, 2 EQ, 3 LE, 4 GT, 5 NE, 6 GE).
    static void IsetpImm(Sm86Asm a, int pd, int ra, uint imm, int op) => a.Alu(0x00c, 4, 0, ra, imm, 0).F(76, 3, op).F(74, 2, 0).F(81, 3, pd).F(84, 3, PT).F(87, 3, PT);

    // VOTE / VOTEU: mode 0 ALL, 1 ANY, 2 EQ.
    static void Vote(Sm86Asm a, int rd, int mode, int pd, int ps, bool inv = false, bool uniform = false)
    {
        a.Emit(uniform ? 0x886 : 0x806).F(16, uniform ? 6 : 8, rd).F(72, 2, mode).F(81, 3, pd).F(87, 3, ps).F(90, 1, inv ? 1 : 0);
    }

    // SHFL with an immediate lane and c (0xf89): mode 0 IDX, 1 UP, 2 DOWN, 3 BFLY.
    static void ShflImm(Sm86Asm a, int rd, int ra, int lane, int c, int mode, int pd = PT)
    {
        a.Emit(0xf89).F(16, 8, rd).F(24, 8, ra).F(40, 13, c).F(53, 5, lane).F(58, 2, mode).F(81, 3, pd);
    }

    static uint P2rAll(Sm86Asm a, int rd, uint mask) { a.Emit(0x803).F(16, 8, rd).F(24, 8, RZ).F(32, 32, mask); return mask; }

    static uint Plus100(int lane) => (uint)(lane + 100);

    static IEnumerable<Test> Tests()
    {
        // VOTE.ANY / ALL / EQ and their predicate results.
        {
            var a = Prologue();
            IsetpImm(a, 0, RLane, 5, 1);                                                                      // P0 = lane < 5
            Vote(a, 0, 1, 1, 0);                                                                              // VOTE.ANY R0, P1, P0
            Vote(a, 1, 0, 2, PT);                                                                             // VOTE.ALL R1, P2, PT
            Vote(a, RZ, 2, 3, 0);                                                                             // VOTE.EQ RZ, P3, P0
            P2rAll(a, 2, 0xf);                                                                                // P2R R2, PR, RZ, 0xf
            Mov(a, 3, RLane);
            yield return new Test { Name = "vote", Asm = a, Expected = l => new uint[] { 0x1f, 0xffffffff, (l < 5 ? 1u : 0u) | 2 | 4 } };
        }

        // SHFL with immediate lane and c: IDX, BFLY, DOWN (out-of-range lanes keep their own value).
        {
            var a = Prologue();
            ShflImm(a, 0, 5, 7, 0x1f, 0);
            ShflImm(a, 1, 5, 1, 0x1f, 3);
            ShflImm(a, 2, 5, 3, 0x1f, 2);
            Mov(a, 3, RLane);
            yield return new Test { Name = "shfl", Asm = a, Expected = l => new[] { Plus100(7), Plus100(l ^ 1), Plus100(l + 3 <= 31 ? l + 3 : l) } };
        }

        // SHFL.UP with c = 0, SHFL.IDX with register lane and c (0x389), SHFL.BFLY within 4-lane segments (c = 0x1c03).
        {
            var a = Prologue();
            ShflImm(a, 0, 5, 2, 0, 1);
            Lop3Imm(a, 6, RLane, 5, 0x3c);                                                                    // R6 = lane ^ 5
            MovImm(a, 7, 0x1f);
            a.Emit(0x389).F(16, 8, 1).F(24, 8, 5).F(32, 8, 6).F(64, 8, 7).F(58, 2, 0).F(81, 3, PT);          // SHFL.IDX R1, R5, R6, R7
            ShflImm(a, 2, 5, 2, 0x1c03, 3);
            Mov(a, 3, RLane);
            yield return new Test { Name = "shfl2", Asm = a, Expected = l => new[] { Plus100(l >= 2 ? l - 2 : l), Plus100(l ^ 5), Plus100(l ^ 2) } };
        }

        // REDUX.SUM / REDUX.MIN.S32 and MATCH.ANY.
        {
            var a = Prologue();
            a.Emit(0x3c4).F(16, 6, 4).F(24, 8, 5).F(78, 3, 3);                                               // REDUX.SUM UR4, R5
            a.Alu(0x010, 4, 6, RZ, 10, RLane).F(75, 1, 1).F(81, 3, PT).F(84, 3, PT).F(87, 3, PT).F(90, 1, 1) // IADD3 R6, RZ, 10, -R20
                .F(77, 3, PT).F(80, 1, 1);
            a.Emit(0x3c4).F(16, 6, 5).F(24, 8, 6).F(73, 1, 1).F(78, 3, 4);                                   // REDUX.MIN.S32 UR5, R6
            MovUr(a, 0, 4);
            MovUr(a, 1, 5);
            Lop3Imm(a, 8, RLane, 3, 0xc0);                                                                    // R8 = lane & 3
            a.Emit(0x3a1).F(16, 8, 2).F(24, 8, 8).F(79, 1, 1).F(81, 3, PT);                                  // MATCH.ANY R2, R8
            Mov(a, 3, RLane);
            uint sum = (uint)Enumerable.Range(0, Lanes).Sum(l => (long)Plus100(l));
            yield return new Test { Name = "redux_match", Asm = a, Expected = l => new[] { sum, unchecked((uint)(10 - 31)), 0x11111111u << (l & 3) } };
        }

        // MATCH.ALL.U64 (equal) and MATCH.ALL (different), R2P with a mask, PLOP3 with two LUTs, P2R.
        {
            var a = Prologue();
            MovImm(a, 8, 5);
            MovImm(a, 9, 9);
            a.Emit(0x3a1).F(16, 8, 0).F(24, 8, 8).F(73, 1, 1).F(81, 3, 0);                                   // MATCH.ALL.U64 R0, P0, R8
            Lop3Imm(a, 10, RLane, 1, 0xc0);                                                                   // R10 = lane & 1
            a.Emit(0x3a1).F(16, 8, 1).F(24, 8, 10).F(81, 3, 5);                                              // MATCH.ALL R1, P5, R10
            a.Emit(0x804).F(24, 8, RLane).F(32, 32, 6);                                                       // R2P PR, R20, 0x6
            a.Emit(0x81c).F(87, 3, 0).F(77, 3, 1).F(68, 3, 2).F(64, 3, 0xea & 7).F(72, 5, 0xea >> 3)         // PLOP3.LUT P3, P4, P0, P1, P2, 0xea, 0x96
                .F(16, 8, 0x96).F(81, 3, 3).F(84, 3, 4);
            P2rAll(a, 2, 0x3f);
            Mov(a, 3, RLane);
            yield return new Test
            {
                Name = "match_plop3",
                Asm = a,
                Expected = l =>
                {
                    uint p1 = (uint)(l >> 1) & 1, p2 = (uint)(l >> 2) & 1;
                    uint p3 = (1 & p1) | p2, p4 = 1 ^ p1 ^ p2;
                    return new uint[] { 0xffffffff, 0, 1 | p1 << 1 | p2 << 2 | p3 << 3 | p4 << 4 };
                },
            };
        }

        // UPLOP3 / UP2UR / VOTEU.
        {
            var a = Prologue();
            a.Emit(0x89c).F(87, 3, PT).F(77, 3, PT).F(68, 3, PT).F(64, 3, 7).F(72, 5, 0x1f)                // UPLOP3.LUT UP0, UP1, UPT, UPT, UPT, 0xff, 0x00
                .F(16, 8, 0).F(81, 3, 0).F(84, 3, 1).F(91, 1, 1);
            a.Emit(0x89c).F(87, 3, 0).F(77, 3, 1).F(68, 3, PT).F(64, 3, 0xfc & 7).F(72, 5, 0xfc >> 3)       // UPLOP3.LUT UP2, UPT, UP0, UP1, UPT, 0xfc, 0x00
                .F(16, 8, 0).F(81, 3, 2).F(84, 3, PT).F(91, 1, 1);
            a.Emit(0x883).F(16, 6, 6).F(24, 6, URZ).F(32, 32, 7).F(91, 1, 1);                                // UP2UR UR6, UPR, URZ, 0x7
            MovUr(a, 0, 6);
            IsetpImm(a, 0, RLane, 3, 1);                                                                      // P0 = lane < 3
            Vote(a, 7, 1, PT, 0, uniform: true);                                                              // VOTEU.ANY UR7, UPT, P0
            MovUr(a, 1, 7);
            MovImm(a, 2, 0x1234);
            Mov(a, 3, RLane);
            yield return new Test { Name = "uniform_preds", Asm = a, Expected = l => new uint[] { 5, 0x7, 0x1234 } };
        }

        // MOVM.16.MT88 and its inverse.
        {
            var a = Prologue();
            a.Alu(0x019, 4, 6, RLane, 1, RZ).F(73, 2, 3);                                                     // R6 = lane << 1
            a.Alu(0x010, 4, 7, 6, 1, RZ).F(81, 3, PT).F(84, 3, PT).F(87, 3, PT).F(90, 1, 1).F(77, 3, PT).F(80, 1, 1); // R7 = R6 + 1
            a.Alu(0x019, 4, 8, 7, 16, RZ).F(73, 2, 3);                                                        // R8 = R7 << 16
            a.Alu(0x012, 1, 9, 6, 8, RZ).F(72, 8, 0xfc).F(81, 3, PT).F(87, 3, PT).F(90, 1, 1);               // R9 = R6 | R8
            a.Emit(0x23a).F(16, 8, 0).F(24, 8, 9);                                                            // MOVM.16.MT88 R0, R9
            a.Emit(0x23a).F(16, 8, 1).F(24, 8, 0);                                                            // MOVM.16.MT88 R1, R0
            Mov(a, 2, 9);
            Mov(a, 3, RLane);
            uint Src(int l) => (uint)(2 * l) | (uint)(2 * l + 1) << 16;
            uint Movm(Func<int, uint> src, int l)
            {
                int half = (l >> 2) & 1;
                uint lo = (src(8 * (l & 3) + (l >> 3)) >> (16 * half)) & 0xffff;
                uint hi = (src(8 * (l & 3) + 4 + (l >> 3)) >> (16 * half)) & 0xffff;
                return lo | hi << 16;
            }
            yield return new Test { Name = "movm", Asm = a, Expected = l => new[] { Movm(Src, l), Src(l), Src(l) } };
        }

        // HMMA.16816.F16 without and with C, HMMA.1688.F32.
        var (data, model) = MatrixData();

        foreach (string variant in new[] { "hmma16816", "hmma16816_c", "hmma1688_f32" })
        {
            var a = Prologue();

            for (int i = 0; i < 8; i++)
            {
                Ldc(a, 10 + i, i * 4);                                                                        // R10-R13 A, R14-R15 B, R16-R17 C
            }

            switch (variant)
            {
                case "hmma16816":
                    a.Emit(0x23c).F(16, 8, 0).F(24, 8, 10).F(32, 8, 14).F(64, 8, RZ).F(75, 1, 1);
                    MovImm(a, 2, 0);
                    break;
                case "hmma16816_c":
                    a.Emit(0x23c).F(16, 8, 0).F(24, 8, 10).F(32, 8, 14).F(64, 8, 16).F(75, 1, 1);
                    MovImm(a, 2, 0);
                    break;
                default:
                    // m16n8k8 uses A registers 0-1 (K columns 0-7) and B register 0; f32 D in R4-R7, C = RZ.
                    a.Emit(0x23c).F(16, 8, 4).F(24, 8, 10).F(32, 8, 14).F(64, 8, RZ).F(76, 1, 1);
                    Mov(a, 0, 4);
                    Mov(a, 1, 5);
                    Mov(a, 2, 6);
                    break;
            }

            Mov(a, 3, RLane);
            string v = variant;
            yield return new Test { Name = variant, Asm = a, Data = data, Expected = l => model(v, l) };
        }
    }

    /// <summary>
    /// Random A (16x16), B (16x8) and C (16x8) in multiples of 1/4 in [-2, 2] (every product and sum is exact in f16), packed into the PTX mma fragments
    /// per lane (A registers 0-3, B 4-5, C 6-7), and the model of D for each variant.
    /// </summary>
    static (uint[], Func<string, int, uint[]>) MatrixData()
    {
        var rng = new Random(86);
        float R() => rng.Next(-8, 9) / 4f;
        float[,] A = new float[16, 16], B = new float[16, 8], C = new float[16, 8];

        for (int r = 0; r < 16; r++) for (int k = 0; k < 16; k++) A[r, k] = R();
        for (int k = 0; k < 16; k++) for (int n = 0; n < 8; n++) B[k, n] = R();
        for (int r = 0; r < 16; r++) for (int n = 0; n < 8; n++) C[r, n] = R();

        static uint H(float f) => BitConverter.HalfToUInt16Bits((Half)f);
        static uint Pack(float lo, float hi) => H(lo) | H(hi) << 16;

        uint[] data = new uint[Lanes * 8];

        for (int l = 0; l < Lanes; l++)
        {
            int g = l >> 2, t = l & 3;

            for (int kb = 0; kb < 2; kb++)
            {
                for (int h = 0; h < 2; h++)
                {
                    data[l * 8 + 2 * kb + h] = Pack(A[g + 8 * h, 8 * kb + 2 * t], A[g + 8 * h, 8 * kb + 2 * t + 1]);
                }

                data[l * 8 + 4 + kb] = Pack(B[8 * kb + 2 * t, g], B[8 * kb + 2 * t + 1, g]);
            }

            for (int h = 0; h < 2; h++)
            {
                data[l * 8 + 6 + h] = Pack(C[g + 8 * h, 2 * t], C[g + 8 * h, 2 * t + 1]);
            }
        }

        uint[] Model(string variant, int l)
        {
            int g = l >> 2, t = l & 3;
            int k = variant == "hmma1688_f32" ? 8 : 16;
            bool withC = variant == "hmma16816_c";
            float D(int r, int n)
            {
                float sum = withC ? C[r, n] : 0f;
                for (int i = 0; i < k; i++) sum += A[r, i] * B[i, n];
                return sum;
            }

            if (variant == "hmma1688_f32")
            {
                return new[] { F32(D(g, 2 * t)), F32(D(g, 2 * t + 1)), F32(D(g + 8, 2 * t)) };
            }

            return new[] { Pack(D(g, 2 * t), D(g, 2 * t + 1)), Pack(D(g + 8, 2 * t), D(g + 8, 2 * t + 1)), 0u };
        }

        static uint F32(float f) => BitConverter.SingleToUInt32Bits(f);

        return (data, Model);
    }
}
#endif
