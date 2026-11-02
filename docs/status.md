# 次にやること

次の作業と、人の判断を待っていることは、この文書だけに書く。他の文書には書かない。終わったものは消し、結果は各方式の文書へ書く。

更新日: 2026-11-02

## 人の判断待ち

| 項目 | 材料 |
| --- | --- |
| Split・GatherNearDepthを採用するか | ビルドでDirect 24.2 ms、Split 20.7 ms（0.86倍）、Gather 15.2 ms（0.63倍）、GatherNearDepth 19.5 ms（0.80倍）。Gatherは手前の建物で円が半径4画素で切れて隙間が出るので、採用候補はGatherNearDepth。原寸で色の差が8より大きい画素は、初期視点でSplit 0.73%、Gather 8.50%、GatherNearDepth 4.04%（周回経路ではGatherNearDepth 8.71%で、Splitは未計測）。GatherNearDepthの差の約92〜95%は、円の中心が最大約0.7画素ずれる丸めによるもので、誤りではなく精度の差（[原因](gather-prototype.md#細かい差の原因円の中心の丸め)）（[対策の結果](gather-prototype.md#円切れの対策-手前の角で判定するgatherneardepthモード)） |

## 次の作業

上ほど先にやる。

1. **Splitの並び順の係数を7か17に変えるか試す**: 実測では今の黄金比より0.3〜0.8ms速い（[分割描画検証](split-render-validation.md#並び順の係数)）
2. **点キャッシュ方式のSplitで測り直す**
   - SplitCount比較（旧方式の値しかない）。`SplitCount = 8` も測る
   - カメラ移動後と解像度変更後のスクリーンショット
   - 周回経路上のDirectとの画素差（GatherNearDepthと比べるため。[Gatherの記録](gather-prototype.md#円切れの対策-手前の角で判定するgatherneardepthモード)と同じ撮り方）

## いつかやる

- 点群と普通のメッシュを一緒に表示する（時期は未定）。前後関係と半透明は対応済み。影は不要と判断している。SSAOはメッシュのために残す（2026-11-02判断）。ただし点群はSSAOに入らないため、点群とメッシュの間には陰影が付かない
