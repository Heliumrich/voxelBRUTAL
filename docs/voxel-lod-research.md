# Blocky voxel LOD, visibility and streaming research

Research and implementation checkpoint: 18 October 2026. This document concerns blocky, opaque terrain. The first milestone established CPU selection and visibility references; the second adds topology-aware mesh plans, atomic mixed-LOD publication and a validated two-pass Vulkan Hi-Z renderer.

## Decisions in this milestone

The world model offers uniform or dense chunk payloads, and the streamed procedural demo keeps a sparse edit overlay. LOD selection uses a finite forest of equal-size octree roots. These are separate structures: editing a voxel does not require rebuilding a monolithic world octree. A `ChunkKey` identifies a resolution and a chunk-grid address; one chunk contains 32 cubed samples at its selected resolution.

`LodSelector` has two policies:

- The safe default requests a uniform level across the root forest. If the requested cover exceeds its explicit chunk budget, it selects a coarser **complete** cover and reports `BudgetLimited`. It never truncates a partition to meet a count budget.
- Adaptive mixed selection prioritizes projected voxel scale divided by distance to the chunk AABB. A split includes the necessary coarse-neighbour split closure before checking the budget. The resulting target leaves are face-balanced to 2:1. Selection does not depend on view direction, so rotating the camera does not itself change the terrain partition.

This screen error is a scale heuristic, not a measured mesh simplification error bound. The generator's coarse point-sampling policy can erase thin features and is not conservative occupancy reduction. The demo quantizes selection updates to 64-voxel camera cells and coalesces movement while a candidate builds. Per-node hysteresis, incremental root movement, edit-aware coarse aggregation, and measured error metadata remain future work.

`ResidentLodCover` separates the desired partition from what has actually reached GPU residency. In mixed mode, it retains an ancestor until every desired child subtree supplies complete coverage. In safe mode, a uniform level changes globally only when the complete replacement level is resident. An accepted empty mesh counts as resident. A cold start with no covering mesh reports uncovered roots explicitly.

The earlier stateless `ResidentLodCover` prevents temporal loading holes but can temporarily put levels differing by more than one next to each other. It is therefore not the publication path for the adaptive demo. `LodBoundaryPlan` still exposes those boundaries for diagnostics and legacy callers.

The adaptive path instead builds a `LodMeshPlan`. It validates a complete, non-overlapping partition of the finite root forest and rejects any face adjacency exceeding 2:1. It caches all touching neighbours, including all four finer neighbours on a coarse face. Every mesh identity includes its chunk key, content revision and a deterministic topology stamp. The stamp includes neighbour keys and revisions: a neighbour edit can alter the shared boundary even if this chunk's own interior revision did not change.

`AtomicLodPublication` retains the entire published plan while the candidate loads and remeshes. It publishes only when every exact candidate version is GPU resident. Old and new versions of the same chunk can coexist until this switch. Cancellation or a newer candidate releases unused staging requirements without changing the published cover. This deliberately conservative transaction avoids temporarily unbalanced fallbacks and prevents pairing old boundary geometry with new neighbours. It can delay detail updates while one candidate mesh is unavailable; local transaction groups are a later scheduling optimization.

The integration must retain the current render cover and required ancestors until replacements finish uploading. Retaining only the finest desired set would remove the very fallback meshes that keep coverage intact. CPU generation completion and GPU residency are different states.

## Spatial transitions for blocky terrain

The implemented boundary method represents the surface of the selected piecewise-resolution solid, rather than adding skirts. Interior quads use the existing binary greedy mesher. Chunk boundary faces are rebuilt using materials from the **actual selected neighbour leaves**, sampled on their own global lattice. A coarse face next to finer chunks uses a 64 by 64 half-step grid, so each finer cell can occlude or expose its corresponding part of the coarse face. Fine faces independently query the coarse neighbour occupancy. Thus a solid/air boundary is emitted by the solid side, while two solid sides suppress the shared face. At the finite forest boundary, missing neighbours are explicit air.

The snapshot and boundary context must be captured under the same source read lock and carry the plan's exact topology stamp. Packed half-step positions and dimensions preserve the finer face subdivision without converting global coordinates to floats. Atomic publication supplies the temporal half of correctness; the boundary mesher supplies the spatial half. The intended correctness claim is watertight opaque occupancy coverage across the selected resolution boundary, not smooth visual morphing or preservation of thin features lost by coarse sampling. Independent boundary-coverage tests are required in addition to partition tests.

