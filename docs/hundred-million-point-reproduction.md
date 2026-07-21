# 1億点超データ表示の再現条件

この手順の対象は、1億点を超えるOPCデータをViewerで開き、LOD・Streamingを通して描画できることの再現である。全点の同時描画を意味しない。確認済みの `sample.opc` は70.45億点を格納する一方、現行シーンのPoint Budgetは400万点で、実際の準備済み描画点数はカメラ位置と常駐状態に応じて数百万点になる。

## 固定する成果物

| 項目 | 固定値 | 状態 |
| --- | --- | --- |
| Viewer | PointCloudViewer commit `2e226fe` | 確認済み |
| Scene | `Assets/Scenes/TcpViewer.unity` | 確認済み |
| OPC配置先 | `Assets/sample.opc` | 確認済み。ただしGit除外 |
| OPC形式 | `OPC\0`、version 1.0.0、9 byte/point | ヘッダから確認済み |
| 元点数 | 7,045,046,813点 | ヘッダから確認済み |
| ノード数 | 枝18,727、葉120,534 | ヘッダから確認済み |
| ファイルサイズ | 67,851,426,064 byte | 確認済み |
| SHA-256 | `E6D98478135528DCACB36E2220DFEC0DD776E0949E6FFBD3EE2A6F560BA027BE` | 2026-07-22確認 |

`sample.opc` は約67.9GBで、`.gitignore` の `*.opc` 対象である。リポジトリをcloneしただけでは再現できない。現在、同一ファイルの配布先、元LASの公開可否、同一OPCを再生成する入力一覧は未記録であり、第三者再現の最大の未解決点である。

## OPC生成手順

PointCloudConverter本体はこのリポジトリへコピーせず、公開GitHubリポジトリを固定コミットで参照する。既に `C:\Users\<user>\Github\PointCloudConverter` がclone済みなら、そのパスを `--converter-repo` に指定してよい。既存の未コミット変更がある場合は破棄せず、別worktreeで固定commitを開く。

