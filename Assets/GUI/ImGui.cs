using System;
using System.Collections.Generic;
using UnityEngine;

namespace IMGUI
{
    /// <summary>
    /// 즉시 모드 선언 층. **매 프레임 선언하고, 선언 안 된 것은 알아서 사라진다.**
    ///
    /// 리테인드 모드의 아픈 곳은 그리기가 아니라 수명이다. GUIItem을 만들고 Register한 쪽이
    /// 언젠가 Unregister도 해야 하는데, 그 "언젠가"가 조건 분기 안으로 들어가는 순간 새는
    /// 위젯이 생긴다. 여기서는 **안 부르는 것이 곧 지우는 것**이다.
    ///
    /// **그리기는 하나도 안 바뀐다.** GUIItem·GUIStyleMaker·Tween·GUIManager의 그리기 루프를
    /// 그대로 쓴다. 이 클래스가 하는 일은 캐시를 뒤져 없으면 만들고, 이번 프레임에 안 불린
    /// 것을 걷어내는 것뿐이다 - 즉시 모드는 **선언에만** 있다.
    ///
    /// 그래서 상태가 프레임을 넘어 산다. 호버, 텍스트필드 내용, 트윈 진행도는 캐시에 있는 그
    /// GUIItem이 계속 들고 있다. 매 프레임 위젯을 새로 만드는 진짜 즉시 모드가 못 하는 일이다.
    ///
    /// <example><code>
    /// private void Update()
    /// {
    ///     ImGui.Begin();
    ///
    ///     ImGui.Label("line0", rect, "함장님, 좌현에 접촉");
    ///
    ///     if (ImGui.Button("skip", rect2, "건너뛰기"))
    ///         SkipDialogue();
    /// }
    /// </code></example>
    /// </summary>
    public static class ImGui
    {
        private sealed class Entry
        {
            public GUIItem item;
            public int lastFrame;

            /// <summary>
            /// 버튼이 눌렸다. **엔트리에 두는 것이 중요하다** - 위젯이 걷힐 때 같이 사라진다.
            /// 옆에 사전을 따로 두면 그 사전만 남아서 조용히 자란다.
            /// </summary>
            public bool pressed;
        }

        private static readonly Dictionary<string, Entry> Cache = new();
        private static readonly List<string> Stale = new();

        private static int _lastBegin = -1;

        // -----------------------------------------------------------------
        // 프레임
        // -----------------------------------------------------------------

        /// <summary>
        /// 이번 프레임의 선언을 시작한다. 지난 프레임에 아무도 안 부른 위젯이 여기서 걷힌다.
        ///
        /// **Update에서만 부른다. OnGUI에서 부르면 안 된다.** OnGUI는 한 프레임에 여러 번
        /// 불린다(Layout, Repaint, 입력 이벤트마다). 거기서 선언하면 같은 위젯을 프레임당
        /// N번 손대게 되고, 그리기 도중에 Register/Unregister가 섞인다. GUIManager가 흔들림
        /// 감쇠를 Update에서 하는 것과 정확히 같은 이유다.
        ///
        /// 안 부르면 아무것도 안 사라진다 - 조용히 리테인드로 돌아가므로 "왜 안 지워지지"의
        /// 첫 번째 용의자다. 그래서 <see cref="LiveCount"/>가 있다.
        /// </summary>
        public static void Begin()
        {
            int frame = Time.frameCount;

            if (_lastBegin == frame)
                return;   // 여러 스크립트가 각자 불러도 수확은 프레임당 한 번

            _lastBegin = frame;
            Stale.Clear();

            // 지난 프레임을 통째로 건너뛴 것만 걷는다. 이번 프레임 것은 아직 선언 전이다.
            foreach (KeyValuePair<string, Entry> pair in Cache)
            {
                if (pair.Value.lastFrame < frame - 1)
                    Stale.Add(pair.Key);
            }

            for (int i = 0; i < Stale.Count; i++)
            {
                Retire(Cache[Stale[i]].item);
                Cache.Remove(Stale[i]);
            }
        }

        /// <summary>선언한 것을 전부 버린다. 씬을 갈아탈 때.</summary>
        public static void Clear()
        {
            foreach (KeyValuePair<string, Entry> pair in Cache)
                Retire(pair.Value.item);

            Cache.Clear();
            _lastBegin = -1;
        }

