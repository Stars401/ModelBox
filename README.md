# modelBox — Unity URP 实时 Shader 调试可视化工具

> **版本:** v0.3.2 | **作者:** Sky幻 | **Unity:** 2021.3+ | **管线:** URP 12.0+

## 概述

modelBox 为 Shader 和图形开发者提供**运行时实时、全场景覆盖**的调试可视化能力。一键切换 35 种调试视图，在 SceneView 中直接观察 Shader 中间数据，无需断点、无需修改任何 Shader 源码。

### 核心特性

- **零侵入**：不修改你的 Shader，不增加 Shader Variant，纯 Editor 工具构建时自动剥离
- **35 种调试模式**：覆盖几何数据、诊断、PBR 分析、光照分离、导数诊断 5 大类别
- **分屏对比**：左侧正常渲染 + 右侧调试模式，支持冻结快照和 A/B 帧对比
- **选区调试**：对选中物体单独进行材质覆盖（贴图通道/棋盘格/属性颜色）和网格叠加（线框/顶点/法线/切线/AABB）
- **骨骼面板**：独立侧边栏页面，骨骼层级树、逐骨骼权重统计、搜索过滤
- **Shader 信息面板**：查看/切换 Keywords（绿色高亮已启用）、Passes、Properties（可编辑 + Undo）
- **Mesh 信息面板**：顶点数、UV 通道、子网格材质分配、LOD 层级预览
- **材质沙盒**：A/B 材质参数 Diff，支持实时编辑和应用到原始
- **像素拾取**：PixelBar 实时显示鼠标指向的物体/Shader/坐标/自定义属性
- **GPU 加速叠加**：实例化顶点球体 + LOD 距离优化 + 深度测试开关
- **可配置 HUD**：性能统计逐项开关（FPS [BETA] / DC / Tri / Vert / 总分配 / C#堆）
- **一键安装/卸载**：自动配置 URP RendererFeature

---

## 安装

### 方式一：Package Manager（推荐）

1. `Window > Package Manager > + > Add package from disk...`
2. 选择本工具根目录（包含 `package.json` 的文件夹）

### 方式二：Packages 目录

将整个文件夹复制到项目的 `Packages/com.unity.modelbox/` 目录下。

### 首次配置

1. 打开 `Tools > modelBox`
2. 点击 **"一键安装 URP 集成"**
3. 工具自动将 `ModelBoxRendererFeature` 添加到当前活跃的 URP Renderer Asset
4. 自动启用 URP Depth Texture 和 Opaque Texture

---

## 快速开始

1. `Tools > modelBox` 打开主窗口
2. `Alt+1` 切换到世界坐标模式
3. `Alt+0` 开关调试
4. `Alt+S` 开启分屏对比
5. SceneView 底部查看像素信息

---

## 主窗口（6 个侧边栏页面）

| 页面 | 图标 | 功能 |
|------|------|------|
| **检查** | In | 选中物体总览 + 快速叠加开关 + SEL/分屏/冻结控制 |
| **场景** | Sc | 35 种调试模式选择 + 参数调节 |
| **物体** | Ob | 选区调试（贴图通道/UV棋盘格/Shader属性）+ Shader 信息 |
| **沙盒** | Sb | 材质 A/B 对比测试 |
| **骨骼** | Bo | 骨骼层级树 + 蒙皮权重统计 + 权重可视化（热力图/过滤模式） |
| **网格** | Me | 网格详情 + 子网格材质 + LOD 层级预览 |
| **设置** | St | 安装/卸载、UI 偏好、HUD 配置、快捷键参考 |

---

## 调试模式（35 种）

### 常用

| 模式 | 说明 |
|------|------|
| 世界坐标 (WP) | 世界坐标 → 归一化 RGB（Scale 控制可视范围） |
| 世界法线 (WN) | 世界法线 → RGB（`N * 0.5 + 0.5` 映射） |
| 深度 (Dp) | 深度 → 灰度（从深度缓冲重建，ScreenSpace 路径） |
| 顶点颜色 (VC) | 顶点颜色 → RGBA |
| 线框 (WF) | 线框叠加（选中物体 + 暗色法线底色） |

### 几何

