
import sqlite3, os, json
p = os.path.expanduser(r"~/.local/share/opencode/opencode.db")
con = sqlite3.connect("file:" + p.replace("\\","/") + "?mode=ro", uri=True)
cur = con.cursor()
row = cur.execute("select value from kv where key='models-dev:catalog'").fetchone()
v = json.loads(row[0])
print("catalog updatedAt:", v.get("updatedAt"), "digest:", v.get("digest"))
cat = json.loads(v["body"])
print("providers in catalog:", len(cat))
print("has 'opencode':", "opencode" in cat)
op = cat.get("opencode", {})
print("opencode provider keys:", list(op.keys()))
print("opencode npm:", op.get("npm"), "| api:", op.get("api"), "| name:", op.get("name"))
print("opencode env:", op.get("env"))
models = op.get("models", {})
print("opencode model count:", len(models))
print("opencode model ids:", sorted(models.keys()))
for mid, m in sorted(models.items()):
    if "free" in mid or "deepseek" in mid:
        print("\n--- MODEL", mid, "---")
        print(json.dumps(m, indent=2, ensure_ascii=False)[:1800])

print("\n===== any provider whose models contain the failing id =====")
for pid, pv in cat.items():
    for mid, m in (pv.get("models") or {}).items():
        if mid == "deepseek-v4.1-flash-free":
            print("provider", pid, "->", mid)
            print(json.dumps({"providerApi": pv.get("api"), "providerNpm": pv.get("npm"), "model": m}, indent=2, ensure_ascii=False)[:2000])

print("\n===== count of *-free models per provider =====")
for pid, pv in cat.items():
    f = [m for m in (pv.get("models") or {}) if "free" in m]
    if f: print(pid, len(f), f[:6])
