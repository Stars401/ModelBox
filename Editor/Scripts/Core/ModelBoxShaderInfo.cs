using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
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
            var passNames = ParsePassNames(shader);
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
            if (enable)
                material.EnableKeyword(keywordName);
            else
                material.DisableKeyword(keywordName);

            // 更新内部状态
            for (int i = 0; i < Keywords.Count; i++)
            {
                var kw = Keywords[i];
                if (kw.Name == keywordName)
                {
                    kw.IsEnabled = enable;
                    Keywords[i] = kw;
                    break;
                }
            }
            EnabledKeywordCount = 0;
            foreach (var kw in Keywords)
                if (kw.IsEnabled) EnabledKeywordCount++;
        }

        /// <summary>
        /// 切换 pass 启用状态。
        /// </summary>
        public void ApplyPassChange(Material material, string passName, bool enabled)
        {
            if (material == null) return;
            material.SetShaderPassEnabled(passName, enabled);

            for (int i = 0; i < Passes.Count; i++)
            {
                var p = Passes[i];
                if (p.Name == passName)
                {
                    p.IsEnabled = enabled;
                    Passes[i] = p;
                    break;
                }
            }
            EnabledPassCount = 0;
            foreach (var p in Passes)
                if (p.IsEnabled) EnabledPassCount++;
        }

        /// <summary>
        /// 从 shader 文件文本中解析 pass 名称。
        /// </summary>
        private static List<string> ParsePassNames(Shader shader)
        {
            var names = new List<string>();
            var path = AssetDatabase.GetAssetPath(shader);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var text = File.ReadAllText(path);
                // 匹配 Pass { ... Name "xxx" ... } 中的 Name
                // 简化：直接匹配所有 Name "xxx"（跳过注释行）
                var lines = text.Split('\n');
                bool inPass = false;
                int braceDepth = 0;
                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();

                    // 跳过注释和 UsePass 指令
                    if (line.StartsWith("//") || line.StartsWith("UsePass"))
                        continue;

                    if (!line.Contains("UsePass") && line.Contains("Pass") && (line.Contains("{") || line.EndsWith("{")))
                    {
                        inPass = true;
                        braceDepth = 0;
                    }

                    if (inPass)
                    {
                        foreach (char c in line)
                        {
                            if (c == '{') braceDepth++;
                            if (c == '}') braceDepth--;
                        }

                        var match = Regex.Match(line, @"Name\s+""([^""]+)""");
                        if (match.Success)
                        {
                            var name = match.Groups[1].Value;
                            if (!names.Contains(name))
                                names.Add(name);
                        }

                        if (braceDepth <= 0)
                            inPass = false;
                    }
                }
            }

            // Fallback：使用索引命名
            if (names.Count == 0)
            {
                int passCount = shader.passCount;
                for (int i = 0; i < passCount; i++)
                    names.Add($"pass_{i}");
            }

            return names;
        }
    }
}
