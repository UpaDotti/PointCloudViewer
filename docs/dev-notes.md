# 開発メモ（確認・計測の手順）

C#のコンパイル確認、エディタでの確認や計測、ビルドをする前に読む。

## 確認と計測

- C#のコンパイル確認は `dotnet build Assembly-CSharp.csproj`。新規.csで「型が見つからない」と出たら、Unityがcsprojを作り直してエントリが消えている。csprojのCompileエントリを確認し、最後はUnityで Assets > Refresh
- エディタ操作は `unity command --project-path .`（com.unity.pipeline）。`--project-path` は必ず付ける（別プロジェクトのエディタも返るため）
- エディタが裏にあると最初の `eval_file` がタイムアウトする。`--timeout 120`（秒）で再実行する
- シェーダなどを `AssetDatabase.Refresh` で読み直した直後は、`editor_play` が成功を返してもPlayが止まることがある。`editor_status` で再読み込みの終わりを待ってからPlayに入り、スクリプトの待ちには必ず上限を付ける
- `eval_file` では using が効かず、URPの型も直接参照できない。完全修飾名とリフレクションで触る
- `capture_game_view` の `save_path` はAssets基準。撮影後に `AssetDatabase.DeleteAsset` で消す
- `capture_game_view` は1920x1080の画面でも1280x720に縮めて保存する。画素差を測るときは、Play中に `ScreenCapture.CaptureScreenshot`（絶対パス可）で原寸を撮る。ときどき保存されないので、ファイルができたか確かめて撮り直す
- 計測データは山手線内データ（707図郭、4,283,302,643点。作り方は `docs/hundred-million-point-reproduction.md`）。`Tools/Run-Benchmark.ps1 -OpcPath <OPCの絶対パス>` で渡す。データを移した直後の1回目はStreamingが間に合わず描く点が減ることがあるので、捨てる回にする
- ビルド計測は `Tools/Run-Benchmark.ps1`。途中の画面を撮って点群が写っていることを確かめる。計測は起動から約12秒で終わるので、走っている間に撮る。窓が裏にあると画面全体の撮影には写らないので、`PrintWindow`（フラグ3）で窓の中身を撮る
- 自作シェーダのタグは `"RenderPipeline"="UniversalPipeline"` にする。違う値（例: `UniversalRenderPipeline`）だと、エディタでは描けるのに、ビルドでURPがそのシェーダの中身を全部捨てて何も描かれない。ビルドログの `After scriptable stripping: 0` が目印
- エディタでビルドすると、URPアセットやProjectSettingsが書き換わることがある。コミット前に差分を確認する
- `eval_file` で `BenchmarkPlayerBuilder.Build()` を呼ぶと、5秒でタイムアウトのエラーが返るが、ビルドは裏で続く。結果は `Editor.log` の `BenchmarkPlayerBuilder:` の行で確かめる。`errors=` が1以上でも `Succeeded` なら出力はできている（2026-11-03は `errors=2`。ログにUnity Connectの通信エラーがあるが、数え方は確かめていない）
- パスごとのGPU時間は `ProfilerRecorder` + `GpuRecorder` で取る。Render Graphのパスには自前の `ProfilingSampler` を渡す。サンプル数を必ず確認する
- GPUのサンプルは再コンパイル直後の1回目のPlayでしか届かないことがある。`ProfilingSampler.gpuElapsedTime` と `Profiling.Recorder` は値が止まるので使わない
- GPU描画の不具合は推測で直さず、エディタ内の診断スクリプトで実測する。出力にversionマーカーを入れ、調査後に削除する
