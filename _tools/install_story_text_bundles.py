# -*- coding: utf-8 -*-
"""Verify the rebuilt story bundles and install them into the game package."""
import argparse
import os
import shutil
import UnityPy

UnityPy.config.FALLBACK_UNITY_VERSION = '2020.3.18f1'
OUT = r'D:\Games\Shadowbus-dev\artifacts\story_out_chs'
CN_T = r'D:\Games\Shadowbus-dev\artifacts\cn_text'
CHS = r'D:\Games\Shadowbus\Resources\story_text\chs'


def payload(path):
    env = UnityPy.load(path)
    out = {}
    cab = None
    for key, f in env.files.items():
        if hasattr(f, 'files'):
            inner = sorted(f.files.keys())
            cab = inner[0] if inner else None
    for obj in env.objects:
        if obj.type.name != 'TextAsset':
            continue
        d = obj.read()
        raw = d.m_Script
        raw = raw.encode('utf-8', 'surrogateescape') if isinstance(raw, str) else (raw or b'')
        out[getattr(d, 'm_Name', '') or ''] = raw
    return out, cab


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--install', action='store_true')
    args = parser.parse_args()

    names = sorted(os.listdir(OUT))
    ok = bad = 0
    cabs = {}
    for name in names:
        ours, cab = payload(os.path.join(OUT, name))
        cn, _ = payload(os.path.join(CN_T, name))
        if ours == cn:
            ok += 1
        else:
            bad += 1
            if bad <= 3:
                print('MISMATCH', name, {k: len(v) for k, v in ours.items()}, {k: len(v) for k, v in cn.items()})
        cabs.setdefault(cab, []).append(name)

    dupes = {k: v for k, v in cabs.items() if len(v) > 1}
    print('rebuilt=%d  text==CN=%d  mismatch=%d  duplicated CAB groups=%d' % (len(names), ok, bad, len(dupes)))
    for name in names[:3]:
        ours, cab = payload(os.path.join(OUT, name))
        print('  %-58s cab=%s' % (name, cab))
        for k, v in ours.items():
            print('      %-34s %r' % (k, v[:80]))

    if args.install:
        for name in names:
            shutil.copy2(os.path.join(OUT, name), os.path.join(CHS, name))
        print('installed %d bundle(s) into %s' % (len(names), CHS))


if __name__ == '__main__':
    main()
