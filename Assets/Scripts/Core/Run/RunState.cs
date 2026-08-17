using System.IO;
using UnityEngine;

/// <summary>
/// 런 하나가 들고 가는 함선의 상태. **전투가 끝날 때마다 여기 덮어쓴다.**
///
/// 손상된 배는 새 포맷이 아니다 - 배치가 적은 <see cref="ShipDef"/>다. 부서진 판은 배치에서
/// 그냥 빠지고, 그 빈 칸이 곧 뚫린 구멍이다. 살아남았지만 상한 판만 Placement.hp를 든다.
///
/// **StreamingAssets에 쓰지 않는다.** 거기 있는 것은 설계도다. 런의 손상을 거기 덮으면
/// destroyer.json이 반쯤 부서진 채로 남아 다음 런이 그 상태로 출항한다. 원본과 런 상태가
/// 갈라져 있어야 "다시 시작"이 존재할 수 있다.
///
/// 배 수치(drag, angleAccel...)는 저장하지 않는다. 런이 바꾸는 것은 **구조**뿐이고, 수치는
/// 설계도에 있다. 그래서 저장할 때 원본 파일의 원문을 가져와 placements만 갈아끼운다 -
/// ShipDef.Save가 export에 쓰는 것과 정확히 같은 비파괴 병합이다.
/// </summary>
public static class RunState
{
    private const string FileName = "run-ship.json";

    private static string FilePath =>
        Path.Combine(Application.persistentDataPath, FileName);

    /// <summary>저장된 런이 있는가.</summary>
    public static bool Exists => File.Exists(FilePath);

    /// <summary>
    /// 지금 이 배의 상태를 그대로 뜬다. <see cref="Battle.onEnd"/>에서 부른다.
    /// </summary>
    public static bool Save(Ship ship)
    {
        if (ship == null)
            return false;

        string origin = string.IsNullOrEmpty(ship.shipDefName) ? ship.name : ship.shipDefName;
        ShipDef damaged = ShipExporter.Export(ship.transform, origin);

        if (damaged == null)
            return false;

        damaged.basedOn = origin;

        string text = JsonUtility.ToJson(damaged, prettyPrint: true);

        // 설계도 원문 위에 배치만 얹는다. 이러면 배 수치가 글자 하나까지 그대로 따라온다.
        ShipDef design = ShipDef.Load(origin);

        if (design != null && !string.IsNullOrEmpty(design.raw))
        {
            string merged = Merge(design.raw, text);

            if (merged != null)
                text = merged;
        }

        File.WriteAllText(FilePath, text);

        Debug.Log($"[RunState] 판 {damaged.placements.Count}개 저장: {FilePath}");
        return true;
    }

    /// <summary>
    /// 저장된 상태. 없으면 null - 그때는 설계도 그대로 출항한다.
    ///
    /// <see cref="ShipDef.Load"/>를 안 쓰는 이유는 그것이 StreamingAssets만 본다는 것뿐이다.
    /// 파싱과 검증은 <see cref="ShipDef.Parse"/>로 같은 문을 탄다 - 이 파일은 사람 손이
    /// 닿는 자리에 있고(persistentDataPath), 검증을 빼면 오타 난 키가 조용히 기본값으로
    /// 묻혀서 "왜 배가 굼뜨지"가 된다. 그걸 막는 것이 def 검증의 존재 이유다.
    /// </summary>
    public static ShipDef Load()
    {
        if (!Exists)
            return null;

        ShipDef def = ShipDef.Parse(File.ReadAllText(FilePath), FileName);

        if (def == null)
            Debug.LogError($"[RunState] {FilePath}를 못 썼다. 설계도로 시작한다.");

        return def;
    }

    /// <summary>런이 끝났다. 다음은 다른 함장이고, 그 배는 설계도 그대로다.</summary>
    public static void Clear()
    {
        if (Exists)
            File.Delete(FilePath);

        RunLog.Clear();
    }

    /// <summary>설계도 원문의 placements를 손상된 쪽 것으로 갈아끼운다.</summary>
    private static string Merge(string designRaw, string damagedJson)
    {
        int start = damagedJson.IndexOf('[');
        int end = damagedJson.LastIndexOf(']');

        if (start < 0 || end <= start)
            return null;

        return DefKeys.ReplaceTopLevelValue(
            designRaw, "placements", damagedJson.Substring(start, end - start + 1));
    }
}
