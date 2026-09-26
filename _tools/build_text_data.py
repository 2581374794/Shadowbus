# -*- coding: utf-8 -*-
"""Build the shipped CN text override data.

For every text table the game loads, take the `Chs` section of the CN (NetEase) client
bundle and write it to Mods/Text/chs/<table>.json. The plugin overlays this on top of the
table the game just parsed, so the port needs no bundle surgery and stays reversible.

`--lang chs` writes the Simplified Chinese set; the same script can build other languages
straight from the same CN bundles if a language table is ever wanted.
"""
import argparse
import json
import os
import UnityPy

UnityPy.config.FALLBACK_UNITY_VERSION = '2020.3.18f1'
CN_T = r'D:\Games\Shadowbus-dev\artifacts\cn_text'
GAME_MODS = r'D:\Games\Shadowbus\Shadowverse\Mods\Text'
REPO_MODS = r'D:\Github\Shadowbus\Mods\Text'


def load_table(path):
    """Return (table_name, region_dict) of the bundle's first TextAsset."""
    env = UnityPy.load(path)
    for obj in env.objects:
        if obj.type.name != 'TextAsset':
            continue
        d = obj.read()
        raw = d.m_Script
        raw = raw.encode('utf-8', 'surrogateescape') if isinstance(raw, str) else (raw or b'')
        try:
            data = json.loads(raw.decode('utf-8', 'surrogateescape'))
        except Exception:
            return None, None
        if isinstance(data, dict) and len(data) == 1:
            table = list(data.keys())[0]
            return table, data[table]
        return None, data
    return None, None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--lang', default='chs')
    parser.add_argument('--region', default='Chs')
    args = parser.parse_args()

    out_dirs = [os.path.join(GAME_MODS, args.lang), os.path.join(REPO_MODS, args.lang)]
    for d in out_dirs:
        os.makedirs(d, exist_ok=True)

    names = sorted(f for f in os.listdir(CN_T)
                   if f.startswith('master_') and f.endswith('.unity3d'))
    total = 0
    written = 0
    for name in names:
        table, regions = load_table(os.path.join(CN_T, name))
        if not table or not isinstance(regions, dict):
            print('%-48s skipped (no regions)' % name)
            continue
        region = regions.get(args.region)
        if not isinstance(region, dict) or not region:
            print('%-48s skipped (no %s section)' % (name, args.region))
            continue
        payload = json.dumps(region, ensure_ascii=False, separators=(',', ':'))
        for d in out_dirs:
            with open(os.path.join(d, table + '.json'), 'w', encoding='utf-8') as fh:
                fh.write(payload)
        written += 1
        total += len(region)
        print('%-48s %6d entries  %7d B' % (table, len(region), len(payload.encode('utf-8'))))

    print()
    print('%d table(s), %d entry(ies) total' % (written, total))
    for d in out_dirs:
        size = sum(os.path.getsize(os.path.join(d, f)) for f in os.listdir(d))
        print('  %s : %d file(s), %.2f MB' % (d, len(os.listdir(d)), size / 1048576.0))


if __name__ == '__main__':
    main()
