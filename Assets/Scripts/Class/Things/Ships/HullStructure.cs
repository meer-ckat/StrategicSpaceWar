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
    /// <summary>맵이 실물이라고 말한 칸들. 죽은 칸을 빼기 위한 원본이다.</summary>
    private readonly HashSet<Vector2Int> _solid = new();

    /// <summary>이 덩어리가 생길 때 붙어 있던 칸. 처음부터 떠 있던 칸은 떼어내지 않는다.</summary>
    private readonly HashSet<Vector2Int> _attached = new();

    private ShipGrid.Map _map;
    private bool _hasMap;
    private float _breakawaySpeed = 2f;
    private bool _dirty;
    private Rigidbody2D _body;

    private void Awake() => _body = GetComponent<Rigidbody2D>();

    /// <summary>
    /// Armor가 자기 마지막 서브셀을 잃을 때 부른다. 여기서 바로 BFS를 돌리지 않는 이유는,
    /// 이 호출이 파편 연쇄나 Physics2D.Simulate 콜백 한가운데서 오기 때문이다.
    /// </summary>
    public void ReportPlateLost() => _dirty = true;

    /// <summary>함선이 지어진 직후. 맵의 실물 칸과 본체 덩어리를 기록해 둔다.</summary>
    public void Build(ShipGrid.Map map, float breakawaySpeed)
    {
        _map = map;
        _hasMap = true;
        _breakawaySpeed = breakawaySpeed;

        _solid.Clear();
        _attached.Clear();

        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        {
            if (ShipGrid.Solid(map.cells[col, row]))
                _solid.Add(new Vector2Int(col, row));
        }

        List<List<Vector2Int>> chunks = ShipGrid.BuildStructure(map, AliveCells());

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
    /// 떨어져 나온 조각. 맵과 실물 칸은 물려받고, 붙어 있던 칸은 이 조각 자신이다 -
    /// 잔해에는 "원래 떠 있던 칸" 같은 게 없다.
    /// </summary>
    public void Adopt(
        ShipGrid.Map map,
        HashSet<Vector2Int> solid,
        List<Vector2Int> chunk,
        float breakawaySpeed)
    {
        _map = map;
        _hasMap = true;
        _breakawaySpeed = breakawaySpeed;

        _solid.Clear();
        _solid.UnionWith(solid);

        _attached.Clear();

        foreach (Vector2Int cell in chunk)
            _attached.Add(cell);
    }

    /// <summary>
    /// 주인이 OnTick 맨 앞에서 부른다. 조각이 실제로 떨어져 나갔으면 true - 함선은 그때
    /// 방을 다시 짓고, 잔해는 할 일이 없다.
    /// </summary>
    public bool SplitIfBroken()
    {
        if (!_dirty)
            return false;

        _dirty = false;

        if (!_hasMap || _solid.Count == 0)
            return false;

        List<List<Vector2Int>> chunks = ShipGrid.BuildStructure(_map, AliveCells());

        // 한 덩어리면 아직 통째다
        if (chunks.Count <= 1)
            return false;

        var byCell = new Dictionary<Vector2Int, Transform>();

        foreach (Transform child in transform)
        {
            if (child != null)
                byCell[ShipGrid.ToCell(child.localPosition, _map.width, _map.height)] = child;
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

    /// <summary>아직 자식으로 남아 있는 실물 칸.</summary>
    private HashSet<Vector2Int> AliveCells()
    {
        var alive = new HashSet<Vector2Int>();

        foreach (Transform child in transform)
        {
            // Destroy는 프레임 끝까지 미뤄지지만 == null은 즉시 참이 된다. 한 프레임에
            // 여러 틱이 도는 따라잡기 상황에서 죽은 판을 살아 있다고 세면 안 된다.
            if (child == null)
                continue;

            Vector2Int cell = ShipGrid.ToCell(child.localPosition, _map.width, _map.height);

            if (_solid.Contains(cell))
                alive.Add(cell);
        }

        return alive;
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

    private void Breakaway(
        List<Vector2Int> chunk,
        Dictionary<Vector2Int, Transform> byCell,
        int totalAlive)
    {
        var go = new GameObject($"{name} Debris");
        go.transform.SetPositionAndRotation(transform.position, transform.rotation);

        Rigidbody2D body = go.AddComponent<Rigidbody2D>();
        body.gravityScale = 0f;
        body.angularDamping = 0f;
        body.linearDamping = _body.linearDamping;

        // 조각이 가져가는 질량만큼 본체가 가벼워진다
        float share = totalAlive > 0 ? (float)chunk.Count / totalAlive : 0f;
        float mass = Mathf.Max(1f, _body.mass * share);

        body.mass = mass;
        _body.mass = Mathf.Max(1f, _body.mass - mass);

        Vector2 centre = Vector2.zero;
        int moved = 0;

        foreach (Vector2Int cell in chunk)
        {
            if (!byCell.TryGetValue(cell, out Transform child) || child == null)
                continue;

            centre += (Vector2)child.position;
            moved++;

            child.SetParent(go.transform, worldPositionStays: true);
        }

        if (moved == 0)
        {
            Destroy(go);
            return;
        }

        centre /= moved;

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

        body.linearVelocity = _body.linearVelocity + spin + push * _breakawaySpeed;
        body.angularVelocity = _body.angularVelocity;

        // 순서가 중요하다. HullDebris.Awake가 HullStructure를 찾으므로 구조가 먼저 있어야 한다.
        go.AddComponent<HullStructure>().Adopt(_map, _solid, chunk, _breakawaySpeed);
        go.AddComponent<HullDebris>();

        Debug.Log($"[{name}] 선체 {chunk.Count}칸이 떨어져 나갔다.");
    }
}
