using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using Core;

/// <summary>
/// 격자 한 칸. 안쪽은 SubGrid × SubGrid 서브셀로 다시 나뉜다.
///
/// **판은 자기가 어디를 맞았는지 기억한다** - 서브셀 HP가 그 자리의 RHA 배율을 정한다.
/// </summary>
[RequireComponent(typeof(Collider2D))]
public abstract class Armor : Thing
{
    public const int SubGrid = Ballistics.SubGrid;
    public const int SubCount = Ballistics.SubCount;

    [Header("Armor")]
    [SerializeField] private float rha = 100f;        // mm

    /// <summary>
    /// 제곱미터당 HP. 실제 총량은 콜라이더 넓이를 곱해서 나온다.
    /// 즉 재질을 정의한다.
    /// </summary>
    [FormerlySerializedAs("cellHp")]
    [SerializeField] private float hpPerSquareMetre = 1800f;

    [SerializeField] private float plateThickness = 0.1f; // m, 실제 판 두께. 오버매치 판정에만 쓴다

    /// <summary>콜라이더가 박스가 아닐 때만 쓴다. 박스는 자기 크기를 스스로 알려준다.</summary>
    [SerializeField] private Vector2 fallbackCellSize = Vector2.one;

    /// <summary>무너진 서브셀의 파편이 맞힐 수 있는 것. Armor | Module.</summary>
    [SerializeField] private LayerMask debrisLayer;

    private readonly float[] _hp = new float[SubCount];
    public bool sealsRoom = true;
    private int _dead;

    // 고립 서브셀 판정용. 판마다 하나씩 - 한 판을 쓰는 동안 다른 판이 끼어들 수 있다
    // (파편이 옆 판을 치고, 그 판이 다시 쓸기 시작한다).
    private readonly bool[] _alive = new bool[SubCount];
    private readonly bool[] _inLargest = new bool[SubCount];
    private bool _sweeping;

    // Destroy는 프레임 끝까지 미뤄진다. 그래서 판을 죽인 ApplyDamageAlong 루프가 계속
    // 이 판을 부른다. 이 걸쇠가 없으면 남은 서브셀이 죽을 때마다 붕괴 파편이 다시 터지고
    // 선체에 "판 사라졌다"를 다시 신고한다.
    private bool _collapsed;

    /// <summary>
    /// 지금 주저앉는 중인가. 붕괴가 치명 모듈을 터뜨리고, 그 유폭이 이 판을 다시 때리며
    /// 같은 자리로 돌아오기 때문에 있다. <see cref="_collapsed"/>로 대신할 수 없다 -
    /// 그것을 미리 세우면 잔해로 간 판이 다시는 피해를 못 받는다.
    /// </summary>
    private bool _collapsing;

    // 서브셀 격자는 콜라이더 위에 얹힌다. 손으로 적은 크기가 콜라이더와 어긋나면 진입점이
    // 엉뚱한 서브셀에 떨어지고 채널이 판 바깥까지 흘러나간다. 그래서 적지 않고 읽는다.
    private Vector2 _cellSize = Vector2.one;
    private Vector2 _cellOffset;

    /// <summary>m². Awake에서 콜라이더를 읽은 직후 정해지고 그 뒤로 안 바뀐다.</summary>
    private float _cellArea = 1f;

    /// <summary>
    /// 판의 실물 모양. **콜라이더 중심 기준 로컬 좌표**다 - 서브셀 격자와 같은 공간이라
    /// 변환이 없다. 비어 있으면 콜라이더 사각형 전체이고, 그것이 기본값이다.
    ///
    /// 콜라이더는 이 모양을 안 따라간다. 여전히 상자다. 그래서 단계 1에서 바뀌는 것은
    /// **모양과 무게뿐이고 각도는 아니다** - 도탄과 /cosθ는 레이캐스트가 준 상자 면의
    /// 법선을 읽으므로, 다각형으로 빗면을 만들어도 경사각 이득이 0이다. 뾰족한 뱃머리가
    /// 잘 튕기는 게 아니라 그냥 얇은 뱃머리가 된다. 그것이 필요해지면 PolygonCollider2D다.
    /// </summary>
    [SerializeField] private Vector2[] shape;

    /// <summary>
    /// 서브셀이 <see cref="shape"/>에 얼마나 잠겼나(0~1). Awake에서 한 번 굽고 안 바뀐다.
    ///
    /// **이 값은 RHA에 곱하고 가중치에는 접지 않는다.** 가중치로 옮기면 SubCellPath가
    /// 합을 1로 재정규화하면서 빈 부분이 분모에서 사라져 삼각형의 뾰족한 끝이 밑동만큼
    /// 막는다. CLAUDE.md 불변식에 한 줄 있다.
    /// </summary>
    private readonly float[] _solid = new float[SubCount];

    /// <summary>m². 폴리곤이 없으면 <see cref="_cellArea"/>와 같다.</summary>
    private float _shapeArea = 1f;

    public float PlateThickness => plateThickness;

    /// <summary>
    /// 판 전체 구조 예산. 넓이를 곱한 뒤의 값이라 이게 진짜 총량이다.
    /// **콜라이더 넓이가 아니라 실물 넓이다** - 반쯤 파낸 판이 온전한 판만큼 튼튼하면
    /// 다각형을 넣은 의미가 절반 사라진다.
    /// </summary>
    public float PlateHp => hpPerSquareMetre * _shapeArea;

