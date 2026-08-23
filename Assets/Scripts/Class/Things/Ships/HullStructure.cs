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
    /// 마스크 + 카운트다. HashSet이었는데, 파단 BFS의 입력이 정확히 이 모양의 마스크라
    /// 매번 베끼고 있었다 - 처음부터 마스크로 들면 복사도 해싱도 없다.
    /// 인덱스는 _map 기준 row * width + col. 맵 참조는 본체와 잔해가 공유하므로
    /// (Breakaway 주석 참조) 칸 좌표계도 인덱스도 그대로 통한다.
    private bool[] _alive = System.Array.Empty<bool>();
    private int _aliveCount;

    private int CellIndex(Vector2Int cell) => cell.y * _map.width + cell.x;
    /// <summary>
    /// 후면 칸 -> 남은 체력. 예전에는 집합이었는데 #9에서 체력이 붙었다.
    ///
    /// **서브셀은 없다.** 후면 그림이 칸 해상도라 칸 안을 표현할 데가 없다 - 시뮬레이션이
    /// 있지도 않은 정밀도를 들고 있으면 그 숫자는 언젠가 거짓말이 된다.
    /// </summary>
    private readonly Dictionary<Vector2Int, RearCell> _rear = new();

    /// <summary>
    /// 후면 칸 하나가 아는 것. 체력과 유효 RHA, 둘 다 그 자리 판에서 나온다.
    ///
    /// 사전을 둘로 나누지 않는 이유: 같은 순간에 같은 자리에서 정해지는 두 값이라,
    /// 나누면 언젠가 한쪽만 갱신하는 코드가 생긴다.
    /// </summary>
    public struct RearCell
    {
        public float hp;
        public float rha;

        /// <summary>
        /// 그 자리에 서 있던 판의 발자국(칸 중심 기준, 배 좌표계). **그림 전용이다** -
        /// 후면 피해도 기압도 이 값을 안 읽는다. null이면 칸 전체.
        /// 판이 죽어도 남는 것이 맞다: 후면은 판이 죽은 뒤의 벽이고, 그 벽의 모양은
        /// 죽은 판이 서 있던 모양이다.
        /// </summary>
        public Vector2[] footprint;

        /// <summary>
        /// 심을 때의 체력. **그림 전용이다** - 시뮬레이션은 hp만 읽는다. hp/maxHp가
        /// 세월이다: 외피가 상한 만큼 어두워지고 갉아먹힌다. 이게 없으면 후면은
        /// 죽는 순간까지 새것이고, 판만 삭아서 그림에 시간이 안 흐른다.
        /// </summary>
        public float maxHp;
    }

    /// <summary>이 덩어리가 생길 때 붙어 있던 칸. 처음부터 떠 있던 칸은 떼어내지 않는다.</summary>
    private bool[] _attached = System.Array.Empty<bool>();

    private ShipGrid.Map _map;
    private bool _hasMap;
    private float _breakawaySpeed = 2f;
    private bool _dirty;
    private Rigidbody2D _body;
    public IReadOnlyCollection<Vector2Int> Rear => _rear.Keys;
    public static readonly List<HullStructure> All = new();

    /// <summary>충각 브로드페이즈가 읽는다. 함선·잔해·운석 전부 이 몸으로 움직인다.</summary>
    public Rigidbody2D Body => _body;

    private void Awake() => _body = GetComponent<Rigidbody2D>();

    /// <summary>
    /// 장부에 남은 칸 수. 자식을 다시 세지 않으므로, 이 수가 실제 판 수와 어긋나면 어딘가에서
    /// 판이 신고 없이 사라졌다는 뜻이다 - 2,000장짜리 구조물을 디버깅할 때 제일 먼저 볼 값.
    /// </summary>
    public int AliveCount => _aliveCount;

    /// <summary>
    /// 이 구조물이 두 덩어리 이상으로 갈라진 적이 있는가.
    ///
    /// **판이 몇 장 떨어져 나간 것과는 다른 사건이다.** <see cref="Shed"/>는 한 장을
    /// 떼어내지만 남은 것은 여전히 한 덩어리고, 여기는 <see cref="Breakaway"/>에서만
    /// 켜진다 - 구조 BFS가 실제로 두 성분을 찾았을 때다.
    ///
    /// 고리에 이 차이가 그대로 드러난다. 거울 껍질을 한 군데 끊으면 여전히 8방향으로 이어진
    /// 호 하나라 안 갈라지고, **두 군데를 끊어야** 두 조각이 된다. 초복사가 껍질이 닫혀
    /// 있어서 되는 것이라, "갈라졌다"가 곧 "시설이 죽었다"이다.
    ///
    /// 한 번 켜지면 안 꺼진다. 조각이 다시 붙는 일은 없다.
    /// </summary>
    public bool HasSplit { get; private set; }

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
        // 선체 직속 자식만. Stamp가 도장을 찍는 규칙과 정확히 같아야 한다 - 판에 볼트로
        // 붙은 모듈의 localPosition은 판 기준이라 엉뚱한 칸을 지운다.
        // 정체 모를 신고는 보수적으로 dirty - 드물어서 값이 없다.
        if (!_hasMap || plate == null || plate.parent != transform)
        {
            _dirty = true;
            return;
        }

        Vector2Int cell = _map.ToCell(plate.localPosition);

        if (!_map.Inside(cell))
        {
            _dirty = true;
            return;
        }

        int idx = CellIndex(cell);

        if (_alive[idx])
        {
            _alive[idx] = false;
            _aliveCount--;
        }

        if (ShipGrid.Solid(_map.cells[cell.x, cell.y]))
            _map.cells[cell.x, cell.y] = ShipGrid.Cell.Empty;

        // 링 검사: 이 죽음이 선체를 못 가르면 파단 BFS 자체를 안 돈다. 그라인딩의 외판
        // 피격 대부분이 여기 걸려서, "판이 죽는 틱마다 전체 BFS"가 "가를 수 있는 죽음이
        // 있던 틱에만 BFS"로 줄어든다. true 쪽은 그대로 BFS에 묻는다 - 보수적으로만 틀린다.
        if (ShipGrid.RemovalMightSplit(_alive, _map.width, _map.height, cell))
            _dirty = true;

        RearDiesWithLastPlate();
    }

    /// <summary>
    /// 판이 한 장도 안 남은 몸의 후면은 지탱할 것이 없다. 안 지우면 판이 전멸한 자리에
    /// 뒷벽 그룹만 떠서 산다 - 콜라이더도 없어 쏠 수조차 없는 유령이다.
    /// </summary>
    private void RearDiesWithLastPlate()
    {
        if (_aliveCount > 0 || _rear.Count == 0)
            return;

        _rear.Clear();
        RearVersion++;
    }

    private ShipGrid.Map _designMap;
    public ShipGrid.Map DesignMap => _designMap;
    private Texture2D _shipHullPng;
    public Texture2D ShipHullPng => _shipHullPng;
    public bool HasRear(Vector2Int cell) => _rear.ContainsKey(cell);

    /// <summary>후면 그림용. 발자국까지 필요해서 HasRear와 따로 있다.</summary>
    public bool TryGetRear(Vector2Int cell, out RearCell wall) => _rear.TryGetValue(cell, out wall);

    /// <summary>
    /// 살아 있는 격자의 칸이 **뒤가 뚫려 있나**. 방이 물어보는 자리다.
    ///
    /// **좌표 변환이 여기 있는 이유**: Room.cells는 _map(Stamp가 만든 살아 있는 격자) 칸이고
    /// _rear는 설계도 격자 칸이다. Stamp가 살아남은 판의 극값에서 원점을 잡으므로 판이
    /// 죽으면 둘이 어긋나는데, 그걸 부르는 쪽마다 기억하게 두면 언젠가 한 곳이 잊는다.
    /// #9의 파단이 정확히 그 실수였다.
    ///
    /// 설계에 애초에 후면이 없던 칸(격자 밖, 우주)은 뚫린 것이 아니다 - 뚫리려면 먼저
    /// 있어야 한다.
    /// </summary>
    /// <summary>
    /// 이 칸에 후면이 깔리나. **세 자리(SeedRear·LostRear·RearBreached)가 전부 이
    /// 술어를 써야 한다** - 갈라두면 저장·복원에서 Strut 후면만 안 돌아오는 반쪽
    /// 상태가 생긴다.
    ///
    /// ShipGrid.BackPlate에 하나를 얹는다: **우주에 닿지 않은 Vent에도 후면이 깔린다.**
    /// BackPlate가 Vent를 뺀 전제는 "Vent = 우주에 튀어나온 안테나"였는데, Strut이
    /// 선체 안쪽 바닥으로 쓰이면서 전제가 깨졌다 - 안 얹으면 Strut 칸 뒤로는 탄이
    /// 후면 저항 없이 나가고, 외피 그림에는 그 칸만 구멍이 뚫린다. 같은 실내에서
    /// 빈 칸은 후면이 있는데 Strut 칸은 없는 것이 애초에 비일관이다.
    ///
    /// 안테나는 지금처럼 후면이 없다 - 8방향에 우주가 닿아 있어서 걸러진다. 우주에
    /// 열린 엔진 노즐 밑 Strut도 같다.
    /// </summary>
    private static bool RearWorthy(ShipGrid.Map map, int col, int row)
    {
        ShipGrid.Cell c = map.cells[col, row];

        if (ShipGrid.BackPlate(c))
            return true;

        if (c != ShipGrid.Cell.Vent)
            return false;

        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int nc = col + dx, nr = row + dy;

            // 격자 밖도 우주다.
            if (nc < 0 || nc >= map.width || nr < 0 || nr >= map.height
                || map.cells[nc, nr] == ShipGrid.Cell.Exterior)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 설계도 칸 기준으로 "여기 후면이 있었는데 지금 없다". 그림이 구멍 가장자리를
    /// 물어뜯는 데 쓴다 - RearBreached와 같은 질문이지만 그쪽은 살아 있는 몸의 칸을
    /// 받아 변환부터 한다.
    /// </summary>
    public bool RearTorn(Vector2Int designCell)
        => _designMap != null
        && _designMap.Inside(designCell)
        && RearWorthy(_designMap, designCell.x, designCell.y)
        && !_rear.ContainsKey(designCell);

    public bool RearBreached(Vector2Int liveCell)
    {
        if (_designMap == null || !_hasMap)
            return false;

        Vector2Int cell = _designMap.ToCell(_map.ToLocal(liveCell.x, liveCell.y));

        if (!_designMap.Inside(cell) || !RearWorthy(_designMap, cell.x, cell.y))
            return false;

        return !_rear.ContainsKey(cell);
    }

    /// <summary>
    /// 저장에서 되살아난 배가 잃었던 후면을 다시 뺀다. <see cref="SeedRear"/> **뒤에** 부른다 -
    /// 씨앗은 언제나 설계도 전체이고, 저장이 하는 일은 거기서 빼는 것뿐이다.
    ///
    /// 순서가 뒤집히면 아무 일도 안 일어난다. SeedRear가 _rear를 채우면서 방금 뺀 칸을
    /// 도로 넣기 때문인데, 에러가 안 나서 "저장이 왜 안 먹지"로만 보인다.
    /// </summary>
    public void ForgetRear(List<Vector2Int> cells)
    {
        if (cells == null)
            return;

        for (int i = 0; i < cells.Count; i++)
            _rear.Remove(cells[i]);
    }

    /// <summary>
    /// 설계에는 있었는데 지금 이 몸에 없는 후면 칸. 저장에 적을 목록이다.
    ///
    /// 잔해로 떠난 칸과 고아로 지워진 칸이 여기 같이 들어온다 - 본체 입장에서 둘은 같은
    /// 사실이다("내 것이 아니다"). 잔해가 어디로 갔는지는 저장하지 않는다. 다음 구역에
    /// 잔해를 다시 띄우지 않기 때문이다.
    /// </summary>
    public List<Vector2Int> LostRear()
    {
        var lost = new List<Vector2Int>();

        if (_designMap == null)
            return lost;

        for (int col = 0; col < _designMap.width; col++)
        for (int row = 0; row < _designMap.height; row++)
        {
            if (!RearWorthy(_designMap, col, row))
                continue;

            var cell = new Vector2Int(col, row);

            if (!_rear.ContainsKey(cell))
                lost.Add(cell);
        }

        return lost;
    }
    /// <summary>
    /// 후면 칸 하나를 깎는다. 0 이하가 되면 그 칸이 장부에서 빠지고, 그림에 구멍이 뚫린다
    /// (<see cref="BackPlateView"/>가 칸 수 변화를 보고 다시 굽는다).
    ///
    /// **한 번 빠진 칸은 안 돌아온다.** #8의 저장 포맷("사라진 칸만 적는다")이 그 위에 서
    /// 있고, <see cref="SeedRear"/>의 "이미 차 있으면 안 한다" 가드가 그것을 지킨다.
    /// </summary>
    /// <summary>
    /// 후면이 바뀔 때마다 오른다. 그림이 이걸 보고 다시 굽는다 - 칸 **수**만 보면
    /// 부분 손상(hp만 깎임)이 화면에 영영 안 나온다.
    /// </summary>
    public int RearVersion { get; private set; }

    /// <summary>
    /// 그림(RearSkin 셰이더)이 칸 마스크를 다시 채울 때 읽는다. 값까지 필요해서
    /// Rear(키만)로는 모자라다.
    /// </summary>
    public IEnumerable<KeyValuePair<Vector2Int, RearCell>> RearEntries => _rear;

    /// <summary>설계도 정적 마스크 굽기용 공개 창구. 판정 자체는 RearWorthy 하나다.</summary>
    public static bool RearWorthyAt(ShipGrid.Map map, int col, int row) => RearWorthy(map, col, row);

    public void DamageRear(Vector2Int cell, float amount)
    {
        if (amount <= 0f || !_rear.TryGetValue(cell, out RearCell wall))
            return;

        wall.hp -= amount;
        RearVersion++;

        if (wall.hp > 0f)
            _rear[cell] = wall;
        else
            _rear.Remove(cell);
    }

    /// <summary>
    /// 월드 한 점이 이 몸의 어느 후면 칸인가. 없으면 false.
    ///
    /// **후면에는 콜라이더가 없다.** 그래서 물리 질의로는 절대 못 찾는다 - 파편도 유폭도
    /// 콜라이더와 판 그래프로 대상을 고르는데 후면은 둘 다에 없다. 유일한 길은 위치를
    /// 칸으로 바꿔서 장부를 직접 보는 것이고, 이 함수가 그 다리다.
    ///
    /// 콜라이더를 달면 안 된다 - 그 순간 후면이 오브젝트가 되고 전면과 겹쳐서, "함선에는
    /// Z축이 없다"는 규칙이 깨진다.
    /// </summary>
    public bool CellAt(Vector2 worldPoint, out Vector2Int cell)
    {
        cell = default;

        if (_designMap == null)
            return false;

        cell = _designMap.ToCell(transform.InverseTransformPoint(worldPoint));
        return _designMap.Inside(cell);
    }

    /// <summary>
    /// 설계도 사각형을 덮는 보수적 원의 반경(월드). 전 몸 순회(Blast/Punch/SpallRear)가
    /// 몸마다 InverseTransformPoint를 내기 전에 이 원으로 거른다 - 파편 한 발의 끝점을
    /// 실제로 품는 몸은 한둘인데, 그라인딩 구름에서는 몸이 수백이다.
    /// _designMap이 불변이라 세팅 시점(SeedRear/Adopt)에 한 번만 잰다.
    /// </summary>
    private float _rearBound;

    private void CacheRearBound()
    {
        if (_designMap == null)
        {
            _rearBound = 0f;
            return;
        }

        float rx = Mathf.Max(
            Mathf.Abs(_designMap.origin.x),
            Mathf.Abs(_designMap.origin.x + (_designMap.width - 1) * ShipGrid.CellSize));
        float ry = Mathf.Max(
            Mathf.Abs(_designMap.origin.y),
            Mathf.Abs(_designMap.origin.y - (_designMap.height - 1) * ShipGrid.CellSize));

        Vector3 s = transform.lossyScale;
        float scale = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y));

        _rearBound = Mathf.Sqrt(rx * rx + ry * ry) * scale + ShipGrid.CellSize;
    }

    /// <summary>
    /// 이 몸이 저 지점의 후면 피해 후보인가. 원 밖이면 CellAt의 Inside가 어차피 false고,
    /// 후면이 빈 몸(Shed로 태어난 한 장짜리 잔해 전부)은 DamageRear가 어차피 no-op이다 -
    /// 그 두 "어차피"를 네이티브 호출 전에 확정하는 것뿐이라 결과가 안 변한다.
    /// </summary>
    private bool NearRear(Vector2 worldPoint)
        => _rear.Count > 0
        && ((Vector2)transform.position - worldPoint).sqrMagnitude <= _rearBound * _rearBound;

    /// <summary>
    /// 배 안에서 터진 것이 반대편 벽을 때린다. 살아 있는 모든 몸을 훑는다 - 유폭은 남의
    /// 배에도 건너가고(<c>Radiate</c>), 잔해도 후면을 들고 다니기 때문이다.
    ///
    /// 거리 감쇠는 판을 때릴 때와 같은 식이다. 후면만 다른 곡선을 쓰면 "왜 뒷벽만 잘
    /// 버티지"를 두 곳에서 튜닝하게 된다.
    /// </summary>
    public static void BlastRear(Vector2 pivot, float damage)
    {
        float cutoff = damage * Ballistics.BlastCutoff;

        for (int i = 0; i < All.Count; i++)
        {
            HullStructure body = All[i];

            if (body == null || !body.NearRear(pivot) || !body.CellAt(pivot, out Vector2Int at))
                continue;

            int reach = Mathf.CeilToInt(Ballistics.BlastRadius / ShipGrid.CellSize);

            for (int dc = -reach; dc <= reach; dc++)
            for (int dr = -reach; dr <= reach; dr++)
            {
                var cell = new Vector2Int(at.x + dc, at.y + dr);
                float metres = Mathf.Sqrt(dc * dc + dr * dr) * ShipGrid.CellSize;
                float share = damage * Mathf.Pow(Ballistics.BlastFalloff, metres);

                if (share >= cutoff)
                    body.DamageRear(cell, share);
            }
        }
    }

    /// <summary>
    /// 앞판을 뚫은 탄이 뒷벽까지 갔다. 남은 관통력이 유효 RHA를 넘으면 그 칸이 뚫린다.
    ///
    /// **새 판정 함수를 안 만든다.** PenetrationManager.Resolve가 이미 답을 냈고
    /// (penetrationAfter가 그 나머지다), 여기서는 그 숫자 하나를 벽과 견줄 뿐이다.
    /// 판정 규칙이 두 벌이 되면 "왜 앞판은 뚫었는데 뒷벽 계산은 다르지"가 생긴다.
    ///
    /// **탄은 안 건드린다.** 후면은 깊이 방향의 벽이라 평면 안 궤적에 영향이 없다 -
    /// 뚫고 나가도 속도도 관통력도 그대로고, 그래서 관통한 탄이 뒤에 선 배를 계속 맞힌다.
    /// 물리적으로 완벽하진 않지만 이 게임에 Z가 없고, 일렬로 선 함대를 한 발이 꿰뚫는
    /// 그림이 그 대가로 산다.
    ///
    /// 못 뚫으면 그만큼 깎기만 한다 - 여러 발이 같은 자리를 때리면 결국 열린다.
    /// </summary>
    public static void PunchRear(Vector2 worldPoint, Vector2 dir, float penetration, float damage)
    {
        // **출구는 입사점이 아니다.** 탄은 선체 두께만큼 안을 가로지른 뒤에야 반대편에
        // 닿는다. 평면 안에서 비스듬히 들어온 탄일수록 그 사선이 길어서, 뱃머리로 들어온
        // 것이 허리 뒷벽으로 나간다.
        //
        // 스치듯 들어오면 출구가 배 밖이라 아무 데도 안 뚫린다. 그게 맞다 - 스쳐 지나간
        // 탄이 반대편을 뚫을 이유가 없다. CellAt의 Inside 검사가 그걸 그냥 걸러낸다.
        Vector2 exit = worldPoint + dir.normalized * Ballistics.HullDepth;

        for (int i = 0; i < All.Count; i++)
        {
            HullStructure body = All[i];

            if (body == null || !body.NearRear(exit) || !body.CellAt(exit, out Vector2Int cell))
                continue;

            if (!body._rear.TryGetValue(cell, out RearCell wall))
                continue;

            if (penetration >= wall.rha)
            {
                body._rear.Remove(cell);
                body.RearVersion++;   // 그림이 이걸 보고 칸 마스크를 다시 채운다
            }
            else
            {
                body.DamageRear(cell, damage);
            }
        }
    }

    /// <summary>파편 하나가 아무 판도 못 맞고 날아간 끝. 거기 벽이 있으면 박힌다.</summary>
    public static void SpallRear(Vector2 worldPoint, float amount)
    {
        for (int i = 0; i < All.Count; i++)
        {
            HullStructure body = All[i];

            if (body != null && body.NearRear(worldPoint)
                && body.CellAt(worldPoint, out Vector2Int cell))
                body.DamageRear(cell, amount);
        }
    }

    /// <summary>
    /// 후면 칸 하나의 체력. **그 자리 판을 따른다.**
    ///
    /// 배 하나에 숫자 하나를 정하려던 것이 애초에 무리였다. dart는 충각 끝에 Lance Armor
    /// (m²당 100,000)를 달고 몸통은 mk3(180)인데, 배 전체를 하나로 뭉치면 어느 쪽으로
    /// 정하든 틀린다 - 평균이면 뒷벽이 무적이 되고, 최빈값이면 충각 끝 뒷벽이 종잇장이라
    /// 앞판은 긁히지도 않는데 뒤만 뚫린다.
    ///
    /// 후면은 **반대편 외판**이니 그 자리 재질을 따르는 것이 원래 맞다. 그러면 충각 끝은
    /// 뒷벽도 Lance급이고 몸통은 얇다 - 규칙 하나로 둘 다 나온다.
    ///
    /// 그 칸에 판이 있으면 그것, 없으면(실내) 둘러싼 판 중 제일 두꺼운 것. 실내를 감싸는
    /// 것이 곧 그 방의 외피라서다. 아무것도 못 찾으면 폴백.
    /// </summary>
    /// <summary>
    /// 실내 칸을 감싸는 8방향. 선체 연결성과 같은 이웃이다 - 대각선으로만 붙은 판도
    /// 그 방의 외피이므로, 4방향으로 보면 모서리 방의 뒷벽이 이유 없이 얇아진다.
    /// </summary>
    private static readonly Vector2Int[] Around =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        new(1, 1), new(1, -1), new(-1, 1), new(-1, -1),
    };

    private static RearCell RearWallAt(
        Vector2Int cell, Dictionary<Vector2Int, RearCell> plates, RearCell fallback)
    {
        if (plates.TryGetValue(cell, out RearCell own))
            return Thinned(own);

        RearCell best = default;

        foreach (Vector2Int dir in Around)
        {
            if (plates.TryGetValue(cell + dir, out RearCell near) && near.hp > best.hp)
                best = near;
        }

        RearCell wall = Thinned(best.hp > 0f ? best : fallback);

        // **이웃에서 빌린 발자국은 버린다.** 발자국은 그 판이 앉은 칸 중심 기준이라,
        // 실내 칸에 그대로 대면 엉뚱한 자리를 깎는다. 실내 뒷벽은 칸 전체가 맞다.
        wall.footprint = null;

        return wall;
    }

    /// <summary>
    /// 판 한 장을 후면 한 겹으로 얇게 만든다. 체력과 관통 저항이 따로 준다.
    /// **발자국은 그대로 옮긴다** - 여기서 빼먹으면 모든 후면이 이 함수를 지나므로
    /// 마스킹이 통째로 죽는데, 증상은 "후면이 네모로 나온다"뿐이라 원인이 한참
    /// 떨어져 보인다. Tidy가 shape를 버리던 것과 같은 패턴이다: 값 타입을 새로
    /// 지으면서 필드 하나를 잊는 것.
    /// </summary>
    private static RearCell Thinned(RearCell plate)
    {
        float hp = plate.hp * Ballistics.RearHpFactor;

        return new RearCell
        {
            hp = hp,
            maxHp = hp,
            rha = plate.rha * Ballistics.RearRhaFactor,
            footprint = plate.footprint,
        };
    }

    /// <summary>
    /// 칸 -> 그 자리 판의 체력. 살아 있는 자식에서 뽑는다 - SeedRear는 배가 지어진 뒤에
    /// 불리므로 이 시점에 판이 전부 제자리에 있다.
    ///
    /// **문은 뺀다.** Door도 Armor를 달고 있어서 컴포넌트로는 안 갈리는데, 후면은 외판이지
    /// 문이 아니다. 문 자리의 후면은 이웃 판을 따라간다.
    /// </summary>
    private Dictionary<Vector2Int, RearCell> PlateHealthByCell(ShipGrid.Map designMap)
    {
        var plates = new Dictionary<Vector2Int, RearCell>();

        if (designMap == null)
            return plates;

        foreach (Transform child in transform)
        {
            if (child == null || child.GetComponent<Door>() != null)
                continue;

            if (!child.TryGetComponent(out Armor plate) || plate.PlateHp <= 0f)
                continue;

            Vector2Int cell = designMap.ToCell(child.localPosition);

            if (designMap.Inside(cell))
                plates[cell] = new RearCell
                {
                    hp = plate.PlateHp,
                    rha = plate.RHA,
                    footprint = plate.FootprintLocal(),
                };
        }

        return plates;
    }

    public void SeedRear(ShipGrid.Map designMap, Texture2D shipHullPng)
    {
        if(_rear.Count > 0)
            return;

        _designMap = designMap;
        _shipHullPng = shipHullPng;
        CacheRearBound();

        // 칸마다 다른 값을 준다. 판이 하나도 없으면(씬 저작 중인 배) 폴백 하나로 간다.
        Dictionary<Vector2Int, RearCell> plates = PlateHealthByCell(designMap);

        // 판을 하나도 못 찾았을 때의 폴백. 씬에 손으로 짓는 중인 배가 그 경우다.
        var bare = new RearCell { hp = Ballistics.RearHp, rha = Ballistics.RearHp };

        for(int col = 0; col < designMap.width; col++)
        {
            for(int row = 0; row < designMap.height; row++)
            {
                if(RearWorthy(designMap, col, row))
                {
                    var cell = new Vector2Int(col, row);
                    _rear[cell] = RearWallAt(cell, plates, bare);
                }
            }
        }
    }

    void OnEnable()
    {
        All.Add(this);
    }

    void OnDisable()
    {
        All.Remove(this);
    }

    /// <summary>함선이 지어진 직후. 맵의 실물 칸과 본체 덩어리를 기록해 둔다.</summary>
    public void Build(ShipGrid.Map map, float breakawaySpeed)
    {
        _map = map;
        _hasMap = true;
        _breakawaySpeed = breakawaySpeed;

        int size = map.width * map.height;
        _alive = new bool[size];
        _attached = new bool[size];
        _aliveCount = 0;

        // 격자에서 그대로 베낀다. Stamp는 **살아 있는 자식만** 도장을 찍으므로 이 순간
        // "맵이 실물이라고 말한 칸"과 "지금 살아 있는 칸"은 같은 집합이다.
        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        {
            if (ShipGrid.Solid(map.cells[col, row]))
            {
                _alive[row * map.width + col] = true;
                _aliveCount++;
            }
        }

        List<List<Vector2Int>> chunks = ShipGrid.BuildStructure(map, _alive);

        if (chunks.Count == 0)
            return;

        foreach (Vector2Int cell in chunks[0])
            _attached[CellIndex(cell)] = true;

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
    public void Adopt(ShipGrid.Map map, List<Vector2Int> chunk, Dictionary<Vector2Int, RearCell> owned, float breakawaySpeed, ShipGrid.Map designMap, Texture2D shiphullpng)
    {
        _map = map;
        _hasMap = true;
        _breakawaySpeed = breakawaySpeed;
        _designMap = designMap;
        _shipHullPng = shiphullpng;
        CacheRearBound();

        int size = map.width * map.height;
        _alive = new bool[size];
        _attached = new bool[size];
        _aliveCount = 0;

        // 잔해에게는 조각이 곧 전부다. 지금 살아 있는 칸이자 처음부터 붙어 있던 칸이다.
        // 체력까지 그대로 물려받는다. 집합만 넘기면 갈라지는 순간 상한 후면이 새것으로
        // 되살아난다 - 에러가 안 나서 "왜 잔해 뒷벽이 멀쩡하지"로만 보인다.
        foreach (KeyValuePair<Vector2Int, RearCell> pair in owned)
            _rear[pair.Key] = pair.Value;

        // 잔해에게는 조각이 곧 전부다. 지금 살아 있는 칸이자 처음부터 붙어 있던 칸이다.
        foreach (Vector2Int cell in chunk)
        {
            int idx = CellIndex(cell);

            if (!_alive[idx])
            {
                _alive[idx] = true;
                _aliveCount++;
            }

            _attached[idx] = true;
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

        if (!_hasMap || _aliveCount == 0)
            return false;

        // 장부를 그대로 넘긴다. 예전에는 여기서 자식 2,000개를 훑어 살아 있는 칸을 다시
        // 세느라 GetComponent가 4,000번 넘게 나갔다 - 판 한 장 죽을 때마다.
        List<List<Vector2Int>> chunks = ShipGrid.BuildStructure(_map, _alive);
        // 한 덩어리면 아직 통째다
        if (chunks.Count <= 1)
            return false;

        // SplitRear는 **누가 갖나**만 정한다(순수 함수, self-test 있음). **얼마나 상했나**는
        // 여기서 실어 나른다 - 지금 _rear에만 있는 값이고, 잔해가 태어난 뒤에는 물어볼 데가 없다.
        var rearCells = new HashSet<Vector2Int>(_rear.Keys);

        // **씨앗을 설계도 좌표로 옮긴다.** chunks는 _map(살아 있는 격자) 칸이고 _rear는
        // 설계도 격자 칸이다. Stamp가 살아남은 판의 극값에서 원점을 잡으므로, 판이 죽으면
        // _map은 줄어드는데 _designMap은 안 변한다 - 두 좌표계를 그대로 섞으면 씨앗이
        // 엉뚱한 자리에 떨어져서 소유권이 아무렇게나 갈린다. 증상은 "후면이 분리되려다
        // 본체로 도로 돌아온다"다.
        List<HashSet<Vector2Int>> owned = ShipGrid.SplitRear(ToDesignCells(chunks), rearCells);

        List<Dictionary<Vector2Int, RearCell>> carried = WithHealth(owned);

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
            {
                owned[0].UnionWith(owned[i]);

                foreach (KeyValuePair<Vector2Int, RearCell> pair in carried[i])
                    carried[0][pair.Key] = pair.Value;
                continue;
            }

            Breakaway(chunks[i], byCell, alive, carried[i]);
            broke = true;
        }
        
        _rear.Clear();

        foreach (KeyValuePair<Vector2Int, RearCell> pair in carried[0])
            _rear[pair.Key] = pair.Value;
        return broke;
    }

    /// <summary>
    /// 살아 있는 격자의 칸을 설계도 격자의 칸으로 옮긴다. 두 격자는 같은 배 로컬 공간을
    /// 재므로, 칸 -> 로컬 -> 칸으로 한 번 돌면 된다.
    /// </summary>
    private List<List<Vector2Int>> ToDesignCells(List<List<Vector2Int>> chunks)
    {
        var moved = new List<List<Vector2Int>>(chunks.Count);

        foreach (List<Vector2Int> chunk in chunks)
        {
            var slice = new List<Vector2Int>(chunk.Count);

            if (_designMap != null)
            {
                foreach (Vector2Int cell in chunk)
                    slice.Add(_designMap.ToCell(_map.ToLocal(cell.x, cell.y)));
            }

            moved.Add(slice);
        }

        return moved;
    }

    /// <summary>조각별 칸 집합에 지금 체력을 실어 준다. 순서는 그대로다.</summary>
    private List<Dictionary<Vector2Int, RearCell>> WithHealth(List<HashSet<Vector2Int>> owned)
    {
        var carried = new List<Dictionary<Vector2Int, RearCell>>(owned.Count);

        for (int i = 0; i < owned.Count; i++)
        {
            var slice = new Dictionary<Vector2Int, RearCell>(owned[i].Count);

            foreach (Vector2Int cell in owned[i])
            {
                if (_rear.TryGetValue(cell, out RearCell wall))
                    slice[cell] = wall;
            }

            carried.Add(slice);
        }

        return carried;
    }

    private bool WasAttached(List<Vector2Int> chunk)
    {
        foreach (Vector2Int cell in chunk)
        {
            if (_attached[CellIndex(cell)])
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

        if (!_map.Inside(cell) || !_alive[CellIndex(cell)])
            return false;

        // 떼어내기 전에 격자에서 지운다. MakeDebris가 _alive에서 빼주지만 맵은 안 건드린다 -
        // 안 지우면 오버레이가 이미 뚫린 자리를 계속 갑판으로 그린다.
        if (ShipGrid.Solid(_map.cells[cell.x, cell.y]))
            _map.cells[cell.x, cell.y] = ShipGrid.Cell.Empty;

        // ReportPlateLost와 같은 링 검사. _alive에는 이 칸이 아직 있지만 검사가 칸 자신을
        // 안 보므로 결과가 같다. Shed는 한 장짜리 이탈이라 대부분 여기서 걸러진다.
        if (ShipGrid.RemovalMightSplit(_alive, _map.width, _map.height, cell))
            _dirty = true;

        // **정적 버퍼를 안 쓴다.** 여기는 ApplyDamage 한가운데고, 그 아래로 붕괴 파편이
        // 다른 판을 죽이며 재진입할 수 있다. GameObject 하나를 새로 만드는 값에 비하면
        // 한 칸짜리 리스트 할당은 공짜고, 재진입 버그가 존재할 자리가 사라진다.
        var chunk = new List<Vector2Int> { cell };
        var byCell = new Dictionary<Vector2Int, Transform> { [cell] = plate };

        return MakeDebris(chunk, byCell, _aliveCount, new Dictionary<Vector2Int, RearCell>(), out _, out _);
    }

    private void Breakaway(
        List<Vector2Int> chunk,
        Dictionary<Vector2Int, Transform> byCell,
        int totalAlive, Dictionary<Vector2Int, RearCell> owned)
    {
        if (!MakeDebris(chunk, byCell, totalAlive, owned, out GameObject go, out Vector2 centre))
            return;

        // 조각이 실제로 떠난 뒤에 켠다. MakeDebris가 실패하면(옮길 판이 하나도 없으면)
        // 갈라진 것이 아니다.
        HasSplit = true;

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
        int totalAlive, Dictionary<Vector2Int, RearCell> owned,
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
        // 조각이 가져가는 몫에 **후면도 센다.** 판 수만 보면 속이 텅 빈 조각(껍데기만
        // 남은 뱃머리 같은 것)이 실제보다 가볍게 나가서 종잇장처럼 날아간다.
        //
        // 배 총질량은 안 건드린다. massPerPlate는 판 한 장의 질량이 아니라 배 전체를 판
        // 수로 나눈 값이라 후면·격벽·배선이 이미 그 안에 녹아 있다 - 여기서 더하면
        // 이중 계산이고, 아홉 척의 기동 튜닝이 통째로 무의미해진다. 나눠 갖는 비율만
        // 정확해지면 된다.
        float mine = chunk.Count + owned.Count * Ballistics.RearMassShare;
        float whole = totalAlive + _rear.Count * Ballistics.RearMassShare;

        float share = whole > 0f ? mine / whole : 0f;
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

            // 여기가 재부모화의 유일한 자리다 - Armor.CachedBody(SameBodyAs가 읽는 몸 캐시)를
            // 같이 갱신해야 충격 전도가 잔해로 건너뛰지 않는다.
            if (child.TryGetComponent(out Armor reparented))
                reparented.CachedBody = go.transform;

            // 장부에서도 넘긴다. 판이 죽은 게 아니라 남의 몸으로 간 것이라 ReportPlateLost가
            // 안 불린다 - 여기서 안 빼면 본체는 떠나간 칸을 영영 살아 있다고 센다.
            int aliveIdx = CellIndex(cell);

            if (_alive[aliveIdx])
            {
                _alive[aliveIdx] = false;
                _aliveCount--;
            }
        }

        if (moved == 0)
        {
            Destroy(go);
            return false;
        }

        // 이 조각이 마지막 판들을 데려갔으면 본체 후면도 여기서 죽는다.
        RearDiesWithLastPlate();

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
                    Ballistics.Hash(0, Core.TickManager.currentTick, chunk.Count))
                    .Range(0f, 360f));

        // 상한을 건다. spin은 `거리 × 각속도`라 반지름 120 m짜리 거울에서는 각속도가 조금만
        // 붙어도 조각이 아광속으로 날아간다. 조각이 또 갈라지면 그 값을 또 물려받는다.
        body.linearVelocity = Vector2.ClampMagnitude(
            _body.linearVelocity + spin + push * _breakawaySpeed, Ballistics.DebrisMaxSpeed);

        body.angularVelocity = Mathf.Clamp(
            _body.angularVelocity, -Ballistics.DebrisMaxSpin, Ballistics.DebrisMaxSpin);

        // 소형 조각은 **시각 전용 잔해**다. 구조·충각·파단 스크립트를 아예 안 붙이고
        // 콜라이더를 꺼서, 관성으로 날아가는 그림만 남긴다 - 그라인딩 폭풍의 잔해 대부분이
        // 한 장짜리라 물리 몸·틱 리스너·브로드페이즈 항목이 통째로 안 생긴다.
        // 대가(의도된 것): 이 조각은 더 못 쏘고, 못 갈고, 배를 못 민다. 들고 있던 후면
        // 칸도 같이 사라진다. 수명이 틱이 아니라 초 단위인 것도 그래서 무해하다 -
        // 아무와도 상호작용하지 않는 것의 소멸 시각은 시뮬레이션에 안 보인다.
        // **맨판만 시각 잔해가 된다.** 판의 자식은 모듈뿐이므로(판만 격자에 도장을 찍는다
        // 불변식의 짝) 자식이 있으면 포탑·탄약고가 실려 있다 - 유령으로 만들면 쏠 수 없는
        // 산 모듈이 60초를 떠다닌다. 그런 조각은 정식 잔해로 간다.
        bool bareOnly = chunk.Count <= Ballistics.VisualDebrisMaxPlates;

        if (bareOnly)
        {
            foreach (Transform plate in go.transform)
            {
                if (plate.childCount > 0)
                {
                    bareOnly = false;
                    break;
                }
            }
        }

        if (bareOnly)
        {
            foreach (Collider2D col in go.GetComponentsInChildren<Collider2D>())
                col.enabled = false;

            // 탄도 스냅샷에서도 빠진다 - 유령 판은 Physics2D에도 TraceWorld에도 없어야 한다.
            TraceWorld.Invalidate();

            // 콜라이더가 없으면 질량중심이 몸 원점(= 본체가 있던 자리)에 남는다. 그러면
            // 각속도가 판을 그 먼 점 주위로 공전시킨다 - 판 자리로 옮겨야 제자리에서 돈다.
            body.centerOfMass = go.transform.InverseTransformPoint(centre);

            Destroy(go, Ballistics.DebrisLifeTick * Core.TickManager.TickDeltaTime);

            debris = go;
            return true;
        }

        // 순서가 중요하다. Hulk.Awake가 HullStructure를 찾으므로 구조가 먼저 있어야 한다.
        go.AddComponent<HullStructure>().Adopt(_map, chunk, owned, _breakawaySpeed, _designMap, _shipHullPng);
        Hulk hulk = go.AddComponent<Hulk>();

        // 배치해 둔 운석과 달리 잔해는 사라져야 한다. 영원히 두면 한 판이 끝날 때쯤
        // 씬이 조각으로 덮인다.
        hulk.lifeTick = Ballistics.DebrisLifeTick;
        hulk.breakawaySpeed = _breakawaySpeed;

        debris = go;
        return true;
    }
}
