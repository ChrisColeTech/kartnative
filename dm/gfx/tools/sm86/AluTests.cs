// ALU-family tests of the SM86 frontend (docs/reference/shaders-sm86.md, Tests): hand-encoded fragment programs whose R0-R3 at EXIT are the results
// of the instructions under test, with inputs read from c[0] (so the translator cannot fold them). Each program is translated in-process, converted to
// MSL with the chain's spirv-cross options and run on the Mac's GPU by alu_run (alu_run.swift, built beside the harness); the four words of colour 0
// (an RGBA32Uint target) are compared with reference values computed here from the SM70+ semantics (NAK's hardware-tested folds, PTX).
//   alu <out dir> <alu_run binary> [--math safe|relaxed|fast] [--filter <substring>] [--jobs N]
#if !BASELINE
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

static class AluTests
{
    const int RZ = Sm86Asm.RZ, PT = Sm86Asm.PT, URZ = Sm86Asm.URZ;

    sealed class TestAccessor : IGpuAccessor
    {
        readonly byte[] _code;
        public readonly List<string> Notes = new();

        public TestAccessor(byte[] code) => _code = code;

        public void Log(string message) => Notes.Add(message);
        // c[n] is binding n: alu_run --buf 0 binds the inputs.
        public SetBindingPair CreateConstantBufferBinding(int index) => new(0, index);
        public SetBindingPair CreateImageBinding(int count, bool isBuffer) => new(0, 96);
        public SetBindingPair CreateStorageBufferBinding(int index) => new(0, 64 + index);
        public SetBindingPair CreateTextureBinding(int count, bool isBuffer) => new(0, 112);
        public ReadOnlySpan<ulong> GetCode(ulong address, int minimumSize)
        {
            int start = (int)Math.Min(address, (ulong)_code.Length) & ~7;
            return MemoryMarshal.Cast<byte, ulong>(new ReadOnlySpan<byte>(_code)[start..]);
        }
        public bool QueryHostSupportsTextureShadowLod() => true;
        public int QuerySamplerArrayLengthFromPool() => 1 << 12;
        public int QueryTextureArrayLengthFromBuffer(int slot) => 32;
        public int QueryTextureArrayLengthFromPool() => 1 << 20;
        public bool QueryHostSupportsDepthClipControl() => false;

        // Colour 0 is an unsigned integer output, so the registers reach the target bit for bit.
        public GpuGraphicsState QueryGraphicsState()
        {
            var outputs = new Array8<AttributeType>();
            outputs[0] = AttributeType.Uint;

            return new GpuGraphicsState(false, InputTopology.Points, false, TessPatchType.Triangles, TessSpacing.EqualSpacing, false, false, false, false,
                false, 1f, AlphaTestOp.Always, 0f, default, true, outputs, false, false, true, false);
        }
    }

    /// <summary>One test program: instructions writing R0-R3, its c[0] inputs and the expected words (null: not checked).</summary>
    sealed class Test
    {
        public readonly string Name;
        public readonly Sm86Asm A = new();
        public readonly List<uint> Input = new();
        public readonly uint?[] Expect = new uint?[4];
        public readonly float[] Tolerance = new float[4];
        public int Width = 1, Height = 1;
        public Func<int, int, uint?> PixelExpect; // (pixel, register) for multi-pixel tests

        public Test(string name) => Name = name;

        public int Word(uint value)
        {
            Input.Add(value);
            return (Input.Count - 1) * 4;
        }

        public long Cb(uint value) => Sm86Asm.Cbuf(0, Word(value));

        /// <summary>MOV r, c[0][x] with x holding the value.</summary>
        public void Ld(int r, uint value) => A.Alu(0x002, 5, r, RZ, Cb(value), RZ).F(72, 4, 0xf);

        public void Ld(int r, float value) => Ld(r, Bits(value));

        /// <summary>ULDC ur, c[0][x].</summary>
        public void Uld(int ur, uint value) => A.Emit(0xab9).F(16, 6, ur).F(32, 32, Cb(value)).F(73, 3, 4);

        /// <summary>MOV r, ur.</summary>
        public void FromU(int r, int ur) => A.Alu(0x002, 6, r, RZ, ur, RZ).F(72, 4, 0xf).F(91, 1, 1);

        /// <summary>r = predicate ? 1 : 0, through SEL r, RZ, 1, !p.</summary>
        public void FromP(int r, int p, bool uniform = false)
        {
            if (uniform)
            {
                A.Alu(0x087, 4, r, URZ, 1, URZ).F(87, 3, p).F(90, 1, 1).F(91, 1, 1);
            }
            else
            {
                A.Alu(0x007, 4, r, RZ, 1, RZ).F(87, 3, p).F(90, 1, 1);
            }
        }

        public void Expects(params uint[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                Expect[i] = values[i];
            }
        }

        public void ExpectsF(float tolerance, params float[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                Expect[i] = Bits(values[i]);
                Tolerance[i] = tolerance;
            }
        }
    }

    static uint Bits(float f) => (uint)BitConverter.SingleToInt32Bits(f);
    static float F(uint u) => BitConverter.Int32BitsToSingle((int)u);
    static uint H(float f) => BitConverter.HalfToUInt16Bits((Half)f);
    static float FH(uint h) => (float)BitConverter.UInt16BitsToHalf((ushort)h);
    static uint H2(float lo, float hi) => H(lo) | (H(hi) << 16);

    // References (SM70+ semantics).

    static double RoundMode(double x, int mode) => mode switch
    {
        0 => Math.Round(x, MidpointRounding.ToEven),
        1 => Math.Floor(x),
        2 => Math.Ceiling(x),
        _ => Math.Truncate(x),
    };

