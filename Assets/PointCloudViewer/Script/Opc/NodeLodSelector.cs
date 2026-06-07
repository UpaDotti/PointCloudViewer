using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// 八分木を走査して可視ノードを集め、Point Budgetをwater-fillingで配分するLOD選択。
/// 入力はCameraStateとOpcFileだけで、UnityのCamera/Screenには依存しない（テスト可能）。
/// 「1ピクセル当たり点数」は前フレームの実効値を降下判定にも使い、判断基準を一本化している。
/// </summary>
public class NodeLodSelector
{
    // 実効PPP（points per pixel）の動的クランプ範囲（暴走防止）
    private const float MinPointsPerPixel = 0.1f;
    private const float MaxPointsPerPixel = 4.0f;

    private readonly OpcFile _file;

    // 降下判定と配分に使う「1ピクセル当たり点数」。
    // 初期値は設定値で、以後は毎フレーム Budget/総面積 から逆算した実効値に更新される。
    private float _effectivePointsPerPixel;

    // 走査・配分の作業バッファ（毎フレーム使い回してGCを避ける）
    private readonly List<VisibleNode> _visibleNodes = new List<VisibleNode>();
    private readonly List<uint> _desiredCounts = new List<uint>();
    private readonly List<uint> _finalAllocs = new List<uint>();
    private readonly List<int> _activeIndices = new List<int>();
    private readonly Queue<int> _nodeIndicesToCheck = new Queue<int>();

    // Select中のカメラ情報（ComputeAreaPx用に保持する）
    private CameraState _camera;

    // Profiler用マーカー（名前は従来のまま維持してベンチマーク連続性を保つ）
    private static readonly ProfilerMarker _markerApplyPointBudget = new ProfilerMarker("OpcLoader.ApplyPointBudget");

    public NodeLodSelector(OpcFile file, float initialPointsPerPixel)
    {
        _file = file;
        _effectivePointsPerPixel = Mathf.Clamp(initialPointsPerPixel, MinPointsPerPixel, MaxPointsPerPixel);
    }

    /// <summary>
    /// 可視ノードを集めてPoint Budgetを配分し、選択結果（どのノードを何点描くか）をresultsに詰める。
    /// </summary>
    public void Select(in CameraState camera, int pointBudget, List<NodeSelection> results)
    {
        _camera = camera;

        // 視界に入っているノードを集める（葉 / 代表点で止める枝）
        CollectVisibleNodes();

        // Point Budgetを適用して描画対象を決定
        using (_markerApplyPointBudget.Auto())
        {
            ApplyPointBudget(pointBudget, results);
        }
    }

    // 八分木を走査して可視ノード候補を_visibleNodesへ集める
    private void CollectVisibleNodes()
    {
        // 初期化
        _visibleNodes.Clear();
        _nodeIndicesToCheck.Clear();

        OpcFile.Node[] nodes = _file.Nodes;

        // ルートノードを追加
        _nodeIndicesToCheck.Enqueue(_file.RootIndex);

        // 探索ループ
        while (_nodeIndicesToCheck.Count > 0)
        {
            // 処理対象のノードを取得
            int nodeIndex = _nodeIndicesToCheck.Dequeue();
            OpcFile.Node node = nodes[nodeIndex];

            // ノードが見えているか
            if (!GeometryUtility.TestPlanesAABB(_camera.frustumPlanes, node.bounds))
            {
                // 子の判定をスキップ
                continue;
            }

            // 代表点では足りない枝は子へ降りる
            if (!node.isLeaf && !CanStopAtBranch(node))
            {
                for (int i = 0; i < 8; i++)
                {
                    int childIndex = _file.ChildIndices[node.childStart + i];
                    if (childIndex >= 0)
                    {
                        _nodeIndicesToCheck.Enqueue(childIndex);
                    }
                }
                continue;
            }

            // 葉、または代表点で足りる枝は描画候補に追加
            _visibleNodes.Add(new VisibleNode
            {
                index = nodeIndex,
                pointCount = node.pointCount,
                areaPx = ComputeAreaPx(node.bounds),
            });
        }
    }

    private bool CanStopAtBranch(in OpcFile.Node node)
    {
        // 代表点が無い枝は止められない
        if (node.pointCount == 0) return false;

        // 画面ピクセル面積
        float areaPx = ComputeAreaPx(node.bounds);

        // 画面に必要な点数（面積 × 実効の1ピクセル当たり点数。配分と同じ物差しを使う）
        float requiredCount = areaPx * _effectivePointsPerPixel;

        // 枝の代表点が必要点数を満たすなら、葉に降りなくてよい
        return node.pointCount >= requiredCount;
    }

    private float ComputeAreaPx(Bounds bounds)
    {
        // Bounds球の半径を取得
        float r = bounds.extents.magnitude;

        // カメラ前方方向の奥行き（背後/直近はクランプして異常値を防ぐ）
        float depth = Vector3.Dot(bounds.center - _camera.position, _camera.forward);
        if (depth < _camera.minDepth) depth = _camera.minDepth;

        // ワールド半径を画面ピクセル半径へ変換（シェーダと同じ式: r × worldToPixel ÷ 奥行き）
        float rPx = r * _camera.worldToPixel / depth;

        // 画面ピクセル数を上限とし、異常値が全体配分を支配しないようにする
        float areaPx = Mathf.PI * rPx * rPx;
        return Mathf.Clamp(areaPx, 1f, _camera.maxAreaPx);
    }

