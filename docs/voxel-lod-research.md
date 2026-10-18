# Blocky voxel LOD, visibility and streaming research

Research and implementation checkpoint: 18 October 2026. This document concerns blocky, opaque terrain. It distinguishes CPU foundation code from renderer features that still need integration and GPU validation.

## Decisions in this milestone

The world model offers uniform or dense chunk payloads, and the streamed procedural demo keeps a sparse edit overlay. LOD selection uses a finite forest of equal-size octree roots. These are separate structures: editing a voxel does not require rebuilding a monolithic world octree. A `ChunkKey` identifies a resolution and a chunk-grid address; one chunk contains 32 cubed samples at its selected resolution.

`LodSelector` has two policies:

- The safe default requests a uniform level across the root forest. If the requested cover exceeds its explicit chunk budget, it selects a coarser **complete** cover and reports `BudgetLimited`. It never truncates a partition to meet a count budget.
- Experimental mixed selection prioritizes projected voxel scale divided by distance to the chunk AABB. A split includes the necessary coarse-neighbour split closure before checking the budget. The resulting target leaves are face-balanced to 2:1. Selection does not depend on view direction, so rotating the camera does not itself change the terrain partition.

This screen error is a scale heuristic, not a measured mesh simplification error bound. The generator's coarse point-sampling policy can erase thin features and is not conservative occupancy reduction. Hysteresis, incremental root movement, edit-aware coarse aggregation, and measured error metadata remain future work.

`ResidentLodCover` separates the desired partition from what has actually reached GPU residency. In mixed mode, it retains an ancestor until every desired child subtree supplies complete coverage. In safe mode, a uniform level changes globally only when the complete replacement level is resident. An accepted empty mesh counts as resident. A cold start with no covering mesh reports uncovered roots explicitly.

This publication rule prevents **temporal loading holes**. It does not create spatial seam geometry. Resident fallbacks can temporarily put levels differing by more than one next to each other; `LodBoundaryPlan` reports those boundaries and exposes whether each is 2:1. The mixed mode must remain experimental until the renderer can render those boundaries correctly.

The integration must retain the current render cover and required ancestors until replacements finish uploading. Retaining only the finest desired set would remove the very fallback meshes that keep coverage intact. CPU generation completion and GPU residency are different states.

## Spatial transitions for blocky terrain

