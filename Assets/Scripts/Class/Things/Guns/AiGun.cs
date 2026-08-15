using UnityEngine;

/// <summary>
/// 자동 포탑. Gun이 선회·사각 판정·연사율을 전부 이미 하고 있어서, 여기서 정하는 것은
/// 무엇을 겨누느냐 하나다.
///
/// WantsToFire를 오버라이드하지 않는 것이 핵심이다 - Gun의 기본값이 true라, 표적이
/// 사선(fireArc)에 들어오는 순간 알아서 쏜다. M7Cannon이 그걸 마우스 버튼으로 덮어썼을 뿐,
/// 자동 사격이 원래 기본 동작이다.
/// </summary>
public class AiGun : Gun
{
    protected override bool TryGetTarget(out Vector2 worldPoint)
    {
        worldPoint = default;

        if (owner == null)
            return false;

        Ship target = owner.NearestHostile();

        if (target == null)
            return false;

        // 편차 조준은 넣지 않는다. 탄속 900 m/s에 교전거리 40 m면 비행시간 0.045초,
        // 함선이 10 m/s로 움직여도 리드가 0.45 m라 함선 크기보다 작다.
        // 원거리 교전이 생기면 여기에 세 줄 추가하면 된다.
        worldPoint = target.transform.position;
        return true;
    }
}
