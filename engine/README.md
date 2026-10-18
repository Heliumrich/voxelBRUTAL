# Voxel BRUTAL

A modular, editable voxel prototype built on BRUTAL's Vulkan/GLFW bindings. Framework library sources are unchanged. [Architecture](../docs/voxel-foundation.md), [LOD and visibility research](../docs/voxel-lod-research.md), [validation evidence](../docs/voxel-validation.md).

## Run

Use a stable **.NET 10 SDK** and a Vulkan 1.3 GPU. Development validation requires `VK_LAYER_KHRONOS_validation` from the Vulkan SDK; `--no-validation` disables it.

From the repository root in PowerShell:

```powershell
.\build.ps1 Test
.\build.ps1 Demo
.\build.ps1 Smoke
.\build.ps1 Smoke --validate-occlusion
.\build.ps1 Smoke --stress --pixel-error 12 --frames 1400
.\build.ps1 Demo --radius 64 --max-lod 6
.\build.ps1 Demo --min-y -256 --max-y 1024
```

WASD flies, Space/Ctrl moves vertically, Shift accelerates, right mouse or arrows turns, E removes a targeted voxel, Q places grass, and Escape exits. This is a fly-through terrain prototype; collision, persistence and gameplay are still future work.

The script selects `.tools/dotnet/dotnet.exe` when present, otherwise the installed SDK. `-DebugBuild` selects Debug. NuGet versions are pinned. This BRUTAL source snapshot omits framework projects/native build inputs, so native integration uses the published bindings while the engine source lives inside this fork. A fresh clone needs an installed SDK; the locally downloaded SDK/cache is ignored by Git.

On another platform: `dotnet build engine/VoxelBrutal.slnx -c Release`, then `dotnet run --project engine/VoxelBrutal.Demo -c Release`. Windows/RTX 4080 is the verified runtime target. The dependency-free CPU suite is an executable harness; run it with the script or `dotnet run --project engine/VoxelBrutal.Tests -c Release`.

## World and height

Chunks are **cubic 32×32×32**, addressed with signed 64-bit X/Y/Z coordinates. The default generated interval is **[-128, 512)**: 640 voxels vertically, with a highest editable generated-domain coordinate of 511. `--min-y` and `--max-y` configure content/streaming bounds rather than a chunk-system limit. A 4096-voxel interval is covered by CPU tests. Raising the interval does not allocate a dense vertical column: only selected cubic chunks are generated.

One globally continuous source combines a height field, 3D tunnels/chambers, and climate-dependent cave width and material mixtures. Temperate, arid and alpine weights blend across world coordinates rather than chunk borders. Sparse edits overlay that source. `--no-caves` disables caves. Coarse voxels point-sample the same source, so detail smaller than their voxel scale can disappear; this is not topology-preserving terrain simplification.

## Active rendering features

* Material-aware binary greedy meshing and eight-byte packed quads.
* Budgeted, face-balanced 2:1 adaptive octree selection, integer-relative camera origins and infinite reverse-Z depth.
* Shared-boundary meshing against the **actual selected neighboring LODs**. Coarse boundary faces subdivide onto the fine lattice. This creates a closed opaque piecewise-voxel surface without skirts.
* Atomic mesh-cover publication. Old and candidate versions coexist until every exact replacement and its boundary topology is resident. Edits and canceled work cannot publish mismatched borders.
* GPU frustum culling, packed-quad vertex pulling, shared mesh arena and indirect draw batches.
* Actual **current-frame two-phase GPU Hi-Z**: early visible seeds → minimum depth pyramid → late newly-visible geometry. Odd viewport edges, near-plane crossings, camera turns, edits and teleports are checked conservatively.
* Bounded workers, integration/upload budgets, stale-result rejection, explicit GPU upload pressure and deferred mesh frees.

Spatial closure is implemented. Smooth POP geomorphing, zero LOD popping, conservative thin-feature reduction and transparent-material transitions are separate future work. The exact unit-face oracle establishes the selected blocky surface's coverage, not the quality of its coarse approximation.

## Controls and diagnostics

| Argument | Meaning |
| --- | --- |
| `--radius N` | Requested horizontal radius in base chunks (32 voxels), enclosed by complete octree roots |
| `--lod N`, `--max-lod N` | Finest and root LOD levels; voxel scale is 2^LOD |
| `--pixel-error N` | Detail-selection scale heuristic, default 6; higher values reduce geometry |
| `--chunk-budget N` | Maximum candidate leaf count, default 8192 |
| `--quad-budget-mib N` | Shared packed-quad arena size, default 128 MiB; pressure coarsens an unpublished candidate |
| `--uniform` | Uniform level selection; default root level equals `--lod`, so use a small radius |
| `--workers N` | Fixed generation worker count |
| `--no-occlusion` | GPU frustum/depth baseline for comparisons |
| `--frames N` | Exit after a bounded number of frames |
| `--validate-occlusion` | Run actual GPU pixel/visibility comparisons against the uncullled reference |
| `--report PATH` | Save JSON runtime counters |
| `--screenshot PATH.ppm` | Save the final GPU-readback image; enables test-only readback overhead |

`--stress` moves/turns the camera, teleports, resizes and edits. Enable synchronization validation with `$env:VK_LAYER_VALIDATE_SYNC = '1'`. Hidden-window elapsed time includes sleeps, generation and validation; it is not a gameplay FPS measurement.

`Bench` records CPU generation/meshing on the current source. Historical height-field baseline figures are in the validation notes; they must not be compared as the same workload after adding caves. `Capture` remains available for LunarG's screenshot layer; `--screenshot` captures a final frame after loading and does not require that layer.

## Projects

| Project | Responsibility |
| --- | --- |
| Core | World data, generation, edits, snapshots, meshing, LOD plans, CPU visibility references, streaming and raycasts |
| Rendering | BRUTAL/Vulkan resources, mesh arena, version residency, Hi-Z, indirect draws and GPU diagnostics |
| Demo | GLFW input/camera, adaptive streaming coordinator and GPU validation scenes |
| Tests | Independent CPU correctness oracles and optional benchmarks |

Textures, ambient lighting, shadows, water, disk storage, collision, mesh shaders and ray tracing remain further milestones. BRUTAL's existing licence/third-party notices remain authoritative; the demo copies its notice and logo assets, while a shipped game also needs the required pre-game display and credits.
