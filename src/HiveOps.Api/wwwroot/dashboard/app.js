const API='/api';
// Minimal shim for anime.js to avoid runtime errors if CDN fails to load
try{
 if(typeof window!=='undefined' && typeof window.anime==='undefined'){
   const noop=()=>{};
   window.anime=Object.assign(function(){return null;}, { stagger: () => noop });
 }
}catch{}
const s={token:localStorage.getItem('token'),user:JSON.parse(localStorage.getItem('user')||'null'),isAdmin:false,incidents:[],tenantId:localStorage.getItem('tenantId')||null,tenants:[],deploymentMode:null};
if(s.token&&s.user){showApp();}else{showLogin();}
async function lb(){
try{
 if(!s.isAdmin){return;}
 const el=document.getElementById('v-adm');
 let card=document.getElementById('billing-card');
 if(!card){
   const wrap=document.createElement('div');
   wrap.className='hive-card p-6';
   wrap.id='billing-card';
   wrap.innerHTML=`<h3 class="font-semibold text-lg mb-4">Billing & Suscripciones</h3>
   <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
     <div class="col-span-1">
       <label class="text-sm block mb-2" style="color:var(--text-secondary)">Tenant</label>
       <select id="bill-tenant" class="input-field w-full px-3 py-2 rounded-xl text-sm"></select>
     </div>
     <div class="col-span-1">
       <label class="text-sm block mb-2" style="color:var(--text-secondary)">Price Id</label>
       <input id="bill-price" class="input-field w-full px-3 py-2 rounded-xl text-sm" placeholder="price_..."/>
     </div>
     <div class="col-span-1 flex items-end">
       <button id="bill-ensure" class="btn-primary px-5 py-2.5 rounded-xl text-sm">Activar Suscripción</button>
     </div>
   </div>
   <div id="bill-status" class="mt-4 text-sm" style="color:var(--text-secondary)"></div>`;
   el.appendChild(wrap);
 }
 const sel=document.getElementById('bill-tenant');
 sel.innerHTML='';
 const ts=await api('GET','/admin/tenants');
 ts.forEach(t=>{
   const o=document.createElement('option');
   o.value=t.id; o.textContent=`${t.name} ${t.subscriptionStatus?('· '+t.subscriptionStatus):''}`;
   sel.appendChild(o);
 });
 if(s.tenantId) sel.value=s.tenantId;
 document.getElementById('bill-ensure').onclick=async ()=>{
   const tid=sel.value; const pid=(document.getElementById('bill-price').value||'').trim();
   if(!tid){toast('Selecciona un tenant','error');return;}
   if(!pid){toast('Ingresa un PriceId','error');return;}
   try{await api('POST',`/admin/tenants/${tid}/billing/ensure-subscription`,{priceId:pid}); toast('Suscripción activada','success'); lb();}
   catch(e){toast(e.message,'error');}
 };
 const st=document.getElementById('bill-status');
 const cur=ts.find(x=>x.id===sel.value);
 st.textContent=cur?`Estado actual: ${cur.subscriptionStatus||'—'} · Price: ${cur.stripePriceId||'—'}`:'Selecciona un tenant';
 sel.onchange=()=>{ const c=ts.find(x=>x.id===sel.value); st.textContent=c?`Estado actual: ${c.subscriptionStatus||'—'} · Price: ${c.stripePriceId||'—'}`:'Selecciona un tenant'; };
 const lt=document.getElementById('l-tenants');
 if(lt){
   lt.innerHTML=ts.length?ts.map(t=>{
     const active=t.id===s.tenantId;
     return `<div class="flex items-center justify-between gap-3 p-3 rounded-xl border ${active?'border-cyan-500/40 bg-cyan-500/5':''}" style="border-color:var(--border-color)">
       <div class="min-w-0">
         <div class="text-sm font-semibold truncate" style="color:var(--text-primary)">${esc(t.name||'(sin nombre)')}</div>
         <div class="text-xs" style="color:var(--text-tertiary)">${esc(t.subscriptionStatus||'sin suscripción')}${t.hasRollbackCapability===false?' · sin rollback':''}</div>
       </div>
       <div class="flex items-center gap-2 flex-shrink-0">
         ${active?'<span class="text-[10px] px-2 py-0.5 rounded-full bg-cyan-500/15 text-cyan-300 border border-cyan-500/30 font-semibold uppercase tracking-wider">Activo</span>':''}
         <button type="button" onclick="actAsTenant('${t.id}')" class="btn-secondary px-3 py-1.5 rounded-lg text-xs">${active?'Viendo':'Actuar como'}</button>
       </div>
     </div>`;
   }).join(''):'<div class="text-center py-6 text-sm" style="color:var(--text-secondary)">Sin tenants registrados</div>';
 }
const billCard=document.getElementById('billing-card');
if(billCard) billCard.style.display=(s.deploymentMode==null||s.deploymentMode==='SaaS')?'':'none';
 let dbCard=document.getElementById('selfhosted-db-card');
 if(s.deploymentMode==='SelfHosted'){
   if(!dbCard){
     dbCard=document.createElement('div');
     dbCard.id='selfhosted-db-card';
     dbCard.className='hive-card p-6';
     dbCard.innerHTML=`<h3 class="font-semibold text-lg mb-4">Base de datos dedicada (self-hosted)</h3>
     <p class="text-sm mb-4" style="color:var(--text-secondary)">Asigná la cadena SQL para este tenant (queda cifrada en el servidor). Dejá vacío para borrar y usar la base catálogo.</p>
     <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
       <div class="col-span-1"><label class="text-sm block mb-2" style="color:var(--text-secondary)">Tenant</label>
       <select id="shdb-tenant" class="input-field w-full px-3 py-2 rounded-xl text-sm"></select></div>
       <div class="col-span-2"><label class="text-sm block mb-2" style="color:var(--text-secondary)">Connection string</label>
       <input id="shdb-conn" type="password" autocomplete="off" class="input-field w-full px-3 py-2 rounded-xl text-sm" placeholder="Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True"/></div>
     </div>
     <div class="flex flex-wrap gap-3 mt-4">
       <button id="shdb-save" type="button" class="btn-primary px-5 py-2.5 rounded-xl text-sm">Guardar cadena</button>
       <button id="shdb-clear" type="button" class="btn-secondary px-5 py-2.5 rounded-xl text-sm">Quitar (usar catálogo)</button>
     </div>
     <div id="shdb-status" class="mt-3 text-sm" style="color:var(--text-secondary)"></div>`;
     el.appendChild(dbCard);
     document.getElementById('shdb-save').onclick=async ()=>{
       const tid=(document.getElementById('shdb-tenant')||{}).value;
       const cs=(document.getElementById('shdb-conn')||{}).value||'';
       if(!tid){toast('Selecciona un tenant','error');return;}
       try{
         await fetch(`${API}/admin/tenants/${tid}/dedicated-database`,{method:'PUT',headers:{'Content-Type':'application/json','Authorization':'Bearer '+(s.token||'')},credentials:'include',body:JSON.stringify({connectionString:cs})});
         document.getElementById('shdb-conn').value='';
         toast(cs?'Cadena guardada':'Cadena eliminada','success'); shdbRefresh();
       }catch(e){toast(e.message||'Error','error');}
     };
     document.getElementById('shdb-clear').onclick=async ()=>{
       const tid=(document.getElementById('shdb-tenant')||{}).value;
       if(!tid){toast('Selecciona un tenant','error');return;}
       try{
         await fetch(`${API}/admin/tenants/${tid}/dedicated-database`,{method:'PUT',headers:{'Content-Type':'application/json','Authorization':'Bearer '+(s.token||'')},credentials:'include',body:JSON.stringify({connectionString:null})});
         toast('Cadena eliminada','success'); shdbRefresh();
       }catch(e){toast(e.message||'Error','error');}
     };
   }
   const shSel=document.getElementById('shdb-tenant');
   if(shSel){
     shSel.innerHTML='';
     ts.forEach(t=>{const o=document.createElement('option');o.value=t.id;o.textContent=t.name;shSel.appendChild(o);});
     if(s.tenantId) shSel.value=s.tenantId;
     shSel.onchange=()=>shdbRefresh();
   }
   shdbRefresh();
 } else if(dbCard){ dbCard.remove(); }
}catch(e){console.warn('billing ui error',e);}
}
async function shdbRefresh(){
try{
 const tid=(document.getElementById('shdb-tenant')||{}).value;
 const st=document.getElementById('shdb-status');
 if(!st||!tid) return;
 const r=await fetch(`${API}/admin/tenants/${tid}/dedicated-database`,{headers:{'Authorization':'Bearer '+(s.token||''),'Content-Type':'application/json'},credentials:'include'});
 if(!r.ok){ st.textContent='No se pudo leer el estado'; return; }
 const j=await r.json();
 st.textContent=j&&j.configured?'Hay cadena dedicada configurada para este tenant.':'Sin cadena dedicada (usa catálogo).';
}catch{}
}
document.getElementById('login-form').addEventListener('submit',async e=>{
e.preventDefault();
const el=document.getElementById('le');el.classList.add('hidden');
try{
const r=await fetch(`${API}/auth/login`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({username:document.getElementById('lu').value,password:document.getElementById('lp').value})});
if(!r.ok)throw new Error('Credenciales invalidas');
const d=await r.json();
s.token=d.token;
s.user={username:d.displayName||d.username||'',role:d.role,tenantName:d.tenantName||null};
s.isAdmin=d.role==='SuperAdmin'||d.role==='Admin';
if(d.tenantId){s.tenantId=d.tenantId;localStorage.setItem('tenantId',s.tenantId);} 
localStorage.setItem('token',s.token);localStorage.setItem('user',JSON.stringify(s.user));
showApp();
}catch(x){el.textContent=x.message;el.classList.remove('hidden');}
});
let hubConn=null;
async function initRealtime(){
  try{
    if(typeof signalR==='undefined')return;
    if(hubConn){try{await hubConn.stop();}catch{}}
    hubConn=new signalR.HubConnectionBuilder().withUrl('/hubs/supervision').withAutomaticReconnect().build();
    hubConn.on('DeploymentUpdated',p=>{
      const msg=`Deploy ${p.status}${p.progress?` (${p.progress}%)`:''}${p.eta?` · ETA ${p.eta}s`:''}`;
      toast(msg, p.status==='SUCCESS'?'success':(p.status==='FAILED'?'error':'info'));
      try{li();ld();}catch{}
    });
    hubConn.on('LlmRetryUpdated', async p=>{
      try{
        // Refresh only the badge for this incident
        const token=s.token||''; const hdr={'Authorization':'Bearer '+token,'Content-Type':'application/json'}; if(s.tenantId) hdr['X-Tenant-Id']=s.tenantId;
        const r=await fetch(`/api/support/incidents/${encodeURIComponent(p.incidentId)}/messages`,{headers:hdr,credentials:'include'});
        if(!r.ok) return;
        const msgs=await r.json(); const n=computeLlmRetries(msgs||[]);
        const slot=document.getElementById('rt-'+p.incidentId);
        if(slot){ slot.innerHTML = n>0?renderRetryBadge(n):''; slot.classList.add('status-pulse'); setTimeout(()=>slot.classList.remove('status-pulse'),1200); }
      }catch{}
    });
    await hubConn.start();
    if(s.tenantId){await hubConn.invoke('JoinTenantGroup', s.tenantId);}    
  }catch(e){console.warn('Realtime init failed',e)}
}
function showLogin(){document.getElementById('login-screen').classList.remove('hidden');document.getElementById('app').classList.add('hidden');}
async function loadDeploymentInfo(){
try{const d=await api('GET','/deployment/info');s.deploymentMode=d&&d.mode?d.mode:null;}catch{s.deploymentMode=null;}
}
async function showApp(){
try{
document.getElementById('login-screen').classList.add('hidden');
document.getElementById('app').classList.remove('hidden');
s.isAdmin=s.user.role==='SuperAdmin'||s.user.role==='Admin';
await loadDeploymentInfo();
if(s.isAdmin){const adm=document.getElementById('n-adm');if(adm)adm.classList.remove('hidden');}
else {const adm=document.getElementById('n-adm');if(adm)adm.classList.add('hidden');}
renderIdentityBadge();
setTimeout(()=>{loadTenants();n('dashboard');ld();initRealtime();},100);
}catch(e){console.error('Error in showApp:',e);showLogin();}
}
function renderIdentityBadge(){
const badge=document.getElementById('role-badge');if(!badge)return;
const sel=document.getElementById('tenant-select');
const scope=document.getElementById('scope-label');
const amber='px-2.5 py-1 rounded-full text-[10px] font-semibold uppercase tracking-widest border border-amber-500/40 bg-amber-500/15 text-amber-300';
const cyan='px-2.5 py-1 rounded-full text-[10px] font-semibold uppercase tracking-widest border border-cyan-500/40 bg-cyan-500/15 text-cyan-300';
const udName=document.getElementById('ud-name'),udRole=document.getElementById('ud-role');
if(udName)udName.textContent=(s.user&&s.user.username)||'Usuario';
if(s.user&&s.user.role==='SuperAdmin'){
badge.textContent='SUPER USUARIO';badge.className=amber;
if(udRole)udRole.textContent='Super usuario · plataforma';
if(scope)scope.textContent='Plataforma · todos los tenants';
}else if(s.user&&s.user.role==='Admin'){
badge.textContent='ADMIN';badge.className=amber;
if(udRole)udRole.textContent='Admin · multi-tenant';
if(scope)scope.textContent='Administración multi-tenant';
}else{
badge.textContent='TENANT';badge.className=cyan;
if(udRole)udRole.textContent='Administrador del tenant';
if(sel)sel.classList.add('hidden');
updateScopeLabel();
}
}
function updateScopeLabel(){
const scope=document.getElementById('scope-label');if(!scope)return;
if(s.isAdmin){
const t=(s.tenants||[]).find(x=>x.id===s.tenantId);
scope.textContent=s.tenantId?('Actuando como: '+(t?t.name:s.tenantId)):'Sin tenant seleccionado';
}else{
scope.textContent=s.user&&s.user.tenantName?('Tenant: '+s.user.tenantName):'';
}
}
function logout(){localStorage.clear();s.token=null;s.user=null;showLogin();}
async function api(m,p,b){
const o={method:m,headers:{'Content-Type':'application/json','Authorization':'Bearer '+(s.token||'')},credentials:'include'};
if(s.tenantId)o.headers['X-Tenant-Id']=s.tenantId;
if(b)o.body=JSON.stringify(b);
const r=await fetch(API+p,o);
if(r.status===401){logout();throw new Error('Sesion expirada');}
if(!r.ok){const e=await r.text();throw new Error(e);}
return r.status===204?null:await r.json();
}
function toast(msg,t='info'){
const el=document.getElementById('toast'),ic=document.getElementById('ti'),tx=document.getElementById('tm');
tx.textContent=msg;
ic.className='w-6 h-6 rounded-full flex items-center justify-center text-sm '+(t==='success'?'bg-emerald-500/20 text-emerald-400':t==='error'?'bg-red-500/20 text-red-400':'bg-cyan-500/20 text-cyan-400');
ic.innerHTML=t==='success'?'<svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M5 13l4 4L19 7"/></svg>':t==='error'?'<svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"/></svg>':'<svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"/></svg>';
el.classList.add('show');
setTimeout(()=>el.classList.remove('show'),4000);
}
function showToast(msg,t){toast(msg,t);}
function n(v){
document.querySelectorAll('.view-section').forEach(el=>{el.classList.remove('active');el.classList.add('hidden');el.style.opacity='0';});
document.querySelectorAll('.nav-item').forEach(el=>{el.classList.remove('active');});
const viewMap={dashboard:'dash',incidents:'inc',kb:'kb',playbooks:'playbooks',config:'cfg',admin:'adm'};
const navMap={dashboard:'dash',incidents:'inc',kb:'kb',playbooks:'pb',config:'cfg',admin:'adm'};
const viewId=viewMap[v]||v;
const navId=navMap[v]||viewId;
const t=document.getElementById('v-'+viewId)||document.getElementById('v-'+v);
if(t){t.classList.remove('hidden');t.classList.add('active');
anime({targets:t,opacity:[0,1],translateY:[16,0],duration:450,easing:'easeOutCubic'});}
else{console.warn('Dashboard view not found for',v,viewId);}
const nb=document.getElementById('n-'+navId)||document.getElementById('n-'+viewId)||document.getElementById('n-'+v);
if(nb){nb.classList.add('active');}
if(v==='dashboard')ld();if(v==='incidents')li();if(v==='kb')lk();if(v==='playbooks')loadPlaybooks();if(v==='config')lc();if(v==='admin'){lm();lh();lb();}
}
async function ld(){
try{
// Admins pueden ver summary global sin tenant seleccionado
if(!s.tenantId&&!s.isAdmin){
document.getElementById('c-open').textContent='0';
document.getElementById('c-crit').textContent='0';
document.getElementById('c-res').textContent='0';
document.getElementById('c-avg').textContent='0';
document.getElementById('l-rec').innerHTML='<div class="p-8 text-center" style="color:var(--text-secondary)">Selecciona un tenant</div>';
return;
}
const d=await api('GET','/support/summary');
anime({targets:['#c-open','#c-crit','#c-res','#c-avg'],translateY:[20,0],opacity:[0,1],delay:anime.stagger(80),duration:600,easing:'easeOutExpo'});
countUp('c-open',d.openIncidents??d.totalOpen??0);
countUp('c-crit',d.criticalOpen??d.totalCritical??0);
countUp('c-res',d.resolvedThisMonth??d.totalResolved??0);
countUp('c-avg',d.avgResolutionMinutes??d.averageResolutionMinutes??0);
const rec=await api('GET','/support/incidents?status=&severity=&category=&pageSize=5');
const lr=document.getElementById('l-rec');
lr.innerHTML=(rec||[]).length?rec.map(x=>rRow(x,true)).join(''):'<div class="p-8 text-center" style="color:var(--text-secondary)"><svg class="w-12 h-12 mx-auto mb-3 opacity-30" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.5" d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2"/></svg>Sin incidentes recientes</div>';
anime({targets:lr.children,translateX:[-20,0],opacity:[0,1],delay:anime.stagger(40),duration:400,easing:'easeOutCubic'});
loadReadinessAndOutcomes();
}catch(e){toast(e.message,'error');}
}
function countUp(id,target){
const el=document.getElementById(id);let cur=0;const dur=800;const step=16;const inc=target/(dur/step);
const iv=setInterval(()=>{cur+=inc;if(cur>=target){cur=target;clearInterval(iv);}el.textContent=Math.round(cur);},step);
}
async function li(search){
try{
// Admins pueden ver incidentes globales sin tenant seleccionado
if(!s.tenantId&&!s.isAdmin){document.getElementById('l-all').innerHTML='<div class="p-10 text-center" style="color:var(--text-secondary)">Selecciona un tenant</div>';return;}
const f=document.getElementById('fs').value,v=document.getElementById('fv').value;
const q=`/support/incidents?${f?'status='+f+'&':''}${v?'severity='+v+'&':''}${search?'q='+encodeURIComponent(search)+'&':''}`;
const d=await api('GET',q);s.incidents=d||[];
const el=document.getElementById('l-all');
el.innerHTML=d?.length?d.map(x=>rRow(x)).join(''):'<div class="p-10 text-center" style="color:var(--text-secondary)"><svg class="w-12 h-12 mx-auto mb-3 opacity-30" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.5" d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2"/></svg>No hay incidentes</div>';
anime({targets:el.children,translateY:[10,0],opacity:[0,1],delay:anime.stagger(30),duration:300,easing:'easeOutCubic'});

 // Compute LLM retry badges by reading messages per-incident (non-blocking)
 try{
   const token=s.token||''; const hdr={'Authorization':'Bearer '+token,'Content-Type':'application/json'}; if(s.tenantId) hdr['X-Tenant-Id']=s.tenantId;
   (s.incidents||[]).forEach(async it=>{
     try{
       const r=await fetch(`/api/support/incidents/${encodeURIComponent(it.id)}/messages`,{headers:hdr,credentials:'include'});
       if(!r.ok) return;
       const msgs=await r.json();
       const n=computeLlmRetries(msgs||[]);
       const slot=document.getElementById('rt-'+it.id);
       if(slot && n>0){ slot.innerHTML=renderRetryBadge(n); }
     }catch{}
   });
 }catch{}
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
el.innerHTML=list.length?list.map(k=>`<div class="hive-card p-5 cursor-pointer group"><div class="flex justify-between items-start mb-3"><h4 class="font-semibold group-hover:text-cyan-400 transition-colors" style="color:var(--text-primary)">${esc(k.title)}</h4>${k.isPublished?'<span class="text-xs px-2.5 py-1 rounded-full bg-emerald-500/15 text-emerald-400 border border-emerald-500/20">Publicado</span>':'<span class="text-xs px-2.5 py-1 rounded-full bg-stone-700/60 text-stone-400">Borrador</span>'}</div><div class="text-sm line-clamp-3 mb-4" style="color:var(--text-secondary)">${esc(k.content||'')}</div><div class="flex flex-wrap gap-2">${(k.tags||[]).map(t=>`<span class="text-xs px-2 py-1 rounded-lg bg-cyan-500/10 text-cyan-400 border border-cyan-500/20">${esc(t)}</span>`).join('')}</div></div>`).join(''):'<div class="hive-card p-8 text-center col-span-full" style="color:var(--text-secondary)">Sin articulos</div>';
anime({targets:el.children,scale:[0.95,1],opacity:[0,1],delay:anime.stagger(60),duration:500,easing:'easeOutCubic'});
}
async function sk(){
try{
const q=document.getElementById('kq').value;
const d=await api('GET',`/support/kb?q=${encodeURIComponent(q)}`);
renderKb(d||[]);
}catch(e){toast(e.message,'error');}
}
document.getElementById('fk')?.addEventListener('submit',async e=>{
e.preventDefault();
try{
const d=await api('POST','/support/kb',{title:document.getElementById('kt').value,category:document.getElementById('kc').value,tags:(document.getElementById('ktg').value||'').split(',').filter(x=>x.trim()),content:document.getElementById('kb').value,resolutionSteps:document.getElementById('ks').value,isPublished:document.getElementById('kp').checked});
cm('mk');toast('Articulo KB creado','success');document.getElementById('fk').reset();lk();
}catch(x){toast(x.message,'error');}
});
async function lm(){
try{
const d=await api('GET','/support/merge-queue');
const el=document.getElementById('l-mer');
el.innerHTML=(d||[]).length?d.map(m=>`<div class="hive-card p-4 flex items-center justify-between"><div><div class="font-semibold text-sm" style="color:var(--text-primary)">${esc(m.title||'Fix')}</div><div class="text-xs mt-1" style="color:var(--text-tertiary)">Branch: <code class="text-cyan-400 font-mono">${esc(m.branch)}</code></div></div><button onclick="approveMerge('${m.id}')" class="px-4 py-2 rounded-lg bg-emerald-600 hover:bg-emerald-500 text-white text-xs font-medium transition-all">Aprobar</button></div>`).join(''):`<div class="text-center py-6" style="color:var(--text-secondary)">Cola vacia</div>`;
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
const sevColors={Critical:'bg-red-500',High:'bg-amber-500',Medium:'bg-yellow-500',Low:'bg-neutral-500'};
const stColors={Open:'text-cyan-400',InProgress:'text-cyan-300',Resolved:'text-emerald-500',Closed:'text-neutral-400'};
const sev=sevColors[i.severity]||'bg-neutral-500';
const st=stColors[i.status]||'text-neutral-400';
// Derive deploy info from API if provided (server may add DeployStatus/LastCiResult soon)
let dStatus=i.deployStatus||''; let badgeClass='bg-neutral-500'; let logUrl=null;
if(i.lastCiResult){ try{ const o=JSON.parse(i.lastCiResult||'{}'); if(!dStatus && (o.status||o.action)) dStatus=((o.action?o.action+'_':'')+(o.status||'')).trim(); logUrl=o.logUrl||null; }catch{} }
if(/DEPLOYING/i.test(dStatus)) badgeClass='bg-blue-500';
else if(/SUCCESS/i.test(dStatus)) badgeClass='bg-emerald-500';
else if(/FAILED|ROLLBACK/i.test(dStatus)) badgeClass='bg-red-500';
let badgeHtml='';
if(dStatus){ const inner=`<span class="ml-2 px-1.5 py-0.5 rounded text-[10px] ${badgeClass} text-white align-middle">${esc(dStatus)}</span>`; badgeHtml=(s.isAdmin&&logUrl)?`<a href="${esc(logUrl)}" class="hover:opacity-90" target="_blank" rel="noopener noreferrer" title="Ver logs de despliegue">${inner}</a>`:inner; }
const deployBadge=badgeHtml;
return `<div onclick="od('${i.id}')" class="p-4 hover:bg-cyan-500/5 transition-all cursor-pointer flex items-center gap-4 group ${compact?'':'border-b'}" style="border-color:var(--border-color)"><div class="w-2 h-2 rounded-full ${sev} flex-shrink-0 status-pulse"></div><div class="flex-1 min-w-0"><div class="font-medium text-sm truncate flex items-center gap-2" style="color:var(--text-primary)"><span>${esc(i.title)} ${deployBadge}</span><span id="rt-${i.id}" class="inline-block"></span></div><div class="text-xs mt-0.5" style="color:var(--text-tertiary)">${esc(i.category||'Other')} · ${timeAgo(i.createdAt)}</div></div><div class="text-xs font-medium ${st}">${i.status}</div></div>`;
}
function filterIncidents(filter){
if(filter==='Open'||filter==='InProgress'||filter==='Resolved'||filter==='Closed'){
document.getElementById('fs').value=filter;
}
if(filter==='Critical'||filter==='High'||filter==='Medium'||filter==='Low'){
document.getElementById('fv').value=filter;
}
n('incidents');
li();
}
function searchIncidents(){
const q=document.getElementById('global-search').value.trim();
if(!q){li();return;}
n('incidents');
li(q);
}
function esc(t){const d=document.createElement('div');d.textContent=t||'';return d.innerHTML;}
function timeAgo(d){if(!d)return'';const s=Math.floor((Date.now()-new Date(d))/1000);if(s<60)return'ahora';if(s<3600)return Math.floor(s/60)+'m';if(s<86400)return Math.floor(s/3600)+'h';return Math.floor(s/86400)+'d';}
function computeLlmRetries(messages){
  let maxRetry=0; const re=/Reintentando \((\d)\/3\)/g; const re2=/\((\d)\/3\)/g;
  for(const m of messages){
    const txt=((m.content||'')+'');
    let mt; while((mt=re.exec(txt))!==null){ const n=parseInt(mt[1]); if(n>maxRetry) maxRetry=n; }
    if(maxRetry===0 && /Fix rechazado:/.test(txt)){
      const m2=txt.match(re2); if(m2){ const n=parseInt((m2[0]||'').replace(/[^0-9]/g,'')); if(n>maxRetry) maxRetry=n; }
    }
  }
  return isFinite(maxRetry)?maxRetry:0;
}
function renderRetryBadge(n){
  const cls = n>=3? 'bg-red-600 text-white' : 'bg-amber-500 text-white';
  return `<span class="px-1.5 py-0.5 rounded text-[10px] ${cls}">Reintentos LLM: ${n}/3</span>`;
}
function renderMessages(msgs){
  const el=document.getElementById('ml');
  if(!el) return;
  if(!msgs.length){ el.innerHTML='<div class="text-sm" style="color:var(--text-secondary)">Sin mensajes</div>'; return; }
  const isAutoFix=(c)=>/(Reintentando \(|Fix rechazado:|Iniciando diagnóstico|Backup creado:|SQL ejecutado exitosamente|Rollback ejecutado)/i.test(c||'');
  el.innerHTML=msgs.map(m=>{
    const auto=isAutoFix(m.content||'');
    const pill= auto? '<span class="ml-2 text-[10px] px-1.5 py-0.5 rounded bg-neutral-700 text-neutral-300 border border-white/10">Auto-Fix</span>':'';
    const boxCls= auto? 'border-l-2 border-cyan-500/60 bg-white/5' : '';
    return `<div class="p-3 rounded-lg mb-2 ${boxCls}"><div class="text-xs mb-1" style="color:var(--text-tertiary)">${esc(m.role)} · ${new Date(m.createdAt).toLocaleString()} ${pill}</div><div class="text-sm" style="color:var(--text-secondary)">${esc(m.content||'')}</div></div>`;
  }).join('');
}
async function od(id){
try{
const d=await api('GET','/support/incidents/'+id);
const sevColors={Critical:'bg-red-500',High:'bg-amber-500',Medium:'bg-yellow-500',Low:'bg-neutral-500'};
const sev=sevColors[d.severity]||'bg-neutral-500';
const stColors={Open:'text-cyan-400',InProgress:'text-cyan-300',Resolved:'text-emerald-500',Closed:'text-neutral-400'};
const st=stColors[d.status]||'text-neutral-400';
let actions='';
if(d.status==='Open'||d.status==='InProgress'){
actions+=`<div class="flex gap-3 mt-5"><button onclick="upd('${id}','InProgress')" class="btn-primary px-4 py-2 rounded-lg text-xs flex-1">Marcar En Progreso</button><button onclick="upd('${id}','Resolved')" class="btn-secondary px-4 py-2 rounded-lg text-xs flex-1" style="background:var(--bg-tertiary);color:#10b981;border-color:#10b981">Resolver</button></div>`;
}
if(d.suggestedFixSql){
actions+=`<div class="mt-5 p-4 rounded-xl hive-card" style="background:rgba(34,211,238,0.05)"><div class="text-xs mb-2" style="color:var(--text-tertiary)">Fix sugerido (SQL)</div><pre class="text-xs text-cyan-300 overflow-x-auto font-mono">${esc(d.suggestedFixSql)}</pre><div class="flex gap-2 mt-3"><button onclick="appr('${id}')" class="px-3 py-1.5 rounded-lg bg-emerald-600 hover:bg-emerald-500 text-white text-xs">Aprobar</button><button onclick="rej('${id}')" class="px-3 py-1.5 rounded-lg bg-red-600 hover:bg-red-500 text-white text-xs">Rechazar</button></div></div>`;
}
const rcaPanelHtml = renderRootCausePanel(d);
const evidencePackHtml = `${rcaPanelHtml}<div id="inc-snapshots-panel" class="hive-card p-4 mt-4"><div class="text-xs uppercase tracking-wider mb-2" style="color:var(--text-tertiary)">Snapshots de Datos (Before / After Diff &amp; 1-Click Undo)</div><div class="text-xs" style="color:var(--text-secondary)">Cargando snapshots...</div></div><div id="inc-audit-panel" class="hive-card p-4 mt-4"><div class="text-xs uppercase tracking-wider mb-2" style="color:var(--text-tertiary)">Cadena de Auditoría Criptográfica (SHA-256)</div><div class="text-xs" style="color:var(--text-secondary)">Verificando bloques SHA-256...</div></div>`;
document.getElementById('db').innerHTML=`<div class="flex items-center gap-3 mb-5"><div class="w-3 h-3 rounded-full ${sev} status-pulse"></div><div class="text-2xl font-bold" style="color:var(--text-primary)">${esc(d.title)}</div></div><div class="text-sm mb-1" style="color:var(--text-secondary)">Estado: <span class="${st} font-medium">${d.status}</span></div><div class="text-sm mb-5" style="color:var(--text-tertiary)">Creado: ${new Date(d.createdAt).toLocaleString()}</div><div class="hive-card p-4 mb-4"><div class="text-xs uppercase tracking-wider mb-2" style="color:var(--text-tertiary)">Descripcion</div><div class="text-sm leading-relaxed whitespace-pre-wrap" style="color:var(--text-secondary)">${esc(d.description)}</div></div>${d.resolutionNotes?`<div class=\"hive-card p-4 mb-4\"><div class=\"text-xs uppercase tracking-wider mb-2\" style=\"color:var(--text-tertiary)\">Notas de resolucion</div><div class=\"text-sm whitespace-pre-wrap\" style=\"color:var(--text-secondary)\">${esc(d.resolutionNotes)}</div></div>`:''}${d.reasoning?`<div class=\"hive-card p-4 mb-4 border-l-2 border-cyan-500\" style=\"background:rgba(34,211,238,0.03)\"><div class=\"text-xs text-cyan-400 uppercase tracking-wider mb-2\">Analisis IA</div><div class=\"text-sm italic\" style=\"color:var(--text-secondary)\">${esc(d.reasoning)}</div></div>`:''}${d.gitBranch?`<div class=\"text-xs\" style=\"color:var(--text-tertiary)\">Branch: <code class=\"text-cyan-400 font-mono\">${esc(d.gitBranch)}</code> · Commit: <code class=\"font-mono\" style=\"color:var(--text-secondary)\">${esc((d.gitCommitHash||'').substring(0,8))}</code></div>`:''}${actions}${evidencePackHtml}<div id=\"msgs\" class=\"hive-card p-4 mt-4\"><div class=\"text-xs uppercase tracking-wider mb-2\" style=\"color:var(--text-tertiary)\">Mensajes</div><div id=\"ml\"><div class=\"text-sm\" style=\"color:var(--text-secondary)\">Cargando mensajes...</div></div></div>`;

 // Load Snapshots & Audit Chain asynchronously
 loadIncidentSnapshots(id);
 loadIncidentAuditChain(id);

 // Load and render messages with Auto-Fix labels
 try{
   const token=s.token||''; const hdr={'Authorization':'Bearer '+token,'Content-Type':'application/json'}; if(s.tenantId) hdr['X-Tenant-Id']=s.tenantId;
   const r=await fetch(`/api/support/incidents/${encodeURIComponent(id)}/messages`,{headers:hdr,credentials:'include'});
   if(r.ok){ const msgs=await r.json(); renderMessages(msgs||[]); } else { document.getElementById('ml').innerHTML='<div class="text-sm" style="color:var(--text-secondary)">No se pudieron cargar los mensajes.</div>'; }
 }catch{ document.getElementById('ml').innerHTML='<div class="text-sm" style="color:var(--text-secondary)">No se pudieron cargar los mensajes.</div>'; }
document.getElementById('dr').classList.add('open');
anime({targets:'#db > *',translateX:[30,0],opacity:[0,1],delay:anime.stagger(60),duration:450,easing:'easeOutCubic'});
}catch(e){toast(e.message,'error');}
}
function openIncident(id){return od(id);}
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
if(!s.tenantId&&!s.isAdmin){toast('Selecciona un tenant primero','error');return;}
const d=await api('POST','/support/incidents',{title:document.getElementById('ct').value,description:document.getElementById('cd').value,severity:document.getElementById('cv').value,category:document.getElementById('cc').value});
cm('mc');toast('Incidente creado','success');document.getElementById('fc').reset();li();ld();od(d.id);
}catch(x){toast(x.message,'error');}
});
async function loadTenants(){
try{
const tenants=await api('GET','/tenants');
s.tenants=tenants||[];
const sel=document.getElementById('tenant-select');
if(sel){
sel.innerHTML='<option value="">Seleccionar Tenant...</option>'+(tenants||[]).map(t=>`<option value="${t.id}">${t.name}</option>`).join('');
if(s.tenantId&&tenants&&tenants.find(t=>t.id===s.tenantId)){sel.value=s.tenantId;changeTenant();}
}
updateScopeLabel();
}catch(e){console.error('Error loading tenants:',e);toast('Error al cargar tenants','error');}
}
function changeTenant(){
const sel=document.getElementById('tenant-select');
s.tenantId=sel.value;
updateScopeLabel();
if(s.tenantId){
localStorage.setItem('tenantId',s.tenantId);
ld();li();
 if(hubConn&&hubConn.invoke){hubConn.invoke('JoinTenantGroup', s.tenantId).catch(()=>{});} 
}else{
localStorage.removeItem('tenantId');
}
}
function actAsTenant(tid){
if(!tid)return;
const sel=document.getElementById('tenant-select');
if(sel)sel.value=tid;
s.tenantId=tid;
localStorage.setItem('tenantId',tid);
updateScopeLabel();
ld();li();
if(hubConn&&hubConn.invoke){hubConn.invoke('JoinTenantGroup',tid).catch(()=>{});}
toast('Operando como el tenant seleccionado','success');
}
async function patchTenantPath(pathRel, payload){
if(!s.tenantId){throw new Error('Sin tenant');}
const o={method:'PATCH',headers:{'Content-Type':'application/json','Authorization':'Bearer '+(s.token||'')},credentials:'include',body:JSON.stringify(payload)};
if(s.tenantId)o.headers['X-Tenant-Id']=s.tenantId;
const r=await fetch(`${API}/tenants/${s.tenantId}${pathRel}`,o);
if(r.status===401){logout();throw new Error('Sesion expirada');}
const t=await r.text();
if(!r.ok){throw new Error(t||r.statusText);}
return t?JSON.parse(t):null;}
async function lc(){
try{
if(!s.tenantId){toast('Selecciona un tenant primero','warning');return;}
const cfg=await api('GET','/tenants/'+s.tenantId+'/config');
const el=id=>document.getElementById(id);
const llmHint=el('cfg-llm-selfhosted-hint');
if(llmHint){ if(s.deploymentMode==='SelfHosted') llmHint.classList.remove('hidden'); else llmHint.classList.add('hidden'); }
el('cfg-schema-banner').textContent='Schema v'+(cfg.configurationSchemaVersion ?? '?');
const a=cfg.agent||{};
el('cfg-agent-enabled').checked=a.enabled!==false;
el('cfg-planner').checked=a.enableLlmPlanner!==false;
el('cfg-planner-version').value=a.plannerVersion||'v1';
el('cfg-tone').value=a.tone||'sales';
el('cfg-max-steps').value=a.maxSteps??3;
const L=cfg.llm||{};
el('cfg-llm-provider').value=L.provider||'openai';
el('cfg-llm-model').value=L.model||'gpt-4';
const temp=L.temperature??0.7;
el('cfg-llm-temp').value=String(temp);
el('cfg-llm-temp-val').textContent=String(temp);
el('cfg-llm-max-out').value=L.maxOutputTokens??2000;
const D=cfg.deployGit||{};
el('cfg-auto-deploy').checked=D.autoDeployEnabled!==false;
el('cfg-manual-deploy').checked=D.manualDeployAllowed!==false;
el('cfg-git-url').value=D.gitRepositoryUrl||'';
el('cfg-git-branch').value=D.gitDefaultBranch||'main';
el('cfg-git-subpath').value=D.workingDirectoryRelativePath||'';
const E=cfg.escalation||{};
el('cfg-esc-enable').checked=E.enableEscalation!==false;
el('cfg-esc-frust').checked=E.enableFrustrationEscalation!==false;
el('cfg-esc-max-failures').value=E.maxPlannerFailuresBeforeEscalation??3;
el('cfg-esc-keywords').value=(E.triggerKeywords||[]).join(', ');
el('cfg-policies-json').value=JSON.stringify(cfg.policies||[],null,2);
const w=(cfg.channel&&cfg.channel.whatsApp)||{};
el('cfg-wa-buttons').checked=w.enableInteractiveButtons!==false;
el('cfg-wa-menu').checked=w.enableInteractiveMenu!==false;
el('cfg-wa-list-long').checked=w.preferListForLongChoices!==false;
el('cfg-wa-max-qr').value=w.maxQuickReplyButtons??3;
}catch(e){toast(e.message,'error');}
}
function applyPreset(preset){
if(!s.tenantId){toast('Selecciona un tenant primero','warning');return;}
const presets={
basic:{model:'gpt-3.5-turbo',temperature:0.5,maxTokens:1600},
standard:{model:'gpt-4',temperature:0.7,maxTokens:2000},
advanced:{model:'gpt-4',temperature:0.85,maxTokens:4000}
};
const p=presets[preset];
document.getElementById('cfg-llm-model').value=p.model;
document.getElementById('cfg-llm-temp').value=String(p.temperature);
document.getElementById('cfg-llm-temp-val').textContent=String(p.temperature);
document.getElementById('cfg-llm-max-out').value=p.maxTokens;
['basic','standard','advanced'].forEach(k=>{
const el=document.getElementById('preset-'+k);
if(el){if(k===preset)el.classList.add('preset-selected');else el.classList.remove('preset-selected');}
});
saveLlmSection();
}
function toggleAdvancedConfig(){
const els=document.querySelectorAll('.cfg-adv');
const btn=document.getElementById('cfg-adv-toggle');
const hidden=[...els].some(el=>el.classList.contains('hidden'));
els.forEach(el=>el.classList.toggle('hidden',!hidden));
if(btn)btn.textContent=hidden?'Ocultar avanzado':'Mostrar avanzado (LLM · Deploy · Políticas)';
}
function buildAgentPayload(){return{enabled:document.getElementById('cfg-agent-enabled').checked,enableLlmPlanner:document.getElementById('cfg-planner').checked,plannerVersion:document.getElementById('cfg-planner-version').value||'v1',maxSteps:parseInt(document.getElementById('cfg-max-steps').value,10)||3,tone:document.getElementById('cfg-tone').value||'sales',systemPromptOverride:null,promptVariables:{},allowedTools:[]};}
function buildLlmPayload(){return{provider:document.getElementById('cfg-llm-provider').value||'openai',model:document.getElementById('cfg-llm-model').value||'gpt-4',temperature:parseFloat(document.getElementById('cfg-llm-temp').value)||0.7,topP:null,maxOutputTokens:parseInt(document.getElementById('cfg-llm-max-out').value,10)||2000};}
function buildDeployPayload(){return{autoDeployEnabled:document.getElementById('cfg-auto-deploy').checked,manualDeployAllowed:document.getElementById('cfg-manual-deploy').checked,gitRepositoryUrl:(document.getElementById('cfg-git-url').value||'').trim()||null,gitDefaultBranch:document.getElementById('cfg-git-branch').value||'main',workingDirectoryRelativePath:(document.getElementById('cfg-git-subpath').value||'').trim()||null};}
function buildEscalationPayload(){const raw=(document.getElementById('cfg-esc-keywords').value||'').split(',').map(x=>x.trim()).filter(Boolean);return{enableEscalation:document.getElementById('cfg-esc-enable').checked,enableFrustrationEscalation:document.getElementById('cfg-esc-frust').checked,maxPlannerFailuresBeforeEscalation:parseInt(document.getElementById('cfg-esc-max-failures').value,10)||3,triggerKeywords:raw};}
function buildChannelPayload(){return{whatsApp:{enableInteractiveButtons:document.getElementById('cfg-wa-buttons').checked,enableInteractiveMenu:document.getElementById('cfg-wa-menu').checked,preferListForLongChoices:document.getElementById('cfg-wa-list-long').checked,maxQuickReplyButtons:Math.min(3,Math.max(1,parseInt(document.getElementById('cfg-wa-max-qr').value,10)||3))}};}
async function saveAgentRuntime(){if(!s.tenantId)return;try{await patchTenantPath('/config/agent',buildAgentPayload());toast('Runtime del agente guardado','success');}catch(e){toast(e.message,'error');}}
async function saveLlmSection(){if(!s.tenantId)return;try{await patchTenantPath('/config/llm',buildLlmPayload());toast('LLM guardado','success');}catch(e){toast(e.message,'error');}}
async function saveDeployGitSection(){if(!s.tenantId)return;try{await patchTenantPath('/config/deploy-git',buildDeployPayload());toast('Deploy/Git guardado','success');}catch(e){toast(e.message,'error');}}
async function saveEscalationSection(){if(!s.tenantId)return;try{await patchTenantPath('/config/escalation',buildEscalationPayload());toast('Escalación guardada','success');}catch(e){toast(e.message,'error');}}
async function saveChannelSection(){if(!s.tenantId)return;try{await patchTenantPath('/config/channel',buildChannelPayload());toast('Canal guardado','success');}catch(e){toast(e.message,'error');}}
async function savePoliciesSection(){if(!s.tenantId)return;try{const txt=document.getElementById('cfg-policies-json').value.trim();let arr=[]; if(txt){arr=JSON.parse(txt); if(!Array.isArray(arr))throw new Error('Políticas: se esperaba un JSON array')} await patchTenantPath('/config/policies',arr);toast('Políticas guardadas','success');}catch(e){toast(e.message||'JSON inválido','error');}}
async function saveAllConfigSections(){
if(!s.tenantId)return;
try{
await patchTenantPath('/config/agent',buildAgentPayload());
await patchTenantPath('/config/llm',buildLlmPayload());
await patchTenantPath('/config/deploy-git',buildDeployPayload());
await patchTenantPath('/config/escalation',buildEscalationPayload());
await patchTenantPath('/config/channel',buildChannelPayload());
const txt=document.getElementById('cfg-policies-json').value.trim();
let arr=[]; if(txt){arr=JSON.parse(txt); if(!Array.isArray(arr))throw new Error('Políticas: array JSON requerido');}
await patchTenantPath('/config/policies',arr);
toast('Todas las secciones guardadas','success');
}catch(e){toast(e.message||'Error al guardar','error');}}
function resetConfig(){
lc();
toast('Datos recargados desde servidor','info');
}
try{document.getElementById('cfg-llm-temp')?.addEventListener('input',e=>{document.getElementById('cfg-llm-temp-val').textContent=e.target.value});}catch{}


