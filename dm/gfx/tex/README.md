# dm/gfx/tex — texture format mapping, block-linear deswizzling, BC decompression

The part of the graphics layer that deals with texture data, called by the replay layer `../nvn/nvn_mtl.m`. It does three things:

1. converts NVN texture format numbers to Metal pixel formats;
2. unpacks the Switch's "block-linear" layout into an ordinary row-by-row layout;
3. decompresses BC textures on GPUs that do not support BC compressed textures.

Block linear is how the Tegra X1 GPU stores textures: the data is not one row after another, but interleaved in small blocks of 64 bytes × 8 rows (GOBs), and several GOBs are then stacked into larger blocks according to a "block height" and a "block depth". The game's packaged texture files are in exactly this layout, and it must be undone before uploading to Metal. The Switch 2 GPU uses the same blocks with bits 5 and 6 of the offset inside a GOB exchanged; a Switch 2 build sets `nvn_gob_s2` and everything here follows it ([Switch 2 block-linear GOBs](../../../docs/reference/nvn-objects.md#switch-2-block-linear-gobs)).

## Files

| File | Contents |
|---|---|
| `nvn_fmt_mtl.h`, `nvn_fmt_mtl.c` | `nvn_fmt_to_mtl()`: NVN format (0x01–0x9C, and NVN2's 0x9D–0xA2) → Metal pixel format + component remapping. Returns 1 for an exact match, 2 for a substitute (precision, bit order or interpretation differs and a conversion is needed), 0 when Metal has no corresponding format. How each format is handled is written in the comments inside the table. Pure C, no dependency on Metal headers |
| `nvn_deswizzle.h`, `nvn_deswizzle.c` | `nvn_deswizzle()`: deswizzles one level and one layer from block linear to linear; `nvn_swizzle()`: the reverse, used for round trips in unit tests; `nvn_bl_offset()`: the block-linear address of a single point. Pure C |
| `nvn_deswizzle_gpu.h`, `nvn_deswizzle_gpu.m` | A Metal compute shader version of the same deswizzling algorithm, used by the replay layer when uploading packaged textures; when initialization fails or an unsupported format is encountered, the replay layer falls back to the CPU version |
| `nvn_sparse_tex.h`, `nvn_sparse_tex.m` | Sparse textures: a texture of a sparse pool backed by a Metal 4 placement-sparse texture whose 64 KB tiles are mapped only where the guest pool has memory and uploaded tile by tile ([Sparse textures backed by Metal placement-sparse textures](../../../docs/reference/nvn-objects.md#sparse-textures-backed-by-metal-placement-sparse-textures)); test `tests/t_sparse_tex.m` |
| `tools/uploaddump_png.m` | Turns `DM_NVN_UPLOADDUMP` files (each level 0, layer 0 the CPU deswizzle decodes, with a one-line header) into PNGs, decoded by Metal in the format the host uses; build line at its top |
| `tests/` | Unit tests |

The offsets of each level and layer within the storage, and the block height and block depth actually used by each level, are all taken from `../nvn/nvn_tex.c` (`nvn_tex_layout`, `nvn_tex_level_offset`, `nvn_tex_level_tiles`); this directory only does addressing and moving within a level. Packaged parameters override the automatic block dimensions: [Packaged texture block dimensions and linear storage rows](../../../docs/reference/nvn-objects.md#packaged-texture-block-dimensions-and-linear-storage-rows).

## BC textures

Only GPUs with BC compressed textures are supported (`supportsBCTextureCompression` of `MTLDevice`: Apple silicon Macs and the latest iPhones), so BC textures are created and uploaded as BC; there is no decompression path. A device without them is refused at start ([GPUs without BC textures](../../../docs/reference/rendering.md#gpus-without-bc-textures-are-refused-at-start)). A BC texture is never a render target and its raw blocks are not sampled through an uncompressed view (Metal has no such views; Breath of the Wild encodes one 32×32 BC4 texture on the GPU, GitHub issue #4).

## Tests

```sh
make -C dm/gfx/tex/tests run       # run t_fmt_mtl and t_tex
```

- **`t_fmt_mtl`**: takes the mapping for every NVN format in 0x01–0xA2 and, for every one whose mapping is not 0, actually creates a texture on the local Metal device; all of them must be created successfully. It also checks that every NVN format with sRGB in its name lands on a Metal sRGB format. No game files needed.
- **`t_tex`**: the deswizzling tests, in several groups:

  | Group | Criterion |
  |---|---|
  | A round trip | Writes random data into each level and layer and reads it back out; must be byte-for-byte equal. Covers 2D, 2D array and 3D, several bytes-per-block values and non-power-of-two sizes |
  | B address formula | The result of `nvn_bl_offset()` equals the address computed by hand from the public layout rules |
  | D differential copies | Both copy directions match a byte-address oracle for 1,029 deterministic layouts, including padded pitches, byte tails, sparse mip tails, packaged block dimensions, arrays and cubes; untouched storage and row padding are compared too |
  | E hand-made 3D | Places markers at hand-computed addresses, checks where they land after deswizzling, and checks block height, block depth and storage size |
  | M Mario Kart World layouts | Level offsets and storage of three of MKW's packaged textures equal its BNTX files' mip offsets and image size |
  | G Switch 2 GOB | With `nvn_gob_s2`: hand-computed in-GOB offsets, the whole GOB as Switch 1's offsets with bits 5 and 6 exchanged, group D's 1,029 layouts and round trips |

  The A round trip can only prove "one-to-one and within bounds"; it cannot prove that the layout formula is right. What backs it up is the hand-computed addresses of groups B and E, and the games that render right.

## Limitations

- `nvn_deswizzle()` does not support multisampled textures, textures with the LINEAR flag (which are linear to begin with) or the BUFFER target; it returns -1 when it encounters them.
- Cubes and cube arrays are handled as layered textures.
- The component bit order of the 16-bit packed formats (565 / 5551 / 4444) is mapped according to the format names and has not been checked with real data; they are marked as "substitute" in the format table.
