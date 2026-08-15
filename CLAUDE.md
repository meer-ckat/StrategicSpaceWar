# SUPERRADIANCE (repo: StrategicSpaceWar)

2D 사이드뷰 함선 시뮬레이션 로그라이크. Unity, URP, 2D.
우주 로그라이크 × War Thunder × Teardown — 쏜 곳이 실제로 부서지고, 그 결과를 안고 끝까지 간다.

## 핵심 원칙

특수한 결과를 스크립트로 가짜 구현하지 않는다. 기본 규칙을 만들고, 상황은 거기서 자연발생시킨다.
충각·선체 절단·엔진부만 분리·주포만 살아남은 반파 함선은 전부 별도 이벤트가 없다.

## 작업 방식 — 설명이 먼저다

이 리포의 목표는 돌아가는 코드가 아니라, **오너가 이해하고 있는** 돌아가는 코드다.
이해 못 하는 시뮬레이션은 디버깅이 안 되고, 이 프로젝트는 디버깅이 전부다.

- **코드 전에 핵심 로직을 설명한다.** 무엇을 어디에 왜 넣는지, 새로 생기는 규칙이 무엇인지,
  기존 불변식 중 무엇을 건드리는지. 코드 없이, 짧게.
- **오너가 이해했다고 하기 전에는 파일을 건드리지 않는다.** "일단 짜고 설명" 금지.
- **이해가 막히면 그때 할 일은 코드가 아니라 설명이다.** 다른 각도로 다시, 숫자 예시로,
  최소 예제로. 이해될 때까지. 못 알아들었는데 코드가 나오면 그 코드는 실패한 것이다.
- **"이해했지?"로 확인하지 않는다.** 결과를 예측시켜라 — "이 값을 두 배로 하면 어떻게 되나",
  "이 조건이 false면 어디로 가나". 답이 틀리면 아직 설명이 끝난 게 아니다.
- **설명이 길어지면 설계가 복잡한 것이다.** 코드를 더 쓰지 말고 설계를 줄여라.
- **버그를 잡을 때마다 두 가지를 남긴다** — self-test 케이스 하나, 그리고 코드만 봐서는
  안 보이는 것이었다면 아래 불변식에 한 줄.

예외 — 설명 없이 바로 해도 되는 것: 오타, 이름 변경, 포맷, 이미 설명하고 승인된 것의
이어짓기, 오너가 "설명 됐고 그냥 해라"라고 말한 경우.

## 시뮬레이션 불변식

디버깅으로 알아낸 것들이라 코드만 봐서는 안 보인다. 건드리기 전에 읽을 것.