let notifications=[];
function toggleNotifications(){
const el=document.getElementById('notif-dropdown');
if(el.classList.contains('hidden')){el.classList.remove('hidden');loadNotifications();}
else{el.classList.add('hidden');}
}
async function loadNotifications(){
try{
if(!s.tenantId&&!s.isAdmin){document.getElementById('notif-list').innerHTML='<div class="p-4 text-center text-sm" style="color:var(--text-secondary)">Selecciona un tenant</div>';return;}
const d=await api('GET','/support/notifications');
notifications=d||[];
const nl=document.getElementById('notif-list');
if(!notifications.length){nl.innerHTML='<div class="p-4 text-center text-sm" style="color:var(--text-secondary)">Sin notificaciones nuevas</div>';}
else{nl.innerHTML=notifications.map(n=>`<div class="p-3 hover:bg-white/5 cursor-pointer transition-colors border-b" style="border-color:var(--border-color)" onclick="${n.type==='incident'?`od('${n.incidentId}');toggleNotifications();`:`n('incidents');toggleNotifications();`}"><div class="flex items-center gap-2 mb-1"><div class="w-1.5 h-1.5 rounded-full ${n.severity==='Critical'?'bg-red-500':n.severity==='High'?'bg-amber-500':'bg-amber-500/60'}"></div><span class="text-xs font-medium" style="color:var(--text-primary)">${n.title||'Notificacion'}</span></div><div class="text-xs" style="color:var(--text-tertiary)">${timeAgo(n.createdAt)}</div></div>`).join('');}
const badge=document.getElementById('notif-badge');
if(notifications.filter(n=>!n.read).length>0){badge.classList.remove('hidden');badge.classList.add('status-pulse');}
else{badge.classList.add('hidden');badge.classList.remove('status-pulse');}
}catch(e){document.getElementById('notif-list').innerHTML='<div class="p-4 text-center text-sm" style="color:var(--text-secondary)">Error cargando notificaciones</div>';}
}
function markAllRead(){notifications.forEach(n=>n.read=true);document.getElementById('notif-badge').classList.add('hidden');document.getElementById('notif-badge').classList.remove('status-pulse');}

