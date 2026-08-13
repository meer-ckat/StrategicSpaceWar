using UnityEngine;
using System;
using System.Collections.Generic;
using Unity.VisualScripting;

namespace IMGUI
{
    public abstract class GUIItem
    {
        public int Layer;
        public float Opacity;
        public bool isVisible = true;
        public bool isEnabled = true;
        public bool isInteractable = true;
        public GUIContent Content;
        public GUIStyle Style; //어떤 스타일(Texture, Font 등)으로 그릴건지
        public Rect Rect;
        public Vector2 RenderScale = Vector2.one;

        /// <summary>
        /// 실제 그리기 / 클릭 / 호버 판정에 사용하는 Rect.
        /// Rect 자체는 건드리지 않고 중심 기준으로 RenderScale만 적용한다.
        /// </summary>
        public Rect DrawRect
        {
            get
            {
                Vector2 scaledSize =
                    Vector2.Scale(Rect.size, RenderScale);

                return new Rect(
                    Rect.center - scaledSize * 0.5f,
                    scaledSize
                );
            }
        }

        public Vector2 Pos => Rect.position; //some helpers
        public Vector2 Size => Rect.size;

        public string Parent;

        public Action<GUIItem, float> whenTick;


        protected GUIItem(
            GUIContent content,
            Rect rect,
            GUIStyle style = null)
        {
            Content = content;
            Rect = rect;
            Style = style;
        }


        public virtual GUIItem GetGUI() //this = this
        {
            return this;
        }

        public virtual void SetLayer(int layer)
        {
            Layer = layer;
        }

        public virtual void SetRect(Rect newRect)
        {
            Rect = newRect;
        }


        public virtual void SetSize(Vector2 newSize)
        {
            Rect.size = newSize;
        }


        public virtual void SetPos(Vector2 newPos)
        {
            Rect.position = newPos;
        }


        public virtual void SetGroup(string parent)
        {
            Parent = parent;
        }


        public virtual void Tick(float deltaTime) //이 GUI가 Update()동안 뭘 하는지
        {
            whenTick?.Invoke(this, deltaTime);
        }


        public abstract void Draw();
    }


    // =========================================================
    // Label
    //
    // 배경 없음.
    // 일반적인 텍스트 출력용.
    // =========================================================

    public class GUILabel : GUIItem
    {
        public GUILabel(
            GUIContent content,
            Rect rect,
            GUIStyle style = null)
            : base(content, rect, style)
        {
        }


        public override void Draw()
        {
            GUI.Label(
                DrawRect,
                Content,
                Style ?? GUI.skin.label
            );
        }
    }


    // =========================================================
    // Box Label
    //
    // 배경 있음.
    // 텍스트 + Box 배경이 필요한 경우 사용.
    // =========================================================

    public class GUIBoxLabel : GUIItem
    {
        public GUIBoxLabel(
            GUIContent content,
            Rect rect,
            GUIStyle style = null)
            : base(content, rect, style)
        {
        }


        public override void Draw()
        {
            GUI.Box(
                DrawRect,
                Content,
                Style ?? GUI.skin.box
            );
        }
    }


    // =========================================================
    // Group
    // =========================================================

    public class GUIGroup : GUIItem
    {
        public string GroupName;
        private readonly List<GUIItem> childrens = new();
        public IReadOnlyList<GUIItem> Childrens => childrens;
        public bool Scrollable;
        public bool Mask = true; //자식이 밖으로 나가면 걍 깔끔하게 자를거임?
        public Vector2 ScrollPosition;
        public Vector2 ContentSize;
        public bool FitContentWidth;
        public bool FitContentHeight;
        public float ContentWidth
        {
            get => ContentSize.x;
            set => ContentSize.x = value;
        }

        public float ContentHeight
        {
            get => ContentSize.y;
            set => ContentSize.y = value;
        }
        public float PaddingLeft = 10f;
        public float PaddingRight = 10f;
        public float PaddingTop = 10f;
        public float PaddingBottom = 10f;


        public GUIGroup(GUIContent content, Rect rect,string name, GUIStyle style = null): base(content, rect, style)
        {
            GroupName = name;
        }

        public override void SetLayer(int layer)
        {
            base.SetLayer(layer);

            foreach (GUIItem child in childrens)
                child.SetLayer(layer);
        }