    /// <summary>서브셀 하나가 **꽉 찼을 때의** 체력. 모양과 무관한 눈금이다.</summary>
    public float SubCellFullHp => hpPerSquareMetre * _cellArea / SubCount;

    /// <summary>
    /// 서브셀 i가 실제로 가질 수 있는 최대 체력.
    ///
    /// **비율을 여기로 나눠야 한다.** 꽉 찬 칸의 체력으로 나누면 반만 실물인 칸이
    /// 태어나자마자 50% 손상으로 읽혀서, RhaCurve도 그림도 열도 전부 "이 판은 이미
    /// 상했다"고 말한다. 삼각형 판의 빗변 전체가 그렇게 된다.
    /// </summary>
    public float MaxHpAt(int subIndex) => SubCellFullHp * _solid[subIndex];

    /// <summary>서브셀 i의 실물 비율(0~1). Inspector가 "여기가 왜 안 막았나"를 설명할 값.</summary>
    public float SolidAt(int subIndex) => _solid[subIndex];

    /// <summary>안 상한 상태의 명목 RHA.</summary>
    public float RHA => rha;

    /// <summary>판에 남은 구조 비율(0~1). 런 사이에 손상을 들고 가는 값이 이것이다.</summary>
    public float HealthFraction
    {
        get
        {
            float total = 0f;

            for (int i = 0; i < SubCount; i++)
                total += _hp[i];

            return PlateHp > 0f ? total / PlateHp : 0f;
        }
    }

    /// <summary>
    /// 저장된 손상을 되돌려 놓는다. **피해를 다시 넣는 것이 아니다.**
    ///
    /// ApplyDamageEvenly로 되살리면 서브셀이 그 비율만큼 실제로 죽고, 0.4를 복원하려다
    /// PlateCollapseFraction(0.5)을 넘겨 **전투에서 살아남은 판이 로드하자마자 무너진다.**
    /// 여기는 피해 경로가 아니라 로드 경로라 값을 그냥 놓는다.
    ///
    /// Awake가 만땅으로 초기화하므로 **활성화된 뒤에** 불러야 한다.
    /// </summary>
    public void RestoreHealthFraction(float fraction)
    {
        float f = Mathf.Clamp01(fraction);

        for (int i = 0; i < SubCount; i++)
            _hp[i] = MaxHpAt(i) * f;

        _dead = 0;
        AnyBreached = false;
        DamageVersion++;
        DirtySubs = ulong.MaxValue;
    }

    /// <summary>
    /// Health에 fraction을 곱한다. 잔해의 HP를 줄여 우주쓰레기가 미친 존나 강한 운석이 되는걸 방지.
    /// </summary>
    /// <param name="fraction"></param>
    public void ScaleHealth(float fraction)
    {
        float f = Mathf.Clamp01(fraction);

        for (int i = 0; i < SubCount; i++)
            _hp[i] *= f;

        DamageVersion++;
        DirtySubs = ulong.MaxValue;
    }

    protected override void Awake()
    {
        base.Awake();

        // def 규칙대로 부모가 다 잡힌 뒤에 켜지므로 여기서 읽는 parent가 곧 몸이다.
        CachedBody = transform.parent;

        // 배 좌표계에서 로컬 위치 캐시
        CellLocal = transform.localPosition;

        // 콜라이더를 먼저 읽는다. 체력이 넓이에서 나오므로 순서가 뒤집히면
        // 모든 판이 넓이 0의 체력, 즉 0을 들고 시작한다.
        if (TryGetComponent(out BoxCollider2D box))
        {
            _cellSize = box.size;
            _cellOffset = box.offset;
        }
        else
        {
            _cellSize = fallbackCellSize;
            _cellOffset = Vector2.zero;
        }

        _cellArea = Mathf.Max(1e-4f, _cellSize.x * _cellSize.y);

        BakeShape();

        for (int i = 0; i < SubCount; i++)
            _hp[i] = MaxHpAt(i);
    }

