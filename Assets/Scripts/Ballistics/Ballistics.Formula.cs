using UnityEngine;

/// <summary>
/// The closed-form maths. No state, no Unity objects, no side effects - every function
/// here is inputs to output, which is what makes PenetrationSelfTest possible.
/// </summary>
public static partial class Ballistics
{
    public static float Penetration( //총알의 현재 관통력을 계산하는 함수
        float penetrationK, // 실험을 통해 밝혀진 총알의 관통 상수, 이걸 정확히 아는건 어려우니 대충 밸런스 맞게.
        float integrityFactor, //현재 총알의 변형도 / 내구도
        float speed, //속도
        float mass, //무게
        float caliber) //구경
    {
        if (speed <= 0f || mass <= 0f || caliber <= 0f) //오류 fallback
            return 0f;

        return penetrationK * integrityFactor
            * Mathf.Pow(speed, 1.43f)
            * Mathf.Pow(mass, 0.71f)
            / Mathf.Pow(caliber, 1.07f);
    }
    /// <summary>
    /// 장갑의 잔여 체력에 따른 RHA multiplier 계산, 즉 장갑이 걸레짝이 될수록 유효방호력도 낮아짐.
    /// </summary>
    /// <param name="hpFraction"></param>
    /// <returns></returns>
    public static float RhaCurve(float hpFraction) 
    {
        float f = Mathf.Clamp01(hpFraction);

        if (f >= 0.6f)
            return Mathf.Lerp(0.9f, 1.0f, (f - 0.6f) / 0.4f);

        if (f >= 0.25f)
            return Mathf.Lerp(0.5f, 0.9f, (f - 0.25f) / 0.35f);

        return Mathf.Lerp(0f, 0.5f, f / 0.25f);
    }

    /// <summary>
    /// 탄이 잃은 velocity로 잃은 운동량, 즉 다시 말해 충격량을 계산
    /// </summary>
    public static Vector2 ImpactImpulse(
        float mass,
        Vector2 incomingVelocity,
        Vector2 outgoingVelocity)
    {
        if (mass <= 0f)
            return Vector2.zero;

        return mass * (incomingVelocity - outgoingVelocity);
    }

    public static uint Hash(int projectileId, long tick, int hitIndex) //발사체 정보를 randomValue로 바꿈. 그런데 결정론적이라서 projectile id, tick, hitIndex 모두 같으면 똑같은 random이 나옴.
    {
        unchecked
        {
            uint h = 2166136261u;
            h = (h ^ (uint)projectileId) * 16777619u;
            h = (h ^ (uint)tick) * 16777619u;
            h = (h ^ (uint)(tick >> 32)) * 16777619u;
            h = (h ^ (uint)hitIndex) * 16777619u;
            return h;
        }
    }

    /// <summary>
    /// |d + v·t| = s·t 를 푼다. 최소 양수 근이 요격 시각. 못 따라잡으면 false.
    ///
    /// **d와 v는 상대 프레임이다** - 탄이 쏜 배의 속도를 물려받으므로(Projectile.Launch)
    /// 조준은 상대 프레임에서 풀린다. 표적의 절대 미래 위치를 겨누면 내 배 속도만큼
    /// 어긋난다: 탄속 1100에 배속 40이면 2도이고, M12의 fireArc가 0.7도다.
    ///
    /// 여기 있는 이유는 **조준하는 쪽과 조준을 그리는 쪽이 같은 답을 봐야 해서다** -
    /// ShipStatusHud의 리드 마커가 "여기 두면 맞는다"고 약속하는데 AI 포탑이 다른
    /// 식으로 겨누면 그 약속이 플레이어에게만 참이다. Gun.MuzzleShot과 같은 규칙.
    /// </summary>
    public static bool InterceptTime(Vector2 d, Vector2 v, float speed, out float t)
    {
        float a = v.sqrMagnitude - speed * speed;
        float b = 2f * Vector2.Dot(d, v);
        float c = d.sqrMagnitude;

        t = -1f;

        // 탄속과 상대속도가 같은 퇴화: 선형식 bt + c = 0.
        if (Mathf.Abs(a) < 1e-4f)
        {
            if (b >= -1e-6f)
                return false;

            t = -c / b;
            return t > 0f;
        }

        float disc = b * b - 4f * a * c;

        if (disc < 0f)
            return false;

        float root = Mathf.Sqrt(disc);

        float t0 = (-b - root) / (2f * a);
        float t1 = (-b + root) / (2f * a);

        if (t0 > t1)
            (t0, t1) = (t1, t0);

        t = t0 > 0f ? t0 : t1;
        return t > 0f;
    }

    public static Vector2 Rotate(Vector2 v, float degrees) //Vector를 회전시키는 헬퍼 함수
    {
        float r = degrees * Mathf.Deg2Rad;
        float c = Mathf.Cos(r);
        float s = Mathf.Sin(r);
        return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
    }
}
