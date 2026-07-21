# 1億点超データの出所

この成果物でいう「1億点超表示」は、1億点を超える点群を格納したOPCをViewerで開き、LODとStreamingによって選択した数百万点を描画できることを指す。全点の同時描画ではない。

## 元データ

[東京都デジタルツイン実現プロジェクト 区部点群データ](https://www.geospatial.jp/ckan/dataset/tokyopc-23ku-2024)

- 航空レーザ測量による東京都区部の点群データ
- LAS等を1/500図郭単位で配布
- ライセンス: CC BY 4.0

## Converter

[UpaDotti/PointCloudConverter](https://github.com/UpaDotti/PointCloudConverter)

確認に使用したcommit: `13fe28ee50453125fffd1f25ec983b2b929b69a5`

このConverterで東京都公開LASをOPCへ変換した。

## 確認したOPC

- OPCヘッダの元点数: 7,045,046,813点
- ファイルサイズ: 67,851,426,064 byte
- 形式: OPC 1.0.0、9 byte/point
- SHA-256: `E6D98478135528DCACB36E2220DFEC0DD776E0949E6FFBD3EE2A6F560BA027BE`

OPC本体は約67.9GBのためリポジトリには含めない。第三者向けには、元データの公開元とConverterを示すところまでを再現情報の範囲とする。

## Viewerでの結果

Point Budgetを約400万点に設定し、LOD、Frustum Culling、Streamingを通して描画した。既存の固定カメラ経路では、準備済み描画点数が平均約467万〜522万点、Split count 4の平均フレーム時間が16.85〜18.23msだった。

条件と比較値は[分割描画検証](split-render-validation.md)、処理の仕組みは[点群の変換から描画まで](point-cloud-processing-flow.md)に記録している。
