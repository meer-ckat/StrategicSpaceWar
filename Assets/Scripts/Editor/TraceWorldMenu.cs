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
    /// 플레이 모드에서. 운석 100개를 세우고 결정론 레이 300개를 쏴서 시간·굽기 수·판정 파일을
    /// 낸다. 파일은 Temp/TraceBench.txt - 구현을 바꾸기 전후 두 파일을 diff하면 판정 보존이다.
    /// </summary>
    [MenuItem("Tools/Ballistics/Trace Bench (play mode)")]
    private static void Bench()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[TraceWorld] 벤치는 플레이 모드에서만 돈다.");
            return;
        }

        string path = System.IO.Path.Combine(Application.dataPath, "..", "Temp", "TraceBench.txt");
        Debug.Log("[TraceWorld] 벤치\n" + TraceWorld.Bench(path) + "판정 파일: " + path);
    }

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

#if UNITY_EDITOR
    [UnityEditor.MenuItem("Tools/Ballistics/콜라이더 churn 로그")]
    private static void ToggleChurnLog()
    {
        TraceWorld.ChurnLog = !TraceWorld.ChurnLog;
        Debug.Log($"[churn] 로그 {(TraceWorld.ChurnLog ? "켬 - Console을 봐라" : "끔")}");
    }
#endif
}
#endif
