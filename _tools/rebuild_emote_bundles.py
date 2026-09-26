# -*- coding: utf-8 -*-
"""Rebuild the per-character emote bundles as 2020 bundles holding the CN emotion CSV.

Why this exists: those bundles were deleted by mistake from the game package, and the CN
client's copies are Unity 2022 (unloadable here). They are tiny one-TextAsset bundles, so
they are rebuilt from a 2020 master bundle as the shell, with the CN CSV inside - which also
means every leader now uses the CN emotion table, matching the rest of the text port.

Each bundle gets its own internal archive (CAB) name; 875 bundles sharing one shell would
otherwise trip Unity's "another AssetBundle with the same files is already loaded".
"""
import argparse
import hashlib
import io
import os
import shutil
import sqlite3
import UnityPy

UnityPy.config.FALLBACK_UNITY_VERSION = '2020.3.18f1'
PC_A = r'D:\Games\Shadowbus\Resources\a'
MANIFEST_DB = r'D:\Games\Shadowbus\Resources\manifest.db'
SHELL = os.path.join(PC_A, 'master_cardfilterkeywordreplace.unity3d')
CN = r'D:\Games\Shadowbus-dev\artifacts\cn_emote'
OUT = r'D:\Games\Shadowbus-dev\artifacts\emote_out2'
EXPECTED = r'D:\Games\Shadowbus-dev\emote_expected.txt'
PREFIX = 'master_emote_chara_'
CONTAINER = 'assets/_wizardresources2/resources/jpn/master/chara/emote_chara_%s.csv'


def field_bytes(value):
    if value is None:
        return b''
    if isinstance(value, bytes):
        return value
    return value.encode('utf-8', 'surrogateescape')


def read_cn(name):
    env = UnityPy.load(os.path.join(CN, name))
    for obj in env.objects:
        if obj.type.name != 'TextAsset':
            continue
        d = obj.read()
        raw = field_bytes(getattr(d, 'm_Script', None))
        return (getattr(d, 'm_Name', '') or ''), raw
    return None, None


def build(name):
    chara = name[len(PREFIX):-len('.unity3d')]
    asset, raw = read_cn(name)
    if raw is None:
        return None, 'no CN text'
    expected_asset = 'emote_chara_%s' % chara
    env = UnityPy.load(SHELL)
    for obj in env.objects:
        d = obj.read()
        if obj.type.name == 'TextAsset':
            d.m_Name = asset or expected_asset
            d.m_Script = raw.decode('utf-8', 'surrogateescape')
            d.save()
        elif obj.type.name == 'AssetBundle':
            d.m_Name = name
            # Unity 认包用的是 m_AssetBundleName（AssetBundle.name），不是 m_Name：
            # 不一起改掉的话 875 个包全都自称是那一个壳包，引擎会报一堆
            # "AssetBundle '壳包名' was already unloaded."，而且互相顶掉。
            d.m_AssetBundleName = name
            path = CONTAINER % chara
            cont = d.m_Container or []
            if isinstance(cont, dict):
                info = list(cont.values())[0]
                d.m_Container = {path: info}
            else:
                info = list(cont)[0][1]
                d.m_Container = [(path, info)]
            d.save()

    cab = 'CAB-%s' % hashlib.md5(('shadowbus-emote-%s' % chara).encode()).hexdigest()
    bundle = env.file
    renames = {}
    for inner in list(bundle.files.keys()):
        new_name = cab + '.resS' if inner.endswith('.resS') else cab
        if inner != new_name:
            renames[inner] = new_name
    for old, new in renames.items():
        bundle.files[new] = bundle.files.pop(old)

    data = env.file.save(packer='lz4')
    out = os.path.join(OUT, name)
    with io.open(out, 'wb') as fh:
        fh.write(data)
    return out, None


def verify(name):
    env = UnityPy.load(os.path.join(OUT, name))
    asset = None
    container = None
    cab = None
    for key, f in env.files.items():
        if hasattr(f, 'files'):
            inner = sorted(f.files.keys())
            cab = inner[0] if inner else None
    for obj in env.objects:
        d = obj.read()
        if obj.type.name == 'TextAsset':
            asset = (getattr(d, 'm_Name', '') or '', len(field_bytes(getattr(d, 'm_Script', None))))
        elif obj.type.name == 'AssetBundle':
            cont = d.m_Container or []
            items = list(cont.items()) if isinstance(cont, dict) else list(cont)
            container = items[0][0] if items else None
    return asset, container, cab


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--install', action='store_true')
    args = parser.parse_args()

    names = [l.strip() for l in open(EXPECTED, encoding='utf-8') if l.strip()]
    os.makedirs(OUT, exist_ok=True)
    built = failed = 0
    cabs = set()
    dupes = 0
    for name in names:
        out, error = build(name)
        if error:
            print('%-44s %s' % (name, error))
            failed += 1
            continue
        asset, container, cab = verify(name)
        chara = name[len(PREFIX):-len('.unity3d')]
        if cab in cabs:
            dupes += 1
        cabs.add(cab)
        if not container or ('master/chara/emote_chara_%s.' % chara) not in container.lower():
            print('%-44s container problem: %s' % (name, container))
            failed += 1
            continue
        built += 1
    print('built=%d failed=%d duplicated CAB=%d' % (built, failed, dupes))
    for name in names[:3]:
        print('  %-44s %s' % (name, verify(name)))

    if args.install:
        rows = 0
        for name in names:
            src = os.path.join(OUT, name)
            if not os.path.exists(src):
                continue
            dst = os.path.join(PC_A, name)
            shutil.copy2(src, dst)
        con = sqlite3.connect(MANIFEST_DB, timeout=10)
        cur = con.cursor()
        for name in names:
            dst = os.path.join(PC_A, name)
            if not os.path.exists(dst):
                continue
            digest = hashlib.md5(open(dst, 'rb').read()).hexdigest()
            cur.execute('insert or replace into t(k,v) values(?,?)', (name, digest))
            cur.execute('insert or replace into t(k,v) values(?,?)', ('a/' + name, digest))
            rows += 2
        con.commit()
        con.close()
        print('installed %d bundle(s), %d manifest row(s)' % (len(names), rows))


if __name__ == '__main__':
    main()
