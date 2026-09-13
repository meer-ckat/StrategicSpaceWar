#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

/// <summary>
/// Tools > Ballistics > Run Penetration Tests.
/// PenetrationManager.Resolve is pure, so these need no scene, no GameObject, no play mode.
/// Cases from the Phase A design doc, minus the ones that require real colliders
/// (A->B->A ricochet chain, two thin walls in one tick) - those need the firing range.
/// </summary>
public static class PenetrationSelfTest
{
    private static int _pass;
    private static int _fail;

    [MenuItem("Tools/Ballistics/Run Penetration Tests")]
    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        PolygonTests();
        Check("spall trace IJobParallelFor", SpallResolver.TraceJobSelfTest());
        Check("spall budget defers but always drains", SpallResolver.FragmentBudgetSelfTest());
        Check("trace world cached source follows transform", TraceWorld.CachedSourceSelfTest());
        Check("ram sweep capsule keeps contacts, drops the rear", RamImpact.SweptCircleSelfTest());

        {
            // 정면 후퇴: 200 m 앞의 표적이 탄과 같은 선 위를 20 m/s로 멀어진다.
            // 1100 m/s 탄은 (1100-20) m/s로 좁히므로 t = 200/1080.
            Check("intercept closes at the speed difference",
                Ballistics.InterceptTime(Vector2.right * 200f, Vector2.right * 20f, 1100f, out float closing)
                // 판별식이 1.9e11이라 float 유효숫자가 7자리다. 허용오차가 이 이하일 이유가 없다.
                && Mathf.Abs(closing - 200f / 1080f) < 1e-3f);

            // 순수 횡단: 조준점은 표적 앞이고, 리드 거리는 상대속도 x t다.
            // 이 값이 fireArc보다 크다는 것이 리드 조준을 넣은 이유 그 자체다.
            bool cross = Ballistics.InterceptTime(
                Vector2.right * 200f, Vector2.up * 30f, 1100f, out float t);

            Check("crossing target leads ahead of its own position",
                cross && t > 0f && 30f * t > 5f);

            // 탄보다 빠른 표적은 못 잡는다. Gun이 이 경우 리드를 아예 안 거는 근거.
            Check("no solution when the target outruns the shell",
                !Ballistics.InterceptTime(Vector2.right * 200f, Vector2.right * 50f, 40f, out _));
        }

        {
            const float shellMass = 5f;
            Vector2 incoming = Vector2.right * 900f;
            Vector2 fastExit = Ballistics.ImpactImpulse(
                shellMass, incoming, Vector2.right * 800f);
            Vector2 slowExit = Ballistics.ImpactImpulse(
                shellMass, incoming, Vector2.right * 400f);
            Vector2 blocked = Ballistics.ImpactImpulse(
                shellMass, incoming, Vector2.zero);

            Check("impact impulse is projectile momentum loss",
                Mathf.Approximately(fastExit.x, 500f)
                && Mathf.Approximately(slowExit.x, 2500f)
                && Mathf.Approximately(blocked.x, 4500f)
                && blocked.x > slowExit.x && slowExit.x > fastExit.x,
                default);
        }

        Vector2 head = Vector2.down;   // straight into an upward-facing plate

        // 0deg, 3x penetration -> clean pass-through
        {
            var r = PenetrationManager.Resolve(Shell(head, 900f, 300f, 800f), Plate(100f));
            Check("0deg 3x pen", r.outcome == HitOutcome.Penetrated
                && r.newState == ShellState.Intact
                && r.newVelocity.magnitude >= 900f * 0.9f, r);
        }

        // 0deg, very fast, RHA ~= penetration -> shatter gap
        {
            var r = PenetrationManager.Resolve(Shell(head, 1600f, 100f, 800f), Plate(100f));
            Check("0deg shatter gap", r.newState == ShellState.Shattered
                && r.outcome == HitOutcome.Blocked, r);
        }

        // 0deg, very fast, thin plate -> no shatter, resistance is what breaks shells
        {
            var r = PenetrationManager.Resolve(Shell(head, 1600f, 500f, 800f), Plate(5f));
            Check("0deg fast thin plate", r.outcome == HitOutcome.Penetrated
                && r.newState == ShellState.Intact, r);
        }

        // Penetrated + Shattered must be reachable: shatter, still overmatch the plate
        {
            var r = PenetrationManager.Resolve(Shell(head, 6400f, 500f, 800f), Plate(100f));
            Check("penetrated + shattered exists", r.outcome == HitOutcome.Penetrated
                && r.newState == ShellState.Shattered, r);
        }

