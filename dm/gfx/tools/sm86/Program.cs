// SM86 frontend harness (docs/reference/shaders-sm86.md). Commands:
//   translate <out dir> [--isa sm86|maxwell] [--msl] [--metal] [--all] [--jobs N] [--complete <list.txt>] <program.bin>...
//       Translates each program to <out>/<key>.spv (and with --msl <key>.metal through spirv-cross, with --metal compiles it with xcrun metal).
//       SM86 graphics programs are the 128-byte NVN2 header + 16-byte instructions, compute programs the instructions only; the stage comes from
//       <key>.meta.json beside the file (ShaderExtract's dump, $W/shader/sm86) or --stage vs|ps|cs. Maxwell programs are the dmsh_xlate layout (0x50-byte SPH + code).
//       Task and mesh programs are skipped (the translator has no such stages). A program is complete when the translator left no note that makes
//       the chain drop it (an unsupported instruction, "is not implemented"...); the summary counts complete programs per stage (and those whose MSL
//       compiled), lists the opcodes and notes that keep the others out (programs each), and --complete writes the complete programs' paths. MSL
//       and AIR are made for complete programs only, or with --all for every program (to check new emitters in programs other families still block).
//   fuzz-maxwell <InstTable.cs> <count> <seed> <out.txt>
//       Generates random Maxwell programs from the instruction table's encodings and writes one digest line per program and target (SPIR-V and GLSL, or the
//       exception). Run with two translator builds (one with -p:Baseline=true -p:RyuSrc=<old copy>) and compare the files: the Maxwell path must not change.
//   hand <out dir>
//       Writes the hand-encoded SM86 test programs (Sm86Programs.cs) with their meta files, for the translate command.
//   warp <out dir> <alu_run>
//       Runs the warp and tensor tests (WarpTests.cs) on the Mac GPU through alu_run (alu_run.swift): each hand-encoded fragment program renders 8x4
//       pixels, one 32-wide SIMD group, and its R0-R3 are compared with a C# model of the warp. Each test runs with and without DebugMode.
//   alu <out dir> <alu_run binary> [--math safe|relaxed|fast] [--filter <substring>] [--jobs N]
//       Runs the hand-encoded ALU programs (AluTests.cs) on the GPU. Build the runner once with
//       xcrun swiftc -O dm/gfx/tools/sm86/alu_run.swift -o $W/sm86/alu_run (outside the repository).
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

sealed class Accessor : IGpuAccessor
{
    readonly byte[] _code;
    readonly byte[] _c1;
    readonly int[] _local;
    int _binding = 1;
    public readonly List<string> Notes = new();

    /// <summary>The binding the translator got for each constant-buffer slot (GPU tests bind their data there).</summary>
    public readonly Dictionary<int, int> ConstantBufferBindings = new();

    /// <summary>Declares colour target 0 as unsigned integer (GPU tests read raw register bits back).</summary>
    public bool UintColor0;

    public Accessor(byte[] code, byte[] c1 = null, int[] local = null)
    {
        _code = code;
        _c1 = c1 ?? Array.Empty<byte>();
        _local = local ?? new[] { 1, 1, 1 };
    }

    public uint ConstantBuffer1Read(int offset) => offset >= 0 && offset + 4 <= _c1.Length ? BitConverter.ToUInt32(_c1, offset) : 0;
    public void Log(string message) => Notes.Add(message);
    public SetBindingPair CreateConstantBufferBinding(int index) { ConstantBufferBindings[index] = _binding; return new(0, _binding++); }
    public SetBindingPair CreateImageBinding(int count, bool isBuffer) { int b = _binding; _binding += count; return new(0, b); }
    public SetBindingPair CreateStorageBufferBinding(int index) => new(0, _binding++);
#if !BASELINE
    public GpuGraphicsState QueryGraphicsState()
    {
        var outputs = new Ryujinx.Common.Memory.Array8<AttributeType>();
        outputs[0] = UintColor0 ? AttributeType.Uint : AttributeType.Float;

        return new GpuGraphicsState(false, InputTopology.Points, false, TessPatchType.Triangles, TessSpacing.EqualSpacing, false, false, false, false, false, 1f,
            AlphaTestOp.Always, 0f, default, true, outputs, false, false, false, false);
    }
#endif
    public SetBindingPair CreateTextureBinding(int count, bool isBuffer) { int b = _binding; _binding += count; return new(0, b); }
    public ReadOnlySpan<ulong> GetCode(ulong address, int minimumSize)
    {
        // Past the end of the program the span is empty: a decoder that runs off the code stops there.
        int start = (int)Math.Min(address, (ulong)_code.Length) & ~7;
        return MemoryMarshal.Cast<byte, ulong>(new ReadOnlySpan<byte>(_code)[start..]);
    }
    public int QueryComputeLocalSizeX() => _local[0];
    public int QueryComputeLocalSizeY() => _local[1];
    public int QueryComputeLocalSizeZ() => _local[2];
    public bool QueryHostSupportsTextureShadowLod() => true;
    public int QuerySamplerArrayLengthFromPool() => 1 << 12;
    public int QueryTextureArrayLengthFromBuffer(int slot) => 32;
    public int QueryTextureArrayLengthFromPool() => 1 << 20;
    public bool QueryHostSupportsDepthClipControl() => false;
}

