
import sqlite3, os, json
p = os.path.expanduser(r"~/.local/share/opencode/opencode.db")
con = sqlite3.connect("file:" + p.replace("\\","/") + "?mode=ro", uri=True)
con.row_factory = sqlite3.Row
cur = con.cursor()

def dump(title, sql, redact=()):
    print("\n===== " + title + " =====")
    try:
        for r in cur.execute(sql):
            d = dict(r)
            for k in list(d):
                if k in redact and d[k]:
                    v = str(d[k])
                    d[k] = "<len=%d sha?%s...>" % (len(v), v[:6])
                if isinstance(d[k], (bytes,)):
                    d[k] = "<bytes %d>" % len(d[k])
            print(json.dumps(d, ensure_ascii=False, default=str)[:1500])
    except Exception as e:
        print("ERR:", e)

dump("credentials", "select id, integration_id, label, connector_id, method_id, active, substr(value,1,120) as value_prefix, length(value) as vlen, time_updated from credential", ())
dump("kv", "select key, substr(value,1,300) as value from kv")
dump("account_state", "select * from account_state")
dump("permission", "select action, resource, count(*) c from permission group by action, resource order by c desc limit 20")
dump("projects", "select id, name, worktree, vcs from project")

sid = "ses_eb712b95dffeQtlWxnz8ffX8kH"
dump("failing session", "select id, project_id, slug, directory, title, model, agent, cost, tokens_input, tokens_output, time_created, time_updated from session_v2 where id='" + sid + "'")
dump("recent sessions", "select id, slug, title, model, agent, time_updated from session_v2 order by time_updated desc limit 8")
dump("last messages of failing session", "select id, type, seq, time_created, substr(data,1,600) as data from session_message where session_id='" + sid + "' order by seq desc limit 12")
dump("recent assistant errors", "select session_id, seq, time_created, substr(data,1,500) as data from session_message where type='assistant' and data like '%free tier%' order by time_created desc limit 5")
