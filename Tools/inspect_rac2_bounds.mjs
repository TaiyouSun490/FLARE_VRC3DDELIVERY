import fs from 'node:fs';
import { createHash } from 'node:crypto';
const file = fs.readFileSync(process.argv[2]);
function lz4(input, size) {
  const out = Buffer.alloc(size); let r = 0, w = 0;
  while (r < input.length) {
    const token = input[r++]; let n = token >>> 4;
    if (n === 15) { let a; do { a = input[r++]; n += a; } while (a === 255); }
    if (r + n > input.length || w + n > size) throw Error('LZ4 literal overflow');
    input.copy(out, w, r, r + n); r += n; w += n;
    if (r === input.length) break;
    const off = input.readUInt16LE(r); r += 2; n = (token & 15) + 4;
    if ((token & 15) === 15) { let a; do { a = input[r++]; n += a; } while (a === 255); }
    if (!off || off > w || w + n > size) throw Error('LZ4 match overflow');
    for (let i = 0; i < n; i++) { out[w] = out[w - off]; w++; }
  }
  if (w !== size) throw Error('LZ4 length mismatch');
  return out;
}
function adler(data) { let a = 1, b = 0; for (const v of data) { a = (a + v) % 65521; b = (b + a) % 65521; } return ((b << 16) | a) >>> 0; }
const u = (b, o) => b.readUInt32LE(o), f = (b, o) => b.readFloatLE(o);
const v3 = (b, o) => [f(b,o),f(b,o+4),f(b,o+8)];
if (file.toString('ascii',0,4) !== 'RAC2' || u(file,4) !== 3 || u(file,8) !== file.length) throw Error('Bad RAC2 v3');
const flags = u(file,16), count = u(file,12), sections = {};
for (let i = 0; i < count; i++) {
  const t = 24 + i * ((flags & 1) ? 24 : 16), name = file.toString('ascii',t,t+4);
  const stored = file.subarray(u(file,t+4),u(file,t+4)+u(file,t+8));
  const raw = (flags & 1) && u(file,t+16) ? lz4(stored,u(file,t+12)) : stored;
  if ((flags & 3) === 3 && adler(raw) !== u(file,t+20)) throw Error('Checksum mismatch ' + name);
  sections[name] = raw;
}
const box = (b,o) => { const c=v3(b,o), s=v3(b,o+12); return {min:c.map((v,i)=>v-s[i]/2),max:c.map((v,i)=>v+s[i]/2)}; };
const meta=box(sections.META,0), node=sections.NODE, actual={min:[Infinity,Infinity,Infinity],max:[-Infinity,-Infinity,-Infinity]};
const nodes=[]; let p=8;
for (let i=0; i<u(node,4); i++) {
  const bytes=u(node,p), nf=u(node,p+4), nl=u(node,p+12), ml=u(node,p+16), matl=u(node,p+20);
  const name=node.toString('utf8',p+80,p+80+nl), pos=v3(node,p+40), q=[...v3(node,p+52),f(node,p+64)], scale=v3(node,p+68);
  const info=p+80+nl+ml+matl;
  let bounds;
  if(nf & 1) bounds=box(node,info+40);
  else {
    const mesh=p+80+nl, vertices=u(node,mesh+4);
    bounds={min:[Infinity,Infinity,Infinity],max:[-Infinity,-Infinity,-Infinity]};
    for(let j=0;j<vertices;j++) v3(node,mesh+24+j*12).forEach((v,a)=>{ bounds.min[a]=Math.min(bounds.min[a],v); bounds.max[a]=Math.max(bounds.max[a],v); });
  }
  for(let corner=0;corner<8;corner++) {
    const v=bounds.min.map((x,j)=>((corner & (1<<j))?bounds.max[j]:x)*scale[j]);
    const [x,y,z,w]=q, [vx,vy,vz]=v;
    const t=[2*(y*vz-z*vy),2*(z*vx-x*vz),2*(x*vy-y*vx)];
    const out=[vx+w*t[0]+y*t[2]-z*t[1],vy+w*t[1]+z*t[0]-x*t[2],vz+w*t[2]+x*t[1]-y*t[0]].map((v,j)=>v+pos[j]);
    out.forEach((v,j)=>{actual.min[j]=Math.min(actual.min[j],v);actual.max[j]=Math.max(actual.max[j],v);});
  }
  nodes.push({name,pos,scale,q,bounds,frames:(nf & 1)?u(node,info+8):0}); p+=bytes;
}
if (p!==node.length) throw Error('Node lengths inconsistent');
const deltaMin=actual.min.map((v,i)=>v-meta.min[i]), deltaMax=meta.max.map((v,i)=>v-actual.max[i]);
const contains = nodes.length ? [...deltaMin,...deltaMax].every(v=>v>=-.001) : null;
const consistent = nodes.length ? contains && [...deltaMin,...deltaMax].every(v=>v<=.101) : null;
console.log(JSON.stringify({sha256:createHash('sha256').update(file).digest('hex'),flags,
  renderNodes:nodes.length,vatNodes:nodes.filter(n=>n.frames>0).length,meta,actual,
  deltaMin,deltaMax,contains,consistent,...(!process.argv.includes('--summary')?{nodes}:{})},null,2));
if(process.argv.includes('--require-consistent') && consistent!==true) process.exitCode=1;
