using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 최근에 맞은 것들. PenetrationManager의 링버퍼와 같은 성격 - 아무것도 바꾸지 않고
/// 기록만 한다. 오버레이가 매 프레임 씬 전체를 뒤지지 않게 하려고 존재한다.
/// </summary>
public static class DamageLog
{
    /// <summary>파편 24개가 엔진 하나를 때리면 팝업도 24개가 된다. 이 창 안의 피해는 합친다.</summary>
    public const float MergeWindow = 0.3f;

    private const int Capacity = 24;

    public struct ArmorMark
    {
        public Armor armor;
        public float time;
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

    public static void Hit(Armor armor)
    {
        if (armor == null)
            return;

        for (int i = 0; i < Armors.Count; i++)
        {
            if (Armors[i].armor != armor)
                continue;

            // 같은 판을 다시 맞으면 새 표시가 아니라 시계만 되감는다
            Armors[i] = new ArmorMark { armor = armor, time = Time.time };
            return;
        }

        if (Armors.Count >= Capacity)
            Armors.RemoveAt(0);

        Armors.Add(new ArmorMark { armor = armor, time = Time.time });
    }

    public static void Hit(Transform at, float amount, IDamageable target)
    {
        if (at == null || amount <= 0f)
            return;

        float now = Time.time;

        for (int i = 0; i < Modules.Count; i++)
        {
            if (Modules[i].at != at || now - Modules[i].time > MergeWindow)
                continue;

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

    public static void Prune(float maxAge)
    {
        float cutoff = Time.time - maxAge;

        Armors.RemoveAll(m => m.armor == null || m.time < cutoff);
        Modules.RemoveAll(m => m.at == null || m.time < cutoff);
    }
}