// ==========================================
// AI Support Engineer v1.2 — Readiness, ROI, Operating Mode, Playbooks, Evidence Pack
// ==========================================

function updateOperatingModePills(activeMode) {
  const mode = activeMode || 'Assisted';
  ['Shadow', 'Assisted', 'Autonomous'].forEach(m => {
    const btn = document.getElementById('mode-btn-' + m);
    if (!btn) return;
    if (m.toLowerCase() === mode.toLowerCase()) {
      btn.className = 'px-3 py-1.5 rounded-lg text-xs font-semibold transition-all preset-selected';
    } else {
      btn.className = 'px-3 py-1.5 rounded-lg text-xs font-medium transition-all hover:bg-cyan-500/10';
      btn.style.color = 'var(--text-secondary)';
    }
  });
}

async function setOperatingMode(mode) {
  updateOperatingModePills(mode);
  const modeDescriptions = {
    Shadow: 'Modo Shadow (POC 14d): el agente diagnostica, propone fixes y simula playbooks sin ejecutar escrituras en producción.',
    Assisted: 'Modo Assisted: el agente diagnostica con evidencia multi-fuente y solicita aprobación humana antes de aplicar cambios.',
    Autonomous: 'Modo Autonomous: auto-remediación L1/L2 habilitada bajo guardrails de filas, snapshot previo y verificación SQL.'
  };
  try {
    try {
      await api('PUT', '/support/readiness/operating-mode', { operatingMode: mode });
    } catch {
      if (s.tenantId) {
        await api('PATCH', `/tenants/${s.tenantId}/config`, { operatingMode: mode });
      } else {
        await api('PATCH', '/tenant/config', { operatingMode: mode });
      }
    }
    toast(modeDescriptions[mode] || ('Modo operativo cambiado a ' + mode), 'success');
    loadReadinessAndOutcomes();
  } catch (e) {
    toast(e.message || 'No se pudo actualizar el modo operativo', 'error');
  }
}

