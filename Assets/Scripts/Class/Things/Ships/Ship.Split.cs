using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 판이 사라지면 선체가 아직 한 덩어리인지 다시 센다. 떨어져 나간 조각은 자기 리지드바디를
/// 달고 진짜로 떠나간다.
///
/// 방 BFS와는 완전히 다른 그래프를 본다. 방은 빈 칸으로 이어지고, 선체는 실물로 이어진다.
/// </summary>
public abstract partial class Ship
{
    [Header("선체 파단")]
    /// <summary>떨어져 나가는 조각이 받는 이탈 속도. 붙어 있던 자리에서 밀려나는 만큼.</summary>
    public float breakawaySpeed = 2f;

    private bool _structureDirty;

    /// <summary>맵이 실물이라고 말한 칸들. 죽은 칸을 빼기 위한 원본이다.</summary>
    private readonly HashSet<Vector2Int> _solidCells = new();

    /// <summary>지은 시점에 본체에 붙어 있던 칸. 처음부터 떠 있던 칸은 떼어내지 않는다.</summary>
    private readonly HashSet<Vector2Int> _attachedAtBuild = new();

    /// <summary>
    /// Armor가 자기 마지막 서브셀을 잃을 때 부른다. 여기서 바로 BFS를 돌리지 않는 이유는,
    /// 이 호출이 파편 연쇄나 Physics2D.Simulate 콜백 한가운데서 오기 때문이다.
    /// 오브젝트를 재부모화하기에 안전한 자리는 다음 틱의 시작이다.
    /// </summary>
    public void ReportPlateLost() => _structureDirty = true;

    /// <summary>지어진 직후, 맵의 실물 칸과 본체 덩어리를 기록해 둔다.</summary>
    private void SnapshotStructure(ShipGrid.Map map)
    {
        _solidCells.Clear();
        _attachedAtBuild.Clear();

        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        {
            if (ShipGrid.Solid(map.cells[col, row]))
                _solidCells.Add(new Vector2Int(col, row));
        }

        List<List<Vector2Int>> chunks = ShipGrid.BuildStructure(map, AliveCells());

        if (chunks.Count == 0)
            return;

        foreach (Vector2Int cell in chunks[0])
            _attachedAtBuild.Add(cell);

        if (chunks.Count > 1)
            Debug.LogWarning(
                $"[{name}] 맵의 실물 칸이 처음부터 {chunks.Count}덩어리로 나뉘어 있다. " +
                "본체가 아닌 덩어리는 파단 대상에서 제외한다.");
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

            Vector2Int cell = ShipGrid.ToCell(child.localPosition, mapWidth, mapHeight);

            if (_solidCells.Contains(cell))
                alive.Add(cell);
        }

        return alive;
    }

    /// <summary>OnTick 맨 앞에서 부른다. 물리 콜백 밖이라 재부모화가 안전하다.</summary>
    private void SplitIfBroken()
    {
        if (!_structureDirty)
            return;

        _structureDirty = false;

        if (shipMap == null || _solidCells.Count == 0)
            return;

        ShipGrid.Map map = ShipGrid.ParseMap(shipMap.text);
        HashSet<Vector2Int> alive = AliveCells();

        List<List<Vector2Int>> chunks = ShipGrid.BuildStructure(map, alive);

        // 한 덩어리면 아직 배다
        if (chunks.Count <= 1)
            return;

        var byCell = new Dictionary<Vector2Int, Transform>();

        foreach (Transform child in transform)
        {
            if (child != null)
                byCell[ShipGrid.ToCell(child.localPosition, mapWidth, mapHeight)] = child;
        }

        bool broke = false;

        // chunks[0]이 본체. 나머지 중 원래 본체에 붙어 있던 것만 떼어낸다.
        for (int i = 1; i < chunks.Count; i++)
        {
            if (!WasAttached(chunks[i]))
                continue;

            Breakaway(chunks[i], byCell, alive.Count);
            broke = true;
        }

        if (!broke)
            return;

        // 떨어져 나간 엔진이 계속 추력을 내면 안 되고, 사라진 벽이 있는 방은 뚫린 것이다.
        shipArmors.Clear();
        shipEngines.Clear();
        shipArmors.AddRange(GetComponentsInChildren<Armor>());
        shipEngines.AddRange(GetComponentsInChildren<Engine>());

        BuildRooms();
    }

    private bool WasAttached(List<Vector2Int> chunk)
    {
        foreach (Vector2Int cell in chunk)
        {
            if (_attachedAtBuild.Contains(cell))
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
        body.linearDamping = drag;

        // 조각이 가져가는 질량만큼 본체가 가벼워진다
        float share = totalAlive > 0 ? (float)chunk.Count / totalAlive : 0f;
        float mass = Mathf.Max(1f, rig.mass * share);

        body.mass = mass;
        rig.mass = Mathf.Max(1f, rig.mass - mass);

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
        Vector2 spin = Ballistics.Rotate(arm, 90f) * (rig.angularVelocity * Mathf.Deg2Rad);

        Vector2 push = arm.sqrMagnitude > 1e-6f ? arm.normalized : Random.insideUnitCircle.normalized;

        body.linearVelocity = rig.linearVelocity + spin + push * breakawaySpeed;
        body.angularVelocity = rig.angularVelocity;

        go.AddComponent<HullDebris>();

        Debug.Log($"[{name}] 선체 {chunk.Count}칸이 떨어져 나갔다.");
    }
}
