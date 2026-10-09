# FPS 显示（FpsDisplay）

> 在屏幕四角之一显示**实时 FPS**（纯数字）。

| 项目 | 内容 |
| --- | --- |
| Mod ID | `fpsdisplay` |
| 程序集 | `JTYFpsDisplay` → 包内 `Runtime/ModAssembly.dll` |
| 入口类 | `FpsDisplayEntry` |
| 当前版本 | **1.0.1** |
| 分类 | 工具与前置 |

## 内部开关（源码顶部，改完重新构建）

```csharp
/// 显示在哪个角
internal enum Corner { TopLeft = 0, TopRight = 1, BottomLeft = 2, BottomRight = 3 }
private const Corner Where = Corner.TopLeft;   // ★ 默认左上

/// 诊断日志总开关（默认关）—— 关掉后本 Mod 不输出任何 GD.Print
private static readonly bool EnableLog = false;
/// 额外把每次算出的 FPS 值也打进日志（需 EnableLog 打开）
private static readonly bool LogFps = false;

private const double SampleWindowSec = 0.5;    // 统计窗口（秒）
private const int    FontSize        = 22;     // 字号
private const float  MarginPx        = 10f;    // 离屏幕边缘的距离
private static readonly Color TextColor = new Color(0.25f, 1.0f, 0.35f);  // 亮绿
```

数字带 **4px 黑描边**，在任何背景（草地 / 夜晚 / 关卡选择）上都清晰。

换角落只要改 `Where` 一个常量，重新构建即可：

```powershell
python mods\FpsDisplay\build_and_install.py --install
```

## 更新记录

| 版本 | 内容 |
| --- | --- |
| **1.0.1** | FPS 改为**自己按真实帧间隔统计**（见下），修手机端改 FPS 上限后数值不刷新的问题；新增 `EnableLog` / `LogFps` 日志开关（默认关） |
| 1.0.0 | 首版：四角可选、纯数字、黑描边 |

### 1.0.1 为什么不用 `Engine.GetFramesPerSecond()`

用户反馈：**手机端改了 FPS 上限后，显示的仍是旧值；PC 端正常。**

`Engine.GetFramesPerSecond()` 是引擎**内部**的帧率统计，与限帧 / vsync / 低功耗模式的实现耦合 ——
某些平台（尤其 Android 的低功耗模式）改上限后不一定及时跟上。

现在改成**按墙上时间自己统计**，与引擎内部实现彻底解耦：

```csharp
ulong nowMs = Time.GetTicksMsec();          // 真实毫秒，与引擎帧率实现无关
dt = (nowMs - _lastMs) / 1000.0;
_accTime += dt;  _accFrames++;

if (_accTime >= SampleWindowSec)             // 0.5 秒一个窗口
{
    _fps = (int)Math.Round(_accFrames / _accTime);   // 帧数 ÷ 真实耗时
    _accTime = 0; _accFrames = 0;
}
```

* `dt > 1.0` 的异常间隔（切场景 / 暂停造成的长帧）**不计入**，避免把 FPS 拉爆。
* 窗口 0.5 秒 ⇒ 上限改完**下一个窗口就会跳到新值**。

> ⚠️ **该修复仅在 Windows 端验证过**（构建环境只有 Windows 版）。
> 手机端行为属**基于根因分析的推理**，未经实机验证；如有反馈仍不刷新请继续回报。

## 实现要点

| 点 | 做法 | 为什么 |
| --- | --- | --- |
| FPS 从哪来 | `Time.GetTicksMsec()` 累计「帧数 ÷ 真实耗时」 | 与引擎限帧 / vsync / 低功耗模式解耦（见上） |
| 承载节点 | 独立 `CanvasLayer`（`Layer = 128`） | 直接挂 `Control` 会被战斗场景自己的 CanvasLayer / 地图**盖住** |
| 不吃点击 | `MouseFilter = Ignore` | 不会挡住下面的按钮 |
| 位置计算 | **每帧按视口尺寸重算**（不用锚点） | 换分辨率、拉窗口、横竖版切换都能自动跟上 |
| 日志 | `EnableLog` 门控 `Log()` | 正式包不带日志输出；`GD.PrintErr`（异常）不受开关影响 |

## 铁律（本项目已多次踩过）

1. **手写 csproj ⇒ 没有 Godot 源码生成器** ⇒ 自定义 Node 子类的 `_Process` / `_Ready`
   **根本不会被调用**。全部逻辑走 `SceneTree.Connect("process_frame", Callable.From(Action))`。
2. `Callable` **不能**隐式转 `Action`（只有反向可以）⇒ 用 `Connect(名字, callable)`。
3. 入口三回调（`Initialize` / `OnAllModsLoaded` / `Shutdown`）一律 try/catch 吞异常 ——
   抛出会导致**整包回滚**。
4. `Log()` 的方法体里**必须**写 `GD.Print`，绝不能写成 `Log` —— 批量替换时把方法体自己也换掉
   ⇒ 无限递归 ⇒ `StackOverflowException`（catch 抓不住，直接终止线程）。
5. 本类**不是 Node**，拿不到 `GetProcessDeltaTime()` 这类节点方法 ⇒ 计时用
   `Time.GetTicksMsec()`。

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