| 模式 | 说明 |
|------|------|
| 模型坐标 (LP) | 模型坐标 → frac 周期性彩色网格 |
| 模型法线 (LN) | 模型法线 → RGB |
| 平面法线 (FN) | 平面法线（ddx/ddy 交叉积，检查平滑组/硬边） |
| 切线方向 (T) | 切线方向 → RGB（检查 UV X 轴 / MikkTSpace） |
| 副切线方向 (B) | 副切线方向 → RGB（检查 UV Y 轴 / 切线手性） |
| 物体 ID (ID) | 每物体唯一颜色（Transform 矩阵哈希 → HSV 着色） |

### 纹理

| 模式 | 说明 |
|------|------|
| UV0 (U0) | 第一套 UV → RG 通道 |
| UV1 (U1) | 第二套 UV → RG 通道 |
| 不透明纹理 (OT) | 采样 _CameraOpaqueTexture（双 Pass Capture+Draw） |
| Mipmap 级别 (Mip) | UV 导数 → Mip 等级估算（纹理密度检查） |
| 屏幕 UV (SU) | 屏幕空间 UV → RG（检查 ComputeScreenPos） |

### 光照

| 模式 | 说明 |
|------|------|
| 漫反射 (Df) | 柔和 Lambert（中性灰 albedo，无高光） |
| 高光 (Sp) | Blinn-Phong 高光（NdotH^64） |
| 仅光照 (LO) | 仅光照无纹理（漫反射+高光+环境光） |
| NdotL (NL) | Lambert 漫反射：`dot(N, LightDir)` |
| NdotV (NV) | 视角对齐：`dot(N, ViewDir)` |
| Fresnel (Fr) | Schlick 菲涅尔近似：`pow(1-NdotV, 5)` |
| 粗糙度 (Rg) | 粗糙度影响可视化 |
| 金属度 (Mt) | 金属度影响可视化 |

### 诊断

| 模式 | 说明 |
|------|------|
| Overdraw (OD) | Overdraw 热力图（计数 shader + 热力图 blit） |
| 透明层数 (TL) | 透明物体层数热力图 |
| 阴影贴图 (SM) | 阴影贴图可视化（级联着色） |
| 屏幕法线 (SN) | 屏幕空间法线（从深度缓冲 ddx/ddy 重建） |
| 原始深度 (RD) | 原始深度值彩色编码 |
| 物体深度 (ObjD) | 物体自身深度（近亮远暗） |
| 纯色测试 (PC) | 纯品红色（确认 Shader 是否在执行） |
| 几何密度 (Geo) | 世界坐标导数 → 每像素三角形密度 |
| 天光曝光 (Sky) | 天光曝光近似（AO + 法线朝向） |
| 射线步进 (RM) | 从深度缓冲重建世界坐标后沿射线步进，热力图输出 |

---

## 分屏对比

- `Alt+S` 开启/关闭分屏
- 左侧正常渲染，右侧调试模式
- 拖拽分割线调整比例（5%~95%），双击重置 50%
- **冻结**：锁定左侧快照，自由切换右侧调试模式
- **快照 A/B**：SA 保存当前画面到 A，SB 保存到 B，循环切换对比模式

---

## 选区调试

对选中的特定物体应用材质覆盖，独立于全场景调试。

### 材质覆盖模式

| 模式 | 说明 |
|------|------|
| **贴图通道** | 隔离 R/G/B/A/RGB 通道，支持自定义贴图、UV 通道切换、世界坐标 UV、亮度范围钳制 |
| **UV 棋盘格** | 棋盘格图案检查 UV 展开，可调网格密度和颜色 |
| **Shader 属性颜色** | 指定 Shader 属性 → 纯色（支持 Color/Float，一键读取） |

### 网格叠加

| 叠加类型 | 说明 |
|---------|------|
| **线框** | 三角面边线，GPU 批量渲染 + LOD 距离优化 |
| **顶点** | GPU 实例化球体标记（1023/batch），支持深度测试开关 |
| **法线** | 顶点法线方向线，支持粗线（DrawAAPolyLine） |
| **切线** | 顶点切线方向线 |
| **AABB** | 世界空间包围盒线框（12 条边） |

> 同时支持 `MeshRenderer` 和 `SkinnedMeshRenderer`（BakeMesh 烘焙后叠加）。
> 深度测试可在设置页切换：关闭后叠加点可穿透模型显示。

---

## 骨骼面板

独立侧边栏页面（「骨骼」），功能包括：

