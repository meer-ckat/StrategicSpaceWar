using UnityEngine;

public enum ShellState { Intact = 0, Deformed = 1, Shattered = 2 }
public enum HitOutcome { Ricochet, Blocked, Penetrated }

/// <summary>Immutable snapshot of a shell at the moment of contact.</summary>
public struct ProjectileState
{
    public Vector2 velocity;

    public float mass;            // kg
    public float caliber;         // mm
    public float shatterVelocity; // m/s
    public float penetrationK;

    public float integrity;
    public ShellState state;

    public int projectileId;
    public int hitIndex;
    public long tick;

    public float Speed => velocity.magnitude;

    // Gameplay approximation, not a physical law.
    // Swap for an AnimationCurve when tuning demands it (1.0->1.0 / 0.8->0.95 / 0.5->0.65 / 0.2->0.2).
    public float IntegrityFactor => integrity;

    public float Penetration =>
        Ballistics.Penetration(penetrationK, IntegrityFactor, Speed, mass, caliber);
}

/// <summary>
/// The set of surfaces contacted simultaneously this hit.
/// Reused buffer - never allocate one per hit in the tick loop.
/// </summary>
public sealed class SurfaceSet
{
    public const int MaxSurfaces = 8;

    public int count;
    public float minDistance;
    public Vector2 hitPoint;
    public Vector2 targetVelocity;
    public float plateThickness;
    public Collider2D primaryCollider;

    public readonly Vector2[] normal = new Vector2[MaxSurfaces];
    public readonly float[] rha = new float[MaxSurfaces];   // channel-weighted, damage-adjusted
    public readonly Armor[] armor = new Armor[MaxSurfaces];
    public readonly int[] subIndex = new int[MaxSurfaces];

    /// <summary>
    /// Per-surface sub-cell channel. Computed once during collection so resistance and
    /// damage read the same line - sub-cells that never resist would be decoration.
    /// </summary>
    public readonly float[][] channel = new float[MaxSurfaces][];

    public SurfaceSet()
    {
        for (int i = 0; i < MaxSurfaces; i++)
            channel[i] = new float[Ballistics.SubCount];
    }
}

public struct HitResult
{
    public HitOutcome outcome;
    public ShellState newState;
    public Vector2 newVelocity;
    public float newIntegrity;
    public float armorDamage;

    // Resolve decides *what* happens. SpallResolver applies it.

    /// <summary>
    /// The shell broke up on its way through. Its remains are spawned as real projectiles
    /// instead of resolved as one-tick rays - SpallResolver must skip these or they get
    /// paid for twice.
    /// </summary>
    public bool heavySpall;

    public uint spallSeed;
    public int spallCount;
    public float spallEnergy;
    public float spallSpread;     // deg, half-angle
    public Vector2 spallDirection;
    public Vector2 spallOrigin;

    // debug only

    /// <summary>Which impact this was for this shell. 0 = first thing it ever touched.</summary>
    public int shellHitIndex;

    public int surfaceCount;

    /// <summary>Index into the contacted surfaces that judged the angle. -1 = normal-incidence fallback.</summary>
    public int judgeIndex;

    public float angleDeg;
    public float effectiveRHA;
    public float impactSpeed;
    public float normalSpeed;
    public float resistance;
    public float severity;
    public float penetrationBefore;
    public float penetrationAfter;
    public float oldIntegrity;
}

/// <summary>xorshift32. Seeded, so replays and same-seed spall patterns match.</summary>
public struct DeterministicRng
{
    private uint _s;

    public DeterministicRng(uint seed)
    {
        _s = seed == 0u ? 0x9E3779B9u : seed;
    }

    public uint NextUInt()
    {
        _s ^= _s << 13;
        _s ^= _s >> 17;
        _s ^= _s << 5;
        return _s;
    }

    public float Next01() => (NextUInt() >> 8) * (1f / 16777216f);

    public float Range(float min, float max) => min + (max - min) * Next01();
}
