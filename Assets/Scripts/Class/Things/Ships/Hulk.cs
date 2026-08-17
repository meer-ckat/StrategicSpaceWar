using System.Collections.Generic;
using UnityEngine;
using Core;

/// <summary>
/// 배가 아닌, 판으로 이루어진 떠다니는 덩어리. 함선에서 떨어져 나온 잔해가 원래 이것이었고,
/// 운석·폐위성·거울처럼 처음부터 배가 아닌 물체도 전부 같은 것이다.
///
/// 별도 시스템이 아니다. 관통도 파편도 충각 전도도 8방향 파단도 <see cref="Ship"/>이 아니라
/// <see cref="HullStructure"/>와 <see cref="Armor"/>에 걸려 있어서, 리지드바디 하나에 판을
/// 붙여 놓으면 그냥 돈다. 빠져 있던 것은 "처음부터 그렇게 태어나는 길" 하나뿐이었다.
///
/// 함선과 다른 점은 없는 것들이다: 방·기압·승무원·추진·AI. 그건 전부 Ship에만 있다.
/// 폐위성 내부를 밀폐 구획으로 만들고 싶어지면 그때는 Ship을 상속해야 한다.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(HullStructure))]
public class Hulk : Thing
{
    /// <summary>
    /// StreamingAssets/Ships/&lt;이름&gt;.json. 채우면 Awake에서 JSON대로 짓는다 - 운석·폐위성처럼
    /// 씬에 미리 놓아두는 물체용이다.
    ///
    /// 비어 있으면 아무것도 안 짓는다. 함선에서 떨어져 나온 조각이 그 경우인데, 그때는
    /// <see cref="HullStructure.Adopt"/>가 이미 판과 격자를 다 넘겨받은 뒤다.
    /// </summary>
    public string structureDefName;

    /// <summary>
    /// 이 틱 수가 지나면 사라진다. 0 이하면 영원히 남는다 - 배치해 둔 운석이 60초 뒤에
    /// 증발하면 안 된다. 잔해는 Breakaway가 양수로 채워 준다.
    /// </summary>
    public int lifeTick;

    /// <summary>
    /// 판 한 장당 질량. JSON으로 지을 때만 쓴다 - 큰 운석이 자동으로 무거워야 하고,
    /// 인스펙터에서 배치마다 손으로 맞추면 반드시 어긋난다.
    /// 잔해는 Breakaway가 본체에서 비례해서 떼어 온 질량을 이미 들고 있다.
    /// </summary>
    public float massPerPlate = 10f;

    /// <summary>떨어져 나가는 조각이 받는 이탈 속도. 함선의 같은 이름 필드와 같은 뜻이다.</summary>
    public float breakawaySpeed = 2f;

    private HullStructure _structure;

    protected override void Awake()
    {
        base.Awake();
        _structure = GetComponent<HullStructure>();

        // 우주에 아래가 없다. 프로젝트 중력이 0이라 이 줄이 없어도 지금은 안 떨어지지만,
        // 몸을 만드는 세 자리(Ship.Awake, HullStructure.MakeDebris, 여기)가 같은 말을 해야
        // 나중에 중력을 쓰는 무엇이 생겨도 함선과 구조물이 안 딸려간다.
        if (TryGetComponent(out Rigidbody2D body))
            body.gravityScale = 0f;

        // 함선의 Awake와 같은 순서다. 심고 -> 격자를 도장하고 -> 구조에 넘긴다.
        // 세 단계 전부 Transform만 받으므로 Ship이 없어도 그대로 돈다.
        if (!ShipBuilder.SpawnFrom(transform, structureDefName, this))
            return;

        var armorAt = new Dictionary<Vector2Int, Armor>();
        var doorAt = new Dictionary<Vector2Int, Door>();

        ShipGrid.Map map = ShipBuilder.Stamp(transform, armorAt, doorAt);

        if (map == null)
        {
            Debug.LogError($"[Hulk] '{structureDefName}'에 판이 하나도 없다.", this);
            return;
        }

        _structure.Build(map, breakawaySpeed);

        // 파단 때 Breakaway가 조각 크기 비율로 질량을 나눠 주므로, 시작 질량만 판 수에
        // 비례시켜 두면 쪼개진 뒤에도 계속 맞는다.
        var b = GetComponent<Rigidbody2D>();
        b.mass = Mathf.Max(1f, (armorAt.Count + doorAt.Count) * massPerPlate);
    }

    public override void OnTick()
    {
        // 함선과 같은 자리, 같은 이유. 물리 콜백 밖에서, 이번 틱의 무엇보다 먼저.
        // 덩어리도 다시 쪼개진다 - 가운데 판이 없어지면 남은 두 조각은 더 이상 한 몸이 아니다.
        _structure?.TrySplitIfBroken();
        Ram();

        // 판이 하나도 안 남았으면 빈 리지드바디만 떠다니는 셈이다
        if (GetComponentInChildren<Armor>() == null)
        {
            Destroy(gameObject);
            return;
        }

        if (lifeTick > 0 && TickManager.currentTick - spawnTick >= lifeTick)
            Destroy(gameObject);
    }

    /// <summary>
    /// 덩어리도 부딪히면 부순다. 이게 없으면 운석이 체력 무한인 벽이라, 들이받은 함선만
    /// 일방적으로 갈려나간다. 함선과 같은 규칙 - 충돌 콜백 없이 매 틱 앞을 쓴다.
    /// </summary>
    private void Ram()
    {
        if (TryGetComponent(out Rigidbody2D body))
            RamImpact.Punch(transform, body, Vector2.zero);
    }
}
