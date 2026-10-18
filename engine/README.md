# Voxel BRUTAL foundation

A separate engine layer on top of BRUTAL, inside this fork. The upstream framework directories are unchanged. Start with [the architecture and research](../docs/voxel-foundation.md) and [LOD/visibility decisions](../docs/voxel-lod-research.md).

## Run

Install a stable **.NET 10 SDK**. Rendering needs Vulkan 1.3, graphics/compute/presentation on one queue family, dynamic rendering, synchronization2, multi-draw indirect and nonzero indirect first-instance support. The renderer checks these features and its buffer limits before using a device. Development validation uses the Vulkan SDK's `VK_LAYER_KHRONOS_validation`; pass `--no-validation` if it is unavailable.

From the repository root in PowerShell:

```powershell
.\build.ps1 Test
.\build.ps1 Demo
.\build.ps1 Smoke
.\build.ps1 Bench
.\build.ps1 Demo --lod 2 --radius 24
.\build.ps1 Smoke --stress --radius 12
.\build.ps1 Capture --radius 12 --frames 600
```

The script uses `.tools/dotnet/dotnet.exe` if present, otherwise the installed `dotnet`. `-DebugBuild` selects Debug instead of Release. `Capture` uses the Vulkan SDK's screenshot layer and writes a frame under `.artifacts/screenshots/`. The session's SDK was downloaded locally, with its official SHA512 verified; it is ignored by Git. A fresh clone needs an installed SDK. NuGet packages are pinned in the rendering project. The published packages are used because this BRUTAL source snapshot does not ship its own project files or native build system, as explained in the upstream README. The source checkout is the reference for API integration.

On other platforms, use `dotnet build engine/VoxelBrutal.slnx -c Release`, `dotnet run --project engine/VoxelBrutal.Tests -c Release`, or `dotnet run --project engine/VoxelBrutal.Demo -c Release`. Windows is the verified runtime target for this milestone.

The demo is a flying terrain prototype. WASD moves, Space/Ctrl moves vertically, Shift accelerates, right mouse or arrows turns, E removes a targeted voxel, Q places grass, and Escape exits. `--workers N`, `--radius N`, `--lod N`, `--frames N` and `--no-validation` are available. The default view is a 25-by-25 chunk square across six vertical layers, with each chunk covering 32 base voxels. The procedural height bounds limit the demo's vertical requests; the core world and mesher are fully 3D. Views beyond the explicit 16,384 CPU chunk budget are refused.

`--lod` configures a uniform voxel scale for the demo. The CPU library also contains adaptive selection and atomic resident coverage. **Seamless mixed-resolution geometry is not enabled.** The safe uniform policy prevents unsupported spatial transitions. The next LOD milestone is globally aligned POP ordering/morphing or a validated transition mesher.

## Projects

| Project | Responsibility |
| --- | --- |
| `VoxelBrutal.Core` | Chunk coordinates/data, immutable halos, procedural source, edits, meshing, LOD, visibility references, streaming and DDA raycasts; no native dependency |
| `VoxelBrutal.Rendering` | Actual BRUTAL/Vulkan device and swapchain, GPU allocations, upload queue, mesh arena, shaders, compute frustum culling and indirect rendering |
| `VoxelBrutal.Demo` | GLFW application, camera, streaming demand and edit input |
| `VoxelBrutal.Tests` | Dependency-free executable correctness suite, independent reference oracles and optional CPU benchmark |

Run the executable test harness; `dotnet test` does not discover these tests. Benchmark output describes CPU meshing/generation on this machine, including empty and solid terrain chunks. It is not an FPS result or an arbitrary-terrain worst-case guarantee.

## Milestone boundary

The implemented renderer uses opaque material colours, floating-point reverse-Z, a device-local packed-quad arena, fenced per-frame staging and draw buffers, GPU frustum culling and one multi-draw indirect command per terrain pass. Meshes share allocations; there is no buffer, descriptor or pipeline bind per chunk. Uploads and CPU work have explicit budgets. Mesh ranges retire after their last GPU use completes.

The occlusion pyramid in Core is an independently tested **CPU reference**. The renderer does not yet perform GPU Hi-Z occlusion. Mixed-LOD seam geometry/morphing, LOD hysteresis, texture arrays, ambient occlusion, transparency, persistence, collision, mesh shaders, ray tracing and a gameplay loop remain further milestones. See the research documents for the order and acceptance criteria.

BRUTAL's `NOTICE.txt` and logo are copied beside the demo. A shipped game must also satisfy its required pre-game logo display and credits; the framework's existing licence and third-party notices remain authoritative.
