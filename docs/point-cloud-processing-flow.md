# 点群の変換から描画まで

この文書は、LASをViewer向け形式へ変換し、UnityでLOD選択・ストリーミング・GPU転送・描画するまでのデータ遷移を1ページで示す。Viewerが読む形式はOPCだけである。

## 全体フロー

```text
LAS（実座標 + 16-bit RGB）
│
└─ OPC経路: convert_opc.py
   ├─ 中心寄せ・Unity座標化（Y/Z交換）
   ├─ Octree分割（最大深さ8、葉は原則20万点以下）
   ├─ 葉に実点、枝に子から間引いた代表点（最大3万点）を配置
   └─ OPC（ヘッダ + 枝/葉索引 + 9 byte/点の量子化RGBボディ）
      │
      └─ OpcLoader.Start
         ├─ OpcFile: ヘッダと索引を読み、親子参照を復元
         ├─ GpuPointPool: 全ノード共有GraphicsBufferを確保
         └─ PointStreamer: ルート代表点の先読みを開始
            │
            └─ OpcLoader.Update（毎フレーム）
               1. NodeLodSelector
                  Frustum Culling → 画面占有面積でOctree降下判定
                  → Point Budgetをwater-filling配分 → NodeSelection[]
               2. PointStreamer
                  必要点数を反映 → 非同期ファイル読込 → 12 byte/点へパック
                  → 共有GraphicsBufferへ部分アップロード → 不要ノードを解放
               3. MultiDrawRenderer.Prepare
                  未ロード時は常駐する親代表点へfallback
                  → IndirectDrawArgs + NodeDrawDataをGraphicsBufferへ転送
               4. 描画モード分岐
                  ├─ Direct: 1回のRenderPrimitivesIndexedIndirectで全コマンドを描画
                  └─ Split: OpcSplitRenderFeatureへ受け渡し
                     → 毎フレーム1/N点を1画素の点として整列（1画素1点）
                     → 点のある画素を点一覧へ詰め直し（N本保持）
                     → 全N本の点一覧を現在のカメラで円として描画
```

## 描画モード

| モード | 位置づけ | 円の作り方 |
| --- | --- | --- |
| Direct | 基準 | 1点を4頂点のQuadで描く |
| Split | 採用（メイン） | 1/Nずつ作り直す点一覧N本を、Quadで円として描く |

## 主要クラスの責務

| クラス | 責務 | ファイル |
| --- | --- | --- |
| `OpcLoader` | OPC経路の合成ルート。初期化し、毎フレーム `選択 → Streaming → Prepare → 描画モード分岐` の順で呼ぶ | `Assets/PointCloudViewer/Script/Opc/OpcLoader.cs` |
| `NodeLodSelector` | Octreeの可視走査、画面占有面積に基づく降下判定、Point Budgetのwater-filling配分 | `Assets/PointCloudViewer/Script/Opc/NodeLodSelector.cs` |
| `PointStreamer` | ノード常駐状態、非同期ロード要求、GPUプールへのアップロード、fallback親の維持、遅延アンロードを管理 | `Assets/PointCloudViewer/Script/Opc/PointStreamer.cs` |
| `MultiDrawRenderer` | 常駐点からIndirect Drawコマンドとノード別復元情報を構築し、各描画モードへ提供する | `Assets/PointCloudViewer/Script/Opc/MultiDrawRenderer.cs` |
| `OpcSplitRenderFeature` | URP Render Graphに分割描画パスを追加し、1本の点一覧の作り直しと、全点一覧の円描画を行う | `Assets/PointCloudViewer/Script/Opc/SplitRender/OpcSplitRenderFeature.cs` |

補助クラスは、`OpcFile` がOPC索引、`OpcPointLoadWorker` がワーカースレッド上のファイル読込と12 byte形式へのパック、`GpuPointPool` が共有 `GraphicsBuffer` の確保・解放・転送を担当する。

## データが変わる地点

| 地点 | データ |
| --- | --- |
| LAS入力 | 実座標と16-bit RGB。複数LASを再帰的に読む |
| OPC変換後 | ノードごとのtight bounds、点単位offset、点数、Octree path。座標は各ノードbounds内の16-bit量子値、色は8-bit RGB（9 byte/点） |
| CPUロード後 | `PackedPoint12`。`xy`、`z`、`rgba` の3つの`uint`へ再パック（12 byte/点） |
| GPU常駐後 | 全ノード共有のStructured `GraphicsBuffer`。各ノードは`poolStart`と`loadedCount`で領域を指す |
| 描画準備後 | `IndirectDrawIndexedArgs` と `NodeDrawData(boundsMin/max, worldRadius, pointStart)` |
| シェーダ出力 | 量子化座標をboundsで復元し、1点をQuadとしてDirect経路で描画 |
| 点一覧（Split） | 1画素1点に絞った点ごとのワールド座標・Color32・ワールド半径（20 byte/点）。N本を保持し、Quadとして描画 |