- **틱 순서** — 힘 → `Physics2D.SyncTransforms` + `Simulate` (틱당 1회, `simulationMode = Script`) → `ITickLate`(탄). 탄은 정착된 스냅샷 위에서 판정받는다.
- **저항과 피해는 같은 선을 읽는다** — 유효 RHA도, 피해 분배도 탄이 판을 가로지르는 채널 전체. 진입 칸만 저항하면 6×6 격자의 5/6이 장식이 된다.
- **모서리 폴백** — 접촉면 중 탄을 마주보는 것이 하나도 없으면(`MinFacing` 미달) 정면 입사로 판정한다. 이음매의 접선 normal을 곧이곧대로 읽으면 정면 사격이 공중에서 도탄하고, 버리면 벽을 통과한다.
- **진입 서브셀은 채널의 첫 샘플** — 히트 지점은 격자선에 정확히 걸리는 일이 상시라, `floor()`만 쓰면 2.2% 확률로 탄이 *떠나는* 칸을 고른다.
- **두 BFS는 정반대 그래프** — 방은 빈 칸을 4방향으로, 선체는 실물을 8방향으로 잇는다. 계단식 판을 4방향으로 보면 멀쩡한 배가 두 동강 난다.
- **격자와 콜라이더는 다른 층이다** — 방·선체·파단은 1m 정수 격자와 판의 *위치*만 읽는다. 콜라이더 크기(2×2까지)·회전·모양은 탄도와 그림만 쓴다. 경사장갑을 위해 콜라이더를 키운 것이 방 구획을 바꾸면 안 된다. 대가: 2×2 판이 덮은 이웃 칸이 격자상 비어 있으면 방은 그리로 이어진다. 버그가 아니라 이 결정이다.
- **밀폐 주머니가 실내다** — 텍스트 맵이 죽으면서 ' '가 주던 우주/실내 구분이 사라졌다. `MarkExterior`가 비실물 테두리 칸에서 4방향으로 번져서 닿으면 우주, 안 닿으면 실내로 정한다. `Cell.Unset`이 0인 이유가 이것이다 — 기본값이 Exterior면 "아직 안 정해진 칸"과 "우주"가 구분되지 않아 flood가 자기 결과를 다시 읽는다.
- **판만 격자에 도장을 찍고, 판은 선체 직속 자식이어야 한다** — `localPosition`은 부모 기준이라 한 겹만 내려가도 칸 좌표가 통째로 틀린다. 모듈(포탑·엔진)은 자기가 붙은 판의 자식으로 정확히 한 겹 아래 들어간다. 판이 죽으면 같이 죽고 잔해로 떨어지면 같이 날아가는 것이 전부 이 부모 자식 관계 하나에서 나온다 — 별도 코드가 없다.
- **안 쏘는 것과 안 겨누는 것은 다른 일이다** — 아군 사선 검사는 `Gun.OnTick`의 격발 직전에, 포신이 실제로 향한 선을 포구에서 쏴서 본다. 조준 단계(`TryGetTarget`)에서 검사하면 두 가지가 틀린다: 포신은 아직 선회 중이라 표적 쪽을 안 보고 있고, false를 돌려주면 `_pending`이 0으로 리셋돼 포탑이 표적 추적을 통째로 그만둔다. 막혀서 안 쏜 발은 `_pending`을 소모하지 않아 사선이 열리는 순간 나간다.
- **충각의 절단은 별도 코드가 아니다** — `RamImpact.Conduct`의 감쇠가 등방성이 아니라 충격축을 따라 길고 옆으로 짧다(0.80 대 0.30 per m). 그 띠가 반대쪽 외판까지 이어지면 판들이 죽고, 이미 매 틱 도는 `HullStructure.SplitIfBroken`이 두 덩어리를 찾아 가른다. "허리를 끊는" 함수는 없다. BFS와 공식이 하는 일이 다르다는 것이 요점 — **BFS는 어디까지 닿는가**(실물로 이어져 있어야 간다), **공식은 얼마나 먹는가**(위치에서 바로. 경로에 누적하지 않으므로 도달 순서와 무관하다).
- **판의 이웃은 물리로 찾지 않는다** — `Armor.Neighbours`를 `ShipBuilder.Stamp`가 격자에서 한 번 채워 둔다. `OverlapCircle`로 찾으면 세 가지가 틀린다: 호출마다 배열을 할당하고, 콜라이더 반경 때문에 두 칸 건너까지 집어오고, **상대 함선의 판까지 집어온다.** 잔해로 갈라진 뒤에도 참조는 살아 있으므로 `Armor.SameBodyAs`(부모 비교)로 거른다 — 판이 선체 직속 자식이라는 규칙이 여기서 한 번 더 값을 한다.
- **잔해로 간 모듈은 null이 아니다** — 재부모화돼도 `Gun.owner`도 `Ship.shipEngines`도 그대로 살아 있다. 그냥 두면 배가 100m 뒤의 엔진으로 가속하고 날아간 포탑이 계속 쏜다. `Ship.StillAboard`로 매 틱 소속을 다시 확인한다.
- **결정론** — RNG는 `DeterministicRng` + `Ballistics.Hash`뿐. `UnityEngine.Random` 금지.
- **def는 붙이는 순서가 아니라 켜는 순서가 중요하다** — `ThingDef.Spawn`은 GameObject를 **비활성으로 만들고**, 콜라이더·컴포넌트·stats·위치를 다 넣은 **뒤에** 켠다. `AddComponent`는 오브젝트가 활성이면 `Awake`를 즉시 부르기 때문에, 그냥 두면 `Armor.Awake`가 stats보다 먼저 돌아 모든 판이 기본값 체력으로 태어난다. 이 규칙 하나가 URP `Light2D` 같은 남의 컴포넌트까지 데이터로 설정 가능하게 만든다 — `OnEnable`이 값이 다 들어간 뒤에 돌아서 내부 캐시가 올바르게 잡힌다.
- **인스펙터 값은 def가 있으면 장식이다** — `shipDefName`이 채워진 배는 Awake에서 def의 수치가 인스펙터를 덮어쓴다. 플레이 중에 인스펙터에서 `drag`를 밀어봐야 다음 실행에 되돌아온다. 튜닝은 JSON에서 하고 `Tools > Defs > Reload`.
- **export는 배치만 다시 쓴다** — 함선 def에는 손으로 튜닝한 배 수치가 같이 들어 있다. `ShipDef.Save`가 통째로 직렬화하면 그게 조용히 사라지므로, `DefKeys.ReplaceTopLevelValue`로 `placements` 값만 갈아끼운다.
- **JsonUtility는 모르는 키를 조용히 버린다** — `rha`를 `rah`로 오타 내면 에러 없이 기본값이 들어가고 증상은 "장갑이 좀 약한 것 같은데"다. `ThingDef.Validate`가 리플렉션으로 (주 클래스 + comps 전부)의 직렬화 필드 이름을 모아 JSON 최상위 키와 대조하고, 하나라도 모르면 그 def를 아예 안 싣는다. 이 검증은 옵션이 아니라 데이터 주도 설계의 절반이다.
- **`hpPerSquareMetre`는 m²당이다** — 판의 총 구조 예산은 콜라이더 넓이를 곱해서 나온다. 총량으로 두면 0.4×1.0 얇은 패널이 1×1 벽과 같은 체력을 갖고, 45° 경사판(1×1.414)은 프리팹에 √2를 손으로 곱해 적어야 한다 - 새 판 모양마다 사람이 곱셈하는 규칙은 언젠가 반드시 잊힌다. `Armor.Awake`가 콜라이더를 HP 초기화보다 **먼저** 읽어야 하는 이유가 이것이다.
- **파편 연쇄 상한 2개** — `MaxSpallDepth`(레이), `MaxFragmentGeneration`(실체 파편). 둘 다 없으면 한 발이 함선을 지운다.

