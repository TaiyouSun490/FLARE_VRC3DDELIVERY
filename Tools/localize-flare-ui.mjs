// One-time mechanical migration of known UI literals. Never replaces paths or serialized identifiers.
import fs from 'node:fs';
const pairs=JSON.parse(fs.readFileSync('Tools/flare-localization.json','utf8'));
const map=new Map(pairs.flatMap(([en,ja])=>[[en,en],[ja,en]]));
for(const name of ['Rac2CreatorWindow.cs','Rac2CreatorWindow.BoothGuide.cs','Rac2CreatorPerformance.cs','Rac2AvatarPreprocessor.cs','Rac2ConstraintBakeBarrier.cs','Rac2PhysBoneBakeSession.cs','Rac2VatFrameBaker.cs']) {
 const path='Assets/com.avatarcatalog.remote/Editor/'+name;
 let source=fs.readFileSync(path,'utf8');
 source=source.replace(/"(?:\\.|[^"\\])*"/g,(literal,offset)=>{
  if(source.slice(Math.max(0,offset-2),offset)==='L(' || source.slice(Math.max(0,offset-23),offset).endsWith('FlareLocalization.Text('))return literal;
  const value=JSON.parse(literal),key=map.get(value);
  const method=name.startsWith('Rac2Creator')?'L':'FlareLocalization.Text';
  return key===undefined?literal:method+'('+JSON.stringify(key)+')';
 });
 source=source.replace('const string legend =','string legend =');
 // Field initialization must not access EditorPrefs during Unity deserialization.
 source=source.replace('private string _scanSummary = L("Select the root GameObject of the exhibit.");','private string _scanSummary = "";');
 fs.writeFileSync(path,source);
}