static class Harness
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: sm86_harness translate|fuzz-maxwell|hand|warp ...");
            return 2;
        }

        return args[0] switch
        {
            "fuzz-maxwell" => MaxwellFuzz.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), args[4]),
            "translate" => Translate(args.Skip(1).ToArray()),
#if !BASELINE
            "hand" => Sm86Programs.Write(args[1]),
            "warp" => WarpTests.Run(args[1], args[2]),
            "alu" => AluTests.Run(args.Skip(1).ToArray()),
#endif
            _ => Usage(args[0]),
        };
    }

    static int Usage(string cmd)
    {
        Console.Error.WriteLine($"unknown command {cmd}");
        return 2;
    }

    public static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..16].ToLowerInvariant();

    static bool compileIncomplete;

    static int Translate(string[] args)
    {
        string outDir = args[0];
        Directory.CreateDirectory(outDir);
        string isa = "sm86", stageArg = null, completeList = null;
        bool msl = false, metal = false;
        int jobs = 1;
        var files = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--isa": isa = args[++i]; break;
                case "--stage": stageArg = args[++i]; break;
                case "--msl": msl = true; break;
                case "--metal": msl = metal = true; break;
                case "--jobs": jobs = int.Parse(args[++i]); break;
                case "--complete": completeList = args[++i]; break;
                case "--all": compileIncomplete = true; break;
                default:
                    // Programs are <key>.bin; <key>.c<bank>.bin beside one is its constant segment (a glob over the dump picks those and the meta files up).
                    // A directory stands for every program in it.
                    foreach (string path in Directory.Exists(args[i]) ? Directory.GetFiles(args[i], "*.bin").OrderBy(f => f, StringComparer.Ordinal).ToArray() : new[] { args[i] })
                    {
                        if (path.EndsWith(".bin") && !System.Text.RegularExpressions.Regex.IsMatch(path, @"\.c\d+\.bin$"))
                        {
                            files.Add(path);
                        }
                    }

                    break;
            }
        }

        string spirvCross = Environment.GetEnvironmentVariable("SPIRV_CROSS") ?? FindSpirvCross();
        var results = new Result[files.Count];

        System.Threading.Tasks.Parallel.For(0, files.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = jobs }, index =>
        {
            results[index] = TranslateOne(files[index], outDir, isa, stageArg, msl, metal, spirvCross);
        });

        int ok = 0, bad = 0, skipped = 0;
        var complete = new SortedDictionary<string, (int Total, int Complete, int CompleteOk)>();
        var missing = new Dictionary<string, int>();
        var notes = new Dictionary<string, int>();
        var completeFiles = new List<string>();

        for (int index = 0; index < files.Count; index++)
        {
            Result r = results[index];
            Console.WriteLine(r.Line);

            if (r.Skipped)
            {
                skipped++;
                continue;
            }

            if (r.Ok)
            {
                ok++;
            }
            else
            {
                bad++;
            }

            complete.TryGetValue(r.Stage, out var c);
            c.Total++;

            if (r.Missing.Count == 0)
            {
                c.Complete++;
                c.CompleteOk += r.Ok ? 1 : 0;

                if (r.Ok)
                {
                    completeFiles.Add(files[index]);
                }

                foreach (string note in r.NoteKinds)
                {
                    notes[note] = notes.GetValueOrDefault(note) + 1;
                }
            }

            foreach (string op in r.Missing)
            {
                missing[op] = missing.GetValueOrDefault(op) + 1;
            }

            complete[r.Stage] = c;
        }

        Console.WriteLine($"translated: {ok} ok, {bad} failed, {skipped} skipped (task / mesh)");

        foreach (var (stage, c) in complete)
        {
            Console.WriteLine($"complete {stage}: {c.Complete} of {c.Total}, {c.CompleteOk} translated{(msl ? metal ? " and compiled" : " to MSL" : "")}");
        }

        Console.WriteLine($"complete: {complete.Values.Sum(c => c.Complete)} of {complete.Values.Sum(c => c.Total)}, {completeFiles.Count} translated{(msl ? metal ? " and compiled" : " to MSL" : "")}");
        Console.WriteLine("unsupported opcodes (programs): " + string.Join(" ", missing.OrderByDescending(m => m.Value).Select(m => $"{m.Key}:{m.Value}")));

        foreach (var (note, count) in notes.OrderByDescending(n => n.Value).Take(30))
        {
            Console.WriteLine($"note in complete programs ({count}): {note}");
        }

        if (completeList != null)
        {
            File.WriteAllLines(completeList, completeFiles.OrderBy(f => f, StringComparer.Ordinal));
        }

        return bad == 0 ? 0 : 1;
    }

    sealed class Result
    {
        public string Line;
        public string Stage;
        public bool Ok;
        public bool Skipped;
        public HashSet<string> Missing = new();
        public HashSet<string> NoteKinds = new();
    }

    static readonly System.Text.RegularExpressions.Regex UnsupportedNote = new(@"^Unsupported SM86 instruction (0x[0-9a-f]+)");
    static readonly System.Text.RegularExpressions.Regex HexNumber = new(@"0x[0-9A-Fa-f]+|\b[0-9a-f]{32}\b|\b\d+\b");

    static Result TranslateOne(string file, string outDir, string isa, string stageArg, bool msl, bool metal, string spirvCross)
    {
        string key = Path.GetFileNameWithoutExtension(file);
        string metaPath = Path.ChangeExtension(file, ".meta.json");
        JsonObject meta = File.Exists(metaPath) ? JsonNode.Parse(File.ReadAllText(metaPath)).AsObject() : new JsonObject();
        string stage = stageArg ?? (string)meta["stage"] ?? "vs";
        var r = new Result { Stage = stage };

        if (stage is "task" or "mesh")
        {
            r.Skipped = true;
            r.Line = $"{key} ({stage}): skipped";
            return r;
        }

        byte[] c1Bytes = File.Exists(Path.ChangeExtension(file, ".c1.bin")) ? File.ReadAllBytes(Path.ChangeExtension(file, ".c1.bin")) : null;
        int[] local = meta["local_size"] is JsonArray la ? la.Select(x => (int)x).ToArray() : null;
        var acc = new Accessor(File.ReadAllBytes(file), c1Bytes, local);

        try
        {
            TranslationFlags flags = TranslationFlags.DebugMode;

            if (stage is "cs" or "compute")
            {
                flags |= TranslationFlags.Compute;
            }

            if (isa == "sm86")
            {
#if BASELINE
                throw new NotSupportedException("this build has no SM86 frontend");
#else
                flags |= TranslationFlags.Sm86;
#endif
            }

            ShaderProgram p = Translator.CreateContext(0, acc, new TranslationOptions(TargetLanguage.Spirv, TargetApi.Vulkan, flags)).Translate();
            string spv = Path.Combine(outDir, key + ".spv");
            File.WriteAllBytes(spv, p.BinaryCode);
            File.WriteAllLines(Path.Combine(outDir, key + ".notes.txt"), acc.Notes);
            string result = $"spv {p.BinaryCode.Length} bytes, {acc.Notes.Count} notes";
            Classify(acc, r);

            // Incomplete programs stop at SPIR-V unless --all asks for them: their MSL says nothing about the frontend's coverage.
            if (msl && (r.Missing.Count == 0 || compileIncomplete))
            {
                string mslPath = Path.Combine(outDir, key + ".metal");
                Run(spirvCross, new[] { "--msl", "--msl-version", "20300", "--msl-argument-buffers", "--msl-texture-buffer-native", "--msl-pad-fragment-output", spv, "--output", mslPath });
                result += ", msl ok";

                if (metal)
                {
                    Run("xcrun", new[] { "-sdk", "macosx", "metal", "-c", "-O1", mslPath, "-o", Path.Combine(outDir, key + ".air") });
                    result += ", metal ok";
                }
            }

            r.Ok = true;
            r.Line = $"{key} ({stage}): {result}{(r.Missing.Count != 0 ? ", incomplete: " + string.Join(" ", r.Missing) : "")}";
        }
        catch (Exception e)
        {
            Classify(acc, r);
            r.Line = $"[RED] {key} ({stage}): {e.GetType().Name}: {e.Message.Split('\n')[0]}";

            if (Environment.GetEnvironmentVariable("SM86_STACK") != null)
            {
                r.Line += "\n" + e.StackTrace;
            }
        }

        return r;
    }

    // The notes that make the chain drop a program (dmsh_build.py SM86_BLOCKING): an unsupported instruction counts by opcode, the others by their text.
    static readonly System.Text.RegularExpressions.Regex BlockingNote = new(@"is not implemented|Failed to find targets for SM86 BRX|jump table not found|: no function at|Failed to find storage buffer for global memory operation");

    static void Classify(Accessor acc, Result r)
    {
        r.Missing.Clear();
        r.NoteKinds.Clear();

        foreach (string note in acc.Notes)
        {
            var m = UnsupportedNote.Match(note);

            if (m.Success)
            {
                r.Missing.Add(m.Groups[1].Value);
            }
            else if (BlockingNote.IsMatch(note))
            {
                r.Missing.Add("'" + HexNumber.Replace(note, "N") + "'");
            }
            else
            {
                r.NoteKinds.Add(HexNumber.Replace(note, "N"));
            }
        }
    }

    // The in-repo spirv-cross, found from this file's path at build time (the harness is built outside the repository).
    internal static string FindSpirvCross([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath), "../spirv_cross_bin/spirv-cross"));
    }

    internal static void Run(string exe, string[] args)
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

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({proc.ExitCode}): {(stderr + stdout).Trim().Split('\n').FirstOrDefault()}");
        }
    }
}
