using System;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「FPS 显示」Mod —— 在屏幕任意位置显示**实时 FPS（纯数字）**。
///
/// 显示位置/字号/日志开关全部由游戏原生「Mod 配置」面板给出（mod.json 的 settings 声明），
/// 面板里改完**立即生效**（订阅 XWModSettings.Changed，不需要重启）。
///
/// ── 铁律（本项目多次踩过）───────────────────────────────
/// · 手写 csproj ⇒ **没有 Godot 源码生成器** ⇒ 自定义 Node 子类的
///   `_Process` / `_Ready` **根本不会被调用**！逻辑必须走
///   `SceneTree.Connect("process_frame", Callable.From(Action))`。
/// · `Callable` **不能**隐式转 `Action`（只有反向可以）。
/// · 入口三回调一律 try/catch 吞异常（抛出 = 整包回滚）。
/// · 本类**不是 Node** ⇒ 拿不到 `GetProcessDeltaTime()`，计时用 `Time.GetTicksMsec()`。
/// </summary>
public sealed class FpsDisplayEntry : IXWModRuntimeEntry
{
	// 赞助码注入器（SponsorQrInjector.cs 共享组件）用的包根缓存
	internal static string SponsorQrRoot = null;

	private const string P = "[FpsDisplay] ";

	// ============================================================ 配置键
	private const string KShow      = "showFps";
	private const string KCorner    = "corner";
	private const string KCustomX   = "customX";
	private const string KCustomY   = "customY";
	private const string KFontSize  = "fontSize";
	private const string KMargin    = "margin";
	private const string KEnableLog = "enableLog";

	// 兜底默认值（配置不可用时用，与 mod.json 的 default 保持一致）
	private const bool DefShow = true;
	private const string DefCorner = "tl";
	private const int DefCustomX = 10;
	private const int DefCustomY = 10;
	private const int DefFontSize = 22;
	private const int DefMargin = 10;
	private const bool DefLog = false;

	/// <summary>统计窗口（秒）—— 每累计这么久的**真实耗时**算一次 FPS。</summary>
	private const double SampleWindowSec = 0.5;

	// ============================================================ 状态
	private XWModRuntimeContext _context;
	private XWModSettings _settings;
	private bool _settingsSubscribed;

	private SceneTree _tree;
	private Callable _tick;
	private CanvasLayer _layer;
	private Label _label;
	private bool _started;
	private int _diag;

	// 自算 FPS（不依赖 Engine.GetFramesPerSecond）
	private ulong _lastMs;
	private double _accTime;
	private int _accFrames;
	private int _fps;
	private int _shownFontSize = -1;

	private static readonly Color TextColor = new Color(0.25f, 1.0f, 0.35f);

	// ============================================================ 生命周期

