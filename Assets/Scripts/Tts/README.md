# TTS (Supertonic 3)

대사를 소리로 만든다. 온디바이스 ONNX라 서버도 API 키도 없다.

## 파일 지도

| 파일 | 뭐 하는 놈 |
|---|---|
| `SupertonicCore.cs` | 공식 예제 이식본. 모델 로딩·텍스트 전처리·합성·wav 쓰기. **건드릴 일 없다** |
| `SupertonicTts.cs` | 진입점. 세션 하나를 물고 있고, 태그를 벗기고, 보이스를 캐시한다 |
| `TtsSpeaker.cs` | 런타임 재생 컴포넌트 (B안) |
| `Editor/SupertonicBaker.cs` | 대본을 wav로 미리 굽는다 (A안) |
| `Editor/SupertonicTagProbe.cs` | 표현 태그 후보를 구워서 귀로 확인 |
| `Editor/SupertonicSelfTest.cs` | 자가진단 |
| `StreamingAssets/Supertonic/` | 모델 383MB. **저장소에 없다** (.gitignore) |
| `tools/fetch-supertonic.ps1` | 그 383MB를 받는 스크립트 |
| `tools/voice-style-extractor/` | 내 목소리에서 보이스를 뽑는 도구 |

## 처음 한 번

새로 클론했으면 모델부터 받아야 한다. 없으면 `SupertonicTts.ModelsInstalled`가 false고
모든 메뉴가 거부한다.

```
powershell -ExecutionPolicy Bypass -File tools\fetch-supertonic.ps1
```

받고 나서 `Tools > Supertonic > Run Supertonic Tests`. 전부 PASS면 준비 끝이다.
첫 실행은 모델 로딩으로 5초쯤 걸린다.

## A안 - 대사를 미리 굽는다

`Tools > Supertonic > 대사 전부 굽기`

`StreamingAssets/대사/*.json`을 읽어 `StreamingAssets/TtsBaked/<대본이름>/00.wav`로 굽는다.
대본은 `ScriptManager.LoadScript`가 읽으므로 형식이 코드와 어긋날 일이 없다.

화자별로 목소리가 갈린다:

| author | voice |
|---|---|
| 함장 | M1 |
| 전술 | M2 |
| 기관 | M3 |
| 통신 | F1 |
| 관제 | F2 |

표에 없는 화자는 M1. 표는 `SupertonicBaker.VoiceByAuthor`에 있다.

**이미 있는 wav는 건너뛴다.** 대본 한 줄 고치고 다시 돌리면 그 줄만 새로 굽는다.
전부 다시 굽고 싶으면 `Tools > Supertonic > 구운 wav 전부 지우기`.

빌드에 wav만 들어간다. ONNX 런타임도 383MB 모델도 안 실린다.

## B안 - 런타임에 즉석 합성

빈 오브젝트에 `TtsSpeaker`를 얹는다 (`AudioSource`는 자동으로 붙는다). 인스펙터에서
text를 채우고 컨텍스트 메뉴 **"말해봐"**. 플레이 중에도 된다.

코드에서는:

```csharp
await speaker.SpeakAsync("접촉 소실. 교전 종료.");
```

합성은 백그라운드 스레드에서 돈다. 실측(개발 PC CPU 기준):

| | 시간 |
|---|---|
| 첫 호출 (모델 로딩 포함) | 4.7초 |
| "교전 종료." (1.4초 오디오) | 0.60초 |
| 5.1초짜리 긴 대사 | 1.18초 |

실시간의 약 0.25배. 로딩 화면에서 `TtsSpeaker.Warmup()`을 한 번 불러 두면 첫 4.7초가
감춰진다.

빌드에 383MB가 실린다. 그러면 **모델을 배포하는 것**이므로 라이선스 의무가 붙는다 (아래).

## 보이스 추가하기

`StreamingAssets/Supertonic/voice_styles/` 안의 JSON 하나가 화자 하나다. 파일 이름이
곧 보이스 이름이다. 기본 10종(M1~M5, F1~F5) 외에 넣는 법 셋:

**1. 스타일 블렌딩** — 기존 벡터를 섞는다. GPU도 돈도 필요 없다.
`Style.Ttl`/`Style.Dp`가 그냥 `float[]`이라 가중 평균이면 끝. 벡터 공간이 선형이란
보장은 없으니 들어보고 고르는 방식이다.

