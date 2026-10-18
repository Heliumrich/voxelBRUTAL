# Vulkan chunk renderer

The renderer is deliberately one independently replaceable backend. It consumes immutable `ChunkMesh` publications and never reads the mutable world or performs generation. All calls run on the GLFW thread.

```csharp
using var renderer = new VulkanChunkRenderer(window, new() { EnableValidation = true });
renderer.QueueUpload(mesh);
renderer.Draw(new RenderCamera(cameraPosition, cameraRelativeReverseZMatrix));
```

`SetActiveMeshes` restricts drawing to an atomic set of `ChunkMeshId` values (key, data revision and topology stamp), allowing old and candidate versions of the same spatial chunk to remain resident together. The core coordinator publishes the candidate set only after every exact mesh version becomes resident. `RetainMeshes` evicts unneeded versions; their ranges retire after their last GPU use completes. `IsResident(id)` becomes true after the copy has been recorded; graphics use it later in the same ordered command buffer with a transfer-to-vertex barrier. Legacy key-only mode selects the latest publication per key.

## Implemented path

* Vulkan 1.3, dynamic rendering and synchronization2; graphics, compute and presentation share a queue. Safe presentation retirement additionally requires enabled KHR/EXT swapchain maintenance1 and its presentation fences. GPUs lacking that feature are rejected with an explicit capability error.
* One shared device-local packed-quad SSBO, 8 bytes per quad. Chunk meshes suballocate a best-fit arena; adjacent free ranges coalesce. A few coarse native Vulkan allocations avoid per-chunk allocation overhead. VMA is an appropriate future replacement inside `GpuBuffer`, without changing the world or mesher.
* Two CPU frame slots, each with its own command pool, signaled fence, acquire semaphore, mapped coherent staging and chunk buffers, device-local indirect buffer and depth attachment. A frame's mapped data is written only after its fence completes. Presentation wait semaphores are indexed by swapchain image, as required by WSI lifetime rules.
* Camera-relative origins calculated by `WorldPosition.RelativeOrigin`: integer subtraction occurs before conversion to floating point, including origins beyond 2^53. Infinite reversed Z uses D32 float, clear 0 and `GreaterOrEqual` comparison.
* A compute invocation per selected chunk performs conservative six-plane AABB rejection and writes one `VkDrawIndirectCommand`. A synchronization2 barrier exposes shader writes to the indirect command stage. Each geometry pass uses one `vkCmdDrawIndirect`; culled chunks have zero instances. No CPU draw-count readback or indirect count buffer is necessary.
* The vertex shader pulls one packed quad for each six generated vertices, including the per-quad half-grid representation used at mixed-LOD boundaries. The indirect `firstInstance` selects the chunk record, avoiding per-chunk bindings and push constants. Face winding follows the mesher's cyclic axes.
* Bounded staging uploads per frame, a separate pending-publication byte/count limit, explicit capacity checks, and backpressure when the GPU arena is full. `TryQueueUpload` returns false when the pending budget is full so the caller can retain and retry the publication. Old quad ranges cannot be reused until their last GPU serial completes. A resize rebuilds the swapchain and per-frame depth images after device idle.
* Optional validation layer and debug-utils callback; `ValidationErrorCount` exposes errors to the smoke test.

## Presentation, UI and timings

The backend requests Mailbox by default and enumerates the actual surface's non-shared present modes. `SetPresentMode` applies at the next frame boundary. An unavailable requested mode falls back to FIFO with `PresentModeFallbackReason`. Compatible modes use `VK_KHR_swapchain_maintenance1` or its EXT predecessor for per-present switching; incompatible switches rebuild the swapchain after GPU and presentation resources are safe to retire. Compatibility queries account for every declared mode's image-count limits.

