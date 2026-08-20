using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 최근 피격 기록.
/// 판정에는 아무 영향도 주지 않고 UI가 읽기만 한다.
/// </summary>
public static class DamageLog
{
    public const float MergeWindow = 0.3f;

    private const int Capacity = 24;

    private static uint _version;

    public struct ArmorMark
    {
        // 어떤 장갑판이었는지.
        // 판이 파괴되면 null이 될 수 있으므로 위치 계산은 anchor + fallback을 쓴다.
        public Armor armor;

        // 같은 ArmorMark를 UI에서 안정적으로 찾기 위한 키.
        public int armorId;

        // 피격 당시 장갑 Transform.
        // 판이 살아 있으면 이동/회전을 따라간다.
        public Transform anchor;

        // anchor 기준 피격 위치.
        public Vector3 localPoint;

        // anchor가 파괴된 뒤에도 마지막 피격 위치를 남기기 위한 fallback.
        public Vector3 worldPoint;

        public int subIndex;
        public HitOutcome outcome;

        // 같은 판이 다시 맞았는지 UI가 알아내는 값.
        public uint version;

        public float time;

        public Vector3 CurrentWorldPoint =>
            anchor != null
                ? anchor.TransformPoint(localPoint)
                : worldPoint;
    }

    public struct ModuleMark
    {
        public Transform at;
        public float amount;
        public float health01;
        public bool neutralized;
        public float time;
    }

    public static readonly List<ArmorMark> Armors = new();
    public static readonly List<ModuleMark> Modules = new();

    /// <summary>
    /// 탄도 판정 하나가 확정됐을 때 한 번 호출한다.
    /// </summary>
    public static void Hit(
        Armor armor,
        Vector2 worldPoint,
        int subIndex,
        HitOutcome outcome)
    {
        if (armor == null)
            return;

        int armorId = armor.GetInstanceID();
        Transform anchor = armor.transform;

        Vector3 world = worldPoint;
        Vector3 local = anchor.InverseTransformPoint(world);

        float now = Time.time;
        uint version = NextVersion();

        for (int i = 0; i < Armors.Count; i++)
        {
            if (Armors[i].armorId != armorId)
                continue;

            ArmorMark mark = Armors[i];

            mark.armor = armor;
            mark.anchor = anchor;
            mark.localPoint = local;
            mark.worldPoint = world;
            mark.subIndex = subIndex;
            mark.outcome = outcome;
            mark.version = version;
            mark.time = now;

            Armors[i] = mark;
            return;
        }

        if (Armors.Count >= Capacity)
            Armors.RemoveAt(0);

        Armors.Add(new ArmorMark
        {
            armor = armor,
            armorId = armorId,

            anchor = anchor,
            localPoint = local,
            worldPoint = world,

            subIndex = subIndex,
            outcome = outcome,

            version = version,
            time = now,
        });
    }

    public static void Hit(
        Transform at,
        float amount,
        IDamageable target)
    {
        if (at == null || amount <= 0f)
            return;

        float now = Time.time;

        for (int i = 0; i < Modules.Count; i++)
        {
            if (Modules[i].at != at ||
                now - Modules[i].time > MergeWindow)
            {
                continue;
            }

            ModuleMark merged = Modules[i];

            merged.amount += amount;
            merged.health01 = target.Health01;
            merged.neutralized = target.Neutralized;
            merged.time = now;

            Modules[i] = merged;
            return;
        }

        if (Modules.Count >= Capacity)
            Modules.RemoveAt(0);

        Modules.Add(new ModuleMark
        {
            at = at,
            amount = amount,
            health01 = target.Health01,
            neutralized = target.Neutralized,
            time = now,
        });
    }

    public static void PruneArmors(float maxAge)
    {
        float cutoff = Time.time - maxAge;

        // armor가 파괴돼도 holdSeconds 동안 피격점은 남겨둔다.
        // anchor가 죽으면 ArmorMark.CurrentWorldPoint가 worldPoint를 쓴다.
        Armors.RemoveAll(mark => mark.time < cutoff);
    }

    public static void PruneModules(float maxAge)
    {
        float cutoff = Time.time - maxAge;

        Modules.RemoveAll(mark =>
            mark.at == null ||
            mark.time < cutoff);
    }

    public static void Prune(float maxAge)
    {
        PruneArmors(maxAge);
        PruneModules(maxAge);
    }

    private static uint NextVersion()
    {
        _version++;

        // uint overflow로 0이 되더라도 0은 사용하지 않는다.
        if (_version == 0)
            _version = 1;

        return _version;
    }
}