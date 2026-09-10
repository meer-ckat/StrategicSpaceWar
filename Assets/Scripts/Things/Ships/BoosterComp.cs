using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 방아쇠를 당길 때만 추력을 내는 엔진. def의 `comps`에 붙여서 쓴다.
///
/// **새 추력 경로를 만들지 않는다.** <see cref="Ship.AvailableThrust"/>는 여전히
/// <see cref="Engine.MaxPower"/>만 더하고, 이 부속은 그 값을 켰다 껐다 할 뿐이다.
/// 그래서 부스터가 잔해로 떠나든 파괴되든 이미 있는 규칙이 그대로 적용된다 -
/// <c>StillAboard</c>도 <c>Neutralized</c>도 손댈 것이 없다.
///
/// def에서는 <c>MaxPower</c>를 **0으로** 두고 <c>boostPower</c>에 진짜 값을 적는다.
/// 그러면 안 누르고 있을 때 추력이 0이고, 부스터가 "그냥 좋은 엔진"이 되지 않는다.
///
/// 노즐의 빛은 이 모듈에 붙은 URP <c>Light2D</c> 하나다. 뒤로 뱉는 불꽃은 오브젝트가
/// 아니라 <see cref="BoosterTrail"/>의 링버퍼 한 줄이다 - 예전에는 불꽃마다 Light2D를
/// 단 GameObject가 태어나서, 편대가 부스터를 밟으면 초당 1,300개가 생성·파괴됐다.
/// 파티클 시스템을 안 쓰는 이유는 취향이 아니라 데이터다 - ParticleSystem의 설정은
/// 중첩 구조체라 JsonUtility가 못 읽어서, def로 값을 넣을 수가 없다.
/// </summary>
[RequireComponent(typeof(Engine))]
public sealed class BoosterComp : MonoBehaviour
{
    /// <summary>켜졌을 때의 추력(kN). def의 MaxPower가 아니라 이쪽을 적는다.</summary>
    [SerializeField] private float boostPower = 800f;

    /// <summary>불꽃 하나를 뱉는 간격(초). 작을수록 촘촘하고 비싸다.</summary>
    [SerializeField] private float flameInterval = 0.045f;

    /// <summary>노즐에서 얼마나 뒤에 뱉나(m).</summary>
    [SerializeField] private float flameOffset = 0.7f;

    [SerializeField] private float litIntensity = 14f;
    [SerializeField] private float litRadius = 4.5f;

    private Engine _engine;
    private Light2D _light;
    private float _nextFlame;

    private void Start()
    {
        _engine = GetComponent<Engine>();
        _light = GetComponent<Light2D>();
    }

    private void Update()
    {
        if (_engine == null)
            return;

        // 매 프레임 다시 묻는다. 판이 잔해로 떨어져 나가면 부모가 바뀌는데, 캐시해 두면
        // 우주로 날아가는 부스터가 본체의 Shift를 계속 듣는다.
        Ship ship = GetComponentInParent<Ship>();

        bool on = ship != null && ship.Boosting && !_engine.Neutralized;

        _engine.MaxPower = on ? boostPower : 0f;
        _engine.MaxReversePower = on ? boostPower : 0f;

        Glow(on);

        if (on)
            Spit();
    }

    /// <summary>
    /// 노즐 자체의 불빛. 켜질 때는 즉시, 꺼질 때는 잠깐 남는다 - 실제로 뜨거운 것이
    /// 순간에 식지 않는 것과 같고, 연타할 때 깜빡이 되는 것도 이걸로 막힌다.
    /// </summary>
    private void Glow(bool on)
    {
        if (_light == null)
            return;

        float target = on ? litIntensity : 0f;
        float speed = on ? 24f : 128f;

        _light.intensity = Mathf.MoveTowards(_light.intensity, target, speed * Time.deltaTime);
        _light.pointLightOuterRadius = Mathf.Max(0.5f, litRadius * (_light.intensity / Mathf.Max(0.01f, litIntensity)));
    }

    /// <summary>
    /// 불꽃 하나를 뒤로 뱉는다. **월드 좌표로 놓고 온다** - 배에 붙이면 불꽃이 배를
    /// 따라다녀서 가속하는 것처럼 안 보인다. 우주에 놓고 오면 배가 그 자리를 떠나면서
    /// 꼬리가 생긴다.
    /// </summary>
    /// <summary>
    /// 워프 충전. 노즐 뒤로 length m까지 불꽃을 줄지어 놓는다 - 짧은 불꽃이 긴 플라즈마
    /// 기둥이 된다. 배 자체를 늘리는 것보다 배 뒤의 에너지가 늘어나는 것이 워프로 읽힌다.
    /// 프레임마다 불러도 되는 양이다: 3 m 간격이라 destroyer 부스터 12개 × 15 m = 60개.
    /// </summary>
    public void Plume(float length, Color colour)
    {
        if (_engine == null || _engine.Neutralized)
            return;

        Vector3 back = transform.parent != null ? -transform.parent.up : -transform.up;

        for (float d = flameOffset; d < length; d += 3f)
            BoosterTrail.Add(transform.position + back * d, colour);
    }

    private void Spit()
    {
        if (Time.time < _nextFlame)
            return;

        _nextFlame = Time.time + Mathf.Max(0.01f, flameInterval);

        // 배 기준 아래쪽. 부스터는 다리 밑이나 선체 뒤에 붙으므로 그쪽이 노즐이다.
        Vector3 back = transform.parent != null ? -transform.parent.up : -transform.up;

        // 불꽃 색은 노즐 빛에서 그대로 온다. 같은 불이 두 군데서 나올 이유가 없고,
        // def에 색 키를 하나 더 두면 둘이 갈라져도 아무도 모른다.
        Color colour = _light != null ? _light.color : new Color(1f, 0.55f, 0.22f);

        BoosterTrail.Add(transform.position + back * flameOffset, colour);
    }
}
