# Foundation validation

Measured on 18 October 2026 on this Windows development machine. These are development checks, not a frame-rate or maximum-distance claim.

## Second milestone: adaptive LOD and actual GPU Hi-Z

The current Release build has zero warnings/errors and **41 passing CPU cases**. New cases independently expand each selected coarse/fine voxel volume onto a common grid and compare every oriented material face against the complete mesh output. Fixtures cover all axes, both coarse/fine orientations, negative and huge coordinates, corners, edits, topology identity, cancellation, half-grid coordinate/extent64 packing, height settings, 3D caves and continuous climate weights.

The actual Vulkan comparison harness runs with synchronization validation:

```powershell
$env:VK_LAYER_VALIDATE_SYNC = '1'
.\build.ps1 Smoke --validate-occlusion
```

All **eleven GPU image comparisons** matched the reference with exactly **zero differing bytes** and zero Vulkan validation errors on the RTX 4080. The harness tests hidden solid rejection; first-frame recovery after occluder removal, camera rotation and teleportation; restored occluders; near-plane and eye-inside cases; full mesh-version replacement; a 997×613 viewport; coordinate/extent64 carry decoding; and genuine half-voxel GPU coordinates. The reference disables Hi-Z and draws every frustum candidate using the same geometry and camera. Visibility checks use GPU-written per-mesh flags, rather than CPU estimates. Results and paired PPMs are saved under `.artifacts/occlusion-validation/`.

A static cave/biome terrain view loaded a complete **6,406-chunk** mixed cover: 2,320 LOD0, 1,950 LOD1, 1,896 LOD2 and 240 LOD3 chunks. It held 5,098,599 packed quads. GPU readback reported 743 frustum candidates, 102 occluded candidates and 1,541,217 submitted quads. The cover reached publication generation2 and all work/uploads settled, with zero streaming or Vulkan errors. This workload includes the new 3D cave field and is not comparable to the first milestone's simple height-field microbenchmark below. Color readback, validation, generation and sleeps were enabled; its elapsed time is not gameplay FPS.

The GPU arena pressure test used only **8 MiB** of quad storage. It rejected an oversized in-progress candidate, retained the prior complete cover, raised the selection error once, and settled a replacement of 456 chunks spanning LOD0 through LOD4. Its 710,023 quads fit the constrained arena; no work remained and validation reported zero errors. A coarsest root cover that cannot fit reports an explicit capacity error instead of leaving a permanent upload stall.

Terrain now defaults to `[-128,512)`, with 640 vertical voxels. A separate CPU test raises the interval to4096 and confirms cubic chunk allocation is independent of total column height. The generator includes blended climate materials and actual 3D cave air/solid variation. This verifies generation/addressing behavior; it does not establish an unlimited practical view distance.

Profiling found approximately 17.5 ms for selection and 130 ms for topology-plan construction at 6,406 leaves, and boundary sampling dominated the measured build CPU. The demo consequently prepares immutable plans off the render thread, coalesces camera changes, invalidates obsolete edit epochs, and publishes only on the owner thread. Batched/shared boundary sampling and incremental topology reuse remain useful optimization targets.

The final asynchronous-planning stress ran 1,800 submitted frames with moving/turning camera, two teleports, two resizes and three edits. It published fourteen complete topology generations, built 9,526 accepted meshes, retained an active five-level cover during subsequent loading, and ended with zero streaming failures and zero Vulkan/synchronization errors. The changing view was intentionally not settled at exit. Its report is `.artifacts/adaptive-hiz-stress.json`.

## First milestone historical baseline

The remaining baseline measurements were recorded before the cave/biome generator and adaptive topology path were added. They establish the initial implementation's behavior, rather than current performance for the more complex source.

## Build and CPU correctness

`.\build.ps1 Test` builds the four projects in Release and runs the standalone correctness harness. The solution builds with zero warnings and errors. The harness covers independent scalar meshing oracles, material boundaries, halo suppression, winding, immutable ownership, negative/large coordinates, terrain batch equivalence, cancellation, LOD partition coverage, atomic fallback, frustum conventions, reverse-Z occlusion references, stale worker results, retry, upload backpressure and precise raycasts.

The CPU Hi-Z tests include 2,000 randomized projected rectangles checked against a per-pixel oracle. These remain useful independently of the GPU comparisons above.

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

The current evidence establishes the modular renderer, selected mixed-LOD surface coverage and GPU Hi-Z behavior on one Windows machine. It does not establish other GPU/vendor/platform behaviour, production frame latency, smooth visual LOD morphing, thin-feature preservation, collision/gameplay or persistence. Next experiments are in [the architecture document](voxel-foundation.md) and [LOD research](voxel-lod-research.md).

## ImGui, display settings and presentation — 2026-10-18

Release solution build: zero warnings/errors. All 41 CPU tests pass. The GPU occlusion regression still passes all 11 comparisons with exact byte equality and zero Vulkan synchronization-validation errors after adding the UI render pass and presentation extensions.

`--validate-ui` exercises the actual Dear ImGui native input queue and BRUTAL Vulkan backend. Ctrl+click, Ctrl+A, typing and Enter produce 144 FPS; typed 1 clamps to 30, 999 clamps to 360, and 60 restores the default. Native F1 events close/reopen settings, which capture gameplay input. The harness also switches every supported present mode on the actual surface: FIFO, FIFO relaxed, Mailbox, Immediate and FIFO latest ready. An unavailable request falls back to FIFO with an explanation. Six window-mode transitions verify decorations, monitor-sized borderless bounds, and exact restoration of the windowed position/size. All pass with zero validation errors, including backend/renderer teardown.

The adapter reports `VK_KHR_swapchain_maintenance1`, compatible dynamic mode switching, and `VK_KHR_present_id`/`VK_KHR_present_wait`. Presentation fences protect retirement; present-wait is optional pacing, not a substitute for those fences. GPU frame timestamps return real nonzero readings. Device-wide utilization comes from Windows GPU Engine counters matched to the Vulkan LUID; the UUID-matched NVML fallback was separately checked against NVIDIA's driver counters. Unavailable identities remain unavailable.

Private high-resolution timer measurements, independent of GPU readback/display refresh:

| Requested cap | Measured FPS |
| --- | ---: |
| 30 | 29.991 |
| 60 | 60.000 |
| 144 | 143.941 |
| 360 | 359.807 |

A 1,800-frame 3840×2160 borderless run with the ImGui overlay, Mailbox, the default 60 FPS cap and synchronization validation completed with zero errors. It settled all 8,191 selected chunks across LODs 0–3, containing 5,988,265 resident quads. The final smoothed interval was 16.668 ms (59.995 FPS), with a 3.667 ms GPU timestamp interval. This is a capped functional run, not an uncapped throughput or end-to-end latency benchmark.

A separate 420-frame windowed terrain run produced and visually verified the real GPU-readback overlay/settings image. UI font/spacing scaling follows display DPI. Color readback disables swapchain clipping, since clipped hidden/occluded pixels may otherwise be undefined; normal gameplay keeps clipping enabled. Readback and validation overhead affect the FPS displayed in these captures. Local reports/images are under `.artifacts/display/`.

Windows Independent Flip/DWM composition mode was not measured with ETW/PresentMon. No zero-compositor-latency claim is made. The presentation implementation and capability requirements are documented in the [renderer notes](../engine/VoxelBrutal.Rendering/README.md#presentation-ui-and-timings).
