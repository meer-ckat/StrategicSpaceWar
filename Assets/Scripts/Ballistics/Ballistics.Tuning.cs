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
    public const float MinSpeed = 1f;        // m/s, 이보다 느리면 탄이 죽는다
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

    /// <summary>
    /// 반동 배수. **1이 실제 운동량이다** - 탄 질량 x 포구속도가 그대로 배를 민다.
    ///
    /// 등급 차이가 여기서 저절로 나온다. 구축함(100 t) 기준 한 발당 pd20이 0.005°/s,
    /// rail이 2.96°/s로 **595배**다. 근접방어는 안 보이고 주포는 느껴지고 레일건은
    /// 한 발마다 배를 걷어찬다 - 포마다 반동 값을 손으로 적었으면 이 비율이 안 나온다.
    ///
    /// 0으로 두면 기능이 통째로 사라진다. 실험이 개축이 아니라는 뜻이다.
    /// </summary>
    public const float RecoilScale = 1f;

    // --- 파편 ---
    public const float SpallEnergyFraction = 0.35f;
    public const float SpallEnergyPerFragment = 20f;
    public const float SpallMinEnergy = 5f;
    /// <summary>
    /// 관통 명중 하나가 낳는 파편 수. **평시 비용의 주항이다** - 교전 중 초당 수백 발이
    /// 명중하므로 여기 곱한 것이 그대로 틱 예산이 된다. 24에서 12로 내렸다: 프로파일에서
    /// 파면 하나가 self 147us였고, 그 대부분이 파편마다 도는 TraceWorld 질의였다.
    /// 눈에 보이는 것은 명중 지점의 불꽃 개수뿐이다.
    /// </summary>
    public const int SpallMaxCount = 12;
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

    /// <summary>
    /// 죽은 서브셀 하나가 낳는 파편 수. **프레임 그래프의 봉우리가 여기서 나온다** -
    /// 판 한 장이 서브셀 18개쯤을 잃으므로 판당 이 값의 18배고, 유폭이나 파단이 판 수십
    /// 장을 한 틱에 죽이면 그 곱이 통째로 한 프레임에 떨어진다. 6이면 판당 108발,
    /// 3이면 54발이다.
    ///
    /// 이 값은 "얼마나 부서지나"가 아니라 "부서지는 것을 몇 조각으로 세나"다 - 총
    /// 에너지는 CollapseEnergyFraction이 정하고 여기서 나눠 갖는다. 줄이면 조각이 굵어지고
    /// 개수가 준다.
    /// </summary>
    public const int CollapseFragmentCount = 3;

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

    /// <summary>
    /// 한 틱에 처리할 파편 수 상한. **총량이 아니라 스파이크를 눕히는 손잡이다** - 넘친
    /// 파편은 사라지지 않고 다음 틱으로 밀린다. 판 한 장이 무너지면 파편이 서브셀 18개 x
    /// CollapseFragmentCount 6 = 100발 넘게 나오고, 유폭이 판 수십 장을 한 틱에 죽이면
    /// 그 곱이 한 프레임에 통째로 떨어진다.
    ///
    /// 파편 한 발의 값이 판정 + 채널 + 서브셀 피해 적용으로 대략 15us라, 128이면 틱당
    /// 2ms 근처다. 2,000발짜리 폭발은 16틱에 걸쳐 들어오는데 SpallMaxLagTicks가 8이라
    /// 그 뒤로는 따라잡기로 넘어간다 - 상한이 아니라 완충이라는 뜻이다.
    ///
    /// **평시에 안 걸리는 값이어야 한다.** 256일 때 평시가 틱당 144발이라 예산이 한 번도
    /// 안 걸렸고, 그래서 눕히려던 봉우리에서만 늦게 걸렸다. SpallMaxCount를 반으로 줄여
    /// 평시가 72발이 된 지금은 128이 봉우리에만 걸린다.
    ///
    /// 올리면 즉발에 가까워지고 스파이크가 돌아온다. 내리면 평탄해지는 대신 큰 폭발의
    /// 피해가 눈에 띄게 번져 들어온다.
    /// </summary>
    /// <remarks>0이면 예산을 끈다 - 예전처럼 한 틱에 다 처리한다.</remarks>
    public const int MaxFragmentsPerPump = 128;

    /// <summary>
    /// 파편이 밀릴 수 있는 최대 틱 수. 이보다 오래 기다린 요청이 있으면 그 틱은 예산을
    /// 무시하고 따라잡는다.
    ///
    /// **빚은 개수가 아니라 시간으로 재야 한다.** 처음엔 "밀린 파편이 예산의 4배를
    /// 넘으면"으로 했는데, 큰 폭발 하나가 그 문턱을 즉시 넘겨서 정작 눕히려던 그 순간에
    /// 예산이 꺼졌다 - 안전장치가 기능을 무력화한 것이다. 시간으로 재면 폭발이 아무리
    /// 커도 이 틱 수에 걸쳐 퍼지고, 지속적인 초과 생산만 따라잡기로 넘어간다.
    ///
    /// 8이면 0.13초. 피해가 그만큼 늦게 들어오는 것은 눈에 거의 안 보이고, 프레임에는
    /// 확실히 보인다.
    /// </summary>
    public const int SpallMaxLagTicks = 8;

    // --- 모듈 직격 ---

    /// <summary>
    /// 탄이 모듈을 관통하며 놓고 가는 운동에너지의 몫. 모듈은 장갑이 아니라서 탄을 세우지
    /// 못한다 - 400mm 철갑탄이 엔진 케이싱에 막히지는 않는다. 대신 그만큼 느려진다.
    /// 0.15면 모듈 하나당 속도가 약 92%로 떨어져, 다섯 개를 뚫으면 65%쯤 남는다.
    /// </summary>
    public const float ModuleHitFraction = 0.15f;

    // --- 탄약 ---

    /// <summary>
    /// 탄약 한 칸이 담는 질량(kg). **한 발이 한 칸이 아니다** - 20mm(0.5 kg)와 305mm(400 kg)를
    /// 같은 1로 세면 CIWS 한 문이 주포와 같은 속도로 탄약고를 비운다. frigate로 재면 전 포
    /// 동시 사격이 초당 423발인데 적재가 1,760발이라 4.2초에 바닥났다.
    ///
    /// 0.5인 이유는 제일 작은 탄(20mm 0.5 kg)이 정확히 한 칸이어서다 - 그 아래로 내리면
    /// 소구경이 소수점이 되고, 올리면 20mm와 22mm가 같은 값이 된다.
    /// </summary>
    public const float AmmoUnitMass = 0.5f;

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
    /// **1이 물리적으로 맞는 값이다.** Punch는 힘 단계에서, 즉 Physics2D.Simulate보다
    /// 먼저 돈다. 이번 틱에 지나갈 거리만큼 지우면 솔버가 접촉을 잡을 때 이미 치워져
    /// 있으므로 앞질러 볼 이유가 없다.
    ///
    /// 2였던 적이 있다. 얇은 잔해가 겹친 자리에서 남은 접촉이 배를 튕겨내는 것을 막으려고
    /// 한 틱 먼저 값을 치르게 한 것인데, **미는 코드가 없던 시절의 우회로였다.** 지금은
    /// Punch가 운동량을 직접 넘겨주므로 그 증상 자체가 없다. 그리고 대가가 컸다 - 120 m/s면
    /// 판이 **4 m 앞에서 미리 사라져서** 배가 닿기도 전에 구멍이 나고, 눈에는 높은 핑으로
    /// 보인다. 실제로 그렇게 보였다.
    ///
    /// 캐스트 거리와 판별 게이트가 **같은 값을 써야 한다.** 캐스트만 늘리면 게이트가
    /// 늘어난 만큼을 도로 걸러내서 아무것도 안 바뀐다.
    /// </summary>
    public const float RamLookahead = 1f;

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
    /// 실려 가는 몸(상대속도 &lt; RamMinSpeed, 회전 없음)만 앞에 있을 때 충각을 몇 틱에 한 번 돌리나.
    ///
    /// 선체에 박힌 운석을 밀고 가면 상대속도가 0이라 부술 에너지는 없는데, 밀고 있으니(pushing) 게이트가
    /// 열려서 Sweep(판마다 Cast)과 Conduct(96장 x 2)가 **매 틱** 돌았다 - 프로파일러에서 그 둘이 위였다.
    /// 압착은 힘 x 시간이라 6틱에 한 번 6배로 넣으면 초당 합이 같고, 항복 문턱도 같이 6배라 결과가 같다.
    /// 값이 곧 압착의 시간 해상도다(6 = 0.1 s).
    /// </summary>
    public const int RamCarriedEvery = 6;

    /// <summary>
    /// 충각 피해의 질량 무릎(kg). 때리는 몸의 피해가 `m / (m + 이 값)`으로 깎인다 -
    /// 이 질량에서 절반, 훨씬 무거우면 1, 훨씬 가벼우면 질량에 비례해 0으로 떨어진다.
    ///
    /// **운동에너지만으로는 작은 잔해가 너무 아프다.** 판 세 장짜리 조각(약 1.3 t)도
    /// 상대속도가 붙으면 KE가 수백 kJ이고, Reaction이 잔해->배 방향에서 0.99라 그 에너지가
    /// 거의 전부 판으로 들어간다. 물리로는 맞는 그림인데(가벼운 쪽이 튕겨나가며 변형
    /// 에너지를 다 낸다) 게임에서는 파편 구름을 지나갈 때마다 외판이 갈려 나간다.
    ///
    /// 이야기로는 **강성 근사**다: 가벼운 조각은 제 운동에너지를 상대를 파는 데 못 쓰고
    /// 자기가 찌그러지며 흩어진다. DebrisHpFraction(잔해가 먼저 죽는다)과 같은 방향의
    /// 보정이고, 그쪽은 접촉의 수명을 줄이고 이쪽은 접촉당 피해를 줄인다.
    ///
    /// 10 t 기준: 판 3장 조각(1.3 t) 12%, scout(32 t) 76%, destroyer(137 t) 93%.
    /// 함선끼리의 충각은 사실상 안 변하고 잔해만 무뎌진다.
    /// </summary>
    public const float RamMassKnee = 10000f;

    /// <summary>
    /// 압착(CRUSH) 피해 계수. **충돌과 노브를 나눠 쓰지 않는다** - 둘은 이제 개념이
    /// 다르고(충돌은 상대속도², 압착은 저항받는 추력), 노브를 공유하면 한쪽을 맞추는
    /// 순간 다른 쪽이 따라 움직여서 분리한 값이 사라진다.
    ///
    /// 먹는 값의 단위 자체가 다르다: 충돌은 J 비슷한 것을, 압착은 `힘 x dt` = N·s
    /// 비슷한 것을 먹는다. 그래서 RamDamageFraction에서 물려받을 수 있는 숫자가 없고,
    /// **원하는 행동에서 거꾸로 잡았다**:
    ///
    ///   자유로운 물체는 부서지기 전에 밀려나고, 저항하는 물체는 오래 밀면 찌그러진다.
    ///
    /// 초당 피해 = 추력 x 이 값 x Reaction / 접촉판수. 구축함 추력 8.4 MN 기준으로
    /// 900 kg 운석(react 0.003)은 초당 3도 안 되게 먹으면서 31 m/s²로 밀려나 접촉이
    /// 끊기고, 낀 배(react 1.0, 접촉 5장)는 초당 168이라 300 HP 판이 1.8초에 무너진다.
    /// </summary>
    public const float RamPressureDamageScale = 1e-4f;

    /// <summary>
    /// 압착 항복 문턱. 판이 한 틱에 **그냥 견디는** 몫을 자기 총 체력의 비율로 준다.
    ///
    /// **이게 없으면 살짝 대고만 있어도 영원히 갉힌다.** 실제 재료는 항복 응력 아래에서
    /// 영구 변형이 0이다 - 우주선으로 운석을 밀어 옮기는 그림이 성립하는 이유가 그것이고,
    /// 문턱이 없으면 아무리 약한 접촉도 시간만 주면 선체를 뚫는다.
    ///
    /// 0.002면 300 HP 판이 초당 36까지는 공짜다. 구축함(8.4 MN)이 900 kg 운석을 밀 때
    /// 판당 초당 3도 안 되므로 **아무 일도 안 일어나고 운석만 밀려난다.** 낀 배(react 1.0)는
    /// 초당 168이라 문턱을 훨씬 넘어 계속 찌그러진다. 그 사이가 무거운 것을 미는 구간이다.
    ///
    /// **판 체력에 비례하는 것이 요점이다** - 두꺼운 장갑이 더 버티는 것이 공짜로 나오고,
    /// 상수 하나로 재료마다 다른 문턱을 안 적어도 된다.
    /// </summary>
    public const float RamCrushYield = 0.002f;

    /// <summary>
    /// 함선에서 떨어져 나온 조각이 남아 있는 틱 수. 60틱/초라 3600이면 60초.
    /// 배치해 둔 운석·폐위성은 Hulk.lifeTick을 0으로 두어 이 규칙에서 빠진다.
    /// </summary>
    public const int DebrisLifeTick = 3600;

    /// <summary>
    /// 이 판 수 이하의 조각은 시각 전용 잔해다 - 구조·충각 스크립트 없이, 콜라이더 없이,
    /// 관성으로만 날아가다 사라진다. 못 쏘고 못 갈고 배를 못 민다. 그라인딩 잔해의
    /// 대부분이 한 장짜리라 이 문턱 하나가 잔해 구름의 물리·틱 비용을 정한다. 0이면 끔.
    /// </summary>
    public const int VisualDebrisMaxPlates = 1;

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

    // --- 전장 경계 ---

    /// <summary>
    /// 전투 중 플레이어가 전장에서 벗어날 수 있는 거리(m).
    ///
    /// **로그라이크의 전투는 끝나야 다음이 있다.** 벗어나서 흘러가 버리면 그 판이
    /// 안 끝나고, 승리도 노획도 다음 구역도 없다. 지금은 Battle.Stranded가 5초 뒤에
    /// 패배로 끊는데, 그건 안전장치지 규칙이 아니다.
    ///
    /// FightDistance가 120~240이므로 1200이면 교전 거리의 다섯 배다 - 우회·이탈·재접근이
    /// 전부 안에서 되고, "도망쳐서 사라지기"만 막힌다.
    /// </summary>
    public const float BattleZoneRadius = 1200f;

    /// <summary>
    /// 경계를 넘었을 때 되미는 가속(m/s^2). 벽이 아니라 조류다.
    ///
    /// 딱딱한 벽으로 만들면 두 가지가 틀린다: 부딪히는 순간 속도가 사라져서 물리가
    /// 거짓말을 하고, 충각으로 튕겨 나간 배가 벽에 박혀 못 돌아온다. 넘은 만큼에 비례해
    /// 미는 힘이면 멀리 갈수록 세지고, 스스로 되돌아온다.
    /// </summary>
    public const float BattleZonePull = 12f;

    // --- 유폭 ---

    /// <summary>
    /// 폭심에서 1 m 멀어질 때 남는 몫. 충각과 달리 방향이 없다 - 같은 값을 두 축에 다 준다.
    ///
    /// 반경은 이 값과 아래 BlastFloor가 **함께** 정한다(BlastRadiusFor). 세기를 올리면 원도
    /// 커지는데 로그로 자란다 - 10배가 한 배 반이다.
    ///
    /// **0.65에서 내려왔다(반경 6.95 m -> 3.60 m).** 폭발을 국소 집중형으로 바꾸는 튜닝이다 -
    /// 넓게 얇게 훑던 것이 좁게 깊게 판다.
    ///
    /// **이 값은 전역이라 유폭 전부가 같이 바뀐다.** 탄약고·원자로도 같은 곡선을 쓰므로
    /// CriticalModule의 주석에 적힌 destroyer 실측(1600 피해가 어디서 터지면 어떻게 갈리는지)은
    /// 이제 낡았다. 미사일만 따로 주려면 falloff를 def 값으로 만들어야 하는데, 그러면
    /// Detonate/Radiate/DamageRear/BlastRadius 넷에 값을 실어야 한다 - 튜닝 한 번에 그건 과하다.
    /// 시타델 거동이 이상해지면 그때 def로 내린다.
    /// </summary>
    public const float BlastFalloff = 0.435f;

    /// <summary>
    /// 후면 칸 하나의 체력. **판보다 얇다** - 후면은 반대편 외판이고, 앞판을 뚫고 들어온
    /// 것이 거기까지 닿았다면 이미 많이 깎인 뒤다.
    ///
    /// **평소에는 안 쓰인다.** 후면 체력은 그 배가 실제로 입은 판의 평균에서 나온다.
    /// 이 값은 판을 하나도 못 찾았을 때의 폴백이다 - 씬에 손으로 지어놓고 아직 판을 안 단
    /// 배 같은 것.
    /// </summary>
    public const float RearHp = 180f;

    /// <summary>
    /// 후면 = 앞판 평균 x 이 값.
    ///
    /// 1이면 뒷벽이 앞판과 똑같이 튼튼하다. 물리적으로는 그것이 맞지만(같은 외판이다)
    /// 그러면 앞을 뚫고 온 탄이 똑같은 벽을 또 만나서 완전관통이 사실상 안 나온다.
    /// 뚫고 지나가는 그림이 이 게임의 목적이라 얇게 잡는다.
    /// </summary>
    public const float RearHpFactor = 1f;

    /// <summary>
    /// 잔해가 질량을 나눠 가질 때 후면 칸 하나를 판 몇 장으로 세나.
    ///
    /// **배 총질량에는 안 들어간다.** massPerPlate가 이미 배 전체를 판 수로 나눈 값이라
    /// 후면도 거기 녹아 있다. 이 값은 오로지 "이 조각이 전체의 몇 퍼센트인가"를 정확히
    /// 하려는 것이고, 그래서 총합이 안 변한다.
    ///
    /// 후면 칸은 판보다 훨씬 많다(destroyer 240 대 1008). 1로 두면 후면이 배분을 지배해서
    /// 판을 잔뜩 실은 조각이 오히려 가벼워진다.
    /// </summary>
    public const float RearMassShare = 0.2f;

    /// <summary>
    /// 선체에서 뜯겨 나간 조각의 판이 들고 가는 구조 비율. **뜯긴 판은 온전할 수 없다** -
    /// 이 값이 1이면 조각이 본체와 똑같이 단단해서, 판 세 장짜리 파편이 선체에 붙어
    /// 매 틱 갉는 동안 자기는 하나도 안 상한다. 충각은 매 틱 도는 규칙이라 10 m/s짜리
    /// 접촉도 붙어만 있으면 결국 뚫는다 - 한 방이 세서가 아니라 갉는 쪽이 안 죽어서다.
    ///
    /// 낮출수록 잔해가 빨리 부서져 접촉이 스스로 끝난다. 0에 가까우면 뜯기는 순간
    /// 조각이 증발하고, 1이면 지금의 그 증상으로 돌아온다.
    /// </summary>
    public const float DebrisHpFraction = 0.35f;

    /// <summary>
    /// 판이 통째로 받는 충격(충각·유폭) 중 그 판에 볼트로 붙은 모듈이 같이 받는 몫.
    ///
    /// **모듈은 스윕에 안 잡힌다.** `RamImpact.Punch`도 `Radiate`도 Armor만 고르므로,
    /// 이 값이 없으면 포탑을 정면으로 들이받아도 포탑은 흠집 하나 안 난다 - 판만 부서지고
    /// 그 위의 주포는 멀쩡히 계속 쏜다.
    ///
    /// 1이 아닌 이유: 모듈 체력(40쯤)이 판(400쯤)보다 훨씬 작아서, 같은 값을 주면 스치기만
    /// 해도 전부 즉사한다. 큰 충각은 죽이고 작은 접촉은 안 죽이는 선이다.
    /// </summary>
    public const float ModuleShockFraction = 0.15f;

    /// <summary>
    /// 후면의 유효 RHA = 그 자리 판 RHA x 이 값.
    ///
    /// 체력을 그 자리 판에서 뽑는 것(<c>HullStructure.RearHealthAt</c>)과 같은 규칙이다.
    /// 후면은 반대편 외판이고, 앞을 뚫고 온 것이 거기까지 닿았다면 이미 많이 깎인 뒤라
    /// 얇게 잡는다. 1로 두면 완전관통이 사실상 안 나온다.
    /// </summary>
    public const float RearRhaFactor = 0.7f;

    /// <summary>
    /// 선체가 깊이 방향으로 몇 m인가. **이 게임에 없는 축의 유일한 숫자다.**
    ///
    /// 함선에는 Z가 없다(탄·파편·승무원·모듈만 가상 Z를 갖는다). 그래도 "앞으로 들어와
    /// 뒤로 나간다"를 말하려면 두께가 하나는 있어야 하고, 그것이 이 값이다.
    ///
    /// 배 크기에서 파생시키지 않는다 - 사이드뷰의 세로 칸 수는 높이지 두께가 아니라서,
    /// 인간형 함선이 구축함보다 두꺼워지는 식으로 뜻이 어긋난다. 없는 축을 억지로
    /// 파생시키면 그 숫자가 뭘 뜻하는지 아무도 설명 못 하게 된다.
    ///
    /// 배마다 달라야 해지면 그때 ShipDef 필드가 된다. massPerPlate가 걸어온 길이다.
    /// </summary>
    public const float HullDepth = 6f;

    /// <summary>
    /// 폭발이 멈추는 **절대** 하한(HP 단위). 예전에는 폭심 피해의 비율(0.05)이었는데, 비율은
    /// damage가 분자와 분모에 같이 있어서 **모든 폭발의 반경이 똑같아진다** - 105mm 작약
    /// 90짜리와 탄약고 3,200짜리가 같은 원이었다. 절대값으로 두면 반경이 ln(damage)로 자란다:
    /// 세기가 10배면 원이 한 배 반.
    ///
    /// 40인 이유는 그 아래가 아무것도 안 죽이기 때문이다 - mk6 판 1 m²가 400이고 서브셀
    /// 하나가 그 1/36(11)이라, 40이면 서브셀 서너 개를 긁는 자리에서 끊는다.
    /// </summary>
    public const float BlastFloor = 40f;

    /// <summary>
    /// 판 상한. 파편 연쇄 상한과 같은 이유다.
    ///
    /// **BlastFalloff를 올리면 이것도 같이 봐야 한다.** 0.8이면 반경 13.4 m라 원 안에
    /// 566칸이 들어가서, 128에서 끊으면 큰 폭발이 조용히 잘린다. 구축함이 241판이므로
    /// 256이면 한 척을 통째로 덮고도 남는다.
    /// </summary>
    public const int BlastMaxPlates = 256;

    /// <summary>
    /// 세기 damage짜리 폭발이 닿는 반경(m). **손으로 적는 값이 아니다** -
    /// damage x BlastFalloff^r == BlastFloor를 그대로 푼 것이라 튜닝을 따라온다.
    /// 하한 이하의 폭발은 반경 0이다(아무것도 안 죽이는 폭발은 원도 없다).
    ///
    /// 0.435 / 40이면: 작약 90 = 0.97 m, 305mm 840 = 3.7 m, 원자로 1600 = 4.4 m,
    /// 탄약고 3200 = 5.3 m, Sunkiller 10000 = 6.6 m.
    /// </summary>
    public static float BlastRadiusFor(float damage) =>
        damage <= BlastFloor ? 0f : Mathf.Log(BlastFloor / damage) / Mathf.Log(BlastFalloff);

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
    public const float HeatFromDamage = 0.05f;

    /// <summary>
    /// **이웃 판이 죽어서 새로 바깥에 드러났을 때 오르는 열.** 피해 열보다 훨씬 크다 -
    /// 방금 찢어진 단면이 오래 두들겨 맞은 판보다 밝아야 "언제 부서졌나"가 색으로 읽힌다.
    /// 1을 넘겨 잡는 이유: 최대치에 확실히 붙고, 식는 동안 한동안 흰색을 유지한다.
    /// </summary>
    public const float HeatFromExposure = 1.6f;

    /// <summary>
    /// 전도(Conduct)로 들어온 피해가 판을 달구는 몫. **직격의 1/8이다.**
    ///
    /// 충격 전도는 구조가 갈라지는 것이지 타는 것이 아니다 - 열은 탄착점에서 나오지
    /// 10 m 떨어진 격벽에서 나오지 않는다. 그런데 충각 한 번이 판 열 장어치를 축방향
    /// 11 m까지 퍼뜨리므로(구축함 30 m/s면 spent 약 2,972 = 판 10장), 직격과 같은
    /// 눈금으로 달구면 선체 절반이 한꺼번에 하얗게 되고 **열이 전하려던 정보(언제
    /// 상했나)가 포화로 사라진다.** 판은 56%만 상해도 열이 1.0에 닿는다.
    ///
    /// 0이 아니라 1/8인 이유: 전도로 죽은 판도 뜨겁긴 해야 한다. 완전히 0이면 절단선
    /// 안쪽이 부자연스럽게 차갑다. 절단면 자체는 HeatFromExposure가 따로 낸다 - 죽은
    /// 판의 이웃이 받는 그 길은 안 건드렸고, 그게 원래 열의 제일 큰 원천이다.
    /// </summary>
    public const float ConductHeatScale = 0.125f;

    /// <summary>열이 절반으로 식는 데 걸리는 시간(초). 2~5초 사이가 보기 좋다.</summary>
    public const float HeatHalfLife = 5f;

    // 모듈 배치(ModulePlacement). 셋 다 "약간 겹친 건 되고 파묻힌 건 안 됨"의 손잡이다.

    /// <summary>실내 모듈이 판 하나의 넓이를 이 비율 넘게 덮으면 파묻힌 것.</summary>
    public const float ModulePlateCoverMax = 0.30f;

    /// <summary>외장 모듈(포·엔진)이 판에 닿았다고 볼 거리(m). 이 안의 판이 마운트가 된다.</summary>
    public const float ModuleMountReach = 0.5f;

    /// <summary>모듈끼리 겹쳐도 되는 넓이(m²). 겹친 둘은 한 탄에 같이 맞으므로 사실상 금지.</summary>
    public const float ModuleOverlapMax = 0.1f;

    // ---- 전력망 (Docs/Electrical-Design.md §2.1) ----
    public const float PowerNominal = 320f;        // 소비자가 정상으로 보는 전압(L-N)
    public const float PowerBrownout = 0.5f;       // 이 비율 밑이면 NO POWER. 사이는 저하
    public const float WireOhmPerMetre = 0.002f;   // 케이블 저항
    public const float WireHeatCapacity = 400f;    // J/K per m
    public const float WireCoolPerSecond = 0.05f;  // 초당 (T-주변)×이만큼 식는다
    public const float WireBurnKelvin = 400f;      // 이 온도를 넘으면 그 구간이 탄다 = 끊긴다
    public const int PowerInterval = 6;            // 틱. 10 Hz
    public const float TurretMotorTau = 0.25f;     // s. 포탑 모터가 목표 속도에 붙는 시정수. 기동 전류가 이만큼 지속된다
    public const float TurretMotorLoad = 0.3f;     // 정격 속도에서 마찰이 먹는 전류 비율. 기동 = 정격(Vnom/Ra), 회전 중 = 이만큼

    // ---- 정지 회로도 (목업 2026-09-19에서 오너가 고른 값. 모드 B: 텍스처 끄고 선만) ----
    public const float PauseDimAlpha = 1f;         // 덮개. 1이면 배경·텍스처가 완전히 사라진다
    public const float SchematicEdgeAlpha = 0.37f; // 판 윤곽선
    public const float SchematicRoomAlpha = 1f;    // 실내 채움(RoomView 색의 알파)
    public const float WireViewWidth = 0.21f;      // m
    public const int ReadoutFontSize = 15;         // 속보기 라벨 px (배율 밖)
    public const float SchematicGridAlpha = 0.18f; // 1 m 격자선
    public const float ReadoutBackAlpha = 1f;      // 라벨 뒤판
    public const float SchematicBlendSeconds = 0.5f; // 들어가고 나오는 전환(건조 와이어프레임 쓸기)
    public const float PauseCameraReturnSeconds = 0.8f; // 정지 해제 뒤 카메라가 배로 돌아오는 시간. 0이면 순간이동
    public const float PauseZoomMin = 3f;          // 반높이 m. 3이면 1 m 칸이 화면 1/6 - 서브셀이 보인다
    public const float PauseZoomMax = 40f;
    public const float PauseCameraMargin = 10f;    // 정지 카메라가 플레이어 배 격자 밖으로 나갈 수 있는 거리

    /// <summary>
    /// 들판 출구에서 워프하는 데 드는 Δv(m/s). newship 만탱크(≈26,000 m/s)의 1/4쯤 - 들판 횡단이
    /// 1,000~2,000이라 이게 없으면 탱크가 항해 예산이 아니라 장식이다. 못 채우면 출항이 안 된다.
    /// </summary>
    public const float WarpDeltaV = 6000f;

    // 후면 열에 상수를 따로 두지 않는다. 후면이 달궈지는 두 사건이 앞판의 그것과 같은
    // 사건이라 같은 값을 쓴다 - 맞으면 HeatFromDamage, 뚫리면 HeatFromExposure.
    //
    // 한때 "살아 있는 판이 매 틱 자기 뒤를 데운다"는 전도 경로가 있었고 상수도 둘 더
    // 있었는데, 내 배의 후면은 판 뒤(sortingOrder -10)에 어둡게 깔려서 판이 성한 자리는
    // 화면에 아무것도 안 나온다. 안 보이는 것을 매 틱 계산하고 있었다.
}
