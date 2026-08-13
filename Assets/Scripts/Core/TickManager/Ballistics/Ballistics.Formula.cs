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

    /// <summary>HP fraction -> RHA multiplier. 1.0 / 0.9 / 0.5 / 0.0 at 100% / 60% / 25% / 0%.</summary>
    public static float RhaCurve(float hpFraction)
    {
        float f = Mathf.Clamp01(hpFraction);

        if (f >= 0.6f)
            return Mathf.Lerp(0.9f, 1.0f, (f - 0.6f) / 0.4f);

        if (f >= 0.25f)
            return Mathf.Lerp(0.5f, 0.9f, (f - 0.25f) / 0.35f);

        return Mathf.Lerp(0f, 0.5f, f / 0.25f);
    }

    public static uint Hash(int projectileId, long tick, int hitIndex)
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

    public static Vector2 Rotate(Vector2 v, float degrees)
    {
        float r = degrees * Mathf.Deg2Rad;
        float c = Mathf.Cos(r);
        float s = Mathf.Sin(r);
        return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
    }
}
