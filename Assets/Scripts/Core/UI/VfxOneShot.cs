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
    /// <param name="rotationDeg">
    /// 오브젝트를 이만큼 돌려서 띄운다. **자리와 방향은 transform이 전부다** - 시스템이
    /// World space여도 Set Position 같은 블록의 슬롯이 Local이면 스폰 순간 이 행렬로
    /// 변환되므로, 코드가 좌표를 어트리뷰트로 따로 실어 보낼 이유가 없다.
    /// </param>
    public static void Play(string name, Vector2 at, float lifeSeconds = 4f, float rotationDeg = 0f)
        => Spawn(name, at, lifeSeconds, 0f, 0f, withAttributes: false, rotationDeg);

    /// <summary>
    /// Duration/Power를 **이벤트 어트리뷰트**로 실어서 띄운다. Explosion.vfx가 이 둘을
    /// 소스 어트리뷰트로 읽는다 - 블랙보드 노출 프로퍼티가 아니라서 SetFloat로는 안 간다.
    /// 자동 OnPlay를 끄고(initialEventName 비움) 어트리뷰트를 실은 이벤트를 직접 쏜다 -
    /// 안 끄면 켜지는 순간 빈 어트리뷰트로 한 번 더 터진다.
    /// </summary>
    public static void Play(string name, Vector2 at, float lifeSeconds, float duration, float power)
        => Spawn(name, at, lifeSeconds, duration, power, withAttributes: true, 0f);

    private static void Spawn(
        string name, Vector2 at, float lifeSeconds, float duration, float power, bool withAttributes,
        float rotationDeg)
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

        if (withAttributes)
            vfx.initialEventName = string.Empty;

        // **정리 예약을 켜기 전에 건다.** 예전에는 맨 아래에 있었는데, 그러면 아래
        // SetActive/SendEvent가 던지는 순간 예약이 안 걸려서 이 오브젝트가 비활성인 채
        // 씬에 영원히 남는다 - 그림도 안 나오고 아무도 안 지운다. Destroy의 타이머는
        // 비활성 오브젝트에도 그대로 도니까 순서를 앞으로 옮기는 것으로 끝난다.
        Object.Destroy(go, Mathf.Max(0.1f, lifeSeconds));

        go.SetActive(true);

        if (withAttributes)
        {
            VFXEventAttribute attr = vfx.CreateVFXEventAttribute();

            attr.SetFloat("Duration", duration);
            attr.SetFloat("Power", power);

            vfx.SendEvent(VisualEffectAsset.PlayEventID, attr);
        }
    }
}
