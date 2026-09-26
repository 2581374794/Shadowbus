# -*- coding: utf-8 -*-
"""Rebuild `<resources>/story_text/chs/*.unity3d` with the CN (NetEase) story text.

The deployed Simplified folder holds the global client's own story bundles, whose text is a
different translation (and partly traditional glyphs). The CN client's matching bundle is
Unity 2022 and cannot be loaded by this 2020 engine, so the CN text is copied into the
existing 2020 bundle as the shell - the same one-object surgery used for the leader spines.

Each rebuilt bundle also gets its own internal archive (CAB) name, so Unity does not refuse
the second one with "another AssetBundle with the same files is already loaded".
"""
import argparse
import hashlib
import io
import os
import shutil
import UnityPy

UnityPy.config.FALLBACK_UNITY_VERSION = '2020.3.18f1'
CHS = r'D:\Games\Shadowbus\Resources\story_text\chs'
CN_T = r'D:\Games\Shadowbus-dev\artifacts\cn_text'
OUT = r'D:\Games\Shadowbus-dev\artifacts\story_out_chs'


def field_bytes(value):
    if value is None:
        return b''
    if isinstance(value, bytes):
        return value
    return value.encode('utf-8', 'surrogateescape')


def as_text(raw):
    return raw.decode('utf-8', 'surrogateescape')


def cn_assets(name):
    """asset name -> text, from the CN bundle."""
    path = os.path.join(CN_T, name)
    if not os.path.exists(path):
        return None
    out = {}
    env = UnityPy.load(path)
    for obj in env.objects:
        if obj.type.name != 'TextAsset':
            continue
        d = obj.read()
        out[getattr(d, 'm_Name', '') or ''] = field_bytes(getattr(d, 'm_Script', None))
    return out


def inspect():
    names = sorted(f for f in os.listdir(CHS) if f.endswith('.unity3d'))
    cabs = {}
    multi = 0
    name_mismatch = 0
    no_cn = 0
    for name in names:
        try:
            env = UnityPy.load(os.path.join(CHS, name))
        except Exception as exc:
            print('load error', name, exc)
            continue
        texts = []
        cab = None
        for key, f in env.files.items():
            if hasattr(f, 'files'):
                inner = sorted(f.files.keys())
                cab = inner[0] if inner else None
        for obj in env.objects:
            if obj.type.name == 'TextAsset':
                texts.append(getattr(obj.read(), 'm_Name', '') or '')
        cabs.setdefault(cab, []).append(name)
        if len(texts) != 1:
            multi += 1
        cn = cn_assets(name)
        if cn is None:
            no_cn += 1
        elif set(cn.keys()) != set(texts):
            name_mismatch += 1
    dupes = {k: v for k, v in cabs.items() if len(v) > 1}
    print('bundles=%d  distinct CAB=%d  duplicated CAB groups=%d  multi-TextAsset=%d  no-CN=%d  asset-name mismatch=%d'
          % (len(names), len(cabs), len(dupes), multi, no_cn, name_mismatch))
    for k, v in list(dupes.items())[:5]:
        print('   dup CAB %s -> %d bundles e.g. %s' % (k, len(v), v[:3]))


def build(install):
    names = sorted(f for f in os.listdir(CHS) if f.endswith('.unity3d'))
    os.makedirs(OUT, exist_ok=True)
    replaced = skipped = 0
    seen_cabs = set()
    for name in names:
        cn = cn_assets(name)
        env = UnityPy.load(os.path.join(CHS, name))
        changed = False
        if cn:
            for obj in env.objects:
                if obj.type.name != 'TextAsset':
                    continue
                d = obj.read()
                asset = getattr(d, 'm_Name', '') or ''
                if asset in cn:
                    current = field_bytes(getattr(d, 'm_Script', None))
                    if current != cn[asset]:
                        d.m_Script = as_text(cn[asset])
                        d.save()
                        changed = True
        if not changed:
            skipped += 1
            continue

        # The deployed bundles already carry distinct internal archive names, so they are kept
        # as they are; only rename if a duplicate ever shows up (Unity refuses the second one).
        try:
            bundle = env.file
            cabs = sorted({inner for inner in bundle.files.keys() if not inner.endswith('.resS')})
            if len(cabs) != 1 or cabs[0] in seen_cabs:
                cab = 'CAB-%s' % hashlib.md5(('shadowbus-story-%s' % name).encode()).hexdigest()
                renames = {}
                for inner in list(bundle.files.keys()):
                    new_name = cab + '.resS' if inner.endswith('.resS') else cab
                    if inner != new_name:
                        renames[inner] = new_name
                for old, new in renames.items():
                    bundle.files[new] = bundle.files.pop(old)
            else:
                seen_cabs.add(cabs[0])
        except Exception as exc:
            print('cab check failed for', name, exc)

        data = env.file.save(packer='lz4')
        out = os.path.join(OUT, name)
        with io.open(out, 'wb') as fh:
            fh.write(data)
        replaced += 1
    print('rebuilt %d bundle(s), %d already matched the CN text' % (replaced, skipped))
    if install:
        for name in os.listdir(OUT):
            shutil.copy2(os.path.join(OUT, name), os.path.join(CHS, name))
        print('installed %d bundle(s) into %s' % (len(os.listdir(OUT)), CHS))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--inspect', action='store_true')
    parser.add_argument('--install', action='store_true')
    args = parser.parse_args()
    if args.inspect:
        inspect()
    else:
        build(args.install)
