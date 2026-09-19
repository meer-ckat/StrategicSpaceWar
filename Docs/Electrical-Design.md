# 전기 계통 구현 설계 (M2~M4)

오너 설계안 v2(2026-09-18, 채팅)를 코드로 옮기는 문서다. v2의 "확정" 항목은 여기서 다시 논의하지 않는다.
이 문서는 **어느 파일에 무엇이 들어가고 틱마다 무엇이 도는가**만 적는다.

## 0. 지금 있는 것 (커밋 07f6e64)

- `ShipDef.wires` — 폴리라인(칸 좌표). 페인터 전력망 모드가 긋는다.
- `Ship.Power.cs` — 전선을 배 로컬로 옮겨 (판, 서브셀)로 래스터. 하나라도 죽으면 끊김.
  꼭짓점(1/6 m 키)과 기기(원자로·포탑) 칸이 `Connector` 노드. 원자로에서 `Reach()`로 닿은 기기 칸 = `_powered`.
- `Gun` — `owner.Powered(this)` 아니면 `HoldReason.NoPower`.
- `PowerNet.Solve` — 방사형 트리 직류 해석(표 20번). 지금은 스텁, 테스트 5개 빨강.

닿나/안 닿나까지다. 전압·전류·발열이 없다.

## 1. 원칙 (v2에서 그대로)

- MNA 안 쓴다. 섬(island)마다 **트리**로 풀고, 그물(loop)은 BFS 신장트리로 접는다 - 병렬 경로의 정확도를
  버리는 대신 두 패스로 끝난다. 반복법은 단락(1 mΩ)에서 발산하므로 안 쓴다.
- AC는 RMS 크기만. 3상은 버스당 값 하나(L-N 320 V). L-L 부하는 def 플래그로 ×√3.
- 클래스 참조 대신 int 핸들 + SoA. MonoBehaviour 금지. 할당은 재빌드 때만.
- 단락은 특수 케이스가 아니라 저항 1 mΩ.
- 고장 종류를 늘리려면 새 증상이나 새 플레이어 결정이 하나 있어야 한다(v2 원칙 1).

## 2. 데이터

### 2.1 def 키 (JSON, 전부 이미 있는 검증을 탄다)

| def | 키 | 기본 | 뜻 |
|---|---|---|---|
| Reactor (`CriticalModule`) | `lineVoltage` | 554 | L-L RMS. `PhaseVoltage` = /√3 = 320 |
| | `phases` | 3 | |
| | `sourceResistance` | 0.05 | 내부저항 Ω. 단락 전류 = 320/0.05 = 6.4 kA 상한 |
| Gun | `powerWatts` | 2000 | 정격 소비. R = Vnom²/P (320² / 2000 = 51 Ω). 버스 값이 하나(L-N)라 L-L 플래그는 의미가 없어 뺐다 |
| Ballistics.Tuning | `WireOhmPerMetre` | 0.002 | 케이블 저항. 10 m = 0.02 Ω |
| | `WireHeatCapacity` | 400 | J/K per m. 1 m 전선 1 K 올리는 데 400 J |
| | `WireCoolPerSecond` | 0.05 | 초당 (T − 주변) × 이만큼 식는다 |
| | `WireBurnKelvin` | 400 | 이 온도 넘으면 그 자리 서브셀이 죽는다 = 전선이 탄다 |
| | `PowerNominal` | 320 | 소비자가 "정상"으로 보는 전압 |
| | `PowerBrownout` | 0.5 | 이 비율 밑이면 NO POWER. 사이는 저하 |

### 2.2 런타임 그래프 (`Connectivity/PowerGraph.cs`, per Ship)

SoA. 배열은 노드 수에 맞춰 한 번 잡고 재빌드 때만 다시 잡는다.

```
노드 (int k)
  kind[k]     Source | Load | Junction
  emf[k]      Source만. PhaseVoltage
  rInt[k]     Source: sourceResistance / Load: Vnom²/P / Junction: 0
  v[k], i[k]  Solve 결과
  cell[k]     기기 칸 (Junction은 꼭짓점 칸)

간선 (int e)
  a[e], b[e]  노드
  r[e]        Ω = 길이 × WireOhmPerMetre
  wire[e], seg[e]  Ship._wires의 (전선, 구간) - 끊김·열을 거기 적는다
  alive[e]    SegmentIntact - 풀 때마다 다시 읽는다
```

