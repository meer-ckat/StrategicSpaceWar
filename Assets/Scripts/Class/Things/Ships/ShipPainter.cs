#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

/// <summary>
/// Tools > Ship > Ship Painter. 칸을 찍어서 함선을 그리고 JSON으로 뽑는다.
///
/// **런타임을 하나도 안 건드린다.** 여기가 하는 일은 <c>placements</c> 배열을 만드는 것뿐이고,
/// 배를 짓는 것은 여전히 <see cref="ShipBuilder.Spawn"/>이다. 그래서 이 창이 틀려도
/// 시뮬레이션은 안 틀린다 - 틀린 JSON이 나올 뿐이고, 그건 로드할 때 걸린다.
///
/// 좌표 규약은 격자와 같다: col은 오른쪽(뱃머리), row는 **아래로** 증가한다. 화면에 그릴 때
/// row가 곧 y라 손으로 보는 것과 맵이 같은 방향이다.
///
/// v1은 1x1 판만 찍는다. 경사판(rot/size/offset)은 JSON에서 손으로 넣어야 한다 - 마우스로
/// 각도를 고르게 만들면 창이 두 배가 되는데, 경사는 배 한 척에 스무 장 남짓이라 값이 안 맞는다.
/// </summary>
public sealed class ShipPainter : EditorWindow
{
    [System.Serializable]
    private class NameOnly { public string defName; }

    /// <summary>모듈은 격자를 안 차지하므로 판과 따로 든다. 한 칸에 하나씩.</summary>
    private readonly Dictionary<Vector2Int, string> _plates = new();
    private readonly Dictionary<Vector2Int, string> _modules = new();

    private readonly List<string> _plateDefs = new();
    private readonly List<string> _moduleDefs = new();

    private string _brush = "Armor mk5";
    private bool _brushIsModule;

    private string _shipName = "newship";
    private Vector2 _pan = new(40f, 40f);
    private float _zoom = 18f;
    private Vector2 _paletteScroll;
    private string _status = "";

    private const int Margin = 6;

    [MenuItem("Tools/Ship/Ship Painter")]
    private static void Open() => GetWindow<ShipPainter>("Ship Painter").minSize = new Vector2(760, 520);

    private void OnEnable() => BuildPalette();

    /// <summary>
    /// Defs 폴더를 훑어 붓 목록을 만든다. **파일 이름이 아니라 defName을 쓴다** -
    /// `ArmorMk5.json`의 이름은 `Armor mk5`라 둘이 다르다.
    /// </summary>
    private void BuildPalette()
    {
        _plateDefs.Clear();
        _moduleDefs.Clear();

        if (!Directory.Exists(DefDatabase.DefDirectory))
            return;

        foreach (string path in Directory.GetFiles(DefDatabase.DefDirectory, "*.json"))
        {
            NameOnly head = JsonUtility.FromJson<NameOnly>(File.ReadAllText(path));

            if (head == null || string.IsNullOrEmpty(head.defName))
                continue;

            ThingDef def = DefDatabase.Get(head.defName);

            if (def?.MainType == null)
                continue;

            if (typeof(Armor).IsAssignableFrom(def.MainType))
                _plateDefs.Add(head.defName);
            else if (typeof(Gun).IsAssignableFrom(def.MainType)
                  || typeof(Engine).IsAssignableFrom(def.MainType)
                  || typeof(CriticalModule).IsAssignableFrom(def.MainType))
                _moduleDefs.Add(head.defName);
        }

        _plateDefs.Sort();
        _moduleDefs.Sort();
    }

    private void OnGUI()
    {
        DrawToolbar();

        Rect side = new(0f, 22f, 190f, position.height - 22f);
        Rect canvas = new(190f, 22f, position.width - 190f, position.height - 22f);

        DrawPalette(side);
        DrawCanvas(canvas);

        if (!string.IsNullOrEmpty(_status))
            EditorGUI.LabelField(new Rect(196f, position.height - 20f, canvas.width - 12f, 18f), _status);
    }

