using UnityEngine;

/// <summary>
/// 손잡이만 있는 파일이다. 물리 법칙이 아니라 튜닝값이고, 계산은 여기서 안 한다 -
/// 수식은 Ballistics.Formula.cs와 Ballistics.SubCell.cs에 있다.
///
/// 예외는 다른 손잡이에서 파생되는 손잡이 하나(BlastRadius). 두 벌로 적으면 어긋나서 여기 있다.
/// </summary>
public static partial class Ballistics
{
    // --- 기하 / 루프 ---
    public const float Epsilon = 0.005f;      // m, 명중 후 밀어내는 거리
    public const float EdgeEpsilon = 0.01f;   // m, 동시 접촉으로 볼 창
    public const float MinSpeed = 20f;        // m/s, 이보다 느리면 탄이 죽는다
    public const int MaxHitsPerTick = 8;      // 모서리 무한 도탄 방지
    public const float MinCos = 0.15f;        // 유효 RHA 상한 약 6.7배

    /// <summary>
    /// 모서리 폴백 문턱. 콜라이더 이음매를 맞으면 Physics2D가 탄의 진행 방향과 나란한 법선을
    /// 준다(dot이 0 근처) - 실제 면이 아니라 이음매 쓰레기값이다. 곧이곧대로 읽으면 정면
    /// 사격이 공중에서 도탄하고, 버리면 벽을 통과한다.
    ///
    /// 접촉면 중 이만큼도 탄을 마주보는 것이 없으면 정면 입사로 판정한다. 공짜 각도 보너스도,
    /// 공짜 도탄도, 공짜 통과도 없다. 0.02는 약 88.9도라 도탄 상한(+10) 바깥이고, 그래서
    /// 진짜 스치는 명중은 하나도 안 건드린다.
    /// </summary>
    public const float MinFacing = 0.02f;

    // --- 탄 손상 ---
    public const float DeformSeverity = 1.0f;
    public const float ShatterSeverity = 1.5f;
    public const float IntactDecay = 0.95f;
    public const float DeformDecay = 0.70f;
    public const float ShatterDecay = 0.25f;

    // --- 도탄 ---
    public const float BaseCritAngle = 70f;      // 법선에서 몇 도
    public const float OvermatchBonus = 10f;     // 총 상한. 1~2배 오버매치 구간에서 선형
    public const float RicochetTangent = 0.85f;
    public const float RicochetNormal = 0.15f;
    public const float RicochetArmorDamage = 0.05f;

    // --- 장갑 손상 ---
    public const float AttackRatioFloor = 0.2f;

    /// <summary>
    /// 줄(J) → 장갑 HP. 순수 보정값이다. 5 kg 탄이 900 m/s로 정면에서 막히면 약 200.
    /// hpPerSquareMetre는 m²당이므로 콜라이더 넓이를 곱한 값과 비교할 것.
    ///
    /// **이 값이 낳는 진폭에 주의.** 장갑 피해는 (유효RHA / 관통력)²에 비례한다. 판을 3배로
    /// 오버매치하는 탄은 에너지의 11%만 남기고 깨끗한 구멍을 뚫고 지나가고, 간신히 뚫는 탄은
    /// 거의 전부를 쏟는다. "장갑이 안 닳는다"는 대개 이 숫자가 틀린 게 아니라 포가 너무 센 것이다.
    /// </summary>
    public const float DamageScale = 1e-4f;

    // --- 파편 ---
    public const float SpallEnergyFraction = 0.35f;
    public const float SpallEnergyPerFragment = 20f;
    public const float SpallMinEnergy = 5f;
    public const int SpallMaxCount = 24;
    public const int HeavyFragmentCount = 4;

    // 부서진 탄의 잔해만 실체 파편으로 승격할 값어치가 있다. 한 번에 24개가 아니라 4개고,
    // 배를 가로지를 만큼 크다 - SpallRangeMax에 묶인 한 틱짜리 레이로는 못 하는 일이다.
    public const float HeavyFragmentSlowest = 0.7f;   // 남은 속도의 몇 배
    public const int HeavyFragmentLifeTick = 60;      // 2초. 영원히 날지 않는다
    public const int MaxFragmentGeneration = 1;       // 파편은 파편을 안 낳는다
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

    // --- 구조 붕괴 ---

    /// <summary>
    /// HP가 0이 된 서브셀은 저항을 그만둔 게 아니라 **재료가 뜯겨 나간 것이고, 그건 어딘가로
    /// 간다.** 그 칸의 구조 예산 중 파편으로 떠나는 몫.
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
    /// 무너지는 판의 잔해는 관통 뒤의 파편과 달리 선호 방향이 없다. 사방으로 간다.
    /// </summary>
    public const float CollapseSpread = 180f;

