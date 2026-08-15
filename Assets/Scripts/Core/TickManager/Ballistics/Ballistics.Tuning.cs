using UnityEngine;

/// <summary>
/// Every value in this file is a tuning knob, not a physical law. Nothing here computes
/// anything - the maths lives in Ballistics.Formula.cs and Ballistics.SubCell.cs.
/// </summary>
public static partial class Ballistics
{
    // --- geometry / loop ---
    public const float Epsilon = 0.005f;      // m, post-hit push-off
    public const float EdgeEpsilon = 0.01f;   // m, simultaneous-contact window
    public const float MinSpeed = 20f;        // m/s, below this the shell is gone
    public const int MaxHitsPerTick = 8;      // guard against infinite corner ricochets
    public const float MinCos = 0.15f;        // effective RHA cap ~6.7x

    /// <summary>
    /// Hitting the seam between two colliders makes Physics2D report a normal the shell
    /// is travelling parallel to (dot ~ 0) - corner garbage, not a real face. Reading it
    /// literally ricochets a head-on shot off thin air; discarding it tunnels through the
    /// wall. When no contacted surface faces the shell by at least this much, the impact
    /// falls back to normal incidence: no free angle bonus, no free ricochet, no free pass.
    /// 0.02 ~ 88.9 degrees, past the +10 ricochet cap, so no real grazing hit is affected.
    /// </summary>
    public const float MinFacing = 0.02f;

    // --- shell damage ---
    public const float DeformSeverity = 1.0f;
    public const float ShatterSeverity = 1.5f;
    public const float IntactDecay = 0.95f;
    public const float DeformDecay = 0.70f;
    public const float ShatterDecay = 0.25f;

    // --- ricochet ---
    public const float BaseCritAngle = 70f;      // deg from normal
    public const float OvermatchBonus = 10f;     // total cap, 1x..2x overmatch linear
    public const float RicochetTangent = 0.85f;
    public const float RicochetNormal = 0.15f;
    public const float RicochetArmorDamage = 0.05f;

    // --- armor damage ---
    public const float AttackRatioFloor = 0.2f;

    /// <summary>
    /// Joules -> armor HP. Pure calibration knob: 5 kg at 900 m/s blocked head-on deals
    /// ~200. Armor.hpPerSquareMetre is per m2, so compare against that times the plate area.
    ///
    /// Note the swing this feeds: armorDamage scales with (effectiveRHA / penetration)^2,
    /// so a round overmatching the plate 3x deposits only ~11% of its energy and drills a
    /// clean hole, while a marginal penetration dumps nearly all of it. Armor that never
    /// seems to wear down usually means the gun is far too strong for it, not that this
    /// number is wrong.
    /// </summary>
    public const float DamageScale = 1e-4f;

    // --- spall ---
    public const float SpallEnergyFraction = 0.35f;
    public const float SpallEnergyPerFragment = 20f;
    public const float SpallMinEnergy = 5f;
    public const int SpallMaxCount = 24;
    public const int HeavyFragmentCount = 4;

    // A shattered shell's remains are the one spall population worth promoting to real
    // projectiles: 4 per event instead of 24, and big enough to cross a whole ship, which
    // a one-tick ray clamped to SpallRangeMax cannot do.
    public const float HeavyFragmentSlowest = 0.7f;   // fraction of residual speed
    public const int HeavyFragmentLifeTick = 60;     // 2 s - they do not fly forever
    public const int MaxFragmentGeneration = 1;       // fragments never shed fragments
    /// <summary>
    /// 파편이 판 안으로 얼마나 들어가는가. 1이면 판을 가로질러 에너지를 얇게 펴 발라서
    /// 아무데도 안 뚫리고, 0이면 표면 칸만 갉아 테두리만 사라진다. 순수 조율값.
    /// </summary>
    public const float SpallChannelDepth = 0.5f;

    public const float SpallSpreadMax = 45f;   // deg, half-angle
    public const float SpallSpreadMin = 6f;
    public const float SpallRangePerEnergy = 0.5f;  // m per HP-unit of fragment energy
    public const float SpallRangeMin = 1f;
    public const float SpallRangeMax = 15f;

    // --- collapsing structure ---

    /// <summary>
    /// A sub-cell that reaches 0 HP has not just stopped resisting - the plate material
    /// there came apart, and it goes somewhere. Fraction of the sub-cell's structural
    /// budget that leaves as fragments.
    /// </summary>
    public const float CollapseEnergyFraction = 0.5f;

    public const int CollapseFragmentCount = 6;

    /// <summary>
    /// 판이 통째로 무너지는 지점. 36칸을 하나도 남김없이 지워야 사라지게 두면, 마지막
    /// 몇 칸이 이미 아무것도 못 버티는데도 판이 서 있고 탄이 거기서 멈춘다.
    /// 0.5면 36칸 중 18칸에서 무너진다.
    /// </summary>
    public const float PlateCollapseFraction = 0.5f;

