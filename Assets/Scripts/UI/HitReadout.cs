using UnityEngine;

/// <summary>
/// 최근 관통 판정 몇 줄. <see cref="PenetrationManager.Resolve"/>가 매 명중마다
/// Blocked/Ricochet/Penetrated와 <see cref="ShellState"/>를 이미 내는데 그 답이 화면
/// 어디에도 없었다 - 경사장갑이 값을 하는 게임인데 튕겨낸 건지 안 맞은 건지 구분이
/// 안 갔다.
///
/// **플레이어가 낀 명중만 적는다.** 남의 배끼리의 사격은 함장이 알 방법이 없다.
/// <see cref="DamageLog"/>와 달리 판이 아니라 시각 순서로 쌓는다 - 저쪽은 월드에
/// 마커를 찍느라 판마다 최신 하나로 병합하고, 이쪽은 "방금 무슨 일이 있었나"라서
/// 같은 판을 세 번 튕겨낸 것이 세 번으로 읽혀야 한다.
///
/// 판정에는 아무 영향도 없다. 이 파일을 지워도 결과는 안 바뀐다.
/// </summary>
public static class HitReadout
{
    public const int Capacity = 5;

    /// <summary>이만큼 지나면 지운다. 마지막 1초는 흐려지면서 나간다.</summary>
    public const float HoldSeconds = 4f;
    public const float FadeSeconds = 1f;

    public struct Line
    {
        public string text;

        /// <summary>내가 맞은 것인가. 아니면 내가 쏜 것이 맞은 것이다.</summary>
        public bool incoming;

        /// <summary>
        /// 결정적이지 않은 소식인가. 장갑이 세웠거나, 모듈을 긁기만 했거나.
        /// 같은 방향이라도 좋은 소식과 나쁜 소식이 여기서 갈린다.
        /// </summary>
        public bool minor;

        /// <summary>같은 판정이 연달아 몇 번. m12는 초당 여러 발이라 병합이 없으면 도배된다.</summary>
        public int count;

        public float time;
    }

    private static readonly Line[] _lines = new Line[Capacity];

    public static int Count { get; private set; }

    /// <summary>0이 제일 최근.</summary>
    public static Line Get(int age) => _lines[age];

    /// <summary>
    /// 판정 하나가 확정됐다. Rigidbody를 받는 이유는 부르는 쪽(<c>Projectile.Apply</c>)이
    /// 이미 그 둘을 손에 들고 있어서다 - 거기서 함선을 거슬러 찾으면 판이 잔해로
    /// 재부모화되는 중일 수 있고, 애초에 "누가 플레이어인가"는 UI의 질문이지 탄의
    /// 질문이 아니다.
    /// </summary>
    public static void Hit(
        HitOutcome outcome, ShellState state, bool rearHeld,
        Rigidbody2D target, Rigidbody2D shooter)
    {
        Ship player = GameManager.Player();

        if (player == null || player.Rig == null)
            return;

        Rigidbody2D me = player.Rig;
        bool incoming = target == me;

        // 남의 싸움. 잔해에 맞은 것도 여기서 빠진다 - 잔해는 이제 내 배가 아니다.
        if (!incoming && shooter != me)
            return;

        Push(
            Verdict(outcome, state, rearHeld),
            incoming,
            outcome != HitOutcome.Penetrated || rearHeld);
    }

