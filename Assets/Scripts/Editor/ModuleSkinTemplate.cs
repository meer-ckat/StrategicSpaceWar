#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Defs > Export Module Skin Templates.
///
/// <see cref="SolidSkin.skinTexture"/>로 쓸 PNG의 밑판을 def마다 한 장씩 뽑는다. 함선 템플릿
/// (<c>ShipPainter</c>의 "템플릿 뽑기")과 같은 일을 모듈에 하는 것이고, 있는 이유도 같다:
/// **크기를 손으로 못 맞춘다.** pd20은 58x115 px이고, 한 픽셀 틀리면 <see cref="SolidSkin.Fits"/>가
/// 거부한다. 빈 캔버스가 정확한 크기로 나오는 것 자체가 가이드의 절반이다.
///
/// **크기를 여기서 다시 계산하지 않는다.** <see cref="SolidSkin.WantedPixels"/>를 부른다 -
/// 검사하는 쪽과 같은 함수라야 "템플릿대로 그렸는데 거부당한다"가 안 생긴다.
///
/// 파일 이름에 <c>_template</c>을 붙이는 것도 함선 쪽과 같은 이유다. def가 가리키는 이름으로
/// 뽑으면 **가이드선이 그대로 게임 안의 그림이 된다.**
/// </summary>
public static class ModuleSkinTemplate
{
    /// <summary>한 변 상한. 콜라이더를 잘못 적은 def 하나가 에디터를 죽이는 걸 막는다.</summary>
    private const int MaxSide = 4096;

    /// <summary>def가 콜라이더를 안 가진 것(탄)의 기본 크기. SolidSkin.skinSize와 같은 값.</summary>
    private static readonly Vector2 NoColliderSize = new(0.3f, 0.3f);

    /// <summary>
    /// def 파일에서 주 클래스의 필드를 엿본다. <see cref="ThingDef"/>는 콜라이더까지만 알고
    /// <c>muzzleOffset</c>은 <see cref="Gun"/>의 것이라, 그 두 층을 잇는 자리가 필요하다.
    /// JsonUtility가 모르는 키를 버리므로 이 세 개만 담긴 그릇이면 충분하다.
    /// </summary>
    [Serializable]
    private class Peek
    {
        public float muzzleOffset;
        public Vector2 skinSize;
        public string skinTexture;
    }

    [MenuItem("Tools/Defs/Export Module Skin Templates")]
    public static void Run()
    {
        int made = 0;
        int skipped = 0;

        foreach (ThingDef def in DefDatabase.All)
        {
            if (!UsesSolidSkin(def))
            {
                skipped++;
                continue;
            }

            if (Export(def))
                made++;
        }

        AssetDatabase.Refresh();

        Debug.Log($"[ModuleSkinTemplate] 템플릿 {made}장을 {DefDatabase.DefDirectory}에 썼다. "
                + $"({skipped}개는 SolidSkin을 안 쓴다)");
    }

    private static bool UsesSolidSkin(ThingDef def)
    {
        if (def?.comps == null)
            return false;

        foreach (string comp in def.comps)
        {
            if (comp == nameof(SolidSkin))
                return true;
        }

        return false;
    }