기기 ↔ 노드 대응(`_nodeOf`)은 Ship.Power가 든다. PowerGraph는 Unity를 모른다.

빌드 = 지금 `RebuildPowerNet`이 하는 일 + 저항. 전선 하나(폴리라인)가 간선 여럿(꼭짓점 사이마다 하나).

### 2.3 트리로 접기

섬마다 전원에서 BFS. 처음 닿은 간선만 `parent`가 되고 나머지 간선은 이번 풀이에서 빠진다.
→ `PowerNet.Solve(emf, parent, rBranch, rLoad, v, i)`.

전원이 둘 이상인 섬: **emf 큰 쪽이 뿌리**, 나머지 전원은 이번 M2에선 무시(부하도 전원도 아님).
버스 타이·비상전원은 M5.

## 3. 틱

| 무엇 | 주기 | 자리 |
|---|---|---|
| Solve (V·I) + 전선 열 적분 | `PowerInterval`(6틱, 10 Hz) - 위상은 Atmosphere와 같은 `_atmosphereOffset` | `Ship.Power.SolvePower` |
| 그래프 재빌드 | Solve 틱 중 더티일 때만 - 파단(`BuildRooms`), 기기 서명(살아 있는 원자로×1000 + 포탑 수) 변화, 정비의 구매·폐기·수리 | `Ship.Power.RebuildPowerNet` |
| 간선 생사 갱신 | 매 Solve. `BreachVersion`은 판의 **첫** 파공만 세므로 게이트로 못 쓴다 | `SegmentIntact` |
| 소비자 읽기 | 매 틱 (캐시된 v) | `Gun.OnTick` |

지연 ≤ 100 ms. 시계는 하나고 **위상을 안 가른다** - `_atmosphereOffset`은 InstanceID라, 전선이 타는 틱이
판을 죽이고 그 틱이 파편 시드가 되면 재현이 깨진다.

## 4. 결과가 가는 곳

- `Ship.Voltage(module)` → 0 ~ 320. 전선 없는 설계는 `HasPower ? 320 : 0`(예전 규칙).
- `Gun`: `v < Nominal × Brownout` → `NoPower`. 그 위는 `slewRate × (v/Nominal)` - **증상 B(느린 선회)가
  분기문 없이 나온다.** 조준·발사는 그대로.
- 전선 열: 간선마다 `I²R × dt` 넣고 `WireCoolPerSecond`로 식힌다. `WireBurnKelvin` 넘으면 그 구간이 `burned`
  (정비가 되돌린다)이고, 판 위였으면 첫 서브셀을 `Armor.Burn`으로 **조용히** 죽인다 - `ApplyDamage`의 탄착 경로
  (관통 소리·파편·RunLog 관통·붕괴)를 타면 전기 사고가 피탄으로 기록된다. 판 없는 구간(실내 공기)은 `burned`만이
  끊김이라 상태가 둘이다. 단락이 왜 위험한가가 여기서 나온다: 6 kA × 0.02 Ω = 720 kW → 1 초에 탄다.
- 구멍: 손상 저장본은 죽은 판이 배치에서 빠져 있어 "판 없음"이 공기인지 구멍인지 못 가른다. 지을 때 `DesignMap`
  (설계도 격자)이 판이라 하는 자리에 판이 없으면 `holed` - 영구다(판 부활 없음).
- UI (전부 실제 값만):
  - HUD `SYS` 줄 전압, `GRID` 줄 `62A · WIRE 27/29 · FED 8/10` (전선 있는 배만 - 패널이 한 줄 커진다).
  - 포탑 판독 `NO POWER`, 조준선 꺼짐.
  - 전선 오버레이(`WireView`): Tab 속보기와 함께. 급전 RADIANCE / 산 채로 0 V STEEL / 끊김 BREACH. 구간 = 사각 메시,
    `PowerVersion`이 바뀔 때만 다시 칠한다.
  - 끊기는 순간 `RunLog.WireCut`(잃은 포탑 수) + 플레이어면 피탄 판독 줄에 `WIRE CUT -2 GUN`. 첫 풀이는 기준선이라
    손상 저장본의 구멍을 "방금 끊김"으로 안 적는다.
  - **정지 회로도**(Space, `PauseControl.Schematic`): `SchematicView`가 카메라 자식 덮개(VOID, 알파 `PauseDimAlpha`=1)로
    배경·텍스처를 지우고 1 m 격자·판 윤곽(`FootprintLocal`)·모듈 상자를 그린다. 정렬: 덮개 150 < 방 152 < 윤곽 154 < 전선 156.
    방은 `SchematicRoomAlpha`(1)로 불투명, 라벨은 15 px + 뒤판. 값은 목업(claude.ai/artifact/UTdZGnAhxJ4pqziTg5TLBH)에서
    오너가 골랐다. 판 윤곽은 SchematicView가 아니라 PlateSkin의 건조 와이어프레임이 낸다 - 정지에 들어갈 때
    `ArmorSkin.SetBuildFront`를 대각선으로 쓸어 넘긴다(격납고 건조 전환의 역방향, `SchematicBlendSeconds` 0.5 s).
    덮개는 판 아래(-20)라 와이어프레임 사이로 VOID가 비친다. 모듈 그림(SolidSkin)·후면(BackPlateView)은 회로도에서 꺼진다.
    덮개 셰이더 `SUPERRADIANCE/Blueprint`: 월드 좌표 1 m·10 m 격자를 VOID 위에 그리고, 플레이어 배 로컬 축(x+y)이
    `PauseControl.Front`를 넘은 쪽만 덮는다 - 판과 배경이 **같은 전선**으로 갈린다. 빌드에 넣으려면 Always Included에 등록.
  - 정지 진입 애니메이션: Blend 0→1 동안 카메라가 배 격자 중앙·통째로 들어가는 줌(`CameraSystem.Frame`, 여유 4 m)으로
    SmoothStep 이동. 그 뒤에야 끌기·휠을 받는다.
  - **검사(림월드식)**: 정지 중 클릭(끌지 않은 것, 4 px 안) → `Inspect.Click`이 승무원 → 전선 구간 → 모듈 → 판 순으로
    하나를 고른다. 화면 아래 가운데 `INSPECT` 패널에 그것의 진짜 값 전부 - 포탑(상태·내구·전압·전류·선회·정격·발사·탄·사각·위치),
    원자로(상전압·선간·내부저항·출력 kW), 탄약고(탄약), 엔진(추력), 탱크(추진제), 판(내구·서브셀·밀폐·적열), 전선 구간(상태와
    끊긴 이유 - 과열/구멍/판 소실/잔해 이탈/관통, 길이·저항·전류·온도·지나는 서브셀 수), 승무원(생존·방 기압). 선택 테두리 TELEMETRY.
    Esc로 풀고, 정지에서 나오면 풀린다.
  - Tab은 **정지 중에만** - 층 순환 전부 → 전기(전선·기기·라벨) → 기압(방·승무원). 날면서 보는 기압도는 없앴다(오너 2026-09-19).
  - 정지 카메라(`CameraSystem.PausedCamera`): 왼쪽 끌기 이동, 휠 줌(반높이 3~40 m, 커서 고정), 플레이어 배 격자 둘레 10 m 안으로
    clamp. 자동 줌·추적·흔들림은 얼고 풀리면 원래 SmoothDamp로 돌아온다. 클릭(끌지 않은 것)은 M0 승무원 명령 자리로 비워 둔다.

## 5. 파일

```
Connectivity/Connector.cs    (오너) 노드·BFS. 연결성 전용 - 값은 안 든다
Connectivity/PowerNet.cs     (표 20번) 트리 직류 해석. 순수 함수, 할당 없음
Connectivity/PowerGraph.cs   SoA 그래프 + 섬 + 신장트리 + Solve 호출. 순수, Unity 참조는 device[]뿐
Ship.Power.cs                전선 래스터·기기 수집·주기·결과 적용. 여기만 MonoBehaviour를 안다
Ballistics.Tuning.cs         상수
Editor/PowerNetSelfTest.cs   Solve (있음)
Editor/PowerGraphSelfTest.cs 그물→트리, 전원 둘, 끊긴 간선, 열 적분
```

## 6. 순서

1. `PowerNet.Solve` 초록 (테스트 5개).
2. `PowerGraph` - 빌드·섬·트리·Solve. 테스트 먼저.
3. `Ship.Power`가 그래프를 쓴다. `Voltage()`. `_powered` 제거.
4. `Gun` 전압 읽기 + HUD.
5. 전선 열 → 서브셀 손상. 테스트: 단락 1 초 안에 끊김.
6. 커밋. 여기까지가 M2.

