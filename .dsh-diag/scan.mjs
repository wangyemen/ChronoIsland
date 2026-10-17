
import fs from "node:fs";
const exe = "D:/dsh/npm-global/node_modules/@opencode/cli/bin/opencode.exe";
const buf = fs.readFileSync(exe);
console.log("size", buf.length);
const needle = Buffer.from("free tier can only be used", "utf8");
let idx = 0, hits = 0;
while (true) {
  const i = buf.indexOf(needle, idx);
  if (i < 0) break;
  hits++;
  const start = Math.max(0, i - 900);
  const end = Math.min(buf.length, i + 500);
  console.log("\n===== HIT " + hits + " at " + i + " =====");
  console.log(buf.subarray(start, end).toString("latin1"));
  idx = i + 1;
  if (hits > 6) break;
}
console.log("\nTOTAL HITS:", hits);
const n2 = Buffer.from("Download it at https://opencode.ai", "utf8");
console.log("download-string hits:", (() => { let c=0,x=0; while(true){const i=buf.indexOf(n2,x); if(i<0)break; c++; x=i+1;} return c; })());