        public void Add(GUIItem item)
        {
            if (item == null)
                return;

            if (ReferenceEquals(item, this))
            {
                Debug.LogError(
                    $"CRITICAL ERROR: tried GUIGroup '{GroupName}' add itself as a child."
                );
                return;
            }

            if (childrens.Contains(item))
                return;

            childrens.Add(item);

            item.SetGroup(GroupName);
            item.SetLayer(Layer);
        }


        public void Remove(GUIItem item)
        {
            if (item == null)
                return;

            childrens.Remove(item);

            if (item.Parent == GroupName)
            {
                item.SetGroup(null);
            }
        }


        public override void SetPos(Vector2 newPos)
        {
            Vector2 diff =
                newPos - Rect.position;

            foreach (GUIItem crew in childrens)
            {
                crew.SetPos(
                    crew.Rect.position + diff
                );
            }

            base.SetPos(newPos);
        }


        public override void Draw()
        {
            GUI.Box(
                DrawRect,
                Content,
                Style ?? GUI.skin.box
            );
        }

        public void DrawChildren()
        {
            Draw();

            Rect viewport =
                GUILayoutManager.GetContentRect(this);

            if (Scrollable)
            {
                Rect content = new Rect(
                    Vector2.zero,
                    ContentSize
                );

                ScrollPosition = GUI.BeginScrollView(
                    viewport,
                    ScrollPosition,
                    content
                );

                foreach (GUIItem item in Childrens)
                {
                    DrawItemLocal(item, viewport);
                }

                GUI.EndScrollView();
            }
            else if (Mask)
            {
                GUI.BeginGroup(viewport);

                foreach (GUIItem item in childrens)
                {
                    DrawItemLocal(item, viewport);
                }

                GUI.EndGroup();
            }
            else
            {
                foreach (GUIItem item in childrens)
                {
                    DrawItem(item);
                }
            }
        }
        public void GroupAllInList(List<GUIItem> gUIItems)
        {
            foreach(GUIItem item in gUIItems)
            {
                Add(item);
            }
        }

        public static void FitGroupToContent(
            GUIGroup group)
        {
            Vector2 size = group.Size;

            if (group.FitContentWidth)
            {
                size.x =
                    group.ContentSize.x +
                    group.PaddingLeft +
                    group.PaddingRight;
            }

            if (group.FitContentHeight)
            {
                size.y =
                    group.ContentSize.y +
                    group.PaddingTop +
                    group.PaddingBottom;
            }

            group.SetSize(size);
        }

        private static void OffsetTree(GUIItem item, Vector2 offset)
        {
            item.Rect.position += offset;

            if (item is not GUIGroup group)
                return;

            foreach (GUIItem child in group.childrens)
                OffsetTree(child, offset);
        }

        private static void DrawItemLocal(
            GUIItem item,
            Rect viewport)
        {
            Vector2 offset = -viewport.position;

            OffsetTree(item, offset);

            DrawItem(item);

            OffsetTree(item, -offset);
        }

        private static void DrawItem(GUIItem item)
        {
            if (item == null || !item.isVisible)
                return;

            Color oldColor = GUI.color;
            bool oldEnabled = GUI.enabled;

            GUI.color = new Color(
                oldColor.r,
                oldColor.g,
                oldColor.b,
                oldColor.a * Mathf.Clamp01(item.Opacity)
            );

            GUI.enabled = oldEnabled && item.isEnabled && item.isInteractable;

            if (item is GUIGroup group)
                group.DrawChildren();
            else
                item.Draw();

            GUI.enabled = oldEnabled;
            GUI.color = oldColor;
        }
    }


    // =========================================================
    // Button
    // =========================================================

    public class GUIButton : GUIItem
    {
        public Action callBack;


        public GUIButton(
            GUIContent content,
            Rect rect,
            Action callBack = null,
            GUIStyle style = null)
            : base(content, rect, style)
        {
            this.callBack = callBack;
        }


        public override void Draw()
        {
            if (GUI.Button(
                DrawRect,
                Content,
                Style ?? GUI.skin.button))
            {
                callBack?.Invoke();
            }
        }
    }


    // =========================================================
    // Toggle
    // =========================================================

    public class GUIToggle : GUIItem
    {
        public bool Value;

        public Action<bool> onChanged;


        public GUIToggle(
            GUIContent content,
            Rect rect,
            bool value = false,
            Action<bool> onChanged = null,
            GUIStyle style = null)
            : base(content, rect, style)
        {
            Value = value;

            this.onChanged = onChanged;
        }


