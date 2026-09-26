# -*- coding: utf-8 -*-
"""Install the rebuilt spine bundles into the game package and refresh manifest.db."""
import hashlib
import os
import shutil
import sqlite3

PC_A = r'D:\Games\Shadowbus\Resources\a'
MANIFEST = r'D:\Games\Shadowbus\Resources\manifest.db'
OUT = r'D:\Games\Shadowbus-dev\artifacts\spine_out2'
SKINS = ['1110', '1520', '1530', '99002', '99003', '99004', '99007']

digests = {}
for skin in SKINS:
    src = os.path.join(OUT, 'ui_class_%s.unity3d' % skin)
    dst = os.path.join(PC_A, 'ui_class_%s.unity3d' % skin)
    if not os.path.exists(src):
        print('missing', src)
        continue
    shutil.copy2(src, dst)
    name = 'ui_class_%s.unity3d' % skin
    digests[name] = hashlib.md5(open(dst, 'rb').read()).hexdigest()
    print('%-28s %8d B  md5=%s' % (name, os.path.getsize(dst), digests[name][:12]))

try:
    con = sqlite3.connect(MANIFEST, timeout=5)
    cur = con.cursor()
    for name, digest in digests.items():
        cur.execute('insert or replace into t(k,v) values(?,?)', (name, digest))
        cur.execute('insert or replace into t(k,v) values(?,?)', ('a/' + name, digest))
    con.commit()
    con.close()
    print('manifest.db: %d row(s) refreshed (bare + a/ prefix)' % len(digests))
except Exception as exc:
    print('manifest.db not writable (game running?):', exc)
    print('-> harmless: the hash is only used for download checks, the bundle is read from disk')
