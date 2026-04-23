const API='/api';
const s={token:localStorage.getItem('token'),user:JSON.parse(localStorage.getItem('user')||'null'),isAdmin:false,incidents:[],kb:[]};
if(s.token&&s.user){showApp();}else{showLogin();}
document.getElementById('login-form').addEventListener('submit',async e=>{
e.preventDefault();
const el=document.getElementById('le');el.classList.add('hidden');
try{
const r=await fetch(`${API}/auth/login`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({username:document.getElementById('lu').value,password:document.getElementById('lp').value})});
if(!r.ok)throw new Error('Credenciales invalidas');
const d=await r.json();
s.token=d.token;s.user={username:d.username,role:d.role};s.isAdmin=d.role==='SuperAdmin'||d.role==='Admin';
localStorage.setItem('token',s.token);localStorage.setItem('user',JSON.stringify(s.user));
showApp();
}catch(x){el.textContent=x.message;el.classList.remove('hidden');}
});
function showLogin(){document.getElementById('login-screen').classList.remove('hidden');document.getElementById('app').classList.add('hidden');}
function showApp(){document.getElementById('login-screen').classList.add('hidden');document.getElementById('app').classList.remove('hidden');s.isAdmin=s.user.role==='SuperAdmin'||s.user.role==='Admin';document.getElementById('rb').textContent=s.user.role;if(s.isAdmin)document.getElementById('n-adm').classList.remove('hidden');n('dashboard');ld();}
function logout(){localStorage.clear();s.token=null;s.user=null;showLogin();}
async function api(m,p,b){
const o={method:m,headers:{'Content-Type':'application/json',Authorization:'Bearer '+s.token}};
if(b)o.body=JSON.stringify(b);
const r=await fetch(API+p,o);
if(r.status===401){logout();throw new Error('Sesion expirada');}
if(!r.ok){const e=await r.text();throw new Error(e);}
return r.status===204?null:await r.json();
}
function toast(msg,t='info'){
const el=document.getElementById('toast'),ic=document.getElementById('ti'),tx=document.getElementById('tm');
tx.textContent=msg;ic.className='w-5 h-5 rounded-full flex items-center justify-center text-xs '+(t==='success'?'bg-emerald-500/20 text-emerald-400':t==='error'?'bg-rose-500/20 text-rose-400':'bg-indigo-500/20 text-indigo-400');
el.classList.remove('translate-y-20','opacity-0');
setTimeout(()=>el.classList.add('translate-y-20','opacity-0'),3000);
}
function n(v){
document.querySelectorAll('.view-section').forEach(el=>{el.classList.remove('active');el.style.opacity='0';});
document.querySelectorAll('[id^="n-"]').forEach(el=>el.classList.remove('text-white','bg-white/10'));
const t=document.getElementById('v-'+v);t.classList.add('active');
const nb=document.getElementById('n-'+v.replace('dashboard','dash').replace('incidents','inc').replace('admin','adm').replace('kb','kb'));
if(nb){nb.classList.add('text-white','bg-white/10');}
anime({targets:t,opacity:[0,1],translateY:[12,0],duration:400,easing:'easeOutCubic'});
if(v==='incidents')li();if(v==='kb')lk();if(v==='admin'){lm();lh();}
}
async function ld(){
try{
const d=await api('GET','/support/summary');
anime({targets:['#c-open','#c-crit','#c-res','#c-avg'],translateY:[20,0],opacity:[0,1],delay:anime.stagger(80),duration:600,easing:'easeOutExpo'});
countUp('c-open',d.totalOpen||0);countUp('c-crit',d.totalCritical||0);countUp('c-res',d.totalResolved||0);countUp('c-avg',d.averageResolutionMinutes||0);
const rec=await api('GET','/support/incidents?status=&severity=&category=&pageSize=5');
const lr=document.getElementById('l-rec');
lr.innerHTML=(rec||[]).length?rec.map(x=>rRow(x,true)).join(''):'<div class="p-6 text-center text-slate-500">Sin incidentes</div>';
anime({targets:lr.children,translateX:[-20,0],opacity:[0,1],delay:anime.stagger(40),duration:400,easing:'easeOutCubic'});
}catch(e){toast(e.message,'error');}
}
function countUp(id,target){
const el=document.getElementById(id);let cur=0;const dur=800;const step=16;const inc=target/(dur/step);
const iv=setInterval(()=>{cur+=inc;if(cur>=target){cur=target;clearInterval(iv);}el.textContent=Math.round(cur);},step);
}
async function li(){
try{
const f=document.getElementById('fs').value,v=document.getElementById('fv').value;
const q=`/support/incidents?${f?'status='+f+'&':''}${v?'severity='+v+'&':''}`;
const d=await api('GET',q);s.incidents=d||[];
const el=document.getElementById('l-all');
el.innerHTML=d?.length?d.map(x=>rRow(x)).join(''):'<div class="p-8 text-center text-slate-500">Sin incidentes</div>';
anime({targets:el.children,translateY:[10,0],opacity:[0,1],delay:anime.stagger(30),duration:300,easing:'easeOutCubic'});
}catch(e){toast(e.message,'error');}
}
async function lk(){
try{
const d=await api('GET','/support/kb');s.kb=d||[];
renderKb(d||[]);
}catch(e){toast(e.message,'error');}
}
function renderKb(list){
const el=document.getElementById('l-kb');
el.innerHTML=list.length?list.map(k=>`<div class="glass rounded-xl p-5 hover:bg-white/5 transition-colors cursor-pointer group"><div class="flex justify-between items-start mb-2"><h4 class="font-semibold text-white group-hover:text-indigo-300 transition-colors">${esc(k.title)}</h4>${k.isPublished?'<span class="text-xs px-2 py-0.5 rounded bg-emerald-500/20 text-emerald-400 border border-emerald-500/30">Publicado</span>':'<span class="text-xs px-2 py-0.5 rounded bg-slate-700 text-slate-400">Borrador</span>'}</div><div class="text-sm text-slate-400 line-clamp-3 mb-3">${esc(k.content||'')}</div><div class="flex gap-2">${(k.tags||[]).map(t=>`<span class="text-xs px-2 py-0.5 rounded bg-slate-800 text-slate-400 border border-slate-700">${esc(t)}</span>`).join('')}</div></div>`).join(''):'<div class="glass rounded-xl p-8 text-center text-slate-500 col-span-full">Sin articulos</div>';
anime({targets:el.children,scale:[0.97,1],opacity:[0,1],delay:anime.stagger(50),duration:400,easing:'easeOutCubic'});
}
async function sk(){
try{
const q=document.getElementById('kq').value;
const d=await api('GET',`/support/kb?q=${encodeURIComponent(q)}`);
renderKb(d||[]);
}catch(e){toast(e.message,'error');}
}
async function lm(){
try{
const d=await api('GET','/support/merge-queue');
const el=document.getElementById('l-mer');
el.innerHTML=(d||[]).length?d.map(m=>`<div class="glass rounded-lg p-4 flex items-center justify-between"><div><div class="font-semibold text-sm">${esc(m.title||'Fix')}</div><div class="text-xs text-slate-400 mt-1">Branch: <code class="text-indigo-300">${esc(m.branch)}</code></div></div><button onclick="approveMerge('${m.id}')" class="px-3 py-1.5 rounded bg-emerald-600 hover:bg-emerald-500 text-white text-xs font-medium transition-colors">Aprobar</button></div>`).join(''):'<div class="text-center text-slate-500 py-4">Cola vacia</div>';
}catch(e){toast(e.message,'error');}
}
async function lh(){
try{
const d=await api('GET','/support/system-health');
const db=d?.isDatabaseHealthy,pl=d?.isPipelineHealthy;
const hd=document.getElementById('hd'),hp=document.getElementById('hp');
hd.className='w-3 h-3 rounded-full '+(db?'bg-emerald-500':'bg-rose-500');
hp.className='w-3 h-3 rounded-full '+(pl?'bg-emerald-500':'bg-rose-500');
document.getElementById('td').textContent=db?'Healthy':'Unhealthy';
document.getElementById('tp').textContent=pl?'Healthy':'Unhealthy';
document.getElementById('tt').textContent=d?.testsPassedLastRun!=null?(d.testsPassedLastRun?'Passed':'Failed'):'Pendiente';
}catch(e){toast(e.message,'error');}
}
async function approveMerge(id){try{await api('POST',`/support/merge-queue/${id}/approve`);toast('Merge aprobado','success');lm();}catch(e){toast(e.message,'error');}}
function rRow(i,compact){
const sevColors={Critical:'bg-rose-500',High:'bg-amber-500',Medium:'bg-yellow-500',Low:'bg-slate-500'};
const stColors={Open:'text-amber-400',InProgress:'text-indigo-400',Resolved:'text-emerald-400',Closed:'text-slate-400'};
const sev=sevColors[i.severity]||'bg-slate-500';
const st=stColors[i.status]||'text-slate-400';
return `<div onclick="od('${i.id}')" class="p-4 hover:bg-white/5 transition-colors cursor-pointer flex items-center gap-4 ${compact?'':'border-b border-white/5'}"><div class="w-2 h-2 rounded-full ${sev} flex-shrink-0"></div><div class="flex-1 min-w-0"><div class="font-medium text-sm truncate">${esc(i.title)}</div><div class="text-xs text-slate-400 mt-0.5">${esc(i.category||'Other')} · ${timeAgo(i.createdAt)}</div></div><div class="text-xs font-medium ${st}">${i.status}</div></div>`;
}
function esc(t){const d=document.createElement('div');d.textContent=t||'';return d.innerHTML;}
function timeAgo(d){if(!d)return'';const s=Math.floor((Date.now()-new Date(d))/1000);if(s<60)return'ahora';if(s<3600)return Math.floor(s/60)+'m';if(s<86400)return Math.floor(s/3600)+'h';return Math.floor(s/86400)+'d';}
async function od(id){
try{
const d=await api('GET','/support/incidents/'+id);
const sevColors={Critical:'bg-rose-500',High:'bg-amber-500',Medium:'bg-yellow-500',Low:'bg-slate-500'};
const sev=sevColors[d.severity]||'bg-slate-500';
const stColors={Open:'text-amber-400',InProgress:'text-indigo-400',Resolved:'text-emerald-400',Closed:'text-slate-400'};
const st=stColors[d.status]||'text-slate-400';
let actions='';
if(d.status==='Open'||d.status==='InProgress'){
actions+=`<div class="flex gap-2 mt-4"><button onclick="upd('${id}','InProgress')" class="px-3 py-1.5 rounded bg-indigo-600 hover:bg-indigo-500 text-white text-xs">Marcar En Progreso</button><button onclick="upd('${id}','Resolved')" class="px-3 py-1.5 rounded bg-emerald-600 hover:bg-emerald-500 text-white text-xs">Resolver</button></div>`;
}
if(d.suggestedFixSql){
actions+=`<div class="mt-4 p-3 rounded-lg bg-slate-900 border border-slate-700"><div class="text-xs text-slate-400 mb-2">Fix sugerido (SQL)</div><pre class="text-xs text-indigo-300 overflow-x-auto">${esc(d.suggestedFixSql)}</pre><div class="flex gap-2 mt-2"><button onclick="appr('${id}')" class="px-3 py-1 rounded bg-emerald-600 hover:bg-emerald-500 text-white text-xs">Aprobar DB Fix</button><button onclick="rej('${id}')" class="px-3 py-1 rounded bg-rose-600 hover:bg-rose-500 text-white text-xs">Rechazar</button></div></div>`;
}
document.getElementById('db').innerHTML=`<div class="flex items-center gap-3 mb-4"><div class="w-3 h-3 rounded-full ${sev}"></div><div class="text-2xl font-bold">${esc(d.title)}</div></div><div class="text-sm text-slate-400 mb-1">Estado: <span class="${st} font-medium">${d.status}</span></div><div class="text-sm text-slate-400 mb-4">Creado: ${new Date(d.createdAt).toLocaleString()}</div><div class="glass rounded-lg p-4 mb-4"><div class="text-xs text-slate-400 uppercase tracking-wider mb-2">Descripcion</div><div class="text-sm leading-relaxed whitespace-pre-wrap">${esc(d.description)}</div></div>${d.resolutionNotes?`<div class="glass rounded-lg p-4 mb-4"><div class="text-xs text-slate-400 uppercase tracking-wider mb-2">Notas de resolucion</div><div class="text-sm whitespace-pre-wrap">${esc(d.resolutionNotes)}</div></div>`:''}${d.reasoning?`<div class="glass rounded-lg p-4 mb-4 border-l-2 border-indigo-500"><div class="text-xs text-indigo-400 uppercase tracking-wider mb-2">Analisis IA</div><div class="text-sm text-slate-300 italic">${esc(d.reasoning)}</div></div>`:''}${d.gitBranch?`<div class="text-xs text-slate-500">Branch: <code class="text-indigo-300">${esc(d.gitBranch)}</code> · Commit: <code class="text-slate-300">${esc((d.gitCommitHash||'').substring(0,8))}</code></div>`:''}${actions}`;
document.getElementById('dr').classList.add('open');
anime({targets:'#db > *',translateX:[30,0],opacity:[0,1],delay:anime.stagger(60),duration:400,easing:'easeOutCubic'});
}catch(e){toast(e.message,'error');}
}
async function upd(id,st){try{await api('PUT','/support/incidents/'+id,{status:st});toast('Estado actualizado','success');cd();li();ld();}catch(e){toast(e.message,'error');}}
async function appr(id){try{await api('POST','/support/incidents/'+id+'/approve-db-fix');toast('Fix aprobado','success');cd();li();}catch(e){toast(e.message,'error');}}
async function rej(id){try{await api('POST','/support/incidents/'+id+'/reject-fix');toast('Fix rechazado','success');cd();li();}catch(e){toast(e.message,'error');}}
function cd(){document.getElementById('dr').classList.remove('open');}
function om(id){document.getElementById(id).classList.add('open');anime({targets:'#'+id+' .modal-content',scale:[0.9,1],opacity:[0,1],duration:300,easing:'easeOutCubic'});}
function cm(id){document.getElementById(id).classList.remove('open');}
function handleRipple(e){const b=e.currentTarget;const r=document.createElement('span');r.className='absolute inset-0 bg-white/10 rounded-lg';b.appendChild(r);anime({targets:r,scale:[1,1.05],opacity:[0.3,0],duration:400,easing:'easeOutCubic',complete:()=>r.remove()});}
document.getElementById('fc').addEventListener('submit',async e=>{
e.preventDefault();
try{
const d=await api('POST','/support/incidents',{title:document.getElementById('ct').value,description:document.getElementById('cd').value,severity:document.getElementById('cv').value,category:document.getElementById('cc').value});
cm('mc');toast('Incidente creado','success');document.getElementById('fc').reset();li();ld();od(d.id);
}catch(x){toast(x.message,'error');}
});
document.getElementById('fk').addEventListener('submit',async e=>{
e.preventDefault();
try{
await api('POST','/support/kb',{title:document.getElementById('kt').value,category:document.getElementById('kc').value,tags:document.getElementById('ktg').value.split(',').map(t=>t.trim()).filter(Boolean),content:document.getElementById('kb').value,resolutionSteps:document.getElementById('ks').value,isPublished:document.getElementById('kp').checked});
cm('mk');toast('Articulo guardado','success');document.getElementById('fk').reset();lk();
}catch(x){toast(x.message,'error');}
});
