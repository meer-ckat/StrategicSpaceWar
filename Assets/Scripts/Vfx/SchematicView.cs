using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 정지(Space) 회로도의 바닥과 상자. 덮개(Blueprint 셰이더)가 배경(별·성운 카메라의 그림)을 VOID + 월드 1 m 격자로
/// 갈아 끼우고 - 판과 같은 건조 전선(<see cref="PauseControl.Front"/>)이 지나며 - 그 위에 모듈 상자를 그린다. 판의 윤곽은 여기서 안 그린다 - PlateSkin의 건조 와이어프레임(격납고 그 효과)이
/// 판 테두리를 자기 색으로 낸다(<see cref="ArmorSkin.SetBuildFront"/>, PauseControl이 쓸어 넘긴다).
///
/// 정렬: 덮개 -20(판 0·후면 -10보다 아래 - 와이어프레임 사이로 VOID가 비친다) &lt; 방 152 &lt; 상자 154 &lt; 전선 156.
/// 값은 목업에서 오너가 골랐다(2026-09-19).
/// </summary>
public sealed class SchematicView : MonoBehaviour
{
    public const int CoverOrder = -20;
    public const int BoxOrder = 154;

    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int VoidId = Shader.PropertyToID("_Void");
    private static readonly int SteelId = Shader.PropertyToID("_Steel");
    private static readonly int DimId = Shader.PropertyToID("_Dim");
    private static readonly int GridAlphaId = Shader.PropertyToID("_GridAlpha");
    private static readonly int FrontId = Shader.PropertyToID("_Front");
    private static readonly int BandId = Shader.PropertyToID("_Band");
    private static readonly int FadeId = Shader.PropertyToID("_Fade");
    private static readonly int ShipPosId = Shader.PropertyToID("_ShipPos");
    private static readonly int ShipRotId = Shader.PropertyToID("_ShipRot");

    private static Material _coverMaterial;

    private sealed class Overlay
    {
        public LineMesh lines;
        public MeshRenderer renderer;
        public int plates = -1, modules = -1, version = -1;
        public PauseControl.Layer focus;
        public bool shown;
    }

    private readonly Dictionary<Ship, Overlay> _overlays = new();
    private readonly List<Ship> _stale = new();
    // 필드 초기화는 생성자다 - 네이티브 짝이 있는 것(MaterialPropertyBlock·Mesh·Material)은 Awake부터.
    private MaterialPropertyBlock _props;

    private void Awake() => _props = new MaterialPropertyBlock();

