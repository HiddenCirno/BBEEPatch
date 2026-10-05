# -*- coding: utf-8 -*-
import io, os, struct
BASE=os.path.join(os.path.dirname(os.path.abspath(__file__)),"..")
P=os.path.join(BASE,"extracted","raw","localization_chs.ab")
M=0x89ABCDEF
def chash(s):
    h=0x01234567
    for c in s.encode("utf-8"):
        h=((h^c)*M)&0xFFFFFFFF
    return (h*M)&0xFFFFFFFF
d=open(P,"rb").read()
table={}
i,n=0,len(d)
while i+8<=n:
    k,ln=struct.unpack_from("<II",d,i)
    if ln>4000 or i+8+ln>n: i+=1; continue
    try: t=d[i+8:i+8+ln].decode("utf-8")
    except UnicodeDecodeError: i+=1; continue
    table.setdefault(k,t); i+=8+ln
OUT=os.path.join(BASE,"tools","_recon_names_out.txt")
L=[f"loc entries {len(table)}"]
# 1) try candidate name-key prefixes on all actor ids
actor_ids=['100101','100201','100301','100401','100501','101101','101401','101801','102201','102501','102801','102901','103101','103401','103501','103701','103801']
for pre in ["ActorName_","RoleName_","BaseActorName_","ActorProfile_","ActorTitle_","ActorSubTitle_","ActorUltra_","CharName_","HeroName_"]:
    hits=[]
    for aid in actor_ids:
        t=table.get(chash(pre+aid))
        if t: hits.append((pre+aid,t))
    L.append(f"\n[{pre}] hits={len(hits)}")
    for k,t in hits: L.append(f"   {k} = {t[:70]}")
# 2) full ActorActionName_ scan group by prefix (first 3 leading digits pattern)
L.append("\n===== ActorActionName_ prefix groups =====")
import collections
groups=collections.defaultdict(list)
for num in range(10000,999999):
    t=table.get(chash("ActorActionName_"+str(num)))
    if t:
        # prefix = digits minus last 3 (slot code ~3 digits)
        groups[str(num)[:-3]].append((num,t))
for pre in sorted(groups,key=lambda x:int(x)):
    rows=sorted(groups[pre])
    L.append(f"\n-- prefix {pre} ({len(rows)}) --")
    for num,t in rows[:40]:
        L.append(f"   {num} {t[:60]}")
io.open(OUT,"w",encoding="utf-8").write("\n".join(L))
print("done",OUT)
