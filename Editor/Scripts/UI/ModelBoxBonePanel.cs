using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// 骨骼 / 蒙皮 信息面板。
    /// 从 ModelBoxSelectionInspector 提取，在左侧分类栏独立展示。
    /// 功能：骨骼层级树、逐骨骼权重统计、Blend Shape 计数、权重可视化。
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
        private float _weightOpacity = 1.0f;
        private float[] _cachedVertexWeights;
        private int _cachedWeightsBoneIndex = -1;
        private Mesh _cachedWeightsMesh;
        private SkinnedMeshRenderer _cachedSourceSMR;
        private BoneWeightDisplayMode _lastSyncedMode = BoneWeightDisplayMode.Off;
        private float _lastSyncedThreshold = -1f;

        // [feat] 骨骼层级树
        private struct BoneNode
        {
            public int Index;
            public string Name;
            public List<int> ChildIndices;
            public bool HasParentInBones;
        }

        private BoneNode[] _boneNodes;
        private List<int> _boneRootIndices;
        private HashSet<int> _collapsedBones = new HashSet<int>();
        private SkinnedMeshRenderer _cachedTreeSMR;
        private bool _showBoneGizmos = true; // 默认开启，进入骨骼页即可看到骨骼

        // [feat] 在 Draw() 首次调用时订阅 SceneView 点击选骨骼事件
        private bool _subscribedBoneEvent;

        // [fix] 清理事件订阅（由 ModelBoxWindow.OnDisable / AssemblyReload 调用）
        public void Cleanup()
        {
            if (_subscribedBoneEvent)
            {
                ModelBoxSelectionManager.OnBoneSelectedInScene -= OnBoneSelectedInScene;
                _subscribedBoneEvent = false;
            }
        }

        private void EnsureSubscribed()
        {
            if (_subscribedBoneEvent) return;
            ModelBoxSelectionManager.OnBoneSelectedInScene += OnBoneSelectedInScene;
            _subscribedBoneEvent = true;
        }

        private void OnBoneSelectedInScene(int boneIndex)
        {
            _selectedBoneIndex = boneIndex;
            // 强制重新计算权重统计
            _cachedBoneIndex = -1;
            // 强制重新计算权重可视化
            _cachedWeightsBoneIndex = -1;
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>
        /// 构建骨骼层级树。通过 Transform.parent 关系判断父子层级。
        /// </summary>
        private void BuildBoneTree(SkinnedMeshRenderer smr)
        {
            var bones = smr.bones;
            if (bones == null || bones.Length == 0)
            {
                _boneNodes = null;
                _boneRootIndices = null;
                return;
            }

            // 构建 Transform → boneIndex 映射
            var boneMap = new Dictionary<Transform, int>();
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null) boneMap[bones[i]] = i;
            }

            _boneNodes = new BoneNode[bones.Length];
            _boneRootIndices = new List<int>();

            // Pass 1: initialize all nodes first — struct array elements default to null ChildIndices
            for (int i = 0; i < bones.Length; i++)
            {
                _boneNodes[i] = new BoneNode
                {
                    Index = i,
                    Name = bones[i] != null ? bones[i].name : "(null)",
                    ChildIndices = new List<int>(),
                    HasParentInBones = false
                };
            }

            // Pass 2: build parent-child relationships (all ChildIndices are now non-null)
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null)
                {
                    _boneRootIndices.Add(i);
                    continue;
                }

                var parent = bones[i].parent;
                if (parent != null && boneMap.TryGetValue(parent, out int parentIdx))
                {
                    _boneNodes[i].HasParentInBones = true;
                    _boneNodes[parentIdx].ChildIndices.Add(i);
                }

                if (!_boneNodes[i].HasParentInBones)
                    _boneRootIndices.Add(i);
            }
        }

        /// <summary>
        /// 递归渲染骨骼层级树节点。
        /// </summary>
        private void DrawBoneNode(int index, int depth, bool hasFilter, string filterLower)
        {
            var node = _boneNodes[index];
            var bone = _cachedSourceSMR.bones[index];

            // 搜索过滤：如果当前骨骼不匹配，检查子骨骼是否有匹配的
            bool nameMatches = !hasFilter || node.Name.ToLower().Contains(filterLower);
            bool childMatches = false;
            if (hasFilter)
            {
                foreach (var ci in node.ChildIndices)
                {
                    if (DoesSubtreeMatch(ci, filterLower))
                    {
                        childMatches = true;
                        break;
                    }
                }
            }

            if (hasFilter && !nameMatches && !childMatches) return;

            bool isCollapsed = _collapsedBones.Contains(index);
            bool isSelected = (_selectedBoneIndex == index);

            EditorGUILayout.BeginHorizontal();

            // 缩进
            GUILayout.Space(depth * 14);

            // 展开/折叠按钮
            if (node.ChildIndices.Count > 0 && !hasFilter)
            {
                var prevBG = GUI.backgroundColor;
                if (GUILayout.Button(isCollapsed ? "▶" : "▼", EditorStyles.miniButton, GUILayout.Width(18), GUILayout.Height(16)))
                {
                    if (isCollapsed) _collapsedBones.Remove(index);
                    else _collapsedBones.Add(index);
                }
                GUI.backgroundColor = prevBG;
            }
            else
            {
                GUILayout.Space(18);
            }

            // 骨骼名称按钮
            var prevColor = GUI.contentColor;
            if (isSelected)
                GUI.contentColor = new Color(0.4f, 1f, 0.4f);
            else if (depth == 0)
                // 根骨骼：金色
                GUI.contentColor = EditorGUIUtility.isProSkin
                    ? new Color(1f, 0.85f, 0.4f)
                    : new Color(0.6f, 0.45f, 0.1f);
            else
                GUI.contentColor = EditorGUIUtility.isProSkin
                    ? new Color(0.75f, 0.75f, 0.75f)
                    : new Color(0.3f, 0.3f, 0.3f);

            string prefix = isSelected ? "►" : (node.ChildIndices.Count > 0 ? "" : "·");
            if (GUILayout.Button($"{prefix} [{index}] {node.Name}", EditorStyles.miniLabel))
            {
                _selectedBoneIndex = isSelected ? -1 : index;
                // [feat] 立即同步到 SelectionManager（避免 delayCall 时序问题）
                var sm = ModelBoxSelectionManager.Instance;
                if (sm != null) sm.SelectedBoneIndex = _selectedBoneIndex;
                if (!isSelected && bone != null)
                    EditorGUIUtility.PingObject(bone.gameObject);
                if (_displayMode != BoneWeightDisplayMode.Off || _showBoneGizmos)
                    EditorApplication.delayCall += () => SceneView.RepaintAll();
            }

            GUI.contentColor = prevColor;
            EditorGUILayout.EndHorizontal();

            // 递归渲染子骨骼（搜索时自动展开）
            if (!isCollapsed || hasFilter)
            {
                foreach (var ci in node.ChildIndices)
                    DrawBoneNode(ci, depth + 1, hasFilter, filterLower);
            }
        }

        /// <summary>递归检查子树是否有骨骼名称匹配搜索词。</summary>
        private bool DoesSubtreeMatch(int index, string filterLower)
        {
            if (_boneNodes[index].Name.ToLower().Contains(filterLower)) return true;
            foreach (var ci in _boneNodes[index].ChildIndices)
                if (DoesSubtreeMatch(ci, filterLower)) return true;
            return false;
        }

        public void Draw()
        {
            EnsureSubscribed();
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

            // [feat] 构建骨骼层级树（SMR 变化时重建 + 重置状态）
            if (_cachedTreeSMR != smr)
            {
                _cachedTreeSMR = smr;
                _selectedBoneIndex = -1;
                _cachedBoneIndex = -1;
                _collapsedBones.Clear();
                BuildBoneTree(smr);
            }

            // [feat] 同步骨骼 gizmo 状态到 SelectionManager
            var selManager = ModelBoxSelectionManager.Instance;
            if (selManager != null)
            {
                selManager.ShowBoneGizmos = _showBoneGizmos;
                selManager.SelectedBoneIndex = _selectedBoneIndex;
                selManager.BoneWeightTargetSMR = _cachedSourceSMR;
            }

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

            // [feat] 交互提示
            EditorGUILayout.HelpBox(
                "点击骨骼名称或 SceneView 中的骨骼球体选中 → 高亮（橙色球+坐标轴+名称）\n" +
                "点击 ▼/▶ 展开/折叠子骨骼 · 搜索框可按名称过滤",
                MessageType.None);

            // [feat] 骨骼 gizmo 开关 + 全部展开/折叠
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _showBoneGizmos = EditorGUILayout.ToggleLeft(
                new GUIContent("在 Scene 中显示骨骼", "在 SceneView 中显示骨骼位置标记和父子连线"),
                _showBoneGizmos);
            if (EditorGUI.EndChangeCheck())
            {
                var sm = ModelBoxSelectionManager.Instance;
                if (sm != null) sm.ShowBoneGizmos = _showBoneGizmos;
                EditorApplication.delayCall += () => SceneView.RepaintAll();
            }
            if (_boneNodes != null && _boneRootIndices != null && _boneRootIndices.Count > 0)
            {
                if (GUILayout.Button("全部展开", EditorStyles.miniButton, GUILayout.Width(56)))
                {
                    _collapsedBones.Clear();
                    EditorApplication.delayCall += () => SceneView.RepaintAll();
                }
                if (GUILayout.Button("全部折叠", EditorStyles.miniButton, GUILayout.Width(56)))
                {
                    _collapsedBones.Clear();
                    for (int i = 0; i < _boneNodes.Length; i++)
                        if (_boneNodes[i].ChildIndices.Count > 0)
                            _collapsedBones.Add(i);
                    EditorApplication.delayCall += () => SceneView.RepaintAll();
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _boneSearchFilter = EditorGUILayout.TextField(_boneSearchFilter, EditorStyles.toolbarSearchField);
            if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22)))
                _boneSearchFilter = "";
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);

            // ===== 骨骼层级树 =====
            if (_boneNodes != null && _boneRootIndices != null && _boneRootIndices.Count > 0)
            {
                _boneScrollPos = EditorGUILayout.BeginScrollView(_boneScrollPos, GUILayout.MaxHeight(300));

                bool hasFilter = !string.IsNullOrEmpty(_boneSearchFilter);
                string filterLower = hasFilter ? _boneSearchFilter.ToLower() : "";

                foreach (var rootIdx in _boneRootIndices)
                {
                    DrawBoneNode(rootIdx, 0, hasFilter, filterLower);
                }

                EditorGUILayout.EndScrollView();
            }
            else if (bones != null && bones.Length > 0)
            {
                // 树构建失败（骨骼全为 null）—— 显示提示
                EditorGUILayout.HelpBox("骨骼数据异常：bones 数组非空但无法构建层级树。", MessageType.Warning);
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
                _displayMode = (BoneWeightDisplayMode)EditorGUILayout.EnumPopup(
                    new GUIContent("显示模式", "ColorMap: 顶点颜色热力图（蓝=0→红=1）\nThreshold: 仅显示权重大于阈值的顶点"),
                    _displayMode);

                if (_displayMode == BoneWeightDisplayMode.Threshold)
                    _weightThreshold = EditorGUILayout.Slider(
                        new GUIContent("权重阈值", "仅显示权重大于此值的顶点"),
                        _weightThreshold, 0f, 1f);

                if (_displayMode == BoneWeightDisplayMode.ColorMap)
                    _weightOpacity = EditorGUILayout.Slider(
                        new GUIContent("不透明度", "权重表面的透明度。降低可同时看到原始材质"),
                        _weightOpacity, 0.1f, 1f);

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
                    // 复用外层已声明的 selManager（line 261）
                    if (selManager != null)
                    {
                        selManager.BoneWeightMode = _displayMode;
                        selManager.BoneVertexWeights = _cachedVertexWeights;
                        selManager.BoneWeightThreshold = _weightThreshold;
                        selManager.BoneWeightOpacity = _weightOpacity;
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
                    // 关闭权重可视化（但不清除 SMR — 骨骼 gizmo 仍可能需要它）
                    // 复用外层已声明的 selManager（line 261）
                    if (selManager != null)
                    {
                        selManager.BoneWeightMode = BoneWeightDisplayMode.Off;
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
