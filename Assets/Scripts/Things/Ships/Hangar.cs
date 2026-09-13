using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class Hangar : CriticalModule
{
    //격납고 코드
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    public float spawnInterval = 10f; //격납고가 함선을 생성하는 간격
    public string shipPrefabName; //생성할 함선의 설계도 이름

    /// <summary>건조에 걸리는 초. 배가 판 하나씩 자라는 시간이다.</summary>
    public float buildSeconds = 8f;

    /// <summary>선체 밖으로 더 띄우는 여유(m). 겹침을 눈으로 확인하고 조절할 자리.</summary>
    public float clearance = 12f;
    /// <summary>이 격납고가 동시에 띄워 둘 수 있는 수. carrier는 격납고가 6기라 상한이 없으면 분당 45척이 나온다.</summary>
    public int maxAlive = 2;

    /// <summary>
    /// 출격기의 기본 명령. **배 def가 아니라 여기 있는 것이 규칙이다** - 같은 fly가 캠페인 동료로도
    /// 격납고 소모품으로도 태어나므로, 출격 후 행동은 설계가 아니라 누가 내보냈나의 속성이다
    /// (CLAUDE.md "team은 설계가 아니다 - 소환 인자다"와 같은 자리).
    ///
    /// Escort:     편대 고정. 절대 안 떨어진다 - 포탑은 알아서 쏘므로 "따라다니며 쏘는" 호위가 된다.
    /// Aggressive: 편대를 지키다 적이 센서에 들면 교전, 사라지면 자리로 돌아온다.
    /// Attack:     태어나자마자 제일 가까운 적으로. 편대가 아예 없다 - 예전 동작이다.
    /// </summary>
    public string PilotPatrolMode = "Aggressive";

    public enum PilotMode { Escort, Aggressive, Attack }

    /// <summary>
    /// 모르는 값이면 기본으로 떨어지되 **한 번 크게 말한다.** JsonUtility가 모르는 키를 조용히
    /// 버리는 것을 ThingDef.Validate가 막는 것과 같은 이유다 - 오타가 조용히 다른 동작이 되면
    /// 증상이 "격납고가 좀 이상한데"뿐이라 영영 안 보인다.
    /// </summary>
    private PilotMode Mode
    {
        get
        {
            if (System.Enum.TryParse(PilotPatrolMode, ignoreCase: true, out PilotMode parsed))
                return parsed;

            if (!_modeWarned)
            {
                _modeWarned = true;
                Debug.LogError(
                    $"[Hangar] PilotPatrolMode '{PilotPatrolMode}'를 모른다. Escort / Aggressive / Attack 중 하나여야 한다. "
                    + "Aggressive로 간다.", this);
            }

            return PilotMode.Aggressive;
        }
    }

    private bool _modeWarned;

    private float spawnTimer; //생성 타이머
    Ship owner;

    /// <summary>지금 살아 있는 내 소속기. 격침되면 Unity가 참조를 가짜 null로 만들고 아래가 걷는다.</summary>
    private readonly List<Ship> _launched = new();

    /// <summary>편대 자리를 겹치지 않게 하는 번호. 죽어도 안 되감는다 - 되감으면 새로 난 기체가 옆 기체 자리에 겹쳐 태어난다.</summary>
    private int _slotSeq;

    /// <summary>
    /// **CriticalModule은 안 틱한다.** 그걸 그대로 물려받으면 아래 OnTick이 등록조차
    /// 안 돼서 격납고가 배를 영영 안 만든다 - 오버라이드가 있는데 안 불리는 것이라
    /// 에러도 로그도 없다.
    /// </summary>
    protected override bool NeedsTick => true;

    protected override void Awake()
    {
        base.Awake();
        spawnTimer = spawnInterval; //시작 시 타이머 초기화
    }

    public override void OnTick()
    {
        base.OnTick();

        // 컷신 동안은 문을 안 연다. 타이머도 안 흘러서 끝난 뒤 한꺼번에 쏟아지지 않는다.
        if (CutSceneManager.Active || (owner != null && owner.dormant))
            return;

        // **Time.deltaTime이 아니다.** OnTick은 고정 틱(1/60)이라 프레임 시간을 빼면
        // 프레임이 떨어질 때 격납고가 빨라진다. 틱과 프레임을 가르는 이 레포의 규칙.
        spawnTimer -= Core.TickManager.TickDeltaTime;

        if (spawnTimer > 0f)
            return;

        spawnTimer = spawnInterval; //타이머 초기화

        // 죽은 것을 먼저 걷는다. == null은 Unity의 가짜 null이라 Destroy된 배도 잡힌다.
        for (int i = _launched.Count - 1; i >= 0; i--)
        {
            if (_launched[i] == null || !_launched[i].IsCombatEffective) //ICE 검사 추가. ICE 검사가 없는게 밸런스적으로 옳지만, 근데 좀 불편함.
                _launched.RemoveAt(i);
        }

        SteerLaunched();

        if (_launched.Count >= maxAlive)
            return;

        Ship born = Spawn(shipPrefabName); //함선 생성

        if (born != null)
            _launched.Add(born);
    }

    void Start()
    {
        owner = GetComponentInParent<Ship>();
    }

    /// <summary>
    /// 편대 유지와 교전의 전환. **Aggressive만 뒤집는다** - Escort는 고정이라 태어날 때 켠 뇌 떼기를
    /// 아무도 안 건드리고, Attack은 편대 자체가 없다.
    ///
    /// **매 틱이 아니라 격납고 타이머 주기다**(spawnInterval) - 적이 센서에 들락거리는 것은 초 단위라
    /// 5초 해상도로 충분하고, 항모 격납고 6기 x 기체 2대가 매 틱 돌 이유가 없다.
    ///
    /// 조종만 바꾼다. 포탑은 원래부터 ShipAi를 안 거치고 Gun이 직접 표적을 잡으므로,
    /// 편대를 지키는 동안에도 사거리에 들어온 적은 알아서 쏜다.
    /// </summary>
    private void SteerLaunched()
    {
        if (Mode != PilotMode.Aggressive || owner == null)
            return;

        for (int i = 0; i < _launched.Count; i++)
        {
            Ship born = _launched[i];

            if (born == null || !born.TryGetComponent(out ShipAi ai))
                continue;

            // 뇌를 떼면 편대 자리로, 붙이면 스스로 표적을 고른다. Campaign.SteerRovers와 같은 규칙이다.
            ai._detatchBrain = born.NearestHostile() == null;
        }
    }

    private Ship Spawn(string shipDefName) //custom spawnAnimation이 있기에 이런식으로.
    {
        if (string.IsNullOrEmpty(shipDefName))
            return null;

        if (!File.Exists(ShipDef.PathOf(shipDefName)))
        {
            Debug.LogError($"[Campaign] '{shipDefName}' 설계도가 없다. 건너뛴다.");
            return null;
        }

        // 비활성으로 만들고 마지막에 켠다. AddComponent는 오브젝트가 활성이면 Awake를
        // 즉시 부르는데, Ship.Awake가 shipDefName을 읽어 배를 통째로 짓는다.
        var go = new GameObject($"{shipDefName}(Builded from Module {defName})");
        go.SetActive(false);

        go.transform.localScale = owner != null ? owner.transform.localScale : Vector3.one;

        var ship = go.AddComponent<Ship>();
        ship.shipDefName = shipDefName;
        ship.buildSeconds = buildSeconds;

        // **격납고 밖에 짓는다.** 격납고는 4x3인데 scout은 30x14다 - 안에서 지으면
        // 배가 모함 선체를 통째로 뚫는다. 격납고는 컨테이너가 아니라 문이다.
        ship.buildAnchor = owner != null ? owner.transform : transform;
        ship.buildOffset = LaunchOffset(shipDefName);

        go.transform.position = ship.buildAnchor.TransformPoint(ship.buildOffset);
        ship.team = owner != null ? owner.team : default; //소환한 함선이 소환자와 같은 팀이 되도록

        // 조종하는 것이 붙어야 배가 움직인다.
        go.AddComponent<ShipAi>();

        // Ship.Awake가 배를 통째로 짓는 자리라 이 리포에서 제일 많이 던질 수 있는
        // 켜기다. 실패하면 Thing.Activate가 지우고 여기서 그만둔다 - 아래 편대·속도
        // 설정은 살아 있는 배를 전제한다.
        if (!Thing.Activate(go))
            return null;

        Campaign._spawned.Add(go);

        // **Campaign._wingmen에 넣지 않는다.** 그 명단은 구역을 넘어 살아남는 캠페인
        // 동료이고, BuryWingmen이 그 인덱스를 RunState.Lose에 그대로 넘겨 저장된
        // 명부에서 지운다. 사출기가 소모품을 같은 리스트에 밀어 넣으면 두 명단의
        // 번호가 어긋나서, 전투기 한 대가 죽을 때 애먼 동료 함선이 세이브에서 사라진다.

        // 편대 명령. 뇌 떼기와 교전이 배타적이라 예전에는 이걸 켜면 전투기가 대열만 맞추고 안 싸웠는데,
        // Escort()가 매 주기 다시 붙였다 떼면서 그 배타성이 곧 전환이 된다.
        if (Mode != PilotMode.Attack)
        {
            ShipDef born = ShipDef.Load(shipDefName);
            var ai = go.GetComponent<ShipAi>();
            ai._formation = owner != null ? owner.transform : null;
            ai._formationOffset = Campaign.FormationOffset(
                ++_slotSeq, born != null ? born.Bbox() : new RectInt(0, 0, 10, 5));
            ai._detatchBrain = true;
        }

        return ship;
    }

    /// <summary>
    /// 모함 로컬 기준 건조 자리. 격납고가 선체 어느 쪽에 붙었는지로 나가는 방향을 정하고,
    /// 모함 절반 + 새 배 절반 + 여유만큼 밀어낸다.
    ///
    /// 위아래로 내보낸다 - 옆으로 빼면 뱃머리·꽁무니가 교전선과 겹쳐서, 나오는 배가
    /// 모함의 사선에 선다.
    /// </summary>
    private Vector2 LaunchOffset(string shipDefName)
    {
        // 격납고는 격자를 차지하므로 선체 직속 자식이다 - localPosition이 곧 선체 좌표다.
        Vector2 local = transform.localPosition;
        float side = local.y >= 0f ? 1f : -1f;

        float motherHalf = owner != null && owner.DesignMap != null
            ? owner.DesignMap.height * ShipGrid.CellSize * 0.5f
            : 0f;

        ShipDef born = ShipDef.Load(shipDefName);
        float bornHalf = born != null ? born.Bbox().height * ShipGrid.CellSize * 0.5f : 0f;

        return new Vector2(local.x, side * (motherHalf + bornHalf + clearance));
    }
}