async function loadReadinessAndOutcomes() {
  if (!s.tenantId && !s.isAdmin) return;

  // 1. Load Outcomes & ROI
  try {
    const o = await api('GET', '/support/readiness/outcomes');
    if (o) {
      const verified = o.verifiedAutonomousResolutions ?? 0;
      const shadow = o.shadowProposedResolutions ?? 0;
      const hours = o.estimatedHoursSaved != null
        ? Number(o.estimatedHoursSaved).toFixed(1)
        : ((verified * 0.75) + (shadow * 0.5)).toFixed(1);
      const savings = o.estimatedCostSavingsUsd != null
        ? Math.round(Number(o.estimatedCostSavingsUsd))
        : Math.round((verified * 0.75) * 65);

      const elVer = document.getElementById('roi-verified');
      const elShd = document.getElementById('roi-shadow');
      const elHrs = document.getElementById('roi-hours');
      const elSav = document.getElementById('roi-savings');
      const elAud = document.getElementById('roi-audit-badge');

      if (elVer) elVer.textContent = String(verified);
      if (elShd) elShd.textContent = String(shadow);
      if (elHrs) elHrs.textContent = `${hours} h`;
      if (elSav) elSav.textContent = `$${savings.toLocaleString()}`;

      if (elAud) {
        const intact = o.auditChainIntact !== false;
        elAud.textContent = `Audit Chain SHA-256: ${intact ? 'Intacta' : 'Revisar'}`;
        elAud.className = intact
          ? 'text-xs px-2.5 py-1 rounded-full bg-emerald-500/15 text-emerald-400 border border-emerald-500/20 font-medium'
          : 'text-xs px-2.5 py-1 rounded-full bg-amber-500/15 text-amber-400 border border-amber-500/20 font-medium';
      }

      updateOperatingModePills(o.operatingMode || 'Assisted');
    }
  } catch (e) {
    console.warn('Outcomes load error:', e);
    updateOperatingModePills('Assisted');
  }

  // 2. Load Readiness Check
  try {
    const r = await api('GET', '/support/readiness/check');
    const badge = document.getElementById('rd-status-badge');
    const listEl = document.getElementById('rd-checks-list');
    if (!r || !listEl) return;

    if (badge) {
      if (r.ready) {
        badge.textContent = 'LISTO';
        badge.className = 'text-xs px-2.5 py-1 rounded-full bg-emerald-500/15 text-emerald-400 border border-emerald-500/20 font-semibold';
      } else {
        badge.textContent = 'REQUIERE CONFIG';
        badge.className = 'text-xs px-2.5 py-1 rounded-full bg-amber-500/15 text-amber-400 border border-amber-500/20 font-semibold';
      }
    }

    const labels = {
      targetDatabase: 'Target Database (SQL Introspección & Snapshots)',
      gitWorkspace: 'Git Workspace (Commits & PRs L3)',
      knowledgeBase: 'Knowledge Base (Artículos publicados)',
      playbooks: 'Playbooks L1/L2 (Remediación determinística)',
      channels: 'Canales de Notificación & Webhooks'
    };

    const checks = Array.isArray(r.checks) ? r.checks : [];
    if (!checks.length) {
      listEl.innerHTML = '<div class="text-xs py-3 text-center" style="color:var(--text-secondary)">Sin datos de verificación</div>';
    } else {
      listEl.innerHTML = checks.map(c => {
        const ok = !!c.ok;
        const icon = ok
          ? '<span class="w-5 h-5 rounded-full bg-emerald-500/20 text-emerald-400 flex items-center justify-center flex-shrink-0 text-xs font-bold">✓</span>'
          : '<span class="w-5 h-5 rounded-full bg-red-500/20 text-red-400 flex items-center justify-center flex-shrink-0 text-xs font-bold">✕</span>';
        const title = labels[c.name] || c.name;
        return `<div class="flex items-start gap-3 p-2.5 rounded-xl border" style="background:var(--bg-tertiary);border-color:var(--border-color)" title="${esc(c.detail || '')}">
          ${icon}
          <div class="min-w-0 flex-1">
            <div class="text-xs font-semibold" style="color:var(--text-primary)">${esc(title)}</div>
            <div class="text-[11px] truncate mt-0.5" style="color:var(--text-secondary)">${esc(c.detail || '')}</div>
          </div>
        </div>`;
      }).join('');
    }
  } catch (e) {
    console.warn('Readiness check error:', e);
  }
}