**2. Voice Builder** (공식, 유료) — <https://supertonic.supertone.ai/voice-builder>
짧은 녹음을 올리면 Supertonic 3용 JSON을 준다. `voice_styles/`에 넣으면 끝.

**3. 추출기** (`tools/voice-style-extractor/`) — 내 목소리 wav에서 직접 뽑는다.
NVIDIA GPU VRAM 4GB+ 필요, 화자당 3~6분. 자세한 건 그 폴더의 `README.ko.md`.

어느 쪽이든 JSON을 넣은 뒤 `VoiceByAuthor` 표에 이름을 넣으면 그때부터 쓰인다.

## 표현 태그

`<laugh>`, `<breath>`, `<sigh>`를 대사에 그대로 쓰면 모델이 알아듣는다.

```json
{ "message": "살아있네. <laugh> 다행이군.", "author": "함장" }
```

`SupertonicTts.ExpressionTags`에 있는 태그만 통과하고, `<color=...>` 같은 화면용
리치 텍스트는 벗겨진다. 반대로 하지 않은 이유: 오타 하나가 모델에 흘러가면 그 문장을
글자 그대로 읽어버리는데, 대사 수백 줄 중 어느 줄인지 못 찾는다.

공식 문서는 태그가 10종이라고만 하고 목록을 안 밝혔다 (2026-08 기준). 확인된 셋만
넣어놨다. 나머지를 찾으려면 `Tools > Supertonic > 표현 태그 시험 굽기`로 후보 18개를
구워서 들어보고, 실제로 소리가 나는 것만 `ExpressionTags`에 추가해라. 길이로는 판별이
안 된다 — 모델이 모르는 태그를 글자로 읽어도 출력이 그만큼 길어진다.

## 라이선스 의무

코드는 MIT, **모델 가중치는 OpenRAIL-M**이다. 상업 배포 가능하고 로열티도 없지만:

- **A안·B안 공통**: 크레딧에 "AI 합성 음성 (Supertonic 3, Supertone Inc.)" 한 줄.
  부록 A (e) — 기계 생성 콘텐츠를 밝히지 않고 배포하는 것이 금지다.
- **B안만**: 모델 파일을 빌드에 동봉하면 그게 Distribution이다. LICENSE 사본을 같이
  넣고, EULA에 부록 A 사용제한을 강제 조항으로 넣어야 한다 (4조 a·b·d).
- 생성된 wav 자체는 우리 것이다 (6조). Supertone은 출력물에 권리를 주장하지 않는다.
- 동의 없는 실존 인물 목소리 복제 금지 (부록 A (g)).

A안이면 크레딧 한 줄로 끝난다.

## 알아둘 것

**모델이 비결정적이다.** 같은 문장을 두 번 합성하면 파형이 다르다 (노이즈를 샘플링한다).
길이는 같다. 결정론 장부(`Ballistics.Hash` 등)에 TTS를 넣지 마라 — 고정 안 된다.
자가진단이 파형 대신 스타일 벡터를 비교하는 이유도 이것이다.

**JSON 파싱은 `MiniJson`이 한다** (`SupertonicCore.cs` 안). Unity에 `System.Text.Json`이
없어서 갈아끼운 것이고, Newtonsoft를 끌어오는 대신 60줄로 처리했다. 범용 파서가 아니다 —
문자열 값 안에 대괄호나 숫자가 있으면 잘못 읽는다. 다른 JSON에 쓰지 마라.

**win-x64만 들어있다** (`Assets/Plugins/Supertonic/x86_64/`). Linux나 Mac 빌드를 하면
네이티브 DLL이 없어서 죽는다. 필요해지면 같은 nuget 패키지에서 해당 RID의
`onnxruntime` 네이티브를 꺼내 넣으면 된다.

**세션 전체가 락 하나다** (`ponytail:` 주석). 지금은 부르는 자리가 굽기 루프와 대사
한 줄뿐이라 경합이 없다. 화자 둘이 동시에 떠들어야 하면 그때 세션을 쪼개라.

**메모리 383MB.** 에디터에서 굽고 나면 `Tools > Supertonic > 모델 내리기`로 회수해라.