    static uint F2IRef(double x, bool signed, int bits, int mode)
    {
        if (double.IsNaN(x))
        {
            return 0;
        }

        double r = RoundMode(x, mode);
        double min = signed ? -Math.Pow(2, bits - 1) : 0;
        double max = signed ? Math.Pow(2, bits - 1) - 1 : Math.Pow(2, bits) - 1;
        r = Math.Clamp(r, min, max);

        return signed ? (uint)(int)(long)r : (uint)(ulong)r;
    }

    static (uint Lo, uint Hi) F2I64Ref(double x, bool signed, int mode)
    {
        if (double.IsNaN(x))
        {
            return (0, 0);
        }

        double r = RoundMode(x, mode);
        ulong v;

        if (signed)
        {
            v = r >= Math.Pow(2, 63) ? (ulong)long.MaxValue : r <= -Math.Pow(2, 63) ? unchecked((ulong)long.MinValue) : unchecked((ulong)(long)r);
        }
        else
        {
            v = r >= Math.Pow(2, 64) ? ulong.MaxValue : r <= 0 ? 0 : (ulong)r;
        }

        return ((uint)v, (uint)(v >> 32));
    }

    /// <summary>An integer rounded to F32 by mode, exactly (BigInteger arithmetic).</summary>
    static float I2FRef(BigInteger v, int mode)
    {
        if (v.IsZero)
        {
            return 0f;
        }

        bool neg = v.Sign < 0;
        BigInteger m = BigInteger.Abs(v);
        int shift = Math.Max((int)m.GetBitLength() - 24, 0);
        BigInteger kept = m >> shift;
        BigInteger rest = m - (kept << shift);
        bool up = false;

        if (shift > 0)
        {
            BigInteger half = BigInteger.One << (shift - 1);
            up = mode switch
            {
                0 => rest > half || (rest == half && !kept.IsEven),
                1 => neg && !rest.IsZero,
                2 => !neg && !rest.IsZero,
                _ => false,
            };
        }

        double f = (double)(kept + (up ? 1 : 0)) * Math.Pow(2, shift);
        return (float)(neg ? -f : f);
    }

    static uint Prmt(uint a, uint c, uint sel, int mode)
    {
        ulong pair = ((ulong)c << 32) | a;
        uint res = 0;

        for (int i = 0; i < 4; i++)
        {
            int n = (int)(sel & 3);
            int nibble = mode switch
            {
                0 => (int)(sel >> (i * 4)) & 0xf,
                1 => (n + i) & 7,
                2 => (n - i) & 7,
                3 => n,
                4 => Math.Max(i, n),
                5 => Math.Min(i, n),
                _ => (n & 1) * 2 + (i & 1),
            };
            uint b = (uint)(pair >> ((nibble & 7) * 8)) & 0xff;

            if ((nibble & 8) != 0)
            {
                b = (b & 0x80) != 0 ? 0xffu : 0;
            }

            res |= b << (i * 8);
        }

        return res;
    }

    static readonly List<Test> _tests = new();

    static Test New(string name)
    {
        var t = new Test(name);
        _tests.Add(t);
        return t;
    }

    // Encoders of the instructions under test (fields: Mesa NAK sm70_encode.rs).

    static Sm86Asm.Inst F2I(Test t, int d, int b, bool signed, int dstSize, int mode, int srcSize = 2) =>
        t.A.Alu(0x105, 1, d, RZ, b, RZ).F(72, 1, signed ? 1 : 0).F(75, 2, dstSize).F(77, 1, 1).F(78, 2, mode).F(84, 2, srcSize);

    static Sm86Asm.Inst I2F(Test t, int baseOp, int d, int b, bool signed, int dstSize, int mode, int srcSize = 2) =>
        t.A.Alu(baseOp, 1, d, RZ, b, RZ).F(74, 1, signed ? 1 : 0).F(75, 2, dstSize).F(78, 2, mode).F(84, 2, srcSize);

