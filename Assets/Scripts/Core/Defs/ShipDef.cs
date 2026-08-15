using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 배치 하나. 좌표는 격자 칸이지 미터가 아니다 - 격자는 위상만 담당하므로 정수로 충분하고,
/// 정수라서 export/import 왕복이 부동소수점 오차 없이 닫힌다.
///
/// rot과 콜라이더 모양은 격자가 절대 안 본다. 2x2 경사장갑이 이웃 칸을 덮고 있어도 방 구획은
/// 이 col/row 하나만 읽는다. 그게 의도된 거짓말이다.
/// </summary>
[Serializable]
public class Placement
{
    public string def;
    public int col;
    public int row;
    public float rot;          // 도

    /// <summary>이 모듈이 볼트로 붙은 판의 칸. -1이면 선체 직속(= 판이 죽어도 안 죽는다).</summary>
    public int mountCol = -1;
    public int mountRow = -1;

    public bool IsMounted => mountCol >= 0 && mountRow >= 0;

    public Vector2Int Cell => new(col, row);
    public Vector2Int MountCell => new(mountCol, mountRow);
}

/// <summary>
/// 배 한 척의 설계도 전부. 텍스트 맵을 대체한다 - 예전에는 '#'이 벽이라는 것만 알 수 있었고
/// 어떤 벽인지는 프리팹 슬롯 하나로 배 전체가 같아야 했다.
/// </summary>
[Serializable]
public class ShipDef
{
    public string defName;
    public List<Placement> placements = new();

    public static string DirectoryPath => Path.Combine(Application.streamingAssetsPath, "Ships");

    public static string PathOf(string defName) => Path.Combine(DirectoryPath, defName + ".json");

    // ponytail: File 직접 읽기. 데스크톱 전용이다 - 안드로이드나 웹으로 가면 StreamingAssets가
    // 아카이브 안에 들어가서 UnityWebRequest로 바꿔야 한다.
    public static ShipDef Load(string defName)
    {
        string path = PathOf(defName);

        if (!File.Exists(path))
        {
            Debug.LogError($"[ShipDef] '{defName}'을 찾을 수 없다: {path}");
            return null;
        }

        var def = JsonUtility.FromJson<ShipDef>(File.ReadAllText(path));

        if (def == null || def.placements == null || def.placements.Count == 0)
        {
            Debug.LogError($"[ShipDef] {path}를 읽었지만 배치가 하나도 없다.");
            return null;
        }

        return def;
    }

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(PathOf(defName), JsonUtility.ToJson(this, prettyPrint: true));
    }
}
