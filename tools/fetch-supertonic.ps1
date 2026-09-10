# Supertonic 3 모델 가중치를 StreamingAssets로 내려받는다.
#
# 383MB라 저장소에 안 들어간다 (vector_estimator.onnx 하나가 245MB고 GitHub 파일 제한이
# 100MB다). .gitignore에 빠져 있으니 새로 클론했으면 이걸 한 번 돌려라.
#
#   powershell -ExecutionPolicy Bypass -File tools\fetch-supertonic.ps1
#
# 모델: https://huggingface.co/Supertone/supertonic-3  (OpenRAIL-M)
# 코드: https://github.com/supertone-inc/supertonic  (MIT)

$ErrorActionPreference = "Stop"
$base = "https://huggingface.co/Supertone/supertonic-3/resolve/main"
$dest = Join-Path $PSScriptRoot "..\Assets\StreamingAssets\Supertonic"

$files = @(
    "onnx/tts.json",
    "onnx/unicode_indexer.json",
    "onnx/duration_predictor.onnx",
    "onnx/text_encoder.onnx",
    "onnx/vocoder.onnx",
    "onnx/vector_estimator.onnx",
    "voice_styles/M1.json", "voice_styles/M2.json", "voice_styles/M3.json",
    "voice_styles/M4.json", "voice_styles/M5.json",
    "voice_styles/F1.json", "voice_styles/F2.json", "voice_styles/F3.json",
    "voice_styles/F4.json", "voice_styles/F5.json"
)

New-Item -ItemType Directory -Force -Path (Join-Path $dest "onnx") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $dest "voice_styles") | Out-Null

$i = 0
foreach ($f in $files) {
    $i++
    $out = Join-Path $dest $f.Replace("/", "\")

    # 이미 받은 건 건너뛴다. 245MB짜리를 매번 다시 받을 이유가 없다.
    if (Test-Path $out) {
        Write-Host "[$i/$($files.Count)] skip  $f"
        continue
    }

    Write-Host "[$i/$($files.Count)] get   $f"
    $tmp = "$out.part"
    Invoke-WebRequest -Uri "$base/$f" -OutFile $tmp -UseBasicParsing
    Move-Item -Force $tmp $out
}

$mb = [math]::Round((Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
Write-Host ""
Write-Host "완료. $dest ($mb MB)"
Write-Host "Unity에서 Tools > Supertonic > Run Supertonic Tests 로 확인해라."
