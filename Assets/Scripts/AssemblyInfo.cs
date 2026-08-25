using System.Runtime.CompilerServices;

// 자기 검사(SelfTest)는 편집기 어셈블리에 있고, 검사 대상은 대개 internal이다 -
// PivotY·WantedPixels·FilePath처럼 밖에서 부를 이유가 없는 것들이라 public으로 올리면
// 그 사실이 거짓이 된다. 어셈블리를 가르기 전에는 전부 한 덩어리라 그냥 보였다.
//
// **public으로 올리지 않는 것이 요점이다.** 검사를 위해 접근을 넓히면 다음 사람이 그것을
// 정식 API로 읽고, 그때부터 진짜 밖에서 불린다.
[assembly: InternalsVisibleTo("SUPERRADIANCE.Editor")]
