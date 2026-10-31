# SM86 shaders (Switch 2)

Switch 2 programs (NVN2, `*.Nin_Ounce_NVN2.bfsha`) are SM86 (Ampere, the SM70+ ISA) machine code. The translator reads them with a native SM86 frontend inside the vendored Ryujinx translator (`dm/third_party/ryujinx/src/Ryujinx.Graphics.Shader/Sm86/`), which builds the same decoded program and IR the Maxwell frontend builds; optimisation, SPIR-V and the rest of the chain are shared. There is no SM86 to Maxwell rewrite. Field layouts follow Mesa NAK's encoder (`src/nouveau/compiler/nak/sm70_encode.rs`, MIT) and what the Switch 2 dumps show; no GPL code is used. Containers and their dump: [Switch 2 (NVN2) shader binaries](shaders.md#switch-2-nvn2-shader-binaries). General translation: [shaders.md](shaders.md#translation).

## Selecting the frontend

- `TranslationFlags.Sm86` on the `TranslationOptions` passed to `Translator.CreateContext`. Graphics code is the 128-byte NVN2 program header followed by 16-byte instructions; compute code is the instructions only (no header). The header is SPH version 4; its first 0x50 bytes are the Maxwell SPH layout, which `ShaderHeader` reads as before (all 11,567 MKW headers decode this way).
- Not wired into the chain yet. `dmsh_xlate/Program.cs` needs a job field (`"isa": "sm86"`) that adds the flag; `dmsh_build.py` needs to write it for profiles with `switch2 on`, give the code file in the layout above (the ShaderExtract dump `$W/shader/sm86/<key>.bin` is that layout), pass `<key>.c1.bin` as the job's `c1` (BRX tables), and take texture handles from the bank each instruction names (MKW: bank 2, sometimes bank 0; `res.json` reports `cbuf_slot`). Both are chain files: changing them takes a `DMSH_CHAIN` bump.
- Constant bank 24: MKW's fullscreen vertex and fragment programs read it; on the console it is a block the system provides to NVN2 blits (render-area size and reciprocals, a UV clamp rectangle). The translator now has 32 constant-buffer slots, so it is an ordinary slot 24 binding that the host must fill.

## Instruction format

| Bits | Field |
|---|---|
| 0-11 | opcode; bits 9-11 are the ALU source form |
| 12-14, 15 | guard predicate (7 = PT), negated by 15; `@!PT` never runs and emits nothing. Uniform-datapath operations are guarded by UP0-UP6 |
| 16-23 | destination (Rd, or URd for uniform operations) |
| 24-31 | A register |
| 32-63 | B slot (register 32-39, imm32, constant buffer, uniform register 32-37) or C slot in forms 2, 3, 7 |
| 64-71 | C register, or B register in forms 2, 3, 7 |
| 72-90 | operation modifiers; 81-83 / 84-86 predicate outputs; 87-89 (90 negates) a predicate input |
| 91 | constant-buffer bank from a uniform register (forms 3, 5); set on every uniform-datapath ALU operation |
| 105-127 | scheduling control (stall, yield, barriers, reuse); not needed by a translation |

ALU forms (`Sm86Forms`): 1 R (B and C registers), 2 imm32 as C, 3 constant buffer as C, 4 imm32 as B, 5 constant buffer as B, 6 uniform register as B, 7 uniform register as C. Modifier bits belong to the slot: A abs 73 / neg 72, the 32-bit slot abs 62 / neg 63 (none for an immediate), the 64-71 register abs 74 / neg 75. Two-source operations (FADD, FMUL, ISETP, FSETP, SEL, FSEL, IMNMX, FMNMX) take their second source from B in forms 1, 4, 5, 6 and from C in forms 2, 3, 7. Constant-buffer operand: byte offset 38-53, bank 54-58.

Uniform registers UR0-UR62 (UR63 = URZ) and predicates UP0-UP6 (UP7 = UPT) are their own IR register types (`RegisterType.UniformGpr`, `UniformPredicate`): SSA and function arguments treat them like GPRs and predicates; URZ reads 0 and UPT true. No GPR range is reserved for them.

