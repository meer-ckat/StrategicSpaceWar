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

    private float spawnTimer; //생성 타이머
    Ship owner;

    /// <summary>지금 살아 있는 내 소속기. 격침되면 Unity가 참조를 가짜 null로 만들고 아래가 걷는다.</summary>
    private readonly List<Ship> _launched = new();

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
        //
        // _detatchBrain도 안 켠다 - ShipAi에서 뇌 떼기와 교전이 배타적이라
        // 켜는 순간 전투기가 모함 옆에서 대열만 맞추고 안 싸운다.

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

