using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 잠깐 뜨고 꺼지는 빛. 전부 그림이다 - 이 파일이 통째로 없어져도 판정은 똑같이 돌아간다.
///
/// **반경이 커지는 것이 핵심이다.** 유폭은 한 틱 안에 전부 끝나서, 판 40장이 16 ms에 사라진다.
/// 사람 눈에 그건 "터졌다"가 아니라 "없어졌다"로 읽힌다. 밝기만 번쩍이면 여전히 한 프레임짜리
/// 사건이고, 빛이 바깥으로 퍼져야 비로소 충격파가 지나간 것으로 읽힌다.
///
/// 시뮬레이션을 늦추지 않고 그림만 시간을 갖는다. 실제로 일어난 사건을 사람이 볼 수 있는
/// 속도로 보여주는 것이지, 없던 일을 만드는 것이 아니다.
/// </summary>
public sealed class Flash : Thing
{
    /// <summary>빛이 완전히 꺼질 때까지. 잔광이 남는 시간이다.</summary>
    public float seconds = 0.35f;

    /// <summary>
    /// 충격파가 끝까지 퍼지는 데 걸리는 시간. **seconds와 따로다.**
    /// 하나로 묶으면 잔광을 늘리는 순간 충격파가 같이 느려져서, 폭발이 아니라 부풀어 오르는
    /// 거품으로 보인다. 빛은 오래 남아도 되지만 충격파는 빨라야 한다.
    /// </summary>
    public float expandSeconds = 0.4f;

    public float peakIntensity = 12f;
    public float startRadius = 2f;
    public float endRadius = 16f;

    private Light2D _light;
    private float _age;

    protected override void Awake()
    {
        base.Awake();
        _light = GetComponent<Light2D>();

        if (_light == null)
            Debug.LogError($"[Flash] {name}에 Light2D가 없다. def의 comps를 봐라.", this);
    }

    public override void OnTick() { }

    // 틱이 아니라 프레임으로 돈다. 그림이라 틱 격자에 맞출 이유가 없고, 60틱에 묶이면
    // 고프레임에서 계단으로 보인다.
    private void Update()
    {
        _age += Time.deltaTime;

        float t = _age / Mathf.Max(1e-3f, seconds);

        if (t >= 1f || _light == null)
        {
            Destroy(gameObject);
            return;
        }

        // 즉시 최대로 뜨고 제곱으로 꺼진다. 실제 폭발의 밝기 곡선이 그렇고, 선형으로 꺼지면
        // 끝이 질질 끌려서 "연기"처럼 보인다.
        _light.intensity = peakIntensity * (1f - t) * (1f - t);

        // 충격파는 자기 시계로 돈다. 다 퍼진 뒤에는 그 크기로 남아서 잔광이 된다.
        float spread = Mathf.Clamp01(_age / Mathf.Max(1e-3f, expandSeconds));

        _light.pointLightOuterRadius = Mathf.Lerp(startRadius, endRadius, spread);
    }
}