    // Point Budgetを配分し、選択結果をresultsに詰める
    private void ApplyPointBudget(int pointBudget, List<NodeSelection> results)
    {
        // 選択結果リスト / 各種バッファを初期化
        results.Clear();
        _desiredCounts.Clear();
        _finalAllocs.Clear();
        _activeIndices.Clear();

        // visibleが無ければ何もしない
        if (_visibleNodes.Count == 0) return;

        // 実効PPPを更新（Budget/総面積から逆算。次フレームの降下判定にも使う）
        _effectivePointsPerPixel = ComputeEffectivePointsPerPixel(pointBudget);

        // 1パス目: 各ノードの希望点数を計算（pointCountキャップは water-filling 内で適用）
        for (int i = 0; i < _visibleNodes.Count; i++)
        {
            VisibleNode node = _visibleNodes[i];

            // 画面サイズに比例した希望点数（最低1点）
            uint wish = (uint)Mathf.Max(1f, node.areaPx * _effectivePointsPerPixel);

            _desiredCounts.Add(wish);
            _finalAllocs.Add(0);
            _activeIndices.Add(i);
        }

        // 2パス目: water-filling で予算を配分
        AllocateWithWaterFilling(pointBudget);

        // 3パス目: 配分結果を選択結果リストに反映
        for (int i = 0; i < _visibleNodes.Count; i++)
        {
            VisibleNode node = _visibleNodes[i];

            // 実点数を上限としてクランプ
            uint alloc = _finalAllocs[i];
            if (alloc > node.pointCount) alloc = node.pointCount;

            results.Add(new NodeSelection
            {
                nodeIndex = node.index,
                drawCount = alloc,
                areaPx = node.areaPx,
            });
        }
    }

    // 「1ピクセル当たり点数」の実効値を Budget / 可視総面積 から逆算する
    private float ComputeEffectivePointsPerPixel(int pointBudget)
    {
        // visible ノードのピクセル面積を合計
        float totalAreaPx = 0f;
        for (int i = 0; i < _visibleNodes.Count; i++)
            totalAreaPx += _visibleNodes[i].areaPx;

        // 面積ゼロのフェールセーフ（直前の実効値を維持する）
        if (totalAreaPx <= 0f) return _effectivePointsPerPixel;

        // Budget / 総面積で「画面に対するピクセル密度」を逆算（暴走防止のためクランプ）
        float maxPPP = pointBudget / totalAreaPx;
        return Mathf.Clamp(maxPPP, MinPointsPerPixel, MaxPointsPerPixel);
    }

    private void AllocateWithWaterFilling(int pointBudget)
    {
        long remaining = pointBudget;

        // 飽和ノードを順次確定させ、残予算を非飽和ノードに再配分するループ
        while (_activeIndices.Count > 0)
        {
            // 活性ノードの希望合計を集計
            long activeWish = 0;
            for (int j = 0; j < _activeIndices.Count; j++)
                activeWish += _desiredCounts[_activeIndices[j]];

            // 残予算で全希望を満たせるなら確定して終了
            if (activeWish <= remaining)
            {
                for (int j = 0; j < _activeIndices.Count; j++)
                {
                    int idx = _activeIndices[j];
                    uint pc = _visibleNodes[idx].pointCount;
                    uint wish = _desiredCounts[idx];
                    _finalAllocs[idx] = wish < pc ? wish : pc;
                }
                _activeIndices.Clear();
                break;
            }

            // 一律スケールを計算
            double scale = (double)remaining / activeWish;

            // pointCount で頭打ちになるノードを確定 → 残予算から引いて活性から外す
            bool anyCapped = false;
            for (int j = _activeIndices.Count - 1; j >= 0; j--)
            {
                int idx = _activeIndices[j];
                uint pc = _visibleNodes[idx].pointCount;
                double scaled = _desiredCounts[idx] * scale;
                if (scaled >= pc)
                {
                    _finalAllocs[idx] = pc;
                    remaining -= pc;
                    _activeIndices.RemoveAt(j);
                    anyCapped = true;
                }
            }

            // 誰も新たに飽和しなければスケールを確定して終了
            if (!anyCapped)
            {
                for (int j = 0; j < _activeIndices.Count; j++)
                {
                    int idx = _activeIndices[j];
                    uint scaled = (uint)Mathf.Max(1, Mathf.RoundToInt(_desiredCounts[idx] * (float)scale));
                    _finalAllocs[idx] = scaled;
                }
                _activeIndices.Clear();
                break;
            }
        }
    }

    // Point Budget適用前の候補ノード
    private struct VisibleNode
    {
        public int index;
        public uint pointCount;
        public float areaPx;
    }
}
