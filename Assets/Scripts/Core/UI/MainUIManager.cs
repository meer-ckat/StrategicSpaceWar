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

    void FixedUpdate()
    {
        GetShips();
        int ally = 0, enemy = 0;
        foreach(Ship ship in allShipsInMap)
        {
            ally = ship.team == Ship.Team.Ally? ally + 1 : ally;
            enemy = ship.team == Ship.Team.Enemy? enemy + 1 : enemy;

            MapTeamsInfo.text = $"<color=green>{ally}</color> : <color=red>{enemy}</color>";
        }
    }
}