    static void AddConversions()
    {
        var rnd = new Random(86);
        float[] specials = { 1.5f, -2.5f, 2.5f, 3.5f, -0.75f, 0.49999997f, 3e9f, -3e9f, 2147483520f, 2147483648f, 4294967040f, 4294967296f,
            65535.5f, 65536f, -32768.6f, 32767.4f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0f, 1e-30f, 255.5f, -129f, 127.5f };

        // F2I: every size and rounding, signed and unsigned.
        foreach ((bool signed, int dstSize) in new[] { (true, 2), (false, 2), (true, 1), (false, 1), (true, 0), (false, 0) })
        {
            for (int mode = 0; mode < 4; mode++)
            {
                for (int chunk = 0; chunk < specials.Length; chunk += 4)
                {
                    var t = New($"F2I.{(signed ? "S" : "U")}{8 << dstSize}.rnd{mode}.{chunk / 4}");

                    for (int r = 0; r < 4; r++)
                    {
                        float x = specials[chunk + r];
                        t.Ld(4 + r, x);
                        F2I(t, r, 4 + r, signed, dstSize, mode);
                        t.Expect[r] = F2IRef(x, signed, 8 << dstSize, mode);
                    }
                }
            }
        }

        // F2I with the constant-buffer (negated) and uniform-register forms, and from an F16 half.
        {
            var t = New("F2I.forms");
            t.A.Alu(0x105, 5, 0, RZ, t.Cb(Bits(7.75f)), RZ).F(72, 1, 1).F(75, 2, 2).F(77, 1, 1).F(78, 2, 1).F(84, 2, 2).F(63, 1, 1);
            t.Uld(4, Bits(-9.5f));
            t.A.Alu(0x105, 6, 1, RZ, 4, RZ).F(72, 1, 1).F(75, 2, 2).F(77, 1, 1).F(78, 2, 0).F(84, 2, 2).F(91, 1, 1);
            t.Ld(8, H2(3.75f, -6.5f));
            F2I(t, 2, 8, true, 2, 3, 1).F(60, 2, 1);
            F2I(t, 3, 8, true, 2, 1, 1);
            t.Expects(unchecked((uint)-8), unchecked((uint)-10), unchecked((uint)-6), 3);
        }

        // F2I to 64 bits.
        {
            double[] xs = { -1.0, 1e12, -3.0e18, 2.0e19, -2.5, 9.3e18, 4294967296.0, -4294967297.0 };

            for (int i = 0; i < xs.Length; i += 2)
            {
                bool signed = i != 6;
                var t = New($"F2I.{(signed ? "S" : "U")}64.{i / 2}");
                t.Ld(8, (float)xs[i]);
                t.Ld(9, (float)xs[i + 1]);
                F2I(t, 0, 8, signed, 3, 3);
                F2I(t, 2, 9, signed, 3, 0);
                var (lo0, hi0) = F2I64Ref((float)xs[i], signed, 3);
                var (lo1, hi1) = F2I64Ref((float)xs[i + 1], signed, 0);
                t.Expects(lo0, hi0, lo1, hi1);
            }
        }

        // I2F / I2FP: 32-bit values around the F32 precision in every rounding mode.
        uint[] ints = { 0xffffffff, 0x01000001, 0x01000003, 0x80000000, 0x7fffffff, 0xfeffffff, 0x00ffffff, 0x81000001, 0x12345678, 0xedcba987, 0, 0x02000005 };

        foreach (int baseOp in new[] { 0x045, 0x106 })
        {
            foreach (bool signed in new[] { false, true })
            {
                for (int mode = 0; mode < 4; mode++)
                {
                    for (int chunk = 0; chunk < ints.Length; chunk += 4)
                    {
                        var t = New($"{(baseOp == 0x045 ? "I2FP" : "I2F")}.{(signed ? "S" : "U")}32.rnd{mode}.{chunk / 4}");

                        for (int r = 0; r < 4; r++)
                        {
                            uint x = ints[chunk + r];
                            t.Ld(4 + r, x);
                            I2F(t, baseOp, r, 4 + r, signed, 2, mode);
                            t.Expect[r] = Bits(I2FRef(signed ? (int)x : x, mode));
                        }
                    }
                }
            }
        }

        // I2F from bytes and halves (selected by bits 60-61), to F16, and from a uniform register.
        {
            var t = New("I2F.small");
            t.Ld(4, 0x80ff7f05u);
            I2F(t, 0x106, 0, 4, true, 2, 0, 0).F(60, 2, 3);
            I2F(t, 0x106, 1, 4, false, 2, 0, 0).F(60, 2, 2);
            I2F(t, 0x106, 2, 4, true, 2, 0, 1).F(60, 2, 1);
            I2F(t, 0x106, 3, 4, false, 1, 0, 1).F(60, 2, 0);
            t.Expects(Bits(-128f), Bits(255f), Bits(-32513f), H(0x7f05));
        }

        {
            var t = New("I2F.uniform");
            t.Uld(4, unchecked((uint)-1000));
            t.A.Alu(0x106, 6, 0, RZ, 4, RZ).F(74, 1, 1).F(75, 2, 2).F(84, 2, 2).F(91, 1, 1);
            t.A.Alu(0x045, 6, 1, RZ, 4, RZ).F(74, 1, 0).F(75, 2, 2).F(78, 2, 3).F(84, 2, 2).F(91, 1, 1);
            t.Expects(Bits(-1000f), Bits(I2FRef(0xfffffc18u, 3)));
        }

        // I2F from 64-bit pairs, every mode.
        for (int i = 0; i < 8; i++)
        {
            bool signed = (i & 1) != 0;
            int mode = i >> 1;
            var t = New($"I2F.{(signed ? "S" : "U")}64.rnd{mode}");

            for (int r = 0; r < 4; r++)
            {
                ulong v = r switch
                {
                    0 => 0x8000_0000_0000_0001UL,
                    1 => 0x0000_0001_0000_0081UL | ((ulong)rnd.Next() << 33),
                    2 => 0xffff_ffff_ffff_fffbUL,
                    _ => ((ulong)(uint)rnd.Next() << 32) | (uint)rnd.Next(),
                };
                t.Ld(8 + r * 2, (uint)v);
                t.Ld(9 + r * 2, (uint)(v >> 32));
                I2F(t, 0x112, r, 8 + r * 2, signed, 2, mode, 3);
                t.Expect[r] = Bits(I2FRef(signed ? (BigInteger)(long)v : (BigInteger)v, mode));
            }
        }

        // FRND in every mode, with abs / neg, and from F16.
        for (int mode = 0; mode < 4; mode++)
        {
            var t = New($"FRND.rnd{mode}");
            float[] xs = { 2.5f, -2.5f, -0.4f, 1e20f };

            for (int r = 0; r < 3; r++)
            {
                t.Ld(4 + r, xs[r]);
                t.A.Alu(0x107, 1, r, RZ, 4 + r, RZ).F(75, 2, 2).F(78, 2, mode).F(84, 2, 2);
                t.Expect[r] = Bits((float)RoundMode(xs[r], mode));
            }

            t.Ld(7, H2(0, 3.5f));
            t.A.Alu(0x107, 1, 3, RZ, 7, RZ).F(75, 2, 1).F(78, 2, mode).F(84, 2, 1).F(60, 2, 1).F(63, 1, 1);
            t.Expect[3] = H((float)RoundMode(-3.5, mode));
        }

        // F2F between F16 and F32; F2FP.PACK_AB and .MERGE_C.
        {
            var t = New("F2F.F2FP");
            t.Ld(4, 1.0009765625f);
            t.A.Alu(0x104, 1, 0, RZ, 4, RZ).F(75, 2, 1).F(84, 2, 2).F(62, 1, 1);
            t.Ld(5, H2(-2.25f, 65504f));
            t.A.Alu(0x104, 1, 1, RZ, 5, RZ).F(75, 2, 2).F(84, 2, 1).F(60, 2, 1).F(63, 1, 1);
            t.Ld(6, 3.140625f);
            t.Ld(7, -0.000061035156f);
            t.A.Alu(0x03e, 1, 2, 6, 7, RZ);
            t.Ld(8, 0x12345678u);
            t.A.Alu(0x03e, 1, 3, RZ, 6, 8).F(78, 1, 1).F(72, 1, 1);
            t.Expects(H(1.0009765625f), Bits(-65504f), H2(-0.000061035156f, 3.140625f), H(3.140625f) | 0x12340000u);
        }
    }