    private MeshRenderer _cover;
    private Camera _coverCam;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var go = new GameObject("Schematic View");
        DontDestroyOnLoad(go);
        go.AddComponent<SchematicView>();
    }

    private void LateUpdate()
    {
        bool show = PauseControl.Schematic;
        float blend = PauseControl.Blend;
        Cover(show, blend);

        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship ship = Ship.All[i];
            if (ship == null || ship.Map == null) continue;

            if (!_overlays.TryGetValue(ship, out Overlay o))
            {
                o = new Overlay { lines = new LineMesh() };
                o.renderer = LineMesh.Attach(ship.transform, "schematic", o.lines.mesh, BoxOrder);
                _overlays[ship] = o;
            }

            if (o.renderer == null) continue;

            if (o.shown != show)
            {
                o.shown = show;
                o.renderer.enabled = show;
            }

            if (!show) continue;

            _props.SetColor(ColorId, new Color(1f, 1f, 1f, blend));
            o.renderer.SetPropertyBlock(_props);

            int plates = ship.shipArmors.Count;
            int modules = ship.shipGuns.Count + ship.shipCriticals.Count + ship.shipEngines.Count + ship.shipTanks.Count;

            if (o.plates == plates && o.modules == modules && o.version == ship.PowerVersion && o.focus == PauseControl.Focus)
                continue;

            o.plates = plates; o.modules = modules; o.version = ship.PowerVersion; o.focus = PauseControl.Focus;
            Build(ship, o.lines);
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

    /// <summary>화면 전체를 VOID로. 카메라 크기를 따라가고, 카메라가 바뀌면 다시 붙는다. 전환 중엔 blend만큼.</summary>
    private void Cover(bool show, float blend)
    {
        Camera cam = Camera.main;

        if (cam == null)
            return;

        if (_cover == null || _coverCam != cam)
        {
            if (_cover != null) Destroy(_cover.gameObject);

            var go = new GameObject("schematic cover");
            go.transform.SetParent(cam.transform, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, 0f, 1f);
            go.transform.localRotation = Quaternion.identity;

            var mesh = new Mesh { name = "cover" };
            mesh.vertices = new[] { new Vector3(-.5f, -.5f), new Vector3(.5f, -.5f), new Vector3(.5f, .5f), new Vector3(-.5f, .5f) };
            Color c = Palette.Void;
            mesh.colors = new[] { c, c, c, c };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            _cover = go.AddComponent<MeshRenderer>();
            // 빌드에 넣으려면 PlateSkin·RearSkin처럼 Always Included Shaders에 등록해야 한다.
            _coverMaterial ??= new Material(Shader.Find("SUPERRADIANCE/Blueprint"));
            _cover.sharedMaterial = _coverMaterial;
            _cover.sortingOrder = CoverOrder;
            _cover.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _cover.receiveShadows = false;
            _coverCam = cam;
        }

        if (_cover.enabled != show)
            _cover.enabled = show;

        if (!show)
            return;

        // 배와 배경이 같은 전선으로 갈린다 - 플레이어 배 로컬 축(x+y)에서 Front를 넘은 쪽만 덮는다.
        Ship player = GameManager.Player();
        Vector2 pos = player != null ? (Vector2)player.transform.position : (Vector2)cam.transform.position;
        float rad = player != null ? player.transform.eulerAngles.z * Mathf.Deg2Rad : 0f;

        _props.Clear();
        _props.SetColor(VoidId, Palette.Void);
        _props.SetColor(SteelId, Palette.Steel);
        _props.SetFloat(DimId, Ballistics.PauseDimAlpha);
        _props.SetFloat(GridAlphaId, Ballistics.SchematicGridAlpha);
        _props.SetFloat(FrontId, Mathf.Clamp(PauseControl.Front, -1e5f, 1e5f));
        _props.SetFloat(BandId, ConstructionFx.Band * 2f);
        _props.SetFloat(FadeId, Mathf.SmoothStep(0f, 1f, blend));   // 쓸기 위에 페이드 - 전선 하나로는 뚝 끊긴다
        _props.SetVector(ShipPosId, new Vector4(pos.x, pos.y, 0f, 0f));
        _props.SetVector(ShipRotId, new Vector4(Mathf.Cos(rad), Mathf.Sin(rad), 0f, 0f));
        _cover.SetPropertyBlock(_props);
        _props.Clear();

        float h = cam.orthographicSize * 2f * 1.25f;   // 흔들림·줌 전환에 여유
        _cover.transform.localScale = new Vector3(h * cam.aspect, h, 1f);
    }

    /// <summary>모듈 상자. 기기는 전기 상태 색(원자로 RADIANCE, 급전 HULL, 무전력 BREACH). 층에 따라 뺀다.</summary>
    private void Build(Ship ship, LineMesh lines)
    {
        lines.Clear();
        const float thin = 0.04f;
        PauseControl.Layer focus = PauseControl.Focus;

        // 격자는 덮개 셰이더가 월드 좌표로 그린다 - 여기는 상자뿐.
        Color box = Palette.Steel.WithAlpha(Mathf.Min(1f, Ballistics.SchematicEdgeAlpha + 0.2f));

        if (focus == PauseControl.Layer.All)
        {
            for (int i = 0; i < ship.shipEngines.Count; i++) Box(ship, ship.shipEngines[i], box, thin, lines);
            for (int i = 0; i < ship.shipTanks.Count; i++) Box(ship, ship.shipTanks[i], box, thin, lines);
        }

        if (focus != PauseControl.Layer.Air)
        {
            for (int i = 0; i < ship.shipCriticals.Count; i++)
            {
                CriticalModule m = ship.shipCriticals[i];
                if (m == null) continue;
                if (!m.providesPower && focus == PauseControl.Layer.Power) continue;   // 탄약고는 전기가 아니다
                Color c = !m.providesPower ? box : m.Neutralized ? Palette.Breach : Palette.Radiance;
                Box(ship, m, c, m.providesPower ? thin * 2f : thin, lines);
            }

            for (int i = 0; i < ship.shipGuns.Count; i++)
            {
                Gun g = ship.shipGuns[i];
                if (g == null) continue;
                Color c = g.Neutralized ? Palette.Bulkhead : ship.Powered(g) ? Palette.Hull : Palette.Breach;
                Box(ship, g, c, thin * 1.5f, lines);
            }
        }

        lines.Apply();
    }

    private static void Box(Ship ship, Component m, Color c, float width, LineMesh lines)
    {
        if (m == null || !Ship.StillAboard(m, ship)) return;

        Vector2 size = Vector2.one, offset = Vector2.zero;
        if (m.TryGetComponent(out BoxCollider2D col)) { size = col.size; offset = col.offset; }

        float deg = m.transform.eulerAngles.z - ship.transform.eulerAngles.z;
        Vector2 centre = (Vector2)ship.transform.InverseTransformPoint(m.transform.position) + Ballistics.Rotate(offset, deg);
        lines.Rect(centre, size, deg, width, c);
    }
}
