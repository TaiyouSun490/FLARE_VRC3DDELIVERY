import assert from 'node:assert/strict';
import fs from 'node:fs';
const pairs=JSON.parse(fs.readFileSync('Tools/flare-localization.json','utf8'));
const table=new Map(pairs);
assert.equal(table.size,pairs.length,'duplicate key');
const localizer=fs.readFileSync('Assets/com.avatarcatalog.remote/Editor/FlareLocalization.cs','utf8');
for(const [en,ja] of pairs) {
 assert(en.length && ja.length,'empty translation');
 assert(!/[ぁ-んァ-ン一-龯]/.test(en),'Japanese in English resource: '+en);
 assert(localizer.includes('{ '+JSON.stringify(en)+', '+JSON.stringify(ja)+' }'),'resource drift: '+en);
}
for(const name of ['Rac2CreatorWindow.cs','Rac2CreatorWindow.BoothGuide.cs','Rac2CreatorPerformance.cs','Rac2AvatarPreprocessor.cs','Rac2ConstraintBakeBarrier.cs','Rac2PhysBoneBakeSession.cs','Rac2VatFrameBaker.cs']) {
 const source=fs.readFileSync('Assets/com.avatarcatalog.remote/Editor/'+name,'utf8');
 for(const m of source.matchAll(/(?:\bL|FlareLocalization\.Text)\(("(?:\\.|[^"\\])*")\)/g)) assert(table.has(JSON.parse(m[1])),name+': missing key '+m[1]);
 assert(!/const string\s+\w+\s*=\s*(?:L|FlareLocalization\.Text)\(/.test(source),'localized const');
}
const creator=fs.readFileSync('Assets/com.avatarcatalog.remote/Editor/Rac2CreatorWindow.cs','utf8');
assert(creator.includes('FlareLocalization.DrawLanguage()'),'selector missing');
assert(creator.includes('ShowExportError('),'error recovery missing');
assert(!creator.includes('private string _scanSummary = L('),'EditorPrefs accessed in field initializer');
const release=fs.readFileSync('Assets/com.avatarcatalog.remote/Editor/Rac2ReleaseBuilder.cs','utf8');
for(const file of ['FlareLocalization.cs','Rac2CreatorPerformance.cs','Rac2PerformanceRating.cs','USER-GUIDE-JA.md','USER-GUIDE-EN.md'])assert(release.includes(file),'release omission '+file);
for(const language of ['JA','EN']) {
 const guide=fs.readFileSync('Assets/RemoteAvatarCatalogDistribution/USER-GUIDE-'+language+'.md','utf8');
 for(const word of ['3.10.4','2.3.4','250,000','500,000','128 MiB','64 MiB','Use Japanese','Tools > FLARE > RAC2 Creator...'])assert(guide.includes(word),'guide missing '+word);
}
console.log('PASS: '+pairs.length+' bilingual resources, call-site coverage, selector, error recovery, package includes and guide limits.');
