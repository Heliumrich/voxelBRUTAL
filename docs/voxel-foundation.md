# Voxel engine foundation and implementation decisions

Research checkpoint: 18 October 2026. This is a blocky, editable, opaque-terrain foundation. There is no universally optimal voxel architecture independent of workload. These choices create a measurable baseline for modern desktop CPUs and Vulkan GPUs, with correctness contracts that allow backends to be replaced.

## BRUTAL assessment

The local README and code are the starting point. The framework offers direct generated Vulkan bindings, managed handle wrappers, explicit unmanaged allocators, numeric types, GLFW windows, shader compilation, and a batch job system. It avoids an imposed scene graph or renderer, which lets terrain use shared buffers and indirect draws directly. `Brutal.Vulkan.Abstractions` has buffer/staging/frame helpers and partitioners; `Brutal.Vulkan.Vma` provides VMA integration. `Brutal.Concurrency/Jobs/JobScheduler.cs` supports bounded batches, but rejects new queues while running and exposes a busy-blocking `Wait`.

The costs are substantial: application code owns device capability negotiation, swapchain edge cases, synchronization, resource destruction, scene residency and scheduling. This checkout has no framework project files or solution; native libraries and generator build inputs are not sufficient to reconstruct a reliable source build just by globbing C# files. Following its README, the first renderer uses pinned published BRUTAL packages. No existing framework source has been modified. Engine project files live under `engine/`.

The renderer initially uses a few coarse Vulkan allocations through BRUTAL's bindings. It does not allocate memory for every chunk. A VMA-backed allocator can later replace this small boundary if fragmentation or additional resource types justify it. The core uses BCL numeric types and stays independent of native libraries, allowing deterministic headless correctness checks.

## World addressing, storage and ownership

`ChunkKey(X,Y,Z,Lod)` indexes a chunk-grid cell whose side is `32 << Lod` base-world units. Global addresses are signed 64-bit integers; negative positions use floor division, rather than C# truncation. LOD is bounded to 20 and coordinate arithmetic is checked. A full halo also needs representable coordinates on both sides; keys near the signed limits can be addressable while snapshot capture deliberately rejects an overflowing halo.

`ChunkData` stores uniform chunks without a dense array and promotes to an owned 32-cubed `ushort` material array on edits. Zero means air; all other IDs are opaque. Future palettes or RLE belong behind the data boundary, never in the mesher's hot loop. A material-aware mesher cannot use occupancy alone as its merge key.

`ChunkSnapshot` owns immutable 34-cubed samples, including a one-cell halo. Interior samples emit faces; halo samples only determine exposure. This prevents chunk seams from duplicate interior faces and avoids workers reading mutable neighbors. Snapshot allocation is bounded by in-flight workers; it is released after meshing rather than kept for every distant render chunk.

The current streamed procedural world regenerates chunks on demand, retains compact meshes and stores edits in a sparse overlay. It is not yet an on-disk chunk database. The overlay holds a read lock across each full capture, serializes edit transactions, and invalidates the affected chunk plus all touched halo neighbors at each requested LOD. Revisioned work tickets reject results superseded by edits or eviction. Coarse generation is explicitly point sampled: it is not a topology-preserving or conservative occupancy pyramid, and thin features can disappear.

`IVoxelSource` supplies arbitrary deterministic 3D material samples. `IVoxelBatchSource` optionally fills whole halos. The terrain source computes noise once per X/Z column and reuses the height along Y, avoiding 34 duplicate evaluations per column. The generic scalar path remains available for caves, authored blocks and other generators. The supplied demo source is a height field; the world data and meshing do not require one.

## Binary greedy meshing