        // 60deg, 1.2x penetration -> effective RHA doubles -> blocked
        {
            var r = PenetrationManager.Resolve(Shell(Angled(60f), 900f, 120f, 800f), Plate(100f));
            Check("60deg 1.2x pen", r.outcome == HitOutcome.Blocked, r);
        }

        // 80deg, overmatch 1.0 -> bounce
        {
            var r = PenetrationManager.Resolve(
                Shell(Angled(80f), 900f, 300f, 800f), Plate(100f, 0.1f));
            Check("80deg overmatch 1.0", r.outcome == HitOutcome.Ricochet, r);
        }

        // overmatch 3.0 raises crit to 80deg, so 79deg no longer bounces.
        // Not bouncing is not the same as getting through: pen < effective RHA -> blocked.
        {
            var r = PenetrationManager.Resolve(
                Shell(Angled(79f), 900f, 300f, 800f), Plate(100f, 0.1f / 3f));
            Check("79deg overmatch 3.0 blocked", r.outcome == HitOutcome.Blocked, r);
        }

        // same angle, enough penetration -> through
        {
            var r = PenetrationManager.Resolve(
                Shell(Angled(79f), 900f, 1200f, 800f), Plate(100f, 0.1f / 3f));
            Check("79deg overmatch 3.0 penetrated", r.outcome == HitOutcome.Penetrated, r);
        }

        // marginal penetration -> Recht-Ipson leaves almost nothing
        {
            var r = PenetrationManager.Resolve(Shell(head, 800f, 102f / 0.95f, 800f), Plate(100f));
            Check("marginal penetration", r.outcome == HitOutcome.Penetrated
                && r.newVelocity.magnitude <= 800f * 0.20f, r);
        }

        // attack ratio 0.1 -> BB gun cannot sand a battleship down
        {
            var r = PenetrationManager.Resolve(Shell(head, 900f, 10f / 0.95f, 800f), Plate(100f));
            Check("attack ratio 0.1 no damage", r.outcome == HitOutcome.Blocked
                && Mathf.Approximately(r.armorDamage, 0f), r);
        }

        // edge hit: whichever order Unity hands the surfaces back, same answer
        {
            var p = Shell(new Vector2(-0.6f, -0.8f), 900f, 400f, 800f);

            var a = Edge(Vector2.up, 100f, Vector2.right, 300f);
            var b = Edge(Vector2.right, 300f, Vector2.up, 100f);

            var ra = PenetrationManager.Resolve(p, a);
            var rb = PenetrationManager.Resolve(p, b);

            Check("edge order determinism", ra.outcome == rb.outcome
                && Mathf.Approximately(ra.effectiveRHA, rb.effectiveRHA)
                && Mathf.Approximately(ra.severity, rb.severity), ra);
        }

        // seam between two colliders: every reported normal is parallel to travel.
        // Must read as a head-on hit, not a 90 degree air ricochet and not a pass-through.
        {
            var p = Shell(Vector2.down, 900f, 300f, 800f);

            var r = PenetrationManager.Resolve(
                p, Edge(Vector2.right, 100f, Vector2.left, 100f));

            Check("seam normals fall back to normal incidence",
                r.judgeIndex < 0
                && r.angleDeg < 0.01f
                && r.outcome != HitOutcome.Ricochet, r);
        }

        // A shell that gets through drills a channel: every sub-cell on the line loses
        // integrity, not just the one it entered through. Grid-size agnostic on purpose -
        // these must hold at 3x3, 6x6, or anything else.
        {
            int n = Ballistics.SubGrid;
            var w = new float[Ballistics.SubCount];

            // straight up the middle of one column
            bool ok = Ballistics.SubCellPath(new Vector2(0f, -0.5f), Vector2.up, Vector2.one, w);

            int column = Ballistics.SubIndex(Vector2.zero, Vector2.one) % n;
            int touched = 0;
            bool stayedInColumn = true;
            float sum = 0f;

            for (int i = 0; i < w.Length; i++)
            {
                sum += w[i];

                if (w[i] <= 0f)
                    continue;

                touched++;

                if (i % n != column)
                    stayedInColumn = false;
            }

            Check("penetration channel crosses the whole column",
                ok
                && touched == n                     // one sub-cell per row, all of them
                && stayedInColumn
                && Mathf.Abs(sum - 1f) < 1e-3f,     // energy budget conserved
                default);
        }

