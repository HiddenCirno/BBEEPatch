using System;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 面板皮肤。样式只在第一次绘制时构建一次。
///
/// ⚠ IL2CPP 要点(Oracle5 工程踩过, 本工程原样继承):
///   所有背景贴图都是运行时 new 的 Texture2D, **必须设 hideFlags**,
///   否则会被 Resources.UnloadUnusedAssets() 当无主资源回收 ——
///   之后面板会变成"能点但看不见"(背景没了, 命中判定还在)。
/// </summary>
internal static class UiTheme
{
    internal static GUIStyle Win, Box, BoxAlt, Btn, Blue, Red, Label, Value,
                            Section, Title, ToggleOn, ToggleOff, Field, Swatch, RowHi;

    private static bool _ready;
    private static bool _failed;

    /// <summary>皮肤是否可用。面板必须靠它决定画不画 —— 见 Ensure 里的说明。</summary>
    internal static bool Ok => _ready && !_failed;

    internal static void Ensure()
    {
        if (_ready || _failed) return;
        try
        {
            Win = new GUIStyle(GUI.skin.box);
            Win.normal.background = Tex(new Color(0.15f, 0.16f, 0.18f, 1f));
            Win.padding = new RectOffset(6, 6, 6, 6);
            Win.border = new RectOffset(0, 0, 0, 0);

            Box = new GUIStyle(GUI.skin.box);
            Box.normal.background = Tex(new Color(0.20f, 0.21f, 0.23f, 1f));
            Box.padding = new RectOffset(4, 4, 3, 3);
            Box.margin = new RectOffset(0, 0, 1, 1);
            Box.border = new RectOffset(0, 0, 0, 0);

            BoxAlt = new GUIStyle(Box);
            BoxAlt.normal.background = Tex(new Color(0.17f, 0.18f, 0.20f, 1f));

            Btn = Button(new Color(0.25f, 0.26f, 0.28f), new Color(0.35f, 0.36f, 0.39f),
                         new Color(0.12f, 0.13f, 0.15f), Color.white);

            Blue = Button(new Color(0.20f, 0.30f, 0.50f), new Color(0.30f, 0.40f, 0.60f),
                          new Color(0.10f, 0.20f, 0.30f), Color.white);

            Red = Button(new Color(0.50f, 0.20f, 0.20f), new Color(0.70f, 0.30f, 0.30f),
                         new Color(0.30f, 0.20f, 0.20f), Color.white);

            ToggleOn = new GUIStyle(Blue) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            ToggleOff = Button(new Color(0.22f, 0.23f, 0.25f), new Color(0.30f, 0.31f, 0.33f),
                               new Color(0.15f, 0.16f, 0.18f), new Color(0.62f, 0.62f, 0.62f));
            ToggleOff.alignment = TextAnchor.MiddleCenter;

            Label = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 4, 0, 0),
                margin = new RectOffset(0, 0, 2, 2),
                wordWrap = false,
            };
            Label.normal.textColor = new Color(0.84f, 0.84f, 0.84f, 1f);

            Value = new GUIStyle(Label) { alignment = TextAnchor.MiddleCenter };
            Value.normal.textColor = Color.white;
            Value.fontStyle = FontStyle.Bold;

            Section = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(4, 4, 6, 2),
            };
            Section.normal.textColor = new Color(0.65f, 0.78f, 1f, 1f);

            Title = new GUIStyle(Section) { fontSize = 14, alignment = TextAnchor.MiddleLeft };

            // ★ 文本框皮肤。见 UiWidgets.TextField 的注释:
            //   游戏的 GUI.TextField 只有 3 参重载能用, 所以这里给什么样式就用什么样式,
            //   不能再依赖 4 参重载里的 maxLength 参数。
            Field = new GUIStyle(GUI.skin.textField);
            Field.normal.background = Tex(new Color(0.12f, 0.13f, 0.15f, 1f));
            Field.hover.background = Tex(new Color(0.15f, 0.16f, 0.18f, 1f));
            Field.focused.background = Tex(new Color(0.18f, 0.20f, 0.22f, 1f));
            Field.active.background = Field.focused.background;
            Field.normal.textColor = Color.white;
            Field.hover.textColor = Color.white;
            Field.focused.textColor = Color.white;
            Field.active.textColor = Color.white;
            Field.padding = new RectOffset(4, 4, 0, 0);
            Field.border = new RectOffset(0, 0, 0, 0);
            Field.alignment = TextAnchor.MiddleLeft;

            // ⚠ 必须给背景贴图 —— 否则 GUI.Box 画的是透明框, 靠 GUI.color 染色也看不出来,
            //   表现就是"色卡不显示"。给白底, 再靠 GUI.color 染成目标色。
            Swatch = new GUIStyle(GUI.skin.box) { border = new RectOffset(0, 0, 0, 0), margin = new RectOffset(2, 2, 4, 4) };
            Swatch.normal.background = Tex(Color.white);

            RowHi = new GUIStyle(Box);
            RowHi.normal.background = Tex(new Color(0.24f, 0.27f, 0.32f, 1f));

            // ★ 只有全部建完才算"就绪"。
            //   上一版把 _ready = true 写在 try 【之前】—— 中途一旦抛异常,
            //   _ready 和 _failed 同时为真, Ensure 再也不会重试, 而 Win/Field/Btn 这些
            //   后面才赋值的字段全是 null, 面板会拿着 null 去 BeginArea, 每帧一片异常。
            _ready = true;
        }
        catch (Exception e)
        {
            _failed = true;
            _ready = false;
            Plugin.Log?.LogError($"[UI] 建样式失败, 面板本次不绘制: {LogEx.Unwrap(e)}");
        }
    }

    private static Texture2D Tex(Color c)
    {
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        t.SetPixel(0, 0, c);
        t.Apply();
        t.hideFlags = HideFlags.HideAndDontSave;
        return t;
    }

    private static GUIStyle Button(Color normal, Color hover, Color active, Color text)
    {
        var s = new GUIStyle(GUI.skin.button);
        s.normal.background = Tex(normal);
        s.hover.background = Tex(hover);
        s.active.background = Tex(active);
        s.normal.textColor = text;
        s.hover.textColor = Color.white;
        s.active.textColor = Color.white;
        s.border = new RectOffset(0, 0, 0, 0);
        s.margin = new RectOffset(2, 2, 2, 2);
        return s;
    }
}
