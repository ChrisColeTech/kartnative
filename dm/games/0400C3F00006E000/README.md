# Mario Kart World

Switch 2, `0400C3F00006E000`, 1.0.0 (`ResourceSizeTable.Product.100`). Bring-up in progress: it does not boot yet.

## Build

| Module | Build ID | Notes |
|---|---|---|
| `main` | `9e244438c3d8ef1c703a6a85ef99c4f0` | 42.1 MB text, no SVCs; 502 `mrs tpidr_el0`, 29,446 x18 uses |
| `sdk` | `90ebba182862b9ecdc21fcb87b999012` | SDK 19.3.3 (`NintendoSDK_NVN-19_3_3`), NVN2; SVCs 0x01-0x5f, 0x70, 0x72, 0x80, 0x81, 0x82 |
| `subsdk0` | `d39a7a3d44530bb61b35690e5b0383d5` | movie library |

`main.npdm`: META flags 0x37 (64-bit, 39-bit address space), 64 MB of system resource, a Switch 2 ACID (0x300-byte signature area).

## What the profile needs and why

- `switch2 on`: from `main.npdm` (`profile_guess.py` `switch2`).
- Memory keys: provisional guesses for a Switch 1 layout, to be set by the bring-up.

## Found on this game

Nothing yet.

## Open

Everything: boot, NVN2, SM86 shaders.
