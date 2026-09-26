# -*- coding: utf-8 -*-
"""Build the shipped per-language text override data.

Two independent sets live side by side under the resource root, mirroring `story_text`:

    <资源根>/text/chs/<table>.json   <- the CN (NetEase) client's Chs section
    <资源根>/text/cht/<table>.json   <- the PC client's own Cht section

The plugin picks the folder that matches the current text language, so neither set touches
the other. `_tools/list_cn_text_assets.py` produces the CN pull list.
"""
import argparse
import json
import os
import UnityPy

UnityPy.config.FALLBACK_UNITY_VERSION = '2020.3.18f1'
CN_T = r'D:\Games\Shadowbus-dev\artifacts\cn_text'
PC_A = r'D:\Games\Shadowbus\Resources\a'
RES_ROOT = r'D:\Games\Shadowbus\Resources'
REPO_ROOT = r'D:\Github\Shadowbus\Resources'


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
    parser.add_argument('--lang', required=True, choices=['chs', 'cht'])
    parser.add_argument('--region', required=True, choices=['Chs', 'Cht'])
    parser.add_argument('--source', required=True, choices=['cn', 'pc'])
    args = parser.parse_args()

    source = CN_T if args.source == 'cn' else PC_A
    out_dirs = [os.path.join(RES_ROOT, 'text', args.lang), os.path.join(REPO_ROOT, 'text', args.lang)]
    for d in out_dirs:
        os.makedirs(d, exist_ok=True)

    names = sorted(f for f in os.listdir(source)
                   if f.startswith('master_') and f.endswith('.unity3d'))
    written = total = 0
    for name in names:
        table, regions = load_table(os.path.join(source, name))
        if not table or not isinstance(regions, dict):
            continue
        region = regions.get(args.region)
        if not isinstance(region, dict) or not region:
            continue
        payload = json.dumps(region, ensure_ascii=False, separators=(',', ':'))
        for d in out_dirs:
            with open(os.path.join(d, table + '.json'), 'w', encoding='utf-8') as fh:
                fh.write(payload)
        written += 1
        total += len(region)

    print('%s (%s section of the %s client): %d table(s), %d entry(ies)'
          % (args.lang, args.region, args.source, written, total))
    for d in out_dirs:
        size = sum(os.path.getsize(os.path.join(d, f)) for f in os.listdir(d))
        print('  %s : %d file(s), %.2f MB' % (d, len(os.listdir(d)), size / 1048576.0))


if __name__ == '__main__':
    main()
