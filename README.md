# JKDecompiler
A high-fidelity decompiler for *Star Wars Jedi Knight: Jedi Academy* (JKA) map files (`.bsp`).

This tool is designed to reconstruct map geometry, entity data, and lighting properties with absolute fidelity, ensuring that maps can be opened in GtkRadiant without loss of information.

## Features
- **RBSP Parsing**: Full support for 18-lump JKA BSP format.
- **Flawless Decompilation**: 
    - Reconstruction of Radiant-compatible `.map` files.
    - UV Alignment Solver for perfect texture placement.
    - Bezier Patch Restoration to `patchDef2` curve blocks.
    - Structural and Detail brush preservation.
- **Asset Verification**: Built-in checker to identify missing textures, models, and ICARUS scripts by scanning local `GameData` folder and internal PK3 archives.
- **Interactive UI**:
    - Entity inspection and filtering.
    - Asset usage reports.
    - ICARUS script extraction.

## Getting Started
1. Launch **JKDecompiler**.
2. Go to **File > Set JKA Path** and select your Jedi Academy `GameData` folder.
3. Open a `.bsp` file.
4. Review entities, scripts, and missing assets in the respective tabs.
5. Click **Export Map...** to generate a Radiant-ready `.map` file.

## Requirements
- .NET 8.0 Runtime
- Jedi Academy `GameData` installation for asset validation.
