#!/usr/bin/env python3
"""受け口の UPM パッケージ(com.hidano.oscdesk)の約束を機械的に確かめる。

oscdesk リポジトリの tests/guards/unity-package.test.ts から移したもの(oscdesk の DESIGN.md D-136)。
C# の振る舞いは EditMode テストが見るので、ここではファイルの並びと写しの一致だけを縛る。

  1. Tests/Editor/ の見本の写しが oscdesk の protocol/ の原本と同じ
     (言語間の見本の正は oscdesk。写しが古いと、EditMode テストが古い契約で緑になる)
  2. 追跡中のすべてのファイルとフォルダに .meta があり、余った .meta が無い
     (レジストリから入れたパッケージは読み取り専用で Unity が .meta を作れず、.meta の無いファイルは無視される)
  3. package.json の版が CHANGELOG.md の先頭の版と同じ

使い方(リポジトリ直下で。標準ライブラリだけを使う):
  python3 scripts/check_package.py                      # 確かめる(原本は oscdesk の main を GitHub から読む)
  python3 scripts/check_package.py --sync               # 写しを原本で書き換えてから確かめる
  python3 scripts/check_package.py --upstream D:/path/to/oscdesk   # 手元の oscdesk のクローンを原本にする
  python3 scripts/check_package.py --upstream-ref <ブランチかコミット>  # GitHub の別の版を原本にする
  python3 scripts/check_package.py --skip-upstream      # 写しの比較を飛ばす(オフラインのとき。CI では使わない)

終了コード: 0 = すべて通過、1 = 約束を破っている、2 = 原本を読めない・引数の誤り
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
PACKAGE_PATH = 'Packages/com.hidano.oscdesk'
PACKAGE_ROOT = REPOSITORY_ROOT / PACKAGE_PATH
UPSTREAM_REPOSITORY = 'Hidano-Dev/oscdesk'

# 写し(パッケージの中のパス) → 原本(oscdesk の中のパス)
SAMPLE_COPIES = {
    'Tests/Editor/osc-binary-samples.json': 'protocol/osc-binary-samples.json',
    'Tests/Editor/catalog-samples.json': 'protocol/catalog-samples.json',
}


def read_text(data: bytes) -> str:
    # 改行の違い(checkout の autocrlf)で食い違いにしない
    return data.decode('utf-8').replace('\r\n', '\n')


def fetch_upstream(source: str, ref: str, path: str) -> bytes:
    if source:
        return (Path(source) / path).read_bytes()
    url = f'https://raw.githubusercontent.com/{UPSTREAM_REPOSITORY}/{ref}/{path}'
    request = urllib.request.Request(url, headers={'User-Agent': 'oscdesk-unity-check-package'})
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.read()


def check_copies(args: argparse.Namespace) -> list[str]:
    problems: list[str] = []
    for copy, original in SAMPLE_COPIES.items():
        try:
            original_bytes = fetch_upstream(args.upstream, args.upstream_ref, original)
        except (OSError, urllib.error.URLError) as error:
            print(f'原本を読めません: {original}({error})', file=sys.stderr)
            sys.exit(2)
        copy_path = PACKAGE_ROOT / copy
        if args.sync:
            # 原本のバイト列をそのまま写す(改行も原本に揃える)
            copy_path.write_bytes(original_bytes)
            print(f'写しを更新: {PACKAGE_PATH}/{copy}')
        if read_text(copy_path.read_bytes()) != read_text(original_bytes):
            problems.append(
                f'{PACKAGE_PATH}/{copy} が oscdesk の {original} と違う'
                f'(python3 scripts/check_package.py --sync で写し直してコミットする)'
            )
    return problems


def tracked_package_files() -> list[str]:
    # 公開はクリーンな checkout から行うので、手元にだけある .meta で通らないよう追跡中のファイルを見る
    output = subprocess.run(
        ['git', 'ls-files', '-z', '--', PACKAGE_PATH],
        cwd=REPOSITORY_ROOT, check=True, capture_output=True,
    ).stdout.decode('utf-8')
    prefix = PACKAGE_PATH + '/'
    return [path[len(prefix):] for path in output.split('\0') if path.startswith(prefix)]


def is_ignored_by_unity(segment: str) -> bool:
    # Unity が取り込まない名前(. で始まる、~ で終わる)。その中のフォルダはサンプルの取り込みで
    # Assets/Samples/ の下に作り直されるので .meta は要らないが、Samples~ の中のファイルは写されるので、
    # シーンからの参照(GUID)を保つため .meta を持たせる
    return segment.startswith('.') or segment.endswith('~')


def check_meta_files() -> list[str]:
    tracked = set(tracked_package_files())
    assets: set[str] = set()
    for file in tracked:
        if file.endswith('.meta'):
            continue
        segments = file.split('/')
        if is_ignored_by_unity(segments[-1]):
            continue
        assets.add(file)
        for depth in range(1, len(segments)):
            folder = segments[:depth]
            if not any(is_ignored_by_unity(segment) for segment in folder):
                assets.add('/'.join(folder))
    problems = [f'.meta が無い(Unity で開いて作らせ、git add する): {PACKAGE_PATH}/{asset}'
                for asset in sorted(assets) if f'{asset}.meta' not in tracked]
    problems += [f'対応するファイルの無い .meta: {PACKAGE_PATH}/{file}'
                 for file in sorted(tracked) if file.endswith('.meta') and file[:-len('.meta')] not in assets]
    if not tracked:
        problems.append(f'{PACKAGE_PATH} に追跡中のファイルが無い')
    return problems


def check_version() -> list[str]:
    manifest = json.loads(read_text((PACKAGE_ROOT / 'package.json').read_bytes()))
    problems: list[str] = []
    if manifest.get('name') != 'com.hidano.oscdesk':
        problems.append(f'package.json の name が com.hidano.oscdesk でない: {manifest.get("name")}')
    # 先頭の版の見出しをそのまま比べる(プレリリースやビルドメタデータの版も飛ばさない)
    latest = re.search(r'^## \[([^\]]+)\]', read_text((PACKAGE_ROOT / 'CHANGELOG.md').read_bytes()), re.MULTILINE)
    if latest is None or latest.group(1) != manifest.get('version'):
        found = latest.group(1) if latest else '(見出しなし)'
        problems.append(f'CHANGELOG.md の先頭の版 {found} が package.json の版 {manifest.get("version")} と違う')
    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--upstream', default='', help='原本にする手元の oscdesk のクローン(省略時は GitHub から読む)')
    parser.add_argument('--upstream-ref', default='main', help='GitHub から読むときのブランチかコミット(既定 main)')
    parser.add_argument('--sync', action='store_true', help='写しを原本で書き換えてから確かめる')
    parser.add_argument('--skip-upstream', action='store_true', help='写しの比較を飛ばす(オフライン用)')
    args = parser.parse_args()
    if args.sync and args.skip_upstream:
        parser.error('--sync と --skip-upstream は一緒に使えない')

    problems: list[str] = []
    if args.skip_upstream:
        print('写しの比較を飛ばしました(--skip-upstream)')
    else:
        problems += check_copies(args)
    problems += check_meta_files()
    problems += check_version()

    for problem in problems:
        print(f'NG: {problem}')
    if problems:
        print(f'失敗 {len(problems)} 件')
        return 1
    print('com.hidano.oscdesk のパッケージの検査: すべて通過')
    return 0


if __name__ == '__main__':
    sys.exit(main())
