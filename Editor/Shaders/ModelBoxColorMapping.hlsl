#ifndef MODELBOX_COLOR_MAPPING_INCLUDED
#define MODELBOX_COLOR_MAPPING_INCLUDED

// 颜色映射函数（HeatMap / Rainbow / ApplyColorMap）
// 被 DebugReplacement_Geometry.shader 和 DebugBlit_ScreenSpaceFromDepth.shader 共享

half3 HeatMap(float t)
{
    // 黑 → 蓝 → 绿 → 黄 → 红
    half3 c = half3(0, 0, 0);
    c.r = smoothstep(0.5, 0.8, t);
    c.g = t < 0.5 ? smoothstep(0.0, 0.5, t) : smoothstep(1.0, 0.5, t);
    c.b = smoothstep(0.0, 0.3, t) * (1.0 - smoothstep(0.3, 0.6, t));
    return c;
}

half3 Rainbow(float t)
{
    // 红 → 黄 → 绿 → 青 → 蓝 → 紫
    half3 c;
    c.r = smoothstep(0.0, 0.25, t) * (1.0 - smoothstep(0.75, 1.0, t));
    c.g = smoothstep(0.0, 0.5, t) * (1.0 - smoothstep(0.5, 1.0, t));
    c.b = smoothstep(0.25, 0.75, t) * (1.0 - smoothstep(0.75, 1.0, t));
    return saturate(c);
}

half3 ApplyColorMap(half3 input, int mode)
{
    if (mode == 1) // Gray
    {
        float g = dot(input, half3(0.299, 0.587, 0.114));
        return half3(g, g, g);
    }
    else if (mode == 2) // HeatMap
    {
        float v = dot(input, half3(0.299, 0.587, 0.114));
        return HeatMap(saturate(v));
    }
    else if (mode == 3) // Rainbow
    {
        float v = dot(input, half3(0.299, 0.587, 0.114));
        return Rainbow(saturate(v));
    }
    return input;
}

#endif // MODELBOX_COLOR_MAPPING_INCLUDED
