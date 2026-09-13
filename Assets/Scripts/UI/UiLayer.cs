/// <summary>
/// 화면에 겹치는 것들의 순서. **한 자리에 모아 두는 것이 요점이다** - 예전에는 파일마다
/// 자기 상수를 들고 서로를 주석으로만 알았고("정비·항로(880)보다 아래"), 그 주석이
/// 틀리는 날 조용히 깨졌다. 실제로 <c>ControlHints</c>(900)가 <c>ShipSelectScreen</c>(900)과
/// 동률이었는데, <c>GUIManager.BuildDrawRoots</c>의 List.Sort는 불안정 정렬이라 동률끼리는
/// 순서가 아예 정의되지 않는다 - 실행마다 달라질 수 있었다.
///
/// 간격이 100인 것은 화면 하나가 자기 안에서 +1~+20을 쓰기 때문이다(패널 → 글자 → 덮개).
///
/// **레이어로 못 가리는 것이 하나 있다.** <see cref="ShipStatusHud"/>는 GUIManager 밖에서
/// <c>GUI.*</c>를 직접 부르는 별도 OnGUI다. 그리기 순서는 <c>GUI.depth</c>로 고정했다(계기판 1,
/// GUIManager 0 - 낮은 쪽이 위) - 그래서 계기판은 언제나 이 표의 전부보다 아래다. 가리는 것은
/// 여전히 "화면이 열려 있으면 그리지 않는다"뿐이고, 새 전체 화면을 만들면 그 목록에도 넣어야 한다.
/// </summary>
public static class UiLayer
{
    /// <summary>대사창 뒤에 깔리는 무늬. 유일하게 0보다 아래다.</summary>
    public const int Pattern = -100;

    /// <summary>월드 위에 붙는 것 - 접촉 마커, 신호, 트래커, 상태창.</summary>
    public const int Contact = 100;

    /// <summary>조작 힌트. HUD 위, 맵 아래.</summary>
    public const int Hint = 200;

    /// <summary>맵 정보 창(M).</summary>
    public const int Map = 400;

    /// <summary>
    /// 대사창. **맵보다 위다** - 대사는 지금 일어나는 일이라 무엇을 보고 있든 읽혀야 한다.
    /// 전체 화면(<see cref="Screen"/>)일 때는 DialogueManager가 아예 안 그린다.
    /// </summary>
    public const int Dialogue = 600;

    /// <summary>전체 화면 - 정비·항로. 둘이 동시에 열리는 일은 없다.</summary>
    public const int Screen = 800;

    /// <summary>함선 선택. 런의 시작이라 전부 위.</summary>
    public const int ShipSelect = 900;
}