## 設計上の要点と制約

- 枝の代表点で必要密度を満たせる場合は子へ降りず、足りない場合だけOctreeを降りる。可視ノード間の点数はPoint Budget内で画面占有面積に応じて配分する。
- ストリーミングは要求点数の2倍を先読みし、追加ロード中も旧データまたは常駐親を描いて欠落を抑える。共有GPUプールが満杯なら視界外ノードを解放して再試行する。
- Directは全ノードを1回のマルチドローで描く。Splitは点を`SplitCount`本の点一覧へ分散して1フレームの整列量を減らし、1画素1点に間引いて表示する点の数も減らす。一方、点一覧の本数に比例して表示Draw callと保持バッファが増え、間引きのため静止時でもDirectと一部の画素が異なる（初期視点・1920x1080で、色の差が8より大きい画素は0.73%。[分割描画検証](split-render-validation.md#静止時の画素差)）。
- SplitはURP Render Graphのパスとして、不透明と空の描画後・半透明の前（`BeforeRenderingTransparents`）にカメラの色と深度へ書き込む。Directは `OpcLoader.Update` から直接描画する。
- DirectPixelPoints・Gather・GatherNearDepthは比較のうえ不採用とし、2026-11-04に削除した。理由と測定値は `docs/gather-prototype.md` に残す。
- 旧TPC経路（空間グリッド + チャンク別LOD、`TpcLoader`）は使われていなかったため、2026-11-11に削除した。削除前の実装はコミット 50a9a7d で確認できる。

## 設計判断（LOD / Culling / Streaming）

| 判断 | 採用理由 | 代替案と不採用理由 | 実装根拠 |
| --- | --- | --- | --- |
| Octreeの画面占有面積でLODを選ぶ | カメラ距離だけでなく、投影サイズに応じて細部を増減でき、同じPoint Budgetで視認性を優先できる | 固定深度は視点や画角で点密度が変わり、遠景に過剰な点を割り当てる | `NodeLodSelector` |
| Frustum外ノードを選択対象から除外 | GPU転送・描画コマンドの対象を可視範囲に限定し、数十億点のOPCでも常駐量を抑える | 全ノード走査・描画はメモリと帯域を先に消費する | `NodeLodSelector`、`CameraState` |
| 親代表点をfallbackとして常駐 | 子LODの非同期読込中も穴を作らず、カメラ移動時の見た目を連続させる | 同期ロードは表示を止める。fallbackなしはロード待ちの欠落を生む | `PointStreamer`、`MultiDrawRenderer` |
| 共有GPUプール + 遅延アンロード | ノードごとのBuffer乱立を避け、ロード済み点を再利用できる | ノードごとの専用Bufferは管理・割当が増え、視点移動で断片化しやすい | `GpuPointPool`、`PointPoolAllocator` |
| Direct / Splitを残す | Splitは遠くでも近くでもDirectより速く（約10%）、点ごとに位置を持つのでずれが無いためメインにする。Directは正解の基準として残す | Directだけでは頂点処理が重い。Splitだけでは正しさを比べる基準が無くなる。Gather系は速いが、円が切れるか、近い視点で利点が消える | `MultiDrawRenderer`、`OpcSplitRenderFeature` |

この設計は「全点を同時にGPUへ載せる」のではなく、可視性・投影密度・Point Budgetを先に決め、必要な点だけを段階的に常駐させる方針である。したがって性能評価では、OPC総点数と同時描画点数を分けて記録する。

## 未確認・説明対象外

- 変換スクリプトとViewerを連続実行したE2E再現、OPCファイル仕様の独立した互換性試験、破損ファイル試験は未確認。
- `max_depth=8`、葉20万点、枝3万点、OPC 9 byte/点は現行既定値であり、別設定・別データセットでの品質と性能は未確認。
- シェーダ内部の色変換、アンチエイリアス品質、GPU各パスの専用時間、計測データの作り方（`docs/hundred-million-point-reproduction.md`）はこの文書の対象外。分割描画の確認済み条件と測定値は `docs/split-render-validation.md` を参照する。

## 実装根拠

- Converter: `PointCloudConverter/OctreePointCloud/convert_opc.py`、`points_writer.py`、`octree.py`、`branch_lods.py`、`opc_writer.py`
- Viewer: 上記5クラスに加え、`Assets/PointCloudViewer/Script/Opc/OpcFile.cs`、`OpcPointLoadWorker.cs`、`GpuPointPool.cs`
