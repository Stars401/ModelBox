using System;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// 每个调试模式的可调参数，传递给 Debug Shader。
    /// </summary>
    [Serializable]
    public struct ModelBoxParameters
    {
        [Tooltip("缩放系数（WorldPos=可视范围米, LocalPos=frac网格密度）")]
        public float Scale;

        [Range(-5f, 5f)]
        [Tooltip("颜色偏移量")]
        public float Offset;

        [Range(0.1f, 5f)]
        [Tooltip("Gamma 校正值")]
        public float Gamma;

        [Range(0.1f, 1000f)]
        [Tooltip("深度可视化最大范围（米）")]
        public float DepthRange;

        [Range(2f, 64f)]
        [Tooltip("UV 棋盘格密度")]
        public float CheckerGridSize;

        [Tooltip("颜色映射模式：0=原始, 1=灰度, 2=热力图, 3=彩虹")]
        public int ColorMapMode;

        [Range(1f, 32f)]
        [Tooltip("Overdraw 热力图最大显示次数")]
        public float OverdrawMaxCount;

        [Range(8f, 256f)]
        [Tooltip("Ray March 最大步进次数")]
        public float MaxSteps;

        [Range(0.01f, 10f)]
        [Tooltip("Ray March 步进长度（米）")]
        public float StepSize;

        public static ModelBoxParameters Default => new ModelBoxParameters
        {
            Scale = 1f,
            Offset = 0f,
            Gamma = 1f,
            DepthRange = 100f,
            CheckerGridSize = 10f,
            ColorMapMode = 0,
            OverdrawMaxCount = 8f,
            MaxSteps = 64f,
            StepSize = 0.5f,
        };

        /// <summary>
        /// 将参数应用到 Debug Material 的 Shader 属性。
        /// </summary>
        public void ApplyToMaterial(Material mat)
        {
            if (mat == null) return;
            mat.SetFloat("_DebugScale", Scale);
            mat.SetFloat("_DebugOffset", Offset);
            mat.SetFloat("_DebugGamma", Gamma);
            mat.SetFloat("_DebugDepthRange", DepthRange);
            mat.SetInt("_ColorMapMode", ColorMapMode);
            mat.SetFloat("_OverdrawMaxCount", OverdrawMaxCount);
            mat.SetFloat("_MaxSteps", MaxSteps);
            mat.SetFloat("_StepSize", StepSize);
        }

        public override bool Equals(object obj)
        {
            if (obj is ModelBoxParameters other)
                return Scale == other.Scale && Offset == other.Offset
                    && Gamma == other.Gamma && DepthRange == other.DepthRange
                    && CheckerGridSize == other.CheckerGridSize
                    && ColorMapMode == other.ColorMapMode
                    && OverdrawMaxCount == other.OverdrawMaxCount
                    && MaxSteps == other.MaxSteps && StepSize == other.StepSize;
            return false;
        }

        public override int GetHashCode()
        {
            return Scale.GetHashCode() ^ (Offset.GetHashCode() << 2)
                 ^ (Gamma.GetHashCode() >> 2) ^ (DepthRange.GetHashCode() >> 1)
                 ^ (CheckerGridSize.GetHashCode() << 4) ^ (ColorMapMode.GetHashCode() << 6)
                 ^ (OverdrawMaxCount.GetHashCode() << 8)
                 ^ (MaxSteps.GetHashCode() << 10) ^ (StepSize.GetHashCode() << 12);
        }
    }
}
