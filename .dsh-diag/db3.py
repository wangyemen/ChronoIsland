
import sqlite3, os, json, datetime
p = os.path.expanduser(r"~/.local/share/opencode/opencode.db")
con = sqlite3.connect("file:" + p.replace("\\","/") + "?mode=ro", uri=True)
cur = con.cursor()
print("===== FULL opencode credential =====")
for r in cur.execute("select value, time_created, time_updated from credential where integration_id='opencode'"):
    v = json.loads(r[0])
    for k in list(v):
        if k in ("access","refresh") and v[k]:
            v[k] = v[k][:10] + "..." + "(len=%d)" % len(v[k])
    print(json.dumps(v, indent=2))
    print("time_created:", r[1], datetime.datetime.utcfromtimestamp(r[1]/1000))
    print("time_updated:", r[2], datetime.datetime.utcfromtimestamp(r[2]/1000))

print("\n===== sessions using provider opencode (free models) =====")
for r in cur.execute("select id, model, time_created, time_updated, title from session_v2 where model like '%opencode%' order by time_updated desc limit 25"):
    print(r[2], datetime.datetime.utcfromtimestamp(r[2]/1000), "|", r[3], datetime.datetime.utcfromtimestamp(r[3]/1000), "|", r[1], "|", (r[4] or "")[:40])

print("\n===== count of free-tier errors per day =====")
for r in cur.execute("select substr(data, instr(data,'\"completed\":')+0, 1) x, session_id, time_created from session_message where type='assistant' and data like '%free tier can only be used%'"):
    pass
rows = list(cur.execute("select time_created, session_id from session_message where type='assistant' and data like '%free tier can only be used%' order by time_created"))
from collections import Counter
c = Counter(datetime.datetime.utcfromtimestamp(t/1000).strftime("%Y-%m-%d") for t,_ in rows)
print("total free-tier errors:", len(rows))
for k in sorted(c): print(k, c[k])
print("first:", datetime.datetime.utcfromtimestamp(rows[0][0]/1000) if rows else None, rows[0][1] if rows else "")
print("last:", datetime.datetime.utcfromtimestamp(rows[-1][0]/1000) if rows else None, rows[-1][1] if rows else "")
