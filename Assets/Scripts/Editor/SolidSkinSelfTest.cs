#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

/// <summary>
/// Tools > Ship > Run Solid Skin Tests.
/// pivot과 크기 검사는 순수 함수라 텍스처도 플레이 모드도 필요 없다. 포신이 제자리에 서느냐가
/// 전부 <see cref="SolidSkin.PivotY"/> 한 줄에 달려 있다.
/// </summary>
public static class SolidSkinSelfTest
{
    private static int _pass;
    private static int _fail;

    [MenuItem("Tools/Ship/Run Solid Skin Tests")]
    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        int ppu = ShipDef.PPU;

        // 그림이 콜라이더와 같은 높이면 회전축이 한가운데다.
        {
            Check("pivot: 같은 높이면 0.5",
                Mathf.Approximately(SolidSkin.PivotY(1f, ppu), 0.5f));
        }

        // 포신이 자란 만큼 회전축이 아래로 내려간다. 1 m 몸통 + 1 m 포신이면 아래에서 1/4.
        {
            Check("pivot: 몸통 1 m + 포신 1 m -> 0.25",
                Mathf.Approximately(SolidSkin.PivotY(1f, ppu * 2), 0.25f));
            Check("pivot: 몸통 1 m + 포신 2.6 m -> 아래에서 1/7.2",
                Mathf.Abs(SolidSkin.PivotY(1f, Mathf.RoundToInt(ppu * 3.6f)) - 0.5f / 3.6f) < 0.001f);
        }

        // 회전축이 그림 아래쪽 절반 안에 있어야 한다. 넘으면 포신이 배 안으로 파고든다.
        {
            Check("pivot: 포신이 길수록 낮아진다",
                SolidSkin.PivotY(1f, ppu * 4) < SolidSkin.PivotY(1f, ppu * 2));
        }

        // 가로는 딱 맞아야 한다. 안 맞는데 늘려 쓰면 증상이 "좀 어긋난다"뿐이라 못 찾는다.
        {
            Vector2 one = Vector2.one;

            Check("fits: 딱 맞는 그림", SolidSkin.Fits(one, ppu, ppu, out _));
            Check("fits: 세로가 남는 것은 포신이다", SolidSkin.Fits(one, ppu, ppu * 3, out _));
            Check("fits: 가로가 다르면 거부", !SolidSkin.Fits(one, ppu * 2, ppu * 2, out _));
            Check("fits: 세로가 모자라면 거부", !SolidSkin.Fits(one, ppu, ppu / 2, out _));
            Check("fits: 반 픽셀은 봐준다", SolidSkin.Fits(one, ppu + 1, ppu, out _));
        }

        // 템플릿 생성기와 검사가 같은 식을 봐야 한다. 갈라지면 "템플릿대로 그렸는데 거부당한다"가
        // 되고, 그때 누가 틀렸는지 알 방법이 없다.
        {
            Check("wanted: 1x1 m + 포신 1 m",
                SolidSkin.WantedPixels(Vector2.one, 1f) == new Vector2Int(ppu, ppu * 2));
            Check("wanted: 포신 없으면 콜라이더 그대로",
                SolidSkin.WantedPixels(Vector2.one, 0f) == new Vector2Int(ppu, ppu));
            Check("wanted: pd20 0.6x0.6 + 0.6 -> 58x115",
                SolidSkin.WantedPixels(new Vector2(0.6f, 0.6f), 0.6f) == new Vector2Int(58, 115));

            Vector2Int m7 = SolidSkin.WantedPixels(Vector2.one, 1f);
            Check("wanted: 뽑은 크기는 검사를 통과한다",
                SolidSkin.Fits(Vector2.one, m7.x, m7.y, out _));
        }

        Debug.Log($"[SolidSkinSelfTest] {_pass} passed, {_fail} failed.");
    }

    private static void Check(string what, bool ok)
    {
        if (ok)
        {
            _pass++;
            return;
        }

        _fail++;
        Debug.LogError($"[SolidSkinSelfTest] FAIL: {what}");
    }
}
#endif