        // diagonal entry walks across columns too
        {
            int n = Ballistics.SubGrid;
            var w = new float[Ballistics.SubCount];

            Ballistics.SubCellPath(new Vector2(-0.5f, -0.5f), new Vector2(1f, 1f), Vector2.one, w);

            int touched = 0;
            float sum = 0f;
            for (int i = 0; i < w.Length; i++) { if (w[i] > 0f) touched++; sum += w[i]; }

            Check("diagonal channel touches multiple sub-cells",
                touched >= n && Mathf.Abs(sum - 1f) < 1e-3f, default);
        }

        // The grid must track the real collider size. With a hard-coded 1m grid on a 0.5m
        // collider the entry lands in column 1 instead of 0 and the channel runs out past
        // the plate - which is exactly what a shallow penetration looked like on screen.
        {
            int n = Ballistics.SubGrid;
            var w = new float[Ballistics.SubCount];
            var half = new Vector2(0.5f, 0.5f);

            Check("half-size cell: entry sits in column 0",
                Ballistics.SubIndex(new Vector2(-0.25f, 0f), half) % n == 0, default);

            Ballistics.SubCellPath(new Vector2(-0.25f, 0f), Vector2.right, half, w);

            int touched = 0;
            float sum = 0f;
            for (int i = 0; i < w.Length; i++) { if (w[i] > 0f) touched++; sum += w[i]; }

            Check("half-size cell: channel spans the whole row",
                touched == n && Mathf.Abs(sum - 1f) < 1e-3f, default);
        }

        // Channel-weighted RHA must not inflate: a fresh cell still reads its nominal value.
        // Damaging only the entry sub-cell must barely move it - the whole line has to go.
        {
            int n = Ballistics.SubGrid;
            var w = new float[Ballistics.SubCount];
            var hp = new float[Ballistics.SubCount];

            for (int i = 0; i < hp.Length; i++) hp[i] = 1f;

            Ballistics.SubCellPath(new Vector2(-0.5f, 0f), Vector2.right, Vector2.one, w);

            float fresh = 0f;
            for (int i = 0; i < w.Length; i++) fresh += w[i] * 100f * Ballistics.RhaCurve(hp[i]);

            // gut the entry sub-cell only
            hp[Ballistics.SubIndex(new Vector2(-0.5f, 0f), Vector2.one)] = 0f;

            float chewed = 0f;
            for (int i = 0; i < w.Length; i++) chewed += w[i] * 100f * Ballistics.RhaCurve(hp[i]);

            Check("channel RHA: fresh cell reads nominal",
                Mathf.Abs(fresh - 100f) < 0.1f, default);

            // one dead cell out of n costs roughly 1/n, not the whole plate
            Check("channel RHA: one dead sub-cell is not a free hole",
                chewed > 100f * (1f - 1.5f / n), default);
        }

        // A blocked round stopped partway chews that much of the line. Crediting all of it
        // to the entry sub-cell is what made the far side of a cell behave like air.
        {
            int n = Ballistics.SubGrid;
            var full = new float[Ballistics.SubCount];
            var half = new float[Ballistics.SubCount];

            Ballistics.SubCellPath(new Vector2(-0.5f, 0f), Vector2.right, Vector2.one, full);
            Ballistics.SubCellPath(new Vector2(-0.5f, 0f), Vector2.right, Vector2.one, half, 0.5f);

            int deep = 0, shallow = 0;
            float sum = 0f;

            for (int i = 0; i < full.Length; i++)
            {
                if (full[i] > 0f) deep++;
                if (half[i] > 0f) shallow++;
                sum += half[i];
            }

            Check("blocked round only chews as far as it got",
                deep == n
                && shallow > 1                       // more than the entry cell
                && shallow < deep                    // but not the whole line
                && Mathf.Abs(sum - 1f) < 1e-3f,      // same energy, just less spread out
                default);
        }