        /// <summary>
        /// 위젯을 걷는다. **트윈을 먼저 죽인다** - <see cref="GUITween"/>은 핸들러를
        /// running/fading/scaling 정적 사전에 넣어 두고 완주할 때만 지운다. 즉시 모드
        /// 위젯은 선언을 그만두는 순간 사라지므로 완주하지 못하고, 그 사전만 남아 조용히
        /// 자란다. Unregister는 Items에서만 빼지 사전은 모른다.
        ///
        /// 그래서 즉시 모드에서는 애초에 트윈을 걸지 않는 편이 낫다 - 애니메이션 상태를
        /// 선언하는 쪽이 들고 최종값만 매 프레임 대입하면 수명 문제가 존재하지 않는다.
        /// </summary>
        private static void Retire(GUIItem item)
        {
            if (item == null)
                return;

            GUITween.Kill(item);
            GUIManager.Unregister(item);
        }

        /// <summary>지금 살아 있는 즉시 모드 위젯 수. 새는지 볼 때 제일 먼저 보는 값.</summary>
        public static int LiveCount => Cache.Count;

        // -----------------------------------------------------------------
        // 표시만 하는 것
        // -----------------------------------------------------------------

        public static GUILabel Label(string id, Rect rect, string text, GUIStyle style = null)
        {
            GUILabel label = Get(id, () => new GUILabel(new GUIContent(text), rect, style));

            label.Content.text = text;
            label.Rect = rect;
            label.Style = style;
            return label;
        }

        public static GUIBoxLabel BoxLabel(string id, Rect rect, string text, GUIStyle style = null)
        {
            GUIBoxLabel box = Get(id, () => new GUIBoxLabel(new GUIContent(text), rect, style));

            box.Content.text = text;
            box.Rect = rect;
            box.Style = style;
            return box;
        }

        public static GUIImage Image(
            string id, Rect rect, Texture texture,
            ScaleMode scaleMode = ScaleMode.StretchToFill, GUIStyle style = null)
        {
            GUIImage image = Get(id, () => new GUIImage(texture, rect, scaleMode, style));

            image.SetTexture(texture);
            image.Rect = rect;
            image.ScaleMode = scaleMode;
            return image;
        }

        public static GUIImage Image(
            string id, Rect rect, Sprite sprite,
            ScaleMode scaleMode = ScaleMode.ScaleToFit, GUIStyle style = null)
        {
            GUIImage image = Get(id, () => new GUIImage(sprite, rect, scaleMode, style));

            image.SetSprite(sprite);
            image.Rect = rect;
            image.ScaleMode = scaleMode;
            return image;
        }

        // -----------------------------------------------------------------
        // 입력을 받는 것
        // -----------------------------------------------------------------

        /// <summary>
        /// 눌렸으면 true. **콜백이 아니라 반환값이다** - 그게 즉시 모드의 값어치다. 콜백은
        /// 클로저가 잡은 것을 위젯이 사는 내내 붙들고 있어서, 수명이 헷갈리면 이미 닫힌
        /// 대화상자의 버튼이 지워진 데이터를 건드린다. 반환값은 그 자리에서 끝난다.
        ///
        /// 그리기는 OnGUI라 **눌림은 다음 Update에 보고된다.** 한 프레임 늦지만 클릭은 사람이
        /// 하는 일이라 16 ms는 안 보인다.
        /// </summary>
        public static bool Button(string id, Rect rect, string text, GUIStyle style = null)
        {
            Entry entry = Touch(id);
            GUIButton button = Materialise(entry, id, () =>
            {
                // 클로저가 엔트리를 잡는다. 위젯이 걷히면 이 콜백도 같이 간다.
                Entry captured = entry;
                return new GUIButton(new GUIContent(text), rect,
                    () => captured.pressed = true, style);
            });

            if (button == null)
                return false;

            button.Content.text = text;
            button.Rect = rect;
            button.Style = style;

            bool was = entry.pressed;
            entry.pressed = false;
            return was;
        }

