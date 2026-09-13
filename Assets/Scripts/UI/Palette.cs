using UnityEngine;

/// <summary>
/// UI 팔레트(오너, 2026-09-11). 이름이 곧 용도다 - 화면마다 색을 새로 고르지 않고 여기 이름을
/// 부른다. 값을 바꾸는 자리는 여기 하나. 판·모듈 그림(def의 tint, 적열 HeatTint)은 아트라
/// 여기 안 온다.
/// </summary>
public static class Palette
{
    public static readonly Color Void = Hex(0x080D14);        // 우주 배경
    public static readonly Color DeepSpace = Hex(0x111D2B);   // UI 패널
    public static readonly Color Bulkhead = Hex(0x283B49);    // 테두리·함선 그림자
    public static readonly Color Steel = Hex(0x71858C);       // 금속·보조 정보
    public static readonly Color Hull = Hex(0xD4D8CD);        // 장갑판·본문
    public static readonly Color Radiance = Hex(0xFFC85A);    // 초방사·핵심 강조
    public static readonly Color Heat = Hex(0xF28A3A);        // 열·추진·폭발
    public static readonly Color Breach = Hex(0xE65345);      // 손상·적대·위험
    public static readonly Color Telemetry = Hex(0x65C8D0);   // 조준·선택·항법
    public static readonly Color Signal = Hex(0xA6C98B);      // 정상·수리 완료

    public static Color WithAlpha(this Color c, float a) => new(c.r, c.g, c.b, a);

    /// <summary>채도를 keep(0~1)만 남긴다. 접촉 마커처럼 늘 떠 있는 것은 원색이면 화면이 시끄럽다.</summary>
    public static Color Muted(this Color c, float keep)
    {
        float grey = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
        return new Color(Mathf.Lerp(grey, c.r, keep), Mathf.Lerp(grey, c.g, keep), Mathf.Lerp(grey, c.b, keep), c.a);
    }

    private static Color Hex(int rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
}
