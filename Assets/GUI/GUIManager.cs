using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;
using UnityEngine.UI;

namespace IMGUI // not I'm GUI.
{
    public class GUIManager : MonoBehaviour
    {
        public static GUIManager instance { get; private set; }
        public static Vector2 ScreenSize => new Vector2(Screen.width, Screen.height);
        public static Vector2 ScreenCenter => ScreenSize * 0.5f;
        public static bool Blocked = false;

        /// <summary>
        /// 전체 UI 배율. 1보다 크면 커진다. 인스펙터에서 플레이 중에도 바로 슬라이더로
        /// 확인할 수 있게 인스턴스 필드로 뒀다 - 값을 정하는 것은 오너 몫이라 여기서
        /// 기본값을 안 밀고 1로 둔다.
        ///
        /// **값 자체가 아니라 <see cref="LogicalWidth"/>/<see cref="LogicalHeight"/>가
        /// 실제로 쓰인다.** 각 화면(HUD·접촉 마커·피격 표시·대사창)의 배치 계산은
        /// 여전히 Screen.width/height 크기의 "논리 화면"에 놓고, <see cref="OnGUI"/>가
        /// 그 결과를 이 배율만큼 키워서 실제 화면에 그린다. 그래야 배율을 올려도
        /// 오른쪽·아래 가장자리에 붙은 패널이 화면 밖으로 밀려나지 않는다 - 위치 좌표
        /// 자체가 이미 줄어든 논리 화면 기준이라, 키운 결과가 정확히 실제 화면을 채운다.
        /// 반대로 Screen.width/height를 그대로 쓰고 그리기만 키우면, 배율만큼 화면
        /// 오른쪽 밖으로 밀려나는 패널이 생긴다.
        /// </summary>
        [SerializeField, Range(0.5f, 2f)] private float uiScale = 1f;

        public static float UiScale => instance != null ? instance.uiScale : 1f;
        // 프레임당 한 번 잰다. Screen.width는 네이티브 호출이라 요소마다 부르면 프레임당 수백 번이고,
        // 더 중요한 것은 **한 프레임 안에서 값이 하나**라는 보장이다 - 리사이즈 프레임에 위쪽 요소와
        // 아래쪽 요소가 다른 폭을 읽지 않는다. Update 전(첫 프레임, 에디터)에는 직접 잰다.
        private static float _logicalW, _logicalH;
        public static float LogicalWidth => _logicalW > 0f ? _logicalW : Screen.width / UiScale;
        public static float LogicalHeight => _logicalH > 0f ? _logicalH : Screen.height / UiScale;

        public static Vector2 MousePos
        {
            get
            {
                if (Mouse.current == null)
                    return Vector2.zero;

                Vector2 p = Mouse.current.position.ReadValue();

                // 논리 좌표로 나눈다. 지금은 클릭을 먹는 위젯이 하나도 없어서(전부
                // Decorative 표시 전용) 영향이 없지만, 나중에 인터랙티브 위젯이 배율
                // 적용 뒤 논리 좌표로 자리를 잡으면 이 나눗셈이 없을 때만 클릭이 어긋난다.
                return new Vector2(p.x, Screen.height - p.y) / UiScale;
            }
        }

        private static readonly List<GUIItem> Items = new();
        private static readonly HashSet<GUIItem> PendingRemove = new();
        private static readonly HashSet<GUIItem> PendingAdd = new();

        private readonly List<GUIItem> drawRoots = new();

        private static bool isIterating;

        // 화면 흔들기. GUI.matrix를 한 번 밀면 이 캔버스에 그려지는 게 전부 같이 흔들린다 —
        // 카드, 보드, 라벨을 하나씩 흔들 필요가 없다. uGUI였으면 루트 RectTransform에
        // 스크립트를 붙이고 자식 좌표를 신경 써야 하는 일이다
        private static float shakeTime;
        private static float shakeDuration;
        private static float shakeStrength;
        private static Vector2 shakeOffset;

        // strength는 픽셀 단위 최대 진폭.
        //
        // 약한 흔들림이 센 걸 덮어쓰면 결정적인 순간(2승 4칸)이 잔흔들림에 묻힌다.
        // 이미 더 센 게 돌고 있으면 무시한다
        public static void Shake(float strength = 12f, float duration = 0.25f)
        {
            if (shakeTime > 0f && strength < shakeStrength)
                return;

            shakeStrength = strength;
            shakeDuration = Mathf.Max(0.01f, duration);
            shakeTime = shakeDuration;
        }

        [SerializeField] private Image blocker;

        private Canvas blockerCanvas;

        private void Awake()
        {
            instance = this;

            PendingRemove.Clear();
            PendingAdd.Clear();
            Items.Clear();

            if (blocker != null)
            {
                blockerCanvas = blocker.GetComponent<Canvas>();

                if (blockerCanvas == null)
                    blockerCanvas = blocker.gameObject.AddComponent<Canvas>();

                if (blocker.GetComponent<GraphicRaycaster>() == null)
                    blocker.gameObject.AddComponent<GraphicRaycaster>();

                blockerCanvas.overrideSorting = true;
                blockerCanvas.sortingOrder = 32000;

                blocker.raycastTarget = false;
                blocker.enabled = false;
                blockerCanvas.enabled = false;
            }
        }

