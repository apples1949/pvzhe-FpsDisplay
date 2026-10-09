using System;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「FPS 显示」Mod —— 在屏幕四角之一显示**实时 FPS（纯数字）**。
///
/// ── 内部开关（改完重新构建）─────────────────────────────
///   Where    ：显示在哪个角（左上/右上/左下/右下，默认左上）
///   FontSize ：字号         MarginPx ：离屏幕边缘的距离
///   UpdateEveryFrames ：每多少帧刷一次数字
///
/// ── 铁律（本项目多次踩过）───────────────────────────────
/// · 手写 csproj ⇒ **没有 Godot 源码生成器** ⇒ 自定义 Node 子类的
///   `_Process` / `_Ready` **根本不会被调用**！逻辑必须走
///   `SceneTree.Connect("process_frame", Callable.From(Action))`。
/// · `Callable` **不能**隐式转 `Action`（只有反向可以）。
/// · 入口三回调一律 try/catch 吞异常（抛出 = 整包回滚）。
/// </summary>
public sealed class FpsDisplayEntry : IXWModRuntimeEntry
{
	private const string P = "[FpsDisplay] ";

	/// <summary>显示在哪个角。</summary>
	internal enum Corner
	{
		TopLeft = 0,
		TopRight = 1,
		BottomLeft = 2,
		BottomRight = 3,
	}

	/// <summary>★ 显示角落（改这里，然后重新构建）。</summary>
	private const Corner Where = Corner.TopLeft;

	private const int FontSize = 22;
	private const float MarginPx = 10f;
	private const int UpdateEveryFrames = 8;
	private static readonly Color TextColor = new Color(0.25f, 1.0f, 0.35f);

	private SceneTree _tree;
	private Callable _tick;
	private CanvasLayer _layer;
	private Label _label;
	private int _frames;
	private int _diag;
	private bool _started;

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			Log("初始化完成。显示角落=" + Where + "，字号=" + FontSize);
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
				Log("拿不到 SceneTree，本 Mod 不生效。");
				return;
			}
			_tick = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tick);
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
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tick);
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

	private void OnFrame()
	{
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}
			_frames++;

			if (_label == null || !GodotObject.IsInstanceValid(_label))
			{
				EnsureLabel();
				if (_label == null)
				{
					return;
				}
			}

			// ① 数字：不必每帧写，每 N 帧一次
			if (_frames % UpdateEveryFrames == 0)
			{
				string txt = Engine.GetFramesPerSecond().ToString();
				if (_label.Text != txt)
				{
					_label.Text = txt;
				}
			}

			// ② 位置：按所选角落 + 视口尺寸每帧贴一次
			//    （比用锚点直观；换分辨率/拉窗口/横竖版都能自动跟上）
			Vector2 vp = _tree.Root.GetVisibleRect().Size;
			Vector2 size = _label.GetCombinedMinimumSize();
			float x, y;
			switch (Where)
			{
				case Corner.TopRight:
					x = vp.X - size.X - MarginPx;
					y = MarginPx;
					break;
				case Corner.BottomLeft:
					x = MarginPx;
					y = vp.Y - size.Y - MarginPx;
					break;
				case Corner.BottomRight:
					x = vp.X - size.X - MarginPx;
					y = vp.Y - size.Y - MarginPx;
					break;
				default:
					x = MarginPx;
					y = MarginPx;
					break;
			}
			_label.GlobalPosition = new Vector2(x, y);
		}
		catch (Exception ex)
		{
			if (_diag < 100)
			{
				_diag = 100;
				Log("每帧异常（只报一次）：" + ex.Message);
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
			_label.MouseFilter = Control.MouseFilterEnum.Ignore;
			_label.AddThemeFontSizeOverride("font_size", FontSize);
			_label.AddThemeColorOverride("font_color", TextColor);
			_label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 1f));
			_label.AddThemeConstantOverride("outline_size", 4);
			_label.Text = "0";
			_layer.AddChild(_label);
			Log("已创建 FPS 标签（CanvasLayer.Layer=128）。");
		}
		catch (Exception ex)
		{
			Log("建标签失败：" + ex.Message);
		}
	}

	private void Log(string msg)
	{
		try { GD.Print(P + msg); } catch { }
	}
}