    /// <summary>
    /// 서브셀마다 폴리곤에 잠긴 비율을 굽는다. **콜라이더를 읽은 뒤, 체력 초기화보다
    /// 먼저** 돌아야 한다 - MaxHpAt이 이 값에서 나온다.
    ///
    /// 폴리곤이 없으면 전부 1이고 넓이는 콜라이더 넓이다. 그래서 지금 있는 배들은
    /// 이 코드가 들어와도 **한 바이트도 안 바뀐다.** 그게 이 설계의 안전장치다.
    /// </summary>
    private void BakeShape()
    {
        if (shape == null || shape.Length < 3) //걍 사각형이거나, 폴리곤이 비정상 수치라면(불가능함)
        {
            for (int i = 0; i < SubCount; i++) //진짜 만에 하나 우주 방사선 맞아서 폴리곤라고 뜨더라도 사각형으로 처리한다.
                _solid[i] = 1f;

            _shapeArea = _cellArea; //사각형이니 cellArea
            return;
        }

        Vector2 sub = _cellSize / Ballistics.SubGrid; // 각 서브셀에 크기를 균등하게 나눠준다.
        float subArea = Mathf.Max(1e-6f, sub.x * sub.y); //clamp
        float total = 0f;

        for (int i = 0; i < SubCount; i++)
        {
            // 서브셀 인덱스 규약은 Ballistics.SubCell과 같아야 한다 - col이 낮은 비트다.
            int col = i % Ballistics.SubGrid; // subgrid가 6임. subCount는 subgrid^2이니 36, 그러니까 col은 0~5까지
            int row = i / Ballistics.SubGrid; // 얜 0~6까지.

            var min = new Vector2(col * sub.x - _cellSize.x * 0.5f, row * sub.y - _cellSize.y * 0.5f);
            float area = Ballistics.ClippedArea(shape, min, min + sub);

            _solid[i] = Mathf.Clamp01(area / subArea);
            total += area;
        }

        // 클리핑 넓이의 합이 곧 폴리곤 넓이다 - 따로 재면 두 값이 어긋날 자리가 생긴다.
        // 콜라이더 밖으로 삐져나간 부분은 여기서 저절로 빠진다.
        _shapeArea = Mathf.Max(1e-4f, total);
    }

    // ponytail: 판의 scale이 1이라고 가정한다. 바뀌면 lossyScale로 나눌 것.
    private Vector2 ToCellLocal(Vector2 worldPoint)
        => (Vector2)transform.InverseTransformPoint(worldPoint) - _cellOffset;

    /// <summary>
    /// worldDirection으로 들어온 명중이 떨어지는 서브셀. **방향이 중요하다** - 히트 지점이
    /// 격자선에 정확히 걸리는 일이 상시라, 방향이 없으면 탄이 들어가는 칸이 아니라
    /// *떠나는* 칸을 고른다.
    /// </summary>
    public int SubIndexAt(Vector2 worldPoint, Vector2 worldDirection)
        => Ballistics.EntrySubIndex(
            ToCellLocal(worldPoint),
            transform.InverseTransformDirection(worldDirection),
            _cellSize);

    /// <summary>
    /// 탄이 이 칸을 가로지르는 선 전체의 유효 RHA와, 그 값을 만든 서브셀별 가중치.
    /// **저항과 피해는 같은 선을 읽어야 한다** - 맞기만 하고 저항은 안 하는 서브셀은 장식이다.
    /// 멀쩡한 칸은 그대로 명목 RHA가 나온다. 가중치의 합이 1이기 때문이다.
    /// </summary>
    /// <param name="diameter">탄 직경(m). 0이면 중심선 하나만 훑는다.</param>
    public float ChannelRha(
        Vector2 worldEntry,
        Vector2 worldDirection,
        float[] weights,
        out int entry,
        float diameter = 0f)
    {
        if (!TraceChannel(worldEntry, worldDirection, 1f, weights, out entry, diameter)) //만약 이게 실패했다면
            return EffectiveRhaAt(entry) * _solid[entry];

        float total = 0f;

        for (int i = 0; i < SubCount; i++)
        {
            // **실물 비율은 값에 곱하고 weights에는 안 접는다.** 가중치로 옮기면
            // SubCellPath가 합을 1로 재정규화하면서 빈 부분이 분모에서 사라져,
            // 삼각형의 뾰족한 끝이 밑동만큼 막는다. CLAUDE.md 불변식 참고.
            if (weights[i] > 0f)
                total += weights[i] * EffectiveRhaAt(i) * _solid[i];
        }

        return total;
    }

    /// <summary>
    /// 채널을 weights에 채운다. depthFraction만큼 들어간 지점에서 끊는다.
    /// 레이가 곧바로 빠져나가면 false. entry는 그때 스친 서브셀이다.
    /// </summary>
    public bool TraceChannel(
        Vector2 worldEntry,
        Vector2 worldDirection,
        float depthFraction,
        float[] weights,
        out int entry,
        float diameter = 0f)
    {
        Vector2 local = ToCellLocal(worldEntry);
        Vector2 dir = transform.InverseTransformDirection(worldDirection);

        entry = Ballistics.EntrySubIndex(local, dir, _cellSize);

        // SubCellPath는 스치기만 한 경우에도 합이 1인 분포를 남긴다. 예전처럼
        // weights[entry] = 1f로 덮으면 굵은 탄의 나머지 레인 몫이 사라진다.
        return Ballistics.SubCellPath(local, dir, _cellSize, weights, depthFraction, diameter);
    }

    /// <summary>
    /// 들어온 면만이 아니라 채널 전체에 피해를 넣는다. 총량은 같고, 선이 지나간
    /// 서브셀들에 나눠 담길 뿐이다.
    /// </summary>
    public void ApplyDamageAlong(float[] weights, float amount)
    {
        for (int i = 0; i < SubCount; i++)
        {
            if (weights[i] > 0f)
                ApplyDamage(i, amount * weights[i]);
        }
    }

    /// <summary>
    /// 서브셀에 남은 구조 비율. **자기 최대치로 나눈다** - 반만 실물인 칸이 반만큼의
    /// 체력을 가진 것은 손상이 아니라 원래 그런 것이다. 실물이 아예 없으면 0을 돌려주고,
    /// 그러면 RhaCurve가 0을 내서 저항도 0이 된다.
    /// </summary>
    public float HpFraction(int subIndex)
    {
        float max = MaxHpAt(subIndex);
        return max > 0f ? _hp[subIndex] / max : 0f;
    }

