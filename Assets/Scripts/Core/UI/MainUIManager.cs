using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class MainUIManager : MonoBehaviour
{
    public List<Ship> allShipsInMap = new();
    public TextMeshProUGUI MapTeamsInfo;
    public Ship targetShipInfo;

    void Awake()
    {
        GetShips();
    }

    void GetShips()
    {
        allShipsInMap.Clear();
        Object[] ships = FindObjectsByType(typeof(Ship), FindObjectsSortMode.None);

        foreach(Object ship in ships)
        {
            if(ship is Ship comp)
            {
                if(!comp.IsCombatEffective) continue;
                allShipsInMap.Add(comp);
            }
        }
    }

    private int _lastAlly = -1, _lastEnemy = -1;

    void FixedUpdate()
    {
        // FindObjectsByType(스텝마다 배열 할당 + 씬 전수 검색)을 버리고 이미 있는 정적
        // 목록을 센다. 문자열 보간·TMP 대입은 수가 변한 스텝에만.
        int ally = 0, enemy = 0;

        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship ship = Ship.All[i];

            if (ship == null || !ship.IsCombatEffective)
                continue;

            if (ship.team == Ship.Team.Ally) ally++;
            else if (ship.team == Ship.Team.Enemy) enemy++;
        }

        if (ally == _lastAlly && enemy == _lastEnemy)
            return;

        _lastAlly = ally;
        _lastEnemy = enemy;
        MapTeamsInfo.text = $"<color=green>{ally}</color> : <color=red>{enemy}</color>";
    }
}
