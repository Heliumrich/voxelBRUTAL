# Vulkan chunk renderer

The renderer is deliberately one independently replaceable backend. It consumes immutable `ChunkMesh` publications and never reads the mutable world or performs generation. All calls run on the GLFW thread.

```csharp
using var renderer = new VulkanChunkRenderer(window, new() { EnableValidation = true });
renderer.QueueUpload(mesh);
renderer.Draw(new RenderCamera(cameraPosition, cameraRelativeReverseZMatrix));
```

`SetActiveChunks` restricts drawing to a selected resident cover while allowing backing meshes to remain allocated. `IsResident` becomes true after the mesh's copy has been recorded; graphics use it later in the same ordered command buffer with a transfer-to-vertex barrier. `Remove` removes a draw immediately and retires its allocation after the last submitted use completes. A newer revision replaces an older mesh without mutating data already read by an in-flight frame.

## Implemented path

* Vulkan 1.3, dynamic rendering and synchronization2; graphics, compute and presentation share a queue.
* One shared device-local packed-quad SSBO, 8 bytes per quad. Chunk meshes suballocate a best-fit arena; adjacent free ranges coalesce. A few coarse native Vulkan allocations avoid per-chunk allocation overhead. VMA is an appropriate future replacement inside `GpuBuffer`, without changing the world or mesher.
* Two CPU frame slots, each with its own command pool, signaled fence, acquire semaphore, mapped coherent staging and chunk buffers, device-local indirect buffer and depth attachment. A frame's mapped data is written only after its fence completes. Presentation wait semaphores are indexed by swapchain image, as required by WSI lifetime rules.
* Camera-relative origins calculated by `WorldPosition.RelativeOrigin`: integer subtraction occurs before conversion to floating point, including origins beyond 2^53. Infinite reversed Z uses D32 float, clear 0 and `GreaterOrEqual` comparison.
* A compute invocation per selected chunk performs conservative six-plane AABB rejection and writes one `VkDrawIndirectCommand`. A synchronization2 barrier exposes shader writes to the indirect command stage. One `vkCmdDrawIndirect` consumes the array; culled chunks have zero instances. This deliberately avoids readback and a draw-count buffer.
* The vertex shader pulls one packed quad for each six generated vertices. The indirect `firstInstance` selects the chunk record, avoiding per-chunk bindings and push constants. Face winding follows the mesher's cyclic axes.
* Bounded staging uploads per frame, a separate pending-publication byte/count limit, explicit capacity checks, and backpressure when the GPU arena is full. `TryQueueUpload` returns false when the pending budget is full so the caller can retain and retry the publication. Old quad ranges cannot be reused until their last GPU serial completes. A resize rebuilds the swapchain and per-frame depth images after device idle.
* Optional validation layer and debug-utils callback; `ValidationErrorCount` exposes errors to the smoke test.

## Deliberate boundaries

There is no GPU Hi-Z occlusion rejection in this milestone. GPU frustum rejection and normal hardware depth rejection are active. A current-frame depth prepass, minimum-reduced reversed-Z pyramid, and second visibility pass belong together: blindly reusing previous-frame depth after a camera move or voxel edit can hide newly exposed terrain. The CPU `ReversedDepthPyramid` is a correctness reference, not a GPU implementation or a performance claim.

The demo draws one spatial voxel resolution per view. The renderer can draw LOD-tagged meshes and atomic covers, but it does not manufacture transition geometry, solve voxel sampling across LOD boundaries, or guarantee mixed-resolution seams. Those belong to the core mesher and LOD contract.

The arena has a fixed capacity and no compaction. Residency management should retain spare capacity for replacement meshes: replacing a live mesh requires a fresh range until earlier frames finish. A completely saturated arena will defer uploads until eviction releases space. A finite FIFO upload budget may need priority updates when building a production streamer.

There is no bindless material texture system, AO/light propagation, mesh shader dependency, asynchronous transfer queue, shader hot reload, GPU timing UI or terrain performance promise. Those can be added after measured bottlenecks justify them. Flat material colors and directional light keep this foundation easy to inspect.

## Primary references

* [Khronos GPU rendering and multi-draw indirect sample](https://docs.vulkan.org/samples/latest/samples/performance/multi_draw_indirect/README.html): compute-written draw parameters and instance-count visibility masks.
* [Khronos synchronization examples](https://docs.vulkan.org/guide/latest/synchronization_examples.html): transfer, compute, graphics and WSI dependencies.
* [Khronos swapchain semaphore reuse](https://docs.vulkan.org/guide/latest/swapchain_semaphore_reuse.html): presentation wait semaphores belong to acquired images.
* [Khronos vkCmdDrawIndirect reference](https://docs.vulkan.org/refpages/latest/refpages/source/vkCmdDrawIndirect.html): feature and buffer requirements.
* [BRUTAL NuGet publisher](https://www.nuget.org/profiles/Ahwoo): current package versions and .NET 10 target. The fork currently omits upstream build project files, so the backend references the pinned published BRUTAL bindings while the fork remains available for inspection and future source integration.

## Verification

The development machine's RTX 4080 completed a 480-frame smoke with 3,750 resident terrain chunks and 288,846 packed quads using `VK_LAYER_VALIDATE_SYNC=1`, with zero validation errors. This validates the basic Vulkan path and frame lifetimes; the elapsed time includes generation, fixed smoke sleeps and synchronization validation, so it is not a rendering benchmark. Additional moving-camera, edit and resize stress runs are recorded in the root foundation documentation.

Several BRUTAL limit fields use signed `int` to represent Vulkan `uint32_t`. This backend explicitly reinterprets storage range, indirect count and compute dispatch limits as `uint`; drivers commonly report `UINT32_MAX`, which otherwise looks like `-1` and incorrectly rejects a capable GPU.
