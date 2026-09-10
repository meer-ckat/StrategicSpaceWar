---
name: unity-profile
description: Read a Unity Profiler capture (screenshot or pasted hierarchy) for this project and find what to fix, in order. Use when the user says the game is slow, lags, spikes, drops frames, "과부하", "렉", "느려", "프레임 떨어짐", or pastes/attaches a Profiler window. Also use before and after any performance change to confirm the number actually moved.
---

# Unity 프로파일 읽기

SUPERRADIANCE 전용 절차. 세션마다 다시 발명하지 말 것.

프로파일러 숫자는 **그대로 읽으면 거의 항상 틀린 결론이 나온다.** 아래 순서를 지킨다.

## 0. 이 숫자가 믿을 만한가

먼저 `PlayerLoop`의 ms와 창 위 `CPU: N ms`를 비교한다.

```
CPU: 89.08ms   PlayerLoop: 69.91ms   →  19.17ms(21.6%)가 게임 밖
```

**PlayerLoop 밖은 게임 코드가 아니다.** EditorLoop, 프로파일러 자체 수집 비용, Scene 뷰 이중 렌더링.
차이가 15%를 넘으면 그 자리에서 멈추고 이것부터 시킨다:

- Game 뷰 탭에서 스페이스바(Maximize) — Scene 뷰 렌더링이 통째로 빠진다
- `Hierarchy` 드롭다운 → `Timeline`. Others 블록에 이름이 찍힌다
- 진짜 숫자는 `File > Build Profiles`(Unity 6, Ctrl+Shift+B) → `Development Build` + `Autoconnect Profiler` → Build And Run

`Gfx.WaitForPresent` / `WaitForTargetFPS`가 크면 **CPU가 아니라 GPU 문제거나 그냥 기다리는 중이다.** 방향이 완전히 다르다.

## 1. 틱당으로 환산한다

`TickManager.Early` 같은 줄의 `Calls`가 곧 이 프레임에 돈 **틱 수**다. 3~8 사이에서 매번 다르다.
전부 그 수로 나눈다. **안 나누면 프레임끼리 비교가 아예 안 된다** — 6틱 프레임과 3틱 프레임을 나란히 놓고
"두 배 나빠졌다"고 읽는 것이 이 자리의 기본 실수다.

```
TickManager.Update 42.73ms / 3 calls = 14.24 ms/tick
```

목표는 틱당 **7 ms 아래**. 그 위면 렌더까지 얹었을 때 60 fps가 산술적으로 불가능하다.

한 프레임에 6~8틱이 돌고 있으면 그건 **밀린 것을 따라잡는 중**이라는 뜻이다(`MaxTicksPerFrame` 8).
정상 상태는 프레임당 1틱이다. 스파이크 한 번이 백로그를 만들고 그 뒤 여러 프레임이 8틱씩 돈다.

## 2. Total이 아니라 Self를 본다

Total은 자식 비용이다. 고칠 곳은 **Self가 큰 줄**이다.
접힌 삼각형(▶)이 있으면 아직 답이 아니다 — 펴게 시킨다.

이 리포는 마커가 이미 촘촘하다. 접혀 있으면 물어보지 말고 무엇을 펼지 지목한다:

| 접힌 줄 | 안에 있는 것 |
|---|---|
| `Tick.Ship` | Ship.Split / Ram / Drive / Aim / Atmosphere / Watch |
| `Ship.Ram` | Ram.Sweep / Ram.Gather / Ram.Conduct |
| `TickManager.Late` | Tick.AutoCannonProjectile → Spall.Burst → TraceWorld.Build → **TraceWorld.Pose** |
| `Spall.Burst` | TraceWorld.Build, Spall.TraceJob.Schedule/Complete |

## 3. 개당 비용을 낸다 — 네이티브 호출 세기

`Self ÷ Calls`. 이게 이 프로젝트에서 진짜 범인을 찾는 방법이다.

**µs당 대략의 눈금:**

| 개당 | 무슨 뜻 |
|---|---|
| ~70 ns | 순수 디스패치. 빈 `OnTick()` |
| ~150 ns | 네이티브 프로퍼티 하나 (`transform.position`, `collider.enabled`) |
| ~1 µs | 네이티브 6~8번. **거의 항상 여기가 버그다** |
| >10 µs | 안에 루프가 있다. 펴서 봐라 |

실제로 이 방법으로 잡은 것:

> `TraceWorld.Pose` 8.43 ms / 2 calls = 4.2 ms. 콜라이더 ~4,000개 → 개당 1 µs.
> `TryBuildEntry`가 콜라이더마다 네이티브를 **6번** 불렀다 — null 검사, `enabled`,
> `transform == null`, `TransformVector` ×2, `TransformPoint`.
> `localToWorldMatrix` 한 번으로 합치고 중복 검사를 빼서 **2번**으로. 4.2 → 2.43 ms.

