using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Player ship combat HUD.
///
/// AIRFRAME : 현재 함체 손실
/// FLIGHT   : 속도 / 진행방향 / ΔV / 기압
/// WPN      : 탄종 / 포문 수 / 탄약
///
/// 월드에는 velocity와 turret aim만 표시한다.
/// </summary>
public sealed class ShipStatusHud : MonoBehaviour
{
    private static readonly Color HudColor =
        new(0.78f, 0.90f, 1.00f, 1f);

    private static readonly Color DimColor =
        new(0.78f, 0.90f, 1.00f, 0.45f);

    private static readonly Color WarnColor =
        new(1.00f, 0.80f, 0.30f, 1f);

    private static readonly Color CriticalColor =
        new(1.00f, 0.1f, 0.15f, 1f);

    private static readonly Color PanelBg =
        new(0.02f, 0.03f, 0.05f, 0.72f);

    private static readonly Color GunAimColor =
        new(0.78f, 0.90f, 1.00f, 0.15f);

    private static readonly Color LeadColor =
        new(0.78f, 0.90f, 1.00f, 0.60f);

    private const float Margin = 16f;

    private const float AirframeWidth = 300f;
    private const float AirframeHeight = 150f;

    private const float FlightWidth = 240f;
    private const float FlightHeight = 122f;

    private const float WeaponWidth = 300f;

    private const float HeaderHeight = 26f;
    private const float RowHeight = 21f;
    private const float Padding = 10f;

    private const float VelocityPixelsPerSpeed = 2f;
    private const float MaxVelocityLineLength = 180f;
    private const float VelocityLineWidth = 2f;

    private const float GunAimLineLength = 120f;
    private const float GunAimLineWidth = 1f;

    private const float LeadMarkerSize = 6f;

    private const float MinVisibleSpeed = 0.05f;

    // 탄약 시스템이 아직 없다 - 가짜 숫자 대신 지금의 사실(무한)을 적는다.
    // 실제 탄약이 들어오면 여기만 교체하면 된다.
    private const string InfiniteAmmo = "∞";

    private readonly List<WeaponHudEntry> _weapons = new();

    private static GUIStyle _titleStyle;
    private static GUIStyle _leftStyle;
    private static GUIStyle _centerStyle;
    private static GUIStyle _rightStyle;


    private struct WeaponHudEntry
    {
        public string projectile;
        public int guns;
    }


    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (FindFirstObjectByType<ShipStatusHud>() != null)
            return;

        var go = new GameObject("Ship Status HUD");

        DontDestroyOnLoad(go);

