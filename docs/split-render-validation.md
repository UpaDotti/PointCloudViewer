# Split Rendering Validation

Date: 2026-07-04

## Summary

PointCloudViewer uses a split rendering path instead of the previous stage-based approach.
The current implementation renders one slice per frame into persistent slice color/depth buffers, then composites all valid slices by reprojection and depth testing.

User visual check on 2026-07-04: the split rendering output is drawing correctly.

## Implementation Under Test

- `Assets/PointCloudViewer/Script/Opc/SplitRender/OpcSplitRenderFeature.cs`
  - `OpcSplitRenderFeature`: enables the split render pass from URP.
  - `OpcSplitRenderPass`: renders one slice per frame and composites valid slices.
  - `OpcSplitRenderBridge`: receives prepared draw data from `OpcLoader`.
- `Assets/PointCloudViewer/Shader/PointCloud_CircleSplit.shader`
  - Draws points for the current slice using slice offset and stride.
- `Assets/PointCloudViewer/Shader/PointCloudSplitComposite.shader`
  - Reprojects each slice from its captured VP matrix to the current camera VP matrix.
  - Writes depth so the nearest slice/background relationship can be evaluated by depth testing.
- `Assets/PointCloudViewer/Script/Opc/OpcLoader.cs`
  - Provides `Direct` / `Split` render mode switching for benchmark comparison.

## Validation Conditions

| Item | Result | Evidence |
| --- | --- | --- |
| Split mode selected | Confirmed | `OpcLoader.Mode.Split avg = 1.00` |
| Direct mode selected for comparison | Confirmed | `OpcLoader.Mode.Direct avg = 1.00` in direct run |
| Same camera route | Confirmed by benchmark setup | Both runs use `CameraOrbitMover` and `BenchmarkSession` |
| Comparable selected nodes | Confirmed | Split avg 799.98, Direct avg 799.98 |
| Comparable command count | Confirmed | Split avg 669.53, Direct avg 671.17 |
| Comparable point count | Confirmed | Split avg 5,215,645, Direct avg 5,207,191 |
| Visual correctness | Confirmed by user | User stated: correct drawing is visible |

## Benchmark Result

### Split Rendering

| Metric | Average | p50 | p95 | Max |
| --- | ---: | ---: | ---: | ---: |
| Frame ms | 18.23 | 18.13 | 20.67 | 27.72 |
| Draw commands | 669.53 | 709.00 | 776.00 | 789.00 |
| Draw points | 5,215,645 | 5,229,510 | 5,531,473 | 5,673,077 |
| Slice points per frame | 1,303,911 | 1,307,369 | 1,382,625 | 1,418,164 |
| Split count | 4.00 | 4.00 | 4.00 | 4.00 |
| Composite draw calls | 4.00 | 4.00 | 4.00 | 4.00 |
| Slice draw calls | 669.53 | 709.00 | 776.00 | 789.00 |
| GetVisibleNodes ms | 0.29 | 0.28 | 0.37 | 0.48 |
| SyncNodeStreaming ms | 0.42 | 0.39 | 0.67 | 2.00 |
| DrawPointCloud CPU marker ms | 0.13 | 0.12 | 0.17 | 0.23 |

Approximate FPS from average frame time: 54.9 FPS.

### Direct Rendering

| Metric | Average | p50 | p95 | Max |
| --- | ---: | ---: | ---: | ---: |
| Frame ms | 31.38 | 31.42 | 34.29 | 79.32 |
| Draw commands | 671.17 | 709.00 | 774.00 | 789.00 |
| Draw points | 5,207,191 | 5,217,677 | 5,530,117 | 5,659,720 |
| Direct draw calls | 1.00 | 1.00 | 1.00 | 1.00 |
| GetVisibleNodes ms | 0.26 | 0.26 | 0.33 | 0.90 |
| SyncNodeStreaming ms | 0.31 | 0.29 | 0.50 | 1.22 |
| DrawPointCloud CPU marker ms | 0.11 | 0.11 | 0.15 | 0.23 |

Approximate FPS from average frame time: 31.9 FPS.

## Interpretation

The two runs are comparable because selected nodes, command count, and total draw points are nearly the same.
Under these conditions, split rendering reduces average frame time from 31.38 ms to 18.23 ms.
That is about 1.72x faster by average frame time.

The CPU marker `OpcLoader.DrawPointCloud` does not include the full GPU cost of the rendering work.
Use `Frame` as the primary performance comparison until GPU timestamp or profiler capture is added.

With `SplitCount = 4`, the split path draws about one quarter of the points per frame:

- Total prepared points: about 5.22 million
- Slice points per frame: about 1.30 million

The extra composite cost of four fullscreen passes is lower than the saved point rendering cost in this test.

## Known Limits

- This record relies on user visual confirmation for correctness; no screenshot is committed yet.
- GPU-only timings for direct draw, slice draw, and composite passes are not measured separately.
- `Frame` includes all frame work, not only point cloud rendering.
- Results are for one camera route, one dataset, one resolution, and `SplitCount = 4`.
- More split counts such as 2 and 8 are not yet compared.

## Next Checks

- Capture screenshots for split rendering with background geometry.
- Compare `SplitCount = 2`, `4`, and `8`.
- Add GPU profiler evidence if the result needs to be explained beyond frame time.
