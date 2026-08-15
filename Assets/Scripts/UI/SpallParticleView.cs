using UnityEngine;

/// <summary>
/// 착탄 지점의 섬광만 그린다. 탄도 링버퍼를 읽기만 하므로 시뮬레이션에 되먹임이 없고,
/// 관통 수치를 하나도 안 건드리고 겉모습만 다시 잡을 수 있다.
///
/// 파편이 지나간 선은 SpallTrails가 그린다. 예전에는 여기서도 부채꼴을 그렸는데,
/// SpallResolver와 '같은 시드로 각도를 다시 뽑는' 방식이라 Burst의 난수 인출 순서가
/// 바뀌자 조용히 엉뚱한 곳을 그리기 시작했다. 다시 유도하지 말고 받아 그릴 것.
///
/// One instance per scene; it reads statics.
/// </summary>
[RequireComponent(typeof(ParticleSystem))]
public class SpallParticleView : MonoBehaviour
{
    [SerializeField] private ParticleSystem system;

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
    }
}