- 骨骼概览：骨骼数量、根骨骼、Blend Shapes、Bindpose
- 骨骼层级树：可滚动列表，点击骨骼在 Scene 中高亮
- 搜索过滤：按骨骼名称快速过滤
- 权重统计：选中骨骼后显示受影响顶点数、最大权重、平均权重
- **权重可视化**（两种模式，用户单选）：
  - **顶点颜色模式 (ColorMap)**：所有顶点按权重热力图着色（蓝=0 → 绿=0.5 → 红=1）
  - **顶点过滤模式 (Threshold)**：仅显示权重 > 阈值的顶点，可调阈值滑条
- 临时材质管理：可视化结束自动清理，不影响原始材质

---

## Shader 信息面板

选中场景中的物体后显示 Shader 内部信息：

- **Keywords**：`multi_compile` / `shader_feature` 分类，绿色高亮已启用项，「仅显示已启用」过滤器
- **Passes**：所有 Pass 名称，可启用/禁用
- **Properties**：所有属性（类型/值/描述/标志），支持搜索 + 编辑 + Undo

---

## Mesh 信息面板

- 顶点/三角形/子网格/索引格式/拓扑
- 子网格材质分配详情（SubMesh 0: Body_Mat | 12,000 tri）
- LOD 层级预览（LOD Group 各级别面数/材质数/当前活跃级别高亮）
- 数据通道可用性（法线/切线/顶点色/UV0-3/骨骼权重）

---

## 快捷键

所有快捷键仅在 SceneView 获得焦点时生效。

| 快捷键 | 功能 |
|--------|------|
| `Alt+0` | Toggle 调试开/关 |
| `Alt+1` ~ `Alt+9` | 常用模式 + 诊断模式 |
| `Alt+Shift+1` ~ `Alt+Shift+9` | 高级/PBR 模式 |
| `Alt+,` | 上一个模式（循环） |
| `Alt+.` | 下一个模式（循环） |
| `Alt+S` | 分屏对比开关 |

可在 `Edit > Shortcuts` 中自定义（搜索 "modelBox" 分组）。

---

## 设置

| 设置 | 说明 |
|------|------|
| SceneView 工具栏 | 显示/隐藏浮动工具栏 |
| 像素拾取提示 | 鼠标悬浮时显示像素信息 |
| 像素条 (Pixel Bar) | SceneView 底部信息条 |
| 性能统计 HUD | 逐项开关（DC / Tri / Vert / 总分配 / C#堆） |
| FPS [BETA] | 帧率显示（Editor 中为近似值，仅作参考） |
| 叠加深度测试 | 网格叠加是否被模型遮挡（关闭=透视显示） |

---

## 技术架构

### 三层架构

```
┌─ UI 层 ──────────────────────────────────────────────────────────────┐
│  ModelBoxWindow (6 页面 EditorWindow: 检查/场景/物体/沙盒/骨骼/设置)  │
│  ModelBoxSceneOverlay (SceneView 浮动工具栏, 分组下拉 + 分屏/冻结)   │
│  ModelBoxBonePanel (骨骼侧边栏独立页面)                               │
│  ModelBoxPixelBar (底部像素信息条)                                    │
│  PixelInspector (鼠标悬浮像素拾取)                                    │
│  ModelBoxShortcuts (Alt+数字键 + Alt+S)                              │
├─ 管理层 ─────────────────────────────────────────────────────────────┤
│  ModelBoxManager (Singleton, 模式/参数/Undo/分屏/冻结/快照)          │
│  ModelBoxSettings (ScriptableObject 持久化)                          │
│  ModelBoxSelectionManager (选区材质覆盖 + 网格叠加)                   │
│  IModelBoxRenderer (管线抽象接口)                                     │
├─ 渲染注入层 ─────────────────────────────────────────────────────────┤
│  ModelBoxRendererFeature (URP ScriptableRendererFeature)             │
│  ├── ModelBoxGeometryPass     (overrideMaterial DrawRenderers)       │
│  ├── ModelBoxNormalSceneCapturePass (分屏: 捕获正常场景)             │
│  ├── ModelBoxDebugSceneCapturePass  (分屏: 捕获调试输出)             │
│  ├── ModelBoxSplitCompositePass     (分屏: 左右合成 + 快照 A/B)      │
│  ├── ScreenSpaceBlitPass      (深度→世界坐标全屏 Blit)               │
│  ├── ModelBoxOpaqueTextureDrawPass (OpaqueTexture 可视化)            │
│  ├── ModelBoxOverdrawDrawPass (Overdraw 热力图)                      │
│  └── ModelBoxShadowMapPass    (阴影贴图可视化)                       │
└──────────────────────────────────────────────────────────────────────┘
```