        public void SetBlocked(bool blocked)
        {
            Blocked = blocked;

            if (blocker == null)
                return;

            blocker.transform.SetAsLastSibling();

            blockerCanvas.enabled = blocked;
            blocker.enabled = blocked;
            blocker.raycastTarget = blocked;
        }

        public static void Register(GUIItem item)
        {
            if (item == null)
                return;

            if (isIterating)
            {
                PendingAdd.Add(item);
                return;
            }

            if (!Items.Contains(item))
                Items.Add(item);
        }

        public static void RegisterAndSetParent(GUIGroup parent, GUIItem child)
        {
            if (parent == null || child == null)
                return;

            Register(child);
            parent.Add(child);
        }

        public static void Unregister(GUIItem item)
        {
            if (item == null)
                return;

            if (item is GUIGroup group)
            {
                // 뒤에서부터 도는 것이 필수다. 그리는 중이 아니면 아래 DetachFromParent가
                // 이 목록에서 자기를 빼므로, 앞에서부터 돌면 한 칸씩 건너뛴다.
                for (int i = group.Childrens.Count - 1; i >= 0; i--)
                    Unregister(group.Childrens[i]);
            }

            // **그리는 중에는 아무것도 안 만진다.** GUIGroup.DrawChildren이 childrens를
            // foreach로 도는데, 여기서 부모의 목록을 건드리면 그 순회가 던진다. 그리기가
            // 끝나고 FlushChanges가 같은 일을 한다 - 목록 둘(Items, 부모의 childrens)이
            // **같은 순간에** 바뀌어야 그룹이 죽은 자식을 드는 창이 안 생긴다.
            if (isIterating)
            {
                PendingRemove.Add(item);
                PendingAdd.Remove(item);
                return;
            }

            // 부모의 자식 목록에서도 뺀다. 예전에는 Items에서만 뺐다 - 리테인드로만 쓸
            // 때는 그룹과 자식이 대개 같이 죽어서 안 드러났지만, 즉시 모드는 자식 하나만
            // 선언을 그만두는 일이 상시다. 그러면 그룹이 죽은 자식을 계속 들고 그리려 든다.
            item.DetachFromParent();
            Items.Remove(item);
        }

        private static void FlushChanges()
        {
            if (PendingRemove.Count > 0)
            {
                // 부모에서 떼는 것도 여기서 한다. Unregister가 그리는 중이라 미뤄 둔
                // 일이고, 그리기 목록에서 빠지는 것과 **같은 순간이어야** 그룹이 죽은
                // 자식을 드는 창이 안 생긴다.
                foreach (GUIItem item in PendingRemove)
                {
                    item.DetachFromParent();
                    Items.Remove(item);
                }

                PendingRemove.Clear();
            }

            if (PendingAdd.Count > 0)
            {
                foreach (GUIItem item in PendingAdd)
                {
                    if (!Items.Contains(item))
                        Items.Add(item);
                }

                PendingAdd.Clear();
            }
        }

        // 감쇠는 반드시 Update에서. OnGUI는 한 프레임에 여러 번(Layout/Repaint/각 입력 이벤트)
        // 불리므로 거기서 시간을 깎으면 흔들림이 프레임레이트와 이벤트 수에 따라 제멋대로 짧아진다
        private void TickShake()
        {
            if (shakeTime <= 0f)
                return;

            shakeTime -= Time.unscaledDeltaTime;

            if (shakeTime <= 0f)
            {
                shakeTime = 0f;
                shakeOffset = Vector2.zero;
                return;
            }

            // 제곱 감쇠. 선형이면 끝까지 미세하게 떨려서 멈추는 순간이 흐릿하다
            float k = shakeTime / shakeDuration;
            float amp = shakeStrength * k * k;

            shakeOffset = new Vector2(
                UnityEngine.Random.Range(-amp, amp),
                UnityEngine.Random.Range(-amp, amp)
            );
        }

        private void Update()
        {
            _logicalW = Screen.width / uiScale;
            _logicalH = Screen.height / uiScale;
            TickShake();

            isIterating = true;

            for (int i = 0; i < Items.Count; i++)
            {
                GUIItem item = Items[i];

                if (!PendingRemove.Contains(item))
                    item.Tick(Time.unscaledDeltaTime);
            }

            isIterating = false;
            FlushChanges();
        }

