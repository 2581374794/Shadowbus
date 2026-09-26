# -*- coding: utf-8 -*-
"""Rebuild the 7 spine bundles:

1. donor-shell = the intl 2020 bundle, with the CN skeleton / atlas / textures swapped in;
2. **unique internal CAB name per bundle** - sharing the donor's CAB made Unity refuse the
   second one with "another AssetBundle with the same files is already loaded";
3. copy the CN prefab's transform values onto the pivot nodes, so the model is framed the way
   the CN client frames it (our donor prefab was tuned for a different skeleton -> the model
   sat too low and slightly to the right).
"""
import hashlib
import io
import os
import sys

import UnityPy

UnityPy.config.FALLBACK_UNITY_VERSION = '2020.3.18f1'
PC_A = r'D:\Games\Shadowbus\Resources\a'
CN = r'D:\Games\Shadowbus-dev\artifacts\cn_pull'
OUT = r'D:\Games\Shadowbus-dev\artifacts\spine_out2'
DONOR = os.path.join(PC_A, 'ui_class_101.unity3d')
SKINS = ['1110', '1520', '1530', '99002', '99003', '99004', '99007']
MECANIM_PREFIX = 'Spine Mecanim GameObject'
ANIM_TRANSFORM = 'AnimationTransform'

os.makedirs(OUT, exist_ok=True)


def field_bytes(value):
    if value is None:
        return b''
    if isinstance(value, bytes):
        return value
    return value.encode('utf-8', 'surrogateescape')


def as_text(raw):
    return raw.decode('utf-8', 'surrogateescape')


def clone_vec3(v):
    import UnityPy.classes as uc  # noqa
    out = v.__class__()
    out.x, out.y, out.z = v.x, v.y, v.z
    return out


def read_cn(skin):
    """Skeleton / atlas / textures plus the prefab pivot transforms the CN client uses."""
    env = UnityPy.load(os.path.join(CN, 'ui_class_%s.unity3d' % skin))
    out = {'skel': b'', 'atlas': b'', 'tex': {}, 'scale': None, 'nodes': {}}

    gos = {}
    trs = {}
    for obj in env.objects:
        d = obj.read()
        if obj.type.name == 'GameObject':
            gos[obj.path_id] = d
        elif obj.type.name == 'Transform':
            trs[obj.path_id] = d

    for pid, t in trs.items():
        go = getattr(t, 'm_GameObject', None)
        gid = getattr(go, 'path_id', None) if go is not None else None
        name = gos[gid].m_Name if gid in gos else ''
        if name.startswith(MECANIM_PREFIX) or name == ANIM_TRANSFORM:
            key = 'mecanim' if name.startswith(MECANIM_PREFIX) else 'anim'
            out['nodes'][key] = {
                'name': name,
                'pos': clone_vec3(t.m_LocalPosition),
                'scale': clone_vec3(t.m_LocalScale),
                'rot': t.m_LocalRotation,
            }

    for obj in env.objects:
        d = obj.read()
        name = getattr(d, 'm_Name', '') or ''
        if obj.type.name == 'TextAsset':
            raw = field_bytes(getattr(d, 'm_Script', None))
            if name.endswith('.atlas'):
                out['atlas'] = raw
            else:
                out['skel'] = raw
        elif obj.type.name == 'Texture2D':
            out['tex'][name] = d.image.convert('RGBA')
        elif obj.type.name == 'MonoBehaviour' and name.endswith('_SkeletonData'):
            out['scale'] = getattr(d, 'scale', None)
    return out


