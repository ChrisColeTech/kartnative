# SM86 shaders (Switch 2)

Switch 2 programs (NVN2, `*.Nin_Ounce_NVN2.bfsha`) are SM86 (Ampere, the SM70+ ISA) machine code. The translator reads them with a native SM86 frontend inside the vendored Ryujinx translator (`dm/third_party/ryujinx/src/Ryujinx.Graphics.Shader/Sm86/`), which builds the same decoded program and IR the Maxwell frontend builds; optimisation, SPIR-V and the rest of the chain are shared. There is no SM86 to Maxwell rewrite. Field layouts follow Mesa NAK's encoder (`src/nouveau/compiler/nak/sm70_encode.rs`, MIT) and what the Switch 2 dumps show; no GPL code is used. Containers and their dump: [Switch 2 (NVN2) shader binaries](shaders.md#switch-2-nvn2-shader-binaries). General translation: [shaders.md](shaders.md#translation).

## Selecting the frontend

- `TranslationFlags.Sm86` on the `TranslationOptions` passed to `Translator.CreateContext`. Graphics code is the 128-byte NVN2 program header followed by 16-byte instructions; compute code is the instructions only (no header). The header is SPH version 4; its first 0x50 bytes are the Maxwell SPH layout, which `ShaderHeader` reads as before (all 11,567 MKW headers decode this way).
- In the chain: `dmsh_build.py` (`sm86_code`) gives every NVN2 record a job with `"isa": "sm86"`, which `dmsh_xlate` turns into `TranslationFlags.Sm86`; the code file is the code area up to control +0x38 (the layout above), c1 the record's constant segment (control +0x40, +0x3c bytes). Task and mesh programs get no library yet, nor does a program whose notes match the drop rule below ([Switch 2 translation](shaders.md#switch-2-translation)).
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