## 파일 지도

```
Ballistics.Tuning.cs      상수 전부. 숫자 만질 일이면 여기만.
Ballistics.Formula.cs     관통 공식, RHA 곡선
Ballistics.SubCell.cs     판 안쪽 6×6 격자, 다중 레인 채널
RamImpact.cs              충각 피해 + 충격 전도(비등방). 선체 절단이 여기서 자연발생한다
PenetrationManager.cs     판정. 순수 함수, RNG 없음, 바깥을 안 바꾼다
Projectile.cs             시간 예산 틱 루프
Projectile.Surfaces.cs    레이캐스트 → 접촉면 수집
Projectile.Damage.cs      결과 적용. 바깥이 바뀌는 유일한 자리
Ship.Split.cs             선체 연결성 BFS, 파단
Ship.Ram.cs               충각 → 기존 국부 손상 경로
Hulk.cs                   배 아닌 떠다니는 덩어리. 잔해·운석·거울·폐위성이 전부 이것
ShipGrid.cs               1m 정수 격자. 위상만 - 콜라이더는 여기 안 온다
ShipBuilder.cs            배치 리스트 ↔ 자식 오브젝트. 양방향이 한 파일에 있어야 왕복이 닫힌다
ShipExporter.cs           씬 → JSON (에디터). 뽑고 나서 왕복 검증까지 돈다
ShipDef.cs                배 한 척: 배치 리스트 + 배 수치. StreamingAssets/Ships/*.json
DefKeys.cs                def 키 검증·타입 해석·비파괴 JSON 병합. ThingDef와 ShipDef가 공유
RoomView.cs               방 기압 오버레이. 새는 방을 따로 칠한다 (Tab으로 토글)
ThingDef.cs               물건 한 종류의 정의 + 검증 + Spawn
DefDatabase.cs            defName → ThingDef. StreamingAssets/Defs를 훑는다
SolidSkin.cs              모듈·탄의 절차적 그림 (ArmorSkin은 판 전용)
```

