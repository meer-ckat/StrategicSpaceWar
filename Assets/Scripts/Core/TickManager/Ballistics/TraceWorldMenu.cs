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
}
#endif
