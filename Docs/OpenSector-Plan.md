# 오픈섹터 구상도 — 코드 대조 후 확정안

작성 2026-09-10. 원본은 `SUPERRADIANCE_OpenSector_Research_Report.md`(오너 + ChatGPT). 그 문서는 저장소를 안 봤다. 이 문서는 봤다.
[확정]은 오너가 낸 방향, [선택]은 Claude가 하나 고르고 이유를 붙인 것 — 오너가 뒤집으면 그때 바꾼다.

## 0. 한 문장

**소구역 노드 하나 = 배 2~4척**이던 것을 **소구역 노드 하나 = 60 km 들판에 자리(site) 3~5개 + 출구 2개**로 바꾼다. 갈림길은 메뉴 버튼이 아니라 들판 반대편의 두 출구다. 나머지 캠페인 구조(장 8개, 장 사이 소구역 2단, 시드 결정론, RunState.lanes)는 그대로다.

## 1. 지금 코드가 이미 주는 것

| 보고서가 "필요하다"고 한 것 | 이미 있는 자리 | 비고 |
|---|---|---|
| 알쿠비에레 거품·앞뒤 도플러·안쪽 평평 | `WarpFx.Bubble` (WarpFx.cs:54), `_WarpBubble` 유니폼 | 월드 좌표를 매 프레임 뷰포트로 변환. 배 한 척 전용(static 하나) |
| 배경 캡처·굴절 | `WarpFxFeature` 풀스크린 blit | Shader Graph Scene Color가 아니라 카메라 컬러 통째. 보고서 §12.3의 걱정은 해당 없음 |
| 섹터 정의·연결 | `SectorDef`·`SpawnDef`(CampaignDef.cs), `SubSectorGen.Make(chapter, leg, lane)` | 위치가 시드. 저장은 시드 + lanes만 |
| 섹터 상태·재개 | `RunState.Progress` (sector, leg, lanes, pendingLeg, pendingRefit, 자원 4종, wingmen) | 구역 중간 저장 없음 — 승리 시점에만 배 파일 저장 |
| 전이 연출 | `WarpTransition` (충전 4.2 s → 4300 m/s 스트릭 → 암전 5 s → 도착) | 틱 완전 정지, 도착점 = 원점 +X |
| 화면 밖 접촉 | `ContactView` (SensorRange 1200, IdentifyRange 500, 가장자리 브래킷) | `Tracked`에 첫 관측 판 수만 캐시. 마지막 관측 위치는 없음 |
| 항로 화면 | `LogisticsScreen` (Fork 2갈래 노드, Visited) | 노드 = 버튼. 공간 정보 없음 |
| 적 잠재우기 | `Ship.dormant` + `Campaign._dormant` (45틱 뒤 30틱 간격) | 거리 무관, 시간으로 깬다 |
| 전장 경계 | `Battle.SetZone` + `Ship.HoldInBattleZone` (반경 1200 m, 밖으로 나가면 끌어당김) | 오픈섹터에서는 이 규칙이 적이다 |

새로 지어야 하는 것은 **셋뿐**: 자리(site) 배치 생성, 출구(gate)와 출항 판정, 항해 지도 오버레이. 단거리 점프는 넷째인데 1단계 측정 뒤에 짓는다.

## 2. 보고서 수치 대조