    /// <summary>
    /// 탄이 모듈을 지나쳤다. **장갑 판정과 다른 사건이라 다른 줄이다** - 워썬더가
    /// `Hit` 옆에 `Engine damaged`를 따로 주는 자리가 이것이고, 한 발이 판을 뚫고
    /// 안쪽 원자로까지 가면 둘 다 일어난 것이 사실이다.
    ///
    /// 이름은 defName 그대로다(<c>ThingDef.Spawn</c>이 GameObject 이름을 그렇게 짓는다).
    /// 대문자 변환은 그리는 쪽에서 한다 - 여기는 명중마다 도는 자리라 문자열을 만들면
    /// pd20(900 RPM)이 초당 열다섯 번 할당한다.
    /// </summary>
    public static void Module(string name, bool killed, Rigidbody2D target, Rigidbody2D shooter)
    {
        if (string.IsNullOrEmpty(name))
            return;

        Ship player = GameManager.Player();

        if (player == null || player.Rig == null)
            return;

        Rigidbody2D me = player.Rig;
        bool incoming = target == me;

        if (!incoming && shooter != me)
            return;

        // 죽는 순간만 문자열을 만든다. 이미 죽은 모듈은 부르는 쪽이 걸러낸다.
        Push(killed ? name + " OUT" : name, incoming, !killed);
    }

    /// <summary>
    /// 격실 파공. HullOutcome 계열이 아니라 별도 문이다 - Ship.Atmosphere가
    /// 판 소실을 이미 걸쇠로 세고 있으니(RunLog.RoomBreached와 같은 자리) 여기는
    /// 문장 하나만 얹는다.
    /// </summary>
    public static void RoomBreach() => Push("ROOM BREACHED", true, false);

    /// <summary>
    /// 워썬더가 아이콘이 아니라 문장을 주는 이유가 이것이다 - 각도와 유효 RHA를 같이
    /// 띄우면 플레이어가 산수를 하게 되고, Gaijin은 그 숫자들을 결국 판정 한 마디로
    /// 바꿨다. 숫자는 이미 <see cref="PenetrationManager.Describe"/>에 있다.
    ///
    /// 후면이 <b>관통을 대체한다.</b> 두 줄로 나누면 한 발이 두 줄을 먹고 연발 병합이
    /// 어긋나는데, 애초에 뒷벽은 앞판을 뚫었을 때만 판정받으므로 "REAR ARMOR HELD"가
    /// 이미 "앞판은 뚫렸다"를 품고 있다.
    /// </summary>
    private static string Verdict(HitOutcome outcome, ShellState state, bool rearHeld) => outcome switch
    {
        HitOutcome.Penetrated => rearHeld ? "REAR ARMOR HELD" : "PENETRATION",
        HitOutcome.Ricochet => "RICOCHET",
        _ => state == ShellState.Shattered ? "SHELL SHATTERED" : "NO PENETRATION",
    };

    /// <summary>
    /// 링을 미는 자리. public인 이유는 <c>HitReadoutSelfTest</c>가 여기를 직접 부르기
    /// 때문이다 - 병합·밀기·상한이 이 파일에서 유일하게 틀릴 수 있는 부분인데,
    /// <see cref="Hit"/>는 씬의 플레이어 함선을 물어서 에디터에서 못 부른다.
    /// </summary>
    /// <summary>
    /// 링을 미는 자리. public인 이유는 <c>HitReadoutSelfTest</c>가 여기를 직접 부르기
    /// 때문이다 - 병합·밀기·상한이 이 파일에서 유일하게 틀릴 수 있는 부분인데,
    /// <see cref="Hit"/>는 씬의 플레이어 함선을 물어서 에디터에서 못 부른다.
    /// </summary>
    public static void Push(string text, bool incoming, bool minor)
    {
        float now = Time.time;

        // 같은 판정이 이어지면 줄을 늘리지 않고 센다.
        if (Count > 0 && _lines[0].text == text && _lines[0].incoming == incoming)
        {
            _lines[0].count++;
            _lines[0].text = text;
            _lines[0].time = now;
            return;
        }

        for (int i = Mathf.Min(Count, Capacity - 1); i > 0; i--)
            _lines[i] = _lines[i - 1];

        _lines[0] = new Line
        {
            text = text,
            incoming = incoming,
            minor = minor,
            count = 1,
            time = now,
        };

        if (Count < Capacity)
            Count++;
    }
}