        // A shell wider than the plate starts half its lanes off the plate. SubIndex clamps
        // anything outside onto the border, so those lanes used to pile their whole weight
        // on the edge columns - the fat gun that only ever ate the rim of a plate.
        {
            int n = Ballistics.SubGrid;
            var w = new float[Ballistics.SubCount];

            // 1 m plate, 2 m shell, straight in through the bottom face
            Ballistics.SubCellPath(
                new Vector2(0f, -0.5f), Vector2.up, Vector2.one, w, 1f, 2f);

            float sum = 0f;
            float rim = 0f;
            float middle = 0f;

            for (int i = 0; i < w.Length; i++)
            {
                sum += w[i];

                int col = i % n;

                if (col == 0 || col == n - 1)
                    rim += w[i];
                else
                    middle += w[i];
            }

            Check("overmatching shell does not pile onto the rim",
                Mathf.Abs(sum - 1f) < 1e-3f
                && middle > rim,
                default);
        }

        // 판에서 떨어져 나간 서브셀은 아무것도 떠받치지 않으면서 RHA를 낸다 - 허공이 탄을
        // 막는다. 가장 큰 연결 성분만 남기고, 8방향으로 잇는다(4방향으로 보면 대각으로만
        // 이어진 멀쩡한 판이 두 조각으로 갈린다).
        {
            int n = Ballistics.SubGrid;

            // 1. 멀쩡한 판은 통째로 살아남는다
            Check("성분: 멀쩡한 판은 전부 남는다",
                Ballistics.LargestLivingComponent(Ballistics.SubMaskFull)
                    == Ballistics.SubMaskFull,
                default);

            // 2. 큰 덩어리 + 외딴 칸 하나 -> 외딴 칸은 버려진다
            ulong column = (1UL << 0) | (1UL << n) | (1UL << (2 * n));  // 0열 세 칸
            int lonely = (n - 1) * n + (n - 1);                         // 반대편 구석
            ulong kept = Ballistics.LargestLivingComponent(column | (1UL << lonely));

            Check("성분: 외딴 서브셀은 부서진 것으로 친다", kept == column, default);

            // 3. 대각으로만 닿은 두 칸은 한 덩어리다 (8방향)
            ulong diagonal = (1UL << 0) | (1UL << (n + 1));

            Check("성분: 대각 연결은 끊지 않는다",
                Ballistics.LargestLivingComponent(diagonal) == diagonal, default);

            // 4. 전멸. 아무것도 표시하지 않고 터지지도 않는다
            Check("성분: 전멸한 판은 남는 칸이 없다",
                Ballistics.LargestLivingComponent(0UL) == 0UL, default);

            // 5. 비트 팽창 대 라벨 BFS 무작위 대조. 열 경계 감김(열0->열5) 같은 비트
            //    실수는 특정 모양에서만 드러나므로, 손으로 짠 케이스 넷으로는 부족하다.
            //    레퍼런스는 옛 구현의 축약이고 여기 테스트에만 산다.
            var rng = new System.Random(20260829);
            bool agree = true;

            for (int trial = 0; trial < 512 && agree; trial++)
            {
                ulong alive = ((ulong)(uint)rng.Next() << 32 | (uint)rng.Next())
                    & Ballistics.SubMaskFull;

                agree = Ballistics.LargestLivingComponent(alive)
                    == ReferenceLargestComponent(alive);
            }

            Check("성분: 비트보드가 라벨 BFS와 512판 일치한다", agree, default);
        }

        // Hit points land exactly on sub-cell boundaries constantly - every shot on a grid
        // line or a corner. A bare floor() there can name the sub-cell the shell is leaving.
        // Sweep the whole perimeter at every angle and demand the entry cell is always one
        // the channel actually touched.
        {
            var w = new float[Ballistics.SubCount];
            var size = Vector2.one;

            int bad = 0;
            int nan = 0;

            for (int side = 0; side < 4; side++)
            for (int p = 0; p <= 60; p++)
            for (int a = 0; a <= 45; a++)
            {
                float u = -0.5f + p / 60f;

                Vector2 entry = side switch
                {
                    0 => new Vector2(-0.5f, u),
                    1 => new Vector2(0.5f, u),
                    2 => new Vector2(u, -0.5f),
                    _ => new Vector2(u, 0.5f),
                };

                Vector2 inward = side switch
                {
                    0 => Vector2.right,
                    1 => Vector2.left,
                    2 => Vector2.up,
                    _ => Vector2.down,
                };

                Vector2 d = Ballistics.Rotate(inward, (a / 45f) * 180f - 90f);

                if (!Ballistics.SubCellPath(entry, d, size, w))
                    continue;

                float sum = 0f;
                for (int i = 0; i < w.Length; i++)
                {
                    if (float.IsNaN(w[i])) nan++;
                    sum += w[i];
                }

                if (Mathf.Abs(sum - 1f) > 1e-3f)
                    bad++;

                if (w[Ballistics.EntrySubIndex(entry, d, size)] <= 0f)
                    bad++;
            }

            Check("perimeter sweep: entry cell is always on the channel", bad == 0 && nan == 0, default);
        }

