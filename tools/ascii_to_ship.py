r"""아스키 도면 -> 함선 JSON.

    python tools/ascii_to_ship.py gundam.txt --name gundam --armour "Armor mk5"

도면 한 글자가 격자 한 칸(1m)이다. 왼쪽 위가 (col 0, row 0)이고 row는 아래로 증가한다.

기호
    #   장갑판          .  또는 공백  빈 칸
    D   방탄문
    /   45도 판 (오른쪽으로 올라감)     \   45도 판 (오른쪽으로 내려감)
    E   엔진   R  원자로   M  탄약고
    7   m7 주포   9  m9 중주포   p  pd20 근접방어   L  rail 레일건

모듈(E R M 7 9 p L)은 **판이 아니다.** 격자에 도장을 안 찍고, 상하좌우로 붙어 있는
판 하나에 볼트로 매인다. 그 판이 죽으면 같이 죽고 잔해로 떨어지면 같이 날아간다.
붙을 판이 없으면 에러다 - 그런 모듈은 벽이 부서져도 안 죽어서 저작 실수다.
"""

import argparse
import json
import os

PLATE = {'#': None, 'D': 'Ballistic Door', '/': None, '\\': None}
SLOPE = {'/': -45.0, '\\': 45.0}
MODULE = {
    'E': 'SuperDuper Engine',
    'R': 'Reactor',
    'M': 'Magazine',
    '7': 'm7',
    '9': 'm9',
    'p': 'pd20',
    'L': 'rail',
}

STATS = {
    'massPerPlate': 420,
    'drag': 0.3,
    'angleAccel': 20,
    'angleDrag': 0.5,
    'angleBrake': 10,
    'leakRate': 2,
    'doorRate': 1,
    'crews': 3,
    'FightDistance': 200,
    'DetectionDistance': 300,
    'breakawaySpeed': 2,
}

NEIGHBOURS = ((1, 0), (-1, 0), (0, 1), (0, -1))


def build(lines, name, armour):
    grid = {}

    for row, line in enumerate(lines):
        for col, ch in enumerate(line):
            if ch not in ('.', ' '):
                grid[(col, row)] = ch

    unknown = {ch for ch in grid.values() if ch not in PLATE and ch not in MODULE}

    if unknown:
        raise SystemExit('모르는 기호: %s' % ' '.join(sorted(unknown)))

    placements = []

    for (col, row), ch in sorted(grid.items(), key=lambda kv: (kv[0][1], kv[0][0])):
        if ch not in PLATE:
            continue

        p = {'def': PLATE[ch] or armour, 'col': col, 'row': row,
             'rot': SLOPE.get(ch, 0.0)}

        # 45도 판은 칸의 대각선을 덮는다. 길이가 sqrt(2)인 것이 그 뜻이다.
        if ch in SLOPE:
            p['size'] = {'x': 1.0, 'y': 1.4142135}

        p['mountCol'] = -1
        p['mountRow'] = -1
        placements.append(p)

    for (col, row), ch in sorted(grid.items(), key=lambda kv: (kv[0][1], kv[0][0])):
        if ch not in MODULE:
            continue

        mount = next((n for n in
                      ((col + dc, row + dr) for dc, dr in NEIGHBOURS)
                      if grid.get(n) in PLATE), None)

        if mount is None:
            raise SystemExit(
                "'%s' (%d,%d)에 붙을 판이 없다. 상하좌우 중 하나는 판이어야 한다." % (ch, col, row))

        placements.append({'def': MODULE[ch], 'col': col, 'row': row, 'rot': 0.0,
                           'mountCol': mount[0], 'mountRow': mount[1]})

    if not any(p['mountCol'] < 0 for p in placements):
        raise SystemExit('판이 하나도 없다.')

    ship = {'defName': name, 'basedOn': name}
    ship.update(STATS)
    ship['placements'] = placements
    return ship


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('map')
    ap.add_argument('--name', required=True)
    ap.add_argument('--armour', default='Armor mk5')
    ap.add_argument('--out', default=None)
    args = ap.parse_args()

    with open(args.map, encoding='utf-8') as f:
        lines = f.read().replace('\r', '').rstrip('\n').split('\n')

    ship = build(lines, args.name, args.armour)

    out = args.out or os.path.join('Assets', 'StreamingAssets', 'Ships', args.name + '.json')

    with open(out, 'w', encoding='utf-8') as f:
        json.dump(ship, f, indent=2, ensure_ascii=False)

    plates = sum(1 for p in ship['placements'] if p['mountCol'] < 0)
    cols = [p['col'] for p in ship['placements']]
    rows = [p['row'] for p in ship['placements']]

    print('%s -> %s' % (args.map, out))
    print('%d x %d 칸, 판 %d장, 모듈 %d개'
          % (max(cols) - min(cols) + 1, max(rows) - min(rows) + 1,
             plates, len(ship['placements']) - plates))


if __name__ == '__main__':
    main()
