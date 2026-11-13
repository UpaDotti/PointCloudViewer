# PointCloudViewer

PointCloudViewerは、LAS由来の数十億点規模の点群（計測データは約43億点）を、LODとStreamingで1フレーム数百万点に絞ってUnityで表示するための検証用Viewerです。
Octreeの画面占有面積でLODを選び、見える範囲の点だけを段階的にGPUへ常駐させて、Indirect Drawで描画します。
内蔵GPUで詰まっている頂点処理を減らす方式を比べた結果、Split（点キャッシュ）をメインに採用し、Directを基準として残しています（詰まっている根拠は[フレーム時間の原因分析](docs/profiler-frame-analysis.md)、採用の理由は[集め直し方式](docs/gather-prototype.md#採用の判断)）。

## 描画モード

`OpcLoader` の Render Mode で切り替えます。

| モード | 位置づけ | 円（点の見た目）の作り方 | 詳細 |
| --- | --- | --- | --- |
| Direct | 基準 | 1点を4頂点のQuadで描く | [仕組み](docs/point-cloud-processing-flow.md) |
| Split | 採用（メイン） | 1/Nずつ作り直す点一覧N本を、円として描く | [分割描画検証](docs/split-render-validation.md) |

## 最新の計測

### 計測データ

東京都区部点群データのうち、山手線の内側とその周囲1図郭分（1/500図郭707枚、4,283,302,643点、OPC 41.4GB）です。公開データから同じものを作れます（一覧・スクリプト・手順は[点群データの出所と作り方](docs/hundred-million-point-reproduction.md#山手線内データ2026-11rtx-5070の計測)）。

計測条件: Windowsプレイヤー（通常ビルド、D3D11）、`Tools/Run-Benchmark.ps1 -OpcPath <このOPC>`（`CameraOrbitMover` + `BenchmarkSession (capture=60fps)`、570フレーム、1920x1080の窓）。カメラはデータの中心（市ヶ谷付近）の上空を回ります。DirectとSplitを交互に4回測った平均フレーム時間の範囲です。毎回、計測中の画面で点群が写っていることを確認しています。

| GPU | 計測日 | Direct | Split（点キャッシュ、count 4） | SplitとDirectの比 |
| --- | --- | ---: | ---: | ---: |
| NVIDIA GeForce RTX 5070（VRAM 12GB） | 2026-11-14 | 2.87〜3.09 ms | 1.72〜1.77 ms | 0.58 |
| Intel UHD | 未計測 | – | – | – |

- RTX 5070: ドライバ 32.0.16.1088、リモートデスクトップ接続中に計測。コードはコミット 7d5d340 の時点（以降は文書だけの変更）。1フレームに描いた点は平均約456万点です。1回ごとの値は[分割描画検証](docs/split-render-validation.md#処理時間)にあります。
- 2026-11-13以前の値は別のデータ（23区、作り直せない）での計測です。比べられないので、ここには載せません（[分割描画検証](docs/split-render-validation.md#処理時間)に残しています）。

## 文書

| 知りたいこと | 文書 |
| --- | --- |
| 次にやること・人の判断待ち | [次にやること](docs/status.md) |
| 仕組み（処理の流れ・クラス・データの形・設計判断） | [点群の変換から描画まで](docs/point-cloud-processing-flow.md) |
| 分割描画の実装・計測・URP / Render Graphの制約 | [分割描画検証](docs/split-render-validation.md) |
| 集め直し方式の計測と不採用の理由（コードは削除済み） | [集め直し方式](docs/gather-prototype.md) |
| 点群データの出所・ライセンス・作り方 | [点群データの出所と作り方](docs/hundred-million-point-reproduction.md) |
| 何が重いか（フレーム時間の内訳、頂点処理で詰まっている根拠） | [フレーム時間の原因分析](docs/profiler-frame-analysis.md) |
| AIへの作業指示 | [CLAUDE.md](CLAUDE.md) |

## 点群データの用意

点群データはリポジトリに含まれていません。LASから自分でOPCを作ります。

1. LASを用意します。計測に使ったのは[東京都の区部点群データ](https://www.geospatial.jp/ckan/dataset/tokyopc-23ku-2024)（CC BY 4.0）です。全体はとても大きいので、まずは数図郭分で試すのがおすすめです。RTX 5070の計測に使った山手線内の707図郭は、一覧 `docs/data/tokyo-yamanote-sheets.csv` と `Tools/TokyoPointCloud/` のスクリプトで同じものを用意できます（手順は[点群データの出所と作り方](docs/hundred-million-point-reproduction.md#山手線内データ2026-11rtx-5070の計測)）。
2. [PointCloudConverter](https://github.com/UpaDotti/PointCloudConverter) で、LASのフォルダを1つのOPCに変換します。出力は `sample.opc` です（手順はConverterのREADME）。
3. `sample.opc` を `Assets/` の直下に置きます。`*.opc` は `.gitignore` に入っているのでコミットされません。

別の場所に置くときは、`OpcLoader` の File Path に絶対パスを書きます。ビルドでは起動引数 `-opcPath <パス>` でも指定できます。

## 動かし方

- Unity 6000.3.9f1、URP 17.3.0（Render Graph有効）。
- `Assets/Scenes/TcpViewer.unity` を開いてPlayします。点群ファイルは `OpcLoader` の File Path（既定は `Assets/sample.opc`）です。ファイルが無いときはConsoleにエラーが出て、何も描かれません。
- エディタで計測するときは、シーンの `Benchmark` オブジェクトを有効にしてPlayします。終わるとConsoleに `=== BenchmarkSession` から始まる結果が出ます。

## 現在の制限

- 計測は1データ・1カメラ経路・1台のGPU（RTX 5070）だけです。
- 2026-11-01より前のビルドの計測値は無効です（シェーダのタグ違いで点群が描かれていませんでした）。

## ライセンス

- このリポジトリのコードと文書は [MIT License](LICENSE) です。ただし `docs/data/` の図郭一覧は除きます（下記）。
- 点群データは含みません。計測に使ったのは「[東京都デジタルツイン実現プロジェクト 区部点群データ](https://www.geospatial.jp/ckan/dataset/tokyopc-23ku-2024)」（作成者: 東京都デジタルサービス局、[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/deed.ja)）で、OPCへ変換して使っています。
- `docs/data/tokyo-yamanote-sheets.csv` は、このデータの索引から図郭番号とURLを取り出したもので、CC BY 4.0 に従います。点群を描いた画面や動画も同じです。詳細は[点群データの出所と作り方](docs/hundred-million-point-reproduction.md#元データとライセンス)。
