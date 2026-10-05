using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace IMGUI
{
    // 글자가 칸보다 크면 에디터 콘솔에 한 번 경고한다. GUIItem이 그려지는 두 자리(GUIManager·GUIGroup)가 부른다.
    // ShipStatusHud처럼 GUI.Label을 직접 부르는 OnGUI는 여기를 안 지나서 못 잡는다.
    public static class ClipCheck
    {
        private static readonly HashSet<string> _warned = new();

        [Conditional("UNITY_EDITOR")]
        public static void Check(GUIItem item)
        {
            if (Event.current.type != EventType.Repaint || item is GUIGroup) return;

            GUIContent c = item.Content;
            GUIStyle s = item.Style;
            if (c == null || s == null || string.IsNullOrEmpty(c.text)) return;

            Rect r = item.Rect;
            if (r.width <= 1f || r.height <= 1f) return;   // 등장 애니메이션 중이거나 ImGui의 "폭 자동"

            Vector2 need = s.wordWrap
                ? new Vector2(r.width, s.CalcHeight(c, r.width))
                : s.CalcSize(c);

            // 이 UI의 스타일은 전부 clipping = Overflow라(GUIStyleMaker) 글자가 잘리지 않고 칸 밖으로 삐져나온다.
            // 한 줄짜리가 행 높이보다 몇 px 큰 것은 안 보여서 뺀다(첫 판에서 "속도"·"거리"가 전부 걸렸다).
            // 보이는 것은 둘 - 가로로 상자 밖에 나간 글자, 줄바꿈으로 줄이 늘어 아래 행을 덮는 글자.
            float line = s.CalcHeight(new GUIContent("가"), 10000f);
            bool wide = !s.wordWrap && need.x > r.width + 1f;
            bool tall = s.wordWrap && need.y > Mathf.Max(r.height, line) + line * 0.5f;
            if (!wide && !tall) return;

            // 숫자는 키에서 뺀다 - 거리 라벨이 1 m마다 새 경고를 내서 첫 판에 600줄이 쌓였다.
            string key = System.Text.RegularExpressions.Regex.Replace(c.text, @"\d", "") + r.size;
            if (!_warned.Add(key)) return;

            string where = "";
            for (GUIGroup g = item.Parent; g != null; g = g.Parent)
                where = g.GroupName + "/" + where;

            Debug.LogWarning($"[UI 잘림] {where}{item.GetType().Name} \"{c.text.Replace("\n", "⏎")}\" " +
                             $"필요 {need.x:0}x{need.y:0} / 칸 {r.width:0}x{r.height:0} ({s.fontSize}pt)");
        }
    }
}