        private void OnGUI()
        {
            if(!GUIStyleMaker.Initialized)
            GUIStyleMaker.Initialize();

            GUI.depth = 0;   // 다른 OnGUI(ShipStatusHud, depth 1)보다 위. 낮은 값이 위다.

            isIterating = true;

            // 배율은 이벤트 종류를 안 가리고 건다 - Event.current.mousePosition도 같이
            // 변환돼야 클릭 판정(GetTopMouseLayer)이 그리기와 같은 논리 좌표에서
            // 이뤄진다. uiScale은 인스펙터 슬라이더로만 바뀌는 안정값이라 걸어도 안전하다.
            //
            // 흔들림은 반대로 Repaint에서만 건다 - 프레임마다 요동치는 값을 클릭
            // 이벤트에 걸면 흔들리는 동안 클릭 좌표가 무작위로 밀린다. 그리기만
            // 흔들고 판정은 원래 자리에서 하면 "보이는 건 흔들리는데 누르면 눌린다"가 된다.
            //
            // 순서는 흔들림이 바깥(왼쪽)이다 - shakeOffset은 "화면 픽셀 단위 최대
            // 진폭"이라 배율과 무관하게 항상 같은 픽셀 수만큼 흔들려야 한다. 안쪽에
            // 두면 배율만큼 흔들림도 커진다.
            Matrix4x4 savedMatrix = GUI.matrix;
            bool scaling = !Mathf.Approximately(uiScale, 1f);
            bool shaking =
                Event.current.type == UnityEngine.EventType.Repaint &&
                shakeOffset != Vector2.zero;

            if (scaling || shaking)
            {
                Matrix4x4 m = Matrix4x4.Scale(new Vector3(uiScale, uiScale, 1f));

                if (shaking)
                    m = Matrix4x4.TRS(shakeOffset, Quaternion.identity, Vector3.one) * m;

                GUI.matrix = m;
            }

            BuildDrawRoots();

            bool pointerEvent = IsPointerEvent(Event.current.type);

            int topMouseLayer = pointerEvent
                ? GetTopMouseLayer()
                : int.MinValue;

            for (int i = 0; i < drawRoots.Count; i++)
                DrawRoot(drawRoots[i], pointerEvent, topMouseLayer);

            isIterating = false;
            FlushChanges();

            if (!string.IsNullOrEmpty(GUI.tooltip))
                GUI.Label(new Rect(10f, 10f, 300f, 30f), GUI.tooltip);

            // 안 되돌리면 이 프레임 이후 다른 OnGUI(에디터 오버레이 포함)까지 밀린 채로 그려진다
            if (scaling || shaking)
                GUI.matrix = savedMatrix;
        }

        private void DrawRoot(GUIItem item, bool pointerEvent, int topMouseLayer)
        {
            if (item == null || !item.isVisible)
                return;

            bool oldEnabled = GUI.enabled;
            Color oldColor = GUI.color;

            // Enabled만 비주얼 Disabled 상태에 영향을 줌.
            GUI.enabled =
                oldEnabled &&
                item.isEnabled;

            // Interactable은 입력 이벤트가 들어올 때만 차단.
            // Repaint에서는 GUI.enabled를 건드리지 않으므로 정상 색상으로 보임.
            if (pointerEvent &&
                (!item.isInteractable ||
                item.Layer < topMouseLayer))
            {
                GUI.enabled = false;
            }

            GUI.color = new Color(
                oldColor.r,
                oldColor.g,
                oldColor.b,
                oldColor.a * Mathf.Clamp01(item.Opacity)
            );

            if (item is GUIGroup group)
                group.DrawChildren();
            else
                item.Draw();

            GUI.enabled = oldEnabled;
            GUI.color = oldColor;
        }

        private void BuildDrawRoots()
        {
            drawRoots.Clear();

            for (int i = 0; i < Items.Count; i++)
            {
                GUIItem item = Items[i];

                if (item == null)
                    continue;

                if (PendingRemove.Contains(item))
                    continue;

                if (!item.isVisible)
                    continue;

                // 자식은 GUIGroup이 재귀적으로 그림.
                if (item.Parent != null)
                    continue;

                drawRoots.Add(item);
            }

            drawRoots.Sort((a, b) => a.Layer.CompareTo(b.Layer));
        }

        private static bool IsPointerEvent(UnityEngine.EventType type)
        {
            return
                type == UnityEngine.EventType.MouseDown ||
                type == UnityEngine.EventType.MouseUp ||
                type == UnityEngine.EventType.MouseDrag ||
                type == UnityEngine.EventType.ScrollWheel;
        }

        private int GetTopMouseLayer()
        {
            Vector2 mouse =
                Event.current.mousePosition;

            int topLayer =
                int.MinValue;

            for (int i = 0; i < drawRoots.Count; i++)
            {
                GUIItem item =
                    drawRoots[i];

                if (!item.isVisible)
                    continue;

                // 입력을 받을 생각이 없는 장식은
                // Layer input 검사에서도 제외.
                if (!item.isInteractable)
                    continue;

                if (!item.DrawRect.Contains(mouse))
                    continue;

                if (item.Layer > topLayer)
                    topLayer = item.Layer;
            }

            return topLayer;
        }
    }
}