Maintenance1 is negotiated only when both instance surface maintenance and device features are supported, and is required during device selection. Each present supplies a fence; those presentation fences, rather than render-submit fences or an assumption about device idle, protect swapchain and semaphore destruction. `VK_KHR_present_id` and `VK_KHR_present_wait` are enabled together when available for optional pacing only. A present wait can return out-of-date before presentation resources retire, so it never substitutes for a presentation fence. There is no unextended device-idle fallback: Vulkan does not formally guarantee completion of legacy presentation-engine resources through device idle alone. `LimitPresentationQueue` optionally waits for an older present with a finite 16 ms timeout; call `PrepareFrame` before polling input so frame-capacity waits precede control sampling. The normal two frame contexts remain bounded even when the optional present-wait pair is unavailable because maintenance1 supplies the required retirement path.

These Vulkan choices control frame queueing, not the Windows compositor. Borderless dimensions can allow efficient driver/OS presentation, but Vulkan cannot guarantee Independent Flip, bypass DWM, or promise zero display latency. The demo's CPU frame pacer and window mode belong to the application layer.

`GpuFrameMs` is the elapsed GPU timestamp interval covering the renderer, including its UI pass. Query pools have two timestamp slots per CPU frame, reset only when that frame is safe to reuse. Results are read after the existing fence without `WAIT_BIT`, checked for availability, masked to the queue family's valid timestamp bits, and converted using the device's timestamp period. This duration is distinct from adapter utilization. The demo's utilization monitor uses `AdapterLuid` and canonical `DeviceUuid` to match the actual selected GPU.

BRUTAL's official ImGui Vulkan backend requires `DeviceEx` and a traditional render pass. `OverlayContext` exposes a persistent load-only color pass and a two-slot main-viewport buffer-ring contract. `OverlayRender` records its commands after the dynamic-rendered world and before frame readback/presentation. Framebuffers rebuild with the swapchain; the ImGui context and backend remain owned by the application and are disposed before the renderer. Core terrain rendering and compute passes still use Vulkan 1.3 dynamic rendering.

## Current-frame Hi-Z occlusion

`RendererOptions.EnableOcclusion` defaults to true; `OcclusionEnabled` changes it at runtime. When enabled:

1. Early compute draws meshes from a completed visibility set, after current-camera frustum rejection. They render using the current active geometry and camera into freshly cleared color and depth attachments.
2. Compute copies every D32 depth pixel exactly into an R32 float image padded up to power-of-two dimensions. Padding remains uncovered far depth (0). Each subsequent mip explicitly reduces four source texels with `min`. Odd viewport edges are never dropped or resampled.
3. Late compute tests all current candidate AABBs against that current-frame pyramid. Eight projected corners bound screen coverage and nearest reverse-Z depth. Boxes crossing the near plane/eye, invalid projection values, and ambiguous comparisons stay visible. Expanded world bounds, a full pixel border and a relative depth tolerance make rejection conservative. The selected mip covers the full query rectangle with up to four exact texel fetches; uncovered cells prevent rejection.
4. The late MDI draws newly visible meshes that were absent from the early seed set, loading the early color/depth attachments. All candidates, including early seeds, update the next visibility set, so occluded early draws can fall out of later seed sets.

The history contains only identities, not old depth. Its normal two-frame readback lag can increase geometry work but cannot hide newly revealed terrain: camera motion, mesh replacement, teleportation and occluder removal use freshly rendered current-camera depth. Replaced or topologically changed versions receive independent history identities.

GPU-written per-mesh visibility and counters are read only after a frame fence. `Statistics` reports early draws, late draws, occluded candidates, frustum candidates, actual submitted quads and the readback serial. An occluded candidate may have been an early seed this frame; savings converge as the completed seed set changes. `WaitForLastFrame`, `LastVisibleMeshes`, and optional `EnableColorReadback`/`LastColorFrame` provide actual GPU validation data. Color readback is test-only BGRA8 and disabled during normal play. Readback-enabled swapchains disable presentation clipping so hidden or obscured window pixels remain defined for validation.

## Deliberate boundaries

The renderer handles versioned meshes and the packed half-grid boundary representation. Coverage selection, neighbor sampling and seamless topology construction belong to the core LOD mesher/coordinator.

