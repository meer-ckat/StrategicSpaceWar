using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 전선 오버레이. 기압 보기(Tab, <see cref="RoomView.Showing"/>)와 정지 회로도에서 켜진다 - "속을 본다" 모드다.
/// 배마다 자식 오브젝트 하나에 구간 = 사각 하나짜리 메시(<see cref="LineMesh"/>). 상태가 바뀔 때
/// (<see cref="Ship.PowerVersion"/>)만 다시 짓는다.
/// </summary>
public sealed class WireView : MonoBehaviour
{
    private const int SortingOrder = 156;   // 회로도 덮개(150)·방(152)·윤곽(154) 위

    private sealed class Overlay
    {
        public LineMesh lines;
        public MeshRenderer renderer;
        public int version = -1;
        public bool shown;
    }

    private readonly Dictionary<Ship, Overlay> _overlays = new();
    private readonly List<Ship> _stale = new();
    private readonly List<Ship.WireSeg> _segs = new();
    private readonly HashSet<Vector2Int> _dots = new();
    private MaterialPropertyBlock _props;   // 필드 초기화 = 생성자. 네이티브 객체는 Awake부터

    private void Awake() => _props = new MaterialPropertyBlock();
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var go = new GameObject("Wire View");
        DontDestroyOnLoad(go);
        go.AddComponent<WireView>();
    }

    private void LateUpdate()
    {
        bool show = PauseControl.Schematic && PauseControl.Focus != PauseControl.Layer.Air;

        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship ship = Ship.All[i];
            if (ship == null || !ship.HasWiring) continue;

            if (!_overlays.TryGetValue(ship, out Overlay o))
            {
                o = new Overlay { lines = new LineMesh() };
                o.renderer = LineMesh.Attach(ship.transform, "wires", o.lines.mesh, SortingOrder);
                _overlays[ship] = o;
            }

            if (o.renderer == null) continue;

            if (o.shown != show)
            {
                o.shown = show;
                o.renderer.enabled = show;
            }

            if (!show) continue;

            _props.SetColor(ColorId, new Color(1f, 1f, 1f, PauseControl.Blend));
            o.renderer.SetPropertyBlock(_props);

            if (o.version == ship.PowerVersion) continue;

            o.version = ship.PowerVersion;
            ship.CollectWireSegments(_segs);
            Paint(o.lines, _segs);
        }

        _stale.Clear();
        foreach (KeyValuePair<Ship, Overlay> pair in _overlays)
            if (pair.Key == null || pair.Value.renderer == null)
                _stale.Add(pair.Key);

        foreach (Ship gone in _stale)
        {
            if (_overlays.TryGetValue(gone, out Overlay o) && o.lines != null)
                Destroy(o.lines.mesh);
            _overlays.Remove(gone);
        }
    }

    /// <summary>
    /// 급전 RADIANCE / 살았지만 0 V STEEL / 끊김 BREACH / 차단기 HEAT·내가 연 것 TELEMETRY. 꼭짓점엔 점.
    ///
    /// **색만으로는 안 갈린다.** 급전(노랑)·차단(주황)·단선(빨강)이 전부 난색이고 Bloom이 번져서, 줄이 가늘면
    /// 셋이 한 색으로 읽힌다. 그래서 **끊긴 것은 안 잇는다** - 가운데를 비우고 접점 둘만 남긴 뒤, 단선은 X를
    /// 긋고 차단기는 열린 칼날을 비스듬히 그린다(회로도 기호 그대로). 색을 못 읽어도 모양이 답을 준다.
    /// </summary>
    private void Paint(LineMesh lines, List<Ship.WireSeg> segs)
    {
        lines.Clear();
        _dots.Clear();
        float w = Ballistics.WireViewWidth;

        for (int i = 0; i < segs.Count; i++)
        {
            Ship.WireSeg s = segs[i];
            bool live = s.state == Ship.WireState.Live;
            bool cut = s.state == Ship.WireState.Cut;

            Color c = live ? Palette.Radiance
                : !cut ? Palette.Steel
                : s.tripped ? (s.manual ? Palette.Telemetry : Palette.Heat) : Palette.Breach;

            if (!cut)
            {
                // 0 V는 가늘게. 전류가 흐르는 줄이 굵어야 한눈에 계통이 보인다.
                lines.Line(s.a, s.b, live ? w : w * 0.55f, c);
            }
            else
            {
                Vector2 d = s.b - s.a;
                float len = d.magnitude;
                Vector2 dir = len > 1e-4f ? d / len : Vector2.right;
                float gap = Mathf.Min(len * 0.6f, 0.7f);
                Vector2 p = s.a + dir * (len - gap) * 0.5f, q = s.b - dir * (len - gap) * 0.5f;

                lines.Line(s.a, p, w * 0.55f, c);
                lines.Line(q, s.b, w * 0.55f, c);
                lines.Dot(p, w * 1.2f, c);
                lines.Dot(q, w * 1.2f, c);

                Vector2 mid = (p + q) * 0.5f;
                var perp = new Vector2(-dir.y, dir.x);

                if (s.tripped)
                {
                    // 열린 칼날: 한쪽 접점에서 비스듬히 들려 있다. 닫으면 저 자리로 내려온다는 그림.
                    lines.Line(p, p + (dir * 0.75f + perp * 0.6f) * gap, w * 0.8f, c);
                }
                else
                {
                    // 끊어진 자리 X.
                    float r = Mathf.Min(gap, 0.5f) * 0.45f;
                    lines.Line(mid - (dir + perp) * r, mid + (dir + perp) * r, w * 0.7f, c);
                    lines.Line(mid - (dir - perp) * r, mid + (dir - perp) * r, w * 0.7f, c);
                }
            }

            if (_dots.Add(Vector2Int.RoundToInt(s.a * Ballistics.SubGrid))) lines.Dot(s.a, w * 1.6f, c);
            if (_dots.Add(Vector2Int.RoundToInt(s.b * Ballistics.SubGrid))) lines.Dot(s.b, w * 1.6f, c);
        }

        lines.Apply();
    }
}
