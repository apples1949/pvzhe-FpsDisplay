# FPS 显示（FpsDisplay）

> 在屏幕四角之一显示**实时 FPS**（纯数字）。

| 项目 | 内容 |
| --- | --- |
| Mod ID | `fpsdisplay` |
| 程序集 | `JTYFpsDisplay` → 包内 `Runtime/ModAssembly.dll` |
| 入口类 | `FpsDisplayEntry` |
| 当前版本 | 1.0.0 |
| 分类 | 工具与前置 |

## 内部开关（源码顶部，改完重新构建）

```csharp
/// 显示在哪个角
internal enum Corner { TopLeft = 0, TopRight = 1, BottomLeft = 2, BottomRight = 3 }
private const Corner Where = Corner.TopLeft;   // ★ 默认左上

private const int   FontSize          = 22;    // 字号
private const float MarginPx          = 10f;   // 离屏幕边缘的距离
private const int   UpdateEveryFrames = 8;     // 每多少帧刷一次数字
private static readonly Color TextColor = new Color(0.25f, 1.0f, 0.35f);  // 亮绿
```

数字带 **4px 黑描边**，在任何背景（草地 / 夜晚 / 关卡选择）上都清晰。

换角落只要改 `Where` 一个常量，重新构建即可：

```powershell
python mods\FpsDisplay\build_and_install.py --install
```

## 实现要点

| 点 | 做法 | 为什么 |
| --- | --- | --- |
| FPS 从哪来 | `Engine.GetFramesPerSecond()` | 引擎自带的滑动平均，不用自己数帧 |
| 承载节点 | 独立 `CanvasLayer`（`Layer = 128`） | 直接挂 `Control` 会被战斗场景自己的 CanvasLayer / 地图**盖住** |
| 不吃点击 | `MouseFilter = Ignore` | 不会挡住下面的按钮 |
| 位置计算 | **每帧按视口尺寸重算**（不用锚点） | 换分辨率、拉窗口、横竖版切换都能自动跟上 |
| 刷新频率 | 每 8 帧写一次文本 | 每帧改 Label 会触发重排，没必要 |

## 铁律（本项目已多次踩过）

1. **手写 csproj ⇒ 没有 Godot 源码生成器** ⇒ 自定义 Node 子类的 `_Process` / `_Ready`
   **根本不会被调用**。全部逻辑走 `SceneTree.Connect("process_frame", Callable.From(Action))`。
2. `Callable` **不能**隐式转 `Action`（只有反向可以）⇒ 用 `Connect(名字, callable)`。
3. 入口三回调（`Initialize` / `OnAllModsLoaded` / `Shutdown`）一律 try/catch 吞异常 ——
   抛出会导致**整包回滚**。

## 硬护栏（违反会整包被拒）

1. `mod.json` 必须在**根**且**唯一**
2. `Runtime/` 下**只允许** `ModAssembly.dll`
3. 包内**绝不允许**出现 `.cs`（`ModLoader.IsExecutablePackageFile` 白名单拒收）

## 目录结构

```
FpsDisplay/
├── mod.json
├── build_and_install.py      编译 → 打包 → 装机
├── build_pmod.py             只打包（带 resources 一致性护栏）
├── runtime_src/
│   ├── FpsDisplay.csproj
│   └── FpsDisplayEntry.cs    入口
├── Runtime/ModAssembly.dll   打包用（编译产物改名而来）
└── dist/FpsDisplay.pmod      成品
```
