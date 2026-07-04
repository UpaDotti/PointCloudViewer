# PointCloudViewer

Unity point cloud viewer for experimenting with large point cloud rendering, streaming, LOD selection, and split rendering.

## Current Focus

The current rendering experiment uses split rendering instead of the previous stage-based approach.
It renders one slice of the point cloud per frame into persistent slice buffers, then composites all valid slices by reprojection and depth testing.

See [Split Rendering Validation](docs/split-render-validation.md) for the current benchmark and correctness notes.

## Split Rendering Summary

Main files:

- `Assets/PointCloudViewer/Script/Opc/OpcLoader.cs`
  - Loads OPC data, selects visible nodes, streams point data, prepares draw commands, and switches between `Direct` and `Split` render modes for benchmarking.
- `Assets/PointCloudViewer/Script/Opc/MultiDrawRenderer.cs`
  - Prepares draw commands and exposes prepared point counts for benchmark logs.
- `Assets/PointCloudViewer/Script/Opc/SplitRender/OpcSplitRenderFeature.cs`
  - URP renderer feature that renders one slice per frame and composites valid slices.
- `Assets/PointCloudViewer/Shader/PointCloud_CircleSplit.shader`
  - Point shader for drawing only the current slice.
- `Assets/PointCloudViewer/Shader/PointCloudSplitComposite.shader`
  - Composite shader that reprojects slice color/depth into the current camera view.

## Latest Benchmark

Date: 2026-07-04

| Mode | Avg frame time | Approx FPS | Avg points | Notes |
| --- | ---: | ---: | ---: | --- |
| Split, count 4 | 18.23 ms | 54.9 FPS | 5,215,645 | Draws about 1,303,911 slice points per frame and composites 4 slices |
| Direct | 31.38 ms | 31.9 FPS | 5,207,191 | Single direct indirect draw path |

Result: split rendering is about 1.72x faster than direct rendering in this benchmark route.

## Current Limitations

- Correctness is currently confirmed by visual inspection, but screenshots are not committed yet.
- GPU-only timing for direct draw, slice draw, and composite passes is not recorded separately.
- The benchmark covers one dataset, one route, and `SplitCount = 4`.