    private void DrawToolbar()
    {
        using (new GUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            _shipName = EditorGUILayout.TextField(_shipName, EditorStyles.toolbarTextField, GUILayout.Width(160f));

            if (GUILayout.Button("불러오기", EditorStyles.toolbarButton, GUILayout.Width(70f)))
                Load();

            if (GUILayout.Button("저장", EditorStyles.toolbarButton, GUILayout.Width(50f)))
                Save();

            if (GUILayout.Button("비우기", EditorStyles.toolbarButton, GUILayout.Width(60f)))
            {
                _plates.Clear();
                _modules.Clear();
                _status = "";
            }

            GUILayout.Space(12f);
            GUILayout.Label($"판 {_plates.Count}  모듈 {_modules.Count}", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label("좌클릭 칠하기 / 우클릭 지우기 / 가운데 끌기 이동 / 휠 확대", EditorStyles.miniLabel);
        }
    }

    private void DrawPalette(Rect area)
    {
        GUILayout.BeginArea(area);
        _paletteScroll = GUILayout.BeginScrollView(_paletteScroll);

        GUILayout.Label("판", EditorStyles.boldLabel);

        foreach (string def in _plateDefs)
        {
            if (GUILayout.Toggle(!_brushIsModule && _brush == def, def, EditorStyles.miniButton))
            {
                _brush = def;
                _brushIsModule = false;
            }
        }

        GUILayout.Space(8f);
        GUILayout.Label("모듈", EditorStyles.boldLabel);

        foreach (string def in _moduleDefs)
        {
            if (GUILayout.Toggle(_brushIsModule && _brush == def, def, EditorStyles.miniButton))
            {
                _brush = def;
                _brushIsModule = true;
            }
        }

        GUILayout.Space(10f);

        if (GUILayout.Button("검사"))
            _status = Validate();

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    // =========================================================
    // 캔버스
    // =========================================================

    private void DrawCanvas(Rect area)
    {
        GUI.BeginClip(area);
        Rect local = new(0f, 0f, area.width, area.height);
        EditorGUI.DrawRect(local, new Color(0.11f, 0.12f, 0.14f));

        HandleInput(local);

        HashSet<Vector2Int> exterior = Exterior();

        foreach (KeyValuePair<Vector2Int, string> pair in _plates)
            DrawCell(pair.Key, PlateColour(pair.Value), local);

        // 실내는 판 위에 안 겹치므로 뒤에 그려도 된다. 판이 없는 칸만 칠한다.
        foreach (Vector2Int cell in Interior(exterior))
            DrawCell(cell, new Color(0.16f, 0.34f, 0.5f, 0.55f), local);

        foreach (KeyValuePair<Vector2Int, string> pair in _modules)
        {
            Rect r = CellRect(pair.Key);
            r = new Rect(r.x + r.width * 0.22f, r.y + r.height * 0.22f, r.width * 0.56f, r.height * 0.56f);

            if (r.Overlaps(local))
                EditorGUI.DrawRect(r, ModuleColour(pair.Value));
        }

        GUI.EndClip();
    }

    private void DrawCell(Vector2Int cell, Color colour, Rect clip)
    {
        Rect r = CellRect(cell);

        if (!r.Overlaps(clip))
            return;

        EditorGUI.DrawRect(new Rect(r.x, r.y, r.width - 1f, r.height - 1f), colour);
    }

    private Rect CellRect(Vector2Int cell) =>
        new(_pan.x + cell.x * _zoom, _pan.y + cell.y * _zoom, _zoom, _zoom);

    private Vector2Int CellAt(Vector2 point) =>
        new(Mathf.FloorToInt((point.x - _pan.x) / _zoom), Mathf.FloorToInt((point.y - _pan.y) / _zoom));

    private void HandleInput(Rect local)
    {
        Event e = Event.current;
        Vector2 point = e.mousePosition;

        if (!local.Contains(point))
            return;

        if (e.type == EventType.ScrollWheel)
        {
            float before = _zoom;
            _zoom = Mathf.Clamp(_zoom - e.delta.y, 6f, 48f);

            // 커서 아래 칸이 제자리에 남도록 pan을 보정한다. 안 하면 확대할 때마다
            // 보던 곳이 화면 밖으로 달아난다.
            _pan += (point - _pan) * (1f - _zoom / before);
            e.Use();
            Repaint();
            return;
        }

        bool paint = e.button == 0 && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag);
        bool erase = e.button == 1 && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag);
        bool drag = e.button == 2 && e.type == EventType.MouseDrag;

        if (drag)
        {
            _pan += e.delta;
            e.Use();
            Repaint();
            return;
        }

        if (!paint && !erase)
            return;

        Vector2Int cell = CellAt(point);

        if (erase)
        {
            _modules.Remove(cell);
            _plates.Remove(cell);
        }
        else if (_brushIsModule)
        {
            _modules[cell] = _brush;
        }
        else
        {
            _plates[cell] = _brush;
        }

        e.Use();
        Repaint();
    }

