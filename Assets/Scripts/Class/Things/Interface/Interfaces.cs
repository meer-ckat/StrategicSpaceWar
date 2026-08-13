public interface IDamageable
{
    bool Neutralized { get; }   // 읽기만

    /// <summary>0..1. 표시 전용이라 읽기만 - 체력은 TakeDamage로만 움직인다.</summary>
    float Health01 { get; }

    void TakeDamage(float amount);
}