    /// <summary>
    /// 서브셀을 죽인 파편이 또 파편을 낳는다. 한 세대까지는 판이 무너지는 것처럼 읽히고,
    /// 상한이 없으면 한 발이 함선을 지운다.
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
    public const float RamDamageFraction = 0.5f;

    /// <summary>
    /// 이 아래는 접촉이지 충각이 아니다(m/s). 없으면 나란히 떠 있기만 해도 장갑이 갈린다.
    ///
    /// 예전에는 접촉 충격량(N·s) 기준이었는데, 그 값은 **솔버가 내놓는 출력**이라 상대 질량에
    /// 따라 널뛰었다. 속도는 배가 스스로 아는 값이다.
    ///
    /// 5 m/s면 구축함 기준 예산이 120 - 장갑 한 장(300)도 못 뚫는다. 즉 이 아래는 아무것도
    /// 못 부수는 속도다.
    /// </summary>
    public const float RamMinSpeed = 5f;

    /// <summary>
    /// 충각 스윕이 이번 틱 이동거리보다 이만큼(m) 더 나간다. 솔버가 접촉을 잡기 직전에
    /// 판을 지우려는 여유분이다.
    ///
    /// **RamMinSpeed x TickDeltaTime보다 확실히 작아야 한다** (지금 5 x 1/60 = 0.083 m).
    /// 크면 느린 배가 자기가 가지도 않은 곳을 앞질러 부순다 - 0.2였을 때 5 m/s짜리 배가
    /// 실제 이동거리의 3배를 쓸었다. Unity의 기본 접촉 여유(0.01)보다는 커야 한다.
    /// </summary>
    public const float RamSkin = 0.04f;

    /// <summary>
    /// 충각이 몇 틱 앞을 미리 쓰는가. 1이면 딱 이번 틱 이동거리다.
    ///
    /// **1이면 솔버와 동시에 도착한다.** 판을 지우기 시작하는 그 틱에 솔버도 접촉을 잡으므로,
    /// 얇은 것이 여러 개 겹쳐 있으면 한 틱에 다 못 치우고 남은 것들이 동시 접촉으로 배를
    /// 밀어낸다. 거울이 잔해 구름으로 흩어진 자리에서 이게 나온다 - 빠를수록 한 틱에
    /// 만나는 수가 많아지니 확률이 올라간다.
    ///
    /// 2면 한 틱 먼저 값을 치르기 시작한다. 앞질러 부수는 것이 아니다 - 실제로 지워지는
    /// 것은 여전히 예산이 닿는 만큼뿐이고, <see cref="RamSpendPerTick"/>이 한 틱 지출을
    /// 막고 있다. 늘어나는 것은 "부술 것을 언제부터 보기 시작하는가"뿐이다.
    ///
    /// 캐스트 거리와 판별 게이트가 **같은 값을 써야 한다.** 캐스트만 늘리면 게이트가
    /// 늘어난 만큼을 도로 걸러내서 아무것도 안 바뀐다.
    /// </summary>
    public const float RamLookahead = 2f;

    /// <summary>
    /// 한 틱에 쏟을 수 있는 운동에너지의 최대 몫.
    ///
    /// **이게 없으면 못 뚫는 벽에서 속도가 한 틱에 0이 된다.** 남은 예산을 안 죽는 판에도
    /// 전부 치르기 때문인데, 그러면 `sqrt(v² - v²) = 0`이다. 우리 산수가 배를 세우는 것이라
    /// 솔버가 접촉을 잡을 기회도 회전을 만들 기회도 없어진다.
    ///
    /// 물리적으로도 1/60초에 운동에너지 전부를 벽에 넣을 수는 없다. 0.5면 한 틱에 속도가
    /// 최소 71%(=sqrt(0.5))는 남고, 벽을 갉으면서 여러 틱에 걸쳐 느려진다. 그 사이에
    /// 살아남은 판이 솔버를 막아서 **정지와 회전은 솔버가 한다.**
    ///
    /// 뚫리는 재료(유리·거울)는 애초에 이 상한 근처도 안 가므로 아무 영향이 없다.
    /// </summary>
    public const float RamSpendPerTick = 0.5f;

    /// <summary>
    /// 함선에서 떨어져 나온 조각이 남아 있는 틱 수. 60틱/초라 3600이면 60초.
    /// 배치해 둔 운석·폐위성은 Hulk.lifeTick을 0으로 두어 이 규칙에서 빠진다.
    /// </summary>
    public const int DebrisLifeTick = 3600;