        public override void Draw()
        {
            bool newValue = GUI.Toggle(
                DrawRect,
                Value,
                Content,
                Style ?? GUI.skin.toggle
            );

            if (newValue == Value)
                return;

            Value = newValue;

            onChanged?.Invoke(Value);
        }
    }


    // =========================================================
    // Slider
    //
    // Style      = Slider Track
    // ThumbStyle = Slider Handle
    // =========================================================

    public class GUISlider : GUIItem
    {
        public float Value;
        public float Min;
        public float Max;
        public bool Vertical;
        public GUIStyle ThumbStyle;
        public Action<float> onChanged;

        public GUISlider(Rect rect, float value, float min, float max,
            Action<float> onChanged = null, GUIStyle style = null, GUIStyle thumbStyle = null,
            bool vertical = false)
            : base(GUIContent.none, rect, style)
        {
            Value = value;
            Min = min;
            Max = max;
            this.onChanged = onChanged;
            ThumbStyle = thumbStyle;
            Vertical = vertical;
        }

        public override void Draw()
        {
            if (!isVisible) return;

            bool oldEnabled = GUI.enabled;
            GUI.enabled = isEnabled && isInteractable;

            // GUI.VerticalSlider takes (top, bottom), not (min, max).
            // Max first, so the bar fills upward like every gauge ever built.
            float value = Vertical
                ? GUI.VerticalSlider(
                    DrawRect,
                    Value,
                    Max,
                    Min,
                    Style ?? GUI.skin.verticalSlider,
                    ThumbStyle ?? GUI.skin.verticalSliderThumb)
                : GUI.HorizontalSlider(
                    DrawRect,
                    Value,
                    Min,
                    Max,
                    Style ?? GUI.skin.horizontalSlider,
                    ThumbStyle ?? GUI.skin.horizontalSliderThumb);

            GUI.enabled = oldEnabled;

            if (Mathf.Approximately(value, Value)) return;
            Value = value;
            onChanged?.Invoke(Value);
        }
    }

         public class GUIImage : GUIItem
    {
        public Texture Texture;
        public Sprite Sprite;
        public ScaleMode ScaleMode;

        public GUIImage(
            Texture texture,
            Rect rect,
            ScaleMode scaleMode = ScaleMode.StretchToFill,
            GUIStyle style = null)
            : base(GUIContent.none, rect, style)
        {
            Texture = texture;
            Sprite = null;
            ScaleMode = scaleMode;
        }

        public GUIImage(
            Sprite sprite,
            Rect rect,
            ScaleMode scaleMode = ScaleMode.ScaleToFit,
            GUIStyle style = null)
            : base(GUIContent.none, rect, style)
        {
            Texture = null;
            Sprite = sprite;
            ScaleMode = scaleMode;
        }

        public override void Draw()
        {
            if (!isVisible)
                return;

            if (Sprite != null)
            {
                Texture2D texture = Sprite.texture;
                Rect r = Sprite.textureRect;

                Rect uv = new Rect(
                    r.x / texture.width,
                    r.y / texture.height,
                    r.width / texture.width,
                    r.height / texture.height
                );

                GUI.DrawTextureWithTexCoords(
                    DrawRect,
                    texture,
                    uv,
                    true
                );

                return;
            }

            if (Texture != null)
                GUI.DrawTexture(
                    DrawRect,
                    Texture,
                    ScaleMode
                );
        }

        public void SetTexture(Texture texture)
        {
            Texture = texture;
            Sprite = null;
        }

        public void SetSprite(Sprite sprite)
        {
            Sprite = sprite;
            Texture = null;
        }
    }


    // =========================================================
    // TextField
    // =========================================================

    public class GUITextField : GUIItem
    {
        public string Value;

        public Action<string> onChanged;


        public GUITextField(
            Rect rect,
            string value = "",
            Action<string> onChanged = null,
            GUIStyle style = null)
            : base(
                GUIContent.none,
                rect,
                style)
        {
            Value = value;

            this.onChanged = onChanged;
        }


        public override void Draw()
        {
            string newValue = GUI.TextField(
                DrawRect,
                Value,
                Style ?? GUI.skin.textField
            );

            if (newValue == Value)
                return;

            Value = newValue;

            onChanged?.Invoke(Value);
        }
    }
}