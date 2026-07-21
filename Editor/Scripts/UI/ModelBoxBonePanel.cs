using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// 骨骼 / 蒙皮 信息面板。
    /// 从 ModelBoxSelectionInspector 提取，在左侧分类栏独立展示。
    /// 功能：骨骼层级树、逐骨骼权重统计、Blend Shape 计数。
    /// </summary>
    public class ModelBoxBonePanel
    {
        // 滚动位置
        private Vector2 _boneScrollPos;

        // 选中骨骼
        private int _selectedBoneIndex = -1;

        // [perf] 骨骼权重统计缓存（仅在 _selectedBoneIndex 变化时重新计算）
        private int _cachedBoneIndex = -1;
        private int _cachedInfluencedVerts;
        private int _cachedTotalVerts;
        private float _cachedMaxWeight;
        private float _cachedAvgWeight;

        // 骨骼搜索过滤
        private string _boneSearchFilter = "";

        // [feat] 骨骼权重可视化
        private BoneWeightDisplayMode _displayMode = BoneWeightDisplayMode.Off;
        private float _weightThreshold = 0.1f;
        private float[] _cachedVertexWeights; // 逐顶点权重缓存
        private int _cachedWeightsBoneIndex = -1;
        private Mesh _cachedWeightsMesh;
        private SkinnedMeshRenderer _cachedSourceSMR; // [fix] 权重来源 SMR 引用
        // [fix H6] 跟踪上次状态，仅在变化时触发 RepaintAll
        private BoneWeightDisplayMode _lastSyncedMode = BoneWeightDisplayMode.Off;
        private float _lastSyncedThreshold = -1f;

        public void Draw()
        {
            var selected = Selection.activeTransform;
            if (selected == null)
            {
                EditorGUILayout.HelpBox("请先在 Scene 中选中一个物体。", MessageType.Info);
                return;
            }

            var smr = selected.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null)
            {
                EditorGUILayout.HelpBox(
                    "选中物体没有 SkinnedMeshRenderer 组件。\n骨骼预览仅对蒙皮网格可用。",
                    MessageType.Info);
                return;
            }
            _cachedSourceSMR = smr; // 缓存 SMR 引用用于权重可视化目标

            var mesh = smr.sharedMesh;
            if (mesh == null)
            {
                EditorGUILayout.HelpBox("SkinnedMeshRenderer 没有关联的 Mesh。", MessageType.Warning);
                return;
            }

            var bones = smr.bones;
            var rootBone = smr.rootBone;

            // ===== 基础信息 =====
            ModelBoxStyles.DrawSectionHeader("骨骼概览");

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawStatRow("骨骼数量", $"{bones?.Length ?? 0}");
            DrawStatRow("根骨骼", rootBone != null ? rootBone.name : "(无)");
            DrawStatRow("Blend Shapes", $"{mesh.blendShapeCount}");
            DrawStatRow("绑定模式", mesh.bindposes != null ? $"{mesh.bindposes.Length} bindposes" : "无");
            EditorGUILayout.EndVertical();

            // ===== 骨骼搜索 =====
            EditorGUILayout.Space(6);
            ModelBoxStyles.DrawSectionHeader("骨骼层级");

            EditorGUILayout.BeginHorizontal();
            _boneSearchFilter = EditorGUILayout.TextField(_boneSearchFilter, EditorStyles.toolbarSearchField);
            if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22)))
                _boneSearchFilter = "";
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);

            // ===== 骨骼层级树 =====
            if (bones != null && bones.Length > 0)
            {
                _boneScrollPos = EditorGUILayout.BeginScrollView(_boneScrollPos, GUILayout.MaxHeight(300));

                bool hasFilter = !string.IsNullOrEmpty(_boneSearchFilter);
                string filterLower = hasFilter ? _boneSearchFilter.ToLower() : "";

                for (int i = 0; i < bones.Length; i++)
                {
                    var bone = bones[i];
                    if (bone == null) continue;

                    // 搜索过滤
                    if (hasFilter && !bone.name.ToLower().Contains(filterLower))
                        continue;

                    bool isSelected = (_selectedBoneIndex == i);
                    var prevColor = GUI.contentColor;

                    if (isSelected)
                        GUI.contentColor = new Color(0.4f, 1f, 0.4f);
                    else
                        GUI.contentColor = EditorGUIUtility.isProSkin
                            ? new Color(0.75f, 0.75f, 0.75f)
                            : new Color(0.3f, 0.3f, 0.3f);

                    EditorGUILayout.BeginHorizontal();
                    string prefix = isSelected ? "►" : "  ";
                    if (GUILayout.Button($"{prefix} [{i}] {bone.name}", EditorStyles.miniLabel))
                    {
                        _selectedBoneIndex = isSelected ? -1 : i;
                        // 选中骨骼时在 Scene 中高亮
                        if (!isSelected)
                            EditorGUIUtility.PingObject(bone.gameObject);
                        // [fix] 切换骨骼时触发 SceneView 重绘（权重可视化需要更新）
                        if (_displayMode != BoneWeightDisplayMode.Off)
                            EditorApplication.delayCall += () => SceneView.RepaintAll();
                    }
                    EditorGUILayout.EndHorizontal();

                    GUI.contentColor = prevColor;
                }

                EditorGUILayout.EndScrollView();
            }

            // ===== 权重统计 =====
            if (_selectedBoneIndex >= 0 && _selectedBoneIndex < (bones?.Length ?? 0))
            {
                EditorGUILayout.Space(6);
                ModelBoxStyles.DrawSectionHeader($"骨骼 [{_selectedBoneIndex}] 权重统计");

                // [perf] 仅在选中骨骼变化时重新计算
                if (_cachedBoneIndex != _selectedBoneIndex)
                {
                    _cachedBoneIndex = _selectedBoneIndex;
                    var boneWeights = mesh.boneWeights;
                    if (boneWeights != null && boneWeights.Length > 0)
                    {
                        int influencedVerts = 0;
                        float maxWeight = 0f;
                        float totalWeight = 0f;

                        for (int vi = 0; vi < boneWeights.Length; vi++)
                        {
                            float w = 0f;
                            if (boneWeights[vi].boneIndex0 == _selectedBoneIndex) w = boneWeights[vi].weight0;
                            else if (boneWeights[vi].boneIndex1 == _selectedBoneIndex) w = boneWeights[vi].weight1;
                            else if (boneWeights[vi].boneIndex2 == _selectedBoneIndex) w = boneWeights[vi].weight2;
                            else if (boneWeights[vi].boneIndex3 == _selectedBoneIndex) w = boneWeights[vi].weight3;

                            if (w > 0.001f)
                            {
                                influencedVerts++;
                                totalWeight += w;
                                if (w > maxWeight) maxWeight = w;
                            }
                        }
                        _cachedInfluencedVerts = influencedVerts;
                        _cachedTotalVerts = boneWeights.Length;
                        _cachedMaxWeight = maxWeight;
                        _cachedAvgWeight = influencedVerts > 0 ? totalWeight / influencedVerts : 0;
                    }
                    else
                    {
                        _cachedInfluencedVerts = 0;
                        _cachedTotalVerts = 0;
                        _cachedMaxWeight = 0;
                        _cachedAvgWeight = 0;
                    }
                }

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                DrawStatRow("受影响顶点", $"{_cachedInfluencedVerts:N0} / {_cachedTotalVerts:N0}");
                DrawStatRow("最大权重", $"{_cachedMaxWeight:F3}");
                DrawStatRow("平均权重", $"{_cachedAvgWeight:F3}");
                EditorGUILayout.EndVertical();

                // [feat] 骨骼权重可视化控制
                EditorGUILayout.Space(6);
                ModelBoxStyles.DrawSectionHeader("权重可视化");

                var prevMode = _displayMode;
                _displayMode = (BoneWeightDisplayMode)EditorGUILayout.EnumPopup("显示模式", _displayMode);

                if (_displayMode == BoneWeightDisplayMode.Threshold)
                    _weightThreshold = EditorGUILayout.Slider("权重阈值", _weightThreshold, 0f, 1f);

                // 模式变化时刷新权重缓存并通知 SelectionManager
                if (_displayMode != prevMode)
                    _cachedWeightsBoneIndex = -1; // 强制重算

                // 计算逐顶点权重（仅在骨骼索引或 Mesh 变化时）
                if (_displayMode != BoneWeightDisplayMode.Off)
                {
                    if (_cachedWeightsBoneIndex != _selectedBoneIndex || _cachedWeightsMesh != mesh)
                    {
                        _cachedWeightsBoneIndex = _selectedBoneIndex;
                        _cachedWeightsMesh = mesh;
                        _cachedVertexWeights = ComputeVertexWeights(mesh, _selectedBoneIndex);
                    }

                    // 写入 SelectionManager（仅在状态变化时触发重绘）
                    var selManager = ModelBoxSelectionManager.Instance;
                    if (selManager != null)
                    {
                        selManager.BoneWeightMode = _displayMode;
                        selManager.BoneVertexWeights = _cachedVertexWeights;
                        selManager.BoneWeightThreshold = _weightThreshold;
                        selManager.BoneWeightTargetSMR = _cachedSourceSMR; // [fix] 指定目标 SMR

                        // [fix H6] 仅在模式或阈值变化时触发 SceneView 重绘
                        if (_lastSyncedMode != _displayMode || _lastSyncedThreshold != _weightThreshold)
                        {
                            _lastSyncedMode = _displayMode;
                            _lastSyncedThreshold = _weightThreshold;
                            EditorApplication.delayCall += () => SceneView.RepaintAll();
                        }
                    }

                    // 热力图图例
                    EditorGUILayout.Space(2);
                    var legendColor = GUI.contentColor;
                    GUI.contentColor = EditorGUIUtility.isProSkin
                        ? new Color(0.6f, 0.6f, 0.6f) : new Color(0.4f, 0.4f, 0.4f);
                    EditorGUILayout.LabelField("蓝色 = 0 权重 → 绿色 = 0.5 → 红色 = 1.0", EditorStyles.miniLabel);
                    GUI.contentColor = legendColor;
                }
                else
                {
                    // 关闭可视化
                    var selManager = ModelBoxSelectionManager.Instance;
                    if (selManager != null)
                    {
                        selManager.BoneWeightMode = BoneWeightDisplayMode.Off;
                        selManager.BoneWeightTargetSMR = null;
                    }
                }
            }
            else if (bones != null && bones.Length > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox("点击上方骨骼列表中的骨骼名称，查看其权重统计。", MessageType.Info);
            }
            else
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox("Mesh 没有骨骼权重数据。", MessageType.Info);
            }
        }

        private static void DrawStatRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(90));
            var prevColor = GUI.contentColor;
            GUI.contentColor = EditorGUIUtility.isProSkin
                ? new Color(0.9f, 0.9f, 0.5f) : new Color(0.5f, 0.45f, 0.1f);
            EditorGUILayout.LabelField(value, EditorStyles.miniLabel);
            GUI.contentColor = prevColor;
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 计算指定骨骼对每个顶点的权重值。
        /// 返回 float[]，长度 = mesh.vertexCount。
        /// </summary>
        private static float[] ComputeVertexWeights(Mesh mesh, int boneIndex)
        {
            var boneWeights = mesh.boneWeights;
            if (boneWeights == null || boneWeights.Length == 0) return null;

            var result = new float[boneWeights.Length];
            for (int vi = 0; vi < boneWeights.Length; vi++)
            {
                float w = 0f;
                if (boneWeights[vi].boneIndex0 == boneIndex) w = boneWeights[vi].weight0;
                else if (boneWeights[vi].boneIndex1 == boneIndex) w = boneWeights[vi].weight1;
                else if (boneWeights[vi].boneIndex2 == boneIndex) w = boneWeights[vi].weight2;
                else if (boneWeights[vi].boneIndex3 == boneIndex) w = boneWeights[vi].weight3;
                result[vi] = w;
            }
            return result;
        }
    }
}