[Mikola Lysenko's blocky voxel LOD article](https://0fps.net/2018/03/03/a-level-of-detail-method-for-blocky-voxels/) applies POP buffers to integer-aligned quad meshes. It orders primitives by the level at which quantization collapses them, allowing draw ranges to omit finer primitives. It also describes continuous vertex morphing and stable rounding seeded by the maximum level of a chunk and its neighbours. This is a promising next experiment because it operates on the existing blocky surface mesh.

To implement that experiment correctly here, quantization must use a common global power-of-two lattice, including at negative coordinates. Camera-relative conversion must happen after the integer lattice operations. Neighbour level metadata, primitive ordering for greedy rectangles, material appearance, and quantization at chunk boundaries need explicit tests. Merely scaling independently sampled coarse chunks is not the POP method and does not inherit its seam properties.

[Godot Voxel Tools' blocky skirt source](https://raw.githubusercontent.com/Zylann/godot_voxel/master/meshers/blocky/blocky_lod_skirts.h) documents that its extra border geometry hides many cracks but does not always close them. It also identifies problems around water surfaces and mismatched levels. Skirts are a useful optional visual workaround for some opaque terrain, but they should not be described as a general watertight solution.

[Eric Lengyel's Transvoxel description](https://transvoxel.org/) targets transition triangulation between resolutions of a voxel density field. It is appropriate if a future terrain backend uses smooth isosurfaces. It is not a drop-in transition mesh for the current material-valued cube faces.

## Precision and frustum culling

`WorldPosition` stores a signed 64-bit integer cell plus fractional offsets. It subtracts integer positions through `Int128` before converting the small difference to floating point. This avoids losing individual voxels when the global coordinate exceeds the exact integer range of `double`. A relative floating-point render origin does not, by itself, solve precision if global positions were already rounded before subtraction.

`Frustum.FromViewProjection` uses System.Numerics row-vector matrix conventions and Vulkan's `0 <= z <= w` clip volume. Its extracted planes work for conventional and reverse-Z projections. A degenerate infinite far plane does not introduce a false finite render distance. `RelativeAabb.FromChunk` rounds bounds outwards; support-point tests preserve touching geometry.

The GPU culling implementation must use the same matrix convention, camera origin and conservative bounds. Shader arithmetic at a silhouette can otherwise reject boxes that the CPU reference preserves. Visibility never determines whether a chunk exists or should remain available for LOD coverage; it only determines submission.

[NVIDIA's depth precision analysis](https://developer.nvidia.com/blog/visualizing-depth-precision/) explains why a floating-point reverse-Z depth buffer improves distance precision. A reverse-Z implementation must consistently pair its projection, a depth clear of zero, a greater-depth comparison, and the occlusion reduction direction. A reversed projection combined with ordinary clear/comparison settings is incorrect.

## Occlusion foundation and next GPU pass

`ReversedDepthPyramid` is a **CPU correctness reference**, not a GPU occlusion pass. It builds a minimum-depth pyramid from current opaque reverse-Z depth. Non-power-of-two dimensions use complete ceil-halving reduction. Queries inspect every overlapped texel at a selected mip; unaligned rectangles do not rely on a single centre sample. Clear-depth holes, near-plane crossing bounds, unreliable offscreen bounds, and equal-depth comparisons remain conservatively visible.

[Niagara's actual culling shader](https://raw.githubusercontent.com/zeux/niagara/master/src/shaders/drawcull.comp.glsl) demonstrates the early/late visibility split, reverse-Z nearest-depth comparison, and indirect command generation. Its [depth reduction shader](https://raw.githubusercontent.com/zeux/niagara/master/src/shaders/depthreduce.comp.glsl) uses minimum reduction. This is the relevant reference for the next renderer milestone: draw previously visible terrain, build current-frame Hi-Z, then test newly visible candidates and draw the late survivors. Reusing previous-frame depth alone is unsafe during camera movement or teleportation.

The next pass also needs depth image storage and sampling usages, mip view allocation, per-mip image barriers, conservative projected AABB bounds, a near-plane fallback, late indirect command storage, and visibility history invalidation on projection or viewport changes. Those pieces are not implied by the presence of the CPU pyramid.

## Work scheduling

BRUTAL's inspected `JobScheduler` is a batch scheduler: it refuses `Queue` while jobs run, and its `Wait` busy-blocks. It can support bounded batch adapters, but invoking `Wait` in the render loop would violate streaming latency goals. The engine's separate bounded producer/consumer pipeline keeps request ownership, cancellation tickets, completed work and GPU upload publication explicit.

The useful general pattern from [Lysenko's meshing article](https://0fps.net/2012/06/30/meshing-in-a-minecraft-game/) is to keep generation latency and final render cost separate: publish a useful result promptly and permit later refinement. Here, versioned immutable snapshots and upload-complete replacement provide the necessary ownership boundary for future fast/quality mesher variants.

Sparse voxel ray traversal and mesh shaders remain optional backends. [Arseny Kapoulkine's meshlet measurements](https://zeux.io/2023/01/16/meshlet-size-tradeoffs/) show that larger clusters can reduce culling effectiveness. The present compact-quad indirect draw approach should be profiled before adding mesh shader complexity or assuming that meshlets improve this workload.

## Verification

The LOD/visibility suite currently contains ten cases. It checks negative-coordinate partition coverage against an independent dense occupancy oracle, several balanced selection budgets, globally atomic uniform replacement, multilevel resident fallback, mixed-face metadata, integer subtraction at `2^56`, Vulkan clip planes, infinite reverse-Z, odd-sized depth pyramids and clear-depth holes. Two thousand randomized occlusion rectangles are checked against a full-resolution pixel oracle to rule out false occlusion rejection.

These CPU checks do not establish Vulkan runtime correctness, frame rates, practical maximum render distance, or seamless mixed-resolution rendering. Runtime validation, rendered image inspection, camera motion stress tests and GPU timings are required separately.