def build(skin):
    cn = read_cn(skin)
    env = UnityPy.load(DONOR)
    notes = []

    for obj in env.objects:
        d = obj.read()
        name = getattr(d, 'm_Name', '') or ''
        if obj.type.name == 'TextAsset':
            if name.endswith('.atlas'):
                d.m_Name = 'class_%s.atlas' % skin
                d.m_Script = as_text(cn['atlas'])
            else:
                d.m_Name = 'class_%s' % skin
                d.m_Script = as_text(cn['skel'])
            d.save()
            notes.append('text')
        elif obj.type.name == 'Texture2D':
            key = 'class_%s%s' % (skin, '_A' if name.endswith('_A') else '')
            if key in cn['tex']:
                img = cn['tex'][key]
                d.image = img
                d.m_Name = key
                d.m_Width, d.m_Height = img.size
                d.save()
                notes.append('tex%d' % img.size[0])
        elif obj.type.name in ('GameObject', 'AnimatorController', 'Material') and name.startswith('class_101'):
            d.m_Name = name.replace('class_101', 'class_%s' % skin)
            d.save()
        elif obj.type.name == 'MonoBehaviour' and name.endswith('_SkeletonData'):
            if cn['scale'] is not None:
                try:
                    d.scale = cn['scale']
                    d.save()
                except Exception as exc:
                    notes.append('scale?%s' % exc)
        elif obj.type.name == 'GameObject' and name.startswith(MECANIM_PREFIX):
            d.m_Name = cn['nodes'].get('mecanim', {}).get('name', name)
            d.save()
        elif obj.type.name == 'AssetBundle':
            d.m_Name = 'ui_class_%s.unity3d' % skin
            # 引擎认包看的是 m_AssetBundleName；不改的话 7 个包都自称是那个壳包。
            d.m_AssetBundleName = 'ui_class_%s.unity3d' % skin
            cont = d.m_Container or []
            if isinstance(cont, dict):
                d.m_Container = {p.replace('class_101', 'class_%s' % skin): i for p, i in cont.items()}
            else:
                d.m_Container = [(p.replace('class_101', 'class_%s' % skin), i) for p, i in cont]
            d.save()
            notes.append('container')

    # pivot transforms: without this the model sits where the donor's skeleton wanted it.
    for obj in env.objects:
        if obj.type.name != 'Transform':
            continue
        d = obj.read()
        go = getattr(d, 'm_GameObject', None)
        if go is None:
            continue
        go_name = getattr(go.read(), 'm_Name', '') or ''
        if go_name.startswith(MECANIM_PREFIX):
            node = cn['nodes'].get('mecanim')
        elif go_name == ANIM_TRANSFORM:
            node = cn['nodes'].get('anim')
        else:
            node = None
        if node is None:
            continue
        d.m_LocalPosition = node['pos']
        d.m_LocalScale = node['scale']
        d.m_LocalRotation = node['rot']
        d.save()
        notes.append('xform:%s' % go_name[-24:])

    # unique internal archive name, otherwise Unity rejects the second bundle outright.
    cab = 'CAB-%s' % hashlib.md5(('shadowbus-spine-%s' % skin).encode()).hexdigest()
    bundle = env.file
    try:
        renames = {}
        for inner in list(bundle.files.keys()):
            new_name = cab + '.resS' if inner.endswith('.resS') else cab
            if inner != new_name:
                renames[inner] = new_name
        for old, new in renames.items():
            bundle.files[new] = bundle.files.pop(old)
        if renames:
            notes.append('cab=%s' % cab[4:12])
    except Exception as exc:
        notes.append('cab?%s' % exc)

    data = env.file.save(packer='lz4')
    out = os.path.join(OUT, 'ui_class_%s.unity3d' % skin)
    with io.open(out, 'wb') as fh:
        fh.write(data)
    return out, notes, cab


def verify(skin, path, cab):
    cn = read_cn(skin)
    env = UnityPy.load(path)
    res = {'cab': None, 'skel': False, 'nodes': {}, 'container': None}
    for name, f in env.files.items():
        if hasattr(f, 'files'):
            res['cab'] = list(f.files.keys())
    for obj in env.objects:
        d = obj.read()
        name = getattr(d, 'm_Name', '') or ''
        if obj.type.name == 'TextAsset' and not name.endswith('.atlas'):
            res['skel'] = field_bytes(getattr(d, 'm_Script', None)) == cn['skel']
        elif obj.type.name == 'AssetBundle':
            cont = d.m_Container or []
            items = list(cont.items()) if isinstance(cont, dict) else list(cont)
            res['container'] = items[0][0] if items else None
        elif obj.type.name == 'Transform':
            go = getattr(d, 'm_GameObject', None)
            if go is None:
                continue
            go_name = getattr(go.read(), 'm_Name', '') or ''
            if go_name.startswith(MECANIM_PREFIX) or go_name == ANIM_TRANSFORM:
                p = d.m_LocalPosition
                res['nodes'][go_name] = '(%s,%s,%s)' % (round(p.x, 3), round(p.y, 3), round(p.z, 3))
    return res


for skin in SKINS:
    out, notes, cab = build(skin)
    v = verify(skin, out, cab)
    print('%-7s %8d B  cab=%s  skel=%s  container=%s' % (
        skin, os.path.getsize(out), ','.join(v['cab'] or []), v['skel'],
        (v['container'] or '')[-34:]))
    for k, val in sorted(v['nodes'].items()):
        print('        %-42s %s' % (k, val))
print('output:', OUT)
