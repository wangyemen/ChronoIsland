
import sqlite3, os, json
p = os.path.expanduser(r"~/.local/share/opencode/opencode.db")
con = sqlite3.connect("file:" + p.replace("\\","/") + "?mode=ro", uri=True)
cur = con.cursor()
tabs = [r[0] for r in cur.execute("select name from sqlite_master where type='table'")]
print("TABLES:", tabs)
for t in tabs:
    try:
        cols = [r[1] for r in cur.execute("pragma table_info(" + t + ")")]
        n = cur.execute("select count(*) from " + t).fetchone()[0]
        print("-- " + t + " rows=" + str(n) + " cols=" + str(cols))
    except Exception as e:
        print("ERR " + t + ": " + str(e))
