# -*- coding: utf-8 -*-
"""Build the list of CN text bundles to pull: every text table the game loads + story text."""
import re
import sqlite3

SRC = r'D:\Games\Shadowbus-dev\full_decompile\Assembly-CSharp.decompiled.cs'
CN_DB = r'D:\Games\Shadowbus-dev\artifacts\cn_manifest.db'
OUT = r'D:\Games\Shadowbus-dev\cn_text_list.txt'

text = open(SRC, encoding='utf-8', errors='surrogateescape').read()
paths = sorted(set(re.findall(r'GetAssetTypePath\(\s*"(text/[^"]+)"', text)))

con = sqlite3.connect('file:%s?immutable=1' % CN_DB.replace('\\', '/'), uri=True)
keys = set(k for (k,) in con.execute('select k from t'))
con.close()

wanted = []
missing = []
for p in paths:
    bundle = 'master_%s.unity3d' % p.split('/')[-1]
    (wanted if bundle in keys else missing).append(bundle)

story = sorted(k for k in keys if k.lower().startswith('storylang_'))
story_extra = sorted(k for k in keys if 'story' in k.lower() and 'text' in k.lower() and k.endswith('.unity3d'))
textmaster = sorted(k for k in keys if re.match(r'master_\w*text\w*\.unity3d$', k.lower()))

lines = []
lines.append('# text tables the game loads')
lines.extend(wanted)
lines.append('')
lines.append('# same-named masters that also look like text tables (safety net)')
lines.extend(n for n in textmaster if n not in wanted)
lines.append('')
lines.append('# story text bundles')
lines.extend(story)
lines.append('')
lines.append('# other story-ish text assets')
lines.extend(n for n in story_extra if n not in story)
lines.append('')
lines.append('# NOT in the CN manifest: %s' % ', '.join(missing))

open(OUT, 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
print('tables wanted: %d  (not in CN: %d)' % (len(wanted), len(missing)))
print('not in CN:', missing)
print('textmaster extras: %d' % len([n for n in textmaster if n not in wanted]))
print('story bundles: %d' % len(story))
print('story-ish extras: %d' % len([n for n in story_extra if n not in story]))
print('wrote', OUT)
