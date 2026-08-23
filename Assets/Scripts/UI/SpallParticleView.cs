using UnityEngine;
using UnityEngine.VFX;

/// <summary>
/// 착탄 지점의 섬광만 그린다.
/// 탄도 링버퍼를 읽기만 하므로 시뮬레이션에 되먹임이 없다.
///
/// 파편 궤적은 SpallTrails가 담당한다.
/// 여기서는 실제 파편 방향을 재현하지 않고 착탄 섬광만 그린다.
///
/// **C#은 명중당 이벤트 한 발과 위치만 보낸다.** 몇 개를 어느 방향으로 뿌릴지는
/// 그래프(Spawn Burst + Random)가 정한다. 이유 둘:
///
/// 1) VFXEventAttribute는 그래프가 아는 attribute 이름만 받는다. 표준은 position -
///    "impactPosition" 같은 이름은 그래프에 같은 이름이 선언돼 있지 않으면 에러 없이
///    버려져서, 파티클이 전부 원점에서 태어난다.
/// 2) 같은 프레임에 SendEvent를 여러 번 하면서 attribute 객체 하나를 고쳐 쓰면,
///    이벤트가 그 객체를 참조로 물고 있다가 나중에 처리되므로 전부 마지막 값을
///    받는다. ParticleSystem.Emit처럼 호출 시점에 복사되지 않는다.
///
/// 섬광의 방향 난수가 그래프로 넘어가면서 결정론에서 빠지지만, 이 값은 시뮬레이션이
/// 한 번도 안 읽는 그림 전용이라 결정론 불변식과 무관하다.
///
/// 그래프 쪽 요구사항 (SpallFlash.vfx):
///   - Event 노드 이름 "Impact" -> Spawn (Burst, count는 impactParticles 프로퍼티)
///   - Initialize: Inherit Source Position, Set Velocity (Random Direction * impactSpeed)
///   - Output: Unlit 쿼드. 외피 오버레이(order 100) 위에 보이려면 VFXRenderer
///     sorting을 그 위로.
///
/// One instance per scene; it reads statics.
/// </summary>
[RequireComponent(typeof(VisualEffect))]
public class SpallParticleView : MonoBehaviour
{
    [SerializeField] private VisualEffect effect;

    [Header("Impact flash - 그래프에 같은 이름의 exposed property가 있으면 넣어준다")]
    [SerializeField] private int impactParticles = 6;
    [SerializeField] private float impactSpeed = 4f;

    private static readonly int ImpactEvent = Shader.PropertyToID("Impact");
    private static readonly int PositionAttribute = Shader.PropertyToID("position");

    // **이 이름들은 Penetration.vfx에서 grep으로 뽑은 실제 값이다.** 대소문자까지
    // 정확해야 한다 - 틀리면 에러 없이 버려져서 파티클이 원점에서 태어난다. 그래프의
    // 두 커스텀 attribute가 서로 다른 표기(impactPosition / ImpactSpeed)인 것도
    // 그대로 따른다. 그래프에서 이름을 바꾸면 여기도 같이 바꿔야 한다.
    private static readonly int ImpactPositionAttribute = Shader.PropertyToID("impactPosition");
    private static readonly int ImpactSpeedAttribute = Shader.PropertyToID("ImpactSpeed");
    private static readonly int ParticlesProperty = Shader.PropertyToID("impactParticles");
    private static readonly int SpeedProperty = Shader.PropertyToID("ImpactSpeed");

    private VFXEventAttribute _eventAttribute;
    private long _shown = -1;

    private void Awake()
    {
        if (effect == null)
            effect = GetComponent<VisualEffect>();

        _eventAttribute = effect.CreateVFXEventAttribute();

        // 튜닝 숫자는 여기 남기고 그래프가 받아 쓴다. 그래프에 프로퍼티가 없으면
        // 조용히 넘어간다 - HasInt 없이 SetInt를 부르면 콘솔에 경고가 쌓인다.
        if (effect.HasInt(ParticlesProperty))
            effect.SetInt(ParticlesProperty, impactParticles);

        if (effect.HasFloat(SpeedProperty))
            effect.SetFloat(SpeedProperty, impactSpeed);
    }

    private void Update()
    {
        if (effect == null)
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
        // 표준 이름과 커스텀 이름 양쪽에 넣는다. 그래프가 아는 쪽만 실린다 -
        // Has 검사 없이 없는 이름에 Set하면 콘솔 경고가 쌓인다.
        if (_eventAttribute.HasVector3(PositionAttribute))
            _eventAttribute.SetVector3(PositionAttribute, r.spallOrigin);

        if (_eventAttribute.HasVector3(ImpactPositionAttribute))
            _eventAttribute.SetVector3(ImpactPositionAttribute, r.spallOrigin);

        // 그래프가 속도 크기를 소스 attribute로 읽는다. 안 보내면 0이라 파티클이
        // 제자리에 선다 - "방향이 없다"로 보이는 증상의 정체.
        if (_eventAttribute.HasFloat(ImpactSpeedAttribute))
            _eventAttribute.SetFloat(ImpactSpeedAttribute, impactSpeed);

        effect.SendEvent(ImpactEvent, _eventAttribute);
    }
}
