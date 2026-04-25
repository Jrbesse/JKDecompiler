# JKDecompiler
A high-fidelity decompiler for *Star Wars Jedi Knight: Jedi Academy* (JKA) map files (`.bsp`).

JKDecompiler is built to achieve lossless reconstruction of Raven BSP (RBSP) data, producing stable, Radiant-ready `.map` files that preserve geometry, entity scripting, and texture alignment with maximum precision.

## Key Features

- **Advanced RBSP Parsing**: Full support for JKA-specific BSP variants (18 and 19 lump formats).
- **Precision Geometry Reconstruction**:
    - **Structural Integrity**: Correct handling of 12-byte JKA brush side structures ensures convex brushes are perfectly grouped without corruption.
    - **Radiant Stability**: Implements a high-precision orthonormal basis generator with wide-scale spacing (4096 units) and plane projection to eliminate "bad normal" and "collinear vertex" errors.
    - **Automated Winding**: Corrects plane winding orders to ensure inward-facing normals as required by the `.map` format specification.
- **Texture & Shader Fidelity**:
    - **Intelligent UV Solver**: Matches baked BSP faces to brush sides to recover accurate shift, scale, and rotation parameters.
    - **Path Normalization**: Automatically cleans shader paths (stripping redundant `textures/` prefixes) to ensure assets load immediately in GTKRadiant.
    - **Patch Restoration**: Converts BSP Bezier data back to editable `patchDef2` blocks.
- **Dual Interface Modes**:
    - **Interactive UI (Avalonia)**: Deep inspection of entities, ICARUS scripts, and asset dependencies.
    - **Headless CLI**: Command-line support for batch decompilation and automated pipelines.
- **Asset Verification**: Scans local `GameData` and PK3 archives to identify missing textures, models, and scripts.

## Getting Started

### Graphical Mode
1. Launch `JKDecompiler.exe`.
2. Go to **File > Set JKA Path** and select your Jedi Academy `GameData` folder.
3. Open a `.bsp` file.
4. Use the tabs to review entities, scripts, and missing assets.
5. Click **Export Map...** to generate a Radiant-ready `.map` file.

### Command Line Mode
Decompile maps directly from the terminal for batch processing:
```bash
JKDecompiler.UI.exe <input.bsp> <output.map>
```

## Requirements
- **.NET 8.0 Runtime**
- *Star Wars Jedi Knight: Jedi Academy* installation (for asset validation).

## Build Status
Validated against original Raven Software maps (e.g., `t1_sour.bsp`), achieving 100% compile-back success using `q3map2.exe`.