    public float RhaMultiplier(int subIndex) =>
        Ballistics.RhaCurve(HpFraction(subIndex));

    public float EffectiveRhaAt(int subIndex) =>
        rha * RhaMultiplier(subIndex);

    /// <summary>
    /// HP 0인 서브셀은 기압을 못 버틴다. 그 뒤의 방이 샌다.
    ///
    /// **애초에 실물이 없던 칸은 뚫린 것이 아니다.** 안 거르면 삼각형 판이 태어나는
    /// 순간부터 뒤의 방이 새고, 원인이 "이 배는 왜 처음부터 공기가 없지"로 나온다.
    /// 다각형은 콜라이더 안쪽 모양일 뿐이고 격자는 여전히 그 칸을 실물로 본다 -
    /// 격자와 콜라이더가 다른 층이라는 규칙이 여기서도 그대로다.
    /// </summary>
    public bool IsBreached(int subIndex) => _solid[subIndex] > 0f && _hp[subIndex] <= 0f;

    /// <summary>구멍을 뚫은 그 명중에서 걸린다. 그래서 기압 계산이 서브셀을 훑을 일이 없다.</summary>
    public bool AnyBreached { get; private set; }

    /// <summary>Awake에서 읽은 실제 콜라이더 크기. X선 오버레이가 이걸로 자기 크기를 잡는다.</summary>
    public Vector2 CellSize => _cellSize;

    /// <summary>
    /// 격자상 8방향으로 맞닿은 판. 배를 지을 때 <see cref="ShipBuilder.Stamp"/>가 한 번 채우고
    /// 그 뒤로 안 바뀐다 - 판은 죽기만 하고 새로 생기지 않으므로, 죽은 자리는 `== null`이 된다.
    ///
    /// 이게 없으면 이웃을 찾는 유일한 방법이 물리 질의(OverlapCircle)인데 세 가지가 틀린다:
    /// 접촉마다 배열을 새로 할당하고, 콜라이더 반경 때문에 두 칸 건너까지 집어오고,
    /// 무엇보다 **상대 함선의 판까지 같이 집어온다.**
    ///
    /// 격자 좌표를 여기 저장하지 않는 것이 요점이다 - 잔해로 갈라지면 격자는 의미를 잃지만
    /// 판끼리의 인접 관계는 그대로다. 갈라짐은 <see cref="SameBodyAs"/>가 본다.
    /// </summary>
    public Armor[] Neighbours = System.Array.Empty<Armor>();

    /// <summary>
    /// 지금 붙어 있는 몸(= transform.parent) 캐시. transform.parent는 네이티브 호출인데
    /// SameBodyAs가 충격 전도 BFS의 간선마다 두 번씩 읽는다. 재부모화 지점은
    /// <see cref="HullStructure"/>의 MakeDebris 하나뿐이라 거기서만 갱신하면 안 썩는다.
    /// </summary>
    [System.NonSerialized] public Transform CachedBody;

    /// <summary>
    /// 충격 전도 BFS의 방문 표시. **집합이 아니라 도장이다** - HashSet<Armor>는 간선마다
    /// 해싱하고 자라면서 할당하는데, 그라인딩 중 Conduct는 틱마다 수백 간선을 돈다.
    /// 파면마다 새 도장 번호를 쓰므로 지울 일이 없다(0은 "아직 아무 파면도 안 닿음").
    ///
    /// 재진입(유폭 연쇄)은 이 도장을 안 쓴다 - 안쪽 파면이 도장을 덮어쓰면 바깥 파면이
    /// 이미 때린 판을 다시 때린다. 그때만 지역 집합으로 간다.
    /// </summary>
    [System.NonSerialized] public int ConductStamp;

    /// <summary>
    /// 충각 스윕이 같은 판을 두 번 세지 않게 하는 도장. **전도와 따로 든다** - 충각이
    /// 판을 때리면 그 자리에서 유폭이 나고 그 유폭이 전도 도장을 새 번호로 덮는다.
    /// 한 필드를 나눠 쓰면 돌아온 스윕이 이미 센 판을 다시 세고, 접촉 판 수가 부풀어
    /// 뾰족하게 댄 충각이 넓게 댄 것으로 계산된다.
    /// </summary>
    [System.NonSerialized] public int PunchStamp;

    /// <summary>배 로컬 위치 캐시. Awake에서 한 번. 재부모화가 보존하는 값이라 안 썩는다.</summary>
    [System.NonSerialized] public Vector2 CellLocal;

    /// <summary>
    /// 아직 같은 덩어리인가. 잔해로 떨어져 나가도 이웃 참조는 살아 있어서, 그냥 두면 충격이
    /// 100 m 떨어진 조각으로 건너뛴다. 판은 선체 직속 자식이므로 부모가 같으면 같은 덩어리다.
    /// ReferenceEquals인 이유: 두 캐시가 같은 살아 있는 부모를 가리키거나 다르거나 둘뿐이라
    /// Unity의 == 오버로드(생존 검사)가 필요 없다.
    /// </summary>
    public bool SameBodyAs(Armor other)
        => other != null && ReferenceEquals(other.CachedBody, CachedBody);

