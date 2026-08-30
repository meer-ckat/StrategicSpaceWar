using UnityEngine;
using UnityEngine.VFX;

/// <summary>
/// VFX Graph 한 방. 자리에 띄우고 다 타면 스스로 치운다.
///
/// **자산은 Resources에서 이름으로 찾는다.** <see cref="SoundManager"/>가 클립을 그렇게
/// 읽는 것과 같은 규칙이고, 이유도 같다 - 씬에 손으로 붙여 두면 런타임에 소환되는 것들이
/// 그 참조를 못 받는다. 이 리포에는 프리팹이 없으므로 인스펙터에 끌어다 놓을 자리 자체가
/// 없다.
///
/// def(JSON)로 못 옮기는 이유는 VFX Graph가 Unity 자산이라서다. 그림·소리와 달리 텍스트로
/// 표현할 수가 없어서, 이름으로 부르는 것이 여기서 가능한 최선이다.
/// </summary>
public static class VfxOneShot
{
    private const string Folder = "VFX/";

    /// <summary>
    /// 한 번 찾으면 들고 있는다. Resources.Load는 캐시가 있지만 문자열 조회가 남으므로,
    /// 배가 소환될 때마다 부르는 자리에서는 이쪽이 정직하다.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, VisualEffectAsset> _cache = new();

    /// <summary>
    /// <paramref name="name"/>을 <paramref name="at"/>에 띄운다. 그래프의 초기 이벤트가
    /// <c>OnPlay</c>라 인스턴스가 켜지는 것만으로 재생된다 - 이벤트 이름이나 프로퍼티를
    /// 코드가 맞출 것이 없다.
    ///
    /// 없는 이름이면 **조용히 넘어간다.** 연출은 시뮬레이션이 아니라, 없다고 게임이
    /// 멈추면 안 된다 - 대신 한 번은 크게 말한다.
    /// </summary>
    public static void Play(string name, Vector2 at, float lifeSeconds = 4f)
        => Spawn(name, at, lifeSeconds, 0f, 0f, withAttributes: false, 0f, withPose: false);

    /// <summary>
    /// 방향 있는 한 방(포구 연출). 회전은 transform에도 걸리지만, **World space 그래프는
    /// transform을 안 읽으므로** position/direction을 소스 어트리뷰트로도 같이 실어 보낸다 -
    /// 그래프가 Inherit Source Position/Direction만 켜면 어느 공간이든 제자리에 핀다.
    /// </summary>
    public static void Play(string name, Vector2 at, float lifeSeconds, float rotationDeg)
        => Spawn(name, at, lifeSeconds, 0f, 0f, withAttributes: false, rotationDeg, withPose: true);

    /// <summary>
    /// Duration/Power를 **이벤트 어트리뷰트**로 실어서 띄운다. Explosion.vfx가 이 둘을
    /// 소스 어트리뷰트로 읽는다 - 블랙보드 노출 프로퍼티가 아니라서 SetFloat로는 안 간다.
    /// 자동 OnPlay를 끄고(initialEventName 비움) 어트리뷰트를 실은 이벤트를 직접 쏜다 -
    /// 안 끄면 켜지는 순간 빈 어트리뷰트로 한 번 더 터진다.
    /// </summary>
    public static void Play(string name, Vector2 at, float lifeSeconds, float duration, float power)
        => Spawn(name, at, lifeSeconds, duration, power, withAttributes: true, 0f, withPose: true);

    private static void Spawn(
        string name, Vector2 at, float lifeSeconds, float duration, float power, bool withAttributes,
        float rotationDeg, bool withPose)
    {
        if (string.IsNullOrEmpty(name))
            return;

        if (!_cache.TryGetValue(name, out VisualEffectAsset asset))
        {
            asset = Resources.Load<VisualEffectAsset>(Folder + name);
            _cache[name] = asset;

            if (asset == null)
                Debug.LogWarning($"[VfxOneShot] Resources/{Folder}{name} 없음.");
        }

        if (asset == null)
            return;

        // 비활성으로 만들고 값을 다 넣은 뒤에 켠다 - ThingDef.Spawn과 같은 규칙.
        // 활성 상태에서 붙이면 initialEventName을 지우기 전에 자동 재생이 나간다.
        var go = new GameObject($"vfx {name}");
        go.SetActive(false);
        go.transform.position = new Vector3(at.x, at.y, 0f);
        go.transform.rotation = Quaternion.Euler(0f, 0f, rotationDeg);

        var vfx = go.AddComponent<VisualEffect>();
        vfx.visualEffectAsset = asset;

        bool sendEvent = withAttributes || withPose;

        if (sendEvent)
            vfx.initialEventName = string.Empty;

        go.SetActive(true);

        if (sendEvent)
        {
            VFXEventAttribute attr = vfx.CreateVFXEventAttribute();

            if (withAttributes)
            {
                attr.SetFloat("Duration", duration);
                attr.SetFloat("Power", power);
            }

            if (withPose)
            {
                // rotationDeg는 "그래프의 +x가 향할 월드 각"이라(Gun이 포신각 -90을
                // 넘긴다), 소스 direction도 같은 각으로 만든다 - transform과 어트리뷰트가
                // 다른 방향을 말하면 그래프 공간에 따라 빔이 뒤집힌다.
                float rad = rotationDeg * Mathf.Deg2Rad;

                attr.SetVector3("position", new Vector3(at.x, at.y, 0f));
                attr.SetVector3("direction", new Vector3(Mathf.Cos(rad), Mathf.Sin(rad), 0f));
            }

            vfx.SendEvent(VisualEffectAsset.PlayEventID, attr);
        }

        // 파티클이 다 살고 죽을 시간을 준 뒤 통째로 치운다. 그래프가 스스로 멈추는
        // 것과 오브젝트가 사라지는 것은 다른 일이라, 이게 없으면 다 탄 이펙트가
        // 씬에 계속 쌓인다.
        Object.Destroy(go, Mathf.Max(0.1f, lifeSeconds));
    }
}
