# 点群データの出所と作り方

計測に使った点群データの出所、ライセンス、作り方を記録する。データ本体はリポジトリに含めない。

この成果物でいう「1億点超表示」は、1億点を超える点群を格納したOPCをViewerで開き、LODとStreamingによって選択した数百万点を描画できることを指す。全点の同時描画ではない。

## 元データとライセンス

[東京都デジタルツイン実現プロジェクト 区部点群データ](https://www.geospatial.jp/ckan/dataset/tokyopc-23ku-2024)

- 作成者: 東京都デジタルサービス局 デジタルサービス推進部 デジタルサービス推進課
- ライセンス: [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/deed.ja)
- 利用規約: [免責事項・禁止事項](https://gic-tokyo.s3.ap-northeast-1.amazonaws.com/2023/dig/doc/license.pdf)（東京都はデータの完全性・正確性を保証しない）
- 航空レーザ測量による東京都区部の点群データ（2023年3〜4月計測）。LASを1/500図郭（400m×300m）ごとのZIPで配布している
- 座標系: JGD2011 平面直角座標系 第IX系

このリポジトリでは、上のデータを次のように加工して使っている。

- LASをPointCloudConverterでOPCへ変換し、間引いた代表点を作る
- 図郭の一覧 `docs/data/tokyo-yamanote-sheets.csv` は、配布ページの索引（ベクトルタイル）から図郭番号とZIPのURLを取り出したもの

この一覧と、OPCを描いた画面・動画は元データの加工物なので、MITではなくCC BY 4.0に従う。使うときは上の作成者・ライセンス・元データへのリンクと、加工したことを示す。

## 山手線内データ（2026-11、RTX 5070の計測）

東京都区部点群データのうち、山手線の内側とその周囲1図郭分。

### 範囲の決め方

1. 山手線30駅の座標（WGS84の概略値）を外回りの順に直線で結んで多角形にする。駅の間の線路の曲がりは表さないので、境界ぎわの図郭は実際の線路と1枚ずれることがある
2. 多角形と重なる図郭を選ぶ（575枚。うち完全に内側454枚、境界にかかる121枚）
3. 周囲8方向へ1枚分広げる（132枚追加、計707枚）。広げた先もすべてデータがある

駅の座標と処理は `Tools/TokyoPointCloud/select_sheets.py` にある。2026-11-14に作り直したスクリプトで出し直し、実際に使った707図郭と一致することを確かめた。

### 一覧

`docs/data/tokyo-yamanote-sheets.csv`

| 列 | 内容 |
| --- | --- |
| MESH_NO | 図郭番号（例 `09LD0842`） |
| inside_ratio | 図郭のうち山手線の多角形の内側にある面積の割合 |
| expand_ring | 0=多角形に重なる、1=周囲へ広げて足した |
| zip_bytes | 配布ZIPの大きさ（2026-11-14時点） |
| URL | 配布ZIPのURL |

- ZIPの合計は78.9GB、展開したLASは145.6GB
- 中身が空の図郭が4枚ある（約330バイトのZIP。皇居3枚、赤坂御用地1枚。計測対象外と考えられる）
- ZIPの中身はLAS 1.2、点フォーマット3（RGB・GPS時刻つき）が1ファイル。密度は約50点/m²（`09LD0842` で確認）

### 作り方

```bash
cd Tools/TokyoPointCloud
pip install -r requirements.txt
python select_sheets.py <作業フォルダ> --expand 1        # sheets.csv（この一覧と同じもの）を作る
python download_sheets.py <作業フォルダ>/sheets.csv <ZIPフォルダ>
python extract_sheets.py <ZIPフォルダ> <LASフォルダ>
```

LASフォルダを [PointCloudConverter](https://github.com/UpaDotti/PointCloudConverter) でOPCへ変換する（手順はConverterのREADME）。ダウンロードは2026-11-14に約66分、展開と変換は合わせて約20分だった（変換は論理20コア・メモリ32GBのPC）。

### できたOPC

- Converter: commit `1b82de1`（既定の設定）
- OPCヘッダの元点数: 4,283,302,643点（展開したLAS 707個の点数の合計と一致）
- ファイルサイズ: 41,401,399,516 byte
- 形式: OPC 1.0.0、9 byte/point、枝11,647・葉75,897
- 範囲: 東西8.4km × 南北14.1km × 高さ429m
- SHA-256: `6A82DB8764E4CAC3F69ED71814AB227041A023E9BFCBAFB8AEFF05E15000D469`

計測結果は [README](../README.md) と [分割描画検証](split-render-validation.md#処理時間) にある。

## 23区データ（2026-07〜11、Intel UHDの計測）

### Converter

[UpaDotti/PointCloudConverter](https://github.com/UpaDotti/PointCloudConverter)

確認に使用したcommit: `cbf7cfe5b76e07ff4ca0851a397e230a86e428d9`（2026-11-11の履歴書き換えで旧 `13fe28ee` から変わった。コードの処理は同じ）。2026-11-14の時点で、このcommitはConverterのリポジトリに見当たらない。

このConverterで東京都公開LASをOPCへ変換した。どの図郭を変換したかは記録していない。

### 確認したOPC

- OPCヘッダの元点数: 7,045,046,813点
- ファイルサイズ: 67,851,426,064 byte
- 形式: OPC 1.0.0、9 byte/point
- SHA-256: `E6D98478135528DCACB36E2220DFEC0DD776E0949E6FFBD3EE2A6F560BA027BE`

OPC本体は約67.9GBのためリポジトリには含めない。

### Viewerでの結果

Point Budgetを約400万点に設定し、LOD、Frustum Culling、Streamingを通して描画した。既存の固定カメラ経路では、準備済み描画点数が平均約467万〜522万点、Split count 4の平均フレーム時間が16.85〜18.23msだった（2026-07、旧画像スライス方式での計測。現在の点キャッシュ方式では未計測）。

条件と比較値は[分割描画検証](split-render-validation.md)、処理の仕組みは[点群の変換から描画まで](point-cloud-processing-flow.md)に記録している。