M3(모터·역기전력)는 Load 노드에 `ke·ω`를 역기전력으로 넣는 것 - Solve의 `rLoad`가 상태를 갖게 되는 첫 자리라
그때 반복(2~3회)이 들어온다. M4(차단기·I²t)는 간선에 `alive`를 내리는 규칙 하나.

## 7. 불변식 (짓기 전에 읽을 것)

- `parent[k] < k` - 노드 번호가 곧 위상 순서다. BFS 방문 순으로 번호를 매기면 공짜다.
- **래스터는 지을 때 한 번이다.** 파단 뒤 살아 있는 판으로 다시 굽으면 죽은 판 자리가 "판 없음 = 공기"로 읽혀
  끊긴 전선이 도로 붙는다. (판, 서브셀) 참조는 스스로 죽는다 - 파괴는 null, 이탈은 StillAboard.
- 꼭짓점이 기기에 붙는 조건은 **기기 칸 중심에서 반 서브셀 안**이다. "기기 칸 위"로 하면 칸 경계의 점이
  반올림(은행가) 방향에 따라 옆 기기에 붙는다. 페인터 `WireEndAttached`가 같은 규칙이다.
- 세이브는 전선을 모른다. `burned`·`temp`, 그리고 판의 서브셀 마스크(hp 비율만 저장)가 재시작에서 사라진다 -
  구역 전환은 배 객체를 유지하니 살고, 종료 후 재개만 원상복구된다. 서브셀 마스크 저장은 별도 과제.
- Solve는 `i[]`를 뒤로 패스의 임시 컨덕턴스로 쓴다. 그래서 할당이 없다. `v[]`를 그 용도로 쓰면 앞으로
  패스에서 부모 전압을 읽을 때 컨덕턴스를 읽는다.
- 그물을 트리로 접으면 병렬 경로의 전류가 한쪽에 몰린다. 게임 값이지 물리 값이 아니다 - 두 경로가 정말 필요한
  배(이중화)는 M5 버스 타이에서 답한다.
- 소비자는 절대 그래프를 직접 안 읽는다. `Ship.Voltage(module)` 하나.
- 전선 열은 Armor.Heat(시각)과 다른 것이다. 하나는 온도(시뮬), 하나는 적열(그림).

## 8. M3 - 모터·역기전력 (2026-09-19 시작)

전기기기 과목의 자리. 직류전동기 한 대의 식 `V = E + Ia·Ra`, `E = ke·ω`, `T = kt·Ia`가 전부다.

| 어디 | 무엇 |
|---|---|
| `PowerNet.Solve(..., eLoad, ...)` | **회차(표 21번, 오너).** 부하 k의 전류 `i = (v[k] - eLoad[k]) / rLoad[k]`. 두 패스·할당 없음 유지. 테스트 6~9 (`Tools > Ship > Run PowerNet Tests`) |
| `PowerGraph` | Load 노드에 `eLoad` 배열 하나 더. 재빌드 때만 잡는다 |
| `Gun` | 모터 상태 `Omega`(°/s)·`MotorOn`. `Ke = Vnom·(1 - TurretMotorLoad) / slewRate` - 정격 전압·정격 속도에서 역기전력이 70 % V라 나머지 30 %가 마찰 전류. `Ra = Vnom²/powerWatts`(AddDevice와 같은 식). 속도는 시정수 `TurretMotorTau`(0.25 s)로 `slewRate × V/Vnom`에 붙는다. Slew를 못 부른 틱만 관성으로 식는다 - 매 틱 깎으면 절반에서 멈춘다(실측 4.5/9) |
| `Ship.SolvePower` | 풀기 전에 포탑마다 `rInt = MotorOn ? Ra : 0`, `emf = MotorOn ? Ke·ω : 0`. 서 있는 포탑은 열린 회로(0 A) |
| 나오는 증상 | 기동 순간 전류 = 정격(`Vnom/Ra`, m12 6.3 A), 회전 중 30 %(1.9 A), 서면 0. 큰 포 여덟 문이 같이 기동하면 50 A가 한 번에 케이블을 지난다. 분기문 없음 |

eLoad가 0이면 M2와 같다 - 그래서 기존 테스트 1~5가 회귀 검사다. 반복법은 안 쓴다(§1). 회생(e > v)은 막지 않는다 -
전류 부호가 거꾸로 되는 것이 답이고, 원자로 쪽으로 밀려 들어가는 전류는 M5 버스 타이 전까지는 그냥 v[0]에 반영된다.
