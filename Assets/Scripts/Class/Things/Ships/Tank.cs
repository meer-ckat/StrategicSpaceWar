using UnityEngine;

/// <summary>
/// 추진제. <see cref="Ship.AvailableDeltaV"/>가 살아있는 탱크의 impulse 합을 지금 질량으로
/// 나눠 남은 Δv를 낸다 - 총력적(kN·s)을 드는 이유는 판을 잃어 질량이 줄면 같은 탱크로도
/// Δv가 늘어나게 하려는 것이다. 반파된 배가 더 민첩해진다.
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class Tank : Thing, IDamageable
{
    public float maxHealth = 100f;
    public float impulse;          // kN·s, 총 용량

    /// <summary>지금 남은 연료(kN·s). 전투 피해로 죽는 것과 별개로, 밀 때마다 준다.</summary>
    public float remaining { get; private set; }

    private float _health;

    public bool Neutralized => _health <= 0f;
    public float Health01 => maxHealth > 0f ? _health / maxHealth : 0f;

    protected override void Awake()
    {
        base.Awake();
        _health = maxHealth;
        remaining = impulse;
    }

    /// <summary>
    /// request만큼 쓰려 하고, 실제로 쓴 만큼을 돌려준다. 남은 것보다 많이 요청하면
    /// 있는 만큼만 내주고 0으로 떨어진다 - 호출부가 부족분을 알아서 처리한다.
    /// </summary>
    public float Consume(float request)
    {
        float used = Mathf.Min(request, remaining);
        remaining -= used;
        return used;
    }

    /// <summary>로드 경로. 값을 그냥 놓는다 - TakeDamage의 부작용을 타지 않는다.</summary>
    public void RestoreHealth01(float fraction)
        => _health = maxHealth * Mathf.Clamp01(fraction);

    public void TakeDamage(float amount)
    {
        if (amount <= 0f) return;
        _health = Mathf.Max(0f, _health - amount);
    }

    public override void OnTick() { }   // 탱크는 틱 필요 없음
}
