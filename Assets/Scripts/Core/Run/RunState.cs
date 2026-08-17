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
    private const string ProgressName = "run-progress.json";

    /// <summary>
    /// 배 말고 런이 들고 가는 것들. **배 파일과 따로 사는 이유는 <see cref="Save"/>가 설계도
    /// 원문에 placements만 갈아끼우는 비파괴 병합이기 때문이다** - 여기 있는 값을 거기 얹으면
    /// 그 병합이 진행도까지 건드린다.
    /// </summary>
    [System.Serializable]
    private class Progress
    {
        public int sector;

        /// <summary>
        /// 뜯어 온 판. **전투가 끝났을 때 적 선체에 남아 있던 판 수다.**
        ///
        /// 그래서 어떻게 잡았느냐가 곧 보상이다 - 관통으로 승무원만 죽이면 선체가 멀쩡히
        /// 남아 많이 뜯어오고, 충각으로 갈아버리면 가져올 것이 없다. 규칙을 따로 안 썼다.
        /// </summary>
        public int salvage;
    }

    private static string FilePath =>
        Path.Combine(Application.persistentDataPath, FileName);

    private static string ProgressPath =>
        Path.Combine(Application.persistentDataPath, ProgressName);

    /// <summary>
    /// 이어갈 런이 있는가. **두 파일이 다 있어야 한다.**
    ///
    /// 한쪽만 남는 것은 정상이 아니다 - 손으로 지웠거나, 두 번의 쓰기 사이에서 끊겼거나,
    /// 동기화가 반만 됐거나. 그때 배 파일만 읽으면 **상한 배로 1구역부터** 다시 시작하고,
    /// 진행도만 읽으면 멀쩡한 배로 6구역에서 시작한다. 둘 다 조용하다는 것이 문제다.
    ///
    /// 그래서 반쪽은 런이 아니라고 본다. 시끄럽게 지우고 처음부터 간다 - 틀린 런을 이어가는
    /// 것보다 낫고, 무엇보다 "어? 왜 배가 부서져 있지"를 디버깅할 일이 없어진다.
    /// </summary>
    public static bool Exists
    {
        get
        {
            bool ship = File.Exists(FilePath);
            bool progress = File.Exists(ProgressPath);

            if (ship == progress)
                return ship;

            Debug.LogWarning(
                $"[RunState] 런 파일이 반쪽이다 (배 {ship}, 진행도 {progress}). " +
                "이어갈 수 없으니 지우고 처음부터 간다.");

            Clear();
            return false;
        }
    }

    /// <summary>
    /// 지금 몇 구역인가. 0부터.
    ///
    /// **배 파일에 안 넣는다.** <see cref="Save"/>는 설계도 원문에 placements만 갈아끼우는
    /// 비파괴 병합이고, 그래야 손으로 튜닝한 배 수치가 글자 하나까지 살아남는다. 진행도를
    /// 거기 얹으면 그 병합이 진행도까지 건드리게 된다. 배는 병합이 필요하고 진행도는 그냥
    /// 숫자라, 파일을 나누는 것이 둘 다 단순해지는 길이다.
    /// </summary>
    public static int Sector
    {
        get => Read().sector;

        set
        {
            Progress p = Read();
            p.sector = Mathf.Max(0, value);
            Write(p);
        }
    }

    /// <summary>쓸 수 있는 노획. 판 한 장어치가 1이다.</summary>
    public static int Salvage
    {
        get => Read().salvage;

        set
        {
            Progress p = Read();
            p.salvage = Mathf.Max(0, value);
            Write(p);
        }
    }

    /// <summary>
    /// 진행도를 읽는다. **<see cref="Exists"/>를 탄다** - 배 파일과 짝이 안 맞으면 여기서도
    /// 처음 상태여야 하고, 그 판정을 두 벌로 두면 언젠가 한쪽만 고친다.
    /// </summary>
    private static Progress Read()
    {
        if (!Exists)
            return new Progress();

        return JsonUtility.FromJson<Progress>(File.ReadAllText(ProgressPath)) ?? new Progress();
    }

    private static void Write(Progress p) =>
        File.WriteAllText(ProgressPath, JsonUtility.ToJson(p, prettyPrint: true));

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

    /// <summary>
    /// 런이 끝났다. 다음은 다른 함장이고, 그 배는 설계도 그대로다.
    ///
    /// 진행도도 같이 지운다. **런 파일의 주인이 하나여야** 배는 새것인데 구역은 6인
    /// 상태가 존재할 자리가 없다.
    /// </summary>
    public static void Clear()
    {
        // **Exists를 쓰면 안 된다.** 반쪽일 때 Exists가 Clear를 부르므로 서로를 부르며
        // 스택을 넘긴다. 지우는 쪽은 파일을 곧이곧대로 본다.
        if (File.Exists(FilePath))
            File.Delete(FilePath);

        if (File.Exists(ProgressPath))
            File.Delete(ProgressPath);

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

    // 여기부터 아래는 에디터 전용이다. **닫는 중괄호는 이 블록 밖에 있어야 한다** -
    // 안에 두면 빌드에서 클래스가 안 닫혀 CS1513이 나는데, 에디터에서는 UNITY_EDITOR가
    // 항상 정의돼 있어서 영영 안 보인다. ShipExporter가 실제로 그렇게 깨져 있었다.
#if UNITY_EDITOR

    /// <summary>
    /// 저장된 런을 지운다. 다음 실행은 설계도 그대로, 1구역, 노획 0에서 시작한다.
    ///
    /// 파일이 persistentDataPath에 있고 그 경로가 AppData\LocalLow 밑이라 탐색기 기본
    /// 설정에서 숨겨져 있다. 손으로 지우기 어려운 자리에 있는 것이 이 메뉴의 이유다.
    /// </summary>
    [UnityEditor.MenuItem("Tools/Run/Reset Run")]
    public static void ResetRun()
    {
        bool had = File.Exists(FilePath) || File.Exists(ProgressPath);

        Clear();

        Debug.Log(had
            ? $"[RunState] 런을 지웠다. 다음 실행은 1구역부터: {Application.persistentDataPath}"
            : "[RunState] 지울 런이 없다. 이미 처음 상태다.");
    }

    /// <summary>저장 폴더를 연다. 숨은 폴더라 주소를 알아도 찾아 들어가기 번거롭다.</summary>
    [UnityEditor.MenuItem("Tools/Run/Open Save Folder")]
    public static void OpenSaveFolder()
    {
        Directory.CreateDirectory(Application.persistentDataPath);
        UnityEditor.EditorUtility.RevealInFinder(Application.persistentDataPath);
    }

    /// <summary>지금 저장된 런이 무엇인지 한 줄로. 무엇을 지우는지 보고 나서 지우라고.</summary>
    [UnityEditor.MenuItem("Tools/Run/Log Run State")]
    public static void LogRunState()
    {
        if (!Exists)
        {
            Debug.Log("[RunState] 저장된 런이 없다.");
            return;
        }

        ShipDef ship = Load();

        Debug.Log(
            $"[RunState] {Sector + 1}구역, 노획 {Salvage}장, " +
            $"배 '{ship?.basedOn ?? "?"}' 판 {ship?.placements.Count ?? 0}장. " +
            Application.persistentDataPath);
    }

#endif
}
