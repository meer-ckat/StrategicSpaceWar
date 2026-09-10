#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

/// <summary>
/// Tools > Run > Run Warp Tests.
/// 워프 연출의 순수 함수만 본다 - 줄기 거리, 슬라이드 합, 편대 자리, 카드 줄. 플레이 모드가
/// 필요한 것(scale 복원, 부스터 끄기, 감시)은 MCP로 손으로 본다.
/// </summary>
public static class WarpSelfTest
{
    private static int _pass;
    private static int _fail;

    [MenuItem("Tools/Run/Run Warp Tests")]
    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        // 줄기: 0에서 시작, 단조, 순간 속도가 최고 속도를 안 넘고, 정해진 시간에 정확히 이탈 거리.
        {
            const float step = 0.01f;
            bool monotone = true, capped = true;
            float prev = 0f;

            for (float t = step; t <= WarpTransition.StreakSeconds + 0.5f; t += step)
            {
                float d = WarpTransition.StreakDistance(t);
                if (d < prev - 1e-4f) monotone = false;
                if (d - prev > WarpTransition.StreakSpeed * step + 1e-2f) capped = false;
                prev = d;
            }

            float ramp = WarpTransition.RampSeconds;
            float peak = (WarpTransition.StreakDistance(ramp) - WarpTransition.StreakDistance(ramp - 0.001f)) / 0.001f;

            Check("streak: t=0이면 0", WarpTransition.StreakDistance(0f) == 0f);
            Check("streak: 단조 증가", monotone);
            Check("streak: 순간 속도 ≤ 최고 속도", capped);
            Check("streak: 램프 끝 속도가 최고 속도", Mathf.Abs(peak - WarpTransition.StreakSpeed) < WarpTransition.StreakSpeed * 0.05f);
            Check("streak: StreakSeconds에 이탈 거리",
                Mathf.Abs(WarpTransition.StreakDistance(WarpTransition.StreakSeconds) - WarpTransition.ExitDistance) < 0.01f);
            Check("streak: 이탈 거리에서 멈춘다",
                WarpTransition.StreakDistance(WarpTransition.StreakSeconds + 1f) == WarpTransition.ExitDistance);
        }

        // 슬라이드: 스테이징이 믿는 거리 == Slide가 매 틱 주는 속도의 합. 회귀 - 적분으로 세우면 5 % 지나쳤다.
        {
            const float speed = 800f;
            const int ticks = 30;
            float sum = 0f;

            for (int k = 0; k < ticks; k++)
                sum += Campaign.SlideSpeed(speed, k, ticks) * Core.TickManager.TickDeltaTime;

            Check("slide: 합 == 스테이징 거리", Mathf.Abs(sum - Campaign.SlideDistanceOf(speed, ticks)) < 1e-3f);
            Check("slide: 첫 틱은 최고 속도", Campaign.SlideSpeed(speed, 0, ticks) == speed);
            Check("slide: 마지막 틱은 0보다 크고 첫 틱보다 작다",
                Campaign.SlideSpeed(speed, ticks - 1, ticks) > 0f && Campaign.SlideSpeed(speed, ticks - 1, ticks) < speed);
            Check("slide: 적분(v·T/3)보다 길다", Campaign.SlideDistanceOf(speed, ticks) > speed * ticks * Core.TickManager.TickDeltaTime / 3f);
        }

        // 편대: 첫 둘은 좌우 반대, 번호가 클수록 뒤. 좌우 순서(pingpong)는 오너 식이라 그대로 둔다.
        {
            var box = new RectInt(0, 0, 10, 5);
            Vector2 a = Campaign.FormationOffset(1, box);
            Vector2 b = Campaign.FormationOffset(2, box);
            Vector2 c = Campaign.FormationOffset(3, box);

            Check("formation: 1·2번이 좌우 반대", Mathf.Sign(a.y) != Mathf.Sign(b.y));
            Check("formation: 번호가 클수록 뒤", a.x > b.x && b.x > c.x && a.x < 0f);
            Check("formation: 옆으로 비켜 선다", a.y != 0f && b.y != 0f && c.y != 0f);
        }

        // 카드: 이름과 배지가 그대로 들어간다. 종류가 비면 본구역.
        {
            var s = new SectorDef { name = "감시소 19", kind = "" };
            s.spawns.Add(new SpawnDef { ship = "dart", team = "Enemy" });
            string[] lines = LogisticsScreen.CardLines(s, "다음 섹터");

            Check("card: 네 줄", lines.Length == 4);
            Check("card: 눈썹", lines[0] == "다음 섹터");
            Check("card: 이름", lines[1] == "감시소 19");
            Check("card: 종류가 비면 본구역", lines[2] == "본구역");
            Check("card: 배지에 HOSTILE", lines[3].Contains("HOSTILE"));

            s.kind = "Skirmish";
            Check("card: 종류는 대문자", LogisticsScreen.CardLines(s, "")[2] == "SKIRMISH");
        }

        Debug.Log($"[WarpSelfTest] {_pass} pass, {_fail} fail");
    }

    private static void Check(string name, bool ok)
    {
        if (ok)
            _pass++;
        else
        {
            _fail++;
            Debug.LogError($"[WarpSelfTest] FAIL {name}");
        }
    }
}
#endif
