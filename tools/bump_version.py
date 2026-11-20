"""アプリの版を、関係するファイルすべてで書き換える開発用の道具。

使い方: python tools/bump_version.py 0.2.1

書き換える所:
- app.js     の APP_VERSION
- sw.js      の CACHE_NAME
- index.html の style.css?v= / geom.js?v= / app.js?v=
  （新しい index.html が古い style.css や app.js と組み合わさらないように、版を URL に付ける）
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

RULES = [
    ('app.js', r"(APP_VERSION = ')[^']+(')"),
    ('sw.js', r"(CACHE_NAME = 'setpress-cam-)[^']+(')"),
    ('index.html', r'(href="style\.css\?v=)[^"]+(")'),
    ('index.html', r'(src="geom\.js\?v=)[^"]+(")'),
    ('index.html', r'(src="app\.js\?v=)[^"]+(")'),
]


def main() -> None:
    if len(sys.argv) != 2 or not re.fullmatch(r'\d+\.\d+\.\d+', sys.argv[1]):
        sys.exit('使い方: python tools/bump_version.py 0.2.1')
    ver = sys.argv[1]
    for name, pattern in RULES:
        path = ROOT / name
        text = path.read_text(encoding='utf-8')
        new, n = re.subn(pattern, lambda m: m.group(1) + ver + m.group(2), text)
        if n != 1:
            sys.exit(f'{name}: 書き換える所が {n} 個見つかりました（1 個のはず）: {pattern}')
        path.write_text(new, encoding='utf-8', newline='\n')
        print(f'{name}: {ver}')


if __name__ == '__main__':
    main()
