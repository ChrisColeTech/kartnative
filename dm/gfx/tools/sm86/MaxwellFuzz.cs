// Random Maxwell programs for comparing two translator builds (the fuzz-maxwell command, see Program.cs). The encodings come from the translator's own
// instruction table (Decoders/InstTable.cs, read as text): each instruction takes a table pattern with its free bits random, small register numbers,
// mostly PT guards and in-program targets for the relative branches, so most programs decode and translate.
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

static class MaxwellFuzz
{
    readonly record struct Pattern(ulong Value, ulong Mask, string Name);

    static readonly HashSet<string> Excluded = new() { "Jmp", "Jmx", "Jcal", "Invalid" };
    static readonly HashSet<string> Relative = new() { "Bra", "Ssy", "Pbk", "Pcnt", "Cal" };

    public static int Run(string instTablePath, int count, int seed, string outPath)
    {
        var patterns = new List<Pattern>();
        Pattern exit = default;

        foreach (Match m in Regex.Matches(File.ReadAllText(instTablePath), "Add\\(\"([01x]{64})\",\\s*InstName\\.(\\w+)"))
        {
            string bits = m.Groups[1].Value;
            ulong value = 0, mask = 0;

            for (int i = 0; i < 64; i++)
            {
                ulong bit = 1UL << (63 - i);
                if (bits[i] != 'x')
                {
                    mask |= bit;
                    if (bits[i] == '1')
                    {
                        value |= bit;
                    }
                }
            }

            var p = new Pattern(value, mask, m.Groups[2].Value);
            if (p.Name == "Exit")
            {
                exit = p;
            }

            if (!Excluded.Contains(p.Name))
            {
                patterns.Add(p);
            }
        }

        var rng = new Random(seed);
        var lines = new List<string>();
        string[] stages = { "cs", "vs", "ps", "gs", "tcs", "tes" };

        for (int index = 0; index < count; index++)
        {
            string stage = stages[rng.Next(stages.Length)];
            byte[] code = Generate(rng, patterns, exit, stage);

            foreach ((TargetLanguage lang, TargetApi api, TranslationFlags extra) in new[]
            {
                (TargetLanguage.Spirv, TargetApi.Vulkan, TranslationFlags.None),
                (TargetLanguage.Glsl, TargetApi.OpenGL, TranslationFlags.DebugMode),
            })
            {
                lines.Add($"{index} {stage} {lang} {Harness.Digest(code)} {TranslateOne(code, stage, lang, api, extra)}");
            }
        }

        File.WriteAllLines(outPath, lines);
        Console.WriteLine($"{count} programs, {lines.Count(l => l.Contains(" ok "))} translations ok, {lines.Count(l => l.Contains(" exc "))} exceptions, {lines.Count(l => l.Contains(" timeout"))} timeouts");
        return 0;
    }

    static string TranslateOne(byte[] code, string stage, TargetLanguage lang, TargetApi api, TranslationFlags extra)
    {
        var acc = new Accessor(code, new byte[] { 8, 0, 0, 0, 16, 0, 0, 0 }, new[] { 8, 4, 1 });
        TranslationFlags flags = extra | (stage == "cs" ? TranslationFlags.Compute : 0);

        var task = Task.Run(() =>
        {
            try
            {
                ShaderProgram p = Translator.CreateContext(0, acc, new TranslationOptions(lang, api, flags)).Translate();
                byte[] body = p.BinaryCode ?? Encoding.UTF8.GetBytes(p.Code);
                var info = new StringBuilder();
                foreach (var b in p.Info.CBuffers) info.Append($"c{b.Slot}:{b.Binding};");
                foreach (var b in p.Info.SBuffers) info.Append($"s{b.Slot}:{b.Binding}:{b.SbCbSlot}:{b.SbCbOffset};");
                foreach (var t in p.Info.Textures) info.Append($"t{t.Binding}:{t.Type}:{t.CbufSlot}:{t.HandleIndex};");
                foreach (var t in p.Info.Images) info.Append($"i{t.Binding}:{t.Type}:{t.CbufSlot}:{t.HandleIndex};");
                return $"ok {Harness.Digest(body)} {Harness.Digest(Encoding.UTF8.GetBytes(info.ToString() + string.Join("|", acc.Notes)))}";
            }
            catch (Exception e)
            {
                return $"exc {e.GetType().Name} {e.Message.Split('\n')[0]}";
            }
        });

        return task.Wait(TimeSpan.FromSeconds(10)) ? task.Result : "timeout";
    }

    static byte[] Generate(Random rng, List<Pattern> patterns, Pattern exit, string stage)
    {
        int n = rng.Next(4, 48);
        var slots = new List<int>();
        for (int a = 0; slots.Count < n + 1; a += 8)
        {
            if ((a & 0x1f) != 0)
            {
                slots.Add(a);
            }
        }

        int codeSize = slots[^1] + 8;
        var words = new ulong[codeSize / 8];

        for (int k = 0; k < n; k++)
        {
            Pattern p = patterns[rng.Next(patterns.Count)];
            ulong w = p.Value | (NextU64(rng) & ~p.Mask);
            w = SetFree(w, p.Mask, 0, 8, SmallReg(rng));
            w = SetFree(w, p.Mask, 8, 8, SmallReg(rng));
            w = SetFree(w, p.Mask, 20, 8, SmallReg(rng));
            w = SetFree(w, p.Mask, 39, 8, SmallReg(rng));
            w = SetFree(w, p.Mask, 16, 4, (ulong)(rng.Next(10) < 8 ? 7 : rng.Next(4)) | (rng.Next(20) == 0 ? 8UL : 0));

            if (Relative.Contains(p.Name))
            {
                int target = slots[rng.Next(slots.Count)];
                long rel = target - (slots[k] + 8);
                w = (w & ~(0xffffffUL << 20)) | (((ulong)rel & 0xffffff) << 20);
                if (p.Name == "Bra")
                {
                    w = (w & ~0x1fUL) | 0xf; // condition code T
                }
            }

            words[slots[k] / 8] = w;
        }

        words[slots[n] / 8] = exit.Value | 0xf | (7UL << 16);

        byte[] codeBytes = new byte[codeSize];
        Buffer.BlockCopy(words, 0, codeBytes, 0, codeSize);

        if (stage == "cs")
        {
            return codeBytes;
        }

        int type = stage switch { "vs" => 1, "tcs" => 2, "tes" => 3, "gs" => 4, _ => 5 };
        var header = new int[20];
        header[0] = 1 | (3 << 5) | (type << 10) | (rng.Next(2) << 14) | (rng.Next(2) << 15);
        header[1] = rng.Next(4) * 0x10;
        header[3] = (rng.Next(3) == 0 ? 1 : rng.Next(8)) << 24;
        header[4] = rng.Next(1, 16);
        for (int i = 5; i < 20; i++)
        {
            header[i] = rng.Next();
        }

        byte[] all = new byte[0x50 + codeSize];
        Buffer.BlockCopy(header, 0, all, 0, 0x50);
        Buffer.BlockCopy(codeBytes, 0, all, 0x50, codeSize);
        return all;
    }

    static ulong SmallReg(Random rng) => (ulong)(rng.Next(12) == 0 ? 255 : rng.Next(16));

    static ulong SetFree(ulong w, ulong mask, int offset, int count, ulong value)
    {
        ulong field = ((1UL << count) - 1) << offset;
        return (mask & field) == 0 ? (w & ~field) | ((value << offset) & field) : w;
    }

    static ulong NextU64(Random rng) => ((ulong)(uint)rng.Next() << 33) ^ ((ulong)(uint)rng.Next() << 11) ^ (uint)rng.Next();
}