    private static Color PlateColour(string def) => def switch
    {
        "Ballistic Door" => new Color(0.85f, 0.72f, 0.30f),
        "Glass" => new Color(0.45f, 0.78f, 0.85f),
        "Lance Armor" => new Color(0.62f, 0.71f, 0.86f),
        "Armor mk3" => new Color(0.88f, 0.89f, 0.90f),
        "Armor mk4" => new Color(0.80f, 0.81f, 0.80f),
        "Armor mk6" => new Color(0.55f, 0.60f, 0.68f),
        _ => new Color(0.70f, 0.71f, 0.72f),
    };

    private static Color ModuleColour(string def) => def switch
    {
        "Reactor" => new Color(0.35f, 0.85f, 0.45f),
        "Magazine" => new Color(0.90f, 0.35f, 0.30f),
        "SuperDuper Engine" => new Color(0.95f, 0.60f, 0.25f),
        _ => new Color(0.55f, 0.75f, 0.95f),
    };

    // =========================================================
    // 밀폐 판정 - 격자의 MarkExterior와 같은 규칙(테두리에서 4방향)
    // =========================================================

    private bool Bounds(out Vector2Int min, out Vector2Int max)
    {
        min = max = default;

        if (_plates.Count == 0)
            return false;

        int c0 = int.MaxValue, c1 = int.MinValue, r0 = int.MaxValue, r1 = int.MinValue;

        foreach (Vector2Int cell in _plates.Keys)
        {
            c0 = Mathf.Min(c0, cell.x); c1 = Mathf.Max(c1, cell.x);
            r0 = Mathf.Min(r0, cell.y); r1 = Mathf.Max(r1, cell.y);
        }

        min = new Vector2Int(c0 - 1, r0 - 1);
        max = new Vector2Int(c1 + 1, r1 + 1);
        return true;
    }

    private HashSet<Vector2Int> Exterior()
    {
        var outside = new HashSet<Vector2Int>();

        if (!Bounds(out Vector2Int min, out Vector2Int max))
            return outside;

        var queue = new Queue<Vector2Int>();
        Vector2Int seed = min;   // 판을 한 겹 넓혔으므로 모서리는 반드시 빈 칸이다

        outside.Add(seed);
        queue.Enqueue(seed);

        Vector2Int[] dirs = { Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down };

        while (queue.Count > 0)
        {
            Vector2Int at = queue.Dequeue();

            foreach (Vector2Int dir in dirs)
            {
                Vector2Int next = at + dir;

                if (next.x < min.x || next.y < min.y || next.x > max.x || next.y > max.y)
                    continue;

                if (_plates.ContainsKey(next) || !outside.Add(next))
                    continue;

                queue.Enqueue(next);
            }
        }

        return outside;
    }

    private List<Vector2Int> Interior(HashSet<Vector2Int> exterior)
    {
        var inside = new List<Vector2Int>();

        if (!Bounds(out Vector2Int min, out Vector2Int max))
            return inside;

        for (int col = min.x; col <= max.x; col++)
        for (int row = min.y; row <= max.y; row++)
        {
            var cell = new Vector2Int(col, row);

            if (!_plates.ContainsKey(cell) && !exterior.Contains(cell))
                inside.Add(cell);
        }

        return inside;
    }

    /// <summary>
    /// 모듈이 얹힐 판. 자기 칸에 판이 있으면 그것, 없으면 4방향 이웃 중 첫 판이다.
    /// 없으면 false - 그런 모듈은 배를 지을 때 조용히 안 생긴다.
    /// </summary>
    private bool MountFor(Vector2Int cell, out Vector2Int mount)
    {
        if (_plates.ContainsKey(cell))
        {
            mount = cell;
            return true;
        }

        foreach (Vector2Int dir in new[] { Vector2Int.down, Vector2Int.up, Vector2Int.right, Vector2Int.left })
        {
            if (_plates.ContainsKey(cell + dir))
            {
                mount = cell + dir;
                return true;
            }
        }

        mount = default;
        return false;
    }

    private string Validate()
    {
        if (_plates.Count == 0)
            return "판이 하나도 없다.";

        HashSet<Vector2Int> exterior = Exterior();
        int inside = Interior(exterior).Count;

        int orphan = 0;
        bool reactor = false, engine = false;

        foreach (KeyValuePair<Vector2Int, string> pair in _modules)
        {
            if (!MountFor(pair.Key, out _))
                orphan++;

            if (pair.Value == "Reactor") reactor = true;
            if (pair.Value == "SuperDuper Engine") engine = true;
        }

        Bounds(out Vector2Int min, out Vector2Int max);

        var notes = new List<string>
        {
            $"{max.x - min.x - 1}x{max.y - min.y - 1}칸",
            $"판 {_plates.Count}",
            $"실내 {inside}",
        };

        if (inside == 0) notes.Add("<!> 밀폐 안 됨 - 공기가 없다");
        if (orphan > 0) notes.Add($"<!> 붙을 판이 없는 모듈 {orphan}개");
        if (!reactor) notes.Add("<!> 원자로 없음 - 조타·조준이 멈춘다");
        if (!engine) notes.Add("<!> 엔진 없음");

        return string.Join("   ", notes);
    }

