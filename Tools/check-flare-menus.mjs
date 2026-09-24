import assert from 'node:assert/strict';
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { resolve, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// Static metadata check only: never opens Unity or runs developer commands.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const directories = ['Assets/com.avatarcatalog.remote/Editor', 'Assets/PackagingVerification/Editor'];
const menus = [];
for (const directory of directories) {
  if (!existsSync(join(root, directory))) continue; // Optional, untracked local verification harnesses.
  for (const name of readdirSync(join(root, directory)).filter(n => n.endsWith('.cs'))) {
    const file = `${directory}/${name}`;
    const code = readFileSync(join(root, file), 'utf8');
    const constants = new Map([...code.matchAll(/const\s+string\s+(\w+)\s*=\s*"([^"]*)"/g)].map(m => [m[1], m[2]]));
    assert(!code.includes('Tools/Avatar Catalog/') && !code.includes('GameObject/Avatar Catalog/'), `${file}: old menu root`);
    for (const m of code.matchAll(/\[MenuItem\(([^\r\n]*)\)\]/g)) {
      const [expression, validation] = m[1].split(',');
      const path = [...expression.matchAll(/"(?:\\.|[^"\\])*"|\b[A-Za-z_]\w*\b/g)].map(part => {
        const token = part[0];
        if (token.startsWith('"') && token.endsWith('"')) return JSON.parse(token);
        assert(constants.has(token), `${file}: unresolved menu token ${token}`);
        return constants.get(token);
      }).join('');
      if (path.startsWith('Tools/FLARE/') || path.startsWith('GameObject/FLARE/'))
        menus.push({ path, validate: validation?.trim() === 'true', file });
    }
  }
}
const actions = menus.filter(m => !m.validate);
const keys = actions.map(m => m.path);
assert.equal(keys.length, new Set(keys).size, 'duplicate action registration');
for (const menu of menus.filter(m => m.validate))
  assert(keys.includes(menu.path), `${menu.file}: validator has no matching action`);
const publicTools = keys.filter(p => p.startsWith('Tools/') && !p.startsWith('Tools/FLARE/Developer/'));
assert.deepEqual(publicTools.sort(), [
  'Tools/FLARE/RAC2 Creator...',
  'Tools/FLARE/User Guide...',
  'Tools/FLARE/Catalog/Configure Fixed URLs...',
  'Tools/FLARE/Catalog/Configure Discovery Catalog Fixed URLs...',
].sort());
assert.deepEqual(keys.filter(p => p.startsWith('GameObject/') && !p.startsWith('GameObject/FLARE/Developer/')),
  ['GameObject/FLARE/Create RAC2 from this object...']);
assert(!keys.some(p => p.includes('Export Placed Sirius Walking')), 'obsolete exporter still exposed');
for (const name of ['FlareSiriusExport', 'FlareMariaSceneSetup', 'FlareInspectorRegression', 'FlareBoothToleranceFix']) {
  if (!existsSync(join(root, 'Assets/PackagingVerification/Editor', `${name}.cs`))) continue;
  const code = readFileSync(join(root, 'Assets/PackagingVerification/Editor', `${name}.cs`), 'utf8');
  assert(!code.includes('[InitializeOnLoad'), `${name}: developer action starts automatically`);
}
for (const file of ['Assets/RemoteAvatarCatalogDistribution/README-JA.md',
  'Assets/RemoteAvatarCatalogDistribution/PORTABLE-0.2.4-JA.md',
  'Assets/com.avatarcatalog.remote/README-RAC2-JA.md']) {
  const doc = readFileSync(join(root, file), 'utf8');
  assert(doc.includes('Tools > FLARE > RAC2 Creator...'), `${file}: current entry missing`);
  assert(!doc.includes('Tools > Avatar Catalog >'), `${file}: obsolete instructions`);
}
console.log(`PASS: ${actions.length} unique FLARE actions; ${menus.length-actions.length} validators matched; public/developer separation, no legacy root, obsolete exporter hidden, diagnostics opt-in, docs aligned.`);
