using System;
using System.Collections.Generic;
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
        /// 이 런의 항로 시드. **맵을 저장하지 않는 이유가 이 값이다** - 생성이 결정론
        /// (DeterministicRng)이라 시드와 아래 <see cref="lanes"/>만 있으면 같은 맵이
        /// 그대로 다시 나온다. 노드 목록을 직렬화하면 생성기를 고치는 날 옛 저장과
        /// 새 생성기가 서로 다른 맵을 말하게 된다.
        ///
        /// 0은 "아직 안 정했다"다. 런이 시작될 때 한 번 찍고 그 뒤로 안 바뀐다.
        /// </summary>
        public int seed;

        /// <summary>이 런을 만든 생성기 버전. 0은 버전이 없던 옛 저장이다.</summary>
        public int genVersion;

        /// <summary>
        /// 이번 장 안에서 몇 번째 소구역인가. <see cref="sector"/>는 **장 번호 그대로**다 -
        /// 프롤로그 게이트(ScriptManager)와 sector-entered-{번호} 대본이 그 의미에
        /// 매달려 있어서, 소구역을 그 숫자에 섞으면 3구역 대사가 세 번 나온다.
        ///
        /// 기존 저장은 이 값이 0이라 그대로 열린다.
        /// </summary>
        public int leg;

        /// <summary>
        /// 갈림길마다 고른 레인. 순서가 곧 지나온 길이다 - 시드와 이것 둘이 맵과
        /// 현재 위치를 전부 말한다.
        /// </summary>
        public List<int> lanes = new();

        // 갈림길을 내놓았는데 아직 안 골랐다. leg + 1을 저장해 0이 "없음"이다 - 이 필드가 없던
        // 옛 저장이 기본값 0으로 정확히 그 상태로 열린다(Leg와 같은 규칙). 이게 없으면 항로
        // 화면에서 끈 저장이 깬 구역을 다시 싸우게 하고 노획을 두 번 준다.
        public int pendingLeg;

        /// <summary>지난 들판에서 얼마나 시끄러웠나. 다음 들판의 추격이 그만큼 일찍 온다 - 은폐의 유일한 장기 보상이다.</summary>
        public int heat;

        // 정비 노드에 서 있고 아직 출항 안 했다. 재개하면 갈림길보다 정비 화면이 먼저다 -
        // 자재는 도착 즉시 더해졌는데 수리할 자리가 없으면 그 자재가 그냥 벌점이 된다.
        public bool pendingRefit;

        /// <summary>
        /// 세 자원. **`salvage` 단일 값을 여기서 끝낸다** - 판 수 하나로 수리·재보급·이동을
        /// 전부 사려 하면 세 가지 서로 다른 결정("고칠까/재장전할까/떠날까")이 값 하나를
        /// 두고 경쟁하게 된다. 회수 방식은 <see cref="Campaign"/>의 SalvageResult 계산이
        /// 정한다 - 여기는 그냥 지갑이다.
        /// </summary>
        public int credits;


        /// <summary>전략 이동에 쓰는 추진제. 안 터진 탱크의 remaining 합에서 온다.</summary>
        public int propellant;

        /// <summary>재장전에 쓰는 탄약. 안 터진 탄약고에서 온다.</summary>
        public int munitions;

        /// <summary>
        /// 런의 기억. **문자열 집합 하나뿐이다** - 이벤트 체인이 실제로 생기기 전까지는
        /// 이 이상의 구조(발생 시각, 만료, 카운터)를 미리 짓지 않는다. 값이 필요해지는
        /// 순간의 이벤트 하나가 그 모양을 정하는 게 낫다.
        /// </summary>
        public List<string> flags = new();

        /// <summary>
        /// 합류한 아군의 설계도 이름. **런 전체를 따라다닌다** - 구역마다 이 목록대로
        /// 다시 소환된다.
        ///
        /// **손상은 안 들고 간다.** 다음 구역에 멀쩡한 몸으로 다시 나온다 - 손상을 안고
        /// 가는 것은 플레이어 한 척뿐이고, 그게 이 게임에서 배 한 척이 특별한 유일한
        /// 자리다(CLAUDE.md). 대신 **죽으면 목록에서 빠진다.** 그래서 잃는 것은 한 구역의
        /// 체력이 아니라 남은 런 전체의 동료다.
        /// </summary>
        public List<string> wingmen = new();

        /// <summary>
        /// 편대 자리. <see cref="wingmen"/>과 **같은 순서, 같은 길이**다 - 둘을 한 클래스로
        /// 묶지 않은 이유는 JsonUtility가 중첩 리스트를 못 읽어서고, 그래서 길이가
        /// 어긋나면 <see cref="Wingmen"/>이 짧은 쪽에 맞춰 자른다.
        /// </summary>
        public List<Vector2> wingmenSlots = new();

        /// <summary>
        /// 이 노드에서 알아낸 신호. (x, y, 단계) - 단계는 ContactView.Reveal 정수. 노드가 바뀌면 지운다.
        /// 없으면 재개할 때 15 km에서 잡아 둔 기항지 좌표가 증발한다 - 출구가 흐려진 뒤로는 출구 위치까지 잊는다.
        /// </summary>
        public List<Vector3> reveals = new();
    }

    /// <summary>합류한 아군 한 척. 이름과 편대 자리.</summary>
    public readonly struct Wingman
    {
        public readonly string ship;
        public readonly Vector2 slot;

        public Wingman(string ship, Vector2 slot)
        {
            this.ship = ship;
            this.slot = slot;
        }
    }

    internal static string FilePath =>
        Path.Combine(Application.persistentDataPath, FileName);

    internal static string ProgressPath =>
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
    /// 이어갈 런이 있는가. 아무것도 안 바꾼다.
    /// 이어갈 런이 있는가. **아무것도 안 바꾼다.**
    public static bool Exists => File.Exists(FilePath) && File.Exists(ProgressPath);

    /// 반쪽이면 지운다. 런이 시작될 때 딱 한 번 부른다 - 읽을 때마다 부르면
    /// Save 직후의 정상 반쪽까지 사고로 보고 지운다.
    public static void ValidateOrClear()
    {
        bool ship = File.Exists(FilePath);
        bool progress = File.Exists(ProgressPath);

        if (ship != progress)
        {
            Debug.LogWarning($"[RunState] 런 파일이 반쪽이다 (배 {ship}, 진행도 {progress}). 지우고 처음부터 간다.");
            Clear();
            return;
        }

        if (!progress)
            return;

        // 생성기가 바뀌었으면 시드가 같아도 다른 우주다. 이어가면 저장한 좌표와 새 맵이
        // 어긋난 채로 조용히 굴러간다 - 버리는 쪽이 낫다. OpenSectorGen.Version 참고.
        int saved = Read().genVersion;

        if (saved != OpenSectorGen.Version)
        {
            Debug.LogWarning(
                $"[RunState] 저장된 런이 옛 생성기(v{saved})다. 지금은 v{OpenSectorGen.Version} - 지우고 처음부터 간다.");
            Clear();
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

    /// <summary>
    /// 항로 시드. 처음 읽을 때 없으면 그 자리에서 찍고 저장한다.
    ///
    /// **여기서 시계를 쓰는 것은 결정론 규칙과 안 부딪힌다.** 금지된 것은 시뮬레이션이
    /// UnityEngine.Random을 읽는 것이고, 이 값은 런당 한 번 정해진 뒤 저장되어 그
    /// 다음부터는 전부 DeterministicRng를 먹인다 - 재개해도 같은 맵이다.
    /// </summary>
    public static int Seed
    {
        get
        {
            Progress p = Read();

            if (p.seed != 0)
                return p.seed;

            // 0은 "안 정했다"의 표식이라 결과가 0이면 다시 뽑는다. 안 그러면 그 런은
            // 매번 새 시드를 찍는다 - Cell.Unset이 0이어야 하는 것과 같은 함정이다.
            int fresh = System.DateTime.Now.Ticks.GetHashCode();

            p.seed = fresh != 0 ? fresh : 1;

            // 시드와 같은 자리에서 찍는다 - 둘 다 "이 런의 맵이 무엇인가"이고, 갈라 두면
            // 버전만 없는 저장이 생겨 검사가 통과한다.
            p.genVersion = OpenSectorGen.Version;
            Write(p);

            return p.seed;
        }
    }

    /// <summary>이번 장 안에서 몇 번째 소구역인가. 장이 넘어갈 때 0으로 돌아간다.</summary>
    public static int Leg
    {
        get => Read().leg;

        set
        {
            Progress p = Read();
            p.leg = Mathf.Max(0, value);
            Write(p);
        }
    }

    public static List<int> Lanes => Read().lanes ?? new List<int>();

    public static int PendingLeg => Read().pendingLeg - 1;

    /// <summary>추적 열기 0~<see cref="MaxHeat"/>. 구역을 넘어 남는 유일한 전술 상태다.</summary>
    public const int MaxHeat = 5;

    public static int Heat
    {
        get => Mathf.Clamp(Read().heat, 0, MaxHeat);

        set
        {
            Progress p = Read();
            p.heat = Mathf.Clamp(value, 0, MaxHeat);
            Write(p);
        }
    }

    // 진행도 파일이 있는가. Seed getter는 없으면 파일을 만들므로, 읽기만 하려는 쪽은 이걸 먼저 본다.
    public static bool HasProgress => File.Exists(ProgressPath);
    public static bool PendingRefit => Read().pendingRefit;

    public static void SetPendingFork(int leg, bool refit)
    {
        Progress p = Read();
        p.pendingLeg = leg + 1;
        p.pendingRefit = refit;
        Write(p);
    }

    // 레인 확정. leg·lane·pending 해제를 한 번에 쓴다 - 두 번에 나눠 쓰면 그 사이에 끊긴
    // 저장이 새 leg에 옛 lane을 붙여 엉뚱한 노드를 복원한다.
    public static void CommitLane(int leg, int lane)
    {
        Progress p = Read();
        p.leg = Mathf.Max(0, leg);
        p.lanes ??= new List<int>();
        p.lanes.Add(lane);
        p.pendingLeg = 0;
        p.pendingRefit = false;
        p.reveals = new List<Vector3>();   // 새 노드 = 새 원장
        Write(p);
    }

    /// <summary>이 노드에서 알아낸 신호 전부. ContactView가 구역에 들어설 때 한 번 읽는다.</summary>
    public static List<Vector3> Reveals => Read().reveals ?? new List<Vector3>();

    /// <summary>신호 하나의 단계가 올랐다. 같은 자리는 갈아끼운다. 단계는 올라가기만 하니 호출도 드물다.</summary>
    public static void RememberReveal(Vector2 at, int state)
    {
        Progress p = Read();
        p.reveals ??= new List<Vector3>();

        for (int i = 0; i < p.reveals.Count; i++)
        {
            if ((Vector2)p.reveals[i] == at)
            {
                p.reveals[i] = new Vector3(at.x, at.y, state);
                Write(p);
                return;
            }
        }

        p.reveals.Add(new Vector3(at.x, at.y, state));
        Write(p);
    }

    // refit: 장의 마지막 소구역이 정비 노드였다. 갈림길이 없어 SetPendingFork를 안 타므로 여기서 든다.
    public static void CommitChapter(int sector, bool refit = false)
    {
        Progress p = Read();
        p.sector = Mathf.Max(0, sector);
        p.leg = 0;
        p.pendingLeg = 0;
        p.pendingRefit = refit;
        p.reveals = new List<Vector3>();
        Write(p);
    }

    /// <summary>정비 화면에서 출항했다. 항로 확정(CommitLane·CommitChapter)이 없는 경로용.</summary>
    public static void ClearPendingRefit()
    {
        Progress p = Read();
        if (!p.pendingRefit)
            return;
        p.pendingRefit = false;
        Write(p);
    }

    /// <summary>수리·개조에 쓰는 물자. 판 한 장어치가 1이다.</summary>
    /// <summary>
    /// 창고 상한. **이 숫자가 보급 자리를 선택으로 만든다** - 상한이 없으면 보이는 보급은 전부 가는 것이
    /// 언제나 정답이라 고를 것이 없었다. 탄약이 가득이고 추진제가 빈 배는 탄약고를 지나치고 급유선으로 간다.
    ///
    /// 세 값 다 **한 판 돌려보고 정할 추정치다.** 지금 근거는 자리가 주는 양뿐이다 - 보급 부표가
    /// MTRL 16 / PROP 1,000,000 / MUN 200, 기항지가 MTRL 30 / MUN 400. 상한이 그 몇 배여야
    /// "몇 군데 돌면 찬다"가 된다. 연구는 화물이 아니라 정보라 상한이 없다.
    /// </summary>
    public const int MaxPropellant = 3000000;
    public const int MaxMunitions = 400000;

    /// <summary>
    /// 이 런의 돈. **화물이 아니라 계좌라 상한이 없다** - 위 두 상한이 보급 자리를 선택으로
    /// 만드는 장치인데, 돈에 같은 것을 걸면 "부자가 되면 급여를 못 받는다"가 된다.
    /// 쓰는 곳은 베이스뿐이다(수리·구매).
    /// </summary>
    public static int Credits
    {
        get => Read().credits;

        set
        {
            Progress p = Read();
            p.credits = Mathf.Max(0, value);
            Write(p);
        }
    }

    /// <summary>
    /// 연구점수. **런 자원이 아니라 메타 자원이다 - 저장소가 progress 파일이 아니다.**
    ///
    /// 처음에는 progress에 뒀는데 그게 구조적으로 항상 0을 보여줬다: 소비처(배 선택
    /// 화면)는 죽어야 열리는데, 죽는 순간 Battle이 Clear()로 progress를 지운다. 버는
    /// 것은 됐지만 **쓸 수 있는 유일한 순간에 지갑이 항상 비어 있었다.** 언락(연구된 배,
    /// PlayerPrefs)과 같은 수명이어야 그 배를 열 돈도 같이 살아남는다.
    /// </summary>
    public static int Research
    {
        get => PlayerPrefs.GetInt("research.points", 0);

        set
        {
            PlayerPrefs.SetInt("research.points", Mathf.Max(0, value));
            PlayerPrefs.Save();
        }
    }

    /// <summary>전략 이동에 쓰는 추진제.</summary>
    public static int Propellant
    {
        get => Read().propellant;

        set
        {
            Progress p = Read();
            p.propellant = Mathf.Clamp(value, 0, MaxPropellant);
            Write(p);
        }
    }

    /// <summary>재장전에 쓰는 탄약.</summary>
    public static int Munitions
    {
        get => Read().munitions;

        set
        {
            Progress p = Read();
            p.munitions = Mathf.Clamp(value, 0, MaxMunitions);
            Write(p);
        }
    }

    /// <summary>이 런에서 그 일이 있었는가.</summary>
    public static bool HasFlag(string flag) => Read().flags?.Contains(flag) ?? false;

    /// <summary>런의 기억에 한 줄 남긴다. 같은 flag를 두 번 남겨도 한 번만 남는다.</summary>
    public static void SetFlag(string flag)
    {
        if (string.IsNullOrWhiteSpace(flag))
            return;

        Progress p = Read();
        p.flags ??= new List<string>();

        if (!p.flags.Contains(flag))
            p.flags.Add(flag);

        Write(p);
    }

    /// <summary>
    /// 지금 따라다니는 아군. 순서가 곧 편대 순서다.
    ///
    /// 두 리스트를 짧은 쪽에 맞춰 자른다 - 저장이 반쪽으로 끝났거나 손으로 고친 파일이
    /// 들어와도 인덱스가 밖으로 나가지 않는다.
    /// </summary>
    public static List<Wingman> Wingmen
    {
        get
        {
            Progress p = Read();
            int n = Mathf.Min(p.wingmen?.Count ?? 0, p.wingmenSlots?.Count ?? 0);

            var list = new List<Wingman>(n);

            for (int i = 0; i < n; i++)
                list.Add(new Wingman(p.wingmen[i], p.wingmenSlots[i]));

            return list;
        }
    }

    /// <summary>
    /// 아군 하나가 합류한다. 같은 설계도가 이미 있어도 막지 않는다 - 같은 함급 두 척이
    /// 서로 다른 자리에 서는 것이 편대다.
    /// </summary>
    public static void Join(string ship, Vector2 slot)
    {
        if (string.IsNullOrWhiteSpace(ship))
            return;

        Progress p = Read();

        p.wingmen ??= new List<string>();
        p.wingmenSlots ??= new List<Vector2>();

        p.wingmen.Add(ship);
        p.wingmenSlots.Add(slot);

        Write(p);
    }

    /// <summary>
    /// 아군 하나가 죽었다. **자리로 지운다** - 같은 설계도가 둘일 수 있으므로 이름으로
    /// 지우면 엉뚱한 쪽이 빠진다.
    /// </summary>
    public static void Lose(int index)
    {
        Progress p = Read();

        if (p.wingmen == null || index < 0 || index >= p.wingmen.Count)
            return;

        p.wingmen.RemoveAt(index);

        if (p.wingmenSlots != null && index < p.wingmenSlots.Count)
            p.wingmenSlots.RemoveAt(index);

        Write(p);
    }

    /// <summary>
    /// 진행도를 읽는다. **<see cref="Exists"/>를 탄다** - 배 파일과 짝이 안 맞으면 여기서도
    /// 처음 상태여야 하고, 그 판정을 두 벌로 두면 언젠가 한쪽만 고친다.
    /// </summary>
    /// <summary>
    /// 진행도를 읽는다. **<see cref="Exists"/>(두 파일)가 아니라 진행도 파일 자체를 본다.**
    ///
    /// 예전에는 Exists를 탔다. `Sector`·`Salvage`는 첫 승리 **뒤에만** 쓰이고 그때는 배
    /// 파일도 같이 있어서 20구역을 멀쩡히 굴렀는데, 첫 전투 **전에** 쓰는 것이 하나
    /// 생기자마자(동료 합류) 조용히 깨졌다: 쓰기는 성공해서 파일이 생기는데, 배 파일이
    /// 아직 없으니 Exists가 false라 **바로 다음 Read가 그 파일을 안 읽는다.** 증상은
    /// "합류시켰는데 안 나온다"뿐이고 에러도 경고도 없다.
    ///
    /// 반쪽 상태를 판정하고 지우는 것은 <see cref="ValidateOrClear"/>의 일이고, 그건 런이
    /// 시작될 때 한 번만 돈다. 읽을 때마다 짝을 확인하면 그 둘의 주인이 겹친다 - CLAUDE.md의
    /// "판정과 정리의 주인이 갈려 있어야 한다"가 이 자리를 말한다.
    /// </summary>
    private static Progress Read()
    {
        if (!File.Exists(ProgressPath))
            return new Progress();

        string raw = File.ReadAllText(ProgressPath);
        try
        {
            var v = JsonUtility.FromJson<Progress>(raw) ?? new Progress();
            return v;
        }
        catch(Exception e)
        {
            Debug.LogAssertion(e);
            Clear();
            return new Progress();
        }
       
    }

    private static void Write(Progress p)
    {
        string json = JsonUtility.ToJson(p, prettyPrint: true);
        File.WriteAllText(ProgressPath, json);
    }

    /// <summary>
    /// 지금 이 배의 상태를 그대로 뜬다. <see cref="Battle.onEnd"/>에서 부른다.
    /// </summary>
    public static bool Save(Ship ship)
    {
        if (ship == null)
        {
            Debug.LogError("[RunState] 저장할 플레이어 함선이 없다.");
            return false;
        }

        string origin = string.IsNullOrEmpty(ship.shipDefName) ? ship.name : ship.shipDefName;
        ShipDef design = ShipDef.Load(origin);
        ShipDef damaged = ShipExporter.Export(ship.transform, origin, design);

        if (damaged == null)
        {
            Debug.LogError($"[RunState] '{origin}' export 실패. 손상이 안 남는다.");
            return false;
        }

        damaged.basedOn = origin;

        // 후면은 판과 반대로 **사라진 칸**을 적는다. 후면은 줄어들기만 하고 설계도를
        // basedOn으로 되찾을 수 있어서, 온전한 배면 이 목록이 비고 한 판 싸운 배도 수십 개다.
        var structure = ship.GetComponent<HullStructure>();
        damaged.rearLost = structure != null ? structure.LostRear() : new List<Vector2Int>();

        string text = JsonUtility.ToJson(damaged, prettyPrint: true);

        // 설계도 원문 위에 배치만 얹는다. 이러면 배 수치가 글자 하나까지 그대로 따라온다.
        Debug.Assert(design!=null); //이미 146줄에서 검사되는데 굳이? 일단 놔둘게.

        if (design != null && !string.IsNullOrEmpty(design.raw))
        {
            string merged = Merge(design.raw, damaged);

            if (merged != null)
                text = merged;
        }

        File.WriteAllText(FilePath, text);

        Debug.Log(
            $"[RunState] 판 {damaged.placements.Count}개, 잃은 후면 {damaged.rearLost.Count}칸 저장: {FilePath}");
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

    // 배열 하나만 담은 껍데기. ToJson이 `{"placements":[...]}`를 내주므로 대괄호를 찾는
    // 트릭이 명확해진다. ShipDef를 통째로 직렬화하면 배열이 둘이라(placements, rearLost)
    // "첫 [ 부터 마지막 ] 까지"가 두 배열을 한 덩어리로 집어온다.
    [System.Serializable] private class CellList { public List<Vector2Int> rearLost; }

    private static string ArrayOf(string json)
    {
        int start = json.IndexOf('[');
        int end = json.LastIndexOf(']');

        return start < 0 || end <= start ? null : json.Substring(start, end - start + 1);
    }

    /// <summary>
    /// 설계도 원문 위에 런 상태만 얹는다. 배 수치(drag, angleAccel...)는 글자 하나까지 그대로.
    ///
    /// `placements`는 설계도에 이미 있으니 갈아끼우고, `rearLost`는 **런 중에만 생기는 키라**
    /// 설계도에 없어서 끼워 넣어야 한다. ReplaceTopLevelValue는 없는 키를 못 만든다 -
    /// 그냥 두면 후면 손실이 조용히 저장에서 빠지고, 로그는 "판 N개 저장"을 멀쩡히 찍는다.
    /// </summary>
    private static string Merge(string designRaw, ShipDef damaged)
    {
        string placements = ShipDef.PlacementsArrayJson(damaged.placements);

        if (placements == null)
            return null;

        string merged = DefKeys.ReplaceTopLevelValue(designRaw, "placements", placements);

        if (merged == null)
            return null;

        string rear = ArrayOf(
            JsonUtility.ToJson(new CellList { rearLost = damaged.rearLost }));

        return rear == null
            ? merged
            : DefKeys.UpsertTopLevelValue(merged, "rearLost", rear) ?? merged;
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
            Clear();
            return;
        }

        ShipDef ship = Load();

        Debug.Log(
            $"[RunState] {Sector + 1}구역, CR {Credits} PROP {Propellant} MUN {Munitions}, " +
            $"배 '{ship?.basedOn ?? "?"}' 판 {ship?.placements.Count ?? 0}장. " +
            Application.persistentDataPath);
    }

#endif
}
