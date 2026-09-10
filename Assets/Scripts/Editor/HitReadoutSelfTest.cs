#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

/// <summary>
/// Tools > Run > Run HitReadout Tests. 씬도 플레이 모드도 필요 없다 - HitReadout.Push가
/// 링 하나만 만지기 때문이다. 병합·밀기·상한 셋이 이 파일에서 틀릴 수 있는 전부다.
/// </summary>
public static class HitReadoutSelfTest
{
    private static int _pass;
    private static int _fail;

    [MenuItem("Tools/Run/Run HitReadout Tests")]
    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        // 정적 링이라 이전 실행이 남는다. 상한만큼 밀어 넣어 알려진 상태로 만든 뒤 센다.
        for (int i = 0; i < HitReadout.Capacity; i++)
            HitReadout.Push("RESET " + i, false, true);

        int before = HitReadout.Count;

        Check(before == HitReadout.Capacity,
            $"상한만큼 넣으면 Count == Capacity (got {before})");

        // 1) 같은 판정이 이어지면 줄이 안 늘고 센다.
        HitReadout.Push("NO PENETRATION", true, true);
        HitReadout.Push("NO PENETRATION", true, true);
        HitReadout.Push("NO PENETRATION", true, true);

        Check(HitReadout.Get(0).count == 3,
            $"같은 판정 3연발은 한 줄에 x3 (got {HitReadout.Get(0).count})");

        Check(HitReadout.Count == HitReadout.Capacity,
            "병합은 줄 수를 안 늘린다");

        // 2) 방향이 다르면 같은 문구라도 다른 줄이다 - 내가 튕겨낸 것과 내 탄이 튕긴 것은
        //    같은 사건이 아니다.
        HitReadout.Push("NO PENETRATION", false, true);

        Check(HitReadout.Get(0).count == 1 && !HitReadout.Get(0).incoming,
            "방향이 다르면 병합하지 않는다");

        Check(HitReadout.Get(1).count == 3 && HitReadout.Get(1).incoming,
            "밀려난 줄이 내용 그대로 1번 자리로 간다");

        // 3) 상한을 넘겨도 안 자란다.
        for (int i = 0; i < HitReadout.Capacity * 2; i++)
            HitReadout.Push("PENETRATION " + i, false, false);

        Check(HitReadout.Count == HitReadout.Capacity,
            $"상한을 넘겨도 Count는 Capacity (got {HitReadout.Count})");

        Check(HitReadout.Get(0).text == "PENETRATION " + (HitReadout.Capacity * 2 - 1),
            "0번이 제일 최근");

        Debug.Log($"[HitReadout] {_pass} passed, {_fail} failed");
    }

    private static void Check(bool ok, string what)
    {
        if (ok)
        {
            _pass++;
            return;
        }

        _fail++;
        Debug.LogError($"[HitReadout] FAIL: {what}");
    }
}
#endif
