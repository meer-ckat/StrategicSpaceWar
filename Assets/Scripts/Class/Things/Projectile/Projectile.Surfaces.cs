using UnityEngine;

/// <summary>
/// 이번 충돌에서 탄이 동시에 닿은 면들을 모으는 쪽. 판정은 하지 않는다 - 모아서
/// PenetrationManager에 넘기는 것까지가 여기 일이다.
/// </summary>
public abstract partial class Projectile
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="origin"></param>
    /// <param name="dir"></param>
    /// <param name="distance"></param>
    /// <param name="lastCollider"></param>
    /// 모서리, 꼭짓점과 같이 두 개 이상의 콜라이더가 겹친 지점에서 RHA를 구하는 함수. 이게 없으면 워썬더식 법선 기준 4도 도탄이 되니까 꼭 유지.
    /// <returns></returns>
    private bool CollectSurfaces(Vector2 origin, Vector2 dir, float distance, Collider2D lastCollider)
    {
        _surfaces.count = 0; //surfaces는 이번 레이케스트에 맞은 collider들의 수다.
        _surfaces.primaryCollider = null; //주 collider

        int n = Physics2D.RaycastNonAlloc(origin, dir, _hits, distance, armorLayer);
        if (n <= 0) //없다면 리턴
            return false;

        float min = float.MaxValue; //가장 가까운 콜라이더를 찾음

        for (int i = 0; i < n; i++)
        {
            if (!Accept(_hits[i], dir, lastCollider)) //들어온 콜라이더들을 검사해서 유효하지 않다면 return;
                continue;

            if (_hits[i].distance < min) //최소 거리 찾기
                min = _hits[i].distance;
        }

        if (min == float.MaxValue) //최소거리가 없다면 유효하지 않다는 뜻, return;
            return false;

        float thicknessSum = 0f;

        for (int i = 0; i < n && _surfaces.count < SurfaceSet.MaxSurfaces; i++) //1틱당 처리가능한 MaxSurfaces 까지만 계산.
        {
            RaycastHit2D h = _hits[i];

            //if (!Accept(h, dir, lastCollider)) 이미 앞에서 계산하는데 왜 있어야 하는지 사실 잘 모르겠음, 일단 주석처리
             //   continue; 괜찮은듯? 오류는 안뜸

            if (h.distance - min > Ballistics.EdgeEpsilon) //모서리가 아니라면 continue
                continue;

            if (h.collider == null || !h.collider.TryGetComponent(out Armor armor)) //Armor 컴포넌트가 아니라면 continue. 뭐 Armor 레이어에 다 Armor 넣긴 했지만 일단.
                continue;

            int k = _surfaces.count; //사실 for문 하나 더 돌리는거임

            _surfaces.normal[k] = h.normal; //현재 normal
            // 구경 mm -> m. 저항도 손상도 탄이 실제로 덮은 폭 전체에서 나와야 한다.
            _surfaces.rha[k] = armor.ChannelRha(
                h.point, dir, _surfaces.channel[k], out int sub, caliber * 0.001f);
            _surfaces.armor[k] = armor; //현재 armor 컴포넌트
            _surfaces.subIndex[k] = sub; //로그 보내고 데미지 넣는데 쓰는거
            _surfaces.count++;

            thicknessSum += armor.PlateThickness; //armor의 실 두께

            if (k == 0) //1번째 콜라이더를 주 콜라이더로 설정
                _surfaces.primaryCollider = h.collider;
        }

        if (_surfaces.count == 0)
            return false;

        _surfaces.minDistance = min;
        _surfaces.hitPoint = origin + dir * min;
        _surfaces.plateThickness = thicknessSum / _surfaces.count;

        Rigidbody2D body = _surfaces.primaryCollider.attachedRigidbody;
        _surfaces.targetVelocity = body != null ? body.linearVelocity : Vector2.zero;

        return true;
    }

    private static bool Accept(RaycastHit2D h, Vector2 dir, Collider2D lastCollider)
    {
        if (h.collider == lastCollider && h.distance < Ballistics.Epsilon * 2f)
            return false;

        return Vector2.Dot(-dir, h.normal) >= 0f; //90도 이하 각도로 들어왔을 때만 허락(90도 이상은 뒤에서 뚫고 들어오는 각도, 불가능함. 보통 관통 후 반대쪽으로 나가는 것을 막기 위해 있음)
    }
}
