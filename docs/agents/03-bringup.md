# 03 — Host bring-up

## The loop

```bash
python3 dm/tools/patch_nso.py $W/data/v0/ExeFS/main $W/build/patched_v0 main   # and sdk
make -C dm/host GAME=<title ID> dmhost
$W/run/boot.sh <log name> [seconds] [VAR=value ...]
```

`boot.sh` runs `dmhost <patched dir>` with `DM_ROMFS`, `DM_SAVE`, `DM_TEMP`, `DM_MSL_DIR` set, under a perl `alarm` (a run that hits the time limit ends with `CRASH sig=14` — that is the alarm, not a crash). Logs go to `run/<name>.log`. Logs may contain non-UTF-8 bytes: use `grep -a` / `LC_ALL=C` when filtering.

## Reading the log

| Marker | Meaning |
|---|---|
| `>>> nnosInitialize`, `>>> nnMain` | Boot stages reached |
| `[RED] …` | Something unimplemented or wrong, printed once per kind. Collect them with `grep -aF "[RED]" log \| sort \| uniq -c` after every run |
| `UNIMPL <name>` | A forced NVN / SDK stub was called |
| `[nvn] frame N: … \| name=count …` | Per-frame NVN call counts (calls made on the frame thread; command buffers recorded elsewhere replay later) |
| `[longframe]` | A long frame, with submit / GPU / pipeline / upload breakdown |
| `memory up to frame N: resident … GPU allocated …` | Memory report (RSS, Metal allocated size). Printed when memory moves; steady growth here is a leak. A watchdog exits at 24 GB. Also: memory pools and how many are resident (pools join the residency set on first GPU use), finalized pool buffers still alive, the storage-sharing texture table, and on iOS `headroom to the system memory limit` (`os_proc_available_memory`, how far the app is from being killed for memory) |
| `CRASH sig=… pc=… lr=…` + registers + `bt:` + `stack scan` + thread dump | Crash report: see [05](05-debugging.md) |
| `thread 0x10xx 'name' … waits:<state> addr=…` | Thread dump: what each guest thread waits on (`address` = address arbiter, `mutex` = mutex with holder, `condvar`, `sync object`, `running/not waiting` = running); a thread waiting in an SVC also gets a `guest stack:` line, the words on its guest stack that point into a module (its callers when the code keeps no frame pointers) and x19–x28 |

Useful switches: `DM_FS=1` (file opens, and not-found probes), `DM_SVC=1` (memory SVCs and other syscalls), `DM_THR=1` (thread SVCs), `DM_WATCH=<s>` (dump all threads after s seconds), `DM_WATCH_STALL=<s>` (dump when the frame loop stops progressing).

## Switch 2 programs

A profile with `switch2 on` (written from `main.npdm`) builds a host that answers as the Switch 2 kernel and SDK where they differ: the 31.25 MHz system counter, cores 0-5, `GetInfo` 29-33 and svc 0x80; its SDK (19.3.3 on [Mario Kart World](../../dm/games/0400C3F00006E000/README.md)) also has calls of its own (`oe` graphics scale, audio device modes), found as the Switch 1 ones are. Set the profile's memory from the Switch 2's 12 GB layout (`app_pool` 9809 MiB) and the heap the game's `nninitStartup` asks for ([The Switch 2's 12 GB layout](../reference/memory.md#the-switch-2s-12-gb-layout)).

The graphics API is NVN2: the game checks the device's API version (231.16 on Mario Kart World) and quietly skips its graphics setup when it does not match, so the first symptom is a later null read. To see the non-graphics calls past that point before NVN2 exists, `DM_NVN_API=<major>.<minor>` makes the NVN1 implementations report the version the game wants; the graphics calls that follow run through implementations of another ABI, so treat that run as a list of the calls the game makes and of the service blockers behind them, not as graphics behaviour.

## Failure catalogue

The failures met so far are filed by subsystem, one entry per mechanism (symptom, cause, fix, the games it was found on): [rewriter](../reference/rewriter.md), [memory and address space](../reference/memory.md), [threads](../reference/threads.md), [services and SDK calls](../reference/services.md), [movies](../reference/movies.md), [audio](../reference/audio.md), [frame rate](../reference/frame-rate.md), [NVN objects](../reference/nvn-objects.md) and [game state](../reference/game-state.md); graphics in [rendering](../reference/rendering.md) and [shaders](../reference/shaders.md). Most new-title failures are one of them in a new form, so search the [symptom index](symptoms.md) for the assertion, `[RED]` text, abort site or visible result first, then read the entry and the game notes it names.

A new finding goes to one place: a general mechanism as an entry in its reference (with a line in the symptom index), a fact about one game in that game's note (`dm/games/<id>/README.md`).
