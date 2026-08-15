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
    /// ~200, about 1/9 of a default cell (Armor.cellHp 1800).
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
    public const int HeavyFragmentLifeTick = 120;     // 2 s - they do not fly forever
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
    /// 0.8 = 29/36.
    /// </summary>
    public const float PlateCollapseFraction = 0.8f;

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
}