// ==========================================
// Playbooks L1/L2 ABM
// ==========================================

function togglePlaybookForm(show) {
  const card = document.getElementById('pb-form-card');
  if (!card) return;
  const shouldShow = typeof show === 'boolean' ? show : card.classList.contains('hidden');
  if (shouldShow) card.classList.remove('hidden');
  else card.classList.add('hidden');
}

async function loadPlaybooks() {
  const listEl = document.getElementById('l-pb');
  if (!listEl) return;
  try {
    const list = await api('GET', '/support/playbooks');
    s.playbooks = list || [];
    if (!s.playbooks.length) {
      listEl.innerHTML = '<div class="hive-card p-8 text-center col-span-full" style="color:var(--text-secondary)">No hay playbooks activos configurados para este tenant. Creá el primero con "+ Nuevo Playbook".</div>';
      return;
    }
    listEl.innerHTML = s.playbooks.map(pb => {
      const approvalBadge = pb.requiresApproval
        ? '<span class="text-[11px] px-2 py-0.5 rounded-full bg-amber-500/15 text-amber-400 border border-amber-500/20">Requiere Aprobación</span>'
        : '<span class="text-[11px] px-2 py-0.5 rounded-full bg-emerald-500/15 text-emerald-400 border border-emerald-500/20">Auto-Ejecución</span>';
      const typeBadge = `<span class="text-[11px] px-2 py-0.5 rounded-full bg-cyan-500/15 text-cyan-400 border border-cyan-500/20 font-mono">${esc(pb.actionType || 'SqlTemplate')}</span>`;
      const activeBadge = pb.isActive !== false
        ? ''
        : '<span class="text-[11px] px-2 py-0.5 rounded-full bg-red-500/15 text-red-400 border border-red-500/20">Inactivo</span>';

      return `<div class="hive-card p-5 flex flex-col justify-between gap-4">
        <div>
          <div class="flex flex-wrap items-center justify-between gap-2 mb-2">
            <div class="flex items-center gap-2">
              <code class="text-xs font-bold px-2 py-1 rounded bg-cyan-500/10 text-cyan-300 border border-cyan-500/20">${esc(pb.code)}</code>
              <h4 class="font-semibold text-base" style="color:var(--text-primary)">${esc(pb.name)}</h4>
            </div>
            <div class="flex flex-wrap gap-1.5">
              ${typeBadge}
              ${approvalBadge}
              ${activeBadge}
            </div>
          </div>
          <div class="text-xs mb-3" style="color:var(--text-secondary)">
            <span class="font-medium" style="color:var(--text-tertiary)">Síntomas:</span> ${esc(pb.triggerSymptoms || '—')}
          </div>
          <div class="p-2.5 rounded-lg mb-2 font-mono text-xs overflow-x-auto" style="background:var(--bg-tertiary);color:var(--text-secondary)">
            ${esc(pb.actionPayloadTemplate || '')}
          </div>
          ${pb.verificationSqlQuery ? `<div class="text-[11px] font-mono truncate mb-2" style="color:var(--text-tertiary)">Verificación: ${esc(pb.verificationSqlQuery)}</div>` : ''}
          <div class="flex flex-wrap items-center gap-4 text-xs mt-2" style="color:var(--text-tertiary)">
            <span>Máx filas: <strong style="color:var(--text-primary)">${pb.maxAffectedRows ?? 1}</strong></span>
            <span>Params: <code class="font-mono">${esc(pb.requiredParametersJson || '[]')}</code></span>
          </div>
        </div>
        <div class="flex justify-end gap-2 pt-3 border-t" style="border-color:var(--border-color)">
          <button type="button" onclick="dryRunPlaybook('${esc(pb.id)}','${esc(pb.code)}')" class="btn-secondary px-3 py-1.5 rounded-lg text-xs">Dry-Run</button>
          <button type="button" onclick="deactivatePlaybook('${esc(pb.id)}')" class="px-3 py-1.5 rounded-lg bg-red-600/20 hover:bg-red-600/30 text-red-400 border border-red-500/30 text-xs transition-all">Desactivar</button>
        </div>
      </div>`;
    }).join('');
  } catch (e) {
    toast(e.message || 'Error cargando playbooks', 'error');
  }
}

