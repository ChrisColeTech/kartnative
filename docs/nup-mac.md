# Building and running a NUP on the Mac

This guide explains where a direct-NUP build puts its files, what happens when the NUP moves to an external drive, when shaders rebuild, and how the generated launcher and application shortcut work. Direct NUP input is for `--platform mac`; it does not make an iPhone build or a self-contained Mac app.

## Recommended command

Give the wrapper the exact NUP path. Keep `--work-dir` at a stable location if the NUP is on a removable drive:

```sh
scripts/build_mac_from_nup.sh \
  "/Volumes/Games/Mario Kart World [0400C3F00006E000][v0].nup" \
  --work-dir "/Users/chris/games/0400C3F00006E000_build"
```

The repository accepts only the selected file when a folder contains multiple NUPs. Add `--allow-other-metal` only when the build reports that this Mac's Metal compiler differs from the tested compiler and you accept the risk of incorrect shader output:

```sh
scripts/build_mac_from_nup.sh \
  "/Volumes/Games/Mario Kart World [0400C3F00006E000][v0].nup" \
  --work-dir "/Users/chris/games/0400C3F00006E000_build" \
  --allow-other-metal
```

After a successful build, start the game with:

```sh
/Users/chris/games/0400C3F00006E000_build/run_game.sh
```

The wrapper also asks the main builder to create `/Applications/Mario Kart World.app` when that path does not exist. It never replaces an existing app or a Shortcuts export with the same name.

## What stays in the NUP

The build does not modify, move, expand or copy the NUP's RomFS. The NUP remains the only copy of the game's RomFS.

The build reads:

- the package title ID and version;
- the ExeFS modules needed by the rewriter;
- RomFS file metadata for the virtual directory tree;
- raw RomFS file ranges directly from the package;
- zstd-compressed RomFS entries by decompressing them on demand.

At run time, the host continues reading RomFS from the original NUP. The external drive must therefore remain connected and mounted at the same path while the game runs.

## What the build writes

The generated files live under the selected work directory:

```text
0400C3F00006E000_build/
|-- extract/v0/
|   |-- ExeFS/             ordinary copies of the small code modules
|   `-- RomFS.nup          symbolic link to the original NUP
|-- patched_v0/            rewritten code modules
|-- shader/
|   |-- pkg/               shaders split from the game
|   `-- cache/             compiled Mac shader libraries
|-- tools/dmhost           Mac host executable
|-- font/                  generated Nintendo button-icon font
|-- run/
|   |-- save/              persistent game save
|   |-- temp/              temporary game files
|   `-- pso_archive/       compiled Metal pipeline bases
|-- logs/                  build and launch logs
`-- run_game.sh            executable launcher generated after each build
```

For the Mario Kart World 1.0.0 build measured on 2026-11-12, the original NUP was 21 GB and the generated build was 4.7 GB:

| Generated content | Approximate size |
|---|---:|
| Shader directory | 3.7 GB |
| Run data, primarily the pipeline archive | 900 MB |
| Materialized ExeFS | 39 MB |
| Rewritten modules | 81 MB |
| Entire build directory | 4.7 GB |

These sizes can change with the shader chain, pipeline cache, saves and game version. None of the 4.7 GB is another RomFS copy.

## Default versus stable work directory

The work-directory choice determines whether moving a NUP starts a new build.

### With a stable work directory

This is the recommended removable-drive layout:

```text
/Volumes/Games/
`-- Mario Kart World [...].nup                21 GB, read in place

/Users/chris/games/
`-- 0400C3F00006E000_build/                   generated files and caches
```

Always pass the same `--work-dir`. After the NUP moves, the next build notices that its source path changed, materializes ExeFS again, and updates `extract/v0/RomFS.nup`. It then checks each later stage's input key. Rewriting, shader splitting, shader compilation and host building are reused whenever their actual inputs are unchanged.

The script does not move the old build directory or copy its files elsewhere. It continues using that directory in place.

### Without `--work-dir`

The default build directory is beside the NUP:

```text
/Volumes/Games/
|-- Mario Kart World [...].nup
`-- 0400C3F00006E000_build/
```

If the NUP was previously built at another location, this new directory has none of that build's keys or shader cache. The first run there performs the complete rewrite, shader scan, shader compilation and host build. It does not automatically discover or move the old build.

Use this layout when all generated data should live on an APFS external SSD and a fresh build is acceptable.

## First and incremental builds

A first build performs these stages:

1. Validate the NUP and select the game profile from its title ID and NSO build IDs.
2. Materialize ExeFS and link the NUP as the virtual RomFS.
3. Rewrite the game's modules for native execution on macOS.
4. Scan the NUP's RomFS and split its shaders.
5. Translate and compile the Mac shader libraries.
6. Build the Mac host and prebuild known Metal pipeline bases.
7. Write `run_game.sh` and, when absent, the application shortcut.

Each expensive stage records an input key. A later invocation reports `reused (inputs unchanged)` for matching stages. It does not perform a full rebuild merely because the wrapper was run again.

Common changes have these effects:

| Change | Expected result on the next build |
|---|---|
| NUP moved, same NUP and same `--work-dir` | ExeFS link stage updates; unchanged later outputs are normally reused |
| NUP moved, `--work-dir` omitted | A new build directory is created beside it; full first build there |
| Different NUP contents or game version | Affected extraction, rewrite and shader stages rebuild |
| Rewriter or host source changed | Relevant modules or host rebuild; unchanged shaders can remain cached |
| Shader-chain or profile shader inputs changed | Shader translation and compilation rebuild as required |
| `run_game.sh` deleted | It is regenerated after the next successful Mac build |
| Entire work directory deleted | Full rebuild, with no shader or pipeline cache to reuse |

The stage summaries printed by the script are authoritative: they state whether each output was reused or rebuilt.

## Moving the NUP after a build

1. Quit the game before moving the NUP. The host reads that file directly while running.
2. Move the NUP to the external drive.
3. Run the wrapper with the new exact path and the old stable `--work-dir`.
4. Confirm the build reports the new NUP path and reuse for the unchanged expensive stages.
5. Start `run_game.sh` or the application shortcut.

Do not move the NUP and then run the old launcher without rebuilding. `extract/v0/RomFS.nup` would still point to the old path.

## Moving the generated build

The generated build can also be moved to an APFS external drive. Pass its new location through `--work-dir` and run the wrapper again so absolute paths and the NUP link are regenerated:

```sh
scripts/build_mac_from_nup.sh \
  "/Volumes/Games/Mario Kart World [0400C3F00006E000][v0].nup" \
  --work-dir "/Volumes/Games/0400C3F00006E000_build"
```

Some keys include paths or file identities, so the script may rebuild individual stages after the directory moves. It still checks every stage and reuses whatever remains valid.

`run_game.sh` contains absolute paths. `/Applications/Mario Kart World.app` is created only when absent, so an existing shortcut is not retargeted automatically when the work directory changes. Delete a generated shortcut and rerun the wrapper, or edit an existing Shortcuts action, if the work directory itself moves.

## External-drive formats

| Format or device | Support |
|---|---|
| APFS SSD | Recommended for the NUP and suitable for the build directory |
| exFAT drive | Suitable for the NUP when `--work-dir` stays on APFS; not recommended for the build directory because it needs symbolic links |
| FAT32 drive | Unsuitable: one file cannot exceed 4 GB |
| Slow USB flash drive | May work, but every uncached game resource is read from it and loading can stall |
| External SSD | Preferred for reliable sustained reads |

The volume name is part of the absolute path under `/Volumes`. Renaming the volume changes the NUP path; rerun the wrapper afterward.

macOS privacy controls can restrict removable-volume access according to the application that launched the host. If a launch cannot open the NUP and reports `EPERM` or permission denied, allow removable-volume access for that application in System Settings > Privacy & Security > Files & Folders, or launch `run_game.sh` from a Terminal that has that access. The generated shortcut is only a launcher; unlike `--platform mac-app`, it does not embed the game data or host inside the app bundle.

## Launcher, shortcut, saves and logs

`run_game.sh` is regenerated from the successful build's paths and environment. It opens a window with audio and uses:

- `run/save/` for the persistent save;
- `run/temp/` for temporary files;
- `shader/cache/` for compiled shader libraries;
- `extract/v0/RomFS.nup` for the original package;
- `logs/mac_launch.log` when started through the generated application shortcut.

The NUP wrapper enables `--mac-shortcut`. The main builder can do the same directly:

```sh
python3 scripts/build_ipa_from_nsp.py \
  "/Volumes/Games/Mario Kart World [0400C3F00006E000][v0].nup" \
  --platform mac \
  --mac-shortcut \
  --work-dir "/Users/chris/games/0400C3F00006E000_build"
```

Shortcut creation uses `osacompile` and requires permission to write to `/Applications`. If an app with that game name already exists, the build reports `already exists, left unchanged`.

## Choosing a layout

Use an external NUP with an internal stable work directory when the goal is to free the NUP's 21 GB from the internal disk without recompiling shaders. This keeps the current 4.7 GB build and its caches internally for faster access.

Use an APFS external work directory when the internal disk must also be freed. Expect the initial build there to create several gigabytes of shaders and pipeline data, and keep the drive connected whenever the game runs.

For implementation details, see [the packing script](development.md#the-packing-script-in-detail) and [direct NUP access without a second RomFS copy](reference/packing.md#a-nup-package-is-used-without-a-second-full-romfs-copy).
