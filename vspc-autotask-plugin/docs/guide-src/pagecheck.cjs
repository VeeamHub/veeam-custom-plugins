// Flags pages whose content stream is far smaller than typical, i.e. nearly empty pages
// left behind by a forced break. Compressed stream length is a good enough proxy for ink.
const fs = require('fs');
const raw = fs.readFileSync(process.argv[2], 'latin1');

const objects = new Map();
for (const m of raw.matchAll(/(\d+)\s+0\s+obj\b/g)) {
  const num = Number(m[1]);
  const start = m.index + m[0].length;
  const end = raw.indexOf('endobj', start);
  objects.set(num, raw.slice(start, end === -1 ? start + 4000 : end));
}
const obj = n => objects.get(n) || '';
const refIn = (body, key) => {
  const m = body.match(new RegExp('\\/' + key + '\\s+(\\d+)\\s+0\\s+R'));
  return m ? Number(m[1]) : null;
};

let catalog = null;
for (const [num, body] of objects) if (/\/Type\s*\/Catalog/.test(body)) { catalog = num; break; }
const pageOrder = [];
(function walk(num, d) {
  if (d > 32) return;
  const body = obj(num);
  if (/\/Type\s*\/Page[^s]/.test(body) || (!/\/Kids/.test(body) && /\/Contents/.test(body))) {
    pageOrder.push(num); return;
  }
  const kids = body.match(/\/Kids\s*\[([\s\S]*?)\]/);
  if (!kids) return;
  for (const k of kids[1].matchAll(/(\d+)\s+0\s+R/g)) walk(Number(k[1]), d + 1);
})(refIn(obj(catalog), 'Pages'), 0);

const sizes = pageOrder.map((p, i) => {
  const c = refIn(obj(p), 'Contents');
  const len = c ? Number((obj(c).match(/\/Length\s+(\d+)/) || [])[1] || 0) : 0;
  return { page: i + 1, len };
});
const bodyPages = sizes.slice(2); // skip cover + first contents page
const median = [...bodyPages].sort((a, b) => a.len - b.len)[Math.floor(bodyPages.length / 2)].len;
const sparse = bodyPages.filter(s => s.len < median * 0.35);

console.log(`pages: ${sizes.length}, median content stream: ${median} bytes`);
if (sparse.length) {
  console.log('SPARSE PAGES (under 35% of median): ' +
    sparse.map(s => `p${s.page} (${Math.round(100 * s.len / median)}%)`).join(', '));
} else {
  console.log('no unusually empty pages');
}
