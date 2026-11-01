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

見た目: 静止画（1280x720のキャプチャ）でDirectと色が8段階より大きく違う画素は9.0%で、円の境目でどちらの円が手前になるかの違いです。明るさの偏りや画素のずれはなく、目視ではほぼ同じです。

制限:

- エディタ上の1経路・1視点だけの計測です。ビルドでの計測と、カメラ移動時の見た目の確認はまだです。
- パスごとのGPU時間は取れていません。集め直しの重さは、半径を変えた時のフレーム時間の差から見積もっています（フラグメント版で半径4が約9ms）。
- 円が大きいノードの判定はノード中心の奥行きで行うため、同じノード内でカメラに近い点の円は半径4画素で切れることがあります。
- 集め直しの半径（4画素）は、`OpcLoader.GatherRadiusPixels` と2つのシェーダの `GATHER_RADIUS` で揃える必要があります。