    // =========================================================
    // 입출력
    // =========================================================

    private void Load()
    {
        string path = ShipDef.PathOf(_shipName);

        if (!File.Exists(path))
        {
            _status = $"없는 파일: {path}";
            return;
        }

        ShipDef def = ShipDef.Load(_shipName);

        if (def == null)
        {
            _status = "읽기 실패. 콘솔을 봐라.";
            return;
        }

        _plates.Clear();
        _modules.Clear();

        foreach (Placement p in def.placements)
        {
            ThingDef thing = DefDatabase.Get(p.def);
            var cell = new Vector2Int(p.col, p.row);

            if (thing?.MainType != null && typeof(Armor).IsAssignableFrom(thing.MainType))
                _plates[cell] = p.def;
            else
                _modules[cell] = p.def;
        }

        _status = Validate();
        Repaint();
    }

    /// <summary>
    /// **배 수치는 안 건드린다.** 파일이 이미 있으면 `placements` 값만 갈아끼운다 -
    /// drag나 angleAccel은 손으로 튜닝한 숫자라 통째로 다시 쓰면 조용히 사라진다.
    /// <see cref="ShipDef.Save"/>가 같은 이유로 같은 짓을 한다.
    /// </summary>
    private void Save()
    {
        if (_plates.Count == 0)
        {
            _status = "판이 없어서 저장 안 한다.";
            return;
        }

        var body = new System.Text.StringBuilder();
        body.Append("[\n");

        bool first = true;

        foreach (KeyValuePair<Vector2Int, string> pair in _plates)
            Append(body, ref first, pair.Value, pair.Key, new Vector2Int(-1, -1));

        int orphan = 0;

        foreach (KeyValuePair<Vector2Int, string> pair in _modules)
        {
            if (!MountFor(pair.Key, out Vector2Int mount))
            {
                orphan++;
                continue;
            }

            Append(body, ref first, pair.Value, pair.Key, mount);
        }

        body.Append("\n  ]");

        string path = ShipDef.PathOf(_shipName);
        Directory.CreateDirectory(ShipDef.DirectoryPath);

        string text = File.Exists(path)
            ? DefKeys.ReplaceTopLevelValue(File.ReadAllText(path), "placements", body.ToString())
            : Template(_shipName, body.ToString());

        if (string.IsNullOrEmpty(text))
        {
            _status = "기존 파일에서 placements를 못 찾았다. 파일을 확인해라.";
            return;
        }

        File.WriteAllText(path, text);
        AssetDatabase.Refresh();

        _status = $"{path}에 썼다. {Validate()}"
                + (orphan > 0 ? $"  (붙을 판이 없어 뺀 모듈 {orphan}개)" : "");
    }

    private static void Append(
        System.Text.StringBuilder sb, ref bool first, string def, Vector2Int cell, Vector2Int mount)
    {
        if (!first)
            sb.Append(",\n");

        first = false;

        sb.Append("    { \"def\": \"").Append(def)
          .Append("\", \"col\": ").Append(cell.x)
          .Append(", \"row\": ").Append(cell.y)
          .Append(", \"rot\": 0.0, \"mountCol\": ").Append(mount.x)
          .Append(", \"mountRow\": ").Append(mount.y)
          .Append(" }");
    }

    /// <summary>
    /// 새 배의 기본 수치. 프리깃급을 베껴 두고 JSON에서 손으로 맞추게 한다 -
    /// 창에서 고르게 만들면 이 창이 함선 편집기가 되어 버린다.
    /// </summary>
    private static string Template(string name, string placements) =>
$@"{{
  ""defName"": ""{name}"",
  ""basedOn"": ""{name}"",
  ""massPerPlate"": 350,
  ""drag"": 0.3,
  ""angleAccel"": 24,
  ""angleDrag"": 0.5,
  ""angleBrake"": 10,
  ""leakRate"": 2,
  ""doorRate"": 1,
  ""crews"": 3,
  ""FightDistance"": 180,
  ""DetectionDistance"": 300,
  ""breakawaySpeed"": 2,
  ""placements"": {placements}
}}";
}
#endif
