# UI 레퍼런스 조사 (2026-09-11)

오너 요청: UI 원칙, 오픈월드 UI, 에이스 컴뱃 HUD, 타게팅 마크, 화면 밖 목표의 가장자리 마커.
아래는 조사 요약과 이 게임에 닿는 결론. 시각 결정(색·간격·모양)은 오너 몫이다.

## 1. 원칙 셋 (플레이어가 원하는 것)

- **기능이 먼저다.** 정보에 닿게 해 주면 플레이어는 어수선한 화면도 참는다. 몰입감은 주관적이고 개발비를 정당화 못 할 때가 많다. 순수 디제틱(세계 안에 있는 UI)만으로 100% 플레이 가능한 경쟁 게임은 드물다 — 혼합이 답이다. ([Game UI Discoveries](https://www.gamedeveloper.com/design/game-ui-discoveries-what-players-want))
- **네 분류(Fagerholt & Lorentzon, 2009).** 디제틱(세계 안·캐릭터도 봄, 데드스페이스 척추 게이지) / 공간(세계 좌표에 있으나 캐릭터는 못 봄, 선택 링) / 메타(화면 효과, 피 튀김) / 비디제틱(순수 오버레이, 체력바). 이 게임의 브래킷·상태창은 **공간+비디제틱** 혼합이다. ([Beyond the HUD](https://www.researchgate.net/publication/277202228_Beyond_the_HUD_-_User_Interfaces_for_Increased_Player_Immersion_in_FPS_Games))
- **적응형 HUD.** 필요할 때만 뜨고 도달하면 사라진다. 항해 줌 ↔ 전투 줌 전환과 같은 사고 — 상태에 따라 보이는 것이 바뀐다. ([HUD design guide](https://sunstrikestudios.com/en/blog/HUD_design_in_games/))

## 2. 오픈월드 UI

- **세계가 나침반이다.** 원신·젤다 야숨: 지도 아이콘을 줄이고 멀리 보이는 것(산봉우리) → 중간 호기심(폐허·불빛) → 미세 단서(발자국·연기)를 겹쳐 놓는다. 아이콘을 따라가는 순간 탐험은 목록이 된다. ([StraySpark](https://www.strayspark.studio/blog/open-world-design-pacing-player-freedom), [Choost Games](https://medium.com/@choost-games/why-the-best-open-world-games-ignore-their-own-maps-f7b12e7b10c4))
- **미니맵을 없앤 대가는 가시거리다.** 야숨은 지도 없이 되는 이유가 언덕에서 보이기 때문이다. 이 게임은 화면 64 m·센서 1.2 km라 항해 줌(2.5 km)이 그 언덕이다. 줌이 없으면 "아이콘 없음"은 자유가 아니라 맹목이다.
- **Starsector(2D 탑다운 우주, 제일 가까운 레퍼런스).** 트랜스폰더 끈 함대는 "sensor contact"로만 뜨고 가까워질수록 규모 → 선체 종류 → 세력·이름 순으로 드러난다. 미니맵의 동심원은 거리 단위. 우리 `접촉 → 식별(500 m)` 2단계와 같은 뼈대 — 단계를 3~4개로 늘리면 접근이 곧 정보가 된다. ([Starsector Sensors](https://starsector.wiki.gg/wiki/Sensors), [dev blog](https://fractalsoftworks.com/2015/04/13/sensors/))
- **HighFleet.** 레이더 접촉을 방위·신호 크기(선 길이)로만 준다 — 큰 배일수록 긴 선. 거리 없이 "무언가 크다"만 전하는 방식. 우리 신호 브래킷이 방위만인 것과 같은 결정이고, 크기 정보 하나를 더 얹을 수 있다. ([HighFleet Sensors](https://highfleet.fandom.com/wiki/Sensors))
- **Elite Dangerous.** 스캐너 10 km 원반, 접촉은 열 신호 세기로 탐지 거리가 정해진다. 로그 눈금 모드가 가까운 것을 확대. ([Elite HUD](https://elite-dangerous.fandom.com/wiki/HUD/Center), [IFF](https://wiki.alioth.net/index.php/IFF_system))

## 3. 에이스 컴뱃 HUD — 배울 것

- **컨테이너 박스 = 접촉.** 화면 안 모든 표적에 사각 박스. 색이 IFF다: 적 빨강, 아군 파랑/초록, 미확인 노랑(AC7 UNKNOWN은 락온을 유지해야 정체가 드러난다 — 접근·주시가 정보를 산다). ([AC wiki HUD](https://acecombat.wiki.gg/wiki/Head-up_display))
- **선택 표적은 딱 하나가 다르다.** 박스가 커지고 거리 숫자와 이름이 붙고, 나머지는 작은 박스만. 정보 위계가 "선택 하나 > 나머지"로 명확하다.
- **화면 밖 표적은 가장자리 화살표.** 선택 표적만 화살표를 받는다. 전부에 화살표를 주면 테두리가 화살표로 찬다.
- **DLZ(Dynamic Launch Zone).** 오른쪽 세로 눈금에 표적까지의 거리를 캐럿으로, 무기 유효 사거리를 굵은 구간으로. 거리 숫자보다 "사거리 안인가"를 눈금 하나로 답한다. ([TGT](https://acecombat.fandom.com/wiki/TGT))
- **TGT 라벨.** 임무 필수 표적만 TGT 글자. 우리 `SpawnDef.target`이 정확히 이 자리다.

## 4. 가장자리 마커 알고리즘

- **표준 기법.** 화면 중심을 원점으로 옮기고 표적 방향 직선이 어느 변과 먼저 만나는지로 위치를 정한다(기울기 y = mx, 네 변 소거). 화살표 각도는 atan2. 표적이 화면 안이면 마커를 그 자리에 그린다. ([Tuts+](https://code.tutsplus.com/positioning-on-screen-indicators-to-point-to-off-screen-targets--gamedev-6644t))
- **우리 `ContactView.EdgeMarker`는 클램프 방식**(x·y를 각각 사각형에 자른다). 직선-변 교차와 결과가 같은 자리는 모서리 근처뿐이고, 클램프는 모서리에 마커가 몰린다. 방향이 중요해지면(신호가 많아지면) 교차 방식 + 방향 화살표로 바꾼다.
- **패널 회피.** 마커 사각형은 HUD 패널 띠 안쪽(`ShipStatusHud.TopBand/BottomBand`) — 오늘 넣었다.
- **접기.** 같은 방위의 신호를 "신호 ×3"으로 접는 것은 맞다. 에이스 컴뱃이 선택 표적에만 화살표를 주는 것과 같은 이유.

## 5. 이 게임에 닿는 결론

1. **접촉 단계를 늘린다**: 신호(방위만) → 접촉(거리) → 규모(HighFleet식 크기) → 식별(이름·편). 단계마다 브래킷 모양 하나가 바뀐다.
2. **선택 표적 하나만 두껍게.** 나머지 접촉은 작은 박스. 화면 밖 화살표는 선택 표적과 출구만.
3. **사거리 눈금(DLZ) 하나.** 지금 상태창의 거리 숫자를 "사거리 안/밖" 눈금으로 — 조준하는 사람은 숫자를 안 읽는다.
4. **아이콘 대신 흔적.** 항적·빛·잔해 띠가 다음 장소를 가리킨다. 지도 화면은 안 만든다.
5. **팔레트는 Palette.cs 하나**로. 색 = 용도라 IFF 색 규칙이 저절로 선다: Breach 적, Signal 아군, Steel 미확인, Telemetry 항법·선택.

이 문서는 조사다. 4번은 오픈섹터 설계와 묶여 있고 1~3은 `ContactView`/`ShipStatusHud` 데이터층에서 시작한다 — 모양은 오너가 정한다.
