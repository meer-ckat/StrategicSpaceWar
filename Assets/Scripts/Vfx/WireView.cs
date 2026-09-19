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

    /// <summary>급전 RADIANCE / 살았지만 0 V STEEL / 끊김 BREACH. 꼭짓점엔 점 - 이어진 자리가 보여야 한다.</summary>
    private void Paint(LineMesh lines, List<Ship.WireSeg> segs)
    {
        lines.Clear();
        _dots.Clear();
        float w = Ballistics.WireViewWidth;

        for (int i = 0; i < segs.Count; i++)
        {
            Ship.WireSeg s = segs[i];
            Color c = s.state switch
            {
                Ship.WireState.Live => Palette.Radiance,
                Ship.WireState.Dark => Palette.Steel,
                _ => Palette.Breach,
            };
            lines.Line(s.a, s.b, w, c);

            if (_dots.Add(Vector2Int.RoundToInt(s.a * Ballistics.SubGrid))) lines.Dot(s.a, w * 1.6f, c);
            if (_dots.Add(Vector2Int.RoundToInt(s.b * Ballistics.SubGrid))) lines.Dot(s.b, w * 1.6f, c);
        }

        lines.Apply();
    }
}
