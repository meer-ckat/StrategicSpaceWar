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
- **결정론** — RNG는 `DeterministicRng` + `Ballistics.Hash`뿐. `UnityEngine.Random` 금지.
- **파편 연쇄 상한 2개** — `MaxSpallDepth`(레이), `MaxFragmentGeneration`(실체 파편). 둘 다 없으면 한 발이 함선을 지운다.

## 파일 지도

```
Ballistics.Tuning.cs      상수 전부. 숫자 만질 일이면 여기만.
Ballistics.Formula.cs     관통 공식, RHA 곡선
Ballistics.SubCell.cs     판 안쪽 6×6 격자, 다중 레인 채널
PenetrationManager.cs     판정. 순수 함수, RNG 없음, 바깥을 안 바꾼다
Projectile.cs             시간 예산 틱 루프
Projectile.Surfaces.cs    레이캐스트 → 접촉면 수집
Projectile.Damage.cs      결과 적용. 바깥이 바뀌는 유일한 자리
Ship.Split.cs             선체 연결성 BFS, 파단
Ship.Ram.cs               충각 → 기존 국부 손상 경로
```

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