    static void AddInteger()
    {
        var rnd = new Random(70);

        // IADD3 carries: the outputs are the exact sum's bits 32 and up (one, two carries), wherever they occur.
        {
            var t = New("IADD3.carry");
            t.Ld(4, 0xffffffffu);
            t.Ld(5, 1u);
            Sm86Asm.NoPreds(t.A.Alu(0x010, 1, 0, RZ, RZ, 4)).F(81, 3, 0).F(84, 3, 1);   // RZ + RZ + R4: no carry
            Sm86Asm.NoPreds(t.A.Alu(0x010, 1, 6, 5, RZ, 4)).F(81, 3, 2).F(84, 3, 3);    // 1 + 0 + 0xffffffff: one carry in the third add
            Sm86Asm.NoPreds(t.A.Alu(0x010, 1, 7, 4, 4, 4)).F(81, 3, 4).F(84, 3, 5);     // 3 * 0xffffffff: two carries
            t.FromP(0, 0);
            t.FromP(1, 2);
            t.FromP(2, 4);
            t.FromP(3, 5);
            t.Expects(0, 1, 1, 1);
        }

        // 64-bit subtraction and addition through IADD3 / IADD3.X (the .X modifier bit inverts).
        {
            var t = New("IADD3.X");
            ulong x = 0x0000_0005_0000_0003UL, y = 0x0000_0001_0000_0007UL;
            t.Ld(4, (uint)x);
            t.Ld(5, (uint)(x >> 32));
            t.Ld(6, (uint)y);
            t.Ld(7, (uint)(y >> 32));
            Sm86Asm.NoPreds(t.A.Alu(0x010, 1, 0, 4, 6, RZ)).F(63, 1, 1).F(81, 3, 0);                     // R0 = R4 - R6, P0 = carry
            Sm86Asm.NoPreds(t.A.Alu(0x010, 1, 1, 5, 7, RZ)).F(63, 1, 1).F(74, 1, 1).F(87, 3, 0).F(90, 1, 0); // R1 = R5 + ~R7 + P0
            Sm86Asm.NoPreds(t.A.Alu(0x010, 1, 2, 4, 6, RZ)).F(81, 3, 1);                                 // R2 = R4 + R6, P1
            Sm86Asm.NoPreds(t.A.Alu(0x010, 1, 3, 5, 7, RZ)).F(74, 1, 1).F(87, 3, 1).F(90, 1, 0);         // R3 = R5 + R7 + P1
            ulong diff = x - y, sum = x + y;
            t.Expects((uint)diff, (uint)(diff >> 32), (uint)sum, (uint)(sum >> 32));
        }

        // IMAD.X (carry in), IMAD.HI (unsigned and signed), IMAD with a negated addend.
        {
            var t = New("IMAD.X.HI");
            t.Ld(4, 0xfffffff0u);
            t.Ld(5, 0x20u);
            t.Ld(6, 0xdeadbeefu);
            Sm86Asm.NoPreds(t.A.Alu(0x010, 1, 7, 4, 5, RZ)).F(81, 3, 1);                                          // carry into P1
            t.A.Alu(0x024, 1, 0, RZ, RZ, 6).F(74, 1, 1).F(81, 3, PT).F(87, 3, 1).F(90, 1, 0);                    // IMAD.X R0 = RZ*RZ + R6 + P1
            t.A.Alu(0x027, 1, 1, 6, 4, 5).F(81, 3, PT).F(87, 3, PT).F(90, 1, 1);                                  // IMAD.HI.U32
            t.A.Alu(0x027, 1, 2, 6, 4, RZ).F(73, 1, 1).F(81, 3, PT).F(87, 3, PT).F(90, 1, 1);                     // IMAD.HI (signed)
            t.A.Alu(0x024, 1, 3, 5, 5, 6).F(75, 1, 1).F(81, 3, PT).F(87, 3, PT).F(90, 1, 1);                      // R5 * R5 - R6
            t.Expects(0xdeadbeefu + 1, (uint)(((ulong)0xdeadbeefu * 0xfffffff0u) >> 32) + 0x20,
                unchecked((uint)(((long)(int)0xdeadbeefu * (int)0xfffffff0u) >> 32)), unchecked(0x400 - 0xdeadbeefu));
        }

        // IMAD.WIDE (unsigned, signed with a 64-bit addend) and UIMAD.WIDE.
        {
            var t = New("IMAD.WIDE");
            t.Ld(4, 0xfffffffbu);
            t.Ld(5, 0x12345u);
            t.Ld(6, 0xffffffffu);
            t.Ld(7, 0x7u);
            t.A.Alu(0x025, 1, 0, 4, 5, 6).F(73, 1, 1).F(81, 3, PT);                                              // signed, + R6:R7
            t.Uld(8, 0xfffffffbu);
            t.Uld(9, 0x10u);
            t.A.Alu(0x0a5, 4, 10, 8, 0x30, URZ).F(81, 3, PT).F(91, 1, 1);                                        // UIMAD.WIDE.U32 UR10 = UR8 * 0x30
            t.FromU(2, 10);
            t.FromU(3, 11);
            long w = unchecked((long)(int)0xfffffffbu * 0x12345L + (long)((7UL << 32) | 0xffffffffUL));
            ulong uw = 0xfffffffbUL * 0x30UL;
            t.Expects((uint)w, (uint)((ulong)w >> 32), (uint)uw, (uint)(uw >> 32));
        }

        // LEA: shift-add, negated, .HI with C, .HI.SX32, carry out and .X carry in, the constant-buffer form.
        {
            var t = New("LEA");
            t.Ld(4, 0x80000003u);
            t.Ld(5, 0xfffffff0u);
            t.Ld(6, 0x00000007u);
            Sm86Asm.NoPreds(t.A.Alu(0x011, 1, 0, 4, 5, RZ)).F(75, 5, 3).F(80, 1, 0).F(81, 3, 0);                // R4 << 3 + R5, P0
            Sm86Asm.NoPreds(t.A.Alu(0x011, 1, 1, 4, 5, 6)).F(75, 5, 3).F(80, 1, 1).F(74, 1, 1).F(87, 3, 0).F(90, 1, 0); // LEA.HI.X (R6:R4) << 3 >> 32 + R5 + P0
            Sm86Asm.NoPreds(t.A.Alu(0x011, 1, 2, 4, 5, RZ)).F(75, 5, 5).F(80, 1, 1).F(73, 1, 1);                // LEA.HI.SX32
            Sm86Asm.NoPreds(t.A.Alu(0x011, 5, 3, 5, t.Cb(100), RZ)).F(75, 5, 2).F(80, 1, 0).F(72, 1, 1);        // -(R5 << 2) + c[0][x]
            ulong pair = (7UL << 32) | 0x80000003UL;
            ulong lo = (0x80000003UL << 3 & 0xffffffff) + 0xfffffff0;
            uint carry = (uint)(lo >> 32);
            uint sx = unchecked((uint)((ulong)(long)(int)0x80000003u << 5 >> 32));
            t.Expects((uint)lo, unchecked((uint)(pair >> 29) + 0xfffffff0 + carry), unchecked(sx + 0xfffffff0), unchecked(100 - (0xfffffff0u << 2)));
        }

        // ULEA, UPRMT, UFLO, UPOPC, UBREV, USGXT on uniform registers.
        {
            var t = New("uniform.bitops");
            t.Uld(4, 0x00f0_0f00u);
            t.Uld(5, 0x1234_5678u);
            Sm86Asm.NoPreds(t.A.Alu(0x091, 1, 6, 4, 5, URZ)).F(75, 5, 4).F(80, 1, 0).F(91, 1, 1);                 // ULEA UR6 = UR4 << 4 + UR5
            t.A.Alu(0x096, 4, 7, 4, 0x3715, 5).F(91, 1, 1);                                                         // UPRMT UR7 = UR4, 0x3715, UR5
            t.A.Alu(0x0bd, 1, 8, URZ, 4, URZ).F(81, 3, PT).F(91, 1, 1);                                              // UFLO.U32 UR8 = UR4
            t.A.Alu(0x0bf, 1, 9, URZ, 5, URZ).F(91, 1, 1);                                                          // UPOPC UR9 = UR5
            t.FromU(0, 6);
            t.FromU(1, 7);
            t.FromU(2, 8);
            t.FromU(3, 9);
            t.Expects((0x00f00f00u << 4) + 0x12345678u, Prmt(0x00f00f00u, 0x12345678u, 0x3715, 0), 23, (uint)BitOperations.PopCount(0x12345678u));
        }

        {
            var t = New("uniform.brev.sgxt");
            t.Uld(4, 0x0000_00c5u);
            t.Uld(5, 7u);
            t.A.Alu(0x0be, 1, 6, URZ, 4, URZ).F(91, 1, 1);                                                          // UBREV
            t.A.Alu(0x09a, 1, 7, 4, 5, URZ).F(73, 1, 1).F(91, 1, 1);                                                // USGXT (signed) UR4, 7 bits
            t.A.Alu(0x09b, 1, 8, 5, 5, URZ).F(91, 1, 1);                                                            // UBMSK UR5, UR5: 7 ones at bit 7
            t.A.Alu(0x013, 1, 0, RZ, RZ, RZ);                                                                       // IABS R0 = |RZ|
            t.FromU(1, 6);
            t.FromU(2, 7);
            t.FromU(3, 8);
            t.Expects(0, 0xa3000000u, 0xffffffc5u, 0x7fu << 7);
        }

        // PRMT: random selectors in index mode (immediate: static code; register: dynamic code), and the other modes both ways.
        for (int i = 0; i < 6; i++)
        {
            var t = New($"PRMT.index.{i}");
            uint a = (uint)rnd.Next() ^ 0x80008000, c = (uint)rnd.Next() | 0x00800080;
            t.Ld(4, a);
            t.Ld(5, c);

            for (int r = 0; r < 4; r++)
            {
                uint sel = (uint)rnd.Next() & 0xffff;

                if (i == 0)
                {
                    sel = r switch { 0 => 0x5410, 1 => 0x3276, 2 => 0x0123, _ => 0x8888 };
                }

                if (r % 2 == 0)
                {
                    t.A.Alu(0x016, 4, r, 4, sel, 5);
                }
                else
                {
                    t.Ld(8 + r, sel);
                    t.A.Alu(0x016, 1, r, 4, 8 + r, 5);
                }

                t.Expect[r] = Prmt(a, c, sel, 0);
            }
        }

        for (int mode = 1; mode < 7; mode++)
        {
            var t = New($"PRMT.mode{mode}");
            uint a = 0x83828180, c = 0x87868584;
            t.Ld(4, a);
            t.Ld(5, c);

            for (int r = 0; r < 4; r++)
            {
                uint sel = (uint)(r == 3 ? 2 : r) | 0xfff0;

                if (r < 2)
                {
                    t.A.Alu(0x016, 4, r, 4, sel, 5).F(72, 3, mode);
                }
                else
                {
                    t.Ld(8 + r, sel + (uint)(r - 2));
                    t.A.Alu(0x016, 1, r, 4, 8 + r, 5).F(72, 3, mode);
                    sel += (uint)(r - 2);
                }

                t.Expect[r] = Prmt(a, c, sel, mode);
            }
        }

        // FLO (unsigned, signed, .SH, inverted, zero), POPC, BREV, IABS.
        {
            var t = New("FLO");
            t.Ld(4, 0x00012345u);
            t.Ld(5, 0xfffff000u);
            t.A.Alu(0x100, 1, 0, RZ, 4, RZ).F(81, 3, PT);
            t.A.Alu(0x100, 1, 1, RZ, 5, RZ).F(73, 1, 1).F(81, 3, PT);
            t.A.Alu(0x100, 1, 2, RZ, 4, RZ).F(74, 1, 1).F(81, 3, PT);
            t.A.Alu(0x100, 1, 3, RZ, RZ, RZ).F(74, 1, 1).F(81, 3, PT);
            t.Expects(16, 11, 15, 0xffffffff);
        }

        {
            var t = New("POPC.BREV.IABS");
            t.Ld(4, 0xf0f0_0001u);
            t.A.Alu(0x109, 1, 0, RZ, 4, RZ).F(63, 1, 1);
            t.A.Alu(0x100, 1, 1, RZ, 4, RZ).F(63, 1, 1).F(81, 3, PT);
            t.A.Alu(0x101, 1, 2, RZ, 4, RZ);
            t.Ld(5, 0x80000000u);
            t.A.Alu(0x013, 1, 3, RZ, 5, RZ);
            t.Expects((uint)BitOperations.PopCount(~0xf0f00001u), 27, 0x80000f0fu, 0x80000000u);
        }

        // SGXT (clamp and wrap) and BMSK (clamp and wrap).
        {
            var t = New("SGXT.BMSK");
            t.Ld(4, 0x0001_8f00u);
            t.Ld(5, 40u);
            t.A.Alu(0x01a, 4, 0, 4, 16, RZ).F(73, 1, 1);
            t.A.Alu(0x01a, 1, 1, 4, 5, RZ).F(73, 1, 1);
            t.A.Alu(0x01a, 1, 2, 4, 5, RZ).F(73, 1, 1).F(75, 1, 1);
            t.Ld(6, 30u);
            t.A.Alu(0x01b, 1, 3, 6, 5, RZ);
            t.Expects(0xffff8f00u, 0x00018f00u, 0x00000000u, 0xc0000000u);
        }

        // IDP.4A signed x unsigned.
        {
            var t = New("IDP.4A");
            t.Ld(4, 0x80ff7f02u);
            t.Ld(5, 0x0203ff04u);
            t.Ld(6, 1000u);
            t.A.Alu(0x026, 1, 0, 4, 5, 6).F(73, 1, 1);
            t.A.Alu(0x026, 1, 1, 4, 5, RZ).F(74, 1, 1);
            int s0 = 2 * 4 + 127 * 255 + (-1) * 3 + (-128) * 2 + 1000;
            int s1 = 2 * 4 + 127 * -1 + 255 * 3 + 128 * 2;
            t.Expects((uint)s0, (uint)s1);
        }
    }