    /// <summary>
    /// 판 **로컬** 좌표의 한 점이 들어 있는 서브셀. 바깥에서 "격자가 어디냐"를 물어도 되는
    /// 유일한 자리다 - 콜라이더 기반 격자로 갈아끼울 때 이 메서드 본문만 바꾸면 된다.
    /// </summary>
    public int SubIndexAtLocal(Vector2 localPoint)
        => Ballistics.SubIndex(localPoint - _cellOffset, _cellSize);

    /// <summary>서브셀 격자의 원본 숫자. 셰이더 스킨이 상수로 넘겨 받는다 - 수식은 여기 한 벌뿐이다.</summary>
    public Vector2 CellOffset => _cellOffset;

    /// <summary>
    /// 배치가 준 모양을 꽂는다. **활성화 전에만 부른다** - 굽는 일은 Awake의 BakeShape가
    /// 하므로, 켜진 뒤에 바꾸면 체력과 잠김 비율이 옛 모양으로 남는다.
    /// </summary>
    public void PrepareShape(Vector2[] points) => shape = points;

    /// <summary>내보내기가 읽는다. 없으면 null.</summary>
    public Vector2[] Shape => shape;

    /// <summary>
    /// 판의 발자국을 **판이 앉은 칸 중심 기준, 배 좌표계** 폴리곤으로. 폴리곤 판은
    /// 그 폴리곤, 사각형 판은 회전한 콜라이더의 네 귀퉁이다.
    ///
    /// 후면 그림이 쓴다 - 후면 칸은 1 m 정사각형인데 그 앞의 판이 경사면이면 정사각형이
    /// 판 실루엣 밖으로 삐져나온다. 시뮬레이션(후면 HP)은 여전히 칸 단위다. 이것은
    /// 모양 정보일 뿐이다.
    /// </summary>
    public Vector2[] FootprintLocal()
    {
        float rot = transform.localEulerAngles.z;

        if (shape != null && shape.Length >= 3)
        {
            var pts = new Vector2[shape.Length];

            for (int i = 0; i < shape.Length; i++)
                pts[i] = Ballistics.Rotate(_cellOffset + shape[i], rot);

            return pts;
        }

        Vector2 half = _cellSize * 0.5f;

        return new[]
        {
            Ballistics.Rotate(_cellOffset + new Vector2(-half.x, -half.y), rot),
            Ballistics.Rotate(_cellOffset + new Vector2(half.x, -half.y), rot),
            Ballistics.Rotate(_cellOffset + new Vector2(half.x, half.y), rot),
            Ballistics.Rotate(_cellOffset + new Vector2(-half.x, half.y), rot),
        };
    }

    /// <summary>
    /// 이 점이 판의 실물 모양 안인가. <see cref="SubIndexAtLocal"/>과 **같은 공간**을
    /// 받는다 - 콜라이더 offset을 빼는 자리가 둘이면 언젠가 한쪽만 고쳐진다.
    ///
    /// 폴리곤이 없으면 언제나 true다. 그림 쪽에서 콜라이더 판정과 AND로 묶이므로
    /// 지금 있는 배들은 이 함수가 생겨도 아무것도 안 바뀐다.
    /// </summary>
    public bool InsideShape(Vector2 localPoint)
        => Ballistics.PolygonContains(shape, localPoint - _cellOffset);

    /// <summary>
    /// 적열. **그림 전용이다** - 시뮬레이션은 이 값을 한 번도 안 읽는다. 0이면 원래 색,
    /// 1이면 갓 찢어진 단면.
    ///
    /// HP와 따로 도는 이유: 판이 얼마나 상했나(HP)와 **언제** 상했나(열)는 다른 정보고,
    /// 플레이어가 화면에서 읽고 싶은 것은 후자다. 시뻘건 단면은 방금 찢어진 곳이고
    /// 검게 식은 잔해는 한참 전에 떨어진 것 - 그게 색만으로 전해진다.
    /// </summary>
    public float Heat { get; private set; }

    public void AddHeat(float amount)
    {
        if (amount > 0f)
            Heat = Mathf.Min(1f, Heat + amount);
    }

    /// <summary>
    /// 식는다. 그림 값이라 시뮬레이션에 아무 영향이 없고, 이 메서드가 통째로 없어도
    /// 판정은 똑같이 돈다.
    /// </summary>
    public override void OnTick()
    {
        if (Heat <= 0f)
            return;

        // **판은 자기 뒤 후면을 안 데운다.** 한번 넣어봤다가 뺐다 - 내 배의 후면은
        // sortingOrder -10에 0.35까지 어둡게 깔리므로, 살아 있는 판 **밑**은 그 판이
        // 가려서 화면에 아무것도 안 나온다. 안 보이는 것을 매 틱 뜨거운 판 수만큼
        // 계산하고 있었다.
        //
        // 후면이 빛나는 자리는 판이 없는 자리뿐이고, 거기로 열이 들어오는 길은 둘이다 -
        // 판이 뜯길 때(HullStructure.ReportPlateLost)와 후면 자체가 맞을 때(DamageRear).
        Heat *= Mathf.Pow(0.5f, TickManager.TickDeltaTime / Ballistics.HeatHalfLife);

        if (Heat < 0.004f)
            Heat = 0f;
    }

