using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ModelBox
{
    /// <summary>
    /// URP 一键安装/卸载管理器。
    /// 通过 SerializedObject 操作 URP Renderer Asset 的 m_RendererFeatures 数组，
    /// 程序化注入 ModelBoxRendererFeature。
    ///
    /// RUNTIME-2 修正：Feature 作为 sub-asset 保存到 RendererData 中，域重载后不会丢失。
    /// </summary>
    public static class ModelBoxURPSetup
    {
        private const string FeatureName = "ModelBoxRendererFeature";

        /// <summary>
        /// 检查 Feature 是否已安装到当前激活的 URP Renderer。
        /// </summary>
        public static bool IsSetupComplete()
        {
            var rendererData = GetActiveRendererData();
            if (rendererData == null) return false;

            return rendererData.rendererFeatures
                .Any(f => f != null && f.GetType() == typeof(ModelBoxRendererFeature));
        }

        /// <summary>
        /// 一键添加 Feature 到当前 URP Renderer。
        /// [fix v0.4] 同时安装到所有 Renderer Data（支持多 Renderer 项目）。
        /// </summary>
        public static bool AddFeatureToActiveRenderer()
        {
            var allRendererDatas = GetAllRendererDatas();
            if (allRendererDatas.Count == 0)
            {
                var rendererData = GetActiveRendererData();
                if (rendererData == null)
                {
                    Debug.LogWarning("[ModelBox] 无法获取 URP Renderer Data。请确认项目使用 URP。");
                    return false;
                }
                allRendererDatas.Add(rendererData);
            }

            bool anyAdded = false;
            foreach (var rendererData in allRendererDatas)
            {
                bool alreadyInstalled = rendererData.rendererFeatures
                    .Any(f => f != null && f.GetType() == typeof(ModelBoxRendererFeature));
                if (alreadyInstalled) continue;

                var feature = ScriptableObject.CreateInstance<ModelBoxRendererFeature>();
                feature.name = FeatureName;
                feature.hideFlags = HideFlags.HideInInspector;

                string rendererDataPath = AssetDatabase.GetAssetPath(rendererData);
                if (!string.IsNullOrEmpty(rendererDataPath))
                    AssetDatabase.AddObjectToAsset(feature, rendererDataPath);

                var so = new SerializedObject(rendererData);
                var featuresProp = so.FindProperty("m_RendererFeatures");

                if (featuresProp == null)
                {
                    Debug.LogError($"[ModelBox] 无法访问 {rendererData.name} 的 m_RendererFeatures 属性。");
                    Object.DestroyImmediate(feature, true);
                    continue;
                }

                featuresProp.arraySize++;
                var element = featuresProp.GetArrayElementAtIndex(featuresProp.arraySize - 1);
                element.objectReferenceValue = feature;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(rendererData);
                anyAdded = true;
                Debug.Log($"[ModelBox] Feature 已添加到 Renderer: {rendererData.name}");
            }

            if (!anyAdded)
            {
                Debug.Log("[ModelBox] Feature 已存在于所有 Renderer，无需重复安装。");
                return true;
            }

            EditorApplication.delayCall += () =>
            {
                AssetDatabase.SaveAssets();
                Debug.Log("[ModelBox] Feature 安装完成，资产已保存。");
            };

            EnsureDepthTexture();
            Debug.Log("[ModelBox] 安装成功！ModelBoxRendererFeature 已添加到所有 URP Renderer。");
            return true;
        }

        /// <summary>
        /// 卸载 Feature。
        /// </summary>
        public static bool RemoveFeatureFromActiveRenderer()
        {
            var rendererData = GetActiveRendererData();
            if (rendererData == null) return true;

            var so = new SerializedObject(rendererData);
            var featuresProp = so.FindProperty("m_RendererFeatures");

            if (featuresProp == null) return true;

            bool removed = false;
            for (int i = featuresProp.arraySize - 1; i >= 0; i--)
            {
                var element = featuresProp.GetArrayElementAtIndex(i);
                if (element.objectReferenceValue is ModelBoxRendererFeature)
                {
                    var obj = element.objectReferenceValue;
                    element.objectReferenceValue = null;
                    featuresProp.DeleteArrayElementAtIndex(i);
                    if (obj != null)
                        Object.DestroyImmediate(obj, true);
                    removed = true;
                }
            }

            if (removed)
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(rendererData);
                AssetDatabase.SaveAssets();
                Debug.Log("[ModelBox] 卸载完成。");
            }

            return true;
        }

        /// <summary>
        /// 确保 URP 启用深度纹理（Depth 模式需要 _CameraDepthTexture）。
        /// </summary>
        public static void EnsureDepthTexture()
        {
            var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urpAsset == null) return;

            if (!urpAsset.supportsCameraDepthTexture)
            {
                urpAsset.supportsCameraDepthTexture = true;
                EditorUtility.SetDirty(urpAsset);
                Debug.Log("[ModelBox] 已自动启用 URP 深度纹理（supportsCameraDepthTexture = true）。");
            }
        }

        /// <summary>
        /// 确保 URP 启用 Opaque Texture（OpaqueTexture 模式需要 _CameraOpaqueTexture）。
        /// 如果未启用，采样 _CameraOpaqueTexture 会得到灰白 fallback 纹理，
        /// 这正是用户看到"灰白类似深度图"的根因。
        /// </summary>
        public static void EnsureOpaqueTexture()
        {
            var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urpAsset == null) return;

            if (!urpAsset.supportsCameraOpaqueTexture)
            {
                urpAsset.supportsCameraOpaqueTexture = true;
                EditorUtility.SetDirty(urpAsset);
                Debug.Log("[ModelBox] 已自动启用 URP Opaque Texture（supportsCameraOpaqueTexture = true）。");
            }
        }

        /// <summary>
        /// 获取当前活跃 URP Renderer 的 ScriptableRendererData。
        /// </summary>
        private static ScriptableRendererData GetActiveRendererData()
        {
            var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urpAsset == null) return null;

            var so = new SerializedObject(urpAsset);
            var dataListProp = so.FindProperty("m_RendererDataList");
            var indexProp = so.FindProperty("m_DefaultRendererIndex");

            if (dataListProp == null || indexProp == null) return null;

            int defaultIndex = indexProp.intValue;
            if (defaultIndex < 0 || defaultIndex >= dataListProp.arraySize) return null;

            return dataListProp.GetArrayElementAtIndex(defaultIndex)
                .objectReferenceValue as ScriptableRendererData;
        }

        /// <summary>
        /// [fix v0.4] 获取所有 URP Renderer Data（支持多 Renderer 项目）。
        /// 某些项目使用多个 Renderer（如主相机和 UI 各用一个），需要全部安装 Feature。
        /// </summary>
        private static List<ScriptableRendererData> GetAllRendererDatas()
        {
            var results = new List<ScriptableRendererData>();
            var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urpAsset == null) return results;

            var so = new SerializedObject(urpAsset);
            var dataListProp = so.FindProperty("m_RendererDataList");
            if (dataListProp == null) return results;

            for (int i = 0; i < dataListProp.arraySize; i++)
            {
                var data = dataListProp.GetArrayElementAtIndex(i)
                    .objectReferenceValue as ScriptableRendererData;
                if (data != null)
                    results.Add(data);
            }

            return results;
        }
    }
}