    static void AddFloat()
    {
        // FSET.BF, with abs / neg and a predicate combine.
        {
            var t = New("FSET");
            t.Ld(4, -2f);
            t.Ld(5, 1f);
            t.A.Alu(0x00a, 1, 0, 4, 5, RZ).F(76, 4, 1).F(87, 3, PT);                                  // -2 < 1
            t.A.Alu(0x00a, 1, 1, 4, 5, RZ).F(76, 4, 1).F(73, 1, 1).F(87, 3, PT);                     // |-2| < 1
            t.Ld(6, float.NaN);
            t.A.Alu(0x00a, 1, 2, 6, 5, RZ).F(76, 4, 9).F(87, 3, PT);                                  // NaN <U 1
            t.A.Alu(0x00a, 4, 3, 5, Bits(1f), RZ).F(76, 4, 2).F(74, 2, 0).F(87, 3, PT).F(90, 1, 1); // (1 == 1) AND !PT
            t.Expects(Bits(1f), 0, Bits(1f), 0);
        }

        // MUFU: SIN / COS take turns.
        {
            var t = New("MUFU.trig");
            t.Ld(4, 0.25f);
            t.Ld(5, 0.125f);
            t.A.Alu(0x108, 1, 0, RZ, 4, RZ).F(74, 6, 1);
            t.A.Alu(0x108, 1, 1, RZ, 4, RZ).F(74, 6, 0);
            t.A.Alu(0x108, 1, 2, RZ, 5, RZ).F(74, 6, 1);
            t.A.Alu(0x108, 1, 3, RZ, 5, RZ).F(74, 6, 0).F(63, 1, 1);
            t.ExpectsF(2e-6f, 1f, 0f, MathF.Sqrt(0.5f), MathF.Sqrt(0.5f));
        }

        {
            var t = New("MUFU.other");
            t.Ld(4, 9f);
            t.A.Alu(0x108, 1, 0, RZ, 4, RZ).F(74, 6, 8);
            t.A.Alu(0x108, 1, 1, RZ, 4, RZ).F(74, 6, 5);
            t.Ld(5, 0.5f);
            t.A.Alu(0x108, 1, 2, RZ, 5, RZ).F(74, 6, 9);
            t.Ld(6, H2(0, 3f));
            t.A.Alu(0x108, 1, 3, RZ, 6, RZ).F(74, 6, 2).F(73, 1, 1).F(60, 2, 1);
            t.ExpectsF(1e-6f, 3f, 1f / 3f, MathF.Tanh(0.5f));
            t.Tolerance[2] = 1e-3f;
            t.Expect[3] = H(8f);
        }
    }

