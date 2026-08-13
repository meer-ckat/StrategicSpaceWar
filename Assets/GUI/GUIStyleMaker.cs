using System.Collections.Generic;
using UnityEngine;

namespace IMGUI
{
    public static class GUIStyleMaker
    {
        private static readonly Dictionary<uint, Texture2D> solidTextures = new();

        private static GUIStyle labelBase;
        private static GUIStyle boxBase;
        private static GUIStyle buttonBase;
        private static GUIStyle toggleBase;
        private static GUIStyle textFieldBase;
        private static GUIStyle sliderBase;
        private static GUIStyle sliderThumbBase;
        private static GUIStyle vSliderBase;
        private static GUIStyle vSliderThumbBase;

        public static bool Initialized { get; private set; }

        /// <summary>
        /// 반드시 OnGUI 내부에서 최초 1회 호출.
        /// 이후 GUIStyleMaker는 OnGUI 밖에서도 사용할 수 있다.
        /// </summary>
        public static void Initialize()
        {
            if (Initialized) return;

            labelBase = new GUIStyle(GUI.skin.label);
            boxBase = new GUIStyle(GUI.skin.box);
            buttonBase = new GUIStyle(GUI.skin.button);
            toggleBase = new GUIStyle(GUI.skin.toggle);
            textFieldBase = new GUIStyle(GUI.skin.textField);
            sliderBase = new GUIStyle(GUI.skin.horizontalSlider);
            sliderThumbBase = new GUIStyle(GUI.skin.horizontalSliderThumb);
            vSliderBase = new GUIStyle(GUI.skin.verticalSlider);
            vSliderThumbBase = new GUIStyle(GUI.skin.verticalSliderThumb);

            Initialized = true;
        }

        private static GUIStyle Copy(GUIStyle source)
        {
            if (!Initialized)
            {
                Debug.LogError(
                    "[IMGUI] GUIStyleMaker가 초기화되지 않았습니다.\n" +
                    "GUIHost.OnGUI()에서 GUIStyleMaker.Initialize()를 먼저 호출하세요."
                );

                return new GUIStyle();
            }

            return new GUIStyle(source);
        }

        public static GUIStyle Label(
            Color? text = null,
            int fontSize = 16,
            TextAnchor alignment = TextAnchor.MiddleLeft)
        {
            GUIStyle s = Copy(labelBase);
            s.fontSize = fontSize;
            s.alignment = alignment;

            if (text.HasValue)
                SetText(s, text.Value);

            return s;
        }

        public static GUIStyle Box(
            Color? background = null,
            Color? text = null,
            int fontSize = 0,
            FontStyle fontStyle = FontStyle.Normal)
        {
            GUIStyle s = Copy(boxBase);
            s.fontSize = fontSize;
            s.fontStyle = fontStyle;

            if (background.HasValue)
                SetBackground(s, background.Value);

            if (text.HasValue)
                SetText(s, text.Value);

            return s;
        }

        public static GUIStyle Button(
            Color? background = null,
            Color? text = null,
            Color? hover = null,
            Color? active = null,
            int fontSize = 15)
        {
            GUIStyle s = Copy(buttonBase);

            s.fontSize = fontSize;
            s.fontStyle = FontStyle.Bold;
            s.alignment = TextAnchor.MiddleCenter;

            if (background.HasValue)
            {
                Color n = background.Value;
                SetBackground(s, n, hover ?? n, active ?? hover ?? n);
            }

            if (text.HasValue)
                SetText(s, text.Value);

            return s;
        }

        public static GUIStyle Toggle(
            Color? text = null,
            int fontSize = 16)
        {
            GUIStyle s = Copy(toggleBase);

            s.fontSize = fontSize;
            s.alignment = TextAnchor.MiddleLeft;

            if (text.HasValue)
                SetText(s, text.Value);

            return s;
        }

        public static GUIStyle TextField(
            Color? background = null,
            Color? text = null,
            int fontSize = 16)
        {
            GUIStyle s = Copy(textFieldBase);

            s.fontSize = fontSize;
            s.alignment = TextAnchor.MiddleLeft;

            if (background.HasValue)
                SetBackground(s, background.Value);

            if (text.HasValue)
                SetText(s, text.Value);

            return s;
        }

        public static GUIStyle Slider(
            Color? background = null,
            float height = 7f)
        {
            GUIStyle s = Copy(sliderBase);

            s.fixedHeight = height;
            s.margin = new RectOffset();
            s.padding = new RectOffset();
            s.overflow = new RectOffset();

            if (background.HasValue)
                SetBackground(s, background.Value);

            return s;
        }

        public static GUIStyle SliderThumb(
            Color? background = null,
            Color? hover = null,
            Color? active = null,
            float width = 18f,
            float height = 24f)
        {
            GUIStyle s = Copy(sliderThumbBase);

            s.fixedWidth = width;
            s.fixedHeight = height;
            s.margin = new RectOffset();
            s.padding = new RectOffset();
            s.overflow = new RectOffset();

            if (background.HasValue)
            {
                Color n = background.Value;
                SetBackground(s, n, hover ?? n, active ?? hover ?? n);
            }

            return s;
        }

        /// <summary>세로 슬라이더 트랙. 가로와 달리 fixedWidth가 두께다.</summary>
        public static GUIStyle VerticalSlider(
            Color? background = null,
            float width = 7f)
        {
            GUIStyle s = Copy(vSliderBase);

            s.fixedWidth = width;
            s.margin = new RectOffset();
            s.padding = new RectOffset();
            s.overflow = new RectOffset();

            if (background.HasValue)
                SetBackground(s, background.Value);

            return s;
        }