## 설계도

배는 JSON 배치 리스트로 짓는다. 텍스트 맵은 죽었다 (`ShipGrid.ParseMap`은 테스트 픽스처 전용).

- 저작은 씬에서, 결과는 `Tools > Ship > Export Selected Ship To Json`으로 뽑는다.
- `Ship.shipDefName`이 비어 있으면 씬의 자식을 그대로 쓴다(= export 원본). 채워져 있으면 Awake에서 자식을 갈아엎고 JSON대로 짓는다. 두 원본을 동시에 살려두면 반드시 어긋난다.
- **`Ship`은 abstract가 아니다.** 함선의 종류는 C# 클래스가 아니라 `shipDefName`이다. `ShipDef`는 배치 리스트 **그리고** 배 수치(`drag`, `angleAccel`, `FightDistance`...)를 함께 들고, ThingDef와 같은 두 번 읽기로 Ship에 붓는다.
- **`team`은 설계가 아니다.** 같은 구축함이 아군일 수도 적군일 수도 있다. 위치·`engagementSign`도 같은 이유로 def에 없다 — 소환 인자다.
- 질량은 판 수 × `massPerPlate`. 세 척에 같은 숫자를 손으로 적어두면 작은 배가 큰 배만큼 굼떠진다.
- **프리팹은 없다.** 물건 한 종류가 `StreamingAssets/Defs/<이름>.json` 파일 하나다 — 어느 C# 클래스를 붙일지(`thingClass`), 어떤 부속을 같이 달지(`comps`), 콜라이더가 얼마인지, 수치가 얼마인지 전부.
- 그림도 자산이 아니다. 스프라이트 파일이 없고 `ArmorSkin`(판, 서브셀 단위로 구움)과 `SolidSkin`(모듈·탄, 단색)이 콜라이더에서 런타임에 만든다. **def의 색이 곧 아트다.**
- def는 서로를 **이름으로만** 안다 (`Gun.projectile: "Railgun Bullet"`). JSON끼리는 GUID가 없으니 그것뿐이고, 그게 모딩이 열리는 지점이다.

## 테스트

`Tools > Ballistics > Run Penetration Tests` (에디터 메뉴). 씬도 플레이 모드도 필요 없다 —
`Resolve`가 순수 함수라서. 탄도 수식이나 서브셀 격자를 건드렸으면 돌릴 것.

## Skill routing

When the user's request matches an available skill, invoke it via the Skill tool. When in doubt, invoke the skill.

Key routing rules:
- Product ideas/brainstorming → invoke /office-hours
- Strategy/scope → invoke /plan-ceo-review
- Architecture → invoke /plan-eng-review
- Design system/plan review → invoke /design-consultation or /plan-design-review
- Full review pipeline → invoke /autoplan
- Bugs/errors → invoke /investigate
- QA/testing site behavior → invoke /qa or /qa-only
- Code review/diff check → invoke /review
- Visual polish → invoke /design-review
- Ship/deploy/PR → invoke /ship or /land-and-deploy
- Save progress → invoke /context-save
- Resume context → invoke /context-restore
- Author a backlog-ready spec/issue → invoke /spec
