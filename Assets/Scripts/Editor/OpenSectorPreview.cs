using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 생성기가 무엇을 뽑는지 게임을 켜지 않고 본다. **이 단계에서 확인할 것은 하나다** -
/// 잔해·보급 노드는 적이 없는데, 비아군이 하나도 없으면 Campaign.Stage가 도착점을 못 잡아
/// 원점 앞에 서고 노드가 도착 즉시 끝난다. Hulk를 한 척이라도 세워두면 그 창이 닫힌다.
/// </summary>
public static class OpenSectorPreview
{
    [MenuItem("Tools/Run/Preview Open-Sector Map")]
    public static void Preview()
    {
        var text = new StringBuilder();

        // 저장이 없을 때 RunState.Seed를 읽으면 진행도 파일이 생긴다. 미리보기가 세이브를 낳으면 안 된다.
        if (!RunState.HasProgress)
        {
            text.AppendLine("[OpenSector] 진행 중인 런이 없다. 실제 시드는 첫 갈림길에서 정해진다 - 아래는 예시가 아니라 빈 화면이다.");
            Debug.Log(text.ToString());
            return;
        }

        text.AppendLine($"[OpenSector] 시드 {RunState.Seed}");

        for (int chapter = 0; chapter < 8; chapter++)
        {
            text.AppendLine($"── 장 {chapter + 1} 이후 ──");

            for (int leg = 0; leg < OpenSectorGen.LegsPerChapter; leg++)
            {
                for (int lane = 0; lane < OpenSectorGen.Lanes; lane++)
                {
                    SectorDef made = OpenSectorGen.Make(chapter, leg, lane);

                    if (made == null)
                    {
                        text.AppendLine($"  L{leg} 레인{lane}: 생성 실패");
                        continue;
                    }

                    var ships = new StringBuilder();
                    int hulks = 0;

                    foreach (SpawnDef s in made.spawns)
                    {
                        ships.Append(s.ship).Append(' ');

                        if (s.hulk)
                            hulks++;
                    }

                    text.AppendLine(
                        $"  L{leg} 레인{lane}: {made.name,-14} {made.spawns.Count}척"
                        + $" hulk{hulks} MTRL{made.credits,-3}"
                        + $" 정비{(made.refit ? "O" : "X")} │ {ships}");
                }
            }
        }

        Debug.Log(text.ToString());
    }
}
