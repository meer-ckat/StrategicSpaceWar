# 보이스 스타일 추출기

내 목소리(또는 동의받은 성우 목소리) wav 한 개에서 Supertonic 스타일 벡터를 뽑는다.
결과 JSON을 `Assets/StreamingAssets/Supertonic/voice_styles/`에 넣으면 그때부터
프리셋 M1~F5와 똑같이 쓸 수 있다.

원본: <https://github.com/kdrkdrkdr/supertonic.embed> (연구용 공개 코드)

## 이게 왜 필요한가

Supertonic은 화자를 가중치가 아니라 **스타일 벡터 하나**로 들고 있다. 그 벡터를 만드는
공식 style encoder는 공개돼 있지 않다. 그래서 이 도구는 역으로 찾는다:

1. WavLM layer-4 거리로 프리셋 10개 중 가장 가까운 것을 시작점으로 고른다
2. 그 벡터로 합성한 소리와 목표 wav를 WavLM layer-4 특징(시간평균 mean/std)으로 비교
3. 손실이 0.30 아래로 내려갈 때까지 벡터를 경사하강으로 갱신

시간축을 평균 내기 때문에 두 오디오가 서로 다른 문장이어도 비교가 된다.

## 설치

```
powershell -ExecutionPolicy Bypass -File tools\voice-style-extractor\setup.ps1
```

**NVIDIA GPU VRAM 4GB 이상**이 필요하다. 화자당 3~6분(batch 16이면 화자당 0.5분).

`models/`는 복사하지 않고 `Assets/StreamingAssets/Supertonic`을 정션으로 가리킨다.
383MB를 두 벌 두지 않기 위해서다.

## 쓰기

```
.venv\Scripts\python.exe src\run_batch_extract.py --speakers mine --save-wav --out results\mine
```

`wavs\함장.wav`를 넣으면 `results\mine\styles\함장.json`이 나온다. 3~16초, 한 파일에
한 사람, 샘플레이트는 아무거나(44.1kHz로 자동 리샘플).

`--save-wav`를 주면 화자마다 `ref.wav`(원본)와 합성 문장 5개가 같이 나온다. 먼저 그걸
들어보고 쓸지 정해라.

### 원본에서 고친 것

`--speakers mine` 소스를 추가했다. 원본은 `wavs/01.wav`처럼 두 자리 숫자 파일만 긁어가서
내 목소리를 넣으려면 이름을 숫자로 바꿔야 했다. `mine`은 `wavs/*.wav`를 전부 읽고 파일
이름을 그대로 화자 ID로 쓴다. 기본값도 `mine`으로 바꿨다.

## Supertonic 3에서 되나

된다. 확인했다.

원본 README는 supertonic-2 자산을 받으라고 하지만, v3 ONNX 네 개 모두 이 저장소의
변환 경로(`onnxslim` -> opset 17 강등 -> `_fix_clip` -> `onnx2torch`)를 통과한다.
스타일 벡터 모양도 v2와 v3이 같다 (`style_ttl [1,50,256]`, `style_dp [1,8,16]`).

주의: **v2로 뽑은 스타일을 v3에 그대로 쓰면 안 된다.** 모양은 같아도 벡터 공간이
다르다 - 공식 Voice Builder도 v2용과 v3용 JSON을 따로 준다. `models/`를 v3(우리
StreamingAssets)로 연결해 두는 이유가 이것이다.

## 품질

논문 수치 (154명 x 5문장):

| | SIM (ECAPA) | WER |
|---|---|---|
| 가장 가까운 프리셋 (최적화 없음) | 0.132 | 1.84% |
| 추출한 스타일 | **0.413** | 3.19% |

참고로 ECAPA 코사인 유사도는 남남끼리 0.118, 같은 사람의 다른 녹음끼리 0.682다.
프리셋은 사실상 남남 수준이고, 추출한 벡터는 "같은 사람의 다른 녹음"의 60% 지점에 온다.

손실을 0.30보다 더 낮추지 마라. 0.24까지 밀면 화자 유사도는 조금 오르지만 WER이
1.1%에서 5.2%로 뛴다. 말이 뭉개진다.

## 라이선스와 책임

- **동의 없는 실존 인물 목소리 복제 금지.** Supertonic 모델 라이선스(OpenRAIL-M)
  부록 A (g)이자 이 저장소 README의 첫 번째 조항이다. 관할에 따라 불법이다.
- 합성 음성임을 배포 시 밝혀야 한다 (부록 A (e)). 게임 크레딧 한 줄이면 된다.
- 이 도구로 만든 스타일 JSON은 `.gitignore`에 넣어뒀다. 목소리는 개인정보다.
