using UnityEngine;

/// <summary>
/// Debug ring buffer. Resolve is pure, so recording is free and nothing here feeds back
/// into the simulation - deleting this file would change no outcome.
/// </summary>
public static partial class PenetrationManager
{
    public const int LogCapacity = 256;

    private static readonly HitResult[] _log = new HitResult[LogCapacity];
    private static int _logHead;

    public static int LogCount { get; private set; }

    /// <summary>Never wraps. UI uses it both as a hit number and as a "something changed" stamp.</summary>
    public static long TotalHits { get; private set; }

    /// <summary>Armor of the primary surface of the most recent hit, for the sub-cell readout.</summary>
    public static Armor LastArmor { get; private set; }
    public static int LastSubIndex { get; private set; }

    // Per-surface breakdown of the most recent hit. HitResult is a struct and cannot carry
    // arrays without allocating, and only the newest hit is ever inspected, so it lives here.
    public static readonly Vector2[] LastNormals = new Vector2[SurfaceSet.MaxSurfaces];
    public static readonly float[] LastRha = new float[SurfaceSet.MaxSurfaces];

    /// <summary>
    /// Per-sub-cell share of the most recent shell's channel. Readout only - the live
    /// weights live in SurfaceSet.channel. Primary surface only on an edge hit.
    /// </summary>
    public static readonly float[] LastChannel = new float[Ballistics.SubCount];

    public static void Record(in HitResult r, SurfaceSet s = null)
    {
        _log[_logHead] = r;
        _logHead = (_logHead + 1) % LogCapacity;
        if (LogCount < LogCapacity) LogCount++;

        TotalHits++;

        if (s == null || s.count <= 0)
        {
            LastArmor = null;
            LastSubIndex = 0;
            return;
        }

        LastArmor = s.armor[0];
        LastSubIndex = s.subIndex[0];

        for (int i = 0; i < s.count; i++)
        {
            LastNormals[i] = s.normal[i];
            LastRha[i] = s.rha[i];
        }
        // LastChannel is filled by Projectile.Apply - the channel is not final until the
        // outcome is known and a blocked round has been cut back to how far it got.
    }

    /// <summary>0 = most recent.</summary>
    public static HitResult GetLog(int age) =>
        _log[((_logHead - 1 - age) % LogCapacity + LogCapacity) % LogCapacity];

    public static string Describe(in HitResult r) =>
        $"Surfaces {r.surfaceCount} judge {(r.judgeIndex < 0 ? "FALLBACK" : "N" + r.judgeIndex)} | " +
        $"Angle {r.angleDeg:F1}° | EffRHA {r.effectiveRHA:F0}mm | Vn {r.normalSpeed:F0}m/s | " +
        $"Resist {r.resistance:F2} | Severity {r.severity:F3} | " +
        $"Integrity {r.oldIntegrity:F2}->{r.newIntegrity:F2} | " +
        $"Pen {r.penetrationBefore:F0}->{r.penetrationAfter:F0}mm | " +
        $"{r.outcome}/{r.newState} | Residual {r.newVelocity.magnitude:F0}m/s";
}