[Mikola Lysenko's blocky voxel LOD article](https://0fps.net/2018/03/03/a-level-of-detail-method-for-blocky-voxels/) applies POP buffers to integer-aligned quad meshes. It orders primitives by the level at which quantization collapses them, allowing draw ranges to omit finer primitives. It also describes continuous vertex morphing and stable rounding seeded by the maximum level of a chunk and its neighbours. This is a promising next experiment because it operates on the existing blocky surface mesh.

To implement that experiment correctly here, quantization must use a common global power-of-two lattice, including at negative coordinates. Camera-relative conversion must happen after the integer lattice operations. Neighbour level metadata, primitive ordering for greedy rectangles, material appearance, and quantization at chunk boundaries need explicit tests. Merely scaling independently sampled coarse chunks is not the POP method and does not inherit its seam properties.

[Godot Voxel Tools' blocky skirt source](https://raw.githubusercontent.com/Zylann/godot_voxel/master/meshers/blocky/blocky_lod_skirts.h) documents that its extra border geometry hides many cracks but does not always close them. It also identifies problems around water surfaces and mismatched levels. Skirts are a useful optional visual workaround for some opaque terrain, but they should not be described as a general watertight solution.

[Eric Lengyel's Transvoxel description](https://transvoxel.org/) targets transition triangulation between resolutions of a voxel density field. It is appropriate if a future terrain backend uses smooth isosurfaces. It is not a drop-in transition mesh for the current material-valued cube faces.

## Precision and frustum culling

`WorldPosition` stores a signed 64-bit integer cell plus fractional offsets. It subtracts integer positions through `Int128` before converting the small difference to floating point. This avoids losing individual voxels when the global coordinate exceeds the exact integer range of `double`. A relative floating-point render origin does not, by itself, solve precision if global positions were already rounded before subtraction.

`Frustum.FromViewProjection` uses System.Numerics row-vector matrix conventions and Vulkan's `0 <= z <= w` clip volume. Its extracted planes work for conventional and reverse-Z projections. A degenerate infinite far plane does not introduce a false finite render distance. `RelativeAabb.FromChunk` rounds bounds outwards; support-point tests preserve touching geometry.

The GPU culling implementation must use the same matrix convention, camera origin and conservative bounds. Shader arithmetic at a silhouette can otherwise reject boxes that the CPU reference preserves. Visibility never determines whether a chunk exists or should remain available for LOD coverage; it only determines submission.

[NVIDIA's depth precision analysis](https://developer.nvidia.com/blog/visualizing-depth-precision/) explains why a floating-point reverse-Z depth buffer improves distance precision. A reverse-Z implementation must consistently pair its projection, a depth clear of zero, a greater-depth comparison, and the occlusion reduction direction. A reversed projection combined with ordinary clear/comparison settings is incorrect.

## CPU occlusion reference and implemented GPU Hi-Z

`ReversedDepthPyramid` is a **CPU correctness reference**, not a GPU occlusion pass. It builds a minimum-depth pyramid from current opaque reverse-Z depth. Non-power-of-two dimensions use complete ceil-halving reduction. Queries inspect every overlapped texel at a selected mip; unaligned rectangles do not rely on a single centre sample. Clear-depth holes, near-plane crossing bounds, unreliable offscreen bounds, and equal-depth comparisons remain conservatively visible.

[Niagara's actual culling shader](https://raw.githubusercontent.com/zeux/niagara/master/src/shaders/drawcull.comp.glsl) demonstrates the early/late visibility split, reverse-Z nearest-depth comparison, and indirect command generation. Its [depth reduction shader](https://raw.githubusercontent.com/zeux/niagara/master/src/shaders/depthreduce.comp.glsl) uses minimum reduction. The implemented Vulkan renderer follows that high-level current-frame approach: draw previously visible terrain, build Hi-Z from that frame's opaque depth, then test the remaining candidates and draw late survivors. It also tests early seeds to update the next visibility history. Reusing previous-frame depth alone would be unsafe during camera movement or teleportation.

GPU resources are separate from the CPU reference. Each fenced frame slot owns its sampled D32 depth image, R32 storage/sampled pyramid and views, indirect command buffer, visibility counters and optional color readback. Mip zero copies the exact viewport depth into a power-of-two padded image; padding stays at far depth zero. Subsequent compute dispatches minimum-reduce each mip with per-mip barriers. Exact copy avoids losing edge samples by resampling an odd-sized viewport.

The culling shader expands AABBs for float rounding, projects their corners, adds a full pixel rectangle border and inspects every overlapped mip texel. Bounds crossing the eye or near plane remain visible. A strict reverse-Z comparison with bias can reject only a box behind the farthest occluder over its complete projected rectangle. Coarse mip cells containing sky or padding intentionally reduce culling effectiveness. History uses full mesh identities; the late current-depth pass discovers newly visible geometry after camera or scene changes.

`OcclusionValidation` exercises the **actual Vulkan compute, indirect draw, WSI and BGRA8 readback path**. On an NVIDIA GeForce RTX 4080, eleven comparisons passed with zero differing color bytes and zero Vulkan/synchronization validation errors. The initial three-solid fixture submitted two visible meshes and rejected the hidden mesh. On the first frame after removing the occluder, one early and one late mesh rendered, with identical pixels to an all-frustum-visible reference.

Further comparisons covered restoration, first-frame camera rotation and teleportation, near-plane crossing, an eye-containing AABB, simultaneous old/new mesh versions, a `997 x 613` viewport and packed half-grid carry/fractional-coordinate decoding. Every output matched its independent all-frustum or integer-lattice geometry reference exactly; the test permits at most one channel byte of error. Reports and selected paired PPM captures are written to `.artifacts/occlusion-validation/`, including `results.json`. These fixtures establish correctness for those cases, not a universal performance or render-distance guarantee.

## Work scheduling

BRUTAL's inspected `JobScheduler` is a batch scheduler: it refuses `Queue` while jobs run, and its `Wait` busy-blocks. It can support bounded batch adapters, but invoking `Wait` in the render loop would violate streaming latency goals. The engine's separate bounded producer/consumer pipeline keeps request ownership, cancellation tickets, completed work and GPU upload publication explicit.

The useful general pattern from [Lysenko's meshing article](https://0fps.net/2012/06/30/meshing-in-a-minecraft-game/) is to keep generation latency and final render cost separate: publish a useful result promptly and permit later refinement. Here, versioned immutable snapshots and upload-complete replacement provide the necessary ownership boundary for future fast/quality mesher variants.

A local Release diagnostic using eight level-five roots, the spawn camera, a 900-pixel viewport and six-pixel error selected 6,406 leaves. Warm selection took approximately 17.5 ms and complete neighbour/stamp plan construction approximately 130 ms on this development machine. Those synchronous operations would exceed a frame budget even though they run only after camera-cell changes. Plan preparation therefore belongs on a single coalesced background task using immutable camera, settings and revision snapshots; staging, resource retention and final publication remain owner-thread operations. An edit epoch must invalidate a prepared result captured before that edit.

For 100 leaves sampled across the selected partition, mean CPU costs were approximately 0.74 ms for batched chunk/halo generation, 3.10 ms for boundary context capture and 0.30 ms for meshing. These are diagnostic measurements rather than portable benchmarks. Scalar boundary samples currently repeat terrain column and climate work; sharing immutable neighbour boundary data or adding a batch face sampler is a more targeted next optimization than weakening cave generation or seam correctness. Capacity admission must also account for old plus candidate **quad bytes**, not only chunk counts: a candidate that cannot fit beside the published cover otherwise remains under permanent upload backpressure.

Sparse voxel ray traversal and mesh shaders remain optional backends. [Arseny Kapoulkine's meshlet measurements](https://zeux.io/2023/01/16/meshlet-size-tradeoffs/) show that larger clusters can reduce culling effectiveness. The present compact-quad indirect draw approach should be profiled before adding mesh shader complexity or assuming that meshlets improve this workload.

## Voxy structural reference

The original [Voxy repository](https://github.com/MCRcortex/voxy) provides a useful architectural comparison. Its [WorldEngine](https://github.com/MCRcortex/voxy/blob/dev/src/main/java/me/cortex/voxy/common/world/WorldEngine.java) separates world section storage and dirty callbacks from rendering. Its [AsyncNodeManager](https://github.com/MCRcortex/voxy/blob/dev/src/main/java/me/cortex/voxy/client/core/rendering/hierachical/AsyncNodeManager.java) has an asynchronous preparation path and explicit render-thread publication. Its [HierarchicalOcclusionTraverser](https://github.com/MCRcortex/voxy/blob/dev/src/main/java/me/cortex/voxy/client/core/rendering/hierachical/HierarchicalOcclusionTraverser.java) manages GPU traversal queues, mesh requests and Hi-Z visibility resources.

Those responsibilities support the structural choice made here: keep world storage, meshing jobs, topology publication and GPU visibility independent. This is architectural inspiration from primary source inspection, not copied implementation code. Voxy's OpenGL and Minecraft integration details, packed identifiers and shader interfaces are not used as this Vulkan engine's contracts.

## Vertical range

The demo's configurable default terrain interval is `[-128, 512)`: 640 blocks, with a highest occupied coordinate of 511. This is moderately taller than the standard modern Minecraft baseline. Mojang's [Caves & Cliffs Part II release notes](https://www.minecraft.net/en-us/article/caves---cliffs--part-ii-out-today-java) document a 384-block generation/build range extending down to -64. The engine keeps vertical coordinates and chunk streaming fully three-dimensional; the demo interval is a generator/selection setting, not an addressing or mesher limit.

## Verification

The complete CPU harness currently contains 41 passing cases, including fourteen LOD/visibility cases. It checks negative-coordinate partition coverage against an independent dense occupancy oracle, several balanced selection budgets, globally atomic uniform replacement, multilevel resident fallback, mixed-face metadata, integer subtraction at `2^56`, Vulkan clip planes, infinite reverse-Z, odd-sized depth pyramids and clear-depth holes. Two thousand randomized CPU occlusion rectangles are checked against a full-resolution pixel oracle to rule out false rejection. Mesh-plan cases check four-way neighbour enumeration, stable version reuse, neighbour edit invalidation, invalid partition rejection, atomic GPU version publication and cancellation of stale candidates. Other suites cover mixed-resolution boundary surface oracles, terrain continuity/caves and streaming ticket ownership.

The eleven actual GPU comparisons above separately exercise runtime culling and rendering correctness. CPU partition and boundary tests establish discrete shared-face coverage; they do not establish terrain approximation quality. GPU timings, practical maximum render distance and smooth LOD morphing are separate concerns, and no frame-rate guarantee follows from these tests.
