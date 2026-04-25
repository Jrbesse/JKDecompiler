# Project: JKDecompiler (High-Quality JKA BSP Decompiler)

## Project Overview
A specialized decompiler for *Star Wars Jedi Knight: Jedi Academy* (JKA) / *OpenJK* maps, focusing on high-fidelity reconstruction of scripting data, cameras, and lighting.

## Architecture
- **Language**: C# (.NET 8.0)
- **UI Framework**: Avalonia UI (MVVM)
- **Target Platforms**: Cross-platform (Windows, Linux, macOS)
- **Core Library**: `JKDecompiler.Core` (Parsing and Reconstruction)
- **Frontend**: `JKDecompiler.UI`
- **Verification**: `JKDecompiler.Tests` (xUnit)

## Technical Specifications: RBSP (Raven BSP)
JKA uses a modified version of the Quake 3 BSP format, resetting the version to 1 and adding game-specific lumps.

### Header
- **Magic Number (Ident)**: `RBSP` (ASCII)
- **Version**: `1` (Int32)
- **Lump Directory**: 19 entries, each with `Offset` (Int32) and `Length` (Int32).

### Lumps (Total 19)
| Index | Name          | Description                                         |
|-------|---------------|-----------------------------------------------------|
| 0     | Entities      | ASCII string of all entities and ICARUS parameters. |
| 1     | Shaders       | Surface shaders and texture references.             |
| 2     | Planes        | Plane equations for geometry/collision.             |
| 3     | Nodes         | BSP tree nodes.                                     |
| 4     | Leafs         | BSP tree leaves.                                    |
| 5     | LeafFaces     | Face indices for each leaf.                         |
| 6     | LeafBrushes   | Brush indices for each leaf.                        |
| 7     | Models        | Sub-models (doors, elevators, etc.).                |
| 8     | Brushes       | Convex volumes for collision.                       |
| 9     | BrushSides    | Planes defining the sides of brushes.               |
| 10    | Vertexes      | 3D coordinates, UVs, and colors.                    |
| 11    | MeshVerts     | Vertex indices for mesh surfaces.                   |
| 12    | Fogs          | Fog volume descriptions.                            |
| 13    | Faces         | Surface descriptions (**104 bytes** per record).    |
| 14    | Lightmaps     | Baked lightmap data.                                |
| 15    | LightGrid     | Lighting data for dynamic entities.                 |
| 16    | Visibility    | PVS (Potentially Visible Set) data.                 |
| 17    | LightArray    | Additional lighting information.                    |
| 18    | Decals        | Map decal positioning.                              |

### Critical Data Structures
- **Face (Lump 13)**: 104 bytes. Includes surface type (Polygon, Patch, Mesh, Billboard), lightmap data, and patch dimensions.
- **Entity (Lump 0)**: Contains ICARUS scripting keys (`usescript`, `spawnscript`, `PARM1-8`) and camera data (`info_player_start`, `target_position`, etc.).

## Project Priorities
1.  **Data Integrity (Current Focus)**: Ensure 100% of RBSP data is parsed, specifically lighting data (Lightmaps, LightGrid, LightArray).
2.  **Scripting & Metadata Preservation**: Absolute fidelity for entity keys. Camera coordinates and orientations must not be modified or snapped.
3.  **Geometric Accuracy**: Reconstruct convex brushes from `Planes` and `BrushSides` to ensure solid geometry in editors (Radiant).
4.  **Texture Alignment**: Reverse-engineering baked BSP UVs back to `.map` format shift/scale/rotate parameters.

## Status
- **Core Parsing**: 100% complete for 18-lump RBSP (JKA).
- **Exporting**: `.map` exporter with UV Solver, Bezier Patch Restoration, and Detail Brush support implemented.
    - *Geometry*: Refactored to plane-based reconstruction to resolve GTKRadiant structural errors.
    - *Patching*: Added degenerate patch filtering to resolve `safe_malloc` crashes in Q3Map2.
    - *Texture/Shader Alignment*: Fixed export syntax to adhere to Quake 3/JKA `.map` format.
- **UI**: Entity, Model, ICARUS Script, and Asset inspection complete.
- **Testing**: Validated against `t1_sour.bsp`.
- **Known Issues**:
    - `Winding_BaseForPlane: no axis found` / `Brush_Resize: invalid input` still occur for some brushes in GTKRadiant.
    - *Investigation ongoing*: Suspect current plane-point derivation logic does not strictly adhere to the expected convex hull vertex format of Radiant.

## Phase 3: UX Refinement
1. [x] **Asset Checker Refinement**: Improve logic for PK3 asset scanning and path validation.
2. [x] **Export UI Feedback**: Add progress bar/status message for .map export.
3. [ ] **3D Hardware Preview**: Integrated 3D view in Avalonia for geometry and entity placement.
4. [ ] **Geometry Integrity**: (High Priority) Solve remaining brush-side winding and vertex definition issues.



## Testing Resources
- **Sample Map**: `t1_sour.bsp` (located in the project root) is used for validating JKA-specific features.

## Technical Safeguard
This file serves as the definitive technical and architectural reference in the event of a system or CLI crash.
