// SM86 frontend harness (docs/reference/shaders-sm86.md). Commands:
//   translate <out dir> [--isa sm86|maxwell] [--msl] [--metal] <program.bin>...
//       Translates each program to <out>/<key>.spv (and with --msl <key>.metal through spirv-cross, with --metal compiles it with xcrun metal).
//       SM86 graphics programs are the 128-byte NVN2 header + 16-byte instructions, compute programs the instructions only; the stage comes from
//       <key>.meta.json beside the file (ShaderExtract's dump, $W/shader/sm86) or --stage vs|ps|cs. Maxwell programs are the dmsh_xlate layout (0x50-byte SPH + code).
//   fuzz-maxwell <InstTable.cs> <count> <seed> <out.txt>
//       Generates random Maxwell programs from the instruction table's encodings and writes one digest line per program and target (SPIR-V and GLSL, or the
//       exception). Run with two translator builds (one with -p:Baseline=true -p:RyuSrc=<old copy>) and compare the files: the Maxwell path must not change.
//   hand <out dir>
//       Writes the hand-encoded SM86 test programs (Sm86Asm.cs) with their meta files, for the translate command.
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

    public Accessor(byte[] code, byte[] c1 = null, int[] local = null)
    {
        _code = code;
        _c1 = c1 ?? Array.Empty<byte>();
        _local = local ?? new[] { 1, 1, 1 };
    }

    public uint ConstantBuffer1Read(int offset) => offset >= 0 && offset + 4 <= _c1.Length ? BitConverter.ToUInt32(_c1, offset) : 0;
    public void Log(string message) => Notes.Add(message);
    public SetBindingPair CreateConstantBufferBinding(int index) => new(0, _binding++);
    public SetBindingPair CreateImageBinding(int count, bool isBuffer) { int b = _binding; _binding += count; return new(0, b); }
    public SetBindingPair CreateStorageBufferBinding(int index) => new(0, _binding++);
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
            Console.Error.WriteLine("usage: sm86_harness translate|fuzz-maxwell|hand ...");
            return 2;
        }

        return args[0] switch
        {
            "fuzz-maxwell" => MaxwellFuzz.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), args[4]),
            "translate" => Translate(args.Skip(1).ToArray()),
#if !BASELINE
            "hand" => Sm86Programs.Write(args[1]),
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

    static int Translate(string[] args)
    {
        string outDir = args[0];
        Directory.CreateDirectory(outDir);
        string isa = "sm86", stageArg = null;
        bool msl = false, metal = false;
        var files = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--isa": isa = args[++i]; break;
                case "--stage": stageArg = args[++i]; break;
                case "--msl": msl = true; break;
                case "--metal": msl = metal = true; break;
                default:
                    // Programs are <key>.bin; <key>.c<bank>.bin beside one is its constant segment (a glob over the dump picks those and the meta files up).
                    if (args[i].EndsWith(".bin") && !System.Text.RegularExpressions.Regex.IsMatch(args[i], @"\.c\d+\.bin$"))
                    {
                        files.Add(args[i]);
                    }

                    break;
            }
        }

        string spirvCross = Environment.GetEnvironmentVariable("SPIRV_CROSS") ?? FindSpirvCross();
        int ok = 0, bad = 0;

        foreach (string file in files)
        {
            string key = Path.GetFileNameWithoutExtension(file);
            string metaPath = Path.ChangeExtension(file, ".meta.json");
            JsonObject meta = File.Exists(metaPath) ? JsonNode.Parse(File.ReadAllText(metaPath)).AsObject() : new JsonObject();
            string stage = stageArg ?? (string)meta["stage"] ?? "vs";
            byte[] c1Bytes = File.Exists(Path.ChangeExtension(file, ".c1.bin")) ? File.ReadAllBytes(Path.ChangeExtension(file, ".c1.bin")) : null;
            int[] local = meta["local_size"] is JsonArray la ? la.Select(x => (int)x).ToArray() : null;

            try
            {
                byte[] code = File.ReadAllBytes(file);
                var acc = new Accessor(code, c1Bytes, local);
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

                if (msl)
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

                Console.WriteLine($"{key} ({stage}): {result}");
                ok++;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[RED] {key} ({stage}): {e.GetType().Name}: {e.Message.Split('\n')[0]}");
                if (Environment.GetEnvironmentVariable("SM86_STACK") != null)
                {
                    Console.WriteLine(e.StackTrace);
                }

                bad++;
            }
        }

        Console.WriteLine($"translated: {ok} ok, {bad} failed");
        return bad == 0 ? 0 : 1;
    }

    // The in-repo spirv-cross, found from this file's path at build time (the harness is built outside the repository).
    static string FindSpirvCross([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath), "../spirv_cross_bin/spirv-cross"));
    }

    static void Run(string exe, string[] args)
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