- Blocks end at BRA, BRA.U, EXIT, KILL, CALL, RET, BRX and BRXU (`Sm86InstProps.BlockEnd`). A relative target is the next instruction plus 4 times the signed bits 34-81. BRA.U 0x547 branches on a uniform predicate in bits 24-26 (inverted by 27); the decoder treats it as conditional unless that is UPT.
- BRX: the decoder follows the index back through `LDC Rx, c[0x1][Ry + base]`, `SHF.L.U32` by 2 (or `IMAD.SHL` by 4) and `IMNMX.U32` with an immediate bound, and reads the table from c[1] (`IGpuAccessor.ConstantBuffer1Read`); entries are byte offsets from the program start (the BRX base is made 0 by the compiler). This is the pattern of MKW's 313 BRX programs (22 programs carry a c[1] segment).
- BRXU 0x958 takes the index from the UR in bits 24-29; its chain is uniform (MKW's one BRXU program, 112ae71940e57e09): `UISETP.LT.U32 UPp, URy, bound` + `USEL URx, URy, bound, UPp` (count = bound + 1), `USHF.L.U32` by 2 (or `UIMAD` by 4), `ULDC URw, c[0x1][URz + base]` (0xabb). The tracker follows UR writes through rows with `Uniform | Rd` and `URd`.
- CALL.REL (bit 86 clear) is a function call. CALL.REL.NOINC is a call when the instruction before it moves the return address (the next instruction's byte offset) into the link register; the callee returns with RET.REL.NODEC on that register (R55 or R0 in MKW). Calls become Ryujinx functions, so register usage across them is handled by `RegisterUsage`. A NOINC call without that MOV is a jump (`Sm86Emit.CallAsJump`, the loop-exit pattern).
- BSSY, BSYNC, BREAK, WARPSYNC: convergence only, nothing to emit for a per-invocation translation (control flow itself is the BRA / CALL that follows). KILL is a discard and ends its block (the IR's discard terminates it).
- EXIT inside a called function and RET in the main function log "is not implemented" (a Ryujinx function cannot end the invocation); no MKW program has either (0 of 11,542).

## ALU semantics

Fields follow NAK's encoder; results follow NAK's hardware-tested constant folds (`ir.rs`, checked against the GPU by `hw_tests.rs`) and PTX where NAK has none.

- Conversions: destination size bits 75-76 and source size 84-85 (0 = 8, 1 = 16, 2 = 32, 3 = 64 bits; 64-bit operands use 0x110-0x113 and are register pairs), rounding 78-79 (nearest even, down, up, zero). F2I: signed bit 72, .NTZ bit 77 (on every MKW F2I): saturating, NaN to 0. I2F: signed bit 74; an 8- or 16-bit source is the byte or half bits 60-61 select, in units of its size. I2FP (0x045) is I2F on the FP32 pipe; MKW uses I2FP.U32.RZ 8,600 times (a hash to [0, 1)), so directed roundings are exact (the magnitude truncated to 24 bits, plus one unit when bits were dropped). F2FP.PACK_AB puts A in the high half.
- IADD3's carry outputs (81-83, 84-86) are the exact sum's bits above 31 (at least one carry, at least two), wherever they occur; .X inverts (~x) instead of negating. LEA: shift 75-79, .HI 80, .X 74, carry out 81-83, carry in 87-90, bit 73 .SX32 (A sign-extended into the high word; 221 MKW instructions). IMAD.X adds a carry-in; IMAD.HI is PTX mad.hi; IMAD.WIDE writes a pair.
- FLO gives 0xffffffff when no bit is found, also with .SH (PTX bfind). PRMT: mode 72-74; MKW only uses index mode with immediate selectors, which become shifts and masks.
- FSET with no extra bits is FSET.BF (1.0 / 0.0). FSWZADD: lane codes in bits 32-39, lane 0 in 38-39; codes 1 / 2 are A - B / B - A (Ryujinx's Maxwell SwizzleAdd has them the other way round, so the emitter maps them). MUFU SIN / COS take turns (the compiler multiplies by 1 / 2 pi with FMUL.RZ first).
- F16x2: swizzles at 74-75 (A), 60-61 (32-bit slot), 81-82 (C register; its abs / neg move to 83 / 84): 0 both halves, 1 an F32 value, 2 low half twice, 3 high half twice. Two-source F16x2 operations take the second source from the 32-bit slot in every form. .F32 (bit 78) writes lane 0 as an F32 (MKW converts halves with HADD2.F32 -RZ, x.H0_H0). HSET2 / HSETP2 keep their combine (69-70) and .BF / .H_AND (71) in bits 64-71.

## Notes that drop a program

The chain gives a program no library ("SM86 incomplete") when a translator note matches `Unsupported SM86 instruction|is not implemented|Failed to find targets for SM86 BRX|jump table not found|: no function at|Failed to find storage buffer for global memory operation` (`dmsh_build.py` `SM86_BLOCKING`); the harness counts completeness with the same rule. An emitter that cannot translate a variant logs "... is not implemented"; an approximation that still runs (for example a value read at the pixel centre) must not use those words.

## Warp operations, uniform predicates and tensor operations

Rows in `Sm86InstTable.Warp.cs`, emitters in `Sm86Emit.Warp.cs` and `Sm86Emit.Tensor.cs`. A translation runs on a 32-wide SIMD group (the host subgroup; Apple GPUs run 32 fragment or compute invocations per SIMD group), so warp operations map to subgroup operations over the active invocations.

- VOTE 0x806 / VOTEU 0x886 (mode 72-73: ALL, ANY, EQ; Pu 81-83; Pp 87-89, inverted by 90; Rd / URd = the ballot), through `InstEmit.EmitVote` / `EmitBallot`. MKW mostly uses `VOTE.ALL Rd, PT, PT`, the active mask.
- SHFL 0x389 / 0x589 / 0x989 / 0xf89: lane = register 32-39 or immediate 53-57 (opcode bit 11), c (clamp / segment mask, as Maxwell) = register 64-71 or immediate 40-52 (bit 10), mode 58-59, in-range predicate 81-83; Ryujinx's shuffle helpers (`ShufflePass`, `FeatureFlags.Shuffle`).
- MATCH 0x3a1 (bit 73 a register pair, bit 79 ANY; MKW only uses MATCH.ALL.U64 RZ, P0) and REDUX 0x3c4 (op 78-80: AND, OR, XOR, SUM, MIN, MAX; bit 73 signed; result in a UR): a shuffle of an inactive lane is undefined, so REDUX and MATCH.ANY read every lane in turn and count only those active in the ballot (32 shuffles).
- PLOP3 0x81c / UPLOP3 0x89c (NAK OpPLop3: sources 87-89 / 77-79 / 68-70, inverted by 90 / 80 / 71, bit 67 a UP third source; Pu 81-83 = LUT bits 64-66 | 72-76 << 3, Pv 84-86 = LUT bits 16-23). P2R 0x803, R2P 0x804 and UP2UR 0x883 (not in NAK; from the dump): B-slot forms, mask in the B slot, byte selector bits 76-77 (MKW's R2P uses bytes 0, 2 and 3), P0-P6. R2UR 0x3c2 and REDUX carry `URd` (P-guarded warp operations that write a UR).
- HMMA 0x23c, LDSM 0x83b, MOVM 0x23a: MKW's NDSR upscaler (`agl_ndsr.pck`, 30 compute programs; `main` links NintendoWare_Ndsr-1_1_0 and lists "Ndsr" among agl's render passes, so it is on the render path). The host has no matrix unit SPIR-V reaches; each is rebuilt from the PTX fragment layouts (mma.m16n8k16 / m16n8k8, ldmatrix, movmatrix) with shuffles: one HMMA.16816 is 32 shuffles and 64 f32 FMAs, rounded to f16 once (correct but slow; the largest MKW program, 4,911 shuffles in 2.7 MB of MSL, compiles in 1.5 s). HMMA: size bits 75 | 78 << 1, f32 D / C bit 76, f16 sources (82-83 = 0). LDSM: count 1 << bits 72-73, M88 / MT88 (78-79), address Ra + UR (bit 91) + signed 40-63 in shared memory. MOVM.16.MT88. HMMA m16n8k4 (no documented f16 layout), other source types, LDSM M8N16 / M8N32 and MOVM M832 / M864 are not used by MKW and log "is not implemented".

## Memory, textures and surfaces

Implemented in `Sm86Emit.{Mem,Tex,Surface}.cs` (rows in `Sm86InstTable.{Mem,Tex}.cs`); encodings from NAK `sm70_encode.rs` (`OpLd`, `OpSt`, `OpAtom`, `OpLdc`, `OpTex`..`OpTxq`, `OpSuLd`, `OpSuSt`, `OpSuAtom`) and `nak_nir_lower_tex.c` (source packing).

- Global memory: LDG 0x981 / 0x381, STG 0x986 / 0x386, ATOMG 0x9a8 / 0x3a8, RED 0x98e. The address is Ra (a pair when its .E bit is set) + a uniform register (with bit 91: bits 32-37 for LDG, 64-69 for the others; a pair when bit 72 is set) + the signed offset in bits 40-63; without bit 91, bit 72 is Ra's .E bit. Ra's .E bit is 90 (LDG, STG, RED) or 70 (ATOMG). LDG's bits 64-66 are a predicate stored backwards (7 - index, 0 = PT), inverted by 67; false loads 0. The uniform register is the storage buffer's base, loaded with `ULDC.64 URx, c[0][off]`; the emitter adds Ra first and the base second, the order GlobalToStorage follows, so the accesses become storage-buffer accesses (MKW: every one except those through a pointer read from memory, below). Size in bits 73-75 (U8..B128), cache hints (77-80, 84-86) ignored.
- Shared and local memory: LDS 0x984, STS 0x388 / 0x988, LDL 0x983, STL 0x387 / 0x987, ATOMS 0x38c / 0x98c; address Ra (shared: shifted by the stride in bits 78-79, x1 / x4 / x8 / x16) + the uniform register with bit 91 + offset. They reuse the Maxwell `InstEmit.EmitLoad` / `EmitStore` / `EmitAtomicOp`.
- Atomics: type 73-75 and operation 87-90 (87-89 for RED / SURED) number as Maxwell's `AtomSize` / `AtomOp`; the IR has 32-bit integer atomics only (MKW uses U32 ADD / MIN / MAX), other types, INC / DEC and the CAS opcodes (0x3a9, 0x38d) log "not implemented".
- Constants: ULDC 0xabb is `ULDC URd, c[bank][URa + offset]` (the index uniform register in bits 24-29, bit 91 always set; 336 MKW instructions, BRXU tables among them); LDC with a bindless bank (bit 91, NAK's 0x582) does not occur and logs.
- Barriers: MEMBAR 0x992 is a workgroup memory barrier for the CTA / SM scopes (bits 76-77; MKW's 0x50 before shared-memory barriers) and a device one otherwise; BAR 0xb1d (BAR.SYNC, MKW's only form) the workgroup barrier; ERRBAR 0x9ab (after MEMBAR) and CCTL 0x98f emit nothing.
- Attributes: IPA 0x526 is IPA a[URa + addr] (bit 91, an index uniform register; 4 MKW fragment programs, always URa = 0 there); AST to a[0x5c] is dropped with an IR comment (below).
- Textures (handle in a constant buffer: word offset 40-53, bank 54-58, any bank): TEX 0xb60, TLD 0xb66 (texel fetch: explicit LOD zero / lod, AOFFI, multisample 78, 1D buffers as the Maxwell TLD), TLD4 0xb63 (component 87-88, depth compare, AOFFI and per-pixel offsets in 8-bit fields), TMML 0xb69 (8.8 fixed point, GLSL's order reversed, as Maxwell), TXD 0xb6c (B holds ddx / ddy interleaved per coordinate; A coordinates then array index | offsets << 16; not in MKW), TXQ 0xb6f (dimensions, sample count). Sources: A [array index] coordinates, B [lod / bias] [offsets] [depth reference] [sample]; with the scalar bit 60 and at most four values, the first two (one of two) come from Ra on and the rest from Rb on. Results: components 0-1 to Rd, Rd+1, 2-3 to Rd2 (64-71), Rd2+1; FP16 results (bit 79, TEX .F16 in 3 MKW programs) pack components 0-1 into Rd and 2-3 into Rd2.
- Surfaces (handle in a constant buffer as textures): SULD 0xb97, SUST 0xb9b (formatted, channel mask 72-75), SUATOM 0x593, SURED 0x59f, dimension 61-63 (1D, 1D buffer, 1D array, 2D, 2D array, 3D), coordinates from Ra on. The constant-buffer opcodes are one below NAK's register-handle ones (0x998, 0x99c, 0x394, 0x3a0), as TEX 0xb60 is below 0x361.
- Texture and image banks MKW uses: graphics stages c[2] (texture handles 0x20..0xe0, images 0x120 / 0x128; one fragment program c[3][0x10]), compute c[0] (textures 0x10..0x210, images 0x10..0xe8). Storage-buffer bases: c[0][0x110 + 16 i] vertex, 0x510.. fragment, 0x670.. task, 0x770.. mesh, 0x10..0x170 compute.
- Not registered (none in MKW): the bindless texture / surface forms with the handle in a register (0x361, 0x364, 0x367, 0x36a, 0x36d, 0x370, 0x998, 0x99a, 0x99c, 0x99e, 0x394, 0x396, 0x3a0), the binary surface forms SULD.B / SUST.B, TEX with a LOD clamp (modes 4-5: the IR has no minimum-LOD source; logged).
- Task and mesh programs (wave 3; 7 task, 18 mesh in MKW, skipped by the harness): their only opcodes beyond the other stages are ISBERD 0x923 (80) and ISBEWR 0x927 (944), plus POPC 0x2bf. NAK OpIsberd / OpIsbewr: Ra offset + imm 40-55, type 74-75 (U8, U16, B32), access 76-77 (map, patch, primitive, attribute), skew 78, output 79. MKW: task programs write the task payload (ISBEWR B32 attribute output, 59) and read inputs (ISBERD attribute skew, 5); mesh programs read the payload (ISBERD B32 attribute input, 75), write per-vertex / per-primitive outputs (ISBEWR B32 attribute skew output, 686) and the primitive map (ISBEWR U8 map output, 168 index bytes; B32 map output, 31). They need an IR for the task payload and mesh outputs (Metal object / mesh functions: payload in object_data memory, outputs through mesh<> set_vertex / set_primitive / set_index), and the skew addressing (per-vertex / per-primitive stride) decoded from the program header; no OUT (geometry) instruction occurs in MKW.

## File layout and adding an opcode

| File | Holds |
|---|---|
| `Sm86Inst.cs` | the 128-bit instruction and field readers |
| `Sm86InstTable.cs` | the 4096-entry opcode table, its props and form flags, the registration helpers |
| `Sm86InstTable.{Alu,Mem,Tex,Flow,Warp}.cs` | one family's table rows each |
| `Sm86Emit.cs` | shared operand helpers |
| `Sm86Emit.{Alu,Mem,Tex,Flow,Warp}.cs` | one family's emitters each |
| `Sm86Emit.AluConv.cs` | the ALU family's conversions (F2I, I2F / I2FP, F2F, FRND, F2FP) and its helpers (64-bit register pairs, `CheckModifiers`) |
| `Sm86Emit.AluHalf.cs` | the ALU family's F16x2 operations |
| `Sm86Emit.Tensor.cs` | HMMA / LDSM / MOVM (warp family) |
| `Sm86Emit.Surface.cs` | surface (image) emitters of the texture family (rows in `Sm86InstTable.Tex.cs`) |
| `Sm86Decoder.cs`, `Sm86Flow.cs` | blocks, functions, branch / call / BRX / BRXU targets |
| `Sm86Translator.cs` | per-block emission and guards |

To add an opcode:

1. Find its encoding: the opcode and fields in `sm70_encode.rs` (`impl SM70Op for Op...`), and its use in the dump (`$W/shader/sm86_census.tsv`: count, programs, the common modifier values of bits 72-90, an example program).
2. Add a row in the family's `AddXxx()`: `Add("NAME", baseOpcode, Sm86Forms..., Sm86Emit.Name, props)` for ALU-style operations (one row per form), or `Add("NAME", opcode12, Sm86Emit.Name, props)`. Props: `Rd` when it writes the register in 16-23 (the GPR, or the UR on `Uniform` rows; not for predicate-only outputs: the BRX / BRXU trackers follow these), `Uniform` for the uniform datapath (UR destination, UP guard and predicates), `URd` for a P-guarded warp operation that writes a UR (R2UR, REDUX), `NoPred` when the emitter applies the guard itself, the flow props for control flow, `AttrLoad` / `AttrStore` / `Ipa` for attribute accesses. A `FeatureFlags` argument sets decoder features (shared memory, local memory, stores, shuffle). A second row for the same opcode throws at start-up. Uniform variants of ALU operations go beside the vector row with the same emitter.
3. Write the emitter in the family's `Sm86Emit` file: `Inst(context)` gives the instruction; `Src(context, inst, Slot.A|B|C)`, `SecondSlot(inst)`, `FpSrc` / `IntSrc` (modifiers applied), `SrcCbuf`, `Gpr` / `UGpr` / `Pred` / `UPred`, `PredSrc`, `SetDest` (GPR or UR by the row's `Uniform`), `SetPredDest`. Reuse a Maxwell emitter through decoded fields when the semantics are the same (`InstEmit.EmitLdc`, `EmitAld`, `EmitAst`, `EmitIpa`, `EmitLoadSpecialRegister`, `EmitBrx`); unimplemented variants log through `GpuAccessor.Log` and emit nothing.
4. Check with the harness (below) on the hand set and on the dump programs that use the opcode, and run `fuzz-maxwell` if a shared file changed.

ALU + conversion (`*.Alu*.cs`): every ALU and conversion opcode of the MKW census translates (I2FP, F2I, I2F, FRND, F2F, F2FP, FSET, LEA / ULEA, IABS, PRMT / UPRMT, FLO, POPC, BREV, SGXT, BMSK, IMAD.WIDE / .HI / .X, IDP.4A, FSWZADD, MUFU.F16 / TANH, HADD2 / HMUL2 / HFMA2 / HSET2 / HSETP2 / HMNMX2, and the uniform rows UFLO, UBREV, UPOPC, UIMAD.WIDE, USGXT, UBMSK). Emitters log modifier bits they do not model (`CheckModifiers`); no MKW program logs one.

## Tests

The harness is `dm/gfx/tools/sm86` (C#; build outside the repository, e.g. `dotnet build dm/gfx/tools/sm86/sm86_harness.csproj -c Release --artifacts-path $W/sm86/artifacts`):

- `translate <out dir> [--msl] [--metal] [--all] [--jobs N] [--complete <list>] <program.bin | dir>...` translates SM86 programs (stage from `<key>.meta.json`, c[1] from `<key>.c1.bin`) to SPIR-V, MSL through `spirv_cross_bin` (the chain's options) and AIR through `xcrun metal`, with the translator's notes per program. A directory stands for every program in it; task and mesh programs are skipped. The summary counts complete programs (no note that drops a program, above) per stage and lists the opcodes and notes that keep the others out; MSL / AIR are made for complete programs, or for all with `--all`. The whole MKW dump: `translate <out> --jobs 6 --metal $W/shader/sm86` (about 16 minutes).
- `hand <out dir>` writes hand-encoded programs (`Sm86Programs.cs`) covering uniform chains, predicates, BSSY / BSYNC, a BRX table and a CALL.REL.NOINC / RET pair.
- `warp <out dir> <alu_run>` runs the warp and tensor tests (`WarpTests.cs`) on the Mac GPU: hand-encoded fragment programs render 8x4 pixels (one SIMD group; the test checks every lane id appears once) through `alu_run`, and their R0-R2 are compared with a C# model of the warp, with and without DebugMode.
- `alu <out dir> <alu_run> [--math safe|relaxed|fast] [--filter s] [--jobs N]` runs 243 hand-encoded fragment programs over the ALU family (`AluTests.cs`) on the Mac's GPU: inputs come from c[0] so nothing folds, `alu_run.swift` (build it with `xcrun swiftc -O dm/gfx/tools/sm86/alu_run.swift -o $W/sm86/alu_run`) renders the program's MSL offscreen into an RGBA32Uint target and the four words are compared with reference values. All pass in the three Metal math modes (the chain compiles with fast on the Mac and relaxed on iOS).
- `fuzz-maxwell <InstTable.cs> <count> <seed> <out>` digests random Maxwell programs (SPIR-V without DebugMode, GLSL with it); build the harness once against the current translator and once with `-p:Baseline=true -p:RyuSrc=<older copy>`, and compare the outputs: a change to shared translator files must leave them identical, or explain each difference.

Status (2026-10-31, MKW 1.0.0): every opcode of the 11,542 non-task / mesh programs translates, and all of them compile through MSL to AIR with `xcrun metal` (3,550 vertex, 7,264 fragment, 728 compute); under the drop rule above 11,540 are complete (3,449 before wave 2): the two programs with a global-memory access through a pointer (below) are dropped. `warp`: 20 of 20 pass. Maxwell: 5 x 10,000 random programs are identical before and after the wave-2 frontend work, except the AST-optimizer fix (below), which only changes SPIR-V without DebugMode.

## Translator limits met

- Ryujinx's decoder and IR assumed 64-bit instructions and Maxwell fields in places shared with every frontend: `InstOp` now also keeps bits 64-127; the Maxwell guard extraction in `Translator.EmitOps`, `FunctionMatch` (Maxwell code patterns) and `Block.HasNext` (Maxwell branch fields) are bypassed for SM86 (`Sm86Translator.EmitOps`, `Sm86Decoder.HasNext`).
- The IR has no 64-bit integers: SHF's funnel shift is built from 32-bit shifts and selects, IADD3's carries from unsigned compares.
- Constant-buffer slots were sized for Maxwell's 18; SM86 names 32 banks.
- A Ryujinx function cannot end the program: EXIT inside a called function is not translated (see Control flow).
- IPA's interpolation location: centroid reads (bits 76-77 = 1; 80,197 of MKW's 109,578 IPA 0x326, in 3,355 programs) declare the input centroid (`ShaderDefinitions.CentroidInputs`, SPIR-V `Decoration.Centroid`, MSL `[[centroid_perspective]]`); no MKW program reads one attribute both with and without centroid, so the per-attribute declaration is exact. Offset interpolation (location 2, not in MKW) is logged and read at the pixel centre.
- `a[0x5c]` is the variable pixel rate shading table index (NAK_ATTR_VPRS_TABLE_INDEX), a per-primitive output; 2,838 MKW vertex programs write 1 there. Metal has no per-primitive shading rate (its rasterization rate maps are per screen region), so the store is dropped with an IR comment and those primitives shade at full rate (a platform limit).
- Global memory through a 64-bit pointer read from memory (0459a55e79335cf6 vertex: writes a record through a pointer loaded from its storage buffer; 791fb7522aa03e61 compute: one load) cannot be bound: GlobalToStorage maps global memory only to storage buffers whose base comes from a constant buffer; the note "Failed to find storage buffer for global memory operation" drops both programs.
- The IR has 32-bit integer atomics only: F32 / F16x2 / 64-bit atomics and INC / DEC are rejected (not in MKW).
- Without DebugMode the AST optimizer propagated constants into SPIR-V call arguments (passed by pointer: `KeyNotFoundException` in `GetLocalPointer`, every SHFL with an immediate lane or c) and moved a call past reads of its out arguments; `AstOptimizer` now keeps both. The chain translates with DebugMode by default.
- The IR's shuffles carry 32-bit values as f32 (`GroupNonUniformShuffle` on FP32), as for Maxwell; MATCH compares the shuffled bits.
- FADD / FMUL / FFMA rounding modes and FTZ are not modelled (the IR has none); MKW has FMUL.RZ 96,356 times (mostly before MUFU), FADD.RM 1,022 and FFMA.RZ 479: at most one unit in the last place.
- Metal flushes FP32 denormals, so a directed rounding of a denormal (F2I.CEIL of 1e-40: 1 on the GPU) gives 0.
- 64-bit float conversions and MUFU.RCP64H / RSQ64H become FP64 IR, which Metal cannot compile; MKW has none.
- F2I without .NTZ takes its NaN result from a dispatch setting on the console (0 or 0x80000000); 0 is used. MKW has none.