        // The penetration formula's units are arbitrary; penetrationK is what anchors them
        // to millimetres. Pin the default against real rounds three orders of mass apart,
        // so nobody has to notice a machine gun out-penetrating a tank gun on screen.
        {
            const float K = 0.18f;

            float fifty = Ballistics.Penetration(K, 1f, 890f, 0.046f, 12.7f);
            float auto20 = Ballistics.Penetration(K, 1f, 830f, 0.130f, 20f);
            float big = Ballistics.Penetration(K, 1f, 950f, 28f, 128f);

            Check($"default K: .50 cal ~20mm (got {fifty:F0})",
                fifty > 14f && fifty < 28f, default);

            Check($"default K: 20mm AP ~30mm (got {auto20:F0})",
                auto20 > 21f && auto20 < 42f, default);

            Check($"default K: 128mm APCBC ~200mm (got {big:F0})",
                big > 140f && big < 280f, default);

            // and the ordering itself must never invert
            Check("default K: bigger gun always out-penetrates smaller",
                big > auto20 && auto20 > fifty, default);
        }

        /*
        파쇄는 어디서 깨지든 실물을 남긴다 — 포탄 질량은 사라지지 않는다.
        관통 중 파쇄는 shatterVelocity의 6배가 필요해 실제 포로는 못 낸다.
        아래 through 케이스는 픽스처가 억지로 만든 것이고, 실전에서 켜지는 것은 onFace 쪽이다.
        */

        {
            var through = PenetrationManager.Resolve(
                Shell(head, 6400f, 500f, 800f), Plate(100f));

            var onFace = PenetrationManager.Resolve(
                Shell(head, 1600f, 100f, 800f), Plate(100f));

            Check("penetrated + shattered leaves real fragments",
                through.outcome == HitOutcome.Penetrated
                && through.newState == ShellState.Shattered
                && through.heavySpall, through);

            Check("blocked + shattered leaves real fragments too",
                onFace.outcome == HitOutcome.Blocked
                && onFace.newState == ShellState.Shattered
                && onFace.heavySpall, onFace);

            // a clean penetration must not spawn debris either
            var clean = PenetrationManager.Resolve(Shell(head, 900f, 300f, 800f), Plate(100f));

            Check("clean penetration leaves no fragments",
                clean.newState == ShellState.Intact && !clean.heavySpall, clean);
        }

        // same seed -> same spall pattern
        {
            var x = new DeterministicRng(12345u);
            var y = new DeterministicRng(12345u);

            bool same = true;
            for (int i = 0; i < 32; i++)
                same &= Mathf.Approximately(x.Next01(), y.Next01());

            Check("spall rng determinism", same, default);
        }

        // armor remembers: a chewed sub-cell is genuinely weaker
        {
            bool ok = Ballistics.RhaCurve(1f) > Ballistics.RhaCurve(0.6f)
                && Ballistics.RhaCurve(0.6f) > Ballistics.RhaCurve(0.25f)
                && Mathf.Approximately(Ballistics.RhaCurve(0f), 0f)
                && Mathf.Approximately(Ballistics.RhaCurve(0.6f), 0.9f);

            Check("rha curve monotone", ok, default);
        }

        // 유폭의 두 매질이 같은 눈금을 쓴다. 구조 전도(Conduct)는 컷오프에서 멈추고,
        // 자유 공간(Radiate)은 BlastRadius에서 멈춘다 - 그 둘이 같은 지점이어야 원이 하나다.
        // BlastRadius를 상수로 손수 적어 넣는 순간 여기서 걸린다.
        {
            bool ok = true;
            float last = -1f;

            // 세기마다 반경이 다르다. 각각에서 "반경에서 딱 하한"이 성립해야 하고, 세기가
            // 커지면 반경도 커져야 한다 - 비율 컷오프 시절에는 이 단조성이 아예 없었다.
            foreach (float damage in new[] { 90f, 840f, 1600f, 3200f })
            {
                float r = Ballistics.BlastRadiusFor(damage);
                float atRadius = damage * Mathf.Pow(Ballistics.BlastFalloff, r);

                ok &= Mathf.Abs(atRadius - Ballistics.BlastFloor) < 1e-2f
                    && damage * Mathf.Pow(Ballistics.BlastFalloff, r - 1f) > Ballistics.BlastFloor
                    && r > last;

                last = r;
            }

            ok &= Ballistics.BlastRadiusFor(Ballistics.BlastFloor * 0.5f) <= 0f;

            Check($"blast radius scales with damage (90 = {Ballistics.BlastRadiusFor(90f):0.00} m, "
                + $"3200 = {Ballistics.BlastRadiusFor(3200f):0.00} m)", ok, default);
        }