### 渲染路径

| 模式类型 | 渲染路径 | 说明 |
|---------|---------|------|
| Geometry (29 种) | `overrideMaterial` + `DrawRenderers` | 单 Pass，统一调试 Shader |
| ScreenSpace (3 种) | 全屏 Blit + 深度重建 | Depth, ScreenNormal, RayMarch |
| Capture+Draw (3 种) | 双 Pass | OpaqueTexture, Overdraw, TransparencyLayers |
| Overlay (5 种) | Handles API + Shader 底色 | Wireframe, Vertices, Normals, Tangents, Bounds |
| Split Composite | 全屏三角形合成 | 左正常 + 右调试 + 快照 A/B |

### Shader 技术决策

使用 `switch(_DebugMode)` uniform branch 而非 `#pragma multi_compile`：
- 单个 Shader 变体覆盖全部 35 种模式
- Uniform branch 无 warp divergence（所有 fragment 走同一分支）
- 编译时间短，Variant 数不膨胀
- 运行时实时切换，无需重新编译

---

## 文件结构

```
com.unity.modelbox/
├── package.json
├── README.md
├── Editor/
│   ├── ModelBox.Editor.asmdef
│   ├── Scripts/
│   │   ├── Core/
│   │   │   ├── ModelBoxManager.cs              # 核心单例 + 状态管理
│   │   │   ├── ModelBoxViewMode.cs             # 35 种调试模式枚举
│   │   │   ├── ModelBoxOverlayMode.cs          # 选区模式 + 叠加标志枚举
│   │   │   ├── ModelBoxParameters.cs           # 参数结构体
│   │   │   ├── ModelBoxSettings.cs             # ScriptableObject 持久化
│   │   │   ├── IModelBoxRenderer.cs            # 管线抽象接口
│   │   │   ├── ModelBoxSelectionManager.cs     # 选区调试管理器
│   │   │   ├── ModelBoxMeshCache.cs            # 网格数据缓存
│   │   │   ├── ModelBoxOverlayRenderer.cs      # GPU 叠加渲染器
│   │   │   └── ModelBoxShaderInfo.cs           # Shader 信息分析
│   │   ├── UI/
│   │   │   ├── ModelBoxWindow.cs               # 主 EditorWindow (6 页面)
│   │   │   ├── ModelBoxSceneOverlay.cs         # SceneView 浮动工具栏
│   │   │   ├── ModelBoxModeSelector.cs         # 模式选择 UI
│   │   │   ├── ModelBoxParameterControls.cs    # 参数调节 UI
│   │   │   ├── ModelBoxPixelBar.cs             # 像素信息条
│   │   │   ├── ModelBoxSelectionInspector.cs   # 选区调试 UI (4 子标签)
│   │   │   ├── ModelBoxBonePanel.cs            # 骨骼侧边栏面板
│   │   │   ├── ShaderInfoPanel.cs              # Shader 信息面板
│   │   │   ├── MeshInfoPanel.cs                # Mesh 信息面板
│   │   │   ├── MaterialDiffPanel.cs            # 材质 Diff 面板
│   │   │   └── TexturePreviewWindow.cs         # 纹理预览窗口
│   │   ├── RenderInjection/URP/
│   │   │   ├── ModelBoxRendererFeature.cs      # URP RendererFeature (8 Pass)
│   │   │   ├── ModelBoxGeometryPass.cs         # Geometry + Capture Passes
│   │   │   ├── ModelBoxSplitCompositePass.cs   # 分屏合成 + 快照 A/B
│   │   │   ├── ScreenSpaceBlitPass.cs          # 屏幕空间 Blit Pass
│   │   │   └── ModelBoxURPSetup.cs             # 一键安装/卸载
│   │   ├── Shortcuts/
│   │   │   └── ModelBoxShortcuts.cs            # 快捷键定义
│   │   ├── PixelPicking/
│   │   │   └── PixelInspector.cs               # 像素拾取
│   │   └── Utility/
│   │       ├── ModelBoxStyles.cs               # 样式 + 颜色常量
│   │       ├── PipelineDetector.cs             # 管线检测
│   │       └── ScreenshotCapture.cs            # 截图工具
│   └── Shaders/
│       ├── DebugReplacement_Geometry.shader    # 主调试 Shader（35 模式 switch）
│       ├── DebugReplacement_Overdraw.shader    # Overdraw 计数
│       ├── DebugReplacement_Checkerboard.shader
│       ├── DebugReplacement_UniformColor.shader
│       ├── DebugReplacement_TextureChannel.shader
│       ├── DebugReplacement_Wireframe.shader
│       ├── DebugBlit_OpaqueTexture.shader
│       ├── DebugBlit_ScreenNormal.shader
│       ├── DebugBlit_ScreenSpaceFromDepth.shader  # Depth + ScreenNormal + RayMarch
│       ├── DebugBlit_OverdrawHeatmap.shader
│       ├── DebugBlit_ShadowMap.shader
│       ├── DebugBlit_SplitComposite.shader     # 分屏合成 + 快照 A/B
│       └── DebugOverlay_Vertex.shader          # 顶点球体（可配置深度测试）
└── Tests/Editor/
    ├── ModelBox.Editor.Tests.asmdef
    ├── ModelBoxManagerTests.cs
    ├── ModelBoxViewModeTests.cs
    ├── ModelBoxSelectionTests.cs
    ├── ModelBoxShaderInfoTests.cs
    └── ModelBoxPipelineTests.cs
```