document.getElementById('f-pb')?.addEventListener('submit', async e => {
  e.preventDefault();
  try {
    const payload = {
      code: document.getElementById('pb-code').value.trim(),
      name: document.getElementById('pb-name').value.trim(),
      triggerSymptoms: document.getElementById('pb-symptoms').value.trim(),
      actionType: document.getElementById('pb-type').value,
      actionPayloadTemplate: document.getElementById('pb-payload').value.trim(),
      requiredParametersJson: document.getElementById('pb-params').value.trim() || '[]',
      verificationSqlQuery: document.getElementById('pb-verify').value.trim() || null,
      maxAffectedRows: parseInt(document.getElementById('pb-maxrows').value, 10) || 10,
      requiresApproval: document.getElementById('pb-approval').checked
    };
    await api('POST', '/support/playbooks', payload);
    toast('Playbook creado exitosamente', 'success');
    document.getElementById('f-pb').reset();
    togglePlaybookForm(false);
    loadPlaybooks();
  } catch (err) {
    toast(err.message || 'Error al crear playbook', 'error');
  }
});

async function deactivatePlaybook(id) {
  if (!confirm('¿Desactivar este playbook?')) return;
  try {
    await api('DELETE', `/support/playbooks/${encodeURIComponent(id)}`);
    toast('Playbook desactivado', 'success');
    loadPlaybooks();
  } catch (e) {
    toast(e.message || 'Error al desactivar playbook', 'error');
  }
}

