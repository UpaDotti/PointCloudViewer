# PointCloudViewer

LAS由来の大量点群（数百万〜1億点）をUnityで表示する検証用Viewer。Unity 6000.3.9f1 / URP 17.3.0（Render Graph有効）。

## まず読むもの

- 仕組みの正本: `docs/point-cloud-processing-flow.md`
- 最新の計測値の一覧: `README.md`
- 何が重いか（性能の前提）: `docs/profiler-frame-analysis.md`
- 次にやること・人の判断待ち: `docs/status.md`（次の作業はここだけに書く）
- 各方式の実装と計測: `docs/split-render-validation.md`（Split）、`docs/gather-prototype.md`（Gather）
- 人向けの全体像: <private URL> （正本から生成する。ページを直接編集しない）

## 開発ループ（性能改善・研究）

1. 仮説: 予測と判定基準を実装前に書く。基準は速度と見た目の両方
2. 実装: 新方式は比較用の描画モードとして足し、既存モードを壊さない
3. 確認: 画面に点群が正しく描かれているかを見る。通らなければ計測しない
4. 計測: 条件（エディタ/ビルド、GPU、解像度、データ、カメラ経路）を必ず添える
5. 判断・記録: 採用/不採用/保留は人が決める。理由とコミットを残す。結果は各方式の文書へ、次の作業は `docs/status.md` へ書く。実測の結果をメモリにだけ残さない

仕組みが変わったとき（処理の流れ、クラスの役割、データの形、描画モードの増減、設計判断）は、正本を直してから全体像のページを作り直す。パラメータ調整や計測値だけの変化では更新しない。

## 確認と計測

- C#のコンパイル確認は `dotnet build Assembly-CSharp.csproj`。新規.csで「型が見つからない」と出たら、Unityがcsprojを作り直してエントリが消えている。csprojのCompileエントリを確認し、最後はUnityで Assets > Refresh
- エディタ操作は `unity command --project-path .`（com.unity.pipeline）。`--project-path` は必ず付ける（別プロジェクトのエディタも返るため）
- エディタが裏にあると最初の `eval_file` がタイムアウトする。`--timeout 120`（秒）で再実行する
- `eval_file` では using が効かず、URPの型も直接参照できない。完全修飾名とリフレクションで触る
- `capture_game_view` の `save_path` はAssets基準。撮影後に `AssetDatabase.DeleteAsset` で消す
- ビルド計測は `Tools/Run-Benchmark.ps1`。2026-11-01時点でビルド版は全モードで点群が描かれない（未解決）。ビルドの数値は、画面で点群を確認するまで無効
- パスごとのGPU時間は `ProfilerRecorder` + `GpuRecorder` で取る。Render Graphのパスには自前の `ProfilingSampler` を渡す。サンプル数を必ず確認する
- GPUのサンプルは再コンパイル直後の1回目のPlayでしか届かないことがある。`ProfilingSampler.gpuElapsedTime` と `Profiling.Recorder` は値が止まるので使わない
- GPU描画の不具合は推測で直さず、エディタ内の診断スクリプトで実測する。出力にversionマーカーを入れ、調査後に削除する

## Unity / URP の罠

- Render Graph有効の判定は `UniversalRenderPipelineGlobalSettings.asset` の `m_EnableRenderCompatibilityMode: 0`（`m_EnableRenderGraph` は当てにならない）。自前パスは `RecordRenderGraph` で書く
- `Graphics.RenderPrimitives*` 経路では `UNITY_MATRIX_P` が0になる。投影由来の係数はC#で計算して渡す（`_WorldToPixel`）
- `CommandBuffer.SetViewProjectionMatrices` には未変換の `camera.projectionMatrix` を渡す。自前で合成するクリップ行列だけ `GL.GetGPUProjectionMatrix(proj, true)` を使う
- 深度は `ZTest LEqual`・クリア1.0のまま書く（反転ZはUnityが換算する）
- CommandBuffer系にはマルチドローが無い。ノードごとに `DrawProceduralIndirect` を発行し、`_CommandId` でノードを引く
- Core.hlslは `sampler_LinearClamp` / `sampler_PointClamp` を定義済み。自前で宣言するとredefinitionエラー
- Render Graph記録中に `SetGlobal*` / `SetViewProjectionMatrices` を使うなら `AllowGlobalStateModification(true)`
- 画面の一部だけ書くパスに `AccessFlags.WriteAll` を使わない（前フレームの内容が残る）。全画素を書くパスだけ `WriteAll`
- エディタでビルドすると、URPアセットやProjectSettingsが書き換わることがある。コミット前に差分を確認する

## OPC形式

- 形式を拡張しても `version_major/minor/patch` は変えない。コメントや文書に「v1.1で追加」などのバージョン表記を書かない
- 保存するノードのboundsは点の実範囲（tight bounds）。点サイズの計算に使うため
- 変換スクリプトは別リポジトリ PointCloudConverter にある

## Git

- リモート（origin）はmainだけ。新しいブランチをプッシュしない。プッシュはmainへ送り、実行前に確認する
