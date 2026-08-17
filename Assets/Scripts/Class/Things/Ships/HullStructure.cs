using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 선체 한 덩어리의 구조. 판이 사라질 때마다 아직 한 덩어리인지 다시 세고, 갈라졌으면
/// 떨어진 조각을 자기 리지드바디를 단 잔해로 내보낸다.
///
/// 함선에도 붙고 잔해에도 붙는다. 규칙이 한 곳에만 살아야 해서다 - 잔해용 BFS를 따로
/// 짜면 한쪽만 고치는 날 조용히 어긋난다.
///
/// 잔해가 함선과 같은 격자를 쓸 수 있는 이유: Breakaway가 잔해 오브젝트를 함선과 정확히
/// 같은 위치·회전에 만들고 worldPositionStays로 옮기므로, 자식의 localPosition이 하나도
/// 변하지 않는다. 칸 좌표가 그대로 살아 있다.
///
/// 방 BFS와는 완전히 다른 그래프를 본다. 방은 빈 칸으로 이어지고, 선체는 실물로 이어진다.
///
/// 스스로 틱하지 않는다. 주인이 OnTick 맨 앞에서 SplitIfBroken을 부른다 - 재부모화가
/// 안전한 자리는 물리 콜백 밖, 다음 틱의 시작뿐이다.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public sealed class HullStructure : MonoBehaviour
{
    /// <summary>
    /// 지금 살아 있는 실물 칸. **장부다 - 매번 다시 세지 않는다.**
    ///
    /// 예전에는 판이 하나 죽을 때마다 자식을 전부 훑어서 이 집합을 새로 만들었다. 2,000장짜리
    /// 구조물(거울 껍질)에서는 판 한 장이 죽을 때마다 GetComponent가 4,000번 넘게 나간다.
    ///
    /// 그럴 필요가 없다: <see cref="ReportPlateLost"/>가 죽는 판을 인자로 받으므로 어느 칸이
    /// 빠지는지 이미 안다. 넣고 빼는 자리는 넷뿐이다 - Build(전부), Adopt(자기 조각),
    /// ReportPlateLost(하나 빼기), Breakaway(떨어져 나간 조각 빼기).
    ///
    /// 장부가 틀리려면 판이 **신고 없이** 사라져야 한다. 죽는 길은 Armor.Die 하나뿐이고
    /// 거기서 항상 신고한다. ShipBuilder.Spawn의 DestroyImmediate는 직후에 Build가 장부를
    /// 새로 만들므로 상관없다.
    /// </summary>
    private readonly HashSet<Vector2Int> _alive = new();

    /// <summary>이 덩어리가 생길 때 붙어 있던 칸. 처음부터 떠 있던 칸은 떼어내지 않는다.</summary>
    private readonly HashSet<Vector2Int> _attached = new();

    private ShipGrid.Map _map;
    private bool _hasMap;
    private float _breakawaySpeed = 2f;
    private bool _dirty;
    private Rigidbody2D _body;

    private void Awake() => _body = GetComponent<Rigidbody2D>();

    /// <summary>
    /// 장부에 남은 칸 수. 자식을 다시 세지 않으므로, 이 수가 실제 판 수와 어긋나면 어딘가에서
    /// 판이 신고 없이 사라졌다는 뜻이다 - 2,000장짜리 구조물을 디버깅할 때 제일 먼저 볼 값.
    /// </summary>
    public int AliveCount => _alive.Count;

    /// <summary>
    /// Armor가 자기 마지막 서브셀을 잃을 때 부른다. 여기서 바로 BFS를 돌리지 않는 이유는,
    /// 이 호출이 파편 연쇄나 Physics2D.Simulate 콜백 한가운데서 오기 때문이다.
    ///
    /// 격자에서도 그 칸을 지운다. <see cref="ShipGrid.Map.cells"/>는 '지어질 때의 모습'이
    /// 아니라 '지금 모습'이고, 그걸 지키는 자리가 여기다 - 안 지우면 오버레이가 이미 뚫린
    /// 자리를 계속 갑판으로 그린다.
    ///
    /// 그리고 <see cref="_alive"/> 장부에서도 이 칸을 뺀다. **여기가 장부를 유지하는
    /// 유일한 자리다** - 파단 BFS가 살아 있는 칸을 다시 세지 않는 이유가 이것이다.
    /// </summary>
    public void ReportPlateLost(Transform plate)
    {
        _dirty = true;

        // 선체 직속 자식만. Stamp가 도장을 찍는 규칙과 정확히 같아야 한다 - 판에 볼트로
        // 붙은 모듈의 localPosition은 판 기준이라 엉뚱한 칸을 지운다.
        if (!_hasMap || plate == null || plate.parent != transform)
            return;

        Vector2Int cell = _map.ToCell(plate.localPosition);

        if (!_map.Inside(cell))
            return;

        _alive.Remove(cell);

        if (ShipGrid.Solid(_map.cells[cell.x, cell.y]))
            _map.cells[cell.x, cell.y] = ShipGrid.Cell.Empty;
    }

    /// <summary>함선이 지어진 직후. 맵의 실물 칸과 본체 덩어리를 기록해 둔다.</summary>
    public void Build(ShipGrid.Map map, float breakawaySpeed)
    {
        _map = map;
        _hasMap = true;
        _breakawaySpeed = breakawaySpeed;

        _alive.Clear();
        _attached.Clear();

        // 격자에서 그대로 베낀다. Stamp는 **살아 있는 자식만** 도장을 찍으므로 이 순간
        // "맵이 실물이라고 말한 칸"과 "지금 살아 있는 칸"은 같은 집합이다.
        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        {
            if (ShipGrid.Solid(map.cells[col, row]))
                _alive.Add(new Vector2Int(col, row));
        }

        List<List<Vector2Int>> chunks = ShipGrid.BuildStructure(map, _alive);

        if (chunks.Count == 0)
            return;

        foreach (Vector2Int cell in chunks[0])
            _attached.Add(cell);

        if (chunks.Count > 1)
            Debug.LogWarning(
                $"[{name}] 맵의 실물 칸이 처음부터 {chunks.Count}덩어리로 나뉘어 있다. " +
                "본체가 아닌 덩어리는 파단 대상에서 제외한다.", this);
    }

    /// <summary>
    /// 떨어져 나온 조각. 맵만 물려받고, 살아 있는 칸도 붙어 있던 칸도 이 조각 자신이다 -
    /// 잔해에는 "원래 떠 있던 칸" 같은 게 없다.
    ///
    /// 격자를 공유해도 되는 이유는 <see cref="Breakaway"/>가 잔해를 함선과 같은 위치·회전·
    /// scale로 만들기 때문이다. 자식의 localPosition이 안 변하므로 chunk의 칸 좌표가
    /// 잔해 쪽에서도 그대로 통한다.
    /// </summary>
    public void Adopt(ShipGrid.Map map, List<Vector2Int> chunk, float breakawaySpeed)
    {
        _map = map;
        _hasMap = true;
        _breakawaySpeed = breakawaySpeed;

        _alive.Clear();
        _attached.Clear();

        // 잔해에게는 조각이 곧 전부다. 지금 살아 있는 칸이자 처음부터 붙어 있던 칸이다.
        foreach (Vector2Int cell in chunk)
        {
            _alive.Add(cell);
            _attached.Add(cell);
        }
    }

    /// <summary>
    /// 주인이 OnTick 맨 앞에서 부른다. 조각이 실제로 떨어져 나갔으면 true - 함선은 그때
    /// 방을 다시 짓고, 잔해는 할 일이 없다.
    /// </summary>
    public bool TrySplitIfBroken()
    {
        if (!_dirty)
            return false;

        _dirty = false;

        if (!_hasMap || _alive.Count == 0)
            return false;

        // 장부를 그대로 넘긴다. 예전에는 여기서 자식 2,000개를 훑어 살아 있는 칸을 다시
        // 세느라 GetComponent가 4,000번 넘게 나갔다 - 판 한 장 죽을 때마다.
        List<List<Vector2Int>> chunks = ShipGrid.BuildStructure(_map, _alive);

        // 한 덩어리면 아직 통째다
        if (chunks.Count <= 1)
            return false;

        var byCell = new Dictionary<Vector2Int, Transform>();

        foreach (Transform child in transform)
        {
            // 판만. 그림 오브젝트가 칸을 차지하면 진짜 판이 조각에서 빠지고, 그 그림이
            // 대신 잔해로 딸려간다.
            if (ShipBuilder.IsPlate(child))
                byCell[_map.ToCell(child.localPosition)] = child;
        }

        int alive = 0;

        foreach (List<Vector2Int> chunk in chunks)
            alive += chunk.Count;

        bool broke = false;

        // chunks[0]이 본체. 나머지 중 원래 이 덩어리에 붙어 있던 것만 떼어낸다.
        for (int i = 1; i < chunks.Count; i++)
        {
            if (!WasAttached(chunks[i]))
                continue;

            Breakaway(chunks[i], byCell, alive);
            broke = true;
        }

        return broke;
    }

    private bool WasAttached(List<Vector2Int> chunk)
    {
        foreach (Vector2Int cell in chunk)
        {
            if (_attached.Contains(cell))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 판 한 장을 잔해로 떼어낸다. <see cref="Armor"/>가 주저앉을 때 부른다 - 지우는 대신
    /// 떨어져 나가게 하는 것이 전부다.
    ///
    /// **<see cref="Breakaway"/>의 의식은 안 탄다.** 절단면 광원은 큰 단면이 실제로 생길 때
    /// 하나만 놓는 것인데, 판마다 놓으면 충각 한 번에 40개가 생겨서 "판마다 Light2D를 달지
    /// 않는다"는 규칙이 여기서 뒷문으로 깨진다. 로그도 마찬가지로 40줄이 된다.
    /// </summary>
    public bool Shed(Transform plate)
    {
        if (!_hasMap || plate == null || plate.parent != transform)
            return false;

        Vector2Int cell = _map.ToCell(plate.localPosition);

        if (!_map.Inside(cell) || !_alive.Contains(cell))
            return false;

        // 떼어내기 전에 격자에서 지운다. MakeDebris가 _alive에서 빼주지만 맵은 안 건드린다 -
        // 안 지우면 오버레이가 이미 뚫린 자리를 계속 갑판으로 그린다.
        if (ShipGrid.Solid(_map.cells[cell.x, cell.y]))
            _map.cells[cell.x, cell.y] = ShipGrid.Cell.Empty;

        _dirty = true;

        // **정적 버퍼를 안 쓴다.** 여기는 ApplyDamage 한가운데고, 그 아래로 붕괴 파편이
        // 다른 판을 죽이며 재진입할 수 있다. GameObject 하나를 새로 만드는 값에 비하면
        // 한 칸짜리 리스트 할당은 공짜고, 재진입 버그가 존재할 자리가 사라진다.
        var chunk = new List<Vector2Int> { cell };
        var byCell = new Dictionary<Vector2Int, Transform> { [cell] = plate };

        return MakeDebris(chunk, byCell, _alive.Count, out _, out _);
    }

    private void Breakaway(
        List<Vector2Int> chunk,
        Dictionary<Vector2Int, Transform> byCell,
        int totalAlive)
    {
        if (!MakeDebris(chunk, byCell, totalAlive, out GameObject go, out Vector2 centre))
            return;

        // 절단면 광원. **여기 하나뿐이다** - 판마다 Light2D를 달면 한 척에 200개가 생긴다.
        // 판의 적열은 SpriteRenderer.color의 HDR 값이 Bloom을 통해 내는 것이고, 실제 광원은
        // 큰 단면이 실제로 생기는 이 순간에만 놓는다.
        DefDatabase.Spawn("Cut Glow", null, centre, 0f);

        // 조각이 클수록 낮게. 판 두 장이 떨어지는 것과 배가 반토막 나는 것은 같은 소리가 아니다.
        SoundManager.AudioShot(
            "Breakaway", centre, 1f, Mathf.Lerp(1.15f, 0.65f, Mathf.Clamp01(chunk.Count / 60f)));

        // 갈라진 쪽 판들도 달아오른다. 반대쪽은 Armor가 죽으면서 이미 이웃에게 알렸지만,
        // 이쪽은 죽은 판이 없이 떨어져 나온 것이라 알려줄 사람이 없다.
        foreach (Vector2Int cell in chunk)
        {
            if (byCell.TryGetValue(cell, out Transform child) && child != null
                && child.TryGetComponent(out Armor plate))
                plate.AddHeat(Ballistics.HeatFromExposure * 0.5f);
        }

        Debug.Log($"[{name}] 선체 {chunk.Count}칸이 떨어져 나갔다.", go);
    }

    /// <summary>
    /// 잔해 몸통을 만들고 판들을 옮겨 담는다. 여기까지가 파단과 판 한 장 떨어짐의 공통분모고,
    /// 광원·소리·로그는 부르는 쪽이 정한다 - 한 장짜리는 그 의식을 치르면 안 된다.
    /// </summary>
    private bool MakeDebris(
        List<Vector2Int> chunk,
        Dictionary<Vector2Int, Transform> byCell,
        int totalAlive,
        out GameObject debris,
        out Vector2 centre)
    {
        debris = null;
        centre = transform.position;

        var go = new GameObject($"{name} Debris");
        go.transform.SetPositionAndRotation(transform.position, transform.rotation);

        // **scale까지 맞춰야 한다.** 반대쪽에서 오는 배는 localScale.x가 -1인데, 잔해를
        // scale 1로 만들고 worldPositionStays로 옮기면 Unity가 월드 좌표를 지키려고
        // 자식의 localPosition.x 부호를 뒤집는다. 거울 대칭은 회전만으로 표현이 안 되기
        // 때문이다. 그러면 이 클래스 전체가 기대는 "칸 좌표가 그대로 살아 있다"가 깨져서,
        // 잔해의 ToCell이 전부 반사된 칸을 내놓는다. Adopt가 받은 chunk 좌표와 안 맞으니
        // _alive.Remove가 조용히 아무것도 안 지우고, 그 잔해는 다시는 안 쪼개진다.
        go.transform.localScale = transform.lossyScale;

        Rigidbody2D body = go.AddComponent<Rigidbody2D>();
        body.gravityScale = 0f;
        body.angularDamping = 0f;
        body.linearDamping = _body.linearDamping;

        // 조각이 가져가는 질량만큼 본체가 가벼워진다
        float share = totalAlive > 0 ? (float)chunk.Count / totalAlive : 0f;
        float mass = Mathf.Max(1f, _body.mass * share);

        body.mass = mass;
        _body.mass = Mathf.Max(1f, _body.mass - mass);

        Vector2 sum = Vector2.zero;
        int moved = 0;

        foreach (Vector2Int cell in chunk)
        {
            if (!byCell.TryGetValue(cell, out Transform child) || child == null)
                continue;

            sum += (Vector2)child.position;
            moved++;

            child.SetParent(go.transform, worldPositionStays: true);

            // 장부에서도 넘긴다. 판이 죽은 게 아니라 남의 몸으로 간 것이라 ReportPlateLost가
            // 안 불린다 - 여기서 안 빼면 본체는 떠나간 칸을 영영 살아 있다고 센다.
            _alive.Remove(cell);
        }

        if (moved == 0)
        {
            Destroy(go);
            return false;
        }

        centre = sum / moved;

        // 배가 돌고 있었으면 그 자리의 접선 속도를 그대로 물려받는다. 안 그러면 회전 중에
        // 떨어진 조각이 제자리에 멈춰 서서 배가 조각을 통과하는 것처럼 보인다.
        Vector2 arm = centre - (Vector2)transform.position;
        Vector2 spin = Ballistics.Rotate(arm, 90f) * (_body.angularVelocity * Mathf.Deg2Rad);

        // 조각 중심이 본체 중심과 겹치는 퇴화 케이스. Unity 전역 RNG를 쓰면 여기 하나 때문에
        // 리플레이가 어긋나므로, 다른 곳과 같은 해시로 방향을 뽑는다.
        Vector2 push = arm.sqrMagnitude > 1e-6f
            ? arm.normalized
            : Ballistics.Rotate(
                Vector2.up,
                new DeterministicRng(
                    Ballistics.Hash(GetInstanceID(), Core.TickManager.currentTick, chunk.Count))
                    .Range(0f, 360f));

        // 상한을 건다. spin은 `거리 × 각속도`라 반지름 120 m짜리 거울에서는 각속도가 조금만
        // 붙어도 조각이 아광속으로 날아간다. 조각이 또 갈라지면 그 값을 또 물려받는다.
        body.linearVelocity = Vector2.ClampMagnitude(
            _body.linearVelocity + spin + push * _breakawaySpeed, Ballistics.DebrisMaxSpeed);

        body.angularVelocity = Mathf.Clamp(
            _body.angularVelocity, -Ballistics.DebrisMaxSpin, Ballistics.DebrisMaxSpin);

        // 순서가 중요하다. Hulk.Awake가 HullStructure를 찾으므로 구조가 먼저 있어야 한다.
        go.AddComponent<HullStructure>().Adopt(_map, chunk, _breakawaySpeed);
        Hulk hulk = go.AddComponent<Hulk>();

        // 배치해 둔 운석과 달리 잔해는 사라져야 한다. 영원히 두면 한 판이 끝날 때쯤
        // 씬이 조각으로 덮인다.
        hulk.lifeTick = Ballistics.DebrisLifeTick;
        hulk.breakawaySpeed = _breakawaySpeed;

        debris = go;
        return true;
    }
}