参照先: [UpaDotti/PointCloudConverter](https://github.com/UpaDotti/PointCloudConverter)、commit `13fe28ee50453125fffd1f25ec983b2b929b69a5`

```powershell
git clone https://github.com/UpaDotti/PointCloudConverter.git ..\PointCloudConverter
git -C ..\PointCloudConverter checkout 13fe28ee50453125fffd1f25ec983b2b929b69a5

py -3.12 -m venv .venv-opc-reproduction
.\.venv-opc-reproduction\Scripts\python.exe -m pip install numpy==2.0.1 laspy==2.6.1 tqdm==4.66.4

 # convert_opc.py の固定パスに入力LASを置いて実行する。
 # 入力: C:\Users\<ユーザー名>\Desktop\点群\*.las
 # 出力: C:\Users\<ユーザー名>\Desktop\sample.opc
 .\.venv-opc-reproduction\Scripts\python.exe `
   ..\PointCloudConverter\PointCloudConverter\OctreePointCloud\convert_opc.py
```

Converterはcommit `13fe28ee50453125fffd1f25ec983b2b929b69a5` の `Options()` をそのまま使う。固定値は `max_depth=8`、`max_points_per_leaf=200000`、`max_points_per_branch=30000`、`pattern=*.las`、`read_size=2000000`、OPC 1.0.0、9 byte/pointである。入力LASは第三者が同じ公開データを取得して用意する。

## 生成結果の確認と配置

LASヘッダとOPCヘッダの点数をそれぞれ確認する。

```powershell
 .\.venv-opc-reproduction\Scripts\python.exe -c "import glob, laspy; print(sum(laspy.open(p).header.point_count for p in glob.glob(r'.opc-reproduction\input\*.las')))"
```

期待値は入力LASの合計点数とOPCヘッダの `total_points` がともに100,000,000点以上、OPCがmagic `OPC\0`、version `1.0.0`、`bytes_per_point=9` である。

合格後、Viewerを閉じた状態で生成OPCを配置する。既存の70.45億点版を残す場合は先に別名へ退避する。

```powershell
Copy-Item `
  .\.opc-reproduction\output\sample.opc `
  .\Assets\sample.opc
```

以降は下記のViewer設定と実行手順を使う。

## 環境

| 項目 | 条件 | 根拠・確認状態 |
| --- | --- | --- |
| OS | Windows 11 Home、build 26200 | 検査ホストから確認済み |
| Unity | 6000.3.9f1 (`7a9955a4f2fa`) | `ProjectSettings/ProjectVersion.txt` |
| URP | 17.3.0 | `Packages/manifest.json` |
| Graphics API | 実行時にUnity Editorで確認して記録 | 過去検証時の値は未記録 |
| 解像度 | Game Viewを1920×1080に固定 | 証拠画像は1920×1080。過去ベンチマーク時のGame View表示値は未記録 |
| GPU | Intel(R) UHD Graphics（オンボード） | OPC検証に使用したGPUとしてユーザー確認済み |
| GPU driver | 32.0.101.7076 | 2026-07-22に検査ホストから確認 |
| CPU / RAM | Core i5-12450H（8 core / 12 thread）、約16GB RAM | 2026-07-22に検査ホストから確認 |

OPCの既存検証はオンボードのIntel UHD Graphicsで実施した。RTX 5070はA-04の再現基準に含めない。ただし、過去の実行時点のdriverとGraphics APIは記録されていないため、次回実行値を基準として固定する。

## Viewer設定

| 場所 | 項目 | 固定値 |
| --- | --- | ---: |
| `TcpViewer/OpcLoader` | File Path | `Assets/sample.opc` |
| `TcpViewer/OpcLoader` | Render Mode | `Split` (`_renderMode: 1`) |
| `TcpViewer/OpcLoader` | Point Budget | 4,000,000 |
| `TcpViewer/OpcLoader` | Fallback Quality | 1.0 |
| `TcpViewer/OpcLoader` | Point Size Scale | 0.5 |
| `PC_Renderer/OpcSplitRenderFeature` | Split Count | 4 |
| `PC_Renderer/OpcSplitRenderFeature` | Warp Tolerance | 0.02 |
| `PC_RPAsset` | Render Scale / MSAA | 1.0 / Disabled (`m_MSAA: 1`) |
| Main Camera | FOV / near / far | 60 / 0.3 / 10,000 |
| Main Camera | Dynamic Resolution / Post Processing | Off / Off |

## 固定カメラ経路と計測

Scene内の `Benchmark` GameObjectは現在無効なので、実行前に有効化する。保持されている設定は次のとおり。

| 項目 | 固定値 |
| --- | ---: |
| 注視中心 | `(0, 0, 0)` |
| 軌道直径 / 高さ | 200 / 50 |
| 開始角 / 周回 | 0度 / 1周、反時計回り |
| 所要時間 | 10秒 |
| Capture FPS / warm-up | 60 / 30 frames |

`CameraOrbitMover` がMain Cameraをこの経路で動かし、`BenchmarkSession` が終了時にConsoleへ統計を出力する。終了後はEditorのPlay Modeが自動停止する。

## 実行手順

1. PointCloudViewerをcommit `2e226fe`へ合わせる。未コミット変更がある場合は破棄せず、別worktreeなどでこのcommitを開く。
2. 同一の `sample.opc` を `Assets/sample.opc` に配置する。空きディスクはファイル本体約67.9GBに加え、UnityのLibraryとログの余裕を確保する。
3. PowerShellでファイル同一性を確認する。

   ```powershell
   (Get-Item -LiteralPath .\Assets\sample.opc).Length
   Get-FileHash -LiteralPath .\Assets\sample.opc -Algorithm SHA256
   ```

4. Unity 6000.3.9f1でProjectを開き、`Assets/Scenes/TcpViewer.unity` を開く。
5. Game Viewを1920×1080、Scale 1xに固定する。実行GPUがIntel UHD GraphicsであることをProfilerまたは起動ログで確認し、Graphics APIとGPU driverも記録する。
6. `OpcLoader` と `OpcSplitRenderFeature` を上表どおりにする。Hierarchyで `Benchmark` を有効化する。
7. ConsoleをclearしてPlayする。10秒の軌道終了と自動停止を待ち、例外や `OpcLoader` / `PointStreamer` のエラーがないことを確認する。
8. Consoleの `=== BenchmarkSession` から始まる統計全文、実行時のGame Viewスクリーンショット、CPU/GPU/driver/解像度を同じ計測記録へ保存する。

## 合格条件

- OPCヘッダの元点数が100,000,000点以上であり、このファイルでは7,045,046,813点と確認できる。
- `OpcLoader.Draw.Points` と `OpcLoader.Draw.Commands` が0より大きく、点群がGame Viewに表示される。
- 10秒の固定経路を完走し、ファイル読込失敗、プール満杯、描画例外が出ない。
- `OpcLoader.Mode.Split = 1`、`OpcSplit.SplitCount = 4` をログで確認できる。
- 統計全文、スクリーンショット、ハードウェア情報を同じ実行の証拠として残す。

フレーム時間やFPSには合格しきい値をまだ置かない。まず同じデータ・環境・設定・操作を固定して再測定し、その結果を基準値にする。

## 現在の証拠と未確認事項

- 2026-07-04/08の既存結果はオンボードのIntel UHD Graphicsで実施した（2026-07-22ユーザー確認）。同じViewer経路のSplit count 4で平均16.85〜18.23ms、準備済み点数は平均約467万〜522万点だった。詳細は `docs/split-render-validation.md` にある。
- 既存結果にはOPCのSHA-256、GPU driver、Graphics API、Game View解像度の明記がなく、今回固定した条件での再実行結果ではない。
- Intel UHD Graphics上の現行commit、同一OPC、1920×1080、固定経路によるConsole統計とスクリーンショットは未取得。
- 既存70.45億点版 `sample.opc` の第三者向け入手方法と元LASは未確定。別経路として、合成RGB LAS 100,000,001点の生成・OPC変換・ヘッダ検査手順を固定した。
- 合成入力の100,001点縮小版では、LAS生成、Converter commit `13fe28e` の全OPCパイプライン、ヘッダ検査まで2026-07-22に完走した。1億1点のフルスケール実行値は未取得。
- 「70.45億点格納」と「毎フレーム数百万点描画」は区別する。全点同時常駐・全点同時描画は対象外。
