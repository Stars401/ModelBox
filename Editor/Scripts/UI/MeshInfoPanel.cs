using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// Mesh 信息面板。显示选中物体的网格基础信息：
    /// 顶点数、三角面数、子网格数、UV 通道、法线/切线/顶点颜色、Bounds。
    /// </summary>
    public class MeshInfoPanel
    {
        private Mesh _lastMesh;

        // [perf] Mesh 变化时一次性缓存的通道信息，避免每帧分配大数组
        private bool _hasNormals, _hasTangents, _hasColors, _hasBindposes, _hasBoneWeights;
        private readonly bool[] _hasUV = new bool[4];
        private readonly int[] _uvDim = new int[4]; // 0=无, 2=2D, 3=3D

        public void Draw()
        {
            EditorGUILayout.LabelField("Mesh 信息", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            var selected = Selection.activeTransform;
            if (selected == null)
            {
                EditorGUILayout.HelpBox("请在 Scene 中选择一个物体。", MessageType.Info);
                return;
            }

            var mesh = GetMeshFromRenderer(selected);
            if (mesh == null)
            {
                EditorGUILayout.HelpBox("选中物体没有可访问的 Mesh。", MessageType.Info);
                return;
            }

            // [perf] 缓存失效检测：Mesh 变化时用零分配 API 一次性查询通道信息
            if (mesh != _lastMesh)
            {
                _lastMesh = mesh;

                _hasNormals = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal);
                _hasTangents = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent);
                _hasColors = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color);

                // bindposes / boneWeights 无 HasVertexAttribute 等价，仅在 Mesh 变化时访问一次（可接受）
                _hasBindposes = mesh.bindposes != null && mesh.bindposes.Length > 0;
                _hasBoneWeights = mesh.boneWeights != null && mesh.boneWeights.Length > 0;

                for (int i = 0; i < 4; i++)
                {
                    var attr = UnityEngine.Rendering.VertexAttribute.TexCoord0 + i;
                    _hasUV[i] = mesh.HasVertexAttribute(attr);
                    _uvDim[i] = _hasUV[i] ? mesh.GetVertexAttributeDimension(attr) : 0;
                }
            }

            // Mesh 名称 + 分隔线
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Mesh", EditorStyles.boldLabel, GUILayout.Width(42));
            EditorGUILayout.SelectableLabel(mesh.name, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

            // 基础统计
            DrawStatsGrid(mesh);

            EditorGUILayout.Space(4);

            // Bounds 信息
            DrawBounds(mesh);

            // [feat] 子网格详情（多材质槽支持）
            if (mesh.subMeshCount > 1)
            {
                EditorGUILayout.Space(4);
                var renderer = selected.GetComponentInChildren<Renderer>();
                DrawSubmeshDetails(mesh, renderer);
            }

            // [feat] LOD 层级信息（仅当物体有 LODGroup 时显示）
            var lodGroup = selected.GetComponentInParent<LODGroup>();
            if (lodGroup != null)
            {
                EditorGUILayout.Space(4);
                DrawLODInfo(lodGroup);
            }

            EditorGUILayout.Space(4);

            // 数据通道可用性检查
            DrawDataChannels(mesh);

            EditorGUILayout.Space(4);

            // Appdata 参数列表
            DrawAppdataInfo(mesh);
        }

        private void DrawStatsGrid(Mesh mesh)
        {
            EditorGUILayout.LabelField("基础统计", EditorStyles.boldLabel);

            // [fix] 使用 GUILayout 自动宽度而非 currentViewWidth（减去侧边栏后溢出）
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawStatRow("顶点数", mesh.vertexCount.ToString("N0"));
            int totalTris = 0;
            for (int si = 0; si < mesh.subMeshCount; si++)
                totalTris += (int)(mesh.GetIndexCount(si) / 3);
            DrawStatRow("三角面数", totalTris.ToString("N0"));
            DrawStatRow("子网格数", mesh.subMeshCount.ToString());
            EditorGUILayout.EndVertical();

            GUILayout.Space(8);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawStatRow("索引格式", mesh.indexFormat.ToString());
            // [fix] 多子网格可能有不同拓扑，全部列出
            DrawStatRow("拓扑类型", GetTopologySummary(mesh));
            DrawStatRow("Blend Shape 数", mesh.blendShapeCount.ToString());
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawStatRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(90));
            var prevColor = GUI.contentColor;
            GUI.contentColor = EditorGUIUtility.isProSkin
                ? new Color(0.9f, 0.9f, 0.5f)
                : new Color(0.5f, 0.45f, 0.1f);
            EditorGUILayout.LabelField(value, EditorStyles.miniLabel);
            GUI.contentColor = prevColor;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawDataChannels(Mesh mesh)
        {
            EditorGUILayout.LabelField("数据通道", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // [perf] 使用缓存值而非每帧访问 mesh.normals 等（会分配大数组）
            DrawChannelStatus("法线 (Normal)", _hasNormals);
            DrawChannelStatus("切线 (Tangent)", _hasTangents);
            DrawChannelStatus("顶点颜色 (Color)", _hasColors);

            // UV 通道（显示维度而非重复的顶点数）
            for (int i = 0; i < 4; i++)
                DrawChannelStatus($"UV{i}", _hasUV[i], _hasUV[i] ? $"{_uvDim[i]}D" : "无数据");

            DrawChannelStatus("Bindpose", _hasBindposes);
            DrawChannelStatus("骨骼权重 (BoneWeight)", _hasBoneWeights);

            EditorGUILayout.EndVertical();
        }

        private void DrawChannelStatus(string name, bool available, string extraInfo = null)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(name, EditorStyles.miniLabel);

            var prevColor = GUI.contentColor;
            if (available)
            {
                GUI.contentColor = new Color(0.4f, 0.9f, 0.4f);
                string status = "✓ 有";
                if (!string.IsNullOrEmpty(extraInfo))
                    status += $" ({extraInfo})";
                EditorGUILayout.LabelField(status, EditorStyles.miniLabel, GUILayout.Width(150));
            }
            else
            {
                GUI.contentColor = new Color(0.6f, 0.6f, 0.6f);
                EditorGUILayout.LabelField("✗ 无", EditorStyles.miniLabel, GUILayout.Width(150));
            }
            GUI.contentColor = prevColor;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawBounds(Mesh mesh)
        {
            EditorGUILayout.LabelField("包围盒 (Bounds)", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            var bounds = mesh.bounds;
            var prevColor = GUI.contentColor;
            GUI.contentColor = EditorGUIUtility.isProSkin
                ? new Color(0.7f, 0.7f, 0.7f)
                : new Color(0.35f, 0.35f, 0.35f);
            EditorGUILayout.LabelField($"中心: ({bounds.center.x:F3}, {bounds.center.y:F3}, {bounds.center.z:F3})", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"尺寸: ({bounds.size.x:F3}, {bounds.size.y:F3}, {bounds.size.z:F3})", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Extents: ({bounds.extents.x:F3}, {bounds.extents.y:F3}, {bounds.extents.z:F3})", EditorStyles.miniLabel);
            GUI.contentColor = prevColor;
            EditorGUILayout.EndVertical();
        }

        private void DrawAppdataInfo(Mesh mesh)
        {
            EditorGUILayout.LabelField("Appdata 语义映射", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Shader 中可用的顶点输入：", EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // POSITION — 顶点位置（总是存在）
            DrawAppdataRow("POSITION", "float3", "顶点位置", true);

            // [perf] 使用缓存值替代 mesh.normals != null 等每帧分配
            // NORMAL — 法线
            DrawAppdataRow("NORMAL", "float3", "法线方向", _hasNormals);

            // TANGENT — 切线
            DrawAppdataRow("TANGENT", "float4", "切线方向 + 副切线符号(w)", _hasTangents);

            // TEXCOORD0-3 — UV
            for (int i = 0; i < 4; i++)
            {
                bool hasUV = _hasUV[i];
                int dim = _uvDim[i];
                string typeName = hasUV ? $"float{Mathf.Min(dim, 4)}" : "---";
                string desc = hasUV ? $"UV{i}（{dim}D）" : $"UV{i}（未使用）";
                DrawAppdataRow($"TEXCOORD{i}", typeName, desc, hasUV);
            }

            // COLOR — 顶点颜色
            DrawAppdataRow("COLOR", "float4", "顶点颜色", _hasColors);

            EditorGUILayout.EndVertical();
        }

        private void DrawAppdataRow(string semantic, string type, string description, bool available)
        {
            EditorGUILayout.BeginHorizontal();

            // 语义名
            var prevColor = GUI.contentColor;
            GUI.contentColor = available ? new Color(0.5f, 0.8f, 1f) : new Color(0.5f, 0.5f, 0.5f);
            EditorGUILayout.LabelField(semantic, EditorStyles.miniLabel, GUILayout.Width(100));

            // 类型
            GUI.contentColor = available ? new Color(0.9f, 0.85f, 0.5f) : new Color(0.5f, 0.5f, 0.5f);
            EditorGUILayout.LabelField(type, EditorStyles.miniLabel, GUILayout.Width(55));

            // 描述
            GUI.contentColor = available ? new Color(0.8f, 0.8f, 0.8f) : new Color(0.5f, 0.5f, 0.5f);
            EditorGUILayout.LabelField(description, EditorStyles.miniLabel);

            // 状态
            GUI.contentColor = available ? new Color(0.4f, 0.9f, 0.4f) : new Color(0.6f, 0.3f, 0.3f);
            EditorGUILayout.LabelField(available ? "✓" : "✗", EditorStyles.miniLabel, GUILayout.Width(20));

            GUI.contentColor = prevColor;
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>汇总多子网格的拓扑类型（如 "Triangles" 或 "Triangles + Lines"）。</summary>
        private static string GetTopologySummary(Mesh mesh)
        {
            if (mesh.subMeshCount <= 1) return mesh.GetTopology(0).ToString();
            var seen = new System.Collections.Generic.HashSet<MeshTopology>();
            for (int i = 0; i < mesh.subMeshCount; i++)
                seen.Add(mesh.GetTopology(i));
            if (seen.Count == 1) return mesh.GetTopology(0).ToString();
            var sb = new System.Text.StringBuilder();
            foreach (var t in seen) { if (sb.Length > 0) sb.Append(" + "); sb.Append(t); }
            return sb.ToString();
        }

        /// <summary>子网格详情：显示每个 submesh 的材质/面数/拓扑（多材质槽模型专用）。</summary>
        private void DrawSubmeshDetails(Mesh mesh, Renderer renderer)
        {
            EditorGUILayout.LabelField($"子网格详情 ({mesh.subMeshCount})", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var mats = renderer != null ? renderer.sharedMaterials : null;
            var prevColor = GUI.contentColor;

            for (int si = 0; si < mesh.subMeshCount; si++)
            {
                int triCount = (int)(mesh.GetIndexCount(si) / 3);
                string matName = mats != null && si < mats.Length && mats[si] != null
                    ? mats[si].name : "(无材质)";
                string topo = mesh.GetTopology(si).ToString();

                EditorGUILayout.BeginHorizontal();
                GUI.contentColor = EditorGUIUtility.isProSkin
                    ? new Color(0.7f, 0.85f, 1f) : new Color(0.2f, 0.35f, 0.6f);
                EditorGUILayout.LabelField($"SubMesh {si}", EditorStyles.miniLabel, GUILayout.Width(70));
                GUI.contentColor = EditorGUIUtility.isProSkin
                    ? new Color(0.9f, 0.9f, 0.5f) : new Color(0.5f, 0.45f, 0.1f);
                EditorGUILayout.LabelField(matName, EditorStyles.miniLabel, GUILayout.MinWidth(80));
                GUI.contentColor = EditorGUIUtility.isProSkin
                    ? new Color(0.7f, 0.7f, 0.7f) : new Color(0.35f, 0.35f, 0.35f);
                EditorGUILayout.LabelField($"{triCount:N0} tri", EditorStyles.miniLabel, GUILayout.Width(70));
                EditorGUILayout.LabelField(topo, EditorStyles.miniLabel, GUILayout.Width(60));
                GUI.contentColor = prevColor;
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>LOD 层级信息：显示各级 LOD 的面数/材质/屏幕占比，高亮当前活跃级别。</summary>
        private void DrawLODInfo(LODGroup lodGroup)
        {
            var lods = lodGroup.GetLODs();
            if (lods == null || lods.Length == 0) return;

            // 计算当前 LOD 级别（基于 SceneView 相机距离和 FOV）
            int activeLOD = -1;
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null && sceneView.camera != null)
            {
                float dist = Vector3.Distance(sceneView.camera.transform.position, lodGroup.transform.position);
                float fov = sceneView.camera.fieldOfView;
                float screenHeight = 2f * dist * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
                float relativeSize = screenHeight > 0 ? lodGroup.size / screenHeight : 0;
                for (int i = 0; i < lods.Length; i++)
                {
                    if (relativeSize >= lods[i].screenRelativeTransitionHeight)
                    {
                        activeLOD = i;
                        break;
                    }
                }
                if (activeLOD < 0) activeLOD = lods.Length - 1; // 最低 LOD
            }

            EditorGUILayout.LabelField($"LOD Group ({lods.Length} 级)", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var prevColor = GUI.contentColor;
            for (int i = 0; i < lods.Length; i++)
            {
                var lod = lods[i];
                bool isActive = (i == activeLOD);

                // 统计该级别下所有 Renderer 的面数和材质数
                int triSum = 0, vertSum = 0, matSum = 0;
                if (lod.renderers != null)
                {
                    foreach (var r in lod.renderers)
                    {
                        if (r == null) continue;
                        var m = r.GetComponent<MeshFilter>()?.sharedMesh;
                        if (m != null)
                        {
                            for (int si = 0; si < m.subMeshCount; si++)
                                triSum += (int)(m.GetIndexCount(si) / 3);
                            vertSum += m.vertexCount;
                        }
                        matSum += r.sharedMaterials?.Length ?? 0;
                    }
                }

                // 行样式：活跃级别高亮
                if (isActive)
                    GUI.contentColor = new Color(0.4f, 1f, 0.4f); // 绿色高亮
                else
                    GUI.contentColor = EditorGUIUtility.isProSkin
                        ? new Color(0.7f, 0.7f, 0.7f) : new Color(0.35f, 0.35f, 0.35f);

                EditorGUILayout.BeginHorizontal();
                string label = isActive ? $"► LOD {i}" : $"  LOD {i}";
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(55));
                EditorGUILayout.LabelField($"{lod.screenRelativeTransitionHeight:P0}", EditorStyles.miniLabel, GUILayout.Width(40));
                EditorGUILayout.LabelField($"{triSum:N0} tri", EditorStyles.miniLabel, GUILayout.Width(70));
                EditorGUILayout.LabelField($"{vertSum:N0} vert", EditorStyles.miniLabel, GUILayout.Width(70));
                EditorGUILayout.LabelField($"{matSum} mat", EditorStyles.miniLabel, GUILayout.Width(50));
                EditorGUILayout.EndHorizontal();
            }
            GUI.contentColor = prevColor;

            // Culled 信息
            EditorGUILayout.BeginHorizontal();
            GUI.contentColor = new Color(0.6f, 0.4f, 0.4f);
            EditorGUILayout.LabelField("  Culled", EditorStyles.miniLabel, GUILayout.Width(55));
            EditorGUILayout.LabelField("< threshold", EditorStyles.miniLabel, GUILayout.Width(80));
            GUI.contentColor = prevColor;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private Mesh GetMeshFromRenderer(Transform t)
        {
            var mf = t.GetComponentInChildren<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) return mf.sharedMesh;

            var smr = t.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null) return smr.sharedMesh;

            return null;
        }
    }
}
