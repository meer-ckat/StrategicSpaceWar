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

        public static Vector2 MousePos
        {
            get
            {
                if (Mouse.current == null)
                    return Vector2.zero;

                Vector2 p = Mouse.current.position.ReadValue();
                return new Vector2(p.x, Screen.height - p.y);
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
                for (int i = group.Childrens.Count - 1; i >= 0; i--)
                    Unregister(group.Childrens[i]);
            }

            if (isIterating)
            {
                PendingRemove.Add(item);
                PendingAdd.Remove(item);
                return;
            }

            Items.Remove(item);
        }

        private static void FlushChanges()
        {
            if (PendingRemove.Count > 0)
            {
                foreach (GUIItem item in PendingRemove)
                    Items.Remove(item);

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
            // GUI.skin을 사용하는 초기화는 반드시 여기서.
            GUIStyleMaker.Initialize();

            isIterating = true;

            // 흔들림은 Repaint에서만 건다.
            //
            // GUI.matrix는 Event.current.mousePosition까지 같이 변환하므로 입력 이벤트에도 걸면
            // 흔들리는 동안 클릭 좌표가 프레임마다 무작위로 밀린다. 그리기만 흔들고
            // 판정은 원래 자리에서 하면 "보이는 건 흔들리는데 누르면 눌린다"가 된다
            Matrix4x4 savedMatrix = GUI.matrix;
            bool shaking =
                Event.current.type == UnityEngine.EventType.Repaint &&
                shakeOffset != Vector2.zero;

            if (shaking)
                GUI.matrix = Matrix4x4.TRS(shakeOffset, Quaternion.identity, Vector3.one);

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
            if (shaking)
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
                if (!string.IsNullOrEmpty(item.Parent))
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