    private static bool Export(ThingDef def)
    {
        var peek = new Peek();

        if (!string.IsNullOrEmpty(def.raw))
            JsonUtility.FromJsonOverwrite(def.raw, peek);

        // 콜라이더가 곧 물리 판정 범위다. 없는 것(탄)은 SolidSkin이 skinSize로 그리므로
        // 여기서도 같은 값을 봐야 템플릿과 결과가 맞는다.
        Vector2 size = def.collider != null && def.collider.size.x > 0f && def.collider.size.y > 0f
            ? def.collider.size
            : (peek.skinSize.x > 0f && peek.skinSize.y > 0f ? peek.skinSize : NoColliderSize);

        // 포신은 도는 것에만 있다. 안 도는 모듈은 캔버스가 콜라이더와 같은 크기다.
        bool isGun = def.MainType != null && typeof(Gun).IsAssignableFrom(def.MainType);
        float barrel = isGun ? Mathf.Max(0f, peek.muzzleOffset) : 0f;

        Vector2Int want = SolidSkin.WantedPixels(size, barrel);

        if (want.x <= 0 || want.y <= 0 || want.x > MaxSide || want.y > MaxSide)
        {
            Debug.LogError($"[ModuleSkinTemplate] {def.defName}: {want.x}x{want.y}는 못 뽑는다.");
            return false;
        }

        int ppu = ShipDef.PPU;
        int bodyH = SolidSkin.WantedPixels(size, 0f).y;

        var pixels = new Color32[want.x * want.y];

        var body = new Color32(255, 255, 255, 24);      // 콜라이더 안쪽
        var edge = new Color32(255, 255, 255, 140);     // 콜라이더 테두리
        var grid = new Color32(255, 255, 255, 56);      // 1 m 격자
        var pivot = new Color32(255, 70, 180, 220);     // 회전 중심
        var muzzle = new Color32(80, 220, 255, 200);    // 포구 끝

        // 콜라이더 사각형. 캔버스 아래 끝에 붙는다 - 그림의 아래 끝이 콜라이더의 아래 끝이라는
        // 규칙이 pivot을 정하므로(SolidSkin.PivotY), 템플릿도 같은 자리에 그려야 한다.
        for (int y = 0; y < bodyH && y < want.y; y++)
        for (int x = 0; x < want.x; x++)
        {
            bool onEdge = x == 0 || x == want.x - 1 || y == 0 || y == bodyH - 1;

            pixels[y * want.x + x] = onEdge ? edge : body;
        }

        // 1 m 격자. 원점은 콜라이더의 왼쪽 아래다 - 배 템플릿과 같은 자 위에 서야
        // 모듈 그림과 선체 그림의 칸이 맞는다.
        for (int y = 0; y < want.y; y++)
        for (int x = 0; x < want.x; x++)
        {
            if (x % ppu >= 2 && y % ppu >= 2)
                continue;

            int i = y * want.x + x;

            if (pixels[i].a < grid.a)
                pixels[i] = grid;
        }

        // 회전 중심 = 오브젝트 원점 = 콜라이더 한가운데. 여기가 어긋나면 포탑이 선회할 때
        // 그림이 제자리에서 안 돈다.
        int cx = want.x / 2;
        int cy = bodyH / 2;
        int arm = Mathf.Max(6, ppu / 3);

        for (int d = -arm; d <= arm; d++)
        {
            Plot(pixels, want, cx + d, cy, pivot);
            Plot(pixels, want, cx, cy + d, pivot);
        }

        // 포구 끝. 원점에서 +Y로 muzzleOffset만큼 - 탄이 실제로 태어나는 자리다.
        // 포신을 이 선까지 그리면 그림과 시뮬레이션이 같은 곳을 말한다.
        if (barrel > 0f)
        {
            int my = cy + Mathf.RoundToInt(barrel * ppu);

            for (int x = 0; x < want.x; x++)
                Plot(pixels, want, x, my, muzzle);

            for (int d = -arm; d <= arm; d++)
                Plot(pixels, want, cx, my + d, muzzle);
        }

        var texture = new Texture2D(want.x, want.y, TextureFormat.RGBA32, false);
        texture.SetPixels32(pixels);
        texture.Apply(false);

        string path = Path.Combine(DefDatabase.DefDirectory, $"{Safe(def.defName)}_template.png");
        File.WriteAllBytes(path, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);

        string barrelNote = barrel > 0f ? $", 포신 {barrel} m" : "";
        Debug.Log($"[ModuleSkinTemplate] {def.defName}: {want.x}x{want.y} "
                + $"(콜라이더 {size.x}x{size.y} m{barrelNote})");

        return true;
    }

    private static void Plot(Color32[] pixels, Vector2Int size, int x, int y, Color32 colour)
    {
        if (x < 0 || y < 0 || x >= size.x || y >= size.y)
            return;

        pixels[y * size.x + x] = colour;
    }

    /// <summary>defName에 공백이 흔하고("SuperDuper Engine") 파일 이름으로 못 쓸 글자도 있다.</summary>
    private static string Safe(string name)
    {
        foreach (char bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');

        return name;
    }
}
#endif