    /// <summary>
    /// 서브셀이 HP를 잃을 때마다 오른다. 그림은 매 프레임 float 36개를 비교하는 대신
    /// 이 숫자만 본다 - 안 맞고 있는 판은 int 비교 한 번이 전부다.
    /// </summary>
    public int DamageVersion { get; private set; }

    /// <summary>
    /// 마지막 그리기 이후 변한 서브셀 비트(1UL &lt;&lt; subIndex). **그림 전용이다** -
    /// DamageVersion이 "변했다"를 말하면 이것이 "어디가"를 말해서, 한 칸 맞은 판이
    /// 전 픽셀을 다시 계산하지 않게 한다. SubCount 36이라 ulong 하나로 충분하다.
    /// 전부 갱신(수리)은 전 비트로 표시한다.
    /// </summary>
    public ulong DirtySubs { get; private set; }

    /// <summary>읽고 비운다. 소비자는 ArmorSkin 하나뿐이다.</summary>
    public ulong ConsumeDirtySubs()
    {
        ulong dirty = DirtySubs;
        DirtySubs = 0;
        return dirty;
    }

    private int _lastPenetrateSoundFrame = -1;

    public void ApplyDamage(int subIndex, float amount)
    {
        if (amount <= 0f || _collapsed)
            return;

        // 맞은 만큼 달아오른다. 서브셀 하나를 통째로 날리는 피해가 기준.
        AddHeat(amount / Mathf.Max(1e-3f, SubCellFullHp) * Ballistics.HeatFromDamage);
        foreach (Armor neighbour in Neighbours)
        {
            if (neighbour != null && SameBodyAs(neighbour))
                neighbour.AddHeat(Ballistics.HeatFromExposure);
        }
        // 소리는 판당 프레임당 한 번. SoundManager가 어차피 프레임 중복을 걸러서 들리는
        // 결과는 같은데, 그 거름이 Play 안쪽이라 transform.position(네이티브)과 사전 조회는
        // 호출마다 전액이었다 - 충각 그라인딩은 이 함수를 틱당 수천 번 부른다(판당 36칸).
        if (_lastPenetrateSoundFrame != Time.frameCount)
        {
            _lastPenetrateSoundFrame = Time.frameCount;
            SoundManager.AudioShot("Penetrate", transform.position, Mathf.Clamp01(amount / 100f));
        }
        // 아래의 붕괴가 이 판을 또 때릴 수 있다. 0으로 **떨어지는 순간**에만 터뜨려야
        // 서브셀 하나가 두 번 무너지지 않는다.
        bool wasAlive = _hp[subIndex] > 0f;

        _hp[subIndex] = Mathf.Max(0f, _hp[subIndex] - amount);
        DamageVersion++;
        DirtySubs |= 1UL << subIndex;

        if (!wasAlive || _hp[subIndex] > 0f)
            return;

        AnyBreached = true;
        _dead++;

        Collapse(subIndex);
        KillOrphans();

        // _collapsed 확인이 여기 있는 이유: KillOrphans가 죽인 칸이 임계를 넘겨 이미
        // 판을 무너뜨렸을 수 있다. 그때 이 아래를 또 타면 붕괴 파편이 두 번 나가고
        // Destroy가 두 번 불린다.
        // _collapsing은 재진입 막이다. 아래에서 치명 모듈을 터뜨리면 그 폭발이 이 판을
        // 다시 때리며 여기로 돌아오는데, 그때 이 블록을 또 타면 판이 잔해로 떨어진 뒤에
        // 한 번 더 떨어지려 든다.
        if (_collapsed || _collapsing
            || _dead < Mathf.CeilToInt(SubCount * Ballistics.PlateCollapseFraction))
            return;

        _collapsing = true;

        // 이웃에게 "네 안쪽 면이 방금 바깥이 됐다"고 알린다. 이게 절단면 발광의 전부다 -
        // 폴링도 탐색도 없이, 판이 죽는 그 순간에 정확히 닿아야 할 8장에게만 간다.
        foreach (Armor neighbour in Neighbours)
        {
            if (neighbour != null && SameBodyAs(neighbour))
                neighbour.AddHeat(Ballistics.HeatFromExposure);
        }

        // **함선에 붙어 있던 판은 지우지 않고 떼어낸다.** PlateCollapseFraction이 0.5라
        // 주저앉는 판은 서브셀이 절반 살아 있다 - 그 재료를 파편으로 흩는 대신 판째로
        // 떠나보내면 "재료는 어디론가 간다"가 눈에 보이는 물체로 남는다. 충각은 채널을
        // 깨끗이 뚫어서 아무것도 안 끊으므로, 이 길이 없으면 맞은 배가 증발한다.
        //
        // **_collapsed를 안 세운다.** 이 판은 잔해에서 서브셀을 하나 더 잃으면 같은 검사에
        // 다시 걸리는데, 그때는 부모가 Hulk라 아래 함선 분기를 안 타고 원래 길로 간다.
        // 판은 잔해로 한 번 더 살고, 그 뒤로는 예전과 글자 그대로 같다.
        // **볼트로 붙은 탄약고·원자로는 판과 함께 끝난다.** 구조가 절반 날아가 선체에서
        // 뜯긴 판이 원자로를 온전히 붙들고 있을 수는 없다.
        //
        // 안 죽이면 판이 모듈을 태우고 잔해로 100 m 밖으로 날아가고, 배에는 구멍만 남는다.
        // 그러다 한참 뒤에 그 잔해를 맞히면 거기서 유폭이 난다 - "사라졌는데 나중에 터진다"가
        // 그것이다. 죽이면 뜯기는 그 순간에 터진다.
        //
        // 이 호출이 유폭 연쇄를 통째로 여기서 시작한다. 위의 _collapsing이 그 연쇄가
        // 이 판으로 돌아왔을 때를 막는다.
        // **List 오버로드다.** 판이 죽을 때마다 배열이 하나씩 태어나는데, 갈리는 중에는
        // 그게 매 틱 여러 장이다. 그리고 이 루프는 유폭 연쇄를 시작하는 자리라
        // 재진입한다 - 그래서 목록도 깊이별로 든다(정적 하나면 안쪽 폭발이 바깥의
        // 순회 대상을 갈아치운다).
        List<CriticalModule> criticals = RentCriticals(_collapseDepth++);

        try
        {
            GetComponentsInChildren(criticals);

            for (int i = 0; i < criticals.Count; i++)
            {
                if (criticals[i] != null)
                    criticals[i].TakeDamage(float.MaxValue);
            }
        }
        finally
        {
            _collapseDepth--;
        }

        if (GetComponentInParent<Ship>() != null
            && GetComponentInParent<HullStructure>() is HullStructure hull
            && hull.Shed(transform))
        {
            // 잔해로 갔다. 이 판은 아직 살아 있으므로 다음 피해를 받을 수 있어야 한다 -
            // 그때는 부모가 Hulk라 위 분기를 안 타고 아래 원래 길로 간다.
            _collapsing = false;
            return;
        }

        _collapsed = true;

        // 남은 칸이 몇 개 있어도 판으로서는 이미 끝났다. 그 몫도 파편으로 나가야지,
        // 그냥 증발하면 서브셀 하나가 죽을 때마다 파편이 나오던 규칙이 여기서만 깨진다.
        CollapseRemains();

        // 구조가 하나도 안 남았다. 콜라이더도 같이 사라져야 한다 - 안 그러면 탄이 없는 판에
        // 계속 걸린다.
        //
        // 주인은 통보만 받고, 다음 틱에 구조 BFS를 다시 돈다. 여기서 바로 하면 파편 연쇄나
        // 물리 콜백 한가운데서 GameObject를 재부모화하게 된다.
        //
        // Ship이 아니라 HullStructure를 찾는다. 잔해 안의 판은 Ship을 못 찾아서 아무에게도
        // 보고하지 못했고, 그래서 잔해는 한 번 떨어진 뒤로 영영 안 쪼개졌다.
        GetComponentInParent<Ship>()?.RecalcMass();
        GetComponentInParent<HullStructure>()?.ReportPlateLost(transform, Heat);

        GetComponentsInChildren(_dyingColliders);

        for (int i = 0; i < _dyingColliders.Count; i++)
            _dyingColliders[i].enabled = false;

        Destroy(gameObject);
    }

