using UnityEngine;

/// <summary>
/// Draws the last hit. Read-only consumer of the ballistics ring buffer, exactly like
/// HitInspectorUI - nothing here feeds back into the simulation, so the look can be
/// retuned freely without touching a single penetration number.
///
/// One instance per scene; it reads statics.
/// </summary>
[RequireComponent(typeof(ParticleSystem))]
public class SpallParticleView : MonoBehaviour
{
    [SerializeField] private ParticleSystem system;

    [Header("Spall cone")]
    [SerializeField] private int maxParticlesPerHit = 32;

    /// <summary>Shell speeds are hundreds of m/s. Drawn at that rate they vanish in a frame.</summary>
    [SerializeField] private float speedScale = 0.03f;

    /// <summary>Surface spall has no residual speed to borrow, so it takes a cut of the impact.</summary>
    [SerializeField] private float surfaceSpallSpeed = 0.25f;

    [Header("Impact flash")]
    [SerializeField] private int impactParticles = 6;
    [SerializeField] private float impactSpeed = 4f;

    private long _shown = -1;

    private void Awake()
    {
        if (system == null)
            system = GetComponent<ParticleSystem>();

        // Emit positions are world hit points. Local space would drag every spark along
        // with this transform.
        ParticleSystem.MainModule main = system.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
    }

    private void Update()
    {
        if (system == null)
            return;

        if (_shown == PenetrationManager.TotalHits)
            return;

        _shown = PenetrationManager.TotalHits;

        if (PenetrationManager.LogCount == 0)
            return;

        Draw(PenetrationManager.GetLog(0));
    }

    private void Draw(in HitResult r)
    {
        var emit = new ParticleSystem.EmitParams();

        // Every hit throws something off the face, penetration or not.
        var flash = new DeterministicRng(r.spallSeed ^ 0x5BF03635u);

        for (int i = 0; i < impactParticles; i++)
        {
            emit.position = r.spallOrigin;
            emit.velocity = Ballistics.Rotate(Vector2.right, flash.Range(0f, 360f))
                * (impactSpeed * flash.Range(0.4f, 1f));

            system.Emit(emit, 1);
        }

        // Heavy fragments are real projectiles now - they draw themselves.
        if (r.spallCount <= 0 || r.heavySpall)
            return;

        // Same seed as SpallResolver, so the sparks land where the rays actually went
        // instead of near where they went.
        var rng = new DeterministicRng(r.spallSeed);

        float speed = r.newVelocity.magnitude;

        if (speed < 1f)
            speed = r.impactSpeed * surfaceSpallSpeed;

        int count = Mathf.Min(r.spallCount, maxParticlesPerHit);

        for (int i = 0; i < count; i++)
        {
            Vector2 d = Ballistics.Rotate(r.spallDirection, rng.Range(-r.spallSpread, r.spallSpread));

            emit.position = r.spallOrigin;
            emit.velocity = d * (speed * speedScale);

            system.Emit(emit, 1);
        }
    }
}