        Debug.Log($"[Ballistics] {_pass} passed, {_fail} failed.");
    }

    /// <summary>Direction hitting an upward-facing plate at the given angle from its normal.</summary>
    private static Vector2 Angled(float degFromNormal)
    {
        float r = degFromNormal * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(r), -Mathf.Cos(r));
    }

    private static ProjectileState Shell(
        Vector2 dir, float speed, float targetPenetration, float shatterVelocity,
        float caliber = 100f, float mass = 5f)
    {
        // solve k so this shell has exactly targetPenetration at full integrity
        float k = targetPenetration * Mathf.Pow(caliber, 1.07f)
            / (Mathf.Pow(speed, 1.43f) * Mathf.Pow(mass, 0.71f));

        return new ProjectileState
        {
            velocity = dir.normalized * speed,
            mass = mass,
            caliber = caliber,
            shatterVelocity = shatterVelocity,
            penetrationK = k,
            integrity = 1f,
            state = ShellState.Intact,
            projectileId = 1,
            hitIndex = 0,
            tick = 1,
        };
    }

    private static SurfaceSet Plate(float rha, float plateThickness = 0.1f)
    {
        var s = new SurfaceSet { count = 1, plateThickness = plateThickness };
        s.normal[0] = Vector2.up;
        s.rha[0] = rha;
        return s;
    }

    private static SurfaceSet Edge(Vector2 n0, float rha0, Vector2 n1, float rha1)
    {
        var s = new SurfaceSet { count = 2, plateThickness = 0.1f };
        s.normal[0] = n0; s.rha[0] = rha0;
        s.normal[1] = n1; s.rha[1] = rha1;
        return s;
    }

    /// <summary>
    /// 폴리곤 넓이 클리핑. **오목한 모양이 핵심이다** - Sutherland-Hodgman은 볼록 창으로
    /// 자르지만 원본이 오목하면 잘린 결과에 잇는 변이 생긴다. 신발끈 공식이 그 겹침을
    /// 부호로 상쇄한다는 것이 이 검사가 지키는 전제이고, 그게 깨지면 증상은 "이 판이
    /// 왜 이렇게 약하지"다 - 서브셀 잠김 비율이 조용히 틀린다.
    /// </summary>
    private static void PolygonTests()
    {
        // 2x2에서 오른쪽 위 1x1을 도려낸 L자. 넓이 3.
        var l = new[]
        {
            new Vector2(0f, 0f), new Vector2(2f, 0f), new Vector2(2f, 1f),
            new Vector2(1f, 1f), new Vector2(1f, 2f), new Vector2(0f, 2f),
        };

        Near("L 전체 넓이", Ballistics.PolygonArea(l), 3f);

        // 칸별로 자른 합이 전체와 같아야 한다. 직선을 칸마다 조각내는 저작 도구가
        // 기대는 성질이 정확히 이것이다 - 조각의 합집합이 원본과 같다.
        float total = 0f;

        for (int x = 0; x < 2; x++)
        for (int y = 0; y < 2; y++)
            total += Ballistics.ClippedArea(l, new Vector2(x, y), new Vector2(x + 1f, y + 1f));

        Near("L 칸별 합", total, 3f);

        // 창이 오목한 노치를 정통으로 가로지르는 경우.
        Near("L 노치 가로지르기",
            Ballistics.ClippedArea(l, new Vector2(0.5f, 0.5f), new Vector2(1.5f, 1.5f)), 0.75f);

        // U자. 창이 **떨어진 두 팔**을 동시에 걸치는 것이 최악의 경우다 - 잘린 결과가
        // 두 덩어리라 잇는 변이 반드시 생긴다.
        var u = new[]
        {
            new Vector2(0f, 0f), new Vector2(3f, 0f), new Vector2(3f, 3f), new Vector2(2f, 3f),
            new Vector2(2f, 1f), new Vector2(1f, 1f), new Vector2(1f, 3f), new Vector2(0f, 3f),
        };

        Near("U 전체 넓이", Ballistics.PolygonArea(u), 7f);
        Near("U 두 팔 걸치기",
            Ballistics.ClippedArea(u, new Vector2(0f, 2f), new Vector2(3f, 3f)), 2f);

        // 감기 방향이 반대여도 같은 값이어야 한다. 대칭 복사가 감기를 뒤집는다.
        System.Array.Reverse(l);
        Near("감기 반대", Ballistics.PolygonArea(l), 3f);

        // 볼록 껍질. **입출력이 같은 배열인 채로** 부른다 - 호출자들이 실제로 그렇게
        // 쓰고, 모노톤 체인의 중간 출력이 2*count까지 부푸는 것을 내부 버퍼가 받아야
        // 한다. 이게 깨졌을 때 증상은 껍질이 틀리는 게 아니라 IndexOutOfRange로
        // 도구가 통째로 죽는 것이었다.
        var sq = new[]
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f),
            new Vector2(1f, 1f), new Vector2(0f, 1f),
        };

        int hullCount = Ballistics.ConvexHull(sq, 4, sq);
        Check($"정사각형 껍질 점 4 ({hullCount})", hullCount == 4);
        Near("정사각형 껍질 넓이", Ballistics.PolygonArea(sq), 1f);

        // 안쪽 점은 껍질에서 빠져야 한다.
        var withInner = new[]
        {
            new Vector2(0f, 0f), new Vector2(2f, 0f), new Vector2(0.9f, 0.3f),
            new Vector2(2f, 2f), new Vector2(0f, 2f), new Vector2(1f, 1f),
        };

        int hull2 = Ballistics.ConvexHull(withInner, 6, withInner);
        Check($"안쪽 점 제거 ({hull2})", hull2 == 4);

        // 점 포함 판정. 노치 안쪽은 밖이다.
        Check("L 노치는 바깥", !Ballistics.PolygonContains(l, new Vector2(1.5f, 1.5f)));
        Check("L 밑동은 안쪽", Ballistics.PolygonContains(l, new Vector2(0.5f, 0.5f)));
    }

    private static void Near(string name, float got, float want)
        => Check($"{name} ({got:0.####} vs {want:0.####})", Mathf.Abs(got - want) < 1e-3f);

    /// <summary>
    /// 대조용 레퍼런스: 옛 라벨 BFS의 축약. 8방향, 동점이면 먼저 만난(인덱스 낮은)
    /// 성분 유지 - 제품 코드와 같은 규칙이어야 대조가 대조다.
    /// </summary>
    private static ulong ReferenceLargestComponent(ulong alive)
    {
        int n = Ballistics.SubGrid;
        var stack = new System.Collections.Generic.Stack<int>();
        var seen = new bool[Ballistics.SubCount];
        ulong best = 0;
        int bestSize = 0;

        for (int seed = 0; seed < Ballistics.SubCount; seed++)
        {
            if (seen[seed] || (alive & (1UL << seed)) == 0)
                continue;

            ulong component = 0;
            int size = 0;

            stack.Push(seed);
            seen[seed] = true;

            while (stack.Count > 0)
            {
                int at = stack.Pop();
                component |= 1UL << at;
                size++;

                int col = at % n;
                int row = at / n;

                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nc = col + dx;
                    int nr = row + dy;

                    if ((dx == 0 && dy == 0) || nc < 0 || nc >= n || nr < 0 || nr >= n)
                        continue;

                    int next = nr * n + nc;

                    if (seen[next] || (alive & (1UL << next)) == 0)
                        continue;

                    seen[next] = true;
                    stack.Push(next);
                }
            }

            if (size > bestSize)
            {
                bestSize = size;
                best = component;
            }
        }

        return best;
    }

    private static void Check(string name, bool ok)
    {
        if (ok)
        {
            _pass++;
            return;
        }

        _fail++;
        Debug.LogError($"[Ballistics] FAIL {name}");
    }

    private static void Check(string name, bool ok, in HitResult r)
    {
        if (ok)
        {
            _pass++;
            return;
        }

        _fail++;
        Debug.LogError($"[Ballistics] FAIL {name}\n{PenetrationManager.Describe(r)}");
    }
}
#endif
