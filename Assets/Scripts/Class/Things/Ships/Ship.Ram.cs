using UnityEngine;

/// <summary>
/// 충각 진입점. 실제 계산은 RamImpact에 있다 - 잔해(HullDebris)도 같은 규칙으로 맞아야
/// 해서, 함선에만 있으면 잔해가 체력 무한인 벽이 된다.
/// </summary>
public abstract partial class Ship
{
    private void OnCollisionEnter2D(Collision2D collision)
        => RamImpact.Resolve(transform, collision);
}
