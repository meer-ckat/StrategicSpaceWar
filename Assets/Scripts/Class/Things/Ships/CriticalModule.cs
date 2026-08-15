using UnityEngine;

/// <summary>
/// 죽으면 터지는 모듈. 탄약고와 원자로가 전부 이것이고, 둘의 차이는 데이터 두 개뿐이다 -
/// 전기를 대는가(<see cref="providesPower"/>), 얼마나 크게 터지는가(<see cref="blastDamage"/>).
///
/// **시타델은 규칙이 아니라 배치다.** 이 클래스는 "여기가 시타델"이라는 구역을 격자에 새기지
/// 않는다. 그러면 새 개념이 하나 늘고 방·선체 BFS와 또 어긋난다. 대신 이건 그냥 판에 볼트로
/// 붙는 모듈이고, 어디에 붙였는지가 곧 시타델이다.
///
/// destroyer로 재보면 그 차이가 이렇게 나온다 (blastDamage 1600):
///   중앙·상부구조 밖  -> 선체가 143 + 45로 갈라진다. 포탑이 날아가는 T-80
///   선수             -> 44칸이 사라지지만 선체는 한 덩어리. 격실 설계로 살아남는 에이브럼스
///   중앙·상부구조 밑  -> 판은 제일 많이 죽는데 함교 지붕이 다리를 놓아 안 갈라진다
///
/// 셋 다 배치 JSON의 좌표 하나 차이고, 분기문은 없다.
/// </summary>
public class CriticalModule : Thing, IDamageable
{
    [Header("내구")]
    public float maxHealth = 40f;

    /// <summary>
    /// 죽을 때 자기 자리에 쏟는 피해. 폭발의 **반경**은 이 값이 아니라 Ballistics.BlastCutoff가
    /// 정한다 - 여기는 "닿은 판이 죽느냐"를 정하는 문턱이다. 재본 값: 800이면 큰 구멍,
    /// 1600이면 선체가 갈라진다.
    /// </summary>
    public float blastDamage = 1600f;

    /// <summary>
    /// 원자로인가. 배는 살아 있는 이것이 하나라도 있어야 조타하고 조준한다 - 포탑 선회에
    /// 전기가 든다. 이중화는 def에 둘을 넣는 것으로 정해진다.
    /// </summary>
    public bool providesPower;

    /// <summary>터질 때 띄울 빛의 defName. 비우면 안 띄운다.</summary>
    public string flashDef = "Blast Flash";

    /// <summary>화면 흔들림 세기. 폭발이 화면 밖에서 나도 뭔가 일어났다는 것이 전해진다.</summary>
    public float shake = 0.6f;

    /// <summary>Resources/Sound 아래의 클립 이름. 비우면 소리 없이 터진다.</summary>
    public string blastSound = "Explosion";

    /// <summary>낮을수록 크고 무거운 폭발로 들린다. 같은 클립으로 탄약고와 원자로를 가른다.</summary>
    public float blastPitch = 1f;

    private float _health;
    private bool _detonated;

    // 탄약고가 옆 탄약고를 죽이고, 그게 또 옆을 죽인다. 파편 연쇄와 같은 이유로 상한이 필요하다 -
    // 없으면 배 한 척에 탄약고 셋만 있어도 한 발이 전부를 지운다.
    private static int _chain;

    public bool Neutralized => _health <= 0f;
    public float Health01 => maxHealth > 0f ? _health / maxHealth : 0f;

    protected override void Awake()
    {
        base.Awake();
        _health = maxHealth;
    }

    public override void OnTick() { }

    public void TakeDamage(float amount)
    {
        if (amount <= 0f || _detonated)
            return;

        _health = Mathf.Max(0f, _health - amount);

        if (_health <= 0f)
            Detonate();
    }

    /// <summary>
    /// 유폭. **새 시스템이 아니다** - 충각이 쓰는 충격 전도를 등방성으로 부르고, 서브셀이
    /// 죽을 때 쓰는 파편 폭풍을 크게 부른다. 배가 두 동강 나는 것은 그 결과지 이벤트가 아니다:
    /// 판들이 죽고, 이미 매 틱 도는 HullStructure의 8방향 BFS가 두 덩어리를 찾는다.
    /// </summary>
    private void Detonate()
    {
        _detonated = true;

        if (_chain >= Ballistics.MaxDetonationChain)
            return;

        // 자기가 볼트로 붙은 판이 폭심이다. 판이 없으면(선체 직속으로 떠 있으면) 전도할
        // 그래프가 없으므로 파편만 나간다.
        Armor mount = GetComponentInParent<Armor>();

        _chain++;

        try
        {
            if (mount != null)
                RamImpact.Detonate(mount, blastDamage);

            // 그림과 소리. 시뮬레이션은 위에서 이미 다 끝났고 아래는 사람을 위한 것이다.
            DefDatabase.Spawn(flashDef, null, transform.position, 0f);
            CameraSystem.Shake(shake);

            // 위치를 안 넘긴다 = 2D. SoundManager의 3D 롤오프가 40 m에서 끝나는데 교전거리는
            // 200이라, 위치를 주면 적함의 유폭이 아예 안 들린다. 배 한 척이 사라지는 사건은
            // 어디서 나든 들려야 한다.
            SoundManager.AudioShot(blastSound, 1f, blastPitch);

            SpallResolver.Burst(
                transform.position,
                transform.up,
                Ballistics.CollapseSpread,
                blastDamage * Ballistics.BlastFragmentFraction,
                Ballistics.SpallMaxCount,
                Ballistics.Hash(GetInstanceID(), Core.TickManager.currentTick, 0),
                ~0);
        }
        finally
        {
            _chain--;
        }

        Debug.Log($"[{name}] 유폭. 피해 {blastDamage:0}.");
    }
}