    static void AddHalf()
    {
        // HADD2.F32 (the half to float conversion), HADD2 with swizzles and saturate, HMUL2.RELU.
        {
            var t = New("HADD2.HMUL2");
            t.Ld(4, H2(1.5f, -3.25f));
            t.Ld(5, H2(0.25f, 0.5f));
            t.A.Alu(0x030, 1, 0, RZ, 4, RZ).F(72, 1, 1).F(78, 1, 1).F(60, 2, 3);                          // HADD2.F32 -RZ, R4.H1_H1
            t.A.Alu(0x030, 1, 1, 4, 5, RZ).F(74, 2, 2).F(60, 2, 0).F(77, 1, 1);                          // HADD2.SAT R4.H0_H0, R5
            t.A.Alu(0x032, 1, 2, 4, 5, RZ).F(79, 1, 1).F(63, 1, 1);                                      // HMUL2.RELU R4, -R5
            t.A.Alu(0x030, 4, 3, 4, H2(2f, 2f), RZ).F(73, 1, 1);                                          // HADD2 |R4|, imm
            t.Expects(Bits(-3.25f), H2(1f, 1f), H2(0f, 1.625f), H2(3.5f, 5.25f));
        }

        // HFMA2 with the C register's swizzle and neg (bits 81-84), and the F32 swizzle of a 32-bit float.
        {
            var t = New("HFMA2");
            t.Ld(4, H2(2f, 3f));
            t.Ld(5, H2(4f, 5f));
            t.Ld(6, H2(0.5f, 10f));
            t.Ld(7, 1.25f);
            t.A.Alu(0x031, 1, 0, 4, 5, 6).F(81, 2, 3).F(84, 1, 1);                                        // R4 * R5 - R6.H1_H1
            t.A.Alu(0x031, 1, 1, 4, 7, 6).F(60, 2, 1);                                                    // R4 * R7(F32) + R6
            t.A.Alu(0x031, 2, 2, 4, H2(1f, -1f), 5);                                                      // R4 * R5 + imm (form 2: C imm, B in 64-71)
            t.A.Alu(0x032, 1, 3, 4, 5, RZ).F(78, 1, 1);                                                   // HMUL2.F32: lane 0 as F32
            t.Expects(H2(-2f, 5f), H2(3f, 13.75f), H2(9f, 14f), Bits(8f));
        }

        // HSETP2 per lane and .H_AND, HSET2 .BF and masks, HMNMX2 min / max, NaN and .XORSIGN.
        {
            var t = New("HSETP2.HSET2");
            t.Ld(4, H2(1f, 5f));
            t.Ld(5, H2(2f, 2f));
            t.A.Alu(0x034, 1, 0, 4, 5, 0).F(76, 4, 1).F(81, 3, 0).F(84, 3, 1).F(87, 3, PT);             // lanes 1 < 2, 5 < 2
            t.A.Alu(0x034, 1, 0, 4, 5, 0).F(76, 4, 1).F(71, 1, 1).F(81, 3, 2).F(84, 3, 3).F(87, 3, PT);  // .H_AND
            t.FromP(0, 0);
            t.FromP(1, 1);
            t.FromP(2, 3);
            t.A.Alu(0x033, 1, 3, 4, 5, 0).F(76, 4, 4).F(71, 1, 1).F(87, 3, PT);                           // HSET2.BF.GT
            t.Expects(1, 0, 1, H2(0f, 1f));
        }

        {
            var t = New("HMNMX2");
            t.Ld(4, H2(-1f, 7f));
            t.Ld(5, 0x7e00u | (H(-8f) << 16));
            t.A.Alu(0x040, 1, 0, 4, RZ, RZ).F(60, 2, 2).F(87, 3, PT).F(90, 1, 1);                          // max(R4, RZ.H0_H0)
            t.A.Alu(0x040, 1, 1, 4, 5, RZ).F(87, 3, PT);                                                  // min(R4, R5): NaN lane gives -1
            t.A.Alu(0x040, 1, 2, 4, 5, RZ).F(87, 3, PT).F(81, 1, 1);                                      // .NAN
            t.A.Alu(0x040, 4, 3, 4, H2(3f, 2f), RZ).F(87, 3, PT).F(82, 1, 1);                             // .XORSIGN min(|a|, |b|)
            t.Expect[0] = H2(0f, 7f);
            t.Expect[1] = H2(-1f, -8f);
            t.Expect[2] = 0x7fffu | (H(-8f) << 16);
            t.Expect[3] = H2(-1f, 2f);
        }

        // FSWZADD over a 2 x 2 quad: lane l uses code (ops >> (6 - 2l)) & 3 (0 A + B, 1 A - B, 2 B - A, 3 B).
        {
            var t = New("FSWZADD");
            t.Ld(4, 10f);
            t.Ld(5, 3f);
            t.A.Emit(0x822).F(16, 8, 0).F(24, 8, 4).F(64, 8, 5).F(32, 8, 0b00_01_10_11);
            t.A.Emit(0x822).F(16, 8, 1).F(24, 8, 4).F(64, 8, 5).F(32, 8, 0b11_10_01_00);
            t.Width = 2;
            t.Height = 2;
            float[] codes = { 13f, 7f, -7f, 3f };
            t.PixelExpect = (pixel, reg) =>
            {
                int lane = (pixel / 2 % 2) * 2 + pixel % 2;
                return reg switch
                {
                    0 => Bits(codes[lane]),
                    1 => Bits(codes[3 - lane]),
                    _ => null,
                };
            };
        }
    }

    // Program header: SPH version 4 pixel program (the MKW word 0), colour 0 RGBA written.
    static byte[] Header()
    {
        int[] words = new int[32];
        words[0] = 0x000c5482;
        words[18] = 0xf;
        byte[] header = new byte[0x80];
        Buffer.BlockCopy(words, 0, header, 0, 0x80);
        return header;
    }

    public static int Run(string[] args)
    {
        string outDir = args[0];
        string runner = args[1];
        string math = "safe", filter = null;
        int jobs = 4;

        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--math": math = args[++i]; break;
                case "--filter": filter = args[++i]; break;
                case "--jobs": jobs = int.Parse(args[++i]); break;
            }
        }

        Directory.CreateDirectory(outDir);
        AddConversions();
        AddInteger();
        AddFloat();
        AddHalf();

        string spirvCross = Environment.GetEnvironmentVariable("SPIRV_CROSS") ?? FindSpirvCross();
        List<Test> tests = _tests.Where(t => filter == null || t.Name.Contains(filter)).ToList();
        string[] results = new string[tests.Count];
        int failed = 0;

        Parallel.For(0, tests.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, index =>
        {
            Test t = tests[index];
            string error = RunTest(t, outDir, runner, spirvCross, math);
            results[index] = error == null ? $"ok   {t.Name}" : $"[RED] {t.Name}: {error}";

            if (error != null)
            {
                System.Threading.Interlocked.Increment(ref failed);
            }
        });

        foreach (string line in results)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine($"alu tests: {tests.Count - failed} of {tests.Count} passed (math {math})");
        return failed == 0 ? 0 : 1;
    }

    static string RunTest(Test t, string outDir, string runner, string spirvCross, string math)
    {
        t.A.Emit(0x94d).F(87, 3, PT);
        byte[] code = t.A.Assemble(Header());
        string stem = Path.Combine(outDir, t.Name);
        var acc = new TestAccessor(code);
        ShaderProgram program;

        try
        {
            program = Translator.CreateContext(0, acc, new TranslationOptions(TargetLanguage.Spirv, TargetApi.Vulkan, TranslationFlags.Sm86)).Translate();
        }
        catch (Exception e)
        {
            return $"translation: {e.GetType().Name}: {e.Message}";
        }

        if (acc.Notes.Count != 0)
        {
            return "translator notes: " + string.Join(" | ", acc.Notes);
        }

        File.WriteAllBytes(stem + ".spv", program.BinaryCode);
        File.WriteAllBytes(stem + ".c0.bin", MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(t.Input)).ToArray());

        string error = Exec(spirvCross, "--msl", "--msl-version", "20300", "--msl-argument-buffers", "--msl-texture-buffer-native", "--msl-pad-fragment-output",
            stem + ".spv", "--output", stem + ".metal");

        error ??= Exec(runner, stem + ".metal", stem + ".out", "--size", t.Width.ToString(), t.Height.ToString(), "--math", math, "--buf", "0", stem + ".c0.bin");

        if (error != null)
        {
            return error;
        }

        byte[] output = File.ReadAllBytes(stem + ".out");
        var mismatches = new List<string>();

        for (int pixel = 0; pixel < t.Width * t.Height; pixel++)
        {
            for (int reg = 0; reg < 4; reg++)
            {
                uint? expect = t.PixelExpect != null ? t.PixelExpect(pixel, reg) : t.Expect[reg];
                uint got = BitConverter.ToUInt32(output, pixel * 16 + reg * 4);

                if (expect is not uint want || got == want)
                {
                    continue;
                }

                float tolerance = t.PixelExpect == null ? t.Tolerance[reg] : 0;

                if (tolerance > 0 && Math.Abs(F(got) - F(want)) <= tolerance * Math.Max(1f, Math.Abs(F(want))))
                {
                    continue;
                }

                mismatches.Add($"{(t.Width * t.Height > 1 ? $"pixel {pixel} " : "")}R{reg} = 0x{got:x8} ({F(got)}), want 0x{want:x8} ({F(want)})");
            }
        }

        return mismatches.Count == 0 ? null : string.Join("; ", mismatches);
    }

    static string Exec(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true };

        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi);
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        return proc.ExitCode == 0 ? null : $"{Path.GetFileName(exe)} exited {proc.ExitCode}: {(stderr + stdout).Trim().Split('\n')[0]}";
    }

    static string FindSpirvCross([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath), "../spirv_cross_bin/spirv-cross"));
    }
}
#endif
