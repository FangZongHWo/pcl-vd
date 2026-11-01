import struct
import sys

p = sys.argv[1]
d = open(p, 'rb').read()
num = struct.unpack('>H', d[4:6])[0]
tables = {}
for i in range(num):
    off = 12 + 16 * i
    tag = d[off:off + 4].decode('latin1')
    o, l = struct.unpack('>II', d[off + 8:off + 16])
    tables[tag] = (o, l)

print("size:", len(d), "tables:", sorted(tables))

if 'name' in tables:
    no, nl = tables['name']
    fmt, count, strOff = struct.unpack('>HHH', d[no:no + 6])
    names = {}
    for i in range(count):
        rec = no + 6 + 12 * i
        pid, eid, lid, nid, ln, off = struct.unpack('>HHHHHH', d[rec:rec + 12])
        s = d[no + strOff + off: no + strOff + off + ln]
        try:
            s = s.decode('utf-16-be') if pid == 3 else s.decode('latin1')
        except Exception:
            continue
        names.setdefault(nid, set()).add(s)
    for nid, label in [(1, 'Family'), (2, 'Subfamily'), (4, 'Full'), (6, 'PostScript'), (16, 'TypoFamily')]:
        if nid in names:
            print(label, '=', sorted(names[nid])[:3])

# 大概统计覆盖的字符
if 'cmap' in tables:
    print("has cmap: yes")