        /// <summary>지금 값을 넣고 지금 값을 받는다. 위젯이 상태를 들고 있어서 왕복이 닫힌다.</summary>
        public static bool Toggle(string id, Rect rect, bool value, string text = "",
            GUIStyle style = null)
        {
            GUIToggle toggle = Get(id,
                () => new GUIToggle(new GUIContent(text), rect, value, null, style));

            toggle.Content.text = text;
            toggle.Rect = rect;
            toggle.Style = style;
            return toggle.Value;
        }

        public static float Slider(string id, Rect rect, float value, float min, float max,
            GUIStyle style = null, GUIStyle thumbStyle = null)
        {
            GUISlider slider = Get(id,
                () => new GUISlider(rect, value, min, max, null, style, thumbStyle));

            slider.Rect = rect;
            slider.Min = min;
            slider.Max = max;
            return slider.Value;
        }

        public static string TextField(string id, Rect rect, string value, GUIStyle style = null)
        {
            GUITextField field = Get(id, () => new GUITextField(rect, value, null, style));

            field.Rect = rect;
            field.Style = style;
            return field.Value;
        }

        // -----------------------------------------------------------------

        private static T Get<T>(string id, Func<T> make) where T : GUIItem
            => Materialise(Touch(id), id, make);

        private static T Materialise<T>(Entry entry, string id, Func<T> make) where T : GUIItem
        {
            T item = Resolve(entry, id, make);

            Reset(item);
            return item;
        }

        /// <summary>
        /// 이번 프레임의 모양을 기본값으로 되돌린다. **즉시 모드의 계약이 여기서 닫힌다.**
        /// 선언은 "이번 프레임에 이것이 이렇게 있다"인데, 이 값들을 안 되돌리면 지난
        /// 프레임에 누가 밀어 넣은 Layer·Opacity가 살아남아 같은 선언이 다른 결과를 낸다.
        /// Rect·text를 매번 대입하면서 이 넷만 리테인드로 새고 있었다.
        ///
        /// **Opacity 기본값이 1인 것이 요점이다.** <see cref="GUIItem.Opacity"/>의 필드
        /// 초기값은 0이고 GUIManager는 그걸 알파에 곱한다 - 안 되돌리면 ImGui로 만든 위젯은
        /// 전부 "선언은 됐는데 안 보이는 것"으로 태어난다.
        ///
        /// isInteractable은 <see cref="GUIItem.Decorative"/>에서 나온다. 장식이 입력
        /// 최상단으로 잡히면 그 밑의 버튼이 통째로 죽는다.
        /// </summary>
        private static void Reset(GUIItem item)
        {
            if (item == null)
                return;

            item.Layer = 0;
            item.Opacity = 1f;
            item.RenderScale = Vector2.one;
            item.isVisible = true;
            item.isEnabled = true;
            item.isInteractable = !item.Decorative;
        }

        /// <summary>
        /// 엔트리에 위젯이 없으면 만들어 등록한다.
        ///
        /// 같은 id를 다른 종류로 다시 선언하면 **바꿔 끼운다.** 캐스트를 그냥 하면
        /// InvalidCastException이 나는데, 그건 대개 id 오타라 프레임 하나가 아니라
        /// 게임이 죽는다. 경고를 남기고 새 것으로 가는 편이 낫다.
        /// </summary>
        private static T Resolve<T>(Entry entry, string id, Func<T> make) where T : GUIItem
        {
            if (entry.item is T existing)
                return existing;

            if (entry.item != null)
            {
                Debug.LogWarning(
                    $"[ImGui] id '{id}'가 {entry.item.GetType().Name}였는데 {typeof(T).Name}로 " +
                    "다시 선언됐다. 같은 id를 두 곳에서 쓰고 있지 않은지 볼 것.");

                Retire(entry.item);
                entry.pressed = false;
            }

            T fresh = make();
            entry.item = fresh;
            GUIManager.Register(fresh);
            return fresh;
        }

        /// <summary>이번 프레임에 선언됐다고 표시한다. 없으면 빈 자리만 만든다.</summary>
        private static Entry Touch(string id)
        {
            if (!Cache.TryGetValue(id, out Entry entry))
            {
                entry = new Entry();
                Cache[id] = entry;
            }

            entry.lastFrame = Time.frameCount;
            return entry;
        }
    }
}