## Control flow

- Blocks end at BRA, EXIT, KILL, CALL, RET and BRX (`Sm86InstProps.BlockEnd`). A relative target is the next instruction plus 4 times the signed bits 34-81.
- BRX: the decoder follows the index back through `LDC Rx, c[0x1][Ry + base]`, `SHF.L.U32` by 2 (or `IMAD.SHL` by 4) and `IMNMX.U32` with an immediate bound, and reads the table from c[1] (`IGpuAccessor.ConstantBuffer1Read`); entries are byte offsets from the program start (the BRX base is made 0 by the compiler). This is the pattern of MKW's 313 BRX programs (22 programs carry a c[1] segment).
- CALL.REL (bit 86 clear) is a function call. CALL.REL.NOINC is a call when the instruction before it moves the return address (the next instruction's byte offset) into the link register; the callee returns with RET.REL.NODEC on that register (R55 or R0 in MKW). Calls become Ryujinx functions, so register usage across them is handled by `RegisterUsage`. A NOINC call without that MOV is a jump (`Sm86Emit.CallAsJump`, the loop-exit pattern).
- BSSY, BSYNC, BREAK, WARPSYNC: convergence only, nothing to emit for a per-invocation translation. KILL is a discard and ends its block (the IR's discard terminates it).

## File layout and adding an opcode

| File | Holds |
|---|---|
| `Sm86Inst.cs` | the 128-bit instruction and field readers |
| `Sm86InstTable.cs` | the 4096-entry opcode table, its props and form flags, the registration helpers |
| `Sm86InstTable.{Alu,Mem,Tex,Flow,Warp}.cs` | one family's table rows each |
| `Sm86Emit.cs` | shared operand helpers |
| `Sm86Emit.{Alu,Mem,Tex,Flow,Warp}.cs` | one family's emitters each |
| `Sm86Decoder.cs`, `Sm86Flow.cs` | blocks, functions, branch / call / BRX targets |
| `Sm86Translator.cs` | per-block emission and guards |

To add an opcode:

1. Find its encoding: the opcode and fields in `sm70_encode.rs` (`impl SM70Op for Op...`), and its use in the dump (`$W/shader/sm86_census.tsv`: count, programs, the common modifier values of bits 72-90, an example program).
2. Add a row in the family's `AddXxx()`: `Add("NAME", baseOpcode, Sm86Forms..., Sm86Emit.Name, props)` for ALU-style operations (one row per form), or `Add("NAME", opcode12, Sm86Emit.Name, props)`. Props: `Rd` when it writes the GPR in 16-23 (the BRX tracker follows these), `Uniform` for the uniform datapath (UR destination, UP guard and predicates), `NoPred` when the emitter applies the guard itself, the flow props for control flow, `AttrLoad` / `AttrStore` / `Ipa` for attribute accesses. A `FeatureFlags` argument sets decoder features (shared memory, local memory, stores, shuffle). A second row for the same opcode throws at start-up.
3. Write the emitter in the family's `Sm86Emit` file: `Inst(context)` gives the instruction; `Src(context, inst, Slot.A|B|C)`, `SecondSlot(inst)`, `FpSrc` / `IntSrc` (modifiers applied), `SrcCbuf`, `Gpr` / `UGpr` / `Pred` / `UPred`, `PredSrc`, `SetDest` (GPR or UR by the row's `Uniform`), `SetPredDest`. Reuse a Maxwell emitter through decoded fields when the semantics are the same (`InstEmit.EmitLdc`, `EmitAld`, `EmitAst`, `EmitIpa`, `EmitLoadSpecialRegister`, `EmitBrx`); unimplemented variants log through `GpuAccessor.Log` and emit nothing.
4. Check with the harness (below) on the hand set and on the dump programs that use the opcode, and run `fuzz-maxwell` if a shared file changed.

Wave 2 families:

- **ALU + conversion** (`*.Alu.cs`): I2FP 0x245, F2I 0x105, I2F 0x106, FRND 0x107, FSET 0x00a, LEA 0x011 (and .HI / .X), IABS 0x013, PRMT 0x016, F2F / F2FP, POPC, FLO, SGXT, BREV, IMAD.WIDE / .HI / .X, the half-float HADD2 / HFMA2 / HMUL2 / HSETP2 / HMNMX2, MUFU.F16, uniform variants not yet listed.
- **Memory + texture + surface** (`*.Mem.cs`, `*.Tex.cs`): LDG 0x981 / STG 0x986, LDS / STS 0x984 / 0x388, LDL / STL 0x983 / 0x387, ATOMG / RED 0x9a8 / 0x98e, ATOMS, MEMBAR 0x992, BAR 0xb1d, ULDC from a bindless constant buffer 0xabb (bank from URa, bit 91), LDC with a uniform bank, TLD 0xb66, TLD4 0xb63, TMML 0xb69, TXD 0xb6c, TXQ 0xb6f, SULD / SUST 0xb97 / 0xb9b, TEX LOD clamp and FP16 results, bindless TEX 0x361, ISBERD / ISBEWR for task and mesh programs.
- **Flow + warp + uniform** (`*.Flow.cs`, `*.Warp.cs`): BRA.U 0x547, BRXU 0x958, VOTE / VOTEU, SHFL 0xf89 / 0x989 / 0x589, MATCH, REDUX, PLOP3 / UPLOP3 0x81c, P2R / R2P 0x803 / 0x804, UP2UR, BMOV, EXIT inside a called function, HMMA / LDSM / MOVM (30 MKW compute programs).

## Tests

The harness is `dm/gfx/tools/sm86` (C#; build outside the repository, e.g. `dotnet build dm/gfx/tools/sm86/sm86_harness.csproj -c Release --artifacts-path $W/sm86/artifacts`):

- `translate <out dir> [--msl] [--metal] <program.bin>...` translates SM86 programs (stage from `<key>.meta.json`, c[1] from `<key>.c1.bin`) to SPIR-V, MSL through `spirv_cross_bin` (the chain's options) and AIR through `xcrun metal`, with the translator's notes per program.
- `hand <out dir>` writes hand-encoded programs (`Sm86Programs.cs`) covering uniform chains, predicates, BSSY / BSYNC, a BRX table and a CALL.REL.NOINC / RET pair.
- `fuzz-maxwell <InstTable.cs> <count> <seed> <out>` digests random Maxwell programs; build the harness once against the current translator and once with `-p:Baseline=true -p:RyuSrc=<older copy>`, and compare the outputs: a change to shared translator files must leave them identical.

Status (2026-10-31, MKW 1.0.0): the implemented opcodes cover 3,449 of the 11,567 dumped programs completely (914 vertex, 2,532 fragment, 3 compute); all 3,449 translate to SPIR-V and MSL and compile with `xcrun metal`. Maxwell: 5 x 10,000 random programs (SPIR-V and GLSL) give identical output before and after the frontend for every program that translated before; the only changes are programs that read a constant bank of 18 or more, which used to stop with `IndexOutOfRangeException` in `ResourceManager` and now translate (no Switch 1 game binds such a bank).

## Translator limits met

- Ryujinx's decoder and IR assumed 64-bit instructions and Maxwell fields in places shared with every frontend: `InstOp` now also keeps bits 64-127; the Maxwell guard extraction in `Translator.EmitOps`, `FunctionMatch` (Maxwell code patterns) and `Block.HasNext` (Maxwell branch fields) are bypassed for SM86 (`Sm86Translator.EmitOps`, `Sm86Decoder.HasNext`).
- The IR has no 64-bit integers: SHF's funnel shift is built from 32-bit shifts and selects, IADD3's carries from unsigned compares.
- Constant-buffer slots were sized for Maxwell's 18; SM86 names 32 banks.
- A Ryujinx function cannot end the program: EXIT inside a called function is logged and ignored, as in the Maxwell frontend.
- IPA's interpolation location (centroid, used by most MKW IPAs, or offset) is not modelled, as in the Maxwell frontend.
- An AST to `a[0x5c]` (a per-primitive hint in MKW vertex programs) is not a known attribute; the shared emitter logs `Attribute offset 0x5C is not valid` and drops it.
