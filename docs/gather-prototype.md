# 集め直し方式（Gather、試作）

記録日: 2026-11-01

内蔵GPUでは点群描画が頂点処理で詰まっており、Direct描画は1点を4頂点・三角形2枚のQuadで描いています。集め直し方式は、点を1頂点の1画素点で描き、円の形は画面上で作り直します。

1. 全点を1画素の点として作業テクスチャへ描き、1画素に最も手前の1点だけを残します（色・画面半径・深度）。
2. computeシェーダで、各画素の周り半径4画素の点を調べ、自分を覆う円のうち最も手前の点の色と深度を作業テクスチャへ書きます。8x8画素の区画と周り4画素を共有メモリへ1回だけ読み込み、テクスチャ読み込みを減らしています。
3. 全画面パスで、その結果をカメラの色と深度へ書き写します。円に覆われない画素は書きません。
4. ノード中心での画面半径が4画素を超えるノード（周回中の点の約10%）は、これまで通りQuadで描きます。

主なファイル:

- `Assets/PointCloudViewer/Script/Opc/GatherRender/OpcGatherRenderFeature.cs`: 点描き → 集め直し → 書き写しのパスを積むURP Renderer Featureです。`_useComputeResolve` をオフにすると、集め直しを全画面のフラグメントシェーダで行う版に切り替わります。
- `Assets/PointCloudViewer/Shader/PointCloud_GatherFill.shader`: 1画素の点を描き、色と画面半径を詰めた値・深度を出力します。
- `Assets/PointCloudViewer/Shader/PointCloudGatherResolve.compute`: 共有メモリを使った集め直しです。
- `Assets/PointCloudViewer/Shader/PointCloud_GatherComposite.shader`: 集め直しの結果をカメラへ書き写します。
- `Assets/PointCloudViewer/Script/Opc/MultiDrawRenderer.cs`: 円が大きいノードのコマンドを先頭へ集め、その範囲だけQuadで描きます。

計測日: 2026-11-01、Unityエディタ上、`CameraOrbitMover` + `BenchmarkSession (capture=60fps)`、570フレーム、1920x1080、内蔵GPU（Intel UHD、D3D11）。モードを交互に各2回計測しました。

| モード | 平均フレーム時間 | メモ |
| --- | ---: | --- |
| Direct | 24.6 / 26.7 ms | Quadで円を描く |
| Gather（大きい円はQuad） | 18.7 / 17.9 ms | Quadで描く点は平均約44万点（全体の約10%） |
| Gather（全点を集め直し、半径4で切る） | 16.9 / 17.4 ms | 2026-11-01の別の回。Directは27.6 / 26.6 ms |
| Gather（フラグメント版の集め直し） | 22.4 / 22.6 ms | 同上 |
| DirectPixelPoints（比較用、全点1画素） | 11.4〜11.6 ms | 円を作らないため手前の面が透け、見た目は別物 |

後の回（SSAOの計測、[フレーム時間の原因分析](profiler-frame-analysis.md#点群以外の固定費)）では、Gather（大きい円はQuad）が16.24 / 16.24 ms、同じ回のDirectが25.53 msでした。上の表の18.7 / 17.9 msより約2ms速く、理由は確認していません。

ビルドでの計測（2026-11-02、Windowsプレイヤー・通常ビルド・D3D11・Intel UHD、`Tools/Run-Benchmark.ps1`、1920x1080の窓、コミット 310e1b7）: Direct → Split → DirectPixelPoints → Gather の順に4回測りました。

| モード | 平均フレーム時間 | p95 |
| --- | ---: | ---: |
| Direct | 24.25 / 24.19 / 24.20 / 24.23 ms | 27.0〜27.3 ms |
| Gather（大きい円はQuad） | 15.26 / 15.21 / 15.21 / 15.18 ms | 17.2〜17.8 ms |
| DirectPixelPoints（比較用） | 10.52 / 10.42 / 10.41 / 10.47 ms | 12.7〜12.8 ms |

- ビルドではGatherがDirectより約9.0ms（約37%）速く、エディタでの差（約6〜9ms）と同じ程度です。
- Quadで描く点は平均約45万点で、エディタと同じ程度です。
- 4回目の計測中に画面を撮り、点群が描かれていることを確認しました。

見た目: 静止画（1280x720のキャプチャ）でDirectと色が8段階より大きく違う画素は9.0%で、円の境目でどちらの円が手前になるかの違いです。明るさの偏りや画素のずれはなく、目視ではほぼ同じです。

カメラ移動中の見た目（2026-11-01、エディタ）: 周回経路上を1フレーム0.6°ずつ進めた26視点で、視点ごとにDirectとGatherを撮り比べました。

- Directと違う画素は全視点で13.7〜13.9%で、視点によって増えません。
- 瞬き（相手のモードは前後の視点で安定しているのに、自分だけ前後と違う画素）は、Gather 0.335%、Direct 0.328%で同程度です。
- 連続4視点を拡大して見ても、目立つ違いはありません。
- 撮影の注意: ベンチマークを使わないPlayでは `Application.runInBackground` が無効で、エディタが裏にあると止まり、撮影が1つ前の画面を返します。撮影前に `Application.runInBackground = true` にしました。それでも最後の4視点は撮影がずれたため除いています（原因は未調査）。

制限:

- 1データ・1経路・1台のGPU（Intel UHD）だけの計測です。
- パスごとのGPU時間は取れていません。集め直しの重さは、半径を変えた時のフレーム時間の差から見積もっています（フラグメント版で半径4が約9ms）。
- 円が大きいノードの判定はノード中心の奥行きで行うため、同じノード内でカメラに近い点の円は半径4画素で切れることがあります。
- 集め直しの半径（4画素）は、`OpcLoader.GatherRadiusPixels` と2つのシェーダの `GATHER_RADIUS` で揃える必要があります。