| 보고서 | 코드 | 결론 |
|---|---|---|
| 함선 1,200칸, 60 × 20 m | destroyer 629 배치, cruiser 303, carrier 892. 칸 = 1 m | 칸당 미터 **유지** [선택]. `hpPerSquareMetre`·PPU 48·판 크기가 전부 m라 바꾸면 def 전부 재튜닝 |
| 최고속도 600 m/s | def에 없음. `drag 0.3` + 엔진 추력에서 나온다 | 1단계 첫 일이 **실측**이다 (destroyer·frigate·scout 순항·정지 거리) |
| 점프 12 km | `DetectionDistance` 2000, `SensorRange` 1200, 화면 폭 64 m | 12 km 유지 [확정]. 맹점 착지가 곧 미지다 — 안전은 센서가 아니라 착지 스윕(§7)이 맡는다 |
| AU 단위 | float 정밀도: 30 km에서 4 mm, 100 km에서 1.5 cm. 서브셀 17 cm | 60 km에서 8 mm. 좌표 원점 이동 불필요 |
| 섹터 크기 | `FightDistance` 200, `BattleZoneRadius` 1200, `ContactX` 700 | **센서가 크기를 정하지 않는다** [확정, 2026-09-10]. 크기 = 원하는 횡단 시간 × 실측 순항 속도. 임시값 **60 × 20 km**(600 m/s면 100초). 센서는 그중 얼마를 보느냐만 정한다 — 안 보이는 구간이 곧 미지고, 그 구간을 채우는 것이 방향만 주는 신호(§6)와 점프(§7)다 |

## 3. 구조 [확정 + 선택]

```
MAIN k (손대본, 지금 그대로)
  └ 워프 → OPEN(k, leg 0)  ── 들판. 자리 3~5, 출구 2 (좌상·우하)
        출구 A → OPEN(k, leg 1, lane 0)     출구 B → OPEN(k, leg 1, lane 1)
                    └ 출구 2개 → MAIN k+1 (둘 다 같은 곳. 지금과 같다)
```

- **소구역 노드 하나가 오픈섹터 하나다.** `LegsPerChapter 2`, `Lanes 2`, `Make(chapter, leg, lane)`, `RunState.lanes` 전부 그대로. 갈림길이 메뉴에서 들판으로 옮겨진 것뿐이다.
- **섹터 경계는 전부 워프.** [선택] `Prepare`가 HullStructure 전부를 걷고 좌표가 도착점 기준인 구조를 그대로 쓴다. 안쪽만 연속 항해.
- **재방문 없음, 전진만.** [선택] 보고서 §13의 "재방문 상태 유지"는 자리 상태를 RunState에 넣어야 생긴다. 보고서 §14.2 자신이 "필요한지 먼저 확인"이라 했다. 1단계에서 "돌아가고 싶었다"가 나오면 그때.
- **메인은 안 바꾼다.** 보고서 §5.3 "메인이 더 큰 공간"은 이번 범위 밖. 오픈섹터가 먼저 재미있어야 메인을 키울 이유가 생긴다.

## 4. 데이터

`SectorDef`에 필드 셋 추가. `SpawnDef`에 하나.

```
SectorDef
  float fieldW, fieldH        // 0이면 지금처럼 전장 경계 1200 m 규칙 (손대본 장)
  List<SiteDef> sites         // 자리. 생성기가 채운다
  List<GateDef> gates         // 출구. lane 인덱스 하나씩

SiteDef  { string kind; string label; float x, y; float radius; }
GateDef  { int lane; float x, y; }
SpawnDef { ... int site = -1; }   // 어느 자리 소속인가. -1이면 지금과 같다
```

- `SubSectorGen.Make`가 지금은 템플릿 하나를 `ContactX 700`에 찍는다. 바꾸는 것: 템플릿 K개(K = 3~5, tier 규칙 그대로, Depot은 최대 1개)를 뽑아 들판 안에 간격 ≥ 8 km로 놓고, 각 템플릿의 배를 그 자리 중심 ± 60/90 m에 찍는다(지금 산포 그대로). 출구 둘은 +X 끝 y = ±6 km.
- **시드가 위치를 정한다**는 규칙 유지. 자리 좌표도 `rng`에서 나오니 저장할 것이 안 는다.
- `subsectors.json`은 안 바뀐다. 템플릿은 그대로 "자리 하나의 조합 규칙"이다.
- 배경: `BackgroundView.Jump(seed, kind)`는 kind 하나를 받는다. 오픈섹터는 `""`(맨 하늘) [선택]. 자리마다 배경을 바꾸는 것은 한 하늘에 못 넣는다.

## 5. 규칙 변경 — 자리마다 한 줄

