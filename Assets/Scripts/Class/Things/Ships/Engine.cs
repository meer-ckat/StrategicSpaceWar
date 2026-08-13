using UnityEngine;

[RequireComponent(typeof(Collider2D))]
public class Engine : Thing, IDamageable
{
    public float maxHealth = 100f;
    public float MaxPower;          // kN
    public float MaxReversePower;
    public float currentPower { get; set; }

    private float _health;

    public bool Neutralized => _health <= 0f;
    public float Health01 => maxHealth > 0f ? _health / maxHealth : 0f;

    protected override void Awake()
    {
        base.Awake();
        _health = maxHealth;
    }

    public void TakeDamage(float amount)
    {
        if (amount <= 0f) return;
        _health = Mathf.Max(0f, _health - amount);
    }

    public override void OnTick() { }   // 엔진은 틱 필요 없음
}