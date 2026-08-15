using UnityEngine;
using Core;

/// <summary>
/// One grid cell, split into a SubGrid x SubGrid sub-cell grid.
/// Armor remembers what hit it: sub-cell HP drives the RHA multiplier.
/// </summary>
[RequireComponent(typeof(Collider2D))]
public abstract class Armor : Thing
{
    public const int SubGrid = Ballistics.SubGrid;
    public const int SubCount = Ballistics.SubCount;

    [Header("Armor")]
    [SerializeField] private float rha = 100f;        // mm

    /// <summary>
    /// Structural budget for the WHOLE cell, not per sub-cell. Sub-cell HP is derived,
    /// so SubGrid stays a readout-precision knob: bumping 3x3 to 6x6 used to quadruple
    /// how much punishment a cell could take.
    /// </summary>
    [SerializeField] private float cellHp = 1800f;

    [SerializeField] private float plateThickness = 0.1f; // m, actual plate - overmatch only

    /// <summary>Used only when the collider is not a box. A box tells us its own size.</summary>
    [SerializeField] private Vector2 fallbackCellSize = Vector2.one;

    /// <summary>What the debris from a collapsing sub-cell can hit. Armor | Module.</summary>
    [SerializeField] private LayerMask debrisLayer;

    private readonly float[] _hp = new float[SubCount];
    private int _dead;

    // 고립 서브셀 판정용. 판마다 하나씩 - 한 판을 쓰는 동안 다른 판이 끼어들 수 있다
    // (파편이 옆 판을 치고, 그 판이 다시 쓸기 시작한다).
    private readonly bool[] _alive = new bool[SubCount];
    private readonly bool[] _inLargest = new bool[SubCount];
    private bool _sweeping;

    // Destroy is deferred to the end of the frame, so the ApplyDamageAlong loop that killed
    // the plate keeps calling into it. Without this latch every further sub-cell death
    // re-runs the collapse burst and re-tells the ship the plate is gone.
    private bool _collapsed;

    // The sub-cell grid maps onto the collider. A hand-typed size that disagrees with it
    // lands the entry point in the wrong sub-cell and lets the channel run out past the
    // real edge of the plate, so it is read from the collider instead of typed.
    private Vector2 _cellSize = Vector2.one;
    private Vector2 _cellOffset;

    public float PlateThickness => plateThickness;
    public float SubCellMaxHp => cellHp / SubCount;

    /// <summary>Undamaged nominal RHA.</summary>
    public float RHA => rha;

    protected override void Awake()
    {
        base.Awake();

        for (int i = 0; i < SubCount; i++)
            _hp[i] = SubCellMaxHp;

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
    }

    // ponytail: assumes uniform scale 1 on the armor transform. Divide by lossyScale if that changes.
    private Vector2 ToCellLocal(Vector2 worldPoint)
        => (Vector2)transform.InverseTransformPoint(worldPoint) - _cellOffset;

    /// <summary>
    /// Sub-cell an impact travelling in worldDirection lands in. The direction matters:
    /// hit points sit exactly on cell boundaries constantly, and without it the index
    /// can name the sub-cell the shell is leaving rather than the one it enters.
    /// </summary>
    public int SubIndexAt(Vector2 worldPoint, Vector2 worldDirection)
        => Ballistics.EntrySubIndex(
            ToCellLocal(worldPoint),
            transform.InverseTransformDirection(worldDirection),
            _cellSize);

    /// <summary>
    /// Effective RHA along the line the shell would take through this cell, and the
    /// per-sub-cell weights that produced it. Resistance and damage must read the same
    /// line - a sub-cell that takes damage but never resists is decoration.
    /// A fresh cell still reads its nominal RHA: the weights sum to 1.
    /// </summary>
    /// <param name="diameter">Shell diameter in metres. 0 sweeps the centre line only.</param>
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
    /// Fill weights with the channel, cut off at depthFraction of the way through.
    /// Returns false when the ray leaves immediately; entry is the sub-cell it touched.
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
    /// Damage along the channel, not just the face it came in through.
    /// The energy budget is the same - it is spread over the sub-cells the line crosses.
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

    /// <summary>HP 0 sub-cell cannot hold pressure - the room behind it vents.</summary>
    public bool IsBreached(int subIndex) => _hp[subIndex] <= 0f;

    /// <summary>Latched on the hit that opens the hole, so atmosphere never scans sub-cells.</summary>
    public bool AnyBreached { get; private set; }

    /// <summary>Real collider size, read at Awake. The x-ray overlay sizes itself off this.</summary>
    public Vector2 CellSize => _cellSize;

    /// <summary>
    /// Sub-cell containing a point in the plate's LOCAL space. The single place anything
    /// outside this class is allowed to ask where the grid is - a collider-driven grid can
    /// then replace the body of this method and nothing else has to know.
    /// </summary>
    public int SubIndexAtLocal(Vector2 localPoint)
        => Ballistics.SubIndex(localPoint - _cellOffset, _cellSize);

    /// <summary>
    /// Bumped whenever any sub-cell loses HP. The skin repaints off this instead of
    /// diffing 36 floats every frame; a plate that is not being shot costs one int compare.
    /// </summary>
    public int DamageVersion { get; private set; }

    public void ApplyDamage(int subIndex, float amount)
    {
        if (amount <= 0f || _collapsed)
            return;

        DamageLog.Hit(this);

        // The collapse below can damage this same plate again. Firing only on the
        // transition to 0 keeps one sub-cell from collapsing twice.
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

        // 남은 칸이 몇 개 있어도 판으로서는 이미 끝났다. 그 몫도 파편으로 나가야지,
        // 그냥 증발하면 서브셀 하나가 죽을 때마다 파편이 나오던 규칙이 여기서만 깨진다.
        CollapseRemains();

        // Nothing structural is left. The collider has to go with it, or shells keep
        // stopping on a plate that is no longer there.
        //
        // 주인은 통보만 받고, 다음 틱에 구조 BFS를 다시 돈다. 여기서 바로 하면 파편 연쇄나
        // 물리 콜백 한가운데서 GameObject를 재부모화하게 된다.
        //
        // Ship이 아니라 HullStructure를 찾는다. 잔해 안의 판은 Ship을 못 찾아서 아무에게도
        // 보고하지 못했고, 그래서 잔해는 한 번 떨어진 뒤로 영영 안 쪼개졌다.
        GetComponentInParent<HullStructure>()?.ReportPlateLost();

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
    /// The plate material at a dead sub-cell did not evaporate - it came apart and went
    /// somewhere. Unlike spall behind a penetration this has no preferred direction, so
    /// it sprays the full circle.
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
    /// The plate gives way with living sub-cells still on it. Their material leaves in one
    /// burst from the centre - bigger than a single sub-cell going, which is what a plate
    /// letting go should look like.
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