[Lysenko's original meshing article](https://0fps.net/2012/06/30/meshing-in-a-minecraft-game/) motivates trading mesh rebuild latency against reduced primitive count. [Binary Greedy Meshing v2](https://github.com/cgerikj/binary-greedy-meshing) demonstrates occupancy-bit face elimination, material-compatible greedy merging, compact quad records and vertex pulling. Its reported timings describe its own C++ workload and hardware; they are not performance claims for this C# implementation.

This implementation independently builds X-oriented `ulong` occupancy rows from the immutable halo, computes six visible face masks with bitwise operations, rearranges exposed bits into each face's tangent axes, and greedily merges same-material rectangles. The 32-cell interior plus two halo bits fits one 64-bit lane. This leaves room for safe shifts and simpler boundary handling; 62-cell interiors remain an experiment to benchmark later. Chunk size affects edit latency, culling granularity, upload size and worker scheduling as well as meshing throughput.

Each quad is eight bytes: a geometry `uint` and a surface `uint`. Geometry packs X/Y/Z as six bits each, width-minus-one and height-minus-one as five bits each, and the six face directions as three bits. The lower sixteen surface bits store material; remaining bits are reserved. Face coordinates can reach the 32-cell outer plane. U/V axes cycle X→Y/Z, Y→Z/X, Z→X/Y. The CPU corner decoder and shader follow the same outward winding convention.

Occupancy and face scratch use pooled arrays; final meshes own only their quad arrays. Ambient occlusion, cutout geometry and transparency will require explicit face visibility/merge policies. They cannot be added merely by marking every nonzero material opaque. Greedy T-junctions and coarse/fine transitions are separate concerns; no undocumented screen-space quad inflation is used to disguise a spatial seam algorithm.

## LOD and visibility

Read [the LOD research and implementation notes](voxel-lod-research.md). The library provides budgeted octree selection, complete resident-cover resolution and explicit mixed boundary metadata. Uniform coverage is the safe default until a blocky seam strategy is integrated and verified. Parent retention prevents temporal loading holes; it cannot make independently downsampled geometry watertight. The demo streams one configured uniform level.

`WorldPosition` stores integer cells plus fractional offsets. Rendering subtracts integer chunk/camera origins before converting to float. The GPU receives camera-relative positions and a translation-free camera matrix. An infinite reverse-Z projection and D32 floating-point depth preserve useful distant depth precision; [NVIDIA's analysis](https://developer.nvidia.com/blog/visualizing-depth-precision/) explains the precision benefit.

The current renderer computes conservative chunk AABB frustum tests on the GPU and writes indirect commands, setting rejected chunks' instance count to zero. A CPU frustum reference is independently tested. Long-distance bounds receive a conservative arithmetic margin. Fully enclosed faces are already removed by meshing; hidden terrain behind other chunks is not yet culled by the GPU. The CPU minimum-depth pyramid provides the occlusion oracle for a later current-frame two-phase Hi-Z pass. Camera turns must not reuse stale depth as proof of invisibility.

## Vulkan rendering and lifetimes

[Khronos' multi-draw sample](https://docs.vulkan.org/samples/latest/samples/performance/multi_draw_indirect/README.html) and [the Vulkan Guide's GPU-driven implementation](https://vkguide.dev/docs/gpudriven/gpu_driven_engines/) support the shared-buffer approach: scene data lives in storage buffers, while compute produces indirect work. [Khronos synchronization examples](https://docs.vulkan.org/guide/latest/synchronization_examples.html) provide the transfer-write→shader-read and compute-write→indirect-read dependencies.

The baseline requires Vulkan 1.3 dynamic rendering and synchronization2, plus explicitly enabled `multiDrawIndirect` and `drawIndirectFirstInstance`. It uses a graphics/compute/presentation-capable queue family. Feature flags and storage-buffer, indirect-draw and dispatch limits are checked. Mesh shaders, descriptor indexing, device-generated commands and ray tracing are not needed for this baseline.

A shared device-local storage buffer holds packed quads. A best-fit range allocator coalesces free spans. Frame-local staging buffers transfer new immutable ranges; replacements allocate fresh ranges rather than overwrite meshes still used by an earlier submission. Old ranges are returned only after the final relevant fence serial completes. Renderable and merely resident meshes are separable, so retained LOD ancestors need not draw on top of replacement children.

Each frame slot owns staging, camera-relative chunk metadata, indirect commands, command buffers, a fence and a depth image. CPU reuse waits for that slot's fence. Render-finished semaphores belong to swapchain images, because presentation completion is not proven by a graphics fence alone. Resize rebuilds attachments after device idle; a zero-size framebuffer pauses rendering. One compute dispatch generates one indirect record per active chunk. One `vkCmdDrawIndirect` submits the opaque terrain batch, using a nonzero first-instance chunk index. The vertex shader pulls six vertices per packed quad; it needs neither conventional vertex buffers nor one descriptor bind per mesh.

The initial path keeps fixed indirect slots, rather than compacting commands and using `vkCmdDrawIndirectCount`. Compaction, terrain draw subdivision and meshlet/mesh shader paths should be introduced only when measured command or geometry cost warrants them. The legacy Vulkan Guide is an implementation reference, not a current normative API specification. The current Khronos specification and validation layers determine correctness.

## Streaming and scheduling

The render thread owns desired keys, request revisions, mesh publication and eviction. A bounded priority queue feeds a fixed number of worker loops. In-flight work includes completed-but-unintegrated results, so a slow renderer applies backpressure rather than allowing a completion backlog to grow. Request count, dispatch count, stale-ticket pruning, result integration time and upload bytes each have limits. Worker failures are returned explicitly and can be retried. Shutdown cancels generation and joins workers; the render loop never calls BRUTAL's busy wait.

Distance priorities are captured when requests are inserted. A moving camera updates the finite demand set; this baseline does not implement velocity prediction, per-stage work stealing, disk IO or incremental queue reprioritization. Edits have revision identity, so old results cannot overwrite newer geometry. A stale worker may finish useful computation, but its result cannot resurrect an evicted key.

## Acceptance and next experiments

The executable test suite compares merged quads with an independent scalar unit-face oracle, verifies immutable snapshots and edit halos, checks negative/large coordinates and winding, validates complete LOD partitions, exercises stale scheduling and failures, and compares conservative occlusion queries against full-resolution depth. Vulkan tests must additionally run on real hardware with validation enabled and inspect a rendered frame.

The optional benchmark records generation and mesh latency, primitive reduction and allocated output bytes for a fixed mixed terrain sample. Those measurements establish a baseline, not an optimum or a promised render distance. Follow-up profiling should record camera trace, terrain density, p50/p95/p99 job and frame times, GPU timestamps, mesh bytes, upload pressure and empty/solid ratios. Include checkerboards, caves, edits, teleports and constrained memory.

Recommended next order:

1. Add GPU timestamps, visible-draw counters and moving/teleport stress traces to the working indirect baseline.
2. Integrate a current-frame early/Hi-Z/late occlusion pass and compare rejection against the CPU oracle.
3. Implement globally aligned POP ordering/morphing or a dedicated blocky transition mesher; verify negative-coordinate boundaries, thin features and materials before enabling adaptive mixed rendering.
4. Add LOD hysteresis, persistent region storage and edit-aware coarse reduction. Then compare 32/62-cell chunks and worker scheduling policies on recorded traces.
5. Evaluate texture arrays, directional meshlet subdivision, mesh shaders or distant ray traversal using the same metrics. Fancy techniques should earn their complexity with workload-specific evidence.