    /// <summary>
    /// 판 전체가 고르게 상한다. 충각처럼 한 점으로 파고들지 않고 판을 통째로 미는 충격용.
    /// amount는 판 전체가 받는 총량이고, 여기서 칸 수로 나눈다 - 칸마다 amount를 넣으면
    /// 균일한 게 아니라 SubCount배 센 것이다.
    /// </summary>
    private static readonly Unity.Profiling.ProfilerMarker _mDamageEvenly = new("Armor.DamageEvenly");

    public void ApplyDamageEvenly(float amount)
    {
        if (amount <= 0f)
            return;

        using var _ = _mDamageEvenly.Auto();

        float share = amount / SubCount;

        // 위에서부터 훑는 도중 판이 무너져 사라질 수 있다. ApplyDamage가 _collapsed로
        // 막아주므로 남은 반복은 조용히 아무 일도 안 한다.
        for (int i = 0; i < SubCount; i++)
            ApplyDamage(i, share);

        ShockModules(amount * Ballistics.ModuleShockFraction);
    }

    // 흔들림이 재진입한다: 모듈이 죽으면 CriticalModule.Detonate -> RamImpact.Detonate가
    // 이 판을 다시 때리며 여기로 돌아온다. 깊이별로 목록을 따로 들면 안쪽 폭발이 바깥의
    // 순회 대상을 갈아치우는 일이 없다 - 판 붕괴의 _criticalsByDepth와 같은 패턴이다.
    private static readonly List<List<IDamageable>> _shockedByDepth = new();
    private static int _shockDepth;