        go.AddComponent<ShipStatusHud>();
    }


    private void OnGUI()
    {
        if (Event.current.type != EventType.Repaint)
            return;

        Ship ship = Player();

        if (ship == null)
            return;

        EnsureStyles();

        Camera cam = Camera.main;

        // 월드 정보는 패널보다 먼저 그린다.
        // 패널이 선에 가려지지 않는다.
        // 승무원이 죽으면 조준 정보부터 즉시 끊긴다 - 패널은 하나씩 소등된다.
        if (cam != null && ship.CrewAlive)
        {
            DrawVelocityVector(ship, cam);
            DrawGunAimVectors(ship, cam);
            DrawLeadMarkers(ship, cam);
        }

        // 소등 순서: 무장 -> 비행 -> 함체. 선체 그림이 마지막 숨이다.
        if (BeginSection(ship, 2)) DrawAirframePanel(ship);
        if (BeginSection(ship, 1)) DrawFlightPanel(ship);
        if (BeginSection(ship, 0)) DrawWeaponPanel(ship);

        _sectionDying = false;
        _sectionShake = Vector2.zero;
    }


    // ------------------------------------------------------------
    // 사망 소등
    // ------------------------------------------------------------

    private const float SectionStagger = 0.5f;      // 섹션 사이 간격(초)
    private const float DieFlashSeconds = 0.15f;    // 빨갛게 흔들리는 시간
    private const float DieShakePixels = 3f;

    private static float _deathTime = -1f;
    private static bool _sectionDying;
    private static Vector2 _sectionShake;

    /// <summary>
    /// 이 섹션을 그릴까. 승무원이 살아 있으면 항상 그리고, 죽으면 order 순서대로
    /// 하나씩 꺼진다 - 꺼지기 직전 0.15초 동안 섹션 전체가 빨갛게 흔들린다.
    /// 틴트와 흔들림은 여기서 정하고 Draw* 헬퍼들이 읽는다 - 그리기 코드는 모른다.
    /// </summary>
    private static bool BeginSection(Ship ship, int order)
    {
        _sectionDying = false;
        _sectionShake = Vector2.zero;

        if (ship.CrewAlive)
        {
            _deathTime = -1f;
            return true;
        }

        // 사망 순간을 첫 호출이 적는다. 부활하면 위에서 -1로 돌아간다.
        if (_deathTime < 0f)
            _deathTime = Time.unscaledTime;

        float dieAt = _deathTime + order * SectionStagger;
        float now = Time.unscaledTime;

        if (now >= dieAt + DieFlashSeconds)
            return false;

        if (now < dieAt)
            return true;

        _sectionDying = true;

        // 카메라 Shake와 같은 이유로 UnityEngine.Random을 안 쓴다 - 그림도 결정론이다.
        var rng = new DeterministicRng(
            Ballistics.Hash(0, Core.TickManager.currentTick, order));

        _sectionShake = new Vector2(
            rng.Range(-DieShakePixels, DieShakePixels),
            rng.Range(-DieShakePixels, DieShakePixels));

        return true;
    }

    /// <summary>죽어가는 섹션의 색과 자리. 알파는 원본을 지킨다 - 배경 반투명이 유지된다.</summary>
    private static void ApplySection(ref Rect rect, ref Color color)
    {
        if (_sectionDying)
            color = new Color(Color.red.r, Color.red.g, Color.red.b, color.a);

        rect.position += _sectionShake;
    }


    // ------------------------------------------------------------
    // AIRFRAME
    // ------------------------------------------------------------

    private static void DrawAirframePanel(Ship ship)
    {
        ShipGrid.Map design = ship.DesignMap;
        ShipGrid.Map current = ship.Map;

        if (design == null || current == null)
            return;

        Rect panel = new(
            Margin,
            Screen.height - AirframeHeight - Margin,
            AirframeWidth,
            AirframeHeight
        );

        DrawPanel(panel, "AIRFRAME");

        Rect area = new(
            panel.x + Padding,
            panel.y + HeaderHeight,
            panel.width - Padding * 2f,
            panel.height - HeaderHeight - Padding
        );

        if (design.width <= 0 || design.height <= 0)
            return;

        float cellSize = Mathf.Min(
            area.width / design.width,
            area.height / design.height
        );

        float mapWidth = design.width * cellSize;
        float mapHeight = design.height * cellSize;

        float startX =
            area.x + (area.width - mapWidth) * 0.5f;

        float startY =
            area.y + (area.height - mapHeight) * 0.5f;

        float gap =
            cellSize >= 3f ? 1f : 0f;

        float drawSize =
            Mathf.Max(0.5f, cellSize - gap);

        int currentWidth =
            current.cells.GetLength(0);

        int currentHeight =
            current.cells.GetLength(1);

        // Stamp는 살아 있는 판의 bounding으로 origin을 다시 잡는다 - 가장자리 판이
        // 죽으면 current 격자가 통째로 밀린다. 칸 번호가 아니라 배 로컬 위치가 같은
        // 자리다: design의 (0,0)이 current의 어느 칸인지가 곧 그 차이다.
        // (ToLocal/ToCell이 각자의 origin을 이미 반영한다.)
        Vector2Int shift =
            current.ToCell(design.ToLocal(0, 0));

        for (int col = 0; col < design.width; col++)
        {
            for (int row = 0; row < design.height; row++)
            {
                if (!ShipGrid.Solid(design.cells[col, row]))
                    continue;

                int currentCol = col + shift.x;
                int currentRow = row + shift.y;

                bool alive =
                    currentCol >= 0 &&
                    currentRow >= 0 &&
                    currentCol < currentWidth &&
                    currentRow < currentHeight &&
                    ShipGrid.Solid(current.cells[currentCol, currentRow]);

                DrawRect(
                    new Rect(
                        startX + col * cellSize,
                        startY + row * cellSize,
                        drawSize,
                        drawSize
                    ),
                    alive ? HudColor : CriticalColor
                );
            }
        }
    }


    // ------------------------------------------------------------
    // FLIGHT
    // ------------------------------------------------------------

    private static void DrawFlightPanel(Ship ship)
    {
        Rect panel = new(
            Screen.width - FlightWidth - Margin,
            Margin,
            FlightWidth,
            FlightHeight
        );

        DrawPanel(panel, "FLIGHT");

        Vector2 velocity = ship.velocity;

        float speed = velocity.magnitude;

        // 진행 방향은 월드의 속도 벡터가 이미 그려 준다 - 나침반 숫자는 그 중복이었다.
        // 눈이 세계에서 못 읽는 값은 접근 속도다: 탄속 대비 리드가 여기서 갈린다.
        Ship target = ship.NearestHostile();

        string closing = "—";
        Color closingColor = DimColor;

        if (target != null)
        {
            Vector2 toTarget =
                (Vector2)target.transform.position
                - (Vector2)ship.transform.position;

            if (toTarget.sqrMagnitude > 1e-4f)
            {
                // +면 가까워지는 중. 상대속도를 표적 방향에 투영한 것의 반대 부호다.
                float rate = -Vector2.Dot(
                    target.velocity - ship.velocity,
                    toTarget.normalized
                );

                closing = $"{rate:+0;-0} m/s";
                closingColor = HudColor;
            }
        }

        FuelStatus(
            ship,
            out string deltaV,
            out Color fuelColor
        );

        // 값은 배 전체 평균, 색은 최악의 방. 평균은 방 하나가 진공이어도 90%라고
        // 웃는다 - 숫자는 전체 상태를, 색은 제일 급한 곳을 말해야 한다.
        PressureStatus(
            ship,
            out float pressure,
            out Color pressureColor
        );

        float y =
            panel.y + HeaderHeight;

        DrawValue(
            panel,
            ref y,
            "SPD",
            $"{speed:0} m/s",
            HudColor
        );

        DrawValue(
            panel,
            ref y,
            "CLS",
            closing,
            closingColor
        );

        DrawValue(
            panel,
            ref y,
            "ΔV",
            deltaV,
            fuelColor
        );

        DrawValue(
            panel,
            ref y,
            "PRESS",
            $"{pressure * 100f:0}%",
            pressureColor
        );
    }


    private static void FuelStatus(
        Ship ship,
        out string deltaV,
        out Color color
    )
    {
        if (ship.shipTanks.Count == 0)
        {
            deltaV = "INF";
            color = HudColor;
            return;
        }

        float maxImpulse = 0f;

        for (int i = 0; i < ship.shipTanks.Count; i++)
            maxImpulse += ship.shipTanks[i].impulse;

        float remaining =
            ship.RemainingImpulse();

        float fraction =
            maxImpulse > 0f
                ? remaining / maxImpulse
                : 0f;

        deltaV =
            $"{ship.AvailableDeltaV():0} m/s";

        color =
            StatusColor(fraction);
    }


    private static void PressureStatus(
        Ship ship,
        out float average,
        out Color color
    )
    {
        float air = 0f;
        float volume = 0f;
        float worst = 1f;

        for (int i = 0; i < ship.rooms.Count; i++)
        {
            Room room = ship.rooms[i];

            air += room.air;
            volume += room.Volume;

            if (room.Volume > 0f)
                worst = Mathf.Min(worst, room.Pressure);
        }

        average =
            volume > 0f
                ? air / volume
                : 1f;

        color =
            StatusColor(worst);
    }


    // ------------------------------------------------------------
    // WPN
    // ------------------------------------------------------------

    private void DrawWeaponPanel(Ship ship)
    {
        BuildWeaponEntries(ship);

        int rows =
            Mathf.Max(1, _weapons.Count);

        float height =
            HeaderHeight +
            rows * RowHeight +
            Padding;

        Rect panel = new(
            Screen.width - WeaponWidth - Margin,
            Screen.height - height - Margin,
            WeaponWidth,
            height
        );

        DrawPanel(panel, "WPN");

        float y =
            panel.y + HeaderHeight;

        if (_weapons.Count == 0)
        {
            DrawText(
                new Rect(
                    panel.x + Padding,
                    y,
                    panel.width - Padding * 2f,
                    RowHeight
                ),
                "NO WEAPON",
                DimColor,
                _leftStyle
            );

            return;
        }

        for (int i = 0; i < _weapons.Count; i++)
        {
            WeaponHudEntry entry =
                _weapons[i];

            float x =
                panel.x + Padding;

            float width =
                panel.width - Padding * 2f;

            DrawText(
                new Rect(
                    x,
                    y,
                    width - 100f,
                    RowHeight
                ),
                entry.projectile,
                HudColor,
                _leftStyle
            );

            DrawText(
                new Rect(
                    x + width - 100f,
                    y,
                    45f,
                    RowHeight
                ),
                $"×{entry.guns}",
                DimColor,
                _centerStyle
            );

            DrawText(
                new Rect(
                    x + width - 55f,
                    y,
                    55f,
                    RowHeight
                ),
                InfiniteAmmo,
                DimColor,
                _rightStyle
            );

            y += RowHeight;
        }
    }


    /// <summary>
    /// 같은 projectile을 사용하는 포는 한 줄로 묶는다.
    ///
    /// Dictionary까지 만들 이유가 없는 작은 목록이므로
    /// 단순 선형 탐색한다.
    /// </summary>
    private void BuildWeaponEntries(Ship ship)
    {
        _weapons.Clear();

        for (int i = 0; i < ship.shipGuns.Count; i++)
        {
            Gun gun = ship.shipGuns[i];

            // 잔해로 간 포와 부서진 포는 무장이 아니다.
            if (gun == null || gun.Neutralized || !Ship.StillAboard(gun, ship))
                continue;

            string projectile =
                string.IsNullOrEmpty(gun.projectile)
                    ? "UNKNOWN"
                    : gun.projectile;

            int found = -1;

            for (int j = 0; j < _weapons.Count; j++)
            {
                if (_weapons[j].projectile == projectile)
                {
                    found = j;
                    break;
                }
            }

            if (found >= 0)
            {
                WeaponHudEntry entry =
                    _weapons[found];

                entry.guns++;

                _weapons[found] = entry;
            }
            else
            {
                _weapons.Add(
                    new WeaponHudEntry
                    {
                        projectile = projectile,
                        guns = 1
                    }
                );
            }
        }
    }


    // ------------------------------------------------------------
    // WORLD OVERLAY
    // ------------------------------------------------------------

    private static void DrawVelocityVector(
        Ship ship,
        Camera cam
    )
    {
        Vector2 velocity =
            ship.velocity;

        float speed =
            velocity.magnitude;

        if (speed < MinVisibleSpeed)
            return;

        Vector3 originWorld =
            ship.transform.position;

        Vector2 origin =
            WorldToGui(
                cam,
                originWorld
            );

        Vector2 direction =
            WorldDirectionToGui(
                cam,
                originWorld,
                new Vector3(
                    velocity.x,
                    velocity.y,
                    0f
                )
            );

        if (direction.sqrMagnitude <= 0f)
            return;

        float length =
            Mathf.Min(
                speed * VelocityPixelsPerSpeed,
                MaxVelocityLineLength
            );

        DrawLine(
            origin,
            origin + direction * length,
            HudColor,
            VelocityLineWidth
        );
    }


    /// <summary>
    /// 탄종(muzzleSpeed)별 리드 마커. 마우스를 이 십자에 두면 그 탄이 표적을 요격한다.
    ///
    /// 마커 자리는 표적의 미래 위치가 아니라 <c>표적 + 상대속도 × t</c>다 - 탄이 내 배
    /// 속도를 물려받으므로(Projectile.Launch) 조준 방향은 상대 프레임에서 풀리고, 마우스가
    /// 정하는 것은 포신 방향이라 마커도 그 방향 선상에 있어야 한다. 표적 미래 위치에 찍으면
    /// 내 배 속도만큼 어긋난다 - 탄속 1100에 배속 40이면 2도, fireArc(0.7도)보다 크다.
    ///
    /// 포구 위치·포탑 회전(w×r) 몫은 배 중심으로 근사한다 - 수백 m 사거리에서 반 함체
    /// 오차는 마커 픽셀 하나 아래다.
    /// </summary>
    private static readonly List<float> _leadSpeeds = new();

    private static void DrawLeadMarkers(
        Ship ship,
        Camera cam
    )
    {
        Ship target = ship.NearestHostile();

        if (target == null)
            return;

        _leadSpeeds.Clear();

        for (int i = 0; i < ship.shipGuns.Count; i++)
        {
            Gun gun = ship.shipGuns[i];

            if (gun == null || gun.Neutralized || !Ship.StillAboard(gun, ship))
                continue;

            if (!_leadSpeeds.Contains(gun.muzzleSpeed))
                _leadSpeeds.Add(gun.muzzleSpeed);
        }

        Vector2 d =
            (Vector2)target.transform.position
            - (Vector2)ship.transform.position;

        Vector2 relativeVelocity =
            target.velocity - ship.velocity;

        for (int i = 0; i < _leadSpeeds.Count; i++)
        {
            if (!InterceptTime(d, relativeVelocity, _leadSpeeds[i], out float t))
                continue;

            Vector2 aim =
                (Vector2)target.transform.position
                + relativeVelocity * t;

            Vector2 p = WorldToGui(cam, aim);

            DrawLine(
                p + Vector2.left * LeadMarkerSize,
                p + Vector2.right * LeadMarkerSize,
                LeadColor,
                1f
            );

            DrawLine(
                p + Vector2.up * LeadMarkerSize,
                p + Vector2.down * LeadMarkerSize,
                LeadColor,
                1f
            );
        }
    }


    /// <summary>|d + v·t| = s·t 를 푼다. 최소 양수 근이 요격 시각. 못 따라잡으면 false.</summary>
    private static bool InterceptTime(
        Vector2 d,
        Vector2 v,
        float speed,
        out float t
    )
    {
        float a = v.sqrMagnitude - speed * speed;
        float b = 2f * Vector2.Dot(d, v);
        float c = d.sqrMagnitude;

        t = -1f;

        // 탄속과 상대속도가 같은 퇴화: 선형식 bt + c = 0.
        if (Mathf.Abs(a) < 1e-4f)
        {
            if (b >= -1e-6f)
                return false;

            t = -c / b;
            return t > 0f;
        }

        float disc = b * b - 4f * a * c;

        if (disc < 0f)
            return false;

        float root = Mathf.Sqrt(disc);

        float t0 = (-b - root) / (2f * a);
        float t1 = (-b + root) / (2f * a);

        if (t0 > t1)
            (t0, t1) = (t1, t0);

        t = t0 > 0f ? t0 : t1;
        return t > 0f;
    }


    private static void DrawGunAimVectors(
        Ship ship,
        Camera cam
    )
    {
        for (int i = 0; i < ship.shipGuns.Count; i++)
        {
            Gun gun =
                ship.shipGuns[i];

            // 잔해로 간 포탑은 null이 아니다 - 소속을 다시 확인해야 남의 조준선을 안 그린다.
            if (gun == null || !Ship.StillAboard(gun, ship))
                continue;

            Transform turret =
                gun.Turret;

            if (turret == null)
                continue;

            Vector2 origin =
                WorldToGui(
                    cam,
                    turret.position
                );

            Vector2 direction =
                WorldDirectionToGui(
                    cam,
                    turret.position,
                    turret.up
                );

            if (direction.sqrMagnitude <= 0f)
                continue;

            DrawLine(
                origin,
                origin + direction * GunAimLineLength,
                GunAimColor,
                GunAimLineWidth
            );
        }
    }


    // ------------------------------------------------------------
    // GUI
    // ------------------------------------------------------------

    private static void DrawPanel(
        Rect rect,
        string title
    )
    {
        DrawRect(
            rect,
            PanelBg
        );

        // 에컴 느낌의 최소한의 ㄱ자 테두리.
        DrawRect(
            new Rect(
                rect.x,
                rect.y,
                rect.width,
                1f
            ),
            DimColor
        );

        DrawRect(
            new Rect(
                rect.x,
                rect.y,
                1f,
                rect.height
            ),
            DimColor
        );

        DrawText(
            new Rect(
                rect.x + Padding,
                rect.y,
                rect.width - Padding * 2f,
                HeaderHeight
            ),
            title,
            HudColor,
            _titleStyle
        );
    }


    private static void DrawValue(
        Rect panel,
        ref float y,
        string label,
        string value,
        Color valueColor
    )
    {
        float width =
            panel.width - Padding * 2f;

        float x =
            panel.x + Padding;

        DrawText(
            new Rect(
                x,
                y,
                70f,
                RowHeight
            ),
            label,
            DimColor,
            _leftStyle
        );

        DrawText(
            new Rect(
                x + 70f,
                y,
                width - 70f,
                RowHeight
            ),
            value,
            valueColor,
            _rightStyle
        );

        y += RowHeight;
    }


    private static void DrawRect(
        Rect rect,
        Color color
    )
    {
        ApplySection(ref rect, ref color);

        Color old =
            GUI.color;

        GUI.color =
            color;

        GUI.DrawTexture(
            rect,
            Texture2D.whiteTexture
        );

        GUI.color =
            old;
    }


    private static void DrawText(
        Rect rect,
        string text,
        Color color,
        GUIStyle style
    )
    {
        ApplySection(ref rect, ref color);

        Color old =
            GUI.contentColor;

        GUI.contentColor =
            color;

        GUI.Label(
            rect,
            text,
            style
        );

        GUI.contentColor =
            old;
    }


    private static void DrawLine(
        Vector2 a,
        Vector2 b,
        Color color,
        float width
    )
    {
        Vector2 delta =
            b - a;

        if (delta.sqrMagnitude <= 0.0001f)
            return;

        float angle =
            Mathf.Atan2(
                delta.y,
                delta.x
            )
            * Mathf.Rad2Deg;

        Matrix4x4 oldMatrix =
            GUI.matrix;

        Color oldColor =
            GUI.color;

        GUI.color =
            color;

        GUIUtility.RotateAroundPivot(
            angle,
            a
        );

        GUI.DrawTexture(
            new Rect(
                a.x,
                a.y - width * 0.5f,
                delta.magnitude,
                width
            ),
            Texture2D.whiteTexture
        );

        GUI.matrix =
            oldMatrix;

        GUI.color =
            oldColor;
    }


    private static void EnsureStyles()
    {
        if (_titleStyle != null)
            return;

        _titleStyle =
            new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };

        _leftStyle =
            new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };

        _centerStyle =
            new GUIStyle(_leftStyle)
            {
                alignment = TextAnchor.MiddleCenter
            };

        _rightStyle =
            new GUIStyle(_leftStyle)
            {
                alignment = TextAnchor.MiddleRight
            };
    }


    // ------------------------------------------------------------
    // COORDINATES
    // ------------------------------------------------------------

    private static Vector2 WorldToGui(
        Camera cam,
        Vector3 world
    )
    {
        Vector3 screen =
            cam.WorldToScreenPoint(world);

        return new Vector2(
            screen.x,
            Screen.height - screen.y
        );
    }


    private static Vector2 WorldDirectionToGui(
        Camera cam,
        Vector3 origin,
        Vector3 direction
    )
    {
        if (direction.sqrMagnitude <= 0.000001f)
            return Vector2.zero;

        Vector2 a =
            WorldToGui(
                cam,
                origin
            );

        Vector2 b =
            WorldToGui(
                cam,
                origin + direction.normalized
            );

        Vector2 delta =
            b - a;

        return delta.sqrMagnitude > 0.000001f
            ? delta.normalized
            : Vector2.zero;
    }


    // ------------------------------------------------------------
    // STATUS
    // ------------------------------------------------------------

    private static Color StatusColor(
        float fraction
    )
    {
        if (fraction < 0.15f)
            return CriticalColor;

        if (fraction < 0.40f)
            return WarnColor;

        return HudColor;
    }


    private static Ship Player()
    {
        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship ship =
                Ship.All[i];

            if (
                ship != null &&
                ship.IsPlayerControlled
            )
            {
                return ship;
            }
        }

        return null;
    }
}