async function dryRunPlaybook(id, code) {
  const raw = prompt(`Dry-Run de ${code || 'Playbook'}.\nIngresá los parámetros en formato JSON:`, '{}');
  if (raw === null) return;
  let params = {};
  try {
    params = raw.trim() ? JSON.parse(raw) : {};
  } catch {
    toast('JSON de parámetros inválido', 'error');
    return;
  }
  try {
    const res = await api('POST', `/support/playbooks/${encodeURIComponent(id)}/dry-run`, { parameters: params });
    if (res && res.success) {
      toast(`Dry-Run OK (${res.playbookCode || code}): ${res.affectedRows ?? 0} filas afectadas. ${res.summary || ''}`, 'success');
    } else {
      toast(`Dry-Run falló: ${(res && (res.error || res.summary)) || 'Error desconocido'}`, 'error');
    }
  } catch (e) {
    toast(e.message || 'Error ejecutando Dry-Run', 'error');
  }
}

// ==========================================
// Incident Detail Panels: Root Cause, Snapshots (1-Click Undo), Cryptographic Audit Chain
// ==========================================

function renderRootCausePanel(inc) {
  let rca = null;
  if (inc.rootCauseJson) {
    try {
      rca = typeof inc.rootCauseJson === 'string' ? JSON.parse(inc.rootCauseJson) : inc.rootCauseJson;
    } catch (e) {
      console.warn('Error parsing rootCauseJson', e);
    }
  }

  const hasVerification = inc.verificationQuery || inc.verificationPassed != null || (rca && rca.verificationSqlQuery);
  const hasArenaOrPr = inc.arenaValidationStatus || inc.pullRequestUrl;

  if (!rca && !hasVerification && !hasArenaOrPr) {
    return '';
  }

  const confPct = rca && rca.confidence != null ? Math.round(Number(rca.confidence) * 100) : null;
  const confBadge = confPct != null
    ? `<span class="text-[11px] px-2 py-0.5 rounded-full bg-cyan-500/15 text-cyan-300 border border-cyan-500/25 font-mono">Confianza: ${confPct}%</span>`
    : '';

  const trackColors = {
    Playbook: 'bg-emerald-500/15 text-emerald-300 border-emerald-500/30',
    DatabaseFix: 'bg-cyan-500/15 text-cyan-300 border-cyan-500/30',
    CodeFix: 'bg-purple-500/15 text-purple-300 border-purple-500/30',
    Escalate: 'bg-amber-500/15 text-amber-300 border-amber-500/30'
  };
  const track = rca && rca.recommendedTrack ? rca.recommendedTrack : null;
  const trackBadge = track
    ? `<span class="text-[11px] px-2 py-0.5 rounded-full border ${trackColors[track] || 'bg-stone-700 text-stone-300 border-stone-600'}">Track: ${esc(track)}</span>`
    : '';

  const pbBadge = rca && rca.matchedPlaybookCode
    ? `<span class="text-[11px] px-2 py-0.5 rounded-full bg-emerald-500/15 text-emerald-300 border border-emerald-500/30 font-mono">Playbook: ${esc(rca.matchedPlaybookCode)}</span>`
    : '';

  let verBadge = '';
  if (inc.verificationPassed === true) {
    verBadge = `<span class="text-[11px] px-2 py-0.5 rounded-full bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 font-medium">Verificado: 0 anomalías</span>`;
  } else if (inc.verificationPassed === false) {
    verBadge = `<span class="text-[11px] px-2 py-0.5 rounded-full bg-red-500/20 text-red-300 border border-red-500/30 font-medium">Verificación Fallida</span>`;
  } else if (hasVerification) {
    verBadge = `<span class="text-[11px] px-2 py-0.5 rounded-full bg-amber-500/20 text-amber-300 border border-amber-500/30 font-medium">Verificación: Pendiente</span>`;
  }

  let arenaBadge = '';
  if (inc.arenaValidationStatus) {
    const st = inc.arenaValidationStatus;
    const cls = st === 'Survived'
      ? 'bg-emerald-500/20 text-emerald-300 border-emerald-500/30'
      : (st === 'Defeated' || st === 'Error')
        ? 'bg-red-500/20 text-red-300 border-red-500/30'
        : 'bg-cyan-500/20 text-cyan-300 border-cyan-500/30';
    arenaBadge = `<span class="text-[11px] px-2 py-0.5 rounded-full border ${cls} font-mono">HiveArena: ${esc(st)}</span>`;
  }

  const prLink = inc.pullRequestUrl
    ? `<a href="${esc(inc.pullRequestUrl)}" target="_blank" rel="noopener noreferrer" class="text-xs text-cyan-400 hover:underline font-mono inline-flex items-center gap-1">Ver Pull Request ↗</a>`
    : '';

  const vQuery = inc.verificationQuery || (rca && rca.verificationSqlQuery) || '';
  const dbEv = (rca && Array.isArray(rca.databaseEvidence)) ? rca.databaseEvidence : [];
  const commits = (rca && Array.isArray(rca.recentCommits)) ? rca.recentCommits : [];
  const logs = (rca && Array.isArray(rca.applicationLogs)) ? rca.applicationLogs : [];

  return `<div class="hive-card p-4 mt-4 border-l-2 border-cyan-400">
    <div class="flex flex-wrap items-center justify-between gap-2 mb-3">
      <div class="text-xs uppercase tracking-wider font-semibold text-cyan-400">Root Cause &amp; Multi-Source Evidence</div>
      <div class="flex flex-wrap gap-1.5">
        ${confBadge}
        ${trackBadge}
        ${pbBadge}
        ${verBadge}
        ${arenaBadge}
      </div>
    </div>

    ${rca && rca.summaryHypothesis ? `<div class="text-sm mb-3 leading-relaxed" style="color:var(--text-primary)">${esc(rca.summaryHypothesis)}</div>` : ''}

    ${vQuery ? `<div class="mb-3 p-2.5 rounded-lg border" style="background:var(--bg-tertiary);border-color:var(--border-color)">
      <div class="text-[11px] mb-1" style="color:var(--text-tertiary)">Query de Verificación Post-Fix:</div>
      <pre class="text-xs text-cyan-300 font-mono overflow-x-auto">${esc(vQuery)}</pre>
    </div>` : ''}

    ${prLink ? `<div class="mb-3">${prLink}</div>` : ''}

    <div class="space-y-2 text-xs">
      ${dbEv.length ? `<details class="rounded-lg border p-2.5" style="background:var(--bg-tertiary);border-color:var(--border-color)">
        <summary class="cursor-pointer font-medium" style="color:var(--text-primary)">Evidencia SQL (${dbEv.length} consultas)</summary>
        <div class="mt-2 space-y-2">
          ${dbEv.map(ev => `<div class="p-2 rounded bg-black/20">
            <div class="font-mono text-[11px] text-cyan-300 mb-1">${esc(ev.sqlQuery || '')}</div>
            <div class="text-[11px] mb-1" style="color:var(--text-tertiary)">Filas: ${ev.rowCount ?? 0} · ${ev.success ? 'OK' : esc(ev.error || 'Error')}</div>
            ${ev.renderedTable ? `<pre class="text-[11px] font-mono overflow-x-auto" style="color:var(--text-secondary)">${esc(ev.renderedTable)}</pre>` : ''}
          </div>`).join('')}
        </div>
      </details>` : ''}

      ${commits.length ? `<details class="rounded-lg border p-2.5" style="background:var(--bg-tertiary);border-color:var(--border-color)">
        <summary class="cursor-pointer font-medium" style="color:var(--text-primary)">Commits Recientes (${commits.length})</summary>
        <div class="mt-2 space-y-1.5">
          ${commits.map(c => `<div class="text-[11px] flex flex-wrap gap-2 items-baseline">
            <code class="text-cyan-400 font-mono">${esc((c.commitHash || '').substring(0, 8))}</code>
            <span class="font-medium" style="color:var(--text-primary)">${esc(c.subject || '')}</span>
            <span style="color:var(--text-tertiary)">por ${esc(c.author || 'unknown')}</span>
          </div>`).join('')}
        </div>
      </details>` : ''}

      ${logs.length ? `<details class="rounded-lg border p-2.5" style="background:var(--bg-tertiary);border-color:var(--border-color)">
        <summary class="cursor-pointer font-medium" style="color:var(--text-primary)">Logs de Aplicación (${logs.length})</summary>
        <div class="mt-2 space-y-1.5">
          ${logs.map(l => `<div class="text-[11px] font-mono p-1.5 rounded bg-black/20">
            <span class="text-amber-400">[${esc(l.level || 'INFO')}]</span> ${esc(l.message || '')}
            ${l.exceptionText ? `<div class="text-red-400 mt-0.5 truncate">${esc(l.exceptionText)}</div>` : ''}
          </div>`).join('')}
        </div>
      </details>` : ''}
    </div>
  </div>`;
}