        public static GUIStyle VerticalSliderThumb(
            Color? background = null,
            Color? hover = null,
            Color? active = null,
            float width = 24f,
            float height = 18f)
        {
            GUIStyle s = Copy(vSliderThumbBase);

            s.fixedWidth = width;
            s.fixedHeight = height;
            s.margin = new RectOffset();
            s.padding = new RectOffset();
            s.overflow = new RectOffset();

            if (background.HasValue)
            {
                Color n = background.Value;
                SetBackground(s, n, hover ?? n, active ?? hover ?? n);
            }

            return s;
        }

        public static GUIStyle Clone(GUIStyle source)
            => source == null ? new GUIStyle() : new GUIStyle(source);

        public static GUIStyle Font(this GUIStyle s, int size, FontStyle style = FontStyle.Normal)
        {
            s.fontSize = size;
            s.fontStyle = style;
            return s;
        }

        public static GUIStyle Align(this GUIStyle s, TextAnchor alignment)
        {
            s.alignment = alignment;
            return s;
        }

        public static GUIStyle Padding(this GUIStyle s, int all)
        {
            s.padding = new RectOffset(all, all, all, all);
            return s;
        }

        public static GUIStyle Padding(this GUIStyle s, int horizontal, int vertical)
        {
            s.padding = new RectOffset(horizontal, horizontal, vertical, vertical);
            return s;
        }

        public static GUIStyle Padding(this GUIStyle s, int left, int right, int top, int bottom)
        {
            s.padding = new RectOffset(left, right, top, bottom);
            return s;
        }

        public static GUIStyle Margin(this GUIStyle s, int all)
        {
            s.margin = new RectOffset(all, all, all, all);
            return s;
        }

        public static GUIStyle Margin(this GUIStyle s, int left, int right, int top, int bottom)
        {
            s.margin = new RectOffset(left, right, top, bottom);
            return s;
        }

        /// <summary>1x1 단색 배경에는 9-slice border가 독이다. 0으로 밀어야 납작하게 깔린다.</summary>
        public static GUIStyle Border(this GUIStyle s, int all)
        {
            s.border = new RectOffset(all, all, all, all);
            return s;
        }

        public static GUIStyle Overflow(this GUIStyle s, int all)
        {
            s.overflow = new RectOffset(all, all, all, all);
            return s;
        }

        public static GUIStyle FixedSize(this GUIStyle s, float width = 0f, float height = 0f)
        {
            s.fixedWidth = width;
            s.fixedHeight = height;
            return s;
        }

        public static GUIStyle Wrap(this GUIStyle s, bool value = true)
        {
            s.wordWrap = value;
            return s;
        }

        public static GUIStyle RichText(this GUIStyle s, bool value = true)
        {
            s.richText = value;
            return s;
        }

        public static GUIStyle Text(
            this GUIStyle s,
            Color normal,
            Color? hover = null,
            Color? active = null,
            Color? focused = null)
        {
            SetText(s, normal, hover, active, focused);
            return s;
        }

        public static GUIStyle Background(
            this GUIStyle s,
            Color normal,
            Color? hover = null,
            Color? active = null,
            Color? focused = null)
        {
            SetBackground(s, normal, hover, active, focused);
            return s;
        }

        public static GUIStyle NoSpacing(this GUIStyle s)
        {
            s.margin = new RectOffset();
            s.padding = new RectOffset();
            s.overflow = new RectOffset();
            return s;
        }

        private static void SetText(
            GUIStyle s,
            Color normal,
            Color? hover = null,
            Color? active = null,
            Color? focused = null)
        {
            Color h = hover ?? normal;
            Color a = active ?? h;
            Color f = focused ?? normal;

            s.normal.textColor = normal;
            s.hover.textColor = h;
            s.active.textColor = a;
            s.focused.textColor = f;

            s.onNormal.textColor = normal;
            s.onHover.textColor = h;
            s.onActive.textColor = a;
            s.onFocused.textColor = f;
        }

        private static void SetBackground(
            GUIStyle s,
            Color normal,
            Color? hover = null,
            Color? active = null,
            Color? focused = null)
        {
            Color h = hover ?? normal;
            Color a = active ?? h;
            Color f = focused ?? normal;

            Texture2D n = Solid(normal);
            Texture2D ht = Solid(h);
            Texture2D at = Solid(a);
            Texture2D ft = Solid(f);

            s.normal.background = n;
            s.hover.background = ht;
            s.active.background = at;
            s.focused.background = ft;

            s.onNormal.background = n;
            s.onHover.background = ht;
            s.onActive.background = at;
            s.onFocused.background = ft;
        }

        public static Texture2D Solid(Color color)
        {
            Color32 c = color;

            uint key =
                ((uint)c.r << 24) |
                ((uint)c.g << 16) |
                ((uint)c.b << 8) |
                c.a;

            if (solidTextures.TryGetValue(key, out Texture2D texture) && texture != null)
                return texture;

            texture = new Texture2D(1, 1)
            {
                name = $"GUI_{c.r}_{c.g}_{c.b}_{c.a}",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            texture.SetPixel(0, 0, color);
            texture.Apply(false, true);

            solidTextures[key] = texture;

            return texture;
        }
    }
}