| 지금 | 오픈섹터 | 어디 |
|---|---|---|
| 적 전멸 = 구역 승리 (`NoHostilesLeft`) | **출구에서 출항 = 승리.** `objective = () => _departed` | `Campaign.Prepare`에서 대입. Battle 틱 루프 불변 |
| 적을 처음 본 자리 중심 반경 1200 m 밖으로 못 나감 | `fieldW > 0`이면 전장 = 들판 사각형. 밖은 같은 끌어당김 | `Battle.SetZone` 오버로드 + `HoldInBattleZone` 사각형 판정 |
| 적 45틱 뒤 시간으로 깬다 | **거리로 깬다.** 플레이어가 자리 중심 1500 m 안 → 그 자리 배들 30틱 간격 기상 | `Campaign.OnTick`의 `_dormant` 루프에 거리 조건 |
| `IsHostileTo`가 잠든 배를 양쪽에서 뺀다 | 그대로. 잠든 자리는 못 쏘고 안 쏜다 — "아직 안 건드린 자리" | 변경 없음 |
| 정비 노드 도착 = RefitScreen | Depot 자리 반경 200 m 안에서 키 하나 → `RefitScreen.Open()` (틱 정지 그대로) | `Campaign.OnTick`에 근접 검사 + ControlHints 한 줄 |
| `RunState.pendingRefit` = 정비 노드에서 껐다 | **삭제 대상.** 오픈섹터에서는 재개 시 원점 도착이라 정비 자리에 서 있을 수 없다. 손대본 장의 `refit`만 남으면 그 플래그는 `CommitChapter` 경로 하나로 줄어든다 | RunState |
| Skirmish 자리 끝 = 아무 일 없음 | 자리 소속 적 전멸 → `RunLog.SiteCleared` + 노획은 **출항 때 한 번** `ComputeSalvage` (지금과 같은 자리) | RunLog 한 종 추가 |
| 승리 시 `RunState.Save(player)` | 출항 시 저장. 구역 중간 저장 없음 — 죽으면 그 구역 도착부터 | 변경 없음 |
| `Stranded` (Δv 0 + 사거리 안 적 없음 5초 = 패배) | **그대로.** 들판 한가운데서 연료가 떨어지면 5초 뒤 패배 | 오너 판단(2026 기존). 점프가 생기면 점프 충전 중은 예외 |

## 6. 항해 지도 [선택 전부 — 오너가 시각을 고친다]

- **오버레이 하나, 키 M.** ImGui로 들판 사각형을 화면에 축소해 그린다(60 km → 화면 폭의 60 %). 틱은 **안 세운다** — 세우면 지도가 공짜 정찰이 된다. 보고서 §11.3이 미정으로 둔 것을 이렇게 정한다.
- 그리는 것: 플레이어 위치·기수, 자리 아이콘(kind별 글자 하나: S/E/W/D/P), 출구 둘(lane 라벨 = 다음 노드 `label`), 마지막 관측 접촉.
- **자리는 방향으로만 보인다, 종류는 1200 m 안에서만.** 밖에서는 지도 가장자리의 방위 눈금 하나(거리 없음). 60 km에서 위치까지 주면 항해가 지도 클릭이 된다. 보고서 §11.2 "UNKNOWN만 나열하지 않는다"의 최소 구현. 이동 열원/정지 물체 구분은 `Tracked`에 `lastPos/lastTick` 두 필드로 나온다 — 두 관측 사이 거리가 곧 이동 여부.
- 색은 팔레트 SUPERRADIANCE 그대로. 여기까지가 골격이고 모양·간격·문구는 오너 몫.

## 7. 단거리 점프 — 3단계, 1단계 측정 뒤

보고서 §9.3의 여덟 질문에 답을 하나씩 붙인다. 전부 [선택].

| 질문 | 답 | 이유 |
|---|---|---|
| 중간 공간 통과 | 순간 이동. 틱 3개(50 ms) 동안 위치 보간 | 연속 이동이면 "점프 중 피격" 규칙이 따라온다 |
| 경로 충돌 | 착지점까지 `Physics2D.OverlapBox` 스윕. 막히면 **그 앞 100 m에 착지** | 거부하면 적 위치가 새고, 겹치면 설명 없는 충돌 |
| 도착점 막힘 | 위와 같은 규칙 | |
| 속도 | 보존 | 점프로 정지하면 도주가 공짜 |
| 준비 중 조작 | 회전만. 추력 0. 취소 가능 | 충전 1.5 s가 위험이어야 전투 중 사용이 결정이 된다 |
| 점프 중 피격 | 충전 중 판이 맞으면 **충전 취소**. 이동 3틱은 무적 없음(어차피 50 ms) | `Armor.ApplyDamage` 사건 하나로 끝난다 |
| 센서 밖 | 간다. 최대 12 km | 맹점 착지가 미지의 값이다. 안전은 위 스윕이 맡는다 [확정] |
| 전투 중 | 된다. 재사용 10 s | 금지하면 "전투 중"의 정의가 필요해진다 |

연출은 `WarpFx.Bubble` 그대로 부른다(충전 중 반경 커짐 → 이동 순간 링 → 착지 뒤 0.3 s 감쇠). 새 셰이더 없음. `WarpFx`가 static 하나라 플레이어만 점프한다 — 적 점프는 이 범위 밖.

## 8. 단계와 검증

### 1단계 — 들판 하나 (보고서 §14.1)
지을 것: §4 데이터, §5의 1·2·3·5행, §6 지도. 점프 없음.
측정 먼저: destroyer·frigate·scout의 0 → 순항 시간, 순항 속도, 정지 거리. 이 숫자가 들판 크기를 정한다.
확인:
- 자리 3~5개에서 플레이어가 **간 곳과 안 간 곳**이 있는가. 전부 다 갔으면 자리가 적거나 들판이 작다.
- 판단 없는 이동 시간이 총 체류의 몇 %인가. 60 km면 이 값이 클 것이 거의 확실해서 점프(3단계)를 1단계에 당겨 붙일 가능성이 크다 — 측정이 그 결정을 한다.
- 출구 도착 → `LogisticsScreen` → 워프. 지금 흐름이 그대로 이어지는가.
- 프로파일: `TraceWorld`는 거리 컬링이 없다. 자리 5개 × 3척 + Wreck hulk에서 틱 시간을 잰다. 지금 노드는 4~8척이다.
- 손상된 배(판 40 % 소실, 탱크 반)로 같은 들판. 경로가 바뀌는가.

### 2단계 — 갈림길이 공간이 되는가 (보고서 §14.2)
출구 A·B의 다음 노드 라벨을 지도에 보여준다. 확인: 출구 선택이 "가까운 쪽"으로 수렴하는가. 그러면 출구 위치를 자리와 엮는다(Depot 옆 출구 vs Elite 옆 출구).

### 3단계 — 점프 (보고서 §14.3)
1단계의 "판단 없는 이동 %"가 근거일 때만. 같은 들판에서 점프 유무 비교.
중단 기준: 최대 거리 연타가 항해를 대체하거나, 전투 이탈이 공짜가 되면 재사용 시간·충전 취소 규칙을 먼저 조인다.

### 범위 축소 기준 (보고서 §14.4 그대로)
1단계에서 "자리 선택 이유를 설명 못 한다"가 나오면 들판을 접고 지금 갈림길 화면에 자리 정보만 붙인다. 지은 데이터(§4)는 그래도 남는다.

## 9. 안 하는 것

- 재방문·자리 상태 영속 (§3).
- 메인섹터 확장 (§3).
- 관측 정보의 별도 클래스. `ContactView.Tracked`에 필드 둘.
- 적의 점프·순찰·이동. `ShipAi`는 지금 적 없으면 조종간을 놓는다. 자리 사이를 오가는 적은 "이동 열원" 경험의 원천이라 매력 있지만, 순찰 경로 = 새 AI 상태다. 2단계 뒤.
- 시간 압박·전역 붕괴·함장 계승 (보고서 §4.2와 같은 결론).
- 새 셰이더. 거품은 있는 것.

## 10. 첫 커밋 — 레드팀 뒤 수정본 (2026-09-10 지음, 미커밋. 출구 마커·R 정비·배경 드리프트 0.1·동료 연료 규칙까지 포함)

§11의 P1 여섯이 원안의 "자리만 흩고 날아본다"를 죽였다. 최소 동반 변경 없이는 도착 직후 전장 조류가 플레이어를 튕긴다. 1단계 첫 커밋은 아래 일곱 줄이 한 덩어리다.

1. `SubSectorGen.Make`: 템플릿 K개(3~5, Depot ≤ 1)를 8 km 간격에 흩는다. `sector.refit = false`, `materials = 0`(자리별 값은 근접 사건으로 옮긴다). `SectorDef.gate` 하나(`Vector2`). **출구는 하나** — 갈림길은 항로 화면에 그대로 둔다(2단계가 "출구 선택이 공간이 되는가"를 재기로 했으니 lane-gate는 그 뒤).
2. `Campaign.Prepare`: sites 노드면 `_battle.objective = () => _departed`를 **체인 앞에** 무조건 대입하고 `SeekZone`을 안 탄다(경계 없음 — 60 km에서 "밖"은 아직 문제가 아니다).
3. `WakeDormant`: 시간 대신 "플레이어가 **그 배**에서 1500 m 안 **또는** 그 배 `AliveCount`가 줄었다". 자리 소속 필드 없이 같은 자리 배는 ±90 m라 같이 깬다. 둘째 조건이 없으면 잠든 자리를 사거리 밖에서 공짜로 죽인다.
4. `Hangar.OnTick`: `owner.dormant`면 return. 없으면 carrier 자리가 잠들지 않는다.
5. `ComputeSalvage`: `!IsCombatEffective`인 배만 센다. 출항 = 승리가 되는 순간 안 싸운 자리의 멀쩡한 배가 노획으로 들어온다.
6. 출항 시 `TickManager.Paused = true`. 안 하면 `Interlude` 90틱 동안 `_battle = null`인 채로 2 km 안 적이 계속 쏜다.
7. `DramaManager.OnBattleEnd`: sites 노드는 `battle-won` 안 튼다(자리 하나 안 건드려도 전승 보고가 나온다).

지도는 새 오버레이 대신 `ContactView.Draw`의 가장자리 브래킷을 출구 좌표에 재사용(`Vector2` 오버로드 하나). 정비는 근접이 아니라 Depot 자리에서 키 하나로 **기존 `EnterRefit`**을 부르고 `DepartRefit`이 돌아갈 phase를 기억한다 — `RefitScreen.Open()`만 부르면 틱·입력·카메라를 아무도 안 돌려줘 소프트락이다. `pendingRefit`는 **안 지운다** — `RunStateSelfTest`가 걸려 있고 이득이 없다.

`BackgroundView.yawPerMetre 0.006`은 600 m/s에서 3.6°/s, 60 km = 360°다. sites 노드에서 1/10.

## 11. 레드팀 (2026-09-10)

Codex(ChatGPT)는 파일 읽는 도중 사용량 한도(103k 토큰, 답변 0줄). 아래는 **Claude 별도 인스턴스**가 같은 질문지로 낸 것이고, P1 전부 코드로 재확인했다. Codex 재실행은 9월 11일 03:09 이후.