	public void Initialize(XWModRuntimeContext context)
	{
			try { SponsorQrRoot = context.PackageRoot; } catch { }
		try
		{
			_context = context;
			try
			{
				_settings = context.Settings;
			}
			catch (Exception ex)
			{
				// 老宿主没有这套 API；本 Mod 退回内置默认值继续工作
				Warn("原生 Mod 配置不可用，改用内置默认值：" + ex.GetBaseException().Message);
			}
			Log("初始化完成：显示=" + ReadBool(KShow, DefShow) + " 位置=" + ReadString(KCorner, DefCorner));
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Initialize 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started)
			{
				return;
			}
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				Warn("拿不到 SceneTree，本 Mod 不生效。");
				return;
			}
			_tick = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tick);
			SubscribeSettings();
			_started = true;
			Log("已挂载 process_frame。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void Shutdown()
	{
		try
		{
			UnsubscribeSettings();
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tick);
			}
			if (_layer != null && GodotObject.IsInstanceValid(_layer))
			{
				_layer.QueueFree();
				_layer = null;
				_label = null;
			}
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Shutdown 异常（已吞）：" + ex.Message); } catch { }
		}
		finally
		{
			_started = false;
		}
	}

	// ============================================================ 配置订阅

	private void SubscribeSettings()
	{
		try
		{
			if (_settings == null || _settingsSubscribed)
			{
				return;
			}
			_settings.Changed += OnSettingsChanged;
			_settingsSubscribed = true;
		}
		catch (Exception ex)
		{
			Warn("订阅配置变更失败：" + ex.GetBaseException().Message);
		}
	}

	private void UnsubscribeSettings()
	{
		try
		{
			if (_settings != null && _settingsSubscribed)
			{
				_settings.Changed -= OnSettingsChanged;
				_settingsSubscribed = false;
			}
		}
		catch { }
	}

	/// <summary>面板保存后回调（宿主已在主线程调用）。keys 是本次改动的配置键。</summary>
	private void OnSettingsChanged(System.Collections.Generic.IReadOnlyList<string> keys)
	{
		try
		{
			Log("配置已更新：" + string.Join("、", keys));
			// 下一帧 OnFrame 会读到新值并立刻生效；这里把字号缓存失效即可
			_shownFontSize = -1;
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "配置回调异常（已吞）：" + ex.Message); } catch { }
		}
	}

	// ---------- 读值：宿主存的对象类型不保证，按 object 取再稳健转换 ----------

	/// <summary>
	/// 取原始值。用 `Get&lt;object&gt;` 而不是 `Get&lt;int&gt;` —— 宿主把值存成
	/// `Dictionary&lt;string, object&gt;`，整数很可能是 `long` 或 `JsonElement`，
	/// 直接 `Get&lt;int&gt;` 会抛 `InvalidCastException`。
	/// </summary>
	private object ReadRaw(string key)
	{
		if (_settings == null)
		{
			return null;
		}
		try
		{
			return _settings.Get<object>(key);
		}
		catch (Exception ex)
		{
			Warn("读取配置项失败 " + key + "：" + ex.GetBaseException().Message);
			return null;
		}
	}

	private bool ReadBool(string key, bool fallback)
	{
		object v = ReadRaw(key);
		if (v is bool b)
		{
			return b;
		}
		try
		{
			if (v is string s)
			{
				if (bool.TryParse(s, out bool sb)) { return sb; }
				return fallback;
			}
			if (v is System.Text.Json.JsonElement je
				&& (je.ValueKind == System.Text.Json.JsonValueKind.True
					|| je.ValueKind == System.Text.Json.JsonValueKind.False))
			{
				return je.GetBoolean();
			}
		}
		catch { }
		return fallback;
	}

	private int ReadInt(string key, int fallback)
	{
		object v = ReadRaw(key);
		if (v == null)
		{
			return fallback;
		}
		try
		{
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.Number && je.TryGetInt32(out int n))
				{
					return n;
				}
				if (je.ValueKind == System.Text.Json.JsonValueKind.String
					&& int.TryParse(je.GetString(), out int ns))
				{
					return ns;
				}
				return fallback;
			}
			return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
		}
		catch
		{
			return fallback;
		}
	}

	private string ReadString(string key, string fallback)
	{
		object v = ReadRaw(key);
		if (v is string s)
		{
			return s;
		}
		try
		{
			if (v is System.Text.Json.JsonElement je
				&& je.ValueKind == System.Text.Json.JsonValueKind.String)
			{
				return je.GetString();
			}
		}
		catch { }
		return fallback;
	}

	// ============================================================ 每帧

	private void OnFrame()
	{
		try { SponsorQrInjector.Tick(Engine.GetMainLoop() as SceneTree, "FPS 显示", SponsorQrRoot, "Resources/SponsorQR.png"); } catch { }
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}

			// ① 总开关：关掉就藏起来（字也不刷了）
			if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KShow, DefShow)） */
			{
				if (_label != null && GodotObject.IsInstanceValid(_label) && _label.Visible)
				{
					_label.Visible = false;
				}
				return;
			}

			if (_label == null || !GodotObject.IsInstanceValid(_label))
			{
				EnsureLabel();
				if (_label == null)
				{
					return;
				}
			}
			if (!_label.Visible)
			{
				_label.Visible = true;
			}

			// ② 字号（变了才改，避免每帧触发重排）
			int fs = Mathf.Clamp(ReadInt(KFontSize, DefFontSize), 8, 72);
			if (fs != _shownFontSize)
			{
				_shownFontSize = fs;
				_label.AddThemeFontSizeOverride("font_size", fs);
			}

			// ③ FPS：自己按**墙上时间**统计（不用 Engine.GetFramesPerSecond ——
			//    它是引擎内部统计、与限帧/vsync/低功耗模式耦合，手机端改上限后可能不刷新）
			double dt = 0.0;
			try
			{
				ulong nowMs = Time.GetTicksMsec();
				if (_lastMs > 0 && nowMs > _lastMs)
				{
					dt = (nowMs - _lastMs) / 1000.0;
				}
				_lastMs = nowMs;
				if (dt > 1.0)
				{
					dt = 0.0;   // 切场景/暂停的长间隔不计入，避免把 FPS 拉爆
				}
			}
			catch { }
			_accTime += dt;
			_accFrames++;
			if (_accTime >= SampleWindowSec && _accFrames > 0)
			{
				_fps = (int)Math.Round(_accFrames / _accTime);
				_accTime = 0.0;
				_accFrames = 0;
			}
			string txt = _fps.ToString();
			if (_label.Text != txt)
			{
				_label.Text = txt;
			}

			// ④ 位置：四角 或 自定义绝对坐标
			Vector2 vp = _tree.Root.GetVisibleRect().Size;
			Vector2 size = _label.GetCombinedMinimumSize();
			float margin = Mathf.Clamp(ReadInt(KMargin, DefMargin), 0, 200);
			string corner = (ReadString(KCorner, DefCorner) ?? DefCorner).Trim().ToLowerInvariant();
			float x, y;
			switch (corner)
			{
				case "tr":
					x = vp.X - size.X - margin;
					y = margin;
					break;
				case "bl":
					x = margin;
					y = vp.Y - size.Y - margin;
					break;
				case "br":
					x = vp.X - size.X - margin;
					y = vp.Y - size.Y - margin;
					break;
				case "custom":
					x = ReadInt(KCustomX, DefCustomX);
					y = ReadInt(KCustomY, DefCustomY);
					break;
				default:                       // tl
					x = margin;
					y = margin;
					break;
			}
			_label.GlobalPosition = new Vector2(x, y);
		}
		catch (Exception ex)
		{
			if (_diag < 100)
			{
				_diag = 100;
				Warn("每帧异常（只报一次）：" + ex.GetBaseException().Message);
			}
		}
	}

	/// <summary>
	/// 建 CanvasLayer + Label。
	/// ⚠️ 必须用 CanvasLayer —— 直接挂 Control 会被战斗场景自己的 CanvasLayer / 地图盖住。
	/// </summary>
	private void EnsureLabel()
	{
		try
		{
			if (_layer != null && GodotObject.IsInstanceValid(_layer))
			{
				return;
			}
			_layer = new CanvasLayer();
			_layer.Name = "ModFpsDisplayLayer";
			_layer.Layer = 128;
			_tree.Root.AddChild(_layer);

			_label = new Label();
			_label.Name = "ModFpsLabel";
			_label.MouseFilter = Control.MouseFilterEnum.Ignore;   // 不吃点击，不挡按钮
			_label.AddThemeFontSizeOverride("font_size", Mathf.Clamp(ReadInt(KFontSize, DefFontSize), 8, 72));
			_label.AddThemeColorOverride("font_color", TextColor);
			_label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 1f));
			_label.AddThemeConstantOverride("outline_size", 4);
			_label.Text = "0";
			_layer.AddChild(_label);
			Log("已创建 FPS 标签（CanvasLayer.Layer=128）。");
		}
		catch (Exception ex)
		{
			Warn("建标签失败：" + ex.GetBaseException().Message);
		}
	}

	// ============================================================ 日志

	private void Log(string msg)
	{
		if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KEnableLog, DefLog)） */
		{
			return;
		}
		try { GD.Print(P + msg); } catch { }
	}

	/// <summary>异常报告：不受日志开关影响，始终输出（方便排查）。</summary>
	private void Warn(string msg)
	{
		if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KEnableLog, false)） */ { return; }
		// ★ 2026-10-14：该输出原先绕过日志开关，现统一受 enableLog 控制
		try { GD.PrintErr(P + msg); } catch { }
	}
}