    /// <summary>
    /// **판에 볼트로 붙은 것은 판이 받는 충격을 같이 받는다.** 충각 스윕도 유폭 질의도
    /// Armor만 고르므로, 이 길이 없으면 포탑을 정면으로 들이받아도 포탑은 멀쩡하다.
    ///
    /// <see cref="ApplyDamageEvenly"/>에서만 부르는 것이 요점이다 - 그쪽은 판을 통째로
    /// 미는 충격(충각·유폭) 전용이고, 포탄은 <see cref="ApplyDamageAlong"/>으로 온다.
    /// 관통은 판을 뚫고 지나가는 것이라 그 위의 모듈을 흔들 이유가 없다.
    /// </summary>
    private void ShockModules(float amount)
    {
        if (amount <= 0f || _collapsed)
            return;

        while (_shockedByDepth.Count <= _shockDepth)
            _shockedByDepth.Add(new List<IDamageable>());

        List<IDamageable> shocked = _shockedByDepth[_shockDepth++];

        try
        {
            GetComponentsInChildren(shocked);

            for (int i = 0; i < shocked.Count; i++)
            {
                IDamageable module = shocked[i];

                // 이미 죽은 것을 또 때리면 유폭이 두 번 난다.
                if (module != null && !module.Neutralized)
                    module.TakeDamage(amount);
            }
        }
        finally
        {
            _shockDepth--;
        }
    }

    /// <summary>
    /// 판에서 떨어져 나간 서브셀은 부서진 것으로 친다. 아무것도 떠받치지 않는 칸이
    /// 혼자 화면에 남아 있는 것이 이상하기도 하지만, 그보다 그 칸이 아직 RHA를 내고
    /// 있다는 게 더 문제다 - 허공이 탄을 막는다.
    ///
    /// 죽이는 방법은 ApplyDamage 그대로다. 파편도 나가고 붕괴 판정도 그대로 탄다 -
    /// 여기만 특별한 길로 빠지면 "재료는 어디론가 간다"는 규칙에 예외가 생긴다.
    /// </summary>
    // 죽는 길의 목록 둘. 판이 죽을 때마다 배열을 새로 만들던 자리다.
    // 콜라이더 쪽은 재진입하지 않는다(끄고 바로 Destroy) - 깊이가 필요한 것은 모듈뿐이다.
    private static readonly List<Collider2D> _dyingColliders = new();
    private static readonly List<List<CriticalModule>> _criticalsByDepth = new();
    private static int _collapseDepth;

    private static List<CriticalModule> RentCriticals(int depth)
    {
        while (_criticalsByDepth.Count <= depth)
            _criticalsByDepth.Add(new List<CriticalModule>());

        return _criticalsByDepth[depth];
    }

    private static readonly Unity.Profiling.ProfilerMarker _mKillOrphans = new("Armor.KillOrphans");

    private void KillOrphans()
    {
        using var _ = _mKillOrphans.Auto();

        // ApplyDamage -> KillOrphans -> ApplyDamage 로 다시 들어온다. 한 번만 쓸면 된다:
        // 성분을 통째로 걷어냈으므로 새로 고립되는 칸은 생기지 않는다.
        if (_sweeping || _collapsed)
            return;

        _sweeping = true;

        try
        {
            for (int i = 0; i < SubCount; i++)
                _alive[i] = _hp[i] > 0f;

            Ballistics.LargestLivingComponent(_alive, _inLargest);

            for (int i = 0; i < SubCount; i++)
            {
                if (_alive[i] && !_inLargest[i])
                    ApplyDamage(i, float.MaxValue);
            }
        }
        finally
        {
            _sweeping = false;
        }
    }

    /// <summary>
    /// 죽은 서브셀의 재료는 증발한 게 아니라 **뜯겨서 어딘가로 갔다.** 관통 뒤의 파편과 달리
    /// 선호 방향이 없어서 원 전체로 흩뿌린다.
    /// </summary>
    private void Collapse(int subIndex)
    {
        Vector2 world = transform.TransformPoint(
            Ballistics.SubCellCentre(subIndex, _cellSize) + _cellOffset);

        Debug.Assert(stableId >= 0, $"[Armor line.458] '{defName}'에 stableId가 없다. def로 안 지어진 배다.", this);

        SpallResolver.Burst(
            world,
            transform.up,
            Ballistics.CollapseSpread,
            SubCellFullHp * Ballistics.CollapseEnergyFraction,
            Ballistics.CollapseFragmentCount,
            Ballistics.Hash((stableId < 0)? GetInstanceID() : stableId, TickManager.currentTick, subIndex),
            debrisLayer);
    }

    /// <summary>
    /// 살아 있는 서브셀을 남긴 채 판이 통째로 주저앉는다. 남은 재료가 중심에서 한 번에
    /// 터져 나간다 - 서브셀 하나가 죽을 때보다 크고, 판이 놓이는 건 원래 그렇게 보여야 한다.
    /// </summary>
    private void CollapseRemains()
    {
        int living = SubCount - _dead;

        if (living <= 0)
            return;
        Debug.Assert(stableId >= 0, $"[Armor line.480] '{defName}'에 stableId가 없다. def로 안 지어진 배다.", this);
        SpallResolver.Burst(
            transform.TransformPoint(_cellOffset),
            transform.up,
            Ballistics.CollapseSpread,
            living * SubCellFullHp * Ballistics.CollapseEnergyFraction,
            Mathf.Clamp(living, 1, Ballistics.SpallMaxCount),
            Ballistics.Hash((stableId < 0)? GetInstanceID() : stableId, TickManager.currentTick, SubCount),
            debrisLayer);
    }
}