| # | 등급 | 발견 | 근거 | 처리 |
|---|---|---|---|---|
| 1 | P1 | 원안 첫 커밋은 못 날아본다. 45틱 뒤 전 자리 기상 → `SeekZone`이 `Ship.All` 첫 적으로 중심을 잡음 → 30 km 밖이면 `HoldInBattleZone`이 `over × 12 × mass`를 매 틱 | Ship.cs:1316, Tuning.cs:383 | §10-2 |
| 2 | P1 | carrier 자리는 잠들지 않는다. `Hangar.OnTick`은 컷신만 보고 fly를 찍고, 태어난 fly는 `dormant=false` | Hangar.cs:44 | §10-4 |
| 3 | P1 | `ComputeSalvage`가 죽었는지 안 본다. 승리 = 전멸이라 무해했던 것 | Campaign.cs:1058 | §10-5 |
| 4 | P1 | `RefitScreen.Open()`은 UI만. 틱 정지·Borrow·phase는 private `EnterRefit`. 들판에서 열고 출항하면 `DepartRefit`이 phase 검사로 무시 → 소프트락 | RefitScreen.cs:60, Campaign.cs:850 | §10 정비 |
| 5 | P1 | 워프는 `LogisticsScreen.Depart → Run(chosen)`뿐이고 `chosen`은 화면의 lane. 출구 둘을 둬도 화면이 덮어쓴다 | LogisticsScreen.cs:261, 669 | 출구 하나 |
| 6 | P1 | 거리 기상만이면 잠든 자리를 사거리 밖에서 공짜로 쏜다(탄은 물리라 맞고, 수동 주포는 커서로 쏜다, 탄 수명 30 s) | Ship.cs:1490, Projectile.cs:26 | §10-3 |
| 7 | P2 | Wreck·Depot만 뽑힌 들판은 `!HasHostile → peaceful` 분기로 떨어져 동료 진입 즉시 끝난다 | Campaign.cs:515 | §10-2 "체인 앞" |
| 8 | P2 | 출항 뒤 `Interlude` 최대 90틱 + 대본 대기 동안 틱이 돈다 | Campaign.cs:313, 832 | §10-6 |
| 9 | P2 | 템플릿 `refit:true`가 노드에 남으면 출항 뒤 정비 화면이 한 번 더 | SubSectorGen.cs:162, Campaign.cs:805 | §10-1 |
| 10 | P2 | `battle-won` 대사가 안 싸운 출항에도 | DramaManager.cs:362 | §10-7 |
| 11 | P2 | 동료(newship, 탱크 11장)가 60 km를 편대로 따라오다 연료로 죽으면 `BuryWingmen`이 영구 상실로 적는다 | ShipAi.cs:130, Campaign.cs:1113 | **미결.** 측정 뒤. 동료 연료를 안 깎거나 점프에 태운다 |
| 12 | P2 | 배경 드리프트 360° | BackgroundView.cs:21 | §10 마지막 줄 |
| 13 | P2 | 점프는 속도로 못 한다. `m_MaxTranslationSpeed 100`(스텝당 100 m), `AutoSyncTransforms 0`이라 `rig.position`과 `transform.position` 둘 다 대입 | Physics2DSettings.asset:14, 54 | §7 수정 |
| 14 | P2 | "충전 중 피격 → 취소"에 걸 이벤트가 없다. `DamagedPlateCount` 비교가 제일 싸다 | RunLog.cs:162 | §7 수정 |
| 15 | P2 | destroyer·frigate·scout·cruiser·lance는 탱크 0장. `Stranded`는 탱크 없으면 절대 안 걸리고 "탱크 반" 테스트도 불가 | Ships/*.json, Battle.cs:581 | §8 측정 함선 = newship |
| 16 | P2 | "구역 중간 저장 없음"은 이미 거짓 — `RefitScreen.Repair/Buy`가 `RunState.Save` | RefitScreen.cs:260 | §1 문장 정정 |
| 17 | P2 | 최대 22척이 `Prepare` 한 프레임에서 지어진다. `Transit` 5초는 실시간이라 스파이크가 거품 첫 프레임을 먹는다 | Campaign.cs:461 | 측정 뒤 |
| 18 | P2 | `LogisticsScreen` 갈래 카드가 `spawns` 합산이라 "HOSTILE ×12"로 뜬다 | LogisticsScreen.cs:430 | 2단계 |

레드팀이 더 단순하다고 한 것을 §10이 그대로 받았다: `GateDef`·`fieldW/H`·사각 경계·`SpawnDef.site`·`SiteDef.radius`·`Tracked.lastPos`·M키 오버레이·`pendingRefit` 삭제 전부 뺌.