async function loadIncidentSnapshots(incidentId) {
  const panel = document.getElementById('inc-snapshots-panel');
  if (!panel) return;
  try {
    const snaps = await api('GET', `/support/incidents/${encodeURIComponent(incidentId)}/snapshots`);
    const list = Array.isArray(snaps) ? snaps : [];
    if (!list.length) {
      panel.innerHTML = `<div class="text-xs uppercase tracking-wider mb-1" style="color:var(--text-tertiary)">Snapshots de Datos (Before / After Diff &amp; 1-Click Undo)</div>
        <div class="text-xs" style="color:var(--text-secondary)">Sin snapshots de filas registrados para este incidente.</div>`;
      return;
    }

    panel.innerHTML = `<div class="text-xs uppercase tracking-wider mb-3 font-semibold" style="color:var(--text-tertiary)">Snapshots de Datos (${list.length}) &amp; 1-Click Undo</div>
      <div class="space-y-4">
        ${list.map(snap => {
          const revertedBadge = snap.isReverted
            ? `<span class="text-[11px] px-2.5 py-1 rounded-full bg-amber-500/20 text-amber-300 border border-amber-500/30 font-medium">Revertido el ${snap.revertedAt ? new Date(snap.revertedAt).toLocaleString() : '—'}</span>`
            : `<button type="button" onclick="revertSnapshot('${esc(incidentId)}','${esc(snap.id)}')" class="px-3 py-1.5 rounded-lg bg-amber-500 hover:bg-amber-400 text-black font-semibold text-xs transition-all">Revertir cambio (1-Click Undo)</button>`;

          return `<div class="p-3 rounded-xl border space-y-2.5" style="background:var(--bg-tertiary);border-color:var(--border-color)">
            <div class="flex flex-wrap items-center justify-between gap-2">
              <div>
                <span class="text-xs font-bold font-mono text-cyan-400">${esc(snap.targetTable || 'Table')}</span>
                <span class="text-xs ml-2" style="color:var(--text-secondary)">${snap.affectedRowCount ?? 0} filas afectadas</span>
              </div>
              ${revertedBadge}
            </div>
            <div class="grid grid-cols-1 md:grid-cols-2 gap-2">
              <div>
                <div class="text-[10px] uppercase mb-1" style="color:var(--text-tertiary)">Before (JSON)</div>
                <pre class="text-[11px] font-mono p-2 rounded bg-black/30 overflow-x-auto max-h-36" style="color:var(--text-secondary)">${esc(snap.beforeRowsJson || '[]')}</pre>
              </div>
              <div>
                <div class="text-[10px] uppercase mb-1" style="color:var(--text-tertiary)">After (JSON)</div>
                <pre class="text-[11px] font-mono p-2 rounded bg-black/30 overflow-x-auto max-h-36" style="color:var(--text-secondary)">${esc(snap.afterRowsJson || '[]')}</pre>
              </div>
            </div>
            ${snap.inverseSqlScript ? `<details class="text-xs">
              <summary class="cursor-pointer text-[11px]" style="color:var(--text-tertiary)">Ver SQL Inverso de Rollback</summary>
              <pre class="text-[11px] font-mono p-2 mt-1 rounded bg-black/30 text-amber-300 overflow-x-auto">${esc(snap.inverseSqlScript)}</pre>
            </details>` : ''}
          </div>`;
        }).join('')}
      </div>`;
  } catch (e) {
    panel.innerHTML = `<div class="text-xs uppercase tracking-wider mb-1" style="color:var(--text-tertiary)">Snapshots de Datos</div>
      <div class="text-xs" style="color:var(--text-secondary)">No se pudieron cargar los snapshots.</div>`;
  }
}

async function revertSnapshot(incidentId, snapshotId) {
  if (!confirm('¿Ejecutar 1-Click Undo para restaurar las filas afectadas a su estado previo?')) return;
  try {
    const res = await api('POST', `/support/incidents/${encodeURIComponent(incidentId)}/snapshots/${encodeURIComponent(snapshotId)}/revert`);
    toast((res && res.message) || `Snapshot revertido (${res?.affectedRows ?? 0} filas restauradas)`, 'success');
    loadIncidentSnapshots(incidentId);
    loadIncidentAuditChain(incidentId);
  } catch (e) {
    toast(e.message || 'Error al revertir snapshot', 'error');
  }
}

const auditChainsCache = {};

async function loadIncidentAuditChain(incidentId) {
  const panel = document.getElementById('inc-audit-panel');
  if (!panel) return;
  try {
    const chain = await api('GET', `/support/incidents/${encodeURIComponent(incidentId)}/audit-chain`);
    if (!chain) return;
    auditChainsCache[incidentId] = chain;
    const total = chain.totalEntries ?? (Array.isArray(chain.entries) ? chain.entries.length : 0);
    const valid = chain.isValid !== false;
    const badge = valid
      ? `<span class="text-[11px] px-2.5 py-1 rounded-full bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 font-medium">Cadena SHA-256 Verificada (${total} bloques)</span>`
      : `<span class="text-[11px] px-2.5 py-1 rounded-full bg-red-500/20 text-red-300 border border-red-500/30 font-medium">Cadena Rota en bloque ${esc(String(chain.brokenAtEntryId || '?'))}</span>`;

    const entries = Array.isArray(chain.entries) ? chain.entries : [];
    panel.innerHTML = `<div class="flex flex-wrap items-center justify-between gap-2 mb-3">
        <div class="text-xs uppercase tracking-wider font-semibold" style="color:var(--text-tertiary)">Cadena de Auditoría Criptográfica (SHA-256)</div>
        <div class="flex flex-wrap items-center gap-2">
          ${badge}
          <button type="button" onclick="downloadAuditPack('${esc(incidentId)}')" class="btn-secondary px-2.5 py-1 rounded-lg text-[11px]">Descargar Evidencia JSON</button>
        </div>
      </div>
      ${entries.length ? `<div class="space-y-1.5 max-h-56 overflow-y-auto pr-1">
        ${entries.map(en => {
          const shortHash = en.entryHash ? '0x' + String(en.entryHash).substring(0, 10) : '0x—';
          const ts = en.createdAt ? new Date(en.createdAt).toLocaleTimeString() : '';
          return `<div class="flex items-center justify-between gap-2 px-2.5 py-1.5 rounded-lg text-xs border" style="background:var(--bg-tertiary);border-color:var(--border-color)">
            <div class="flex items-center gap-2 min-w-0">
              <span class="font-mono text-[11px] text-cyan-400">#${en.sequenceNumber ?? '?'}</span>
              <span class="font-medium truncate" style="color:var(--text-primary)">${esc(en.eventType || '')}</span>
              <span class="text-[11px] truncate" style="color:var(--text-tertiary)">· ${esc(en.actor || 'system')}</span>
            </div>
            <div class="flex items-center gap-2 flex-shrink-0">
              <code class="text-[10px] font-mono px-1.5 py-0.5 rounded bg-black/30 text-emerald-400" title="${esc(en.entryHash || '')}">${esc(shortHash)}</code>
              <span class="text-[10px]" style="color:var(--text-tertiary)">${esc(ts)}</span>
            </div>
          </div>`;
        }).join('')}
      </div>` : `<div class="text-xs" style="color:var(--text-secondary)">Sin bloques de auditoría registrados.</div>`}`;
  } catch (e) {
    panel.innerHTML = `<div class="text-xs uppercase tracking-wider mb-1" style="color:var(--text-tertiary)">Cadena de Auditoría Criptográfica (SHA-256)</div>
      <div class="text-xs" style="color:var(--text-secondary)">No se pudo verificar la cadena de auditoría.</div>`;
  }
}

function downloadAuditPack(incidentId) {
  const data = auditChainsCache[incidentId] || { incidentId, exportedAt: new Date().toISOString() };
  const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `incident-${incidentId}-audit-pack.json`;
  document.body.appendChild(a);
  a.click();
  setTimeout(() => {
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  }, 150);
}

