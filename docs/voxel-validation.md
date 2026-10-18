# Foundation validation

Measured on 18 October 2026 on this Windows development machine. These are development checks, not a frame-rate or maximum-distance claim.

## Build and CPU correctness

`.\build.ps1 Test` builds the four projects in Release and runs the standalone correctness harness. The solution builds with zero warnings and errors. The harness covers independent scalar meshing oracles, material boundaries, halo suppression, winding, immutable ownership, negative/large coordinates, terrain batch equivalence, cancellation, LOD partition coverage, atomic fallback, frustum conventions, reverse-Z occlusion references, stale worker results, retry, upload backpressure and precise raycasts.

The CPU Hi-Z tests include 2,000 randomized projected rectangles checked against a per-pixel oracle. This does not validate an actual GPU occlusion pass, which remains unimplemented.

## CPU measurement

Command: `.\build.ps1 Bench --output '..\.artifacts\cpu-baseline.json'`.

Runtime .NET 10.0.13; 16 logical processors available. Single-threaded warmed sample of 144 deterministic 32-cubed terrain chunks, including empty and solid chunks:

| Measurement | Result |
| --- | ---: |
| Mean halo terrain generation | 116.2 µs/chunk |
| Median binary greedy meshing | 58.9 µs/chunk |
| p95 binary greedy meshing | 127.1 µs/chunk |
| Exposed unit faces | 52,425 |
| Greedy quads | 14,892 |
| Packed output | 119,136 bytes |
| Meshing managed allocation, whole sample | 495,088 bytes |

The benchmark harness allocates its small timing list and output meshes during the measured allocation window. Scratch pools were warmed first. Results vary with JIT tiering, thermal state, terrain density and concurrent processes. Checkerboard geometry, arbitrary caves and sustained edits are correctness cases rather than the representative performance sample. No GPU timing is included.

## Vulkan runtime

GPU: NVIDIA GeForce RTX 4080, Vulkan 1.4.351 driver, Vulkan SDK validation layer 1.4.341. The renderer requests a Vulkan 1.3 path. Native BRUTAL packages are pinned in the rendering project.

Stationary validation: a radius-12 view loaded all **3,750 chunks**, containing **288,846 greedy quads**, with zero pending uploads, streaming failures or validation errors. This includes commands for empty meshes; the indirect command count is a candidate count, not a readback of actually visible chunks. GPU frustum rejection and hardware depth testing are active.

Synchronization validation found an acquire/layout dependency that was corrected by chaining the image transition's source stage to the colour-attachment acquire wait. It also exposed the need to reinterpret Vulkan unsigned limits represented by BRUTAL as signed integers. Both fixes are in the final backend. Final synchronization validation reports zero errors.

Stress command:

```powershell
$env:VK_LAYER_VALIDATE_SYNC = '1'
.\build.ps1 Smoke --stress --radius 12 --frames 900
```

The hidden WSI run submitted 900 frames while moving/turning the camera, teleporting twice, resizing twice and applying repeated terrain edits. It accepted 7,173 chunk builds and discarded 19 superseded results. The ending moving view had 2,762 resident chunks and 234,221 quads, zero queued uploads, zero build failures and **zero validation errors**, including resource destruction. The incomplete ending view is expected during continued motion, rather than a complete-residency performance measurement. The run is about correctness of streaming and lifetime changes; hidden-window elapsed time must not be presented as gameplay FPS.

Visual verification uses `.\build.ps1 Capture --radius 12 --frames 600`, with LunarG's screenshot layer saving frame 200 under `.artifacts/screenshots/`. The actual rendered image was inspected for terrain coverage, winding, material colours and background. A grass/stone ID mismatch found in that inspection was corrected. Screenshots and local machine output are ignored build artifacts.

## Scope of the evidence

This establishes a working modular first renderer on one Windows machine. It does not establish other GPU/vendor/platform behaviour, production frame latency, seamless mixed-resolution rendering, collision/gameplay, persistence, or GPU Hi-Z correctness. The next experiments and acceptance criteria are in [the architecture document](voxel-foundation.md) and [LOD research](voxel-lod-research.md).