    /// <summary>
    /// Debris from a disintegrating plate has no preferred direction the way spall behind
    /// a penetration does, so it goes everywhere.
    /// </summary>
    public const float CollapseSpread = 180f;

    /// <summary>
    /// Fragments that kill sub-cells spawn more fragments. One extra generation reads as a
    /// plate coming apart; unbounded, a single shell erases the ship.
    /// </summary>
    public const int MaxSpallDepth = 2;

    // --- 모듈 직격 ---

    /// <summary>
    /// 탄이 모듈을 관통하며 놓고 가는 운동에너지의 몫. 모듈은 장갑이 아니라서 탄을 세우지
    /// 못한다 - 400mm 철갑탄이 엔진 케이싱에 막히지는 않는다. 대신 그만큼 느려진다.
    /// 0.15면 모듈 하나당 속도가 약 92%로 떨어져, 다섯 개를 뚫으면 65%쯤 남는다.
    /// </summary>
    public const float ModuleHitFraction = 0.15f;

    // --- 승무원 ---

    /// <summary>
    /// 승무원이 버틸 수 있는 최저 기압(0~1). 함내에 이 이상인 방이 하나도 없으면 승무원이
    /// 죽고, 조타·사격·수리가 전부 멎는다. 격파 판정은 이것 하나에서 자연발생한다 -
    /// 함선 HP 같은 건 없다.
    ///
    /// 이 값을 만지기 전에 leakRate를 먼저 봐라. 둘이 곱해져서 "얼마나 버티느냐"가 된다.
    /// </summary>
    public const float CrewMinPressure = 0.3f;

    // --- 충각 ---

    /// <summary>
    /// 충돌 에너지 중 판이 실제로 먹는 몫. DamageScale을 그대로 통과하므로 포탄 피해와
    /// 같은 눈금 위에 있다 - 여기만 만지면 충각의 치명도를 따로 조절할 수 있다.
    /// </summary>
    public const float RamDamageFraction = 1f;

    /// <summary>
    /// 이 아래는 접촉이지 충돌이 아니다. 없으면 두 함선이 스치기만 해도 장갑이 갈린다.
    /// N·s.
    /// </summary>
    public const float RamMinImpulse = 200f;

    /// <summary>
    /// 함선에서 떨어져 나온 조각이 남아 있는 틱 수. 60틱/초라 3600이면 60초.
    /// 배치해 둔 운석·폐위성은 Hulk.lifeTick을 0으로 두어 이 규칙에서 빠진다.
    /// </summary>
    public const int DebrisLifeTick = 3600;

    /// <summary>
    /// 충격축을 1 m 따라갈 때 남는 몫. 1에 가까울수록 배를 깊이 관통한다.
    ///
    /// 이 값과 아래 RamConductAcross의 **비율**이 충각의 성격을 통째로 정한다. 0.80 대 0.30이면
    /// 축으로 7 m 간 판이 0.21배를 먹는 동안 옆으로 2 m 벗어난 판은 0.09배로 잘려 나간다 -
    /// 폭 한 칸짜리 띠가 배를 가로질러 죽는다. 띠가 반대쪽 외판까지 이어지면 HullStructure의
    /// 8방향 BFS가 다음 틱에 두 덩어리를 찾아 배를 가른다. **절단을 위한 코드는 없다.**
    ///
    /// 둘을 같은 값으로 두면 등방성으로 돌아가서 충돌 지점 주변이 둥글게 패기만 한다.
    ///
    /// 이 값으로 destroyer를 재보면(scratch 시뮬) **격벽 위**를 때렸을 때 판 한 장을 죽이는
    /// 피해의 5~6배에서 선체가 갈라진다. 격벽 사이를 때리면 아래가 방이라 충격이 내려갈
    /// 구조가 없어 절대 안 갈라지고, 상부구조 밑(26열)은 지붕이 다리를 놓아 역시 안 갈라진다.
    /// </summary>
    public const float RamConductAlong = 0.80f;

    /// <summary>충격축에서 1 m 벗어날 때 남는 몫. 낮을수록 절단선이 가늘고 날카롭다.</summary>
    public const float RamConductAcross = 0.30f;

    /// <summary>
    /// 진입 판 피해의 이 비율 아래로 떨어지면 거기서 멈춘다. 절대값이 아니라 비율이라
    /// 살짝 스친 충돌과 전속 충각이 같은 모양의 자국을 남긴다 - 크기만 다르다.
    ///
    /// 도달 거리를 정하는 것도 이 값이다: 0.08이면 축으로 11 m, 옆으로 2 m에서 끊긴다.
    /// </summary>
    public const float RamConductCutoff = 0.08f;

    /// <summary>
    /// 충격 하나가 건드릴 수 있는 판의 상한. 파편 연쇄 상한과 같은 이유다 - 없으면
    /// 튜닝을 한 번 잘못 만졌을 때 충돌 한 번이 함선을 통째로 지운다.
    /// </summary>
    public const int RamConductMaxPlates = 96;
}