**찾을 패턴:** 루프 안의 `transform.TransformX()`, `.bounds`, `.position`, `== null`,
`GetComponent`, 그리고 **`Dictionary<UnityEngine.Object, T>`** (키 비교가 네이티브 생존 검사를 탄다).

## 4. 코드 고치기 전에 상수부터

이 리포는 튜닝 상수로 일의 **양**을 줄일 수 있게 지어져 있다. 코드 최적화보다 이게 거의 항상 큰 레버다.
전부 `Ballistics.Tuning.cs`.

| 상수 | 무엇을 곱하나 |
|---|---|
| `SpallMaxCount` | 관통 명중 하나당 파편. **평시 바닥** |
| `CollapseFragmentCount` | 죽은 서브셀 하나당 파편. 판당 ×18. **그래프의 봉우리** |
| `MaxSpallDepth` | 파편 세대. 곱하기 항 |
| `MaxFragmentsPerPump` | 틱당 예산. 총량이 아니라 **완충**이다 — 넘친 것은 다음 틱으로 |

`MaxFragmentsPerPump`는 **평시에 안 걸리는 값이어야 한다.** 평시가 이미 문턱 아래면 예산이
한 번도 안 걸리고, 정작 눕히려던 봉우리에서만 뒤늦게 걸린다. 평시 파편 수를 먼저 재고 그 위에 놓는다.

바닥이 낮은데 봉우리만 높으면 **곱하기 항**(`CollapseFragmentCount`, `MaxSpallDepth`)을 건드린다.
바닥이 전체적으로 높으면 `SpallMaxCount`다.

상수를 만졌으면 `Tools > Ballistics > Run Penetration Tests`.

## 5. 개수가 큰 no-op은 나중에

`Tick.BallisticArmor` 21,372 calls / 1.46 ms 같은 줄은 **개당 70 ns의 순수 디스패치**다.
`Armor.OnTick`은 `if (Heat <= 0f) return;`이고, Door·Engine·Tank·CriticalModule은 `OnTick() { }` 빈 몸이다.
`ArmorSkin.LateUpdate`도 같은 종류(필드 비교 둘).

다 합쳐 프레임의 6% 근처. **판 수에 비례해 커지므로 언젠가는 걷어야 하지만, 62%를 두고 이걸 먼저 하면 안 된다.**
큰 것부터.

## 6. 한 번에 하나씩, 매번 다시 잰다

축이 다르면 여러 개 바꿔도 되지만(양 vs 스파이크), **어느 것이 무엇을 움직였는지 읽히게** 적어 준다.
바꾸고 나서 반드시 다시 재게 하고, 다음 병목을 예측해서 말해 준다 — 예측이 틀리면 그것도 정보다.

## 보고 형식

숫자 → 원인 → 고침 → 다음. 산문 금지.

```
TickManager.Late 11.67 / 3틱 = 3.89 ms/tick
└ 72%가 TraceWorld.Pose 하나. 호출당 4.2 ms, 콜라이더 4,000개 → 개당 1 µs = 네이티브 6번

고침: TryBuildEntry 네이티브 6 → 2 (localToWorldMatrix 한 번 + 중복 검사 제거)
기대: Pose 4.2 → 1.5 ms

다음: 이거 줄이면 Dictionary<Collider2D,int> 조회가 1등이 된다. 재보고 판단.
```

## 이 프로젝트에서 이미 확인된 것

- **`Physics2D.Simulate`는 대체로 그냥 비싸다.** 콜라이더 4,000개 / 바디 53개면 틱당 ~1.8 ms가 정상.
  줄이려면 콜라이더 수를 줄여야 하고, 그건 설계 변경이다.
- **`Ship.Split`의 self는 실제 파단이 일어난 프레임에만 크다.** `_dirty` + `RemovalMightSplit` 링 검사로
  이미 게이트돼 있다. 파단 프레임에는 `ThingDef.Spawn` + GC.Alloc 수백이 같이 뜬다 — 그건 일회성이다.
- **`LogStringToConsole` 한 번에 0.25 ms.** 스택 트레이스 캡처 비용.
  `Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None)`로 없앨 수 있는데
  `Debug.Log`의 클릭 가능한 스택을 잃는다. 사용자에게 물어볼 것.
- **`Light2D`를 단 GameObject를 스폰하는 경로는 전부 의심한다.** 개당 ~31 µs + 6 KB 쓰레기이고
  2D 라이트는 개수가 곧 렌더 비용이다. 부스터 불꽃이 이 문제였고 `BoosterTrail`(GraphicsBuffer 링버퍼)로
  옮겼다. 폭발 `Flash`에는 아직 남아 있다.
- **`MemoryManager.FallbackAllocation`이 뜨면** TempJob 할당자가 넘친 것이다. `Allocator.TempJob`
  NativeArray를 파면마다 새로 잡는 자리를 찾아라.
