#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// TraceWorld 대조 모드 스위치. 셀프테스트가 따로 없는 이유: 대조 모드 자체가
/// 테스트다 - 실전의 모든 파편 레이가 Physics2D와 자체 판정을 동시에 받아서,
/// 수식이 틀리면 플레이 몇 초 안에 불일치 로그가 뜬다. 합성 픽스처보다 넓다.
/// </summary>
public static class TraceWorldMenu
{
    [MenuItem("Tools/Ballistics/Toggle Trace Verify")]
    private static void Toggle()
    {
        TraceWorld.VerifyMode = !TraceWorld.VerifyMode;
        TraceWorld.ResetVerify();
        Debug.Log($"[TraceWorld] 대조 모드 {(TraceWorld.VerifyMode ? "켬" : "끔")}");
    }

    [MenuItem("Tools/Ballistics/Trace Verify Report")]
    private static void Report() => Debug.Log(TraceWorld.VerifyReport());

    /// <summary>
    /// 충각 로그. static 필드라 인스펙터에서 못 켜서 여기 문을 낸다 - 켜면 부딪힐 때마다
    /// 속도·각속도·반경·접촉 판 수·예산·소진을 한 줄로 찍는다. 그 한 줄을 같이 봐야
    /// "왜 이게 뚫리나"가 갈린다.
    /// </summary>
    [MenuItem("Tools/Ballistics/Toggle Ram Log")]
    private static void ToggleRam()
    {
        RamImpact.RamLog = !RamImpact.RamLog;
        Debug.Log($"[RamImpact] 충각 로그 {(RamImpact.RamLog ? "켬" : "끔")}");
    }
}
#endif
