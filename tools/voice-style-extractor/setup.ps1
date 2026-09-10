# SupertonicTTS 보이스 스타일 추출기 설치.
#
#   powershell -ExecutionPolicy Bypass -File tools\voice-style-extractor\setup.ps1
#
# 하는 일: venv 만들고, CUDA용 torch와 나머지 의존성을 깔고, models/ 를 프로젝트가
# 이미 갖고 있는 383MB 모델 폴더로 연결한다(복사 아님 - 정션).
#
# NVIDIA GPU (VRAM 4GB 이상)가 필요하다. CPU로도 돌긴 하지만 화자당 몇 시간 단위다.

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Set-Location $root

Write-Host "== GPU 확인"
try { nvidia-smi --query-gpu=name,memory.total --format=csv,noheader }
catch { Write-Warning "nvidia-smi가 없다. NVIDIA GPU가 없으면 추출이 사실상 안 돌아간다." }

Write-Host "`n== venv"
if (-not (Test-Path "$root\.venv")) { python -m venv "$root\.venv" }
$py = "$root\.venv\Scripts\python.exe"

Write-Host "`n== torch (CUDA 12.1)"
& $py -m pip install --upgrade pip -q
& $py -m pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu121

Write-Host "`n== 나머지 의존성"
& $py -m pip install -r "$root\requirements.txt"

Write-Host "`n== models/ 연결"
# 383MB를 두 번 두지 않는다. StreamingAssets에 이미 있는 걸 정션으로 가리킨다.
$models = "$root\models"
$target = Resolve-Path "$root\..\..\Assets\StreamingAssets\Supertonic"
if (Test-Path $models) {
    Write-Host "이미 있음: $models"
} else {
    cmd /c mklink /J "$models" "$target" | Out-Null
    Write-Host "$models  ->  $target"
}

if (-not (Test-Path "$models\onnx\vector_estimator.onnx")) {
    Write-Warning "모델이 없다. tools\fetch-supertonic.ps1 을 먼저 돌려라."
}

New-Item -ItemType Directory -Force -Path "$root\wavs" | Out-Null

Write-Host @"

설치 끝.

1. wavs\ 에 목소리 wav를 넣어라. 3~16초, 한 파일에 한 사람.
   파일 이름이 그대로 결과 JSON 이름이 된다 (함장.wav -> 함장.json).

2. 추출:
   .venv\Scripts\python.exe src\run_batch_extract.py --speakers mine --save-wav --out results\mine

3. 결과 results\mine\styles\*.json 을
   Assets\StreamingAssets\Supertonic\voice_styles\ 에 복사하면
   그 이름이 곧 보이스 이름이다. SupertonicBaker의 VoiceByAuthor 표에 넣어 써라.

처음 실행 때 WavLM-Large(약 1.2GB)를 HuggingFace에서 받는다.
"@
