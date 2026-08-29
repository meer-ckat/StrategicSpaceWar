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

    public static Vector2 Rotate(Vector2 v, float degrees) //Vector를 회전시키는 헬퍼 함수
    {
        float r = degrees * Mathf.Deg2Rad;
        float c = Mathf.Cos(r);
        float s = Mathf.Sin(r);
        return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
    }
}
