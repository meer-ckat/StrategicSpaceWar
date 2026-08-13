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
        if (!TraceChannel(worldEntry, worldDirection, 1f, weights, out entry, diameter))
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

    public void ApplyDamage(int subIndex, float amount)
    {
        if (amount <= 0f)
            return;

        DamageLog.Hit(this);

        // The collapse below can damage this same plate again. Firing only on the
        // transition to 0 keeps one sub-cell from collapsing twice.
        bool wasAlive = _hp[subIndex] > 0f;

        _hp[subIndex] = Mathf.Max(0f, _hp[subIndex] - amount);

        if (!wasAlive || _hp[subIndex] > 0f)
            return;

        AnyBreached = true;
        _dead++;

        Collapse(subIndex);

        if (_dead < Mathf.CeilToInt(SubCount * Ballistics.PlateCollapseFraction))
            return;

        // 남은 칸이 몇 개 있어도 판으로서는 이미 끝났다. 그 몫도 파편으로 나가야지,
        // 그냥 증발하면 서브셀 하나가 죽을 때마다 파편이 나오던 규칙이 여기서만 깨진다.
        CollapseRemains();

        // Nothing structural is left. The collider has to go with it, or shells keep
        // stopping on a plate that is no longer there.
        //
        // The ship only gets told; it re-runs the structural BFS on its next tick. Doing
        // it here would reparent GameObjects inside a spall cascade or a physics callback.
        GetComponentInParent<Ship>()?.ReportPlateLost();

        Destroy(gameObject);
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