The arena has a fixed capacity and no compaction. Residency management must retain capacity for old and candidate mesh versions during atomic cover publication. A completely saturated arena defers uploads until eviction releases space. A finite FIFO upload budget may need priority updates when building a production streamer.

There is no bindless material texture system, AO/light propagation, mesh shader dependency, asynchronous transfer queue, shader hot reload or terrain performance promise. Those can be added after measured bottlenecks justify them. Flat material colors and directional light keep this foundation easy to inspect.

## Primary references

* [Khronos GPU rendering and multi-draw indirect sample](https://docs.vulkan.org/samples/latest/samples/performance/multi_draw_indirect/README.html): compute-written draw parameters and instance-count visibility masks.
* [Khronos synchronization examples](https://docs.vulkan.org/guide/latest/synchronization_examples.html): transfer, compute, graphics and WSI dependencies.
* [Khronos swapchain semaphore reuse](https://docs.vulkan.org/guide/latest/swapchain_semaphore_reuse.html): presentation wait semaphores belong to acquired images.
* [Khronos swapchain maintenance proposal](https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_swapchain_maintenance1.html): present fences, declared compatible modes and dynamic present-mode switching.
* [Khronos swapchain recreation sample](https://docs.vulkan.org/samples/latest/samples/api/swapchain_recreation/README.html): presentation-resource retirement and why render-idle alone is insufficient with maintenance1.
* [Microsoft flip-model presentation](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model): OS/driver-dependent composition and Independent Flip behavior. This describes Windows presentation behavior, not a portable Vulkan guarantee.
* [Khronos vkCmdDrawIndirect reference](https://docs.vulkan.org/refpages/latest/refpages/source/vkCmdDrawIndirect.html): feature and buffer requirements.
* [Niagara draw culling](https://github.com/zeux/niagara/blob/master/src/shaders/drawcull.comp.glsl): the early/late visibility architecture. This renderer independently uses AABB projection, exact pixel copying and explicit reduction rather than Niagara's sphere projection and reduction sampler.
* [Niagara depth-pyramid conservativeness discussion](https://github.com/zeux/niagara/discussions/50): why a resampled odd-sized base level can lose conservativeness; this backend avoids that base resampling entirely.
* [BRUTAL NuGet publisher](https://www.nuget.org/profiles/Ahwoo): current package versions and .NET 10 target. The fork currently omits upstream build project files, so the backend references the pinned published BRUTAL bindings while the fork remains available for inspection and future source integration.

## Verification

The development machine's RTX 4080 completed a 480-frame smoke with 3,750 resident terrain chunks and 288,846 packed quads using `VK_LAYER_VALIDATE_SYNC=1`, with zero validation errors. This validates the basic Vulkan path and frame lifetimes; the elapsed time includes generation, fixed smoke sleeps and synchronization validation, so it is not a rendering benchmark. Additional moving-camera, edit and resize stress runs are recorded in the root foundation documentation.

The Hi-Z path additionally completed a 480-frame moving/edit/resize stress with synchronization validation, zero Vulkan errors and zero streaming failures. The final readback reported 339 frustum candidates, 176 early draws, 19 late draws and 145 occluded candidates; 213,788 quads were submitted from 1,091,793 resident quads. These are functional counters from one changing scene, not a speedup benchmark.

The actual GPU occlusion harness passed 11 cases with synchronization validation and zero errors. Every compared BGRA8 byte exactly matched the all-frustum reference: settled hidden geometry, first-frame occluder removal/restoration, rotation, teleportation, near-plane crossing, eye-inside bounds, version replacement, 997×613 resize, half-grid carry bits and fractional half-grid coordinates. Results and frame artifacts are written to `.artifacts/occlusion-validation/` by `--validate-occlusion`.

Several BRUTAL limit fields use signed `int` to represent Vulkan `uint32_t`. This backend explicitly reinterprets storage range, indirect count and compute dispatch limits as `uint`; drivers commonly report `UINT32_MAX`, which otherwise looks like `-1` and incorrectly rejects a capable GPU.
