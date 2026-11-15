using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ModelBox
{
    /// <summary>
    /// Shader 信息数据模型。分析指定 Shader/Material 的 Keywords、Passes、Properties。
    /// </summary>
    public class ShaderInfoData
    {
        public Shader SourceShader;
        public string ShaderName;
        public List<KeywordInfo> Keywords = new List<KeywordInfo>();
        public List<PassInfo> Passes = new List<PassInfo>();
        public List<PropertyInfo> Properties = new List<PropertyInfo>();

        public struct KeywordInfo
        {
            public string Name;
            public bool IsOverridable; // true = shader_feature, false = multi_compile
            public bool IsEnabled;
        }

        public struct PassInfo
        {
            public string Name;
            public bool IsEnabled;
        }

        public struct PropertyInfo
        {
            public string Name;
            public string Description;
            public ShaderPropertyType Type;
            public ShaderPropertyFlags Flags;
            public Vector2 RangeLimits;
        }

        public int EnabledKeywordCount;
        public int EnabledPassCount;

        /// <summary>
        /// 分析指定 Shader 和 Material，构建完整数据模型。
        /// </summary>
        public static ShaderInfoData Analyze(Shader shader, Material material)
        {
            if (shader == null || material == null) return null;

            var data = new ShaderInfoData
            {
                SourceShader = shader,
                ShaderName = shader.name
            };

            // --- Keywords ---
            var enabledKeywords = new HashSet<string>(material.shaderKeywords);
            var keywordSpace = shader.keywordSpace;
            var keywords = keywordSpace.keywords;
            for (int i = 0; i < keywords.Length; i++)
            {
                var kw = keywords[i];
                data.Keywords.Add(new KeywordInfo
                {
                    Name = kw.name,
                    IsOverridable = kw.isOverridable,
                    IsEnabled = enabledKeywords.Contains(kw.name)
                });
                if (enabledKeywords.Contains(kw.name))
                    data.EnabledKeywordCount++;
            }

            // --- Passes ---
            var passNames = GetPassNames(material);
            for (int i = 0; i < passNames.Count; i++)
            {
                var passName = passNames[i];
                data.Passes.Add(new PassInfo
                {
                    Name = passName,
                    IsEnabled = material.GetShaderPassEnabled(passName)
                });
                if (material.GetShaderPassEnabled(passName))
                    data.EnabledPassCount++;
            }

            // --- Properties ---
            int propCount = shader.GetPropertyCount();
            for (int i = 0; i < propCount; i++)
            {
                var flags = shader.GetPropertyFlags(i);
                // 跳过隐藏属性
                if ((flags & ShaderPropertyFlags.HideInInspector) != 0)
                    continue;

                var propType = shader.GetPropertyType(i);
                data.Properties.Add(new PropertyInfo
                {
                    Name = shader.GetPropertyName(i),
                    Description = shader.GetPropertyDescription(i),
                    Type = propType,
                    Flags = flags,
                    RangeLimits = propType == ShaderPropertyType.Range ? shader.GetPropertyRangeLimits(i) : Vector2.zero
                });
            }

            return data;
        }

        /// <summary>
        /// 切换 keyword 状态。
        /// </summary>
        public void ApplyKeywordChange(Material material, string keywordName, bool enable)
        {
            if (material == null) return;
            Undo.RecordObject(material, "Shader Keyword Change");
            if (enable)
                material.EnableKeyword(keywordName);
            else
                material.DisableKeyword(keywordName);

            EditorUtility.SetDirty(material);
            RefreshMaterialState(material);
        }

        /// <summary>
        /// 切换 pass 启用状态。
        /// </summary>
        public void ApplyPassChange(Material material, string passName, bool enabled)
        {
            if (material == null) return;
            Undo.RecordObject(material, "Shader Pass Change");
            material.SetShaderPassEnabled(passName, enabled);
            EditorUtility.SetDirty(material);
            RefreshMaterialState(material);
        }

        /// <summary>同步可能由 Inspector 或 Undo 修改的材质状态，保留 Shader 结构缓存。</summary>
        public void RefreshMaterialState(Material material)
        {
            if (material == null) return;
            EnabledKeywordCount = 0;
            for (int i = 0; i < Keywords.Count; i++)
            {
                var kw = Keywords[i];
                kw.IsEnabled = material.IsKeywordEnabled(kw.Name);
                Keywords[i] = kw;
                if (kw.IsEnabled) EnabledKeywordCount++;
            }

            EnabledPassCount = 0;
            for (int i = 0; i < Passes.Count; i++)
            {
                var p = Passes[i];
                p.IsEnabled = material.GetShaderPassEnabled(p.Name);
                Passes[i] = p;
                if (p.IsEnabled) EnabledPassCount++;
            }
        }

        /// <summary>
        /// 从已编译材质读取实际 Pass 名称，不受 ShaderLab 排版影响。
        /// </summary>
        private static List<string> GetPassNames(Material material)
        {
            var names = new List<string>();
            for (int i = 0; i < material.passCount; i++)
            {
                var name = material.GetPassName(i);
                if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                    names.Add(name);
            }

            return names;
        }
    }
}
