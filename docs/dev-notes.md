# 開発メモ（確認・計測の手順）

C#のコンパイル確認、エディタでの確認や計測、ビルドをする前に読む。

## 確認と計測

- C#のコンパイル確認は `dotnet build Assembly-CSharp.csproj`。新規.csで「型が見つからない」と出たら、Unityがcsprojを作り直してエントリが消えている。csprojのCompileエントリを確認し、最後はUnityで Assets > Refresh
- エディタ操作は `unity command --project-path .`（com.unity.pipeline）。`--project-path` は必ず付ける（別プロジェクトのエディタも返るため）
- エディタが裏にあると最初の `eval_file` がタイムアウトする。`--timeout 120`（秒）で再実行する
- `eval_file` では using が効かず、URPの型も直接参照できない。完全修飾名とリフレクションで触る
- `capture_game_view` の `save_path` はAssets基準。撮影後に `AssetDatabase.DeleteAsset` で消す
- ビルド計測は `Tools/Run-Benchmark.ps1`。途中の画面を撮って点群が写っていることを確かめる（起動後15秒以上待ってから撮る）
- 自作シェーダのタグは `"RenderPipeline"="UniversalPipeline"` にする。違う値（例: `UniversalRenderPipeline`）だと、エディタでは描けるのに、ビルドでURPがそのシェーダの中身を全部捨てて何も描かれない。ビルドログの `After scriptable stripping: 0` が目印
- エディタでビルドすると、URPアセットやProjectSettingsが書き換わることがある。コミット前に差分を確認する
- パスごとのGPU時間は `ProfilerRecorder` + `GpuRecorder` で取る。Render Graphのパスには自前の `ProfilingSampler` を渡す。サンプル数を必ず確認する
- GPUのサンプルは再コンパイル直後の1回目のPlayでしか届かないことがある。`ProfilingSampler.gpuElapsedTime` と `Profiling.Recorder` は値が止まるので使わない
- GPU描画の不具合は推測で直さず、エディタ内の診断スクリプトで実測する。出力にversionマーカーを入れ、調査後に削除する