    /// <summary>
    /// 갓 떨어져 나온 조각의 속도 상한(m/s). MaxSpallDepth와 같은 종류의 안전장치다.
    ///
    /// Breakaway는 접선속도를 `회전축에서의 거리 × 각속도`로 물려준다. 공식은 맞는데,
    /// **거울 고리는 반지름이 120 m라 각속도가 조금만 붙어도 값이 폭발한다** - 초당 150°면
    /// 테두리 조각이 314 m/s로 튀어나간다. 조각이 또 갈라지면 그 값을 또 물려받아 커진다.
    ///
    /// 함선이 25 m/s로 다니므로 60이면 충분히 극적이고, 포탄(900 m/s)과는 확실히 다른 층이다.
    /// </summary>
    public const float DebrisMaxSpeed = 60f;

    /// <summary>조각 각속도 상한(도/초). 위와 같은 이유 - 이게 다음 조각의 속도를 정한다.</summary>
    public const float DebrisMaxSpin = 360f;

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

    // --- 유폭 ---

    /// <summary>
    /// 폭심에서 1 m 멀어질 때 남는 몫. 충각과 달리 방향이 없다 - 같은 값을 두 축에 다 준다.
    ///
    /// **반경을 정하는 것은 blastDamage가 아니라 아래 BlastCutoff다.** 0.65에 컷오프 0.05면
    /// 약 7 m에서 끊긴다. 세기를 올리면 그 원 안의 판이 더 확실히 죽을 뿐 원이 커지지 않는다.
    /// </summary>
    public const float BlastFalloff = 0.65f;

    /// <summary>폭심 피해의 이 비율 아래로 떨어지면 멈춘다. 곧 폭발 반경.</summary>
    public const float BlastCutoff = 0.05f;

    /// <summary>
    /// 판 상한. 파편 연쇄 상한과 같은 이유다.
    ///
    /// **BlastFalloff를 올리면 이것도 같이 봐야 한다.** 0.8이면 반경 13.4 m라 원 안에
    /// 566칸이 들어가서, 128에서 끊으면 큰 폭발이 조용히 잘린다. 구축함이 241판이므로
    /// 256이면 한 척을 통째로 덮고도 남는다.
    /// </summary>
    public const int BlastMaxPlates = 256;

    /// <summary>
    /// 자유 공간을 건너가는 유폭의 반경(m). **손으로 적는 값이 아니다** -
    /// BlastFalloff^r == BlastCutoff가 되는 지점을 그대로 푼 것이라, 위 둘을 만지면 따라온다.
    /// 두 벌로 적어두면 튜닝을 바꾼 날 원과 감쇠가 조용히 어긋난다.
    ///
    /// 0.65 / 0.05면 약 6.95 m.
    /// </summary>
    public static readonly float BlastRadius =
        Mathf.Log(BlastCutoff) / Mathf.Log(BlastFalloff);

    /// <summary>폭심 피해 중 파편으로 날아가는 몫. 판을 뚫고 안쪽 모듈까지 가는 것이 이 몫이다.</summary>
    public const float BlastFragmentFraction = 0.25f;

    /// <summary>
    /// 유폭이 유폭을 부르는 깊이 상한. 탄약고 셋을 나란히 둔 배에서 한 발이 전부를 지우는
    /// 것을 막는다. MaxSpallDepth와 같은 종류의 안전장치다.
    /// </summary>
    public const int MaxDetonationChain = 2;

    // --- 적열 (그림 전용) ---
    //
    // 여기 세 값은 시뮬레이션이 한 번도 안 읽는다. 판의 HP와 따로 도는 시각 전용 값이고,
    // 이걸 전부 0으로 두어도 판정은 글자 하나 안 바뀐다.

    /// <summary>
    /// 서브셀 하나를 통째로 날릴 만큼 맞았을 때 오르는 열. 맞은 만큼 달아오른다.
    /// </summary>
    public const float HeatFromDamage = 0.35f;

    /// <summary>
    /// **이웃 판이 죽어서 새로 바깥에 드러났을 때 오르는 열.** 피해 열보다 훨씬 크다 -
    /// 방금 찢어진 단면이 오래 두들겨 맞은 판보다 밝아야 "언제 부서졌나"가 색으로 읽힌다.
    /// 1을 넘겨 잡는 이유: 최대치에 확실히 붙고, 식는 동안 한동안 흰색을 유지한다.
    /// </summary>
    public const float HeatFromExposure = 1.6f;

    /// <summary>열이 절반으로 식는 데 걸리는 시간(초). 2~5초 사이가 보기 좋다.</summary>
    public const float HeatHalfLife = 1.4f;
}
