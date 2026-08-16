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
    /// 판 **1 m²당** 구조 예산. 실제 총량은 콜라이더 넓이를 곱해서 나온다.
    ///
    /// 두 가지를 동시에 막는 정의다.
    /// 1. 서브셀당이 아니라 총량이라, SubGrid는 읽기 정밀도 손잡이로 남는다 - 예전에 3x3을
    ///    6x6으로 올렸더니 판이 4배 튼튼해졌다.
    /// 2. m²당이라, 판 크기가 달라도 재료 밀도가 같다. 예전에는 넓이를 안 봐서 0.4x1.0 얇은
    ///    패널이 1x1 벽과 똑같은 총량을 받았고, 서브셀 하나로 치면 2.7배 단단했다.
    ///    45도 경사판(1 x 1.414)은 프리팹에 1800 x sqrt(2) = 2546을 손으로 적어 넣어야 했는데,
    ///    새 판 모양을 만들 때마다 사람이 곱셈을 하는 것이 곧 언젠가 잊는다는 뜻이다.
    /// </summary>
    [FormerlySerializedAs("cellHp")]
    [SerializeField] private float hpPerSquareMetre = 1800f;

    [SerializeField] private float plateThickness = 0.1f; // m, 실제 판 두께. 오버매치 판정에만 쓴다

    /// <summary>콜라이더가 박스가 아닐 때만 쓴다. 박스는 자기 크기를 스스로 알려준다.</summary>
    [SerializeField] private Vector2 fallbackCellSize = Vector2.one;

    /// <summary>무너진 서브셀의 파편이 맞힐 수 있는 것. Armor | Module.</summary>
    [SerializeField] private LayerMask debrisLayer;

    private readonly float[] _hp = new float[SubCount];
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

    // 서브셀 격자는 콜라이더 위에 얹힌다. 손으로 적은 크기가 콜라이더와 어긋나면 진입점이
    // 엉뚱한 서브셀에 떨어지고 채널이 판 바깥까지 흘러나간다. 그래서 적지 않고 읽는다.
    private Vector2 _cellSize = Vector2.one;
    private Vector2 _cellOffset;

    /// <summary>m². Awake에서 콜라이더를 읽은 직후 정해지고 그 뒤로 안 바뀐다.</summary>
    private float _cellArea = 1f;

    public float PlateThickness => plateThickness;

    /// <summary>판 전체 구조 예산. 넓이를 곱한 뒤의 값이라 이게 진짜 총량이다.</summary>
    public float PlateHp => hpPerSquareMetre * _cellArea;

    public float SubCellMaxHp => PlateHp / SubCount;

    /// <summary>안 상한 상태의 명목 RHA.</summary>
    public float RHA => rha;

    protected override void Awake()
    {
        base.Awake();

        // 콜라이더를 먼저 읽는다. SubCellMaxHp가 넓이에서 나오므로 순서가 뒤집히면
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

        for (int i = 0; i < SubCount; i++)
            _hp[i] = SubCellMaxHp;
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
            return EffectiveRhaAt(entry);

        float total = 0f;

        for (int i = 0; i < SubCount; i++)
        {
            if (weights[i] > 0f)
                total += weights[i] * EffectiveRhaAt(i);
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

    public float HpFraction(int subIndex) => _hp[subIndex] / SubCellMaxHp;

    public float RhaMultiplier(int subIndex) =>
        Ballistics.RhaCurve(_hp[subIndex] / SubCellMaxHp);

    public float EffectiveRhaAt(int subIndex) =>
        rha * RhaMultiplier(subIndex);

    /// <summary>HP 0인 서브셀은 기압을 못 버틴다. 그 뒤의 방이 샌다.</summary>
    public bool IsBreached(int subIndex) => _hp[subIndex] <= 0f;

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
    /// 아직 같은 덩어리인가. 잔해로 떨어져 나가도 이웃 참조는 살아 있어서, 그냥 두면 충격이
    /// 100 m 떨어진 조각으로 건너뛴다. 판은 선체 직속 자식이므로 부모가 같으면 같은 덩어리다.
    /// </summary>
    public bool SameBodyAs(Armor other)
        => other != null && other.transform.parent == transform.parent;

    /// <summary>
    /// 판 **로컬** 좌표의 한 점이 들어 있는 서브셀. 바깥에서 "격자가 어디냐"를 물어도 되는
    /// 유일한 자리다 - 콜라이더 기반 격자로 갈아끼울 때 이 메서드 본문만 바꾸면 된다.
    /// </summary>
    public int SubIndexAtLocal(Vector2 localPoint)
        => Ballistics.SubIndex(localPoint - _cellOffset, _cellSize);

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

        Heat *= Mathf.Pow(0.5f, TickManager.TickDeltaTime / Ballistics.HeatHalfLife);

        if (Heat < 0.004f)
            Heat = 0f;
    }

    /// <summary>
    /// 서브셀이 HP를 잃을 때마다 오른다. 그림은 매 프레임 float 36개를 비교하는 대신
    /// 이 숫자만 본다 - 안 맞고 있는 판은 int 비교 한 번이 전부다.
    /// </summary>
    public int DamageVersion { get; private set; }

    public void ApplyDamage(int subIndex, float amount)
    {
        if (amount <= 0f || _collapsed)
            return;

        DamageLog.Hit(this);

        // 맞은 만큼 달아오른다. 서브셀 하나를 통째로 날리는 피해가 기준.
        AddHeat(amount / Mathf.Max(1e-3f, SubCellMaxHp) * Ballistics.HeatFromDamage);
        SoundManager.AudioShot("Penetrate", transform.position, Mathf.Clamp01(amount / 100f));
        // 아래의 붕괴가 이 판을 또 때릴 수 있다. 0으로 **떨어지는 순간**에만 터뜨려야
        // 서브셀 하나가 두 번 무너지지 않는다.
        bool wasAlive = _hp[subIndex] > 0f;

        _hp[subIndex] = Mathf.Max(0f, _hp[subIndex] - amount);
        DamageVersion++;

        if (!wasAlive || _hp[subIndex] > 0f)
            return;

        AnyBreached = true;
        _dead++;

        Collapse(subIndex);
        KillOrphans();

        // _collapsed 확인이 여기 있는 이유: KillOrphans가 죽인 칸이 임계를 넘겨 이미
        // 판을 무너뜨렸을 수 있다. 그때 이 아래를 또 타면 붕괴 파편이 두 번 나가고
        // Destroy가 두 번 불린다.
        if (_collapsed || _dead < Mathf.CeilToInt(SubCount * Ballistics.PlateCollapseFraction))
            return;

        _collapsed = true;

        // 이웃에게 "네 안쪽 면이 방금 바깥이 됐다"고 알린다. 이게 절단면 발광의 전부다 -
        // 폴링도 탐색도 없이, 판이 죽는 그 순간에 정확히 닿아야 할 8장에게만 간다.
        foreach (Armor neighbour in Neighbours)
        {
            if (neighbour != null && SameBodyAs(neighbour))
                neighbour.AddHeat(Ballistics.HeatFromExposure);
        }

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
        GetComponentInParent<HullStructure>()?.ReportPlateLost(transform);

        foreach (Collider2D col in GetComponentsInChildren<Collider2D>())
             col.enabled = false;

        Destroy(gameObject);
    }

    /// <summary>
    /// 판 전체가 고르게 상한다. 충각처럼 한 점으로 파고들지 않고 판을 통째로 미는 충격용.
    /// amount는 판 전체가 받는 총량이고, 여기서 칸 수로 나눈다 - 칸마다 amount를 넣으면
    /// 균일한 게 아니라 SubCount배 센 것이다.
    /// </summary>
    public void ApplyDamageEvenly(float amount)
    {
        if (amount <= 0f)
            return;

        float share = amount / SubCount;

        // 위에서부터 훑는 도중 판이 무너져 사라질 수 있다. ApplyDamage가 _collapsed로
        // 막아주므로 남은 반복은 조용히 아무 일도 안 한다.
        for (int i = 0; i < SubCount; i++)
            ApplyDamage(i, share);
    }

    /// <summary>
    /// 판에서 떨어져 나간 서브셀은 부서진 것으로 친다. 아무것도 떠받치지 않는 칸이
    /// 혼자 화면에 남아 있는 것이 이상하기도 하지만, 그보다 그 칸이 아직 RHA를 내고
    /// 있다는 게 더 문제다 - 허공이 탄을 막는다.
    ///
    /// 죽이는 방법은 ApplyDamage 그대로다. 파편도 나가고 붕괴 판정도 그대로 탄다 -
    /// 여기만 특별한 길로 빠지면 "재료는 어디론가 간다"는 규칙에 예외가 생긴다.
    /// </summary>
    private void KillOrphans()
    {
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

        SpallResolver.Burst(
            world,
            transform.up,
            Ballistics.CollapseSpread,
            SubCellMaxHp * Ballistics.CollapseEnergyFraction,
            Ballistics.CollapseFragmentCount,
            Ballistics.Hash(GetInstanceID(), TickManager.currentTick, subIndex),
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

        SpallResolver.Burst(
            transform.TransformPoint(_cellOffset),
            transform.up,
            Ballistics.CollapseSpread,
            living * SubCellMaxHp * Ballistics.CollapseEnergyFraction,
            Mathf.Clamp(living, 1, Ballistics.SpallMaxCount),
            Ballistics.Hash(GetInstanceID(), TickManager.currentTick, SubCount),
            debrisLayer);
    }
}
