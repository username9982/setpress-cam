"""Windows 版と Web 版の割付が同じになるか確かめるための図面を作る（開発用）。

使い方: python windows/tests/make_cases.py
出力:   windows/build/cases.json（Web 版の書き出しと同じ形。図面がいくつも入っている）

確かめ方は windows/tests/README.md。
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


class Doc:
    def __init__(self, name, tools, mj=0.0, sheet=(1000, 500)):
        self.d = {'version': 4, 'name': name, 'program': 1, 'tools': tools,
                  'sheet': {'w': sheet[0], 'h': sheet[1], 't': 1.6}, 'mj': mj,
                  'layouts': [], 'points': [], 'lines': [], 'dims': [], 'nextId': 1}

    def nid(self):
        i = self.d['nextId']
        self.d['nextId'] += 1
        return i

    def poly(self, verts):
        """水平・垂直の辺だけの閉じた図形（頂点は (x, y)、Y は上が正）"""
        ids = []
        for x, y in verts:
            i = self.nid()
            self.d['points'].append({'id': i, 'x': x, 'y': y})
            ids.append(i)
        for k in range(len(ids)):
            a, b = ids[k], ids[(k + 1) % len(ids)]
            pa, pb = verts[k], verts[(k + 1) % len(verts)]
            self.d['lines'].append({'id': self.nid(), 'a': a, 'b': b, 'dir': 'h' if pa[1] == pb[1] else 'v'})

    def rect(self, x0, y0, x1, y1):
        self.poly([(x0, y0), (x1, y0), (x1, y1), (x0, y1)])

    def circle(self, x, y, dia):
        """丸（中心 (x, y)、直径 dia）。中心は点として持つ"""
        c = self.nid()
        self.d['points'].append({'id': c, 'x': x, 'y': y})
        self.d.setdefault('circles', []).append({'id': self.nid(), 'c': c, 'd': dia})


def rect_tool(w, h):
    return {'shape': 'rect', 'w': w, 'h': h, 'd': None, 'note': ''}


ROUND = {'shape': 'round', 'w': None, 'h': None, 'd': 8, 'note': ''}
NONE = {'shape': 'none', 'w': None, 'h': None, 'd': None, 'note': ''}


def cases():
    out = []
    # 1: 長方形の外形に、長方形の穴と L 字の穴
    d = Doc('holes', [rect_tool(20, 10), rect_tool(10, 10), ROUND])
    d.rect(100, 100, 400, 300)
    d.rect(130, 230, 180, 260)
    d.poly([(220, 150), (300, 150), (300, 170), (240, 170), (240, 250), (220, 250)])
    d.rect(330, 120, 345, 125)  # 金型が入らない小さい穴
    out.append(d)
    # 2: 上の辺の切り欠きと、角の切り欠き（外へはみ出して描いたもの）、ミクロジョイント
    d = Doc('notches', [rect_tool(15, 15), rect_tool(30, 10), NONE], mj=0.2)
    d.rect(100, 100, 400, 300)
    d.rect(200, 260, 250, 320)
    d.rect(370, 80, 420, 130)
    out.append(d)
    # 3: L 字の外形
    d = Doc('L', [rect_tool(20, 10), rect_tool(15, 15), NONE], mj=0.5)
    d.poly([(100, 100), (400, 100), (400, 200), (250, 200), (250, 350), (100, 350)])
    out.append(d)
    # 4: 隣のワークに近い切り欠き（外へ広げられない）
    d = Doc('blocked', [rect_tool(20, 10), rect_tool(10, 10), NONE])
    d.rect(100, 100, 300, 300)
    d.rect(305, 100, 500, 300)
    d.rect(280, 150, 320, 200)
    out.append(d)
    # 5: 重なる切り欠き・穴
    d = Doc('overlap', [rect_tool(20, 10), rect_tool(10, 10), rect_tool(5, 5)], mj=0.2)
    d.rect(100, 100, 400, 300)
    d.rect(150, 250, 220, 330)
    d.rect(200, 270, 260, 290)
    d.rect(300, 150, 340, 180)
    d.rect(330, 170, 360, 220)
    out.append(d)
    # 6: 細い溝（U 字の外形と、幅の違う切り欠き）
    d = Doc('slot', [rect_tool(15, 15), rect_tool(25, 8), NONE])
    d.poly([(100, 100), (400, 100), (400, 300), (260, 300), (260, 180), (240, 180), (240, 300), (100, 300)])
    d.rect(150, 290, 158, 320)
    out.append(d)
    # 7: 長方形の金型でミクロジョイント（短いほうの辺の余りを単発）
    d = Doc('mj-rect', [rect_tool(30, 10), NONE, NONE], mj=0.3)
    d.rect(50, 50, 350, 250)
    out.append(d)
    d = Doc('mj-rect-tall', [rect_tool(8, 24), NONE, NONE], mj=1)
    d.rect(50, 50, 250, 450)
    d.rect(100, 200, 130, 260)
    out.append(d)
    # 8: 小数の寸法と、いくつものワーク
    d = Doc('decimal', [rect_tool(12.5, 7.3), rect_tool(6.1, 6.1), NONE], mj=0.15)
    d.rect(10.5, 20.25, 210.75, 180.4)
    d.rect(40.1, 60.2, 77.7, 90.9)
    d.rect(300, 50, 420.3, 160.6)
    d.rect(410.3, 70, 430.3, 90)
    out.append(d)
    # 10: 丸穴（φ8・φ12 の丸の金型で単発。φ20 は合う金型がない。外形の外の丸は割り付けない）
    d = Doc('circles', [rect_tool(20, 10), {'shape': 'round', 'w': None, 'h': None, 'd': 12, 'note': ''}, ROUND])
    d.rect(100, 100, 400, 300)
    d.rect(130, 230, 180, 260)
    d.circle(250, 250, 8)
    d.circle(200, 150, 8)
    d.circle(320, 250, 12)
    d.circle(150, 150, 12.5)
    d.circle(350, 150, 20)
    d.circle(380, 120, 20)
    d.circle(600, 200, 8)
    out.append(d)
    # 9: 金型なし
    d = Doc('no-tools', [ROUND, NONE, NONE])
    d.rect(100, 100, 200, 200)
    d.rect(120, 120, 140, 140)
    out.append(d)
    return out


def main():
    drawings = {c.d['name']: {'savedAt': '2026-11-23T00:00:00.000Z', 'doc': c.d} for c in cases()}
    path = ROOT / 'build' / 'cases.json'
    path.parent.mkdir(exist_ok=True)
    path.write_text(json.dumps({'app': 'setpress-cam', 'version': 1, 'drawings': drawings}, ensure_ascii=False, indent=1), encoding='utf-8')
    print('wrote', path)


if __name__ == '__main__':
    main()
