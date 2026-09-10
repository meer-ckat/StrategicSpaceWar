public interface IDamageable
{
    bool Neutralized { get; }   // 읽기만

    /// <summary>0..1. 표시 전용이라 읽기만 - 체력은 TakeDamage로만 움직인다.</summary>
    float Health01 { get; }

    void TakeDamage(float amount);

    /// <summary>
    /// 저장된 손상을 되돌려 놓는다. **피해 경로가 아니라 로드 경로다.**
    ///
    /// TakeDamage로 못 하는 이유는 그것이 절대값을 받는데 maxHealth가 밖에서 안 보이기
    /// 때문이고, 더 큰 이유는 부작용이다 - 탄약고는 0에서 유폭하므로 저장된 손상을 다시
    /// 입히는 순간 로드하자마자 배가 터진다. <see cref="Armor.RestoreHealthFraction"/>과
    /// 같은 이유로 같은 모양이다.
    /// </summary>
    void RestoreHealth01(float fraction);
}