---

## 已知限制

| 限制 | 说明 | 缓解方案 |
|------|------|---------|
| **仅 URP** | HDRP / Built-in 暂不支持 | 各需 2-3 周，评估需求后考虑 |
| **仅不透明物体** | 透明物体、UI Canvas 不在覆盖范围 | TransparencyLayers 模式可统计透明层叠 |
| **顶点动画丢失** | overrideMaterial 替换整个 Shader，自定义顶点动画丢失 | 这是 overrideMaterial 的根本限制 |
| **FPS [BETA]** | Editor 中帧率基于时间差近似，非精确帧计数 | 设置中可关闭，仅作参考 |
| **Overdraw 精度** | R8 格式饱和于 16 次绘制 | 可通过 Overdraw Max 滑条调整灵敏度 |
| **像素拾取精度** | SceneView 中使用 Raycast 降级方案 | CPU 端近似值，非 GPU 实际输出 |

---

## 版本历史

### v0.3.2（当前）
- 骨骼权重可视化：顶点颜色热力图模式 + 顶点过滤模式（可调阈值）
- Mesh 信息独立侧边栏页面
- 场景调试控制补全：SEL/分屏/冻结在检查页也可用
- 模式分类统一：工具栏与模式选择器使用一致的 6+1 分组
- Critical 修复：Undo 竞态条件（int 代际计数器）、事件 handler 域重载泄漏
- 性能优化：消除 5 项 GC 分配（StringBuilder/GUIStyle/GUIContent/string[]）
- FPS 计算修复（EditorApplication.timeSinceStartup + dt 钳制 + BETA 标记）
- BeginChangeCheck/EndChangeCheck GUI 状态栈泄漏修复
- package.json 作者署名：Sky幻

### v0.3.1
- 骨骼侧边栏独立页面（搜索过滤、层级树、权重统计）
- 顶点覆盖深度测试开关（设置页 toggle）
- 冻结左侧快照 + 快照 A/B 对比模式
- 性能 HUD 逐项配置（FPS [BETA] / DC / Tri / Vert / 总分配 / C#堆）
- 分屏分割线边缘吸附（5%/50%/95%）
- 模式按钮中文名 + 最近模式下拉中文名
- Shader Keyword 绿色高亮已启用 + 「仅显示已启用」过滤器
- FPS 计算修复（EditorApplication.timeSinceStartup 替代 Time.unscaledDeltaTime）

### v0.2.2
- 分屏 A/B 对比模式（Alt+S）
- RayMarch 射线步进可视化
- LOD 层级预览
- 性能 HUD 渲染统计（DC/Tri/Vert）
- 骨骼权重预览面板
- SEL 按钮过滤仅选中物体
- 工具栏分类重组 + 可拖拽位置
- PixelBar 独立模式 + 三级 Renderer 回退

### v0.2.0
- 35 种调试模式（从 14 种扩展）
- PBR 诊断、光照分离、导数诊断、屏幕空间模式
- SceneView 工具栏 5 组下拉菜单
- GPU 加速叠加渲染

### v0.1.0
- 初始版本：9 种全场景 Debug View + URP RendererFeature 注入

---

## 卸载

1. 打开 `Tools > modelBox`
2. 切换到 Settings 页面
3. 点击 **"卸载 URP 集成"**
4. 通过 Package Manager 移除包
