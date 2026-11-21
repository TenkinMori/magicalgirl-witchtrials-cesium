using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CesiumLoader.SDK;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Mods;
using Core;
using FairyGUI;
using Tools;
using UI;
using UnityEngine;

namespace WitchTrialsTheme
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new WitchTrialsThemeController());
        }
    }

    /// <summary>
    /// 魔裁主题首页：纯运行时把主页版式换成《魔法少女的魔女审判》主菜单。
    ///
    /// 不改任何游戏文件 / bundle：
    ///   · 5 个官方按钮改用我们的两态美术（原按钮保留点击，官方美术与文字压掉）
    ///   · btn_PlayerGuide 拉成右上角 LOGO
    ///   · 隐藏 11 项杂物（横幅 / 活动 / 抖音 / 底部小按钮 / 拖拽提示 …）
    ///   · 背景交给 HomeBackgroundMod，本模组不碰 Com_BGSet
    ///
    /// 游戏每次 Refresh() 会把可见性与 onClick 抢回去，所以 OnUpdate 低频幂等重放。
    /// </summary>
    public sealed class WitchTrialsThemeController : ModBase
    {
        private const double PollIntervalSeconds = 0.25;

        /// <summary>按钮槽位：面板子控件 / 美术前缀 / 面板坐标 / 尺寸 / 美术相对按钮的偏移。</summary>
        private sealed class Slot
        {
            public string Child;
            public string Art;
            public float X, Y, W, H;
            public float ArtX, ArtY;
        }

        // 尺寸与 plan.json 的 homepanel[].to 完全一致：美术显示尺寸 == 按钮尺寸。
        private static readonly Slot[] Slots =
        {
            new Slot { Child = "btn_GameOnline", Art = "loadgame", X = 18f,  Y = 702f, W = 378f, H = 232f, ArtX = -10f, ArtY = 3f   },
            new Slot { Child = "btn_Dictionary", Art = "newgame",  X = 320f, Y = 664f, W = 267f, H = 202f, ArtX = -17f, ArtY = -30f },
            new Slot { Child = "btn_Mall",       Art = "gallery",  X = 570f, Y = 754f, W = 222f, H = 149f, ArtX = -35f, ArtY = -12f },
            new Slot { Child = "btn_Gacha",      Art = "options",  X = 788f, Y = 709f, W = 206f, H = 157f, ArtX = -25f, ArtY = -10f },
            new Slot { Child = "btn_Activity",   Art = "exit",     X = 964f, Y = 763f, W = 189f, H = 182f, ArtX = -2f,  ArtY = -6f  },
        };

        /// <summary>右上角 LOGO：把 btn_PlayerGuide 拉大换图（点击仍是官方「装扮」）。</summary>
        private const string LogoChild = "btn_PlayerGuide";
        private const float LogoX = 1340f, LogoY = 24f, LogoW = 556f, LogoH = 326f;

        private static readonly string[] HiddenChildren =
        {
            "com_Banner", "com_Activity", "com_expand", "com_ActivityBanner", "com_BattlePass",
            "Com_Tip", "Btn_BGSet", "Btn_Hide",
            "btn_DouYinReward", "btn_DouYinGroupChat", "btn_DouYinShareReward", "btn_DouYinSubscribeReward",
        };

        private bool _enabled = true;
        private bool _overrideExit = true;

        private NTexture _logoTexture;
        private readonly Dictionary<string, NTexture> _art = new Dictionary<string, NTexture>();
        private readonly List<Texture2D> _textures = new List<Texture2D>();
        private readonly Dictionary<string, ButtonSkin> _skins = new Dictionary<string, ButtonSkin>();
        private UIHomePanel _appliedUi;

        private double _nextPoll;
        private double _nextWarn;
        private string _lastWarn;
        private bool _loggedApply;

        public override string Name { get { return "魔裁主题首页"; } }
        public override string Version { get { return "1.0.0"; } }
        public override string Author { get { return "TenkinMori"; } }
        public override string Description
        {
            get { return "把主页版式换成《魔法少女的魔女审判》主菜单（背景请配合 HomeBackgroundMod）。"; }
        }

        public override void OnInitialize()
        {
            ReadConfig(writeDefaults: true);
            Log.Info("配置: enable=" + _enabled + ", overrideExit=" + _overrideExit);
            if (_enabled) Preload();
        }

        public override void OnConfigChanged()
        {
            ReadConfig(writeDefaults: false);
            DisposeTextures();
            Log.Info("配置已更新: enable=" + _enabled);
            if (_enabled) Preload();
        }

        public override void OnUpdate()
        {
            if (!_enabled) return;
            double now = DateTime.UtcNow.TimeOfDay.TotalSeconds;
            if (now < _nextPoll) return;
            _nextPoll = now + PollIntervalSeconds;

            try { Apply(); }
            catch (Exception e) { Warn("应用失败: " + e.Message); }
        }

        public override void OnUnload()
        {
            DisposeTextures();
        }

        // ------------------------------------------------------------------
        private void ReadConfig(bool writeDefaults)
        {
            ModConfig cfg = Config;
            if (cfg == null) return;

            if (writeDefaults && cfg.Count == 0)
            {
                cfg.Set("enable", true);
                cfg.Set("overrideExit", true);
                cfg.SaveIfDirty();
            }

            _enabled = cfg.GetBool("enable", true);
            _overrideExit = cfg.GetBool("overrideExit", true);
        }

        private void Preload()
        {
            if (_logoTexture == null) _logoTexture = LoadTextureN("logo_cn_full.png");
            for (int i = 0; i < Slots.Length; i++)
            {
                GetArt("al_" + Slots[i].Art + "_dark.png");
                GetArt("al_" + Slots[i].Art + "_pink.png");
            }
        }

        private void Apply()
        {
            UIManager manager = SimpleSingletonProvider<UIManager>.inst;
            if (manager == null) return;

            IBasePanel current = manager.currentPanel;
            if (current == null || current.config == null) return;
            if (current.config.PanelType != UIPanelType.Home) return;

            HomePanel home = current as HomePanel;
            if (home == null) return;

            UIHomePanel ui = home.UIObject;
            if (ui == null) return;

            if (!ReferenceEquals(_appliedUi, ui))
            {
                _appliedUi = ui;
                _skins.Clear();
            }

            if (_logoTexture == null)
            {
                Preload();
                if (_logoTexture == null) return;
            }

            PatchCutIn(ui);

            // 模组初始化时主页大多已经播完 Cut in；只有从别的场景回主页才会重播。
            // 正在播就收掉（不留补间），下面直接按落点定位；没播就什么都不做。
            if (ui.Cut_in != null && ui.Cut_in.playing) ui.Cut_in.Stop(false, false);

            HideDecorations(ui);
            SkinButtons(ui);

            if (!_loggedApply)
            {
                _loggedApply = true;
                Log.Info("已应用魔裁主题主页版式");
            }
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// Cut in 是 autoPlay 转场，上舞台时自动播一次，面板每次 Show() 不重播。
        /// 把里面 5 个按钮 + LOGO 的终点坐标改成我们的落点；正在播就收掉，交给 SetXY 定点。
        /// </summary>
        private void PatchCutIn(UIHomePanel ui)
        {
            Transition cutIn = ui.Cut_in;
            if (cutIn == null) return;

            Dictionary<string, Vector2> want = new Dictionary<string, Vector2>();
            for (int i = 0; i < Slots.Length; i++)
            {
                GObject obj = ui.GetChild(Slots[i].Child);
                if (obj != null && !string.IsNullOrEmpty(obj.id)) want[obj.id] = new Vector2(Slots[i].X, Slots[i].Y);
            }
            GObject logo = ui.GetChild(LogoChild);
            if (logo != null && !string.IsNullOrEmpty(logo.id)) want[logo.id] = new Vector2(LogoX, LogoY);

            Array items = GetTransitionItems(cutIn);
            if (items != null && want.Count > 0)
            {
                for (int i = 0; i < items.Length; i++)
                {
                    object item = items.GetValue(i);
                    if (item == null) continue;
                    if (GetItemType(item) != TransitionActionType.XY) continue;

                    string targetId = GetStringField(item, "targetId");
                    if (string.IsNullOrEmpty(targetId)) continue;

                    Vector2 end;
                    if (!want.TryGetValue(targetId, out end)) continue;

                    object tween = GetObjectField(item, "tweenConfig");
                    if (tween == null) continue;
                    object value = GetObjectField(tween, "endValue");
                    if (value == null) continue;
                    SetTValue(value, end.x, end.y);
                }
            }

            // 转场正在跑时不要硬停，由 Apply() 统一处理。
        }

        /// <summary>隐藏横幅 / 活动 / 抖音 / 底部小按钮等杂物；每轮重放，防止被 Refresh 打开。</summary>
        private void HideDecorations(UIHomePanel ui)
        {
            for (int i = 0; i < HiddenChildren.Length; i++)
            {
                GObject obj = ui.GetChild(HiddenChildren[i]);
                if (obj == null) continue;
                obj.visible = false;
                obj.touchable = false;
            }

            // 左下角 Icon 是"显示底栏"的小按钮，plan 里把它的填充色改成全透明。
            if (ui.Icon != null) ui.Icon.alpha = 0f;
        }

        private void SkinButtons(UIHomePanel ui)
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                Slot slot = Slots[i];
                GButton button = ui.GetChild(slot.Child) as GButton;
                if (button == null) continue;

                button.SetPosition(slot.X, slot.Y, 0f);
                button.SetSize(slot.W, slot.H);

                ButtonSkin skin = GetOrCreateSkin(slot, button);
                if (skin == null) continue;
                skin.Apply();
                skin.SyncState();
            }

            // Exit 槽位：btn_Activity 会被 HomePanel.RefreshActivityHubData 抢走 onClick，
            // 没有活动数据时还会被 visible=false。这里每轮重新接管成「退出游戏」。
            if (_overrideExit)
            {
                GButton exit = ui.GetChild("btn_Activity") as GButton;
                if (exit != null)
                {
                    exit.visible = true;
                    exit.grayed = false;
                    exit.onClick.Set(OnExitClick);
                }
            }

            // LOGO
            GButton logo = ui.GetChild(LogoChild) as GButton;
            if (logo != null)
            {
                logo.SetPosition(LogoX, LogoY, 0f);
                logo.SetSize(LogoW, LogoH);

                ButtonSkin skin = GetLogoSkin(logo);
                if (skin != null)
                {
                    skin.Apply();
                    skin.SyncState();
                }
            }
        }

        private ButtonSkin GetOrCreateSkin(Slot slot, GButton button)
        {
            ButtonSkin skin;
            if (_skins.TryGetValue(slot.Child, out skin) && skin != null && ReferenceEquals(skin.Button, button)) return skin;

            skin = new ButtonSkin(button, 2);
            DisableDownEffect(button);
            skin.EnsureArt(0, slot.Art + "_dark", GetArt("al_" + slot.Art + "_dark.png"), slot.ArtX, slot.ArtY, (int)slot.W, (int)slot.H);
            skin.EnsureArt(1, slot.Art + "_pink", GetArt("al_" + slot.Art + "_pink.png"), slot.ArtX, slot.ArtY, (int)slot.W, (int)slot.H);
            _skins[slot.Child] = skin;
            return skin;
        }

        private ButtonSkin GetLogoSkin(GButton button)
        {
            ButtonSkin skin;
            if (_skins.TryGetValue(LogoChild, out skin) && skin != null && ReferenceEquals(skin.Button, button)) return skin;

            skin = new ButtonSkin(button, 1);
            DisableDownEffect(button);
            skin.EnsureArt(0, "logo", _logoTexture, 0f, 0f, (int)LogoW, (int)LogoH);
            _skins[LogoChild] = skin;
            return skin;
        }

        private NTexture GetArt(string fileName)
        {
            NTexture tex;
            if (_art.TryGetValue(fileName, out tex) && tex != null) return tex;
            tex = LoadTextureN(fileName);
            _art[fileName] = tex;
            return tex;
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// 接管一个官方按钮的外观：官方子控件全部压掉，末尾追加 1~2 个 GLoader 叠我们的图。
        /// 原按钮的 touchable 保留，命中测试不看 alpha，所以点击照旧。
        /// </summary>
        private sealed class ButtonSkin
        {
            private sealed class Layer
            {
                public GLoader Loader;
                public NTexture Texture;
                public float X, Y;
                public int W, H;
            }

            private readonly Layer[] _layers;
            private readonly List<GObject> _original = new List<GObject>();
            private Controller _buttonController;
            private EventCallback0 _onChanged;

            public readonly GButton Button;

            public ButtonSkin(GButton button, int layerCount)
            {
                Button = button;
                _layers = new Layer[layerCount];
                for (int i = 0; i < layerCount; i++) _layers[i] = new Layer();

                for (int i = 0; i < button.numChildren; i++)
                {
                    GObject child = button.GetChildAt(i);
                    if (child != null) _original.Add(child);
                }
            }

            public void EnsureArt(int index, string name, NTexture texture, float x, float y, int w, int h)
            {
                if (index < 0 || index >= _layers.Length) return;
                Layer layer = _layers[index];
                layer.Texture = texture;
                layer.X = x;
                layer.Y = y;
                layer.W = w;
                layer.H = h;

                if (layer.Loader == null || layer.Loader.parent != Button)
                {
                    GLoader loader = new GLoader();
                    loader.name = "wt_" + name;
                    loader.fill = FillType.ScaleFree;
                    loader.touchable = false;
                    Button.AddChild(loader);   // 追加在最后 => 压在官方美术之上
                    layer.Loader = loader;
                }

                if (!ReferenceEquals(layer.Loader.texture, texture)) layer.Loader.texture = texture;
            }

            public void Apply()
            {
                HideOriginals();

                for (int i = 0; i < _layers.Length; i++)
                {
                    Layer layer = _layers[i];
                    if (layer.Loader == null) continue;
                    layer.Loader.SetPosition(layer.X, layer.Y, 0f);
                    layer.Loader.SetSize(layer.W, layer.H);
                }

                HookController();
            }

            public void SyncState()
            {
                if (_buttonController == null) HookController();
                bool down = _buttonController != null && _buttonController.selectedPage == "down";

                for (int i = 0; i < _layers.Length; i++)
                {
                    Layer layer = _layers[i];
                    if (layer.Loader == null) continue;
                    // 单层（LOGO）常显；双层时第 0 层常态、第 1 层按下态。
                    layer.Loader.visible = _layers.Length == 1 || (i == 0 ? !down : down);
                }

                HideOriginals();
            }

            private void HookController()
            {
                if (_buttonController == null) _buttonController = Button.GetController("button");
                if (_buttonController == null || _onChanged != null) return;
                _onChanged = SyncState;
                _buttonController.onChanged.Add(_onChanged);
            }

            /// <summary>
            /// 官方子控件带 GearDisplay，切换按钮状态时会把 visible 重新打开，
            /// 所以 visible / alpha 每轮都要重压一次。
            /// </summary>
            private void HideOriginals()
            {
                for (int i = 0; i < _original.Count; i++)
                {
                    GObject child = _original[i];
                    if (child == null) continue;
                    if (child.visible) child.visible = false;
                    if (child.alpha != 0f) child.alpha = 0f;
                    if (child.touchable) child.touchable = false;
                }
            }
        }

        // ------------------------------------------------------------------
        private static readonly FieldInfo DownEffectField =
            typeof(GButton).GetField("_downEffect", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo ItemsField =
            typeof(Transition).GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>把 GButton 的内置按下效果（变暗 / 缩放）关掉，避免压暗我们的美术。</summary>
        private static void DisableDownEffect(GButton button)
        {
            if (DownEffectField == null) return;
            try { DownEffectField.SetValue(button, 0); }
            catch { }
        }

        private static Array GetTransitionItems(Transition transition)
        {
            if (ItemsField == null) return null;
            try { return ItemsField.GetValue(transition) as Array; }
            catch { return null; }
        }

        private static TransitionActionType GetItemType(object item)
        {
            FieldInfo f = item.GetType().GetField("type");
            if (f == null) return TransitionActionType.Unknown;
            object v = f.GetValue(item);
            return v is TransitionActionType ? (TransitionActionType)v : TransitionActionType.Unknown;
        }

        private static string GetStringField(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name);
            if (f == null) return null;
            return f.GetValue(obj) as string;
        }

        private static object GetObjectField(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name);
            return f == null ? null : f.GetValue(obj);
        }

        /// <summary>写 TValue.f1/f2 并置 b1/b2，等价于 Transition.SetValue 的 XY 分支。</summary>
        private static void SetTValue(object tvalue, float x, float y)
        {
            Type t = tvalue.GetType();
            FieldInfo f1 = t.GetField("f1"), f2 = t.GetField("f2");
            FieldInfo b1 = t.GetField("b1"), b2 = t.GetField("b2");
            if (f1 == null || f2 == null) return;
            f1.SetValue(tvalue, x);
            f2.SetValue(tvalue, y);
            if (b1 != null) b1.SetValue(tvalue, true);
            if (b2 != null) b2.SetValue(tvalue, true);
        }

        // ------------------------------------------------------------------
        private void OnExitClick()
        {
            try
            {
                UIManager manager = SimpleSingletonProvider<UIManager>.inst;
                if (manager == null || manager.messageBox == null) return;
                manager.messageBox.ShowOKCancel(1014, DoExit);
            }
            catch (Exception e) { Warn("退出确认框打开失败: " + e.Message); }
        }

        /// <summary>与底栏「退出」一致：先 ExitSdk 再关游戏，不直接调 Application.Quit。</summary>
        private void DoExit()
        {
            try { BnSdkManager.Instance.ExitSdk(); } catch { }
            try { GameManager.inst.CloseGame(); } catch { }
        }

        // ------------------------------------------------------------------
        private NTexture LoadTextureN(string fileName)
        {
            string path = Path.Combine(Path.Combine(SdkPath.CurrentModDirectory, "assets"), fileName);
            if (!File.Exists(path))
            {
                Warn("找不到素材: " + path);
                return null;
            }

            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception e) { Warn("读取素材失败: " + e.Message); return null; }

            int width, height;
            byte[] rgba;
            if (!PngDecoder.TryDecode(data, out width, out height, out rgba))
            {
                Warn("PNG 解码失败（仅支持 8-bit 非隔行）: " + fileName);
                return null;
            }

            // Unity 纹理原点在左下，PNG 原点在左上，逐行翻转。
            int stride = width * 4;
            for (int y = 0; y < height / 2; y++)
            {
                int a = y * stride;
                int b = (height - 1 - y) * stride;
                for (int i = 0; i < stride; i++)
                {
                    byte tmp = rgba[a + i];
                    rgba[a + i] = rgba[b + i];
                    rgba[b + i] = tmp;
                }
            }

            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.name = "WitchTrials_" + fileName;
            tex.LoadRawTextureData(rgba);
            tex.Apply(false, false);

            _textures.Add(tex);
            return new NTexture(tex);
        }

        private void DisposeTextures()
        {
            for (int i = 0; i < _textures.Count; i++)
            {
                try { UnityEngine.Object.Destroy(_textures[i]); } catch { }
            }
            _textures.Clear();
            _art.Clear();
            _skins.Clear();
            _logoTexture = null;
            _appliedUi = null;
        }

        private void Warn(string message)
        {
            double now = DateTime.UtcNow.TimeOfDay.TotalSeconds;
            if (message == _lastWarn && now < _nextWarn) return;
            _lastWarn = message;
            _nextWarn = now + 30;
            Log.Warn(message);
        }
    }
}
