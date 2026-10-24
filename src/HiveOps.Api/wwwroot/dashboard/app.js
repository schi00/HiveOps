const API='/api';
// Minimal shim for anime.js to avoid runtime errors if CDN fails to load
try{
 if(typeof window!=='undefined' && typeof window.anime==='undefined'){
   const noop=()=>{};
   window.anime=Object.assign(function(){return null;}, { stagger: () => noop });
 }
}catch{}
// HIVE account handoff: the launcher passes the shared identity JWT in the URL
// hash (#token=...) so it never reaches server logs or referer headers.
try{
 const _h=location.hash||'',_m=_h.match(/token=([^&]+)/);
 if(_m){
   const _jwt=decodeURIComponent(_m[1]);
   const _b64=_jwt.split('.')[1].replace(/-/g,'+').replace(/_/g,'/');
   const _p=JSON.parse(decodeURIComponent(escape(atob(_b64))));
   const _role=_p.role||_p['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'];
   const _name=_p.name||_p['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name']||_p.email;
   localStorage.setItem('token',_jwt);
   localStorage.setItem('user',JSON.stringify({username:_name||'usuario',role:_role||'Tenant',tenantName:_p.org_name||''}));
   if(_p.org_id&&_p.org_id!=='00000000-0000-0000-0000-000000000000') localStorage.setItem('tenantId',_p.org_id);
   else localStorage.removeItem('tenantId');
   history.replaceState(null,'',location.pathname+location.search);
 }
}catch(_e){console.warn('HIVE token handoff failed',_e)}
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
       <label class="text-sm block mb-2" style="color:var(--text-secondary)">Cuenta</label>
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
   if(!tid){toast('Selecciona una cuenta','error');return;}
   if(!pid){toast('Ingresa un PriceId','error');return;}
   try{await api('POST',`/admin/tenants/${tid}/billing/ensure-subscription`,{priceId:pid}); toast('Suscripción activada','success'); lb();}
   catch(e){toast(e.message,'error');}
 };
 const st=document.getElementById('bill-status');
 const cur=ts.find(x=>x.id===sel.value);
 st.textContent=cur?`Estado actual: ${cur.subscriptionStatus||'—'} · Price: ${cur.stripePriceId||'—'}`:'Selecciona una cuenta';
 sel.onchange=()=>{ const c=ts.find(x=>x.id===sel.value); st.textContent=c?`Estado actual: ${c.subscriptionStatus||'—'} · Price: ${c.stripePriceId||'—'}`:'Selecciona una cuenta'; };
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
   }).join(''):'<div class="text-center py-6 text-sm" style="color:var(--text-secondary)">Sin cuentas registrados</div>';
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
     <p class="text-sm mb-4" style="color:var(--text-secondary)">Asigná la cadena SQL para esta cuenta (queda cifrada en el servidor). Dejá vacío para borrar y usar la base catálogo.</p>
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
       if(!tid){toast('Selecciona una cuenta','error');return;}
       try{
         await fetch(`${API}/admin/tenants/${tid}/dedicated-database`,{method:'PUT',headers:{'Content-Type':'application/json','Authorization':'Bearer '+(s.token||'')},credentials:'include',body:JSON.stringify({connectionString:cs})});
         document.getElementById('shdb-conn').value='';
         toast(cs?'Cadena guardada':'Cadena eliminada','success'); shdbRefresh();
       }catch(e){toast(e.message||'Error','error');}
     };
     document.getElementById('shdb-clear').onclick=async ()=>{
       const tid=(document.getElementById('shdb-tenant')||{}).value;
       if(!tid){toast('Selecciona una cuenta','error');return;}
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
 st.textContent=j&&j.configured?'Hay cadena dedicada configurada para esta cuenta.':'Sin cadena dedicada (usa catálogo).';
}catch{}
}
document.getElementById('login-form').addEventListener('submit',async e=>{
e.preventDefault();
const el=document.getElementById('le');el.classList.add('hidden');
try{
const r=await fetch(`${API}/auth/login`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({username:document.getElementById('lu').value,password:document.getElementById('lp').value})});
if(!r.ok)throw new Error('Credenciales inválidas');
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
async function refreshIdentity(){
try{
const o={method:'GET',headers:{'Content-Type':'application/json','Authorization':'Bearer '+s.token},credentials:'include'};
const r=await fetch(API+'/auth/me',o);
if(!r.ok)return;
const d=await r.json();
if(!d||d.authenticated!==true)return;
s.user={username:d.displayName||(s.user&&s.user.username)||'',role:d.role||(s.user&&s.user.role),tenantName:d.tenantName||(s.user&&s.user.tenantName)};
localStorage.setItem('user',JSON.stringify(s.user));
if(d.tenantId){s.tenantId=d.tenantId;localStorage.setItem('tenantId',d.tenantId);}
else if(s.tenantId==='00000000-0000-0000-0000-000000000000'){s.tenantId=null;localStorage.removeItem('tenantId');}
}catch{}
}
async function loadDeploymentInfo(){
try{const d=await api('GET','/deployment/info');s.deploymentMode=d&&d.mode?d.mode:null;}catch{s.deploymentMode=null;}
}
async function showApp(){
try{
document.getElementById('login-screen').classList.add('hidden');
document.getElementById('app').classList.remove('hidden');
await refreshIdentity();
s.isAdmin=s.user.role==='SuperAdmin'||s.user.role==='Admin';
await loadDeploymentInfo();
if(s.isAdmin){const adm=document.getElementById('n-adm');if(adm)adm.classList.remove('hidden');}
else {const adm=document.getElementById('n-adm');if(adm)adm.classList.add('hidden');}
updateMobileAdminNav();
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
if(scope)scope.textContent='Plataforma · todos las cuentas';
}else if(s.user&&s.user.role==='Admin'){
badge.textContent='ADMIN';badge.className=amber;
if(udRole)udRole.textContent='Admin · multi-cuenta';
if(scope)scope.textContent='Administración multi-cuenta';
}else{
badge.textContent='CUENTA';badge.className=cyan;
if(udRole)udRole.textContent='Administrador de la cuenta';
updateScopeLabel();
}
const canSwitch=s.isAdmin;
const wrap=document.getElementById('tenant-select-wrap'),wrapM=document.getElementById('tenant-select-wrap-mobile');
const acc=document.getElementById('account-name'),accM=document.getElementById('account-name-mobile');
if(wrap)wrap.classList.toggle('hidden',!canSwitch);
if(wrapM)wrapM.classList.toggle('hidden',!canSwitch);
if(acc){acc.classList.toggle('hidden',canSwitch);acc.textContent=(s.user&&s.user.tenantName)||'';}
if(accM){accM.classList.toggle('hidden',canSwitch);const t=accM.querySelector('div');if(t)t.textContent=(s.user&&s.user.tenantName)||'';}
}
function updateScopeLabel(){
const scope=document.getElementById('scope-label');if(!scope)return;
if(s.isAdmin){
const t=(s.tenants||[]).find(x=>x.id===s.tenantId);
scope.textContent=s.tenantId?('Actuando como: '+(t?t.name:s.tenantId)):'Sin cuenta seleccionada';
}else{
scope.textContent=s.user&&s.user.tenantName?('Cuenta: '+s.user.tenantName):'';
}
}
function logout(){localStorage.clear();s.token=null;s.user=null;showLogin();}
async function api(m,p,b){
const o={method:m,headers:{'Content-Type':'application/json','Authorization':'Bearer '+(s.token||'')},credentials:'include'};
if(s.tenantId)o.headers['X-Tenant-Id']=s.tenantId;
if(b)o.body=JSON.stringify(b);
const r=await fetch(API+p,o);
if(r.status===401){logout();throw new Error('Sesión expirada');}
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
const viewMap={dashboard:'dash',incidents:'inc',kb:'kb',playbooks:'playbooks',ctx:'ctx',config:'cfg',admin:'adm'};
const navMap={dashboard:'dash',incidents:'inc',kb:'kb',playbooks:'pb',ctx:'ctx',config:'cfg',admin:'adm'};
const viewId=viewMap[v]||v;
const navId=navMap[v]||viewId;
const t=document.getElementById('v-'+viewId)||document.getElementById('v-'+v);
if(t){t.classList.remove('hidden');t.classList.add('active');
anime({targets:t,opacity:[0,1],translateY:[16,0],duration:450,easing:'easeOutCubic'});}
else{console.warn('Dashboard view not found for',v,viewId);}
const nb=document.getElementById('n-'+navId)||document.getElementById('n-'+viewId)||document.getElementById('n-'+v);
if(nb){nb.classList.add('active');}
if(v==='dashboard')ld();if(v==='incidents')li();if(v==='kb')lk();if(v==='playbooks')loadPlaybooks();if(v==='ctx')lctx();if(v==='config')lc();if(v==='admin'){lm();lh();lb();}
}
async function ld(){
try{
// Admins pueden ver summary global sin tenant seleccionado
if(!s.tenantId&&!s.isAdmin){
document.getElementById('c-open').textContent='0';
document.getElementById('c-crit').textContent='0';
document.getElementById('c-res').textContent='0';
document.getElementById('c-avg').textContent='0';
document.getElementById('l-rec').innerHTML='<div class="p-8 text-center" style="color:var(--text-secondary)">Selecciona una cuenta</div>';
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
const INC_PAGE_SIZE=15;
let incVisibleCount=INC_PAGE_SIZE;
async function li(search){
try{
// Admins pueden ver incidentes globales sin tenant seleccionado
if(!s.tenantId&&!s.isAdmin){document.getElementById('l-all').innerHTML='<div class="p-10 text-center" style="color:var(--text-secondary)">Selecciona una cuenta</div>';return;}
const f=document.getElementById('fs').value,v=document.getElementById('fv').value;
const q=`/support/incidents?${f?'status='+f+'&':''}${v?'severity='+v+'&':''}${search?'q='+encodeURIComponent(search)+'&':''}`;
const d=await api('GET',q);s.incidents=d||[];
incVisibleCount=INC_PAGE_SIZE;
renderIncidentList();
const el=document.getElementById('l-all');
anime({targets:el.children,translateY:[10,0],opacity:[0,1],delay:anime.stagger(30),duration:300,easing:'easeOutCubic'});
loadLlmRetryBadges();
}catch(e){toast(e.message,'error');}
}
function renderIncidentList(){
const el=document.getElementById('l-all');
if(!el)return;
const d=s.incidents||[];
if(!d.length){
  el.innerHTML=`<div class="p-10 text-center" style="color:var(--text-secondary)">
    <svg class="w-12 h-12 mx-auto mb-3 opacity-30" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.5" d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2"/></svg>
    <div class="mb-4">Sin incidentes — los reportes por WhatsApp/Ticketera o el botón + Nuevo aparecerán acá</div>
    <button type="button" onclick="om('mc')" class="btn-primary px-5 py-2.5 rounded-xl text-sm">+ Nuevo</button>
  </div>`;
  return;
}
const visible=d.slice(0,incVisibleCount);
let html=visible.map(x=>rRow(x)).join('');
if(d.length>incVisibleCount){
  html+=`<div class="p-4 text-center"><button type="button" onclick="showMoreIncidents()" class="btn-secondary px-5 py-2 rounded-xl text-xs">Mostrar más (${d.length-incVisibleCount} restantes)</button></div>`;
}
el.innerHTML=html;
}
function showMoreIncidents(){
incVisibleCount+=INC_PAGE_SIZE;
renderIncidentList();
loadLlmRetryBadges();
}
function loadLlmRetryBadges(){
 // Compute LLM retry badges by reading messages per-incident (non-blocking)
 try{
   const token=s.token||''; const hdr={'Authorization':'Bearer '+token,'Content-Type':'application/json'}; if(s.tenantId) hdr['X-Tenant-Id']=s.tenantId;
   (s.incidents||[]).slice(0,incVisibleCount).forEach(async it=>{
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
}
async function lk(){
try{
const d=await api('GET','/support/kb');s.kb=d||[];
renderKb(d||[]);
}catch(e){toast(e.message,'error');}
}
function renderKb(list){
const el=document.getElementById('l-kb');
el.innerHTML=list.length?list.map(k=>`<div class="hive-card p-5 cursor-pointer group"><div class="flex justify-between items-start mb-3"><h4 class="font-semibold group-hover:text-cyan-400 transition-colors" style="color:var(--text-primary)">${esc(k.title)}</h4>${k.isPublished?'<span class="text-xs px-2.5 py-1 rounded-full bg-emerald-500/15 text-emerald-400 border border-emerald-500/20">Publicado</span>':'<span class="text-xs px-2.5 py-1 rounded-full bg-stone-700/60 text-stone-400">Borrador</span>'}</div><div class="text-sm line-clamp-3 mb-4" style="color:var(--text-secondary)">${esc(k.content||'')}</div><div class="flex flex-wrap gap-2">${(Array.isArray(k.tags)?k.tags:String(k.tags||'').split(',').map(s=>s.trim()).filter(Boolean)).map(t=>`<span class="text-xs px-2 py-1 rounded-lg bg-cyan-500/10 text-cyan-400 border border-cyan-500/20">${esc(t)}</span>`).join('')}</div></div>`).join(''):'<div class="hive-card p-8 text-center col-span-full" style="color:var(--text-secondary)"><div class="mb-4">Sin artículos — creá el primero para alimentar la base de conocimiento del agente</div><button type="button" onclick="om(\'mk\')" class="btn-primary px-5 py-2.5 rounded-xl text-sm">+ Nuevo Artículo</button></div>';
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
const d=await api('POST','/support/kb',{title:document.getElementById('kt').value,category:document.getElementById('kc').value,tags:(document.getElementById('ktg').value||'').split(',').map(x=>x.trim()).filter(Boolean).join(', '),content:document.getElementById('kb').value,resolutionSteps:document.getElementById('ks').value,isPublished:document.getElementById('kp').checked});
cm('mk');toast('Articulo KB creado','success');document.getElementById('fk').reset();lk();
}catch(x){toast(x.message,'error');}
});
async function lm(){
try{
const d=await api('GET','/support/merge-queue');
const el=document.getElementById('l-mer');
el.innerHTML=(d||[]).length?d.map(m=>`<div class="hive-card p-4 flex items-center justify-between"><div><div class="font-semibold text-sm" style="color:var(--text-primary)">${esc(m.title||'Fix')}</div><div class="text-xs mt-1" style="color:var(--text-tertiary)">Branch: <code class="text-cyan-400 font-mono">${esc(m.branch)}</code></div></div><button onclick="approveMerge('${m.id}')" class="px-4 py-2 rounded-lg bg-emerald-600 hover:bg-emerald-500 text-white text-xs font-medium transition-all">Aprobar</button></div>`).join(''):`<div class="text-center py-6" style="color:var(--text-secondary)">Cola vacía</div>`;
}catch(e){toast(e.message,'error');}
}
async function lh(){
try{
const d=await api('GET','/support/system-health');
const db=d?.dbHealthy,pl=d?.pipelineHealthy;
const hd=document.getElementById('hd'),hp=document.getElementById('hp');
hd.className='w-3 h-3 rounded-full '+(db?'bg-emerald-500':'bg-rose-500');
hp.className='w-3 h-3 rounded-full '+(pl?'bg-emerald-500':'bg-rose-500');
document.getElementById('td').textContent=db?'Healthy':'Unhealthy';
document.getElementById('tp').textContent=pl?'Healthy':'Unhealthy';
const lastRun=d?.lastTestRun;
document.getElementById('tt').textContent=(!lastRun||lastRun==='unknown')?'Pendiente':lastRun;
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
const q=((document.getElementById('inc-search')||{}).value||'').trim();
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
actions+=`<div class="mt-5 p-4 rounded-xl hive-card" style="background:rgba(34,211,238,0.05)"><div class="text-xs mb-2" style="color:var(--text-tertiary)">Fix sugerido (SQL)</div><pre class="text-xs text-cyan-300 overflow-x-auto font-mono">${esc(d.suggestedFixSql)}</pre><div class="flex flex-wrap gap-2 mt-3"><button onclick="appr('${id}')" class="px-3 py-1.5 rounded-lg bg-emerald-600 hover:bg-emerald-500 text-white text-xs">Aprobar</button><button onclick="rej('${id}')" class="px-3 py-1.5 rounded-lg bg-red-600 hover:bg-red-500 text-white text-xs">Rechazar</button><button onclick="showChatOpsLinks('${id}')" class="btn-secondary px-3 py-1.5 rounded-lg text-xs">Links de aprobación en 1 clic (Slack/Teams)</button></div><div id="chatops-links-box" class="hidden mt-3"></div></div>`;
} else if(d.status==='AwaitingApproval'){
actions+=`<div class="mt-4 flex flex-wrap gap-2"><button onclick="showChatOpsLinks('${id}')" class="btn-secondary px-3 py-1.5 rounded-lg text-xs">Obtener links de aprobación (Slack/Teams)</button></div><div id="chatops-links-box" class="hidden mt-3"></div>`;
}
const rcaPanelHtml = renderRootCausePanel(d);
const evidencePackHtml = `${rcaPanelHtml}<div id="inc-snapshots-panel" class="hive-card p-4 mt-4"><div class="text-xs uppercase tracking-wider mb-2" style="color:var(--text-tertiary)">Copias de seguridad de datos (comparación antes/después y deshacer en 1 clic)</div><div class="text-xs" style="color:var(--text-secondary)">Cargando copias de seguridad...</div></div><div id="inc-audit-panel" class="hive-card p-4 mt-4"><div class="text-xs uppercase tracking-wider mb-2" style="color:var(--text-tertiary)">Registro de auditoría verificable</div><div class="text-xs" style="color:var(--text-secondary)">Verificando integridad del registro...</div></div>`;
document.getElementById('db').innerHTML=`<div class="flex items-center gap-3 mb-5"><div class="w-3 h-3 rounded-full ${sev} status-pulse"></div><div class="text-2xl font-bold" style="color:var(--text-primary)">${esc(d.title)}</div></div><div class="text-sm mb-1" style="color:var(--text-secondary)">Estado: <span class="${st} font-medium">${d.status}</span></div><div class="text-sm mb-5" style="color:var(--text-tertiary)">Creado: ${new Date(d.createdAt).toLocaleString()}</div><div class="hive-card p-4 mb-4"><div class="text-xs uppercase tracking-wider mb-2" style="color:var(--text-tertiary)">Descripción</div><div class="text-sm leading-relaxed whitespace-pre-wrap" style="color:var(--text-secondary)">${esc(d.description)}</div></div>${d.resolutionNotes?`<div class=\"hive-card p-4 mb-4\"><div class=\"text-xs uppercase tracking-wider mb-2\" style=\"color:var(--text-tertiary)\">Notas de resolución</div><div class=\"text-sm whitespace-pre-wrap\" style=\"color:var(--text-secondary)\">${esc(d.resolutionNotes)}</div></div>`:''}${d.reasoning?`<div class=\"hive-card p-4 mb-4 border-l-2 border-cyan-500\" style=\"background:rgba(34,211,238,0.03)\"><div class=\"text-xs text-cyan-400 uppercase tracking-wider mb-2\">Análisis IA</div><div class=\"text-sm italic\" style=\"color:var(--text-secondary)\">${esc(d.reasoning)}</div></div>`:''}${d.gitBranch?`<div class=\"text-xs\" style=\"color:var(--text-tertiary)\">Branch: <code class=\"text-cyan-400 font-mono\">${esc(d.gitBranch)}</code> · Commit: <code class=\"font-mono\" style=\"color:var(--text-secondary)\">${esc((d.gitCommitHash||'').substring(0,8))}</code></div>`:''}${actions}${evidencePackHtml}<div id=\"msgs\" class=\"hive-card p-4 mt-4\"><div class=\"text-xs uppercase tracking-wider mb-2\" style=\"color:var(--text-tertiary)\">Mensajes</div><div id=\"ml\"><div class=\"text-sm\" style=\"color:var(--text-secondary)\">Cargando mensajes...</div></div></div>`;

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
if(!s.tenantId&&!s.isAdmin){toast('Selecciona una cuenta primero','error');return;}
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
sel.innerHTML='<option value="">Seleccionar Cuenta...</option>'+(tenants||[]).map(t=>`<option value="${t.id}">${t.name}</option>`).join('');
if(s.tenantId&&tenants&&tenants.find(t=>t.id===s.tenantId)){sel.value=s.tenantId;changeTenant();}
}
updateScopeLabel();
}catch(e){console.error('Error loading tenants:',e);toast('Error al cargar cuentas','error');}
}
function changeTenant(){
const sel=document.getElementById('tenant-select');
s.tenantId=sel.value;
updateScopeLabel();
const t=(s.tenants||[]).find(x=>x.id===s.tenantId);
if(s.tenantId){
localStorage.setItem('tenantId',s.tenantId);
toast('Supervisando cuenta: '+(t?t.name:s.tenantId),'success');
ctxReloadIfActive();
if(hubConn&&hubConn.invoke){hubConn.invoke('JoinTenantGroup', s.tenantId).catch(()=>{});} 
}else{
localStorage.removeItem('tenantId');
toast('Modo plataforma · vista global','info');
}
Promise.allSettled([ld(),li()]);
}
function actAsTenant(tid){
if(!tid)return;
const sel=document.getElementById('tenant-select');
if(sel)sel.value=tid;
s.tenantId=tid;
localStorage.setItem('tenantId',tid);
updateScopeLabel();
ld();li();
ctxReloadIfActive();
if(hubConn&&hubConn.invoke){hubConn.invoke('JoinTenantGroup',tid).catch(()=>{});}
toast('Operando como el cuenta seleccionada','success');
}
function ctxReloadIfActive(){const v=document.getElementById('v-ctx');if(v&&v.classList.contains('active'))lctx();}
async function patchTenantPath(pathRel, payload){
if(!s.tenantId){throw new Error('Sin cuenta');}
const o={method:'PATCH',headers:{'Content-Type':'application/json','Authorization':'Bearer '+(s.token||'')},credentials:'include',body:JSON.stringify(payload)};
if(s.tenantId)o.headers['X-Tenant-Id']=s.tenantId;
const r=await fetch(`${API}/tenants/${s.tenantId}${pathRel}`,o);
if(r.status===401){logout();throw new Error('Sesión expirada');}
const t=await r.text();
if(!r.ok){throw new Error(t||r.statusText);}
return t?JSON.parse(t):null;}
// ==== Config de tenant — estado cargado en memoria ====
// cfgAgentFull conserva TODOS los campos del agente tal como los devolvió el
// servidor, para reenviarlos intactos al guardar solo enabled/operatingMode.
let cfgAgentFull=null;
let cfgDeployGitFull=null;
let handoffKeywords=[];
let diagBlockedTables=[];

async function lc(){
try{
if(!s.tenantId){toast('Selecciona una cuenta primero','warning');return;}
const cfg=await api('GET','/tenants/'+s.tenantId+'/config');
const el=id=>document.getElementById(id);
el('cfg-schema-banner').textContent='Schema v'+(cfg.configurationSchemaVersion ?? '?');
const a=cfg.agent||{};
cfgAgentFull=a;
el('cfg-agent-enabled').checked=a.enabled!==false;
const om=typeof a.operatingMode==='number'?(['Shadow','Assisted','Autonomous'][a.operatingMode]||'Autonomous'):(a.operatingMode||'Autonomous');
setOpModeRadio(om);
updateOperatingModePills(om);
const L=cfg.llm||{};
const isSH=s.deploymentMode==='SelfHosted';
const llmFields=el('cfg-llm-fields');
const llmNote=el('cfg-llm-saas-note');
const llmSave=el('cfg-llm-save-btn');
if(llmFields)llmFields.classList.toggle('hidden',!isSH);
if(llmNote)llmNote.classList.toggle('hidden',isSH);
if(llmSave)llmSave.classList.toggle('hidden',!isSH);
el('cfg-llm-provider').value=L.provider||'openai';
el('cfg-llm-model').value=L.model||'gpt-4';
const temp=L.temperature??0.7;
el('cfg-llm-temp').value=String(temp);
el('cfg-llm-temp-val').textContent=String(temp);
el('cfg-llm-max-out').value=L.maxOutputTokens??2000;
const dg=cfg.diagnostics||{};
el('cfg-diag-budget').value=dg.maxDiagnosticQueriesPerIncident??'';
diagBlockedTables=Array.isArray(dg.blockedTables)?[...dg.blockedTables]:[];
renderDiagBlockedChips();
el('cfg-diag-sensitive').checked=!!dg.allowSensitiveTables;
renderPolicies(cfg.policies||[]);
await Promise.allSettled([loadAdminSettings(),loadDeployGit(cfg),loadTargetDatabase(),loadChatOpsWebhookConfig()]);
}catch(e){toast(e.message,'error');}
}
function toggleAdvancedConfig(){
const els=document.querySelectorAll('.cfg-adv');
const btn=document.getElementById('cfg-adv-toggle');
const hidden=[...els].some(el=>el.classList.contains('hidden'));
els.forEach(el=>el.classList.toggle('hidden',!hidden));
if(btn)btn.textContent=hidden?'Ocultar avanzado':'Mostrar avanzado';
}
// ---- Modo de operación (radio-cards) ----
function selectedOpMode(){const r=document.querySelector('input[name="cfg-op-mode"]:checked');return r?r.value:'Autonomous';}
function setOpModeRadio(mode){
let found=false;
document.querySelectorAll('input[name="cfg-op-mode"]').forEach(r=>{r.checked=(r.value.toLowerCase()===String(mode).toLowerCase());if(r.checked)found=true;});
if(!found){const def=document.querySelector('input[name="cfg-op-mode"][value="Autonomous"]');if(def)def.checked=true;}
syncOpModeCards();
}
function syncOpModeCards(){
document.querySelectorAll('.cfg-op-card').forEach(card=>{
const r=card.querySelector('input[type="radio"]');
const sel=!!(r&&r.checked);
card.style.borderColor=sel?'rgba(34,211,238,0.65)':'var(--border-color)';
card.style.boxShadow=sel?'0 0 0 2px rgba(34,211,238,0.22)':'';
});
}
async function saveOpMode(){
if(!s.tenantId)return;
try{
const modeMap={Shadow:0,Assisted:1,Autonomous:2};
const payload=Object.assign({},cfgAgentFull||{},{enabled:document.getElementById('cfg-agent-enabled').checked,operatingMode:modeMap[selectedOpMode()]??2});
await patchTenantPath('/config/agent',payload);
cfgAgentFull=payload;
updateOperatingModePills(selectedOpMode());
toast('Modo de operación guardado','success');
}catch(e){toast(e.message,'error');}
}
// ---- Admin settings (asistente / WhatsApp / derivación) ----
async function loadAdminSettings(){
if(!s.tenantId)return;
try{
const d=await api('GET','/tenants/'+s.tenantId+'/admin-settings');
if(!d)return;
const el=id=>document.getElementById(id);
const as=d.assistant||{};
if(el('cfg-assistant-name'))el('cfg-assistant-name').value=as.name||'';
if(el('cfg-tone')&&as.tone)el('cfg-tone').value=as.tone;
if(el('cfg-language')&&as.language)el('cfg-language').value=as.language;
const m=d.messages||{};
if(el('cfg-msg-welcome'))el('cfg-msg-welcome').value=m.welcome||'';
if(el('cfg-msg-fallback'))el('cfg-msg-fallback').value=m.fallback||'';
if(el('cfg-msg-handoff'))el('cfg-msg-handoff').value=m.handoff||'';
const w=d.whatsApp||{};
if(el('cfg-wa-enabled'))el('cfg-wa-enabled').checked=!!w.enabled;
if(el('cfg-wa-phone'))el('cfg-wa-phone').value=w.phoneNumber||'';
if(el('cfg-wa-phoneid'))el('cfg-wa-phoneid').value=w.phoneNumberId||'';
if(el('cfg-wa-buttons'))el('cfg-wa-buttons').checked=w.interactiveButtons!==false;
if(el('cfg-wa-menu'))el('cfg-wa-menu').checked=w.interactiveMenu!==false;
const wth=el('cfg-wa-token-state');
if(wth)wth.textContent=w.hasAccessToken?'Token de acceso (guardado, dejar vacío para no cambiar)':'Token de acceso';
const h=d.handoff||{};
if(el('cfg-ho-frust'))el('cfg-ho-frust').checked=!!h.frustrationEscalation;
if(el('cfg-ho-retries'))el('cfg-ho-retries').value=String(h.maxRetriesBeforeHandoff??3);
if(el('cfg-ho-sla'))el('cfg-ho-sla').value=String(h.slaMinutes??30);
handoffKeywords=Array.isArray(h.triggerKeywords)?[...h.triggerKeywords]:[];
renderHandoffKeywordChips();
}catch(e){console.warn('admin-settings no disponible:',e);}
}
async function saveAssistantSection(){
if(!s.tenantId)return;
try{
const el=id=>document.getElementById(id);
await patchTenantPath('/admin-settings',{assistant:{name:(el('cfg-assistant-name').value||'').trim()||null,tone:el('cfg-tone').value,language:el('cfg-language').value},messages:{welcome:el('cfg-msg-welcome').value||'',fallback:el('cfg-msg-fallback').value||'',handoff:el('cfg-msg-handoff').value||''}});
toast('Asistente guardado','success');
}catch(e){toast(e.message,'error');}
}
async function saveWhatsAppSection(){
if(!s.tenantId)return;
try{
const el=id=>document.getElementById(id);
const w={enabled:el('cfg-wa-enabled').checked,phoneNumber:(el('cfg-wa-phone').value||'').trim()||null,phoneNumberId:(el('cfg-wa-phoneid').value||'').trim()||null,interactiveButtons:el('cfg-wa-buttons').checked,interactiveMenu:el('cfg-wa-menu').checked};
const tok=(el('cfg-wa-token').value||'').trim();
if(tok)w.accessToken=tok;
await patchTenantPath('/admin-settings',{whatsApp:w});
el('cfg-wa-token').value='';
const th=el('cfg-wa-token-state');
if(tok&&th)th.textContent='Token de acceso (guardado, dejar vacío para no cambiar)';
toast('Canal WhatsApp guardado','success');
}catch(e){toast(e.message,'error');}
}
async function saveHandoffSection(){
if(!s.tenantId)return;
try{
const el=id=>document.getElementById(id);
await patchTenantPath('/admin-settings',{handoff:{frustrationEscalation:el('cfg-ho-frust').checked,triggerKeywords:[...handoffKeywords],maxRetriesBeforeHandoff:parseInt(el('cfg-ho-retries').value,10)||3,slaMinutes:parseInt(el('cfg-ho-sla').value,10)||30}});
toast('Derivación a humanos guardada','success');
}catch(e){toast(e.message,'error');}
}
// ---- Chips (keywords de derivación / tablas bloqueadas) ----
function renderChips(containerId,arr,removeFn){
const c=document.getElementById(containerId);if(!c)return;
c.innerHTML=arr.map((k,i)=>`<span class="inline-flex items-center gap-1.5 text-xs px-2.5 py-1 rounded-full bg-cyan-500/10 text-cyan-300 border border-cyan-500/25">${esc(k)}<button type="button" onclick="${removeFn}(${i})" class="font-bold leading-none hover:text-red-400" title="Quitar">×</button></span>`).join('');
}
function addChipFromInput(inputId,arr,containerId,removeFn){
const inp=document.getElementById(inputId);if(!inp)return;
const v=(inp.value||'').trim();
if(!v)return;
if(!arr.includes(v))arr.push(v);
inp.value='';
renderChips(containerId,arr,removeFn);
}
function renderHandoffKeywordChips(){renderChips('cfg-ho-keywords-chips',handoffKeywords,'removeHandoffKeyword');}
function addHandoffKeyword(){addChipFromInput('cfg-ho-keywords',handoffKeywords,'cfg-ho-keywords-chips','removeHandoffKeyword');}
function removeHandoffKeyword(i){handoffKeywords.splice(i,1);renderHandoffKeywordChips();}
function renderDiagBlockedChips(){renderChips('cfg-diag-blocked-chips',diagBlockedTables,'removeDiagBlocked');}
function addDiagBlocked(){addChipFromInput('cfg-diag-blocked',diagBlockedTables,'cfg-diag-blocked-chips','removeDiagBlocked');}
function removeDiagBlocked(i){diagBlockedTables.splice(i,1);renderDiagBlockedChips();}
// ---- Repositorio Git y deploys ----
async function loadDeployGit(cfg){
const el=id=>document.getElementById(id);
let d=null;let endpointOk=false;
try{d=await api('GET','/tenants/'+s.tenantId+'/config/deploy-git');endpointOk=!!d;}
catch(e){d=(cfg&&cfg.deployGit)||null;}
d=d||{};
// El GET dedicado no devuelve workingDirectoryRelativePath — lo preservamos desde /config.
if(d.workingDirectoryRelativePath==null&&cfg&&cfg.deployGit&&cfg.deployGit.workingDirectoryRelativePath)d.workingDirectoryRelativePath=cfg.deployGit.workingDirectoryRelativePath;
cfgDeployGitFull=d;
el('cfg-auto-deploy').checked=d.autoDeployEnabled!==false;
el('cfg-manual-deploy').checked=d.manualDeployAllowed!==false;
el('cfg-git-url').value=d.gitRepositoryUrl||'';
el('cfg-git-branch').value=d.gitDefaultBranch||'main';
const th=el('cfg-git-token-state');
if(th){
if(!endpointOk){th.classList.add('hidden');}
else{th.classList.remove('hidden');th.textContent=d.hasAccessToken?'Token de acceso — guardado (dejar vacío para no cambiar)':'Opcional, para repositorios privados';}
}
}
async function saveDeployGitSection(){
if(!s.tenantId)return;
try{
const el=id=>document.getElementById(id);
const p={autoDeployEnabled:el('cfg-auto-deploy').checked,manualDeployAllowed:el('cfg-manual-deploy').checked,gitRepositoryUrl:(el('cfg-git-url').value||'').trim()||null,gitDefaultBranch:(el('cfg-git-branch').value||'').trim()||'main',clearGitAccessToken:false};
if(cfgDeployGitFull&&cfgDeployGitFull.workingDirectoryRelativePath)p.workingDirectoryRelativePath=cfgDeployGitFull.workingDirectoryRelativePath;
const tok=(el('cfg-git-token').value||'').trim();
if(tok)p.gitAccessToken=tok;
await patchTenantPath('/config/deploy-git',p);
el('cfg-git-token').value='';
const th=el('cfg-git-token-state');
if(tok&&th){th.textContent='Token de acceso — guardado';th.classList.remove('hidden');}
toast('Repositorio y deploys guardados','success');
}catch(e){toast(e.message,'error');}
}
async function testDeployGit(){
if(!s.tenantId){toast('Selecciona una cuenta primero','warning');return;}
try{
const res=await api('POST','/tenants/'+s.tenantId+'/config/deploy-git/test');
if(res&&res.ok===false)toast(res.message||'La conexión con el repositorio falló','error');
else toast((res&&res.message)||'Conexión con el repositorio verificada','success');
}catch(e){toast(e.message||'No se pudo probar la conexión','error');}
}
// ---- Base de datos del negocio ----
async function loadTargetDatabase(){
if(!s.tenantId)return;
const st=document.getElementById('cfg-db-status');
if(!st)return;
try{
const d=await api('GET','/tenants/'+s.tenantId+'/config/target-database');
if(d&&d.configured){
const parts=[d.host,d.database?('base "'+d.database+'"'):null,d.username?('usuario '+d.username):null].filter(Boolean).join(' · ');
st.innerHTML='<span class="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full border border-emerald-500/30 text-emerald-300"><span class="w-1.5 h-1.5 rounded-full bg-emerald-400"></span>Configurada — '+esc(parts||'detalles no disponibles')+'</span>';
}else{
st.innerHTML='<span class="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full border border-amber-500/30 text-amber-300"><span class="w-1.5 h-1.5 rounded-full bg-amber-400"></span>Sin configurar — el asistente solo ve la base de la plataforma</span>';
}
}catch(e){st.innerHTML='';}
}
async function saveTargetDatabase(){
if(!s.tenantId)return;
const inp=document.getElementById('cfg-db-conn');
const cs=(inp&&inp.value||'').trim();
if(!cs){toast('Ingresá la cadena de conexión','warning');return;}
try{
await api('PUT','/tenants/'+s.tenantId+'/config/target-database',{connectionString:cs});
inp.value='';
await loadTargetDatabase();
toast('Base de datos configurada y verificada','success');
}catch(e){toast(e.message||'No se pudo conectar a la base','error');}
}
async function clearTargetDatabase(){
if(!s.tenantId)return;
const ok=await askConfirm({title:'Quitar base de datos',detail:'El asistente dejará de diagnosticar y corregir sobre la base del negocio.',confirmLabel:'Quitar',danger:true});
if(!ok)return;
try{
await api('PUT','/tenants/'+s.tenantId+'/config/target-database',{connectionString:''});
const inp=document.getElementById('cfg-db-conn');if(inp)inp.value='';
await loadTargetDatabase();
toast('Base de datos desvinculada','success');
}catch(e){toast(e.message,'error');}
}
// ---- Diagnósticos ----
async function saveDiagnosticsSection(){
if(!s.tenantId)return;
try{
const el=id=>document.getElementById(id);
const raw=(el('cfg-diag-budget').value||'').trim();
const parsed=parseInt(raw,10);
const budget=raw&&!isNaN(parsed)?Math.min(500,Math.max(1,parsed)):null;
await patchTenantPath('/config/diagnostics',{blockedTables:[...diagBlockedTables],allowSensitiveTables:el('cfg-diag-sensitive').checked,maxDiagnosticQueriesPerIncident:budget});
toast('Diagnósticos guardados','success');
}catch(e){toast(e.message,'error');}
}
// ---- LLM (solo SelfHosted) ----
function buildLlmPayload(){return{provider:document.getElementById('cfg-llm-provider').value||'openai',model:document.getElementById('cfg-llm-model').value||'gpt-4',temperature:parseFloat(document.getElementById('cfg-llm-temp').value)||0.7,topP:null,maxOutputTokens:parseInt(document.getElementById('cfg-llm-max-out').value,10)||2000};}
async function saveLlmSection(){if(!s.tenantId)return;try{await patchTenantPath('/config/llm',buildLlmPayload());toast('LLM guardado','success');}catch(e){toast(e.message,'error');}}
function resetConfig(){
lc();
toast('Datos recargados desde servidor','info');
}

// ==========================================
// Reglas del agente (políticas en lenguaje natural)
// ==========================================
const POLICY_ACTIONS={
allow:{label:'Permite',cls:'bg-emerald-500/15 text-emerald-400 border-emerald-500/30'},
block:{label:'Bloquea',cls:'bg-red-500/15 text-red-400 border-red-500/30'},
require_approval:{label:'Exige aprobación',cls:'bg-amber-500/15 text-amber-400 border-amber-500/30'},
escalate:{label:'Escala a humano',cls:'bg-violet-500/15 text-violet-400 border-violet-500/30'},
notify:{label:'Notifica',cls:'bg-cyan-500/15 text-cyan-400 border-cyan-500/30'}
};
let pendingPolicyRule=null;
let policyDragId=null;
function policyBadge(action){
const b=POLICY_ACTIONS[action]||{label:action||'Regla',cls:'bg-gray-500/15 text-gray-400 border-gray-500/30'};
return `<span class="text-[10px] px-2 py-0.5 rounded-full border font-semibold flex-shrink-0 ${b.cls}">${esc(b.label)}</span>`;
}
function renderPolicies(policies){
const el=document.getElementById('cfg-policies-list');
if(!el)return;
const items=Array.isArray(policies)?policies:[];
s.policies=items;
if(!items.length){
el.innerHTML='<div class="text-xs py-6 text-center rounded-xl border border-dashed" style="border-color:var(--border-color);color:var(--text-secondary)">Sin reglas todavía — escribí una abajo y HiveOps la interpreta</div>';
return;
}
el.innerHTML=items.map(r=>{
const id=esc(String(r.id||''));
const active=r.active!==false;
const main=r.humanReadable||r.originalText||'Regla';
const orig=(r.originalText&&r.originalText!==r.humanReadable)?`<div class="text-[11px] mt-0.5" style="color:var(--text-tertiary)">“${esc(r.originalText)}”</div>`:'';
return `<div class="policy-row p-3 rounded-xl border flex items-center gap-3 ${active?'':'opacity-50'}" draggable="true" data-rule-id="${id}" style="background:var(--bg-tertiary);border-color:var(--border-color)" ondragstart="policyDragStart(event)" ondragover="policyDragOver(event)" ondragleave="policyDragLeave(event)" ondrop="policyDrop(event)" ondragend="policyDragEnd()">
  <span class="cursor-grab select-none flex-shrink-0 text-base leading-none" style="color:var(--text-tertiary)" title="Arrastrá para reordenar">≡</span>
  ${policyBadge(r.action)}
  <div class="min-w-0 flex-1">
    <div class="text-sm" style="color:var(--text-primary)">${esc(main)}</div>
    ${orig}
  </div>
  <label class="flex items-center gap-1.5 text-[11px] cursor-pointer flex-shrink-0" style="color:var(--text-tertiary)" title="Activar / desactivar"><input type="checkbox" class="accent-cyan-500" ${active?'checked':''} onchange="togglePolicy('${id}',this.checked)"/>Activa</label>
  <button type="button" onclick="deletePolicy('${id}')" class="text-xs px-2 py-1 rounded-lg text-red-400 hover:bg-red-500/10 border border-red-500/25 flex-shrink-0 transition-all" title="Eliminar regla">✕</button>
</div>`;
}).join('');
}
function policySuggestion(btn){
const inp=document.getElementById('cfg-policy-input');
if(!inp)return;
inp.value=btn.textContent.trim();
inp.focus();
}
async function interpretPolicy(){
if(!s.tenantId){toast('Selecciona una cuenta primero','warning');return;}
const inp=document.getElementById('cfg-policy-input');
const txt=((inp&&inp.value)||'').trim();
if(!txt){toast('Escribí una regla primero','warning');return;}
const box=document.getElementById('cfg-policy-preview');
if(!box)return;
box.classList.remove('hidden');
box.innerHTML='<div class="text-xs" style="color:var(--text-secondary)">Interpretando…</div>';
pendingPolicyRule=null;
try{
const d=await api('POST','/tenants/'+s.tenantId+'/policies/interpret',{text:txt});
if(!d||d.ok===false){
box.innerHTML=`<div class="text-xs text-red-400">${esc((d&&d.error)||'No se pudo interpretar la regla')}</div>`;
return;
}
pendingPolicyRule={rule:d.rule,insertBeforeRuleId:d.suggestedInsertBeforeRuleId||null};
const warns=(d.warnings||[]).map(w=>`<li>${esc(w)}</li>`).join('');
box.innerHTML=`<div class="flex items-start gap-2 flex-wrap">${policyBadge(d.rule&&d.rule.action)}<div class="text-sm flex-1 min-w-0" style="color:var(--text-primary)">${esc((d.rule&&d.rule.humanReadable)||txt)}</div></div>
${warns?`<ul class="list-disc pl-5 mt-2 space-y-0.5 text-xs text-amber-400">${warns}</ul>`:''}
<div class="flex gap-2 mt-3">
<button type="button" onclick="confirmAddPolicy()" class="btn-primary px-4 py-1.5 rounded-xl text-xs">Agregar regla</button>
<button type="button" onclick="discardPolicyPreview()" class="btn-secondary px-4 py-1.5 rounded-xl text-xs">Descartar</button>
</div>`;
}catch(e){
box.innerHTML=`<div class="text-xs text-red-400">${esc(e.message)}</div>`;
}
}
async function confirmAddPolicy(){
if(!s.tenantId||!pendingPolicyRule)return;
try{
const cfg=await api('POST','/tenants/'+s.tenantId+'/policies',{rule:pendingPolicyRule.rule,insertBeforeRuleId:pendingPolicyRule.insertBeforeRuleId});
renderPolicies(cfg&&cfg.policies||[]);
discardPolicyPreview();
const inp=document.getElementById('cfg-policy-input');
if(inp)inp.value='';
toast('Regla agregada','success');
}catch(e){toast(e.message,'error');}
}
function discardPolicyPreview(){
pendingPolicyRule=null;
const box=document.getElementById('cfg-policy-preview');
if(box){box.classList.add('hidden');box.innerHTML='';}
}
async function togglePolicy(id,active){
if(!s.tenantId)return;
try{
const cfg=await patchTenantPath('/policies/'+encodeURIComponent(id),{active:!!active});
renderPolicies(cfg&&cfg.policies||[]);
toast(active?'Regla activada':'Regla desactivada','success');
}catch(e){toast(e.message,'error');renderPolicies(s.policies||[]);}
}
async function deletePolicy(id){
if(!s.tenantId)return;
if(!confirm('¿Eliminar esta regla?'))return;
try{
const cfg=await api('DELETE','/tenants/'+s.tenantId+'/policies/'+encodeURIComponent(id));
renderPolicies(cfg&&cfg.policies||[]);
toast('Regla eliminada','success');
}catch(e){toast(e.message,'error');}
}
function policyRowOf(e){return e.target&&e.target.closest?e.target.closest('.policy-row'):null;}
function policyDragStart(e){
const row=policyRowOf(e);if(!row)return;
policyDragId=row.dataset.ruleId;
e.dataTransfer.effectAllowed='move';
try{e.dataTransfer.setData('text/plain',policyDragId);}catch(_){}
row.classList.add('opacity-40');
}
function policyDragOver(e){
const row=policyRowOf(e);
if(!row||row.dataset.ruleId===policyDragId)return;
e.preventDefault();
e.dataTransfer.dropEffect='move';
row.classList.add('border-cyan-500/60');
}
function policyDragLeave(e){
const row=policyRowOf(e);
if(row)row.classList.remove('border-cyan-500/60');
}
async function policyDrop(e){
const row=policyRowOf(e);
if(!row)return;
e.preventDefault();
document.querySelectorAll('#cfg-policies-list .policy-row').forEach(r=>r.classList.remove('border-cyan-500/60'));
if(!policyDragId||row.dataset.ruleId===policyDragId)return;
const list=document.getElementById('cfg-policies-list');
const dragRow=[...list.querySelectorAll('.policy-row')].find(r=>r.dataset.ruleId===policyDragId);
if(!dragRow)return;
const rect=row.getBoundingClientRect();
const before=(e.clientY-rect.top)<rect.height/2;
list.insertBefore(dragRow,before?row:row.nextSibling);
const ids=[...list.querySelectorAll('.policy-row')].map(r=>r.dataset.ruleId);
await savePolicyOrder(ids);
}
function policyDragEnd(){
policyDragId=null;
document.querySelectorAll('#cfg-policies-list .policy-row').forEach(r=>r.classList.remove('border-cyan-500/60','opacity-40'));
}
async function savePolicyOrder(ids){
if(!s.tenantId)return;
try{
const cfg=await patchTenantPath('/policies/reorder',{ruleIds:ids});
renderPolicies(cfg&&cfg.policies||[]);
toast('Orden de reglas guardado','success');
}catch(e){toast(e.message,'error');renderPolicies(s.policies||[]);}
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
if(!s.tenantId&&!s.isAdmin){document.getElementById('notif-list').innerHTML='<div class="p-4 text-center text-sm" style="color:var(--text-secondary)">Selecciona una cuenta</div>';return;}
const d=await api('GET','/support/notifications');
const readMarker=localStorage.getItem('notifReadAt:'+(s.tenantId||'platform'));
const readBefore=readMarker?new Date(readMarker):null;
notifications=(d||[]).map(n=>Object.assign({},n,{read:!!(readBefore&&new Date(n.createdAt)<=readBefore)}));
const nl=document.getElementById('notif-list');
if(!notifications.length){nl.innerHTML='<div class="p-4 text-center text-sm" style="color:var(--text-secondary)">Sin notificaciones nuevas</div>';}
else{nl.innerHTML=notifications.map(n=>`<div class="p-3 hover:bg-white/5 cursor-pointer transition-colors border-b" style="border-color:var(--border-color)" onclick="${n.type==='incident'?`od('${n.incidentId}');toggleNotifications();`:`n('incidents');toggleNotifications();`}"><div class="flex items-center gap-2 mb-1"><div class="w-1.5 h-1.5 rounded-full ${n.severity==='Critical'?'bg-red-500':n.severity==='High'?'bg-amber-500':'bg-amber-500/60'}"></div><span class="text-xs font-medium" style="color:var(--text-primary)">${n.title||'Notificación'}</span></div><div class="text-xs" style="color:var(--text-tertiary)">${timeAgo(n.createdAt)}</div></div>`).join('');}
const badge=document.getElementById('notif-badge');
if(notifications.filter(n=>!n.read).length>0){badge.classList.remove('hidden');badge.classList.add('status-pulse');}
else{badge.classList.add('hidden');badge.classList.remove('status-pulse');}
}catch(e){showWidgetError('notif-list','Error cargando notificaciones','loadNotifications()');}
}
function markAllRead(){localStorage.setItem('notifReadAt:'+(s.tenantId||'platform'),new Date().toISOString());notifications.forEach(n=>n.read=true);document.getElementById('notif-badge').classList.add('hidden');document.getElementById('notif-badge').classList.remove('status-pulse');}

// ==========================================
// AI Support Engineer v1.2 — Readiness, ROI, Operating Mode, Playbooks, Evidence Pack
// ==========================================

function updateOperatingModePills(activeMode) {
  const mode = activeMode || 'Assisted';
  s.operatingMode = mode;
  setOpModeRadio(mode);
  const modeTips = {
    Shadow: 'Modo observador: el agente diagnostica y propone soluciones, pero nunca modifica datos.',
    Assisted: 'Modo asistido: el agente diagnostica y pide aprobación humana antes de aplicar cualquier cambio.',
    Autonomous: 'Modo autónomo: el agente resuelve solo, siempre con copias de seguridad previas y límites de seguridad.'
  };
  ['Shadow', 'Assisted', 'Autonomous'].forEach(m => {
    const btn = document.getElementById('mode-btn-' + m);
    if (!btn) return;
    btn.title = modeTips[m] || '';
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
    Shadow: 'Modo observador: el agente diagnostica y propone soluciones, pero nunca modifica datos.',
    Assisted: 'Modo asistido: el agente diagnostica y pide aprobación humana antes de aplicar cualquier cambio.',
    Autonomous: 'Modo autónomo: el agente resuelve solo, siempre con copias de seguridad previas y límites de seguridad.'
  };
  try {
    await api('PUT', '/support/readiness/operating-mode', { operatingMode: mode });
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
    s.outcomes = o || null;
    if (o) {
      const staleRetry = document.getElementById('roi-retry-btn');
      if (staleRetry) staleRetry.remove();
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
        elAud.setAttribute('data-tip', 'Cadena de eventos firmada SHA-256, verificable como evidencia');
        elAud.className = intact
          ? 'text-xs px-2.5 py-1 rounded-full bg-emerald-500/15 text-emerald-400 border border-emerald-500/20 font-medium'
          : 'text-xs px-2.5 py-1 rounded-full bg-amber-500/15 text-amber-400 border border-amber-500/20 font-medium';
      }

      updateOperatingModePills(o.operatingMode || 'Assisted');
    }
  } catch (e) {
    console.warn('Outcomes load error:', e);
    s.outcomes = null;
    updateOperatingModePills('Assisted');
    const elAud = document.getElementById('roi-audit-badge');
    if (elAud) {
      elAud.textContent = 'Sin datos de ROI';
      elAud.className = 'text-xs px-2.5 py-1 rounded-full bg-red-500/15 text-red-400 border border-red-500/20 font-medium';
      let retry = document.getElementById('roi-retry-btn');
      if (!retry && elAud.parentElement) {
        retry = document.createElement('button');
        retry.id = 'roi-retry-btn';
        retry.type = 'button';
        retry.className = 'btn-secondary px-3 py-1.5 rounded-lg text-xs';
        retry.textContent = 'Reintentar';
        retry.setAttribute('onclick', 'loadReadinessAndOutcomes()');
        elAud.parentElement.appendChild(retry);
      }
    }
  }

  // 2. Load Readiness Check
  try {
    const r = await api('GET', '/support/readiness/check');
    s.readiness = r || null;
    const badge = document.getElementById('rd-status-badge');
    const listEl = document.getElementById('rd-checks-list');
    if (!r || !listEl) return;

    const isReady = r.ready ?? r.readyForAutonomousSupport;
    if (badge) {
      if (isReady) {
        badge.textContent = 'LISTO';
        badge.className = 'text-xs px-2.5 py-1 rounded-full bg-emerald-500/15 text-emerald-400 border border-emerald-500/20 font-semibold';
      } else {
        badge.textContent = 'REQUIERE CONFIG';
        badge.className = 'text-xs px-2.5 py-1 rounded-full bg-amber-500/15 text-amber-400 border border-amber-500/20 font-semibold';
      }
    }

    const labels = {
      targetDatabase: 'Base de datos del negocio',
      gitWorkspace: 'Repositorio y despliegues',
      knowledgeBase: 'Base de conocimiento (artículos publicados)',
      playbooks: 'Acciones automáticas configuradas',
      channels: 'Canales de notificación (Slack/Teams/WhatsApp)'
    };

    const actionLabels = {
      targetDatabase: 'Configurar DB →',
      gitWorkspace: 'Configurar Git →',
      knowledgeBase: 'Ir a KB →',
      playbooks: 'Crear Playbook →',
      channels: 'Configurar notificaciones →'
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
        const actionBtn = `<button type="button" onclick="jumpToReadinessFix('${esc(c.name)}')" class="text-[11px] px-2.5 py-1 rounded-lg ${ok ? 'btn-secondary' : 'bg-cyan-500/20 hover:bg-cyan-500/30 text-cyan-300 border border-cyan-500/30'} flex-shrink-0 transition-all">${esc(actionLabels[c.name] || 'Abrir →')}</button>`;
        return `<div class="flex items-center justify-between gap-3 p-2.5 rounded-xl border" style="background:var(--bg-tertiary);border-color:var(--border-color)" title="${esc(c.detail || '')}">
          <div class="flex items-start gap-3 min-w-0 flex-1">
            ${icon}
            <div class="min-w-0 flex-1">
              <div class="text-xs font-semibold" style="color:var(--text-primary)">${esc(title)}</div>
              <div class="text-[11px] truncate mt-0.5" style="color:var(--text-secondary)">${esc(c.detail || '')}</div>
            </div>
          </div>
          ${actionBtn}
        </div>`;
      }).join('');
    }
  } catch (e) {
    console.warn('Readiness check error:', e);
    s.readiness = null;
    showWidgetError('rd-checks-list', 'No se pudo verificar la configuración de la cuenta', 'loadReadinessAndOutcomes()');
  }
  renderSetupWizard();
}

function jumpToReadinessFix(checkName) {
  const highlight = id => {
    setTimeout(() => {
      const el = document.getElementById(id);
      if (!el) return;
      el.scrollIntoView({ behavior: 'smooth', block: 'center' });
      el.classList.add('ring-2', 'ring-cyan-400');
      setTimeout(() => el.classList.remove('ring-2', 'ring-cyan-400'), 2200);
    }, 220);
  };
  if (checkName === 'playbooks') {
    n('playbooks');
    setTimeout(() => togglePlaybookForm(true), 180);
  } else if (checkName === 'knowledgeBase') {
    n('kb');
  } else if (checkName === 'gitWorkspace') {
    n('config');
    setTimeout(() => {
      const els = document.querySelectorAll('.cfg-adv');
      if ([...els].some(e => e.classList.contains('hidden'))) toggleAdvancedConfig();
      document.getElementById('cfg-git-url')?.focus();
    }, 200);
  } else if (checkName === 'channels') {
    n('config');
    highlight('cfg-chatops-card');
  } else if (checkName === 'targetDatabase') {
    if (s.isAdmin && s.deploymentMode === 'SelfHosted') {
      n('admin');
      highlight('selfhosted-db-card');
    } else {
      n('config');
      highlight('cfg-runtime-card');
    }
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
      listEl.innerHTML = '<div class="hive-card p-8 text-center col-span-full" style="color:var(--text-secondary)">No hay playbooks activos configurados para esta cuenta. Creá el primero con "+ Nuevo Playbook".</div>';
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

function normalizeRequiredParamsJson(rawInput, sqlTemplate) {
  const trimmed = (rawInput || '').trim();
  if (trimmed) {
    if (trimmed.startsWith('[')) {
      try {
        const parsed = JSON.parse(trimmed);
        if (Array.isArray(parsed)) return JSON.stringify(parsed.map(x => String(x).replace(/^@/, '').trim()).filter(Boolean));
      } catch {}
    }
    const parts = trimmed.split(/[,;\s]+/).map(x => x.replace(/^@/, '').trim()).filter(Boolean);
    if (parts.length) return JSON.stringify([...new Set(parts)]);
  }
  const detected = [...(sqlTemplate || '').matchAll(/@([A-Za-z_][A-Za-z0-9_]*)/g)].map(m => m[1]);
  return JSON.stringify([...new Set(detected)]);
}

function autoDetectPlaybookParams() {
  const sql = (document.getElementById('pb-payload')?.value || '') + ' ' + (document.getElementById('pb-verify')?.value || '');
  const detected = [...new Set([...sql.matchAll(/@([A-Za-z_][A-Za-z0-9_]*)/g)].map(m => m[1]))];
  const paramsEl = document.getElementById('pb-params');
  if (paramsEl && detected.length) {
    paramsEl.value = JSON.stringify(detected);
  }
}

function fillPlaybookPreset(preset) {
  if (preset === 'unlock_order') {
    document.getElementById('pb-code').value = 'PB-UNLOCK-ORDER';
    document.getElementById('pb-name').value = 'Destrabar orden con pago confirmado';
    document.getElementById('pb-type').value = 'SqlTemplate';
    document.getElementById('pb-symptoms').value = 'orden trabada, orden bloqueada, pago aprobado sin despacho, stuck order';
    document.getElementById('pb-payload').value = 'UPDATE "Orders" SET "Status" = \'Ready\' WHERE "Id" = @OrderId AND "Status" = \'Locked\';';
    document.getElementById('pb-verify').value = 'SELECT "Id" FROM "Orders" WHERE "Id" = @OrderId AND "Status" = \'Locked\';';
    document.getElementById('pb-params').value = '["OrderId"]';
    document.getElementById('pb-maxrows').value = '5';
    document.getElementById('pb-approval').checked = false;
    toast('Ejemplo cargado. Ajustá tabla/columnas si hace falta y hacé clic en Guardar Playbook.', 'info');
  }
}

document.getElementById('f-pb')?.addEventListener('submit', async e => {
  e.preventDefault();
  try {
    const actionPayloadTemplate = document.getElementById('pb-payload').value.trim();
    const payload = {
      code: document.getElementById('pb-code').value.trim(),
      name: document.getElementById('pb-name').value.trim(),
      triggerSymptoms: document.getElementById('pb-symptoms').value.trim(),
      actionType: document.getElementById('pb-type').value,
      actionPayloadTemplate,
      requiredParametersJson: normalizeRequiredParamsJson(document.getElementById('pb-params').value, actionPayloadTemplate),
      verificationSqlQuery: document.getElementById('pb-verify').value.trim() || null,
      maxAffectedRows: parseInt(document.getElementById('pb-maxrows').value, 10) || 5,
      requiresApproval: document.getElementById('pb-approval').checked
    };
    await api('POST', '/support/playbooks', payload);
    toast('Playbook creado exitosamente', 'success');
    document.getElementById('f-pb').reset();
    togglePlaybookForm(false);
    loadPlaybooks();
    loadReadinessAndOutcomes();
  } catch (err) {
    toast(err.message || 'Error al crear playbook', 'error');
  }
});

async function deactivatePlaybook(id) {
  const ok = await askConfirm({
    title: 'Desactivar playbook',
    detail: 'El playbook dejará de estar disponible para remediación automática. Podés volver a crearlo cuando quieras.',
    confirmLabel: 'Desactivar',
    danger: true
  });
  if (!ok) return;
  try {
    await api('DELETE', `/support/playbooks/${encodeURIComponent(id)}`);
    toast('Playbook desactivado', 'success');
    loadPlaybooks();
    loadReadinessAndOutcomes();
  } catch (e) {
    toast(e.message || 'Error al desactivar playbook', 'error');
  }
}

function closePlaybookDryRun() {
  document.getElementById('pb-dryrun-card')?.classList.add('hidden');
}

async function dryRunPlaybook(id, code) {
  const pb = (s.playbooks || []).find(x => String(x.id) === String(id));
  let paramNames = [];
  if (pb && pb.requiredParametersJson) {
    try {
      const parsed = JSON.parse(pb.requiredParametersJson);
      if (Array.isArray(parsed)) paramNames = parsed.map(x => String(x));
    } catch {}
  }
  const card = document.getElementById('pb-dryrun-card');
  const fieldsEl = document.getElementById('pb-dryrun-fields');
  const titleEl = document.getElementById('pb-dryrun-title');
  const resEl = document.getElementById('pb-dryrun-result');
  const execBtn = document.getElementById('pb-dryrun-exec-btn');

  if (!card || !fieldsEl || !execBtn) return;

  titleEl.textContent = `Probar Playbook ${code || ''} (Dry-Run sin commit)`;
  resEl.textContent = '';
  resEl.className = 'text-xs font-mono';

  if (!paramNames.length) {
    fieldsEl.innerHTML = '<div class="text-xs col-span-full" style="color:var(--text-secondary)">Este playbook no requiere parámetros. Hacé clic en Ejecutar Simulación Dry-Run.</div>';
  } else {
    fieldsEl.innerHTML = paramNames.map(p => `<div>
      <label class="text-xs block mb-1 font-mono text-cyan-400">@${esc(p)}</label>
      <input type="text" data-pb-param="${esc(p)}" class="input-field w-full px-3 py-2 rounded-xl text-xs font-mono" placeholder="Valor de prueba para ${esc(p)}..." value="1"/>
    </div>`).join('');
  }

  card.classList.remove('hidden');
  card.scrollIntoView({ behavior: 'smooth', block: 'center' });

  execBtn.onclick = async () => {
    const params = {};
    fieldsEl.querySelectorAll('[data-pb-param]').forEach(inp => {
      params[inp.getAttribute('data-pb-param')] = inp.value;
    });
    resEl.textContent = 'Ejecutando dry-run en transacción...';
    resEl.style.color = 'var(--text-secondary)';
    try {
      const res = await api('POST', `/support/playbooks/${encodeURIComponent(id)}/dry-run`, { parameters: params });
      if (res && res.success) {
        resEl.textContent = `✓ Dry-Run OK (${res.playbookCode || code}): ${res.affectedRows ?? 0} filas afectadas. ${res.summary || ''}`;
        resEl.className = 'text-xs font-mono text-emerald-400';
        toast(`Dry-Run OK: ${res.affectedRows ?? 0} filas`, 'success');
      } else {
        const msg = (res && (res.error || res.summary)) || 'Error en validación';
        resEl.textContent = `✕ ${msg}`;
        resEl.className = 'text-xs font-mono text-red-400';
        toast(`Dry-Run: ${msg}`, 'error');
      }
    } catch (e) {
      resEl.textContent = `✕ ${e.message || 'Error ejecutando Dry-Run'}`;
      resEl.className = 'text-xs font-mono text-red-400';
    }
  };
}

// ==========================================
// ChatOps Webhook Configuration (Slack / Teams / HTTP) & 1-Click Links
// ==========================================

function updateWebhookProviderBadge() {
  const badge = document.getElementById('cfg-wh-provider-badge');
  const url = (document.getElementById('cfg-wh-url')?.value || '').trim().toLowerCase();
  if (!badge) return;
  if (url.includes('hooks.slack.com')) {
    badge.textContent = 'Detectado: Slack Block Kit';
    badge.className = 'text-[11px] px-2.5 py-0.5 rounded-full bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 font-mono';
  } else if (url.includes('webhook.office.com') || url.includes('.logic.azure.com')) {
    badge.textContent = 'Detectado: Microsoft Teams Adaptive Card';
    badge.className = 'text-[11px] px-2.5 py-0.5 rounded-full bg-purple-500/20 text-purple-300 border border-purple-500/30 font-mono';
  } else if (url.startsWith('http://') || url.startsWith('https://')) {
    badge.textContent = 'Detectado: Webhook HTTP (HMAC-SHA256)';
    badge.className = 'text-[11px] px-2.5 py-0.5 rounded-full bg-cyan-500/20 text-cyan-300 border border-cyan-500/30 font-mono';
  } else {
    badge.textContent = 'Auto-detect (Slack / Teams / HTTP)';
    badge.className = 'text-[11px] px-2.5 py-0.5 rounded-full bg-cyan-500/15 text-cyan-300 border border-cyan-500/30 font-mono';
  }
}

async function loadChatOpsWebhookConfig() {
  if (!s.tenantId) return;
  try {
    const wh = await api('GET', `/tenants/${s.tenantId}/config/outbound-webhook`);
    if (!wh) return;
    const en = document.getElementById('cfg-wh-enabled');
    const url = document.getElementById('cfg-wh-url');
    const sec = document.getElementById('cfg-wh-secret');
    if (en) en.checked = !!wh.enabled;
    if (url) url.value = wh.url || '';
    if (sec) sec.value = wh.secret || '';
    const evts = new Set((wh.events || []).map(x => String(x).toLowerCase()));
    document.querySelectorAll('.cfg-wh-evt').forEach(cb => {
      cb.checked = evts.size === 0 ? true : evts.has(cb.value.toLowerCase());
    });
    updateWebhookProviderBadge();
  } catch (e) {
    console.warn('Could not load outbound webhook config:', e);
  }
}

async function saveChatOpsWebhook(silent) {
  if (!s.tenantId) {
    if (!silent) toast('Selecciona una cuenta primero', 'warning');
    return;
  }
  const events = [...document.querySelectorAll('.cfg-wh-evt:checked')].map(cb => cb.value);
  const payload = {
    enabled: !!document.getElementById('cfg-wh-enabled')?.checked,
    url: (document.getElementById('cfg-wh-url')?.value || '').trim() || null,
    secret: (document.getElementById('cfg-wh-secret')?.value || '').trim() || null,
    events
  };
  try {
    await api('PUT', `/tenants/${s.tenantId}/config/outbound-webhook`, payload);
    if (!silent) {
      toast('Configuración de notificaciones guardada', 'success');
      loadReadinessAndOutcomes();
    }
  } catch (e) {
    if (!silent) toast(e.message || 'Error guardando ChatOps Webhook', 'error');
  }
}

async function testChatOpsWebhook() {
  if (!s.tenantId) {
    toast('Selecciona una cuenta primero', 'warning');
    return;
  }
  await saveChatOpsWebhook(true);
  try {
    const res = await api('POST', `/tenants/${s.tenantId}/config/outbound-webhook/test`);
    toast((res && res.message) || 'Tarjeta de prueba enviada al webhook configurado', 'success');
  } catch (e) {
    toast(e.message || 'No se pudo enviar la prueba', 'error');
  }
}

async function showChatOpsLinks(incidentId) {
  const box = document.getElementById('chatops-links-box');
  if (!box) return;
  box.classList.remove('hidden');
  box.innerHTML = '<div class="text-xs" style="color:var(--text-secondary)">Generando links firmados HMAC-SHA256...</div>';
  try {
    const d = await api('GET', `/support/chatops/incidents/${encodeURIComponent(incidentId)}/links`);
    box.innerHTML = `<div class="p-3 rounded-xl border space-y-2" style="background:var(--bg-tertiary);border-color:var(--border-color)">
      <div class="text-xs font-semibold text-cyan-400">Enlaces Firmados 1-Click (válidos 24h sin login)</div>
      <div class="flex items-center justify-between gap-2 text-xs">
        <code class="truncate flex-1 p-1.5 rounded bg-black/30 text-emerald-300 font-mono">${esc(d.approveUrl || '')}</code>
        <button type="button" onclick="navigator.clipboard.writeText('${esc(d.approveUrl || '')}');toast('Link de Aprobación copiado','success')" class="btn-secondary px-2.5 py-1 rounded text-[11px]">Copiar Aprobar</button>
      </div>
      <div class="flex items-center justify-between gap-2 text-xs">
        <code class="truncate flex-1 p-1.5 rounded bg-black/30 text-red-300 font-mono">${esc(d.rejectUrl || '')}</code>
        <button type="button" onclick="navigator.clipboard.writeText('${esc(d.rejectUrl || '')}');toast('Link de Rechazo copiado','success')" class="btn-secondary px-2.5 py-1 rounded text-[11px]">Copiar Rechazar</button>
      </div>
    </div>`;
  } catch (e) {
    box.innerHTML = `<div class="text-xs text-red-400">${esc(e.message || 'Error generando links')}</div>`;
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
    ? `<span class="text-[11px] px-2 py-0.5 rounded-full border ${trackColors[track] || 'bg-stone-700 text-stone-300 border-stone-600'}" data-tip="Camino de remediación recomendado por el motor de causa raíz">Track: ${esc(track)}</span>`
    : '';

  const pbBadge = rca && rca.matchedPlaybookCode
    ? `<span class="text-[11px] px-2 py-0.5 rounded-full bg-emerald-500/15 text-emerald-300 border border-emerald-500/30 font-mono">Playbook: ${esc(rca.matchedPlaybookCode)}</span>`
    : '';

  let verBadge = '';
  const verTip = 'data-tip="Query que debe devolver 0 filas anómalas tras el fix"';
  if (inc.verificationPassed === true) {
    verBadge = `<span class="text-[11px] px-2 py-0.5 rounded-full bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 font-medium" ${verTip}>Verificado: 0 anomalías</span>`;
  } else if (inc.verificationPassed === false) {
    verBadge = `<span class="text-[11px] px-2 py-0.5 rounded-full bg-red-500/20 text-red-300 border border-red-500/30 font-medium" ${verTip}>Verificación fallida</span>`;
  } else if (hasVerification) {
    verBadge = `<span class="text-[11px] px-2 py-0.5 rounded-full bg-amber-500/20 text-amber-300 border border-amber-500/30 font-medium" ${verTip}>Verificación: Pendiente</span>`;
  }

  let arenaBadge = '';
  if (inc.arenaValidationStatus) {
    const st = inc.arenaValidationStatus;
    const cls = st === 'Survived'
      ? 'bg-emerald-500/20 text-emerald-300 border-emerald-500/30'
      : (st === 'Defeated' || st === 'Error')
        ? 'bg-red-500/20 text-red-300 border-red-500/30'
        : 'bg-cyan-500/20 text-cyan-300 border-cyan-500/30';
    const stLabel = st === 'Survived' ? 'Validado' : st === 'Defeated' ? 'Falló validación' : st;
    arenaBadge = `<span class="text-[11px] px-2 py-0.5 rounded-full border ${cls} font-mono" data-tip="HiveArena: entorno efímero que compila y testea el fix antes del PR. Survived = fix validado en entorno de pruebas; Defeated = reproducción del bug falló">HiveArena: ${esc(stLabel)}</span>`;
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
      panel.innerHTML = `<div class="text-xs uppercase tracking-wider mb-1" style="color:var(--text-tertiary)">Copias de seguridad de datos</div>
        <div class="text-xs" style="color:var(--text-secondary)">Sin copias de seguridad registradas para este incidente.</div>`;
      return;
    }

    panel.innerHTML = `<div class="text-xs uppercase tracking-wider mb-3 font-semibold" style="color:var(--text-tertiary)">Copias de seguridad de datos (${list.length}) — deshacer en 1 clic</div>
      <div class="space-y-4">
        ${list.map(snap => {
          const revertedBadge = snap.isReverted
            ? `<span class="text-[11px] px-2.5 py-1 rounded-full bg-amber-500/20 text-amber-300 border border-amber-500/30 font-medium">Revertido el ${snap.revertedAt ? new Date(snap.revertedAt).toLocaleString() : '—'}</span>`
            : `<button type="button" onclick="revertSnapshot('${esc(incidentId)}','${esc(snap.id)}')" class="px-3 py-1.5 rounded-lg bg-amber-500 hover:bg-amber-400 text-black font-semibold text-xs transition-all">Deshacer cambio</button>`;

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
    panel.innerHTML = `<div class="text-xs uppercase tracking-wider mb-1" style="color:var(--text-tertiary)">Snapshots de Datos</div>`;
    const errId = 'inc-snapshots-err';
    panel.innerHTML += `<div id="${errId}"></div>`;
    showWidgetError(errId, 'No se pudieron cargar los snapshots', `loadIncidentSnapshots('${incidentId}')`);
  }
}

async function revertSnapshot(incidentId, snapshotId) {
  const ok = await askConfirm({
    title: 'Deshacer cambio',
    detail: 'Se restaurarán los datos a la copia de seguridad previa al cambio.',
    preview: 'Las filas vuelven al estado capturado antes de la acción',
    confirmLabel: 'Revertir',
    danger: true
  });
  if (!ok) return;
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
      ? `<span class="text-[11px] px-2.5 py-1 rounded-full bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 font-medium" data-tip="Registro de eventos firmado y verificable como evidencia">Registro verificado (${total} eventos)</span>`
      : `<span class="text-[11px] px-2.5 py-1 rounded-full bg-red-500/20 text-red-300 border border-red-500/30 font-medium" data-tip="Registro de eventos firmado y verificable como evidencia">Registro con inconsistencia en el evento ${esc(String(chain.brokenAtEntryId || '?'))}</span>`;

    const entries = Array.isArray(chain.entries) ? chain.entries : [];
    panel.innerHTML = `<div class="flex flex-wrap items-center justify-between gap-2 mb-3">
        <div class="text-xs uppercase tracking-wider font-semibold" style="color:var(--text-tertiary)">Registro de auditoría verificable</div>
        <div class="flex flex-wrap items-center gap-2">
          ${badge}
          <button type="button" onclick="downloadAuditPack('${esc(incidentId)}')" class="btn-secondary px-2.5 py-1 rounded-lg text-[11px]">Descargar evidencia</button>
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
    panel.innerHTML = `<div class="text-xs uppercase tracking-wider mb-1" style="color:var(--text-tertiary)">Registro de auditoría verificable</div>`;
    const errId = 'inc-audit-err';
    panel.innerHTML += `<div id="${errId}"></div>`;
    showWidgetError(errId, 'No se pudo verificar la cadena de auditoría', `loadIncidentAuditChain('${incidentId}')`);
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

// ==========================================
// UX Overhaul: Mobile Nav, Setup Wizard, Confirm Modal, Error States
// ==========================================

// ---------- Mobile navigation ----------
function toggleMobileMenu() {
  const menu = document.getElementById('mobile-menu');
  const btn = document.getElementById('btn-mobile-nav');
  if (!menu) return;
  const willOpen = menu.classList.contains('hidden');
  menu.classList.toggle('hidden');
  if (btn) btn.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
  if (willOpen) {
    const desk = document.getElementById('tenant-select');
    const mob = document.getElementById('tenant-select-mobile');
    if (desk && mob) { mob.innerHTML = desk.innerHTML; mob.value = desk.value; }
    updateMobileAdminNav();
  }
}
function closeMobileMenu() {
  const menu = document.getElementById('mobile-menu');
  const btn = document.getElementById('btn-mobile-nav');
  if (menu) menu.classList.add('hidden');
  if (btn) btn.setAttribute('aria-expanded', 'false');
}
function mobileNav(v) { n(v); closeMobileMenu(); }
function changeTenantMobile() {
  const mob = document.getElementById('tenant-select-mobile');
  const desk = document.getElementById('tenant-select');
  if (mob && desk) desk.value = mob.value;
  changeTenant();
}
function updateMobileAdminNav() {
  const adm = document.getElementById('n-adm-mobile');
  if (!adm) return;
  if (s.isAdmin) adm.classList.remove('hidden');
  else adm.classList.add('hidden');
}

// ---------- Setup Wizard (onboarding checklist) ----------
function renderSetupWizard() {
  const card = document.getElementById('wizard-card');
  const stepsEl = document.getElementById('wizard-steps');
  const progEl = document.getElementById('wizard-progress');
  if (!card || !stepsEl) return;

  const checks = (s.readiness && Array.isArray(s.readiness.checks)) ? s.readiness.checks : [];
  const checkOk = name => checks.some(c => c.name === name && !!c.ok);
  const playbooksOk = checkOk('playbooks') || ((s.playbooks || []).length > 0);
  const mode = (s.outcomes && s.outcomes.operatingMode) || s.operatingMode || null;

  const steps = [
    {
      key: 'targetDatabase',
      title: 'Conectar base de datos del producto',
      desc: 'Vinculá la DB de la cuenta para introspección de esquema y snapshots Before/After.',
      ok: checkOk('targetDatabase'),
      cta: 'Configurar DB →',
      action: "jumpToReadinessFix('targetDatabase')"
    },
    {
      key: 'gitWorkspace',
      title: 'Clonar workspace Git de la cuenta',
      desc: 'El agente clona el repo para generar commits y Pull Requests de fixes L3.',
      ok: checkOk('gitWorkspace'),
      cta: 'Configurar Git →',
      action: "jumpToReadinessFix('gitWorkspace')"
    },
    {
      key: 'channels',
      title: 'Activar notificaciones (Slack/Teams) o WhatsApp',
      desc: 'Recibí alertas y aprobá soluciones con 1 clic desde tu canal de chat.',
      ok: checkOk('channels'),
      cta: 'Configurar notificaciones →',
      action: "jumpToReadinessFix('channels')"
    },
    {
      key: 'playbooks',
      title: 'Crear la primera acción automática',
      desc: 'Acción lista para ejecutar con simulación previa segura y verificación posterior.',
      ok: playbooksOk,
      cta: 'Crear acción →',
      action: "jumpToReadinessFix('playbooks')"
    },
    {
      key: 'shadow',
      title: 'Activar Modo Shadow (POC seguro)',
      desc: 'El agente propone y simula fixes durante 14 días sin escribir en producción.',
      ok: !!mode,
      cta: 'Activar Shadow',
      action: "setOperatingMode('Shadow')"
    }
  ];

  const done = steps.filter(x => x.ok).length;
  const dismissed = (() => { try { return localStorage.getItem('wizardDismissed') === '1'; } catch { return false; } })();
  if (done >= steps.length || dismissed) { card.classList.add('hidden'); return; }
  card.classList.remove('hidden');

  if (progEl) {
    const pct = Math.round((done / steps.length) * 100);
    progEl.innerHTML = `<div class="flex items-center justify-between text-[11px] mb-1.5">
      <span style="color:var(--text-secondary)">${done}/${steps.length} completados</span>
      <span class="font-semibold text-cyan-400">${pct}%</span>
    </div>
    <div class="h-1.5 rounded-full overflow-hidden" style="background:var(--bg-tertiary)">
      <div class="h-full rounded-full bg-cyan-500 transition-all" style="width:${pct}%"></div>
    </div>`;
  }

  stepsEl.innerHTML = steps.map(st => {
    const icon = st.ok
      ? '<span class="w-6 h-6 rounded-full bg-emerald-500/20 text-emerald-400 flex items-center justify-center flex-shrink-0 text-xs font-bold">✓</span>'
      : '<span class="w-6 h-6 rounded-full bg-red-500/15 text-red-400 flex items-center justify-center flex-shrink-0 text-xs font-bold">✕</span>';
    const btn = st.ok
      ? ''
      : `<button type="button" onclick="${st.action}" class="text-[11px] px-3 py-1.5 rounded-lg bg-cyan-500/20 hover:bg-cyan-500/30 text-cyan-300 border border-cyan-500/30 flex-shrink-0 transition-all">${esc(st.cta)}</button>`;
    return `<div class="flex items-center gap-3 p-3 rounded-xl border" style="background:var(--bg-tertiary);border-color:var(--border-color)">
      ${icon}
      <div class="min-w-0 flex-1">
        <div class="text-sm font-semibold" style="color:var(--text-primary)">${esc(st.title)}</div>
        <div class="text-xs mt-0.5" style="color:var(--text-secondary)">${esc(st.desc)}</div>
      </div>
      ${btn}
    </div>`;
  }).join('');

  const dismissBtn = document.getElementById('wizard-dismiss');
  if (dismissBtn && !dismissBtn.onclick) dismissBtn.onclick = dismissSetupWizard;
}

function dismissSetupWizard() {
  const card = document.getElementById('wizard-card');
  if (card) card.classList.add('hidden');
  try { localStorage.setItem('wizardDismissed', '1'); } catch {}
}

// ---------- Styled confirm modal ----------
let _confirmResolver = null;
function askConfirm(opts) {
  opts = opts || {};
  return new Promise(resolve => {
    const m = document.getElementById('m-confirm');
    if (!m) { resolve(false); return; }
    _confirmResolver = resolve;
    const t = document.getElementById('m-confirm-title');
    const d = document.getElementById('m-confirm-detail');
    const p = document.getElementById('m-confirm-preview');
    const bc = document.getElementById('m-confirm-cancel');
    const bk = document.getElementById('m-confirm-btn');
    if (t) t.textContent = opts.title || '¿Confirmar acción?';
    if (d) { d.textContent = opts.detail || ''; d.classList.toggle('hidden', !opts.detail); }
    if (p) { p.textContent = opts.preview || ''; p.classList.toggle('hidden', !opts.preview); }
    if (bc) bc.textContent = opts.cancelLabel || 'Cancelar';
    if (bk) {
      bk.textContent = opts.confirmLabel || 'Confirmar';
      bk.className = opts.danger
        ? 'px-5 py-2.5 rounded-xl text-sm font-semibold bg-red-600 hover:bg-red-500 text-white transition-all'
        : 'btn-primary px-5 py-2.5 rounded-xl text-sm';
    }
    m.classList.remove('hidden');
    m.classList.add('open');
  });
}
function resolveConfirm(v) {
  const m = document.getElementById('m-confirm');
  if (m) { m.classList.add('hidden'); m.classList.remove('open'); }
  const r = _confirmResolver;
  _confirmResolver = null;
  if (r) r(!!v);
}

// ---------- Error state helper ----------
function showWidgetError(elId, msg, retryFnName) {
  const el = document.getElementById(elId);
  if (!el) return;
  el.innerHTML = `<div class="p-6 text-center rounded-xl border border-red-500/30" style="background:rgba(239,68,68,0.06)">
    <svg class="w-10 h-10 mx-auto mb-2 text-red-400 opacity-70" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.5" d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126zM12 15.75h.007v.008H12v-.008z"/></svg>
    <div class="text-sm font-medium text-red-400 mb-1">${esc(msg || 'No se pudo cargar la información')}</div>
    <div class="text-xs mb-3" style="color:var(--text-tertiary)">Error de red o del servidor.</div>
    ${retryFnName ? `<button type="button" onclick="${retryFnName}" class="px-4 py-2 rounded-lg text-xs font-medium border border-red-500/30 text-red-300 hover:bg-red-500/10 transition-all">Reintentar</button>` : ''}
  </div>`;
}

// ---------- Global Escape handling & delegated bindings ----------
document.addEventListener('keydown', e => {
  if (e.key !== 'Escape') return;
  closeMobileMenu();
  const dr = document.getElementById('dr');
  if (dr && dr.classList.contains('open') && typeof cd === 'function') cd();
  const mc = document.getElementById('m-confirm');
  if (mc && (!mc.classList.contains('hidden') || mc.classList.contains('open'))) resolveConfirm(false);
});

document.addEventListener('click', e => {
  const id = e.target && e.target.id;
  if (id === 'm-confirm-btn') resolveConfirm(true);
  else if (id === 'm-confirm-cancel') resolveConfirm(false);
  else if (id === 'wizard-dismiss') dismissSetupWizard();
});

// ==========================================
// Contexto — Tenant Context Pack (Glosario, Documentos, Perfiles, Memoria)
// ==========================================

let ctxPack=null;

function ctxFetchOpts(method,body){
  const o={method,headers:{'Authorization':'Bearer '+(s.token||'')},credentials:'include'};
  if(s.tenantId)o.headers['X-Tenant-Id']=s.tenantId;
  if(body!==undefined){o.headers['Content-Type']='application/json';o.body=JSON.stringify(body);}
  return o;
}
function ctxParseJson(raw,fallback){
  if(raw==null)return fallback;
  if(typeof raw!=='string')return raw;
  try{return JSON.parse(raw);}catch{return fallback;}
}
function ctxList(v){
  if(v==null)return[];
  if(Array.isArray(v))return v;
  if(typeof v==='string')return v.split(/[\n,;]+/).map(x=>x.trim()).filter(Boolean);
  if(typeof v==='object')return Object.keys(v).map(k=>{const x=v[k];return (x&&typeof x==='object')?{name:k,...x}:{name:k,value:x};});
  return[v];
}
function ctxUpdateVersionBadge(){
  const v=document.getElementById('ctx-version');
  if(!v)return;
  v.textContent=ctxPack?('v'+(ctxPack.version??'—')+(ctxPack.updatedAtUtc?(' · actualizado '+timeAgo(ctxPack.updatedAtUtc)):'')):'—';
}
function ctxShowEmpty(html){
  const emptyEl=document.getElementById('ctx-empty');
  const grid=document.getElementById('ctx-grid');
  if(!emptyEl||!grid)return;
  grid.classList.add('hidden');
  emptyEl.classList.remove('hidden');
  emptyEl.innerHTML=html;
}
async function lctx(){
  const emptyEl=document.getElementById('ctx-empty');
  const grid=document.getElementById('ctx-grid');
  if(!emptyEl||!grid)return;
  if(!s.tenantId&&!s.isAdmin){
    ctxShowEmpty('<div class="text-sm" style="color:var(--text-secondary)">Seleccioná una cuenta para ver su contexto.</div>');
    return;
  }
  try{
    const r=await fetch(`${API}/support/context-pack`,ctxFetchOpts('GET'));
    if(r.status===401){logout();return;}
    if(r.status===404){
      ctxPack=null;
      ctxUpdateVersionBadge();
      ctxShowEmpty(`<svg class="w-12 h-12 mx-auto mb-3 opacity-30" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.5" d="M20.25 6.375c0 2.278-3.694 4.125-8.25 4.125S3.75 8.653 3.75 6.375m16.5 0c0-2.278-3.694-4.125-8.25-4.125S3.75 4.097 3.75 6.375m16.5 0v11.25c0 2.278-3.694 4.125-8.25 4.125s-8.25-1.847-8.25-4.125V6.375m16.5 3.75c0 2.278-3.694 4.125-8.25 4.125s-8.25-1.847-8.25-4.125"/></svg>
        <div class="text-sm mb-1 font-semibold" style="color:var(--text-primary)">El agente todavía no tiene contexto de esta cuenta</div>
        <div class="text-xs mb-5" style="color:var(--text-secondary)">Inicializá el Context Pack para escanear la base y empezar a cargar glosario, documentos y memoria.</div>
        <button type="button" id="ctx-init-btn" onclick="ctxInit()" class="btn-primary px-6 py-3 rounded-xl text-sm">Inicializar contexto</button>`);
    }else if(!r.ok){
      throw new Error(await r.text()||'Error cargando el contexto');
    }else{
      ctxPack=await r.json();
      emptyEl.classList.add('hidden');
      grid.classList.remove('hidden');
      ctxUpdateVersionBadge();
      renderCtxGlossary();
      renderCtxProfiles();
      renderCtxMemory();
      renderCtxDigest();
    }
  }catch(e){toast(e.message||'Error cargando el contexto','error');}
  await refreshCtxDocs();
  await limpr();
  await llt();
}
async function ctxInit(){
  const btn=document.getElementById('ctx-init-btn');
  try{
    if(btn){btn.disabled=true;btn.textContent='Inicializando…';}
    const pack=await api('POST','/support/context-pack/refresh-db');
    if(pack)ctxPack=pack;
    toast('Contexto inicializado','success');
  }catch(e){toast(e.message||'No se pudo inicializar el contexto','error');}
  await lctx();
}

// ---------- Glosario de dominio ----------
function ctxGlossaryEntries(){
  const g=ctxParseJson(ctxPack&&ctxPack.domainGlossaryJson,{});
  let entries=[];
  if(Array.isArray(g)){
    entries=g.map(x=>(x&&typeof x==='object')?{term:x.term||x.name||'',meaning:x.meaning||x.significado||x.description||'',tables:ctxList(x.tables||x.tablas),columns:ctxList(x.columns||x.columnas)}:{term:String(x),meaning:'',tables:[],columns:[]});
  }else if(g&&typeof g==='object'){
    const inner=(g.terms&&typeof g.terms==='object')?g.terms:g;
    Object.keys(inner).forEach(k=>{
      const v=inner[k];
      if(typeof v==='string')entries.push({term:k,meaning:v,tables:[],columns:[]});
      else if(v&&typeof v==='object')entries.push({term:v.term||k,meaning:v.meaning||v.description||'',tables:ctxList(v.tables||v.tablas),columns:ctxList(v.columns||v.columnas)});
    });
  }
  return entries.filter(e=>e.term);
}
async function ctxSaveGlossary(entries){
  const obj={};
  entries.forEach(e=>{obj[e.term]={meaning:e.meaning,tables:e.tables,columns:e.columns};});
  const pack=await api('PUT','/support/context-pack/sections/domainGlossary',{json:JSON.stringify(obj)});
  if(pack)ctxPack=pack;
  renderCtxGlossary();
  renderCtxDigest();
}
function renderCtxGlossary(){
  const tbl=document.getElementById('ctx-glossary-table');
  if(!tbl)return;
  const entries=ctxGlossaryEntries();
  if(!entries.length){
    tbl.innerHTML='<tbody><tr><td class="py-6 text-center text-sm" style="color:var(--text-secondary)">Sin términos todavía — agregá el primero abajo</td></tr></tbody>';
    return;
  }
  tbl.innerHTML=`<thead><tr class="text-left text-[11px] uppercase tracking-wider" style="color:var(--text-tertiary)">
    <th class="pb-2 pr-4 font-semibold">Término</th><th class="pb-2 pr-4 font-semibold">Significado</th><th class="pb-2 pr-4 font-semibold">Tablas</th><th class="pb-2 pr-4 font-semibold">Columnas</th><th class="pb-2 w-10"></th></tr></thead>
  <tbody>${entries.map(e=>`<tr class="border-t" style="border-color:var(--border-color)">
    <td class="py-2.5 pr-4 font-mono text-xs text-cyan-300 align-top">${esc(e.term)}</td>
    <td class="py-2.5 pr-4 text-sm align-top" style="color:var(--text-primary)">${esc(e.meaning||'—')}</td>
    <td class="py-2.5 pr-4 text-xs font-mono align-top" style="color:var(--text-secondary)">${esc(e.tables.join(', ')||'—')}</td>
    <td class="py-2.5 pr-4 text-xs font-mono align-top" style="color:var(--text-secondary)">${esc(e.columns.join(', ')||'—')}</td>
    <td class="py-2.5 align-top"><button type="button" onclick="ctxDeleteTerm(decodeURIComponent('${encodeURIComponent(e.term)}'))" class="delete-link p-1" title="Eliminar término" aria-label="Eliminar término"><svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"/></svg></button></td>
  </tr>`).join('')}</tbody>`;
}
async function ctxAddTerm(){
  const tEl=document.getElementById('ctx-term');
  const mEl=document.getElementById('ctx-meaning');
  const tbEl=document.getElementById('ctx-tables');
  const colEl=document.getElementById('ctx-columns');
  const term=(tEl.value||'').trim();
  const meaning=(mEl.value||'').trim();
  const tables=(tbEl.value||'').split(',').map(x=>x.trim()).filter(Boolean);
  const columns=(colEl.value||'').split(',').map(x=>x.trim()).filter(Boolean);
  if(!term){toast('Ingresá un término','error');return;}
  const entries=ctxGlossaryEntries().filter(e=>e.term.toLowerCase()!==term.toLowerCase());
  entries.push({term,meaning,tables,columns});
  try{
    await ctxSaveGlossary(entries);
    tEl.value='';mEl.value='';tbEl.value='';colEl.value='';
    toast('Término agregado al glosario','success');
  }catch(e){toast(e.message||'Error guardando el glosario','error');}
}
async function ctxDeleteTerm(term){
  const entries=ctxGlossaryEntries().filter(e=>e.term!==term);
  try{
    await ctxSaveGlossary(entries);
    toast('Término eliminado','success');
  }catch(e){toast(e.message||'Error guardando el glosario','error');}
}

// ---------- Documentos del proyecto ----------
async function refreshCtxDocs(){
  try{
    const docs=await api('GET','/support/context-pack/documents');
    s.ctxDocs=Array.isArray(docs)?docs:[];
  }catch{s.ctxDocs=s.ctxDocs||[];}
  renderCtxDocs();
}
function renderCtxDocs(){
  const el=document.getElementById('ctx-doc-list');
  if(!el)return;
  const docs=s.ctxDocs||[];
  if(!docs.length){
    el.innerHTML='<div class="text-xs py-4 text-center rounded-xl border border-dashed" style="border-color:var(--border-color);color:var(--text-secondary)">Sin documentos todavía — subí el primero para destilarlo</div>';
    return;
  }
  const badgeCls={Distilled:'bg-emerald-500/15 text-emerald-400 border-emerald-500/25',Pending:'bg-amber-500/15 text-amber-400 border-amber-500/25',Failed:'bg-red-500/15 text-red-400 border-red-500/25'};
  const badgeLbl={Distilled:'Destilado',Pending:'Pendiente',Failed:'Falló'};
  el.innerHTML=docs.map(d=>{
    const st=d.status||'Pending';
    const cls=badgeCls[st]||badgeCls.Pending;
    const lbl=badgeLbl[st]||st;
    const tip=d.distilledSummary?` data-tip="${esc(String(d.distilledSummary).replace(/"/g,'\''))}"`:'';
    return `<div class="flex items-center justify-between gap-3 p-3 rounded-xl border"${tip} style="background:var(--bg-tertiary);border-color:var(--border-color)">
      <div class="min-w-0 flex-1">
        <div class="flex items-center gap-2 min-w-0 flex-wrap">
          <span class="text-sm font-medium truncate" style="color:var(--text-primary)">${esc(d.fileName||'documento')}</span>
          <span class="text-[10px] px-2 py-0.5 rounded-full border ${cls} font-semibold uppercase tracking-wider flex-shrink-0">${esc(lbl)}</span>
        </div>
        <div class="text-[11px] mt-0.5" style="color:var(--text-tertiary)">${d.chunkCount??0} chunks${d.uploadedAtUtc?(' · '+timeAgo(d.uploadedAtUtc)):''}</div>
      </div>
      <button type="button" onclick="ctxDeleteDoc('${esc(String(d.id))}')" class="delete-link p-1.5 flex-shrink-0" title="Eliminar documento" aria-label="Eliminar documento"><svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"/></svg></button>
    </div>`;
  }).join('');
}
async function ctxUploadDoc(){
  const fi=document.getElementById('ctx-doc-file');
  const btn=document.getElementById('ctx-doc-upload-btn');
  const st=document.getElementById('ctx-doc-upload-status');
  const f=fi&&fi.files&&fi.files[0];
  if(!f){toast('Elegí un archivo primero','error');return;}
  if(f.size>512*1024){toast('El archivo supera el máximo de 512KB','error');return;}
  try{
    if(btn){btn.disabled=true;btn.textContent='Destilando…';}
    if(st)st.textContent='Subiendo y destilando '+f.name+'…';
    const text=await f.text();
    await api('POST','/support/context-pack/documents',{fileName:f.name,contentType:f.type||'text/plain',textContent:text});
    fi.value='';
    if(st)st.textContent='';
    toast('Documento subido — destilación en curso','success');
    await refreshCtxDocs();
  }catch(e){
    if(st)st.textContent='';
    toast(e.message||'Error subiendo el documento','error');
  }finally{
    if(btn){btn.disabled=false;btn.textContent='Subir y destilar';}
  }
}
async function ctxDeleteDoc(id){
  const ok=await askConfirm({title:'Eliminar documento',detail:'Se quitará el documento y sus chunks del contexto.',confirmLabel:'Eliminar',danger:true});
  if(!ok)return;
  try{
    await api('DELETE',`/support/context-pack/documents/${encodeURIComponent(id)}`);
    toast('Documento eliminado','success');
    refreshCtxDocs();
  }catch(e){toast(e.message||'Error eliminando el documento','error');}
}

// ---------- Perfil de datos y repositorios ----------
function renderCtxProfiles(){
  const dbEl=document.getElementById('ctx-db-profile');
  const rpEl=document.getElementById('ctx-repo-profiles');
  if(dbEl){
    const p=ctxParseJson(ctxPack&&ctxPack.databaseProfileJson,null);
    const tables=ctxList(p&&(p.criticalTables||p.critical_tables||p.tables));
    const conv=ctxList(p&&(p.conventions||p.namingConventions));
    const anomalies=ctxList(p&&(p.knownAnomalies||p.anomalies||p.dataQualityIssues));
    if(!p||(!tables.length&&!conv.length&&!anomalies.length)){
      dbEl.innerHTML='<div class="text-xs py-6 text-center rounded-xl border border-dashed" style="border-color:var(--border-color);color:var(--text-secondary)">Sin perfil — escaneá tu esquema</div>';
    }else{
      let html='';
      if(p.usesPlatformDatabase===true||p.usesPlatformDatabase==='true'){
        html+='<div class="flex items-start gap-2 p-2.5 mb-3 rounded-lg border border-amber-500/30" style="background:rgba(245,158,11,0.08)"><span class="text-amber-400 flex-shrink-0 mt-0.5"><svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126zM12 15.75h.007v.008H12v-.008z"/></svg></span><span class="text-[11px]" style="color:var(--text-secondary)">Este perfil corresponde a la base interna de la plataforma, no a tu base de datos. Configurá la <strong>Base de datos del negocio</strong> en Configuración y re-escaneá.</span></div>';
      }
      if(tables.length){
        html+='<div class="text-[11px] uppercase tracking-wider mb-2" style="color:var(--text-tertiary)">Tablas críticas</div><div class="space-y-1.5 mb-4">'+tables.map(t=>{
          const name=(t&&typeof t==='object')?(t.name||t.table||t.tableName||''):String(t);
          const purpose=(t&&typeof t==='object')?(t.purpose||t.description||t.notes||''):'';
          return `<div class="p-2.5 rounded-lg border" style="background:var(--bg-tertiary);border-color:var(--border-color)"><div class="text-xs font-mono font-semibold text-cyan-300">${esc(name)}</div>${purpose?`<div class="text-[11px] mt-0.5" style="color:var(--text-secondary)">${esc(purpose)}</div>`:''}</div>`;
        }).join('')+'</div>';
      }
      if(conv.length){
        html+='<div class="text-[11px] uppercase tracking-wider mb-2" style="color:var(--text-tertiary)">Convenciones</div><ul class="text-xs space-y-1 mb-4 list-disc pl-5" style="color:var(--text-secondary)">'+conv.map(c=>`<li>${esc(typeof c==='string'?c:((c&&typeof c==='object')?(c.name||c.value||JSON.stringify(c)):String(c)))}</li>`).join('')+'</ul>';
      }
      if(anomalies.length){
        html+='<div class="text-[11px] uppercase tracking-wider mb-2 text-amber-400">Anomalías conocidas</div><div class="space-y-1.5">'+anomalies.map(a=>{
          const txt=(a&&typeof a==='object')?(a.description||a.name||a.anomaly||JSON.stringify(a)):String(a);
          return `<div class="flex items-start gap-2 p-2 rounded-lg border border-amber-500/25" style="background:rgba(245,158,11,0.06)"><span class="text-amber-400 flex-shrink-0 mt-0.5"><svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126zM12 15.75h.007v.008H12v-.008z"/></svg></span><span class="text-[11px]" style="color:var(--text-secondary)">${esc(txt)}</span></div>`;
        }).join('')+'</div>';
      }
      dbEl.innerHTML=html;
    }
  }
  if(rpEl){
    const raw=ctxParseJson(ctxPack&&ctxPack.repoProfilesJson,null);
    let repos=[];
    if(Array.isArray(raw))repos=raw;
    else if(raw&&typeof raw==='object'){
      const inner=raw.repos||raw.repositories||raw;
      repos=Object.keys(inner).map(k=>{const v=inner[k];return (v&&typeof v==='object')?{...v,name:v.name||k}:{name:String(v&&typeof v==='string'?v:k)};});
    }
    repos=repos.map(x=>typeof x==='string'?{name:x,modules:[]}:{name:x.name||x.repo||x.repository||x.path||'repo',modules:ctxList(x.modules),notes:x.notes||x.description||''});
    if(!repos.length){
      rpEl.innerHTML='<div class="text-xs py-6 text-center rounded-xl border border-dashed" style="border-color:var(--border-color);color:var(--text-secondary)">Sin perfiles de repo todavía</div>';
    }else{
      rpEl.innerHTML='<div class="space-y-2">'+repos.map(r=>{
        const mods=r.modules.map(m=>{
          const mn=(m&&typeof m==='object')?(m.name||m.module||m.path||JSON.stringify(m)):String(m);
          return `<span class="text-[10px] px-2 py-0.5 rounded-lg bg-cyan-500/10 text-cyan-400 border border-cyan-500/20 font-mono">${esc(mn)}</span>`;
        }).join('');
        return `<div class="p-3 rounded-xl border" style="background:var(--bg-tertiary);border-color:var(--border-color)">
          <div class="text-xs font-mono font-semibold text-cyan-300">${esc(r.name)}</div>
          ${r.notes?`<div class="text-[11px] mt-0.5" style="color:var(--text-secondary)">${esc(r.notes)}</div>`:''}
          ${mods?`<div class="flex flex-wrap gap-1.5 mt-2">${mods}</div>`:''}
        </div>`;
      }).join('')+'</div>';
    }
  }
}
async function ctxRefreshDb(){
  const btn=document.getElementById('ctx-refreshdb-btn');
  const orig=btn?btn.innerHTML:'';
  try{
    if(btn){btn.disabled=true;btn.innerHTML='<svg class="w-3.5 h-3.5 inline-block animate-spin mr-1.5 -mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15"/></svg>Escaneando…';}
    const pack=await api('POST','/support/context-pack/refresh-db');
    if(pack)ctxPack=pack;
    renderCtxProfiles();
    renderCtxDigest();
    ctxUpdateVersionBadge();
    toast('Perfil de base de datos actualizado','success');
  }catch(e){toast(e.message||'Error re-escaneando la base','error');}
  finally{if(btn){btn.disabled=false;btn.innerHTML=orig;}}
}

// ---------- Memoria de resoluciones ----------
function ctxMemoryEntries(){
  const m=ctxParseJson(ctxPack&&ctxPack.resolutionMemoryJson,[]);
  let arr=[];
  if(Array.isArray(m))arr=m;
  else if(m&&typeof m==='object'){
    const inner=m.entries||m.memory||m.resolutions||m.items||[];
    arr=Array.isArray(inner)?inner:Object.values(inner);
  }
  return arr.map(e=>{
    if(!e||typeof e!=='object')return{success:true,symptom:String(e),fix:'',entity:'',at:null};
    return{
      success:e.success!=null?!!e.success:(e.fixThatWorked!=null&&!e.fixThatFailed),
      symptom:e.symptom||e.incident||e.title||'',
      fix:e.fixThatWorked||e.fixThatFailed||e.fix||e.resolution||'',
      entity:e.entity||e.entityName||e.table||'',
      at:e.atUtc||e.createdAtUtc||e.createdAt||e.timestamp||null
    };
  }).sort((a,b)=>new Date(b.at||0)-new Date(a.at||0));
}
function renderCtxMemory(){
  const el=document.getElementById('ctx-memory-list');
  if(!el)return;
  const entries=ctxMemoryEntries();
  if(!entries.length){
    el.innerHTML='<div class="text-xs py-6 text-center rounded-xl border border-dashed" style="border-color:var(--border-color);color:var(--text-secondary)">Sin memoria todavía — se construye a medida que se resuelven incidentes</div>';
    return;
  }
  el.innerHTML=entries.map(e=>{
    const icon=e.success
      ?'<span class="w-5 h-5 rounded-full bg-emerald-500/20 text-emerald-400 flex items-center justify-center flex-shrink-0 text-xs font-bold">✓</span>'
      :'<span class="w-5 h-5 rounded-full bg-red-500/20 text-red-400 flex items-center justify-center flex-shrink-0 text-xs font-bold">✕</span>';
    return `<div class="flex items-start gap-3 p-3 rounded-xl border" style="background:var(--bg-tertiary);border-color:var(--border-color)">
      ${icon}
      <div class="min-w-0 flex-1">
        <div class="text-xs" style="color:var(--text-primary)"><span class="font-semibold">${esc(e.symptom||'—')}</span><span style="color:var(--text-tertiary)"> → </span><span class="font-mono text-[11px] ${e.success?'text-emerald-300':'text-red-300'}">${esc(e.fix||'—')}</span></div>
        <div class="text-[10px] mt-1" style="color:var(--text-tertiary)">${e.entity?(esc(e.entity)+' · '):''}${e.at?timeAgo(e.at):''}</div>
      </div>
    </div>`;
  }).join('');
}
async function ctxRebuildMemory(){
  const btn=document.getElementById('ctx-rebuild-btn');
  const orig=btn?btn.innerHTML:'';
  try{
    if(btn){btn.disabled=true;btn.textContent='Reconstruyendo…';}
    const pack=await api('POST','/support/context-pack/rebuild-memory');
    if(pack)ctxPack=pack;
    renderCtxMemory();
    renderCtxDigest();
    ctxUpdateVersionBadge();
    toast('Memoria reconstruida desde historial','success');
  }catch(e){toast(e.message||'Error reconstruyendo la memoria','error');}
  finally{if(btn){btn.disabled=false;btn.innerHTML=orig;}}
}
async function ctxExport(){
  const btn=document.getElementById('ctx-export-btn');
  try{
    if(btn)btn.disabled=true;
    const hdr={'Authorization':'Bearer '+(s.token||'')};
    if(s.tenantId)hdr['X-Tenant-Id']=s.tenantId;
    const r=await fetch(`${API}/support/context-pack/export`,{headers:hdr,credentials:'include'});
    if(r.status===401){logout();return;}
    if(!r.ok)throw new Error(await r.text()||'Error exportando el contexto');
    const blob=await r.blob();
    let name='context-pack.md';
    const cd=r.headers.get('Content-Disposition')||'';
    const fm=cd.match(/filename\*?=(?:UTF-8''|")?([^";]+)/i);
    if(fm)name=decodeURIComponent(fm[1].replace(/"/g,'').trim());
    const url=URL.createObjectURL(blob);
    const a=document.createElement('a');
    a.href=url;a.download=name;
    document.body.appendChild(a);a.click();
    setTimeout(()=>{document.body.removeChild(a);URL.revokeObjectURL(url);},150);
    toast('Contexto exportado','success');
  }catch(e){toast(e.message||'Error exportando el contexto','error');}
  finally{if(btn)btn.disabled=false;}
}

// ---------- PromptDigest ----------
function renderCtxDigest(){
  const el=document.getElementById('ctx-digest');
  if(!el)return;
  const d=ctxPack&&(ctxPack.promptDigest||ctxPack.PromptDigest);
  el.textContent=d||'Sin digest todavía — se genera al consolidar el Context Pack.';
}

// ---------- Auto-mejora del agente (cola de mejoras) ----------
s.ctxImprovements=s.ctxImprovements||[];
s.ctxImproveFilter=s.ctxImproveFilter||'';

const CTX_IMPROVE_KINDS={
  DataQualityRule:{cls:'bg-cyan-500/15 text-cyan-400 border-cyan-500/25',lbl:'Datos'},
  NewPlaybook:{cls:'bg-emerald-500/15 text-emerald-400 border-emerald-500/25',lbl:'Playbook'},
  KbGap:{cls:'bg-amber-500/15 text-amber-400 border-amber-500/25',lbl:'KB'},
  PlannerGap:{cls:'bg-violet-500/15 text-violet-400 border-violet-500/25',lbl:'Planner'}
};
const CTX_IMPROVE_STATUS={
  Suggested:{cls:'bg-amber-500/15 text-amber-400 border-amber-500/25',lbl:'Sugerida'},
  Dismissed:{cls:'bg-gray-500/15 text-gray-400 border-gray-500/25',lbl:'Descartada'},
  Converted:{cls:'bg-emerald-500/15 text-emerald-400 border-emerald-500/25',lbl:'Convertida'}
};

function ctxImproveErrMsg(e,fallback){
  const m=(e&&e.message)?e.message:'';
  if(m){
    try{const j=JSON.parse(m);if(j&&(j.error||j.message))return j.error||j.message;}catch{}
    return m;
  }
  return fallback;
}
async function limpr(){
  const el=document.getElementById('ctx-improve-list');
  if(!el)return;
  const cnt=document.getElementById('ctx-improve-count');
  if(!s.tenantId&&!s.isAdmin){
    s.ctxImprovements=[];
    el.innerHTML='<div class="text-xs py-6 text-center rounded-xl border border-dashed" style="border-color:var(--border-color);color:var(--text-secondary)">Seleccioná una cuenta para ver la auto-mejora del agente</div>';
    if(cnt)cnt.classList.add('hidden');
    return;
  }
  const f=s.ctxImproveFilter||'';
  try{
    const d=await api('GET','/support/improvements'+(f?('?status='+encodeURIComponent(f)):''));
    s.ctxImprovements=Array.isArray(d)?d:[];
    renderCtxImprove();
  }catch(e){
    s.ctxImprovements=[];
    showWidgetError('ctx-improve-list','No se pudieron cargar las sugerencias de mejora','limpr()');
    ctxImproveUpdateCount();
  }
}
async function ctxImproveUpdateCount(){
  const cnt=document.getElementById('ctx-improve-count');
  if(!cnt)return;
  const items=s.ctxImprovements||[];
  const f=s.ctxImproveFilter||'';
  let n=0;
  if(f===''||f==='Suggested'){
    n=items.filter(x=>x&&x.status==='Suggested').length;
  }else{
    try{
      const d=await api('GET','/support/improvements?status=Suggested');
      n=Array.isArray(d)?d.length:0;
    }catch{n=0;}
  }
  cnt.textContent=String(n);
  cnt.classList.toggle('hidden',n===0);
}
function renderCtxImprove(){
  const el=document.getElementById('ctx-improve-list');
  if(!el)return;
  const items=s.ctxImprovements||[];
  if(!items.length){
    el.innerHTML='<div class="text-xs py-6 text-center rounded-xl border border-dashed" style="border-color:var(--border-color);color:var(--text-secondary)">Sin sugerencias todavía — escaneá los fallos del agente para generar tickets de mejora</div>';
    ctxImproveUpdateCount();
    return;
  }
  el.innerHTML=items.map(it=>{
    const kind=CTX_IMPROVE_KINDS[it.kind]||{cls:'bg-gray-500/15 text-gray-400 border-gray-500/25',lbl:it.kind||'—'};
    const st=CTX_IMPROVE_STATUS[it.status]||{cls:'bg-gray-500/15 text-gray-400 border-gray-500/25',lbl:it.status||'—'};
    const occ=(it.occurrenceCount||0)>1?`<span class="text-[10px] px-1.5 py-0.5 rounded-full border border-gray-500/30 text-gray-300 font-semibold" title="Ocurrencias">×${esc(String(it.occurrenceCount))}</span>`:'';
    const id=esc(String(it.id));
    let draftStr='';
    if(it.draftPayloadJson!=null&&it.draftPayloadJson!==''){
      const parsed=ctxParseJson(it.draftPayloadJson,null);
      draftStr=parsed==null?String(it.draftPayloadJson):JSON.stringify(parsed,null,2);
    }
    let btns='';
    if(it.status==='Suggested'){
      btns=`<button type="button" onclick="ctxImproveDraft('${id}',this)" class="text-[11px] px-2.5 py-1 rounded-lg btn-secondary flex-shrink-0 transition-all">Generar borrador</button>
        <button type="button" onclick="ctxImproveAccept('${id}',this)" class="text-[11px] px-2.5 py-1 rounded-lg bg-cyan-500/20 hover:bg-cyan-500/30 text-cyan-300 border border-cyan-500/30 flex-shrink-0 transition-all">Crear playbook</button>
        <button type="button" onclick="ctxImproveDismiss('${id}')" class="text-[11px] px-2.5 py-1 rounded-lg bg-red-500/10 hover:bg-red-500/20 text-red-300 border border-red-500/25 flex-shrink-0 transition-all">Descartar</button>`;
    }else if(it.status==='Dismissed'){
      btns=`<button type="button" onclick="ctxImproveReopen('${id}')" class="text-[11px] px-2.5 py-1 rounded-lg btn-secondary flex-shrink-0 transition-all">Reactivar</button>`;
    }else if(it.status==='Converted'){
      btns=`<button type="button" onclick="n('playbooks')" class="text-[11px] px-2.5 py-1 rounded-lg bg-emerald-500/15 hover:bg-emerald-500/25 text-emerald-300 border border-emerald-500/25 flex-shrink-0 transition-all" title="Ver el playbook generado">→ Playbook</button>`;
    }
    return `<div class="p-3 rounded-xl border" style="background:var(--bg-tertiary);border-color:var(--border-color)">
      <div class="flex items-center justify-between gap-3 flex-wrap">
        <div class="min-w-0 flex-1">
          <div class="flex items-center gap-2 min-w-0 flex-wrap">
            <span class="text-[10px] px-2 py-0.5 rounded-full border ${kind.cls} font-semibold uppercase tracking-wider flex-shrink-0">${esc(kind.lbl)}</span>
            <span class="text-sm font-medium truncate" style="color:var(--text-primary)">${esc(it.title||'Sugerencia')}</span>
            ${occ}
            <span class="text-[10px] px-2 py-0.5 rounded-full border ${st.cls} font-semibold uppercase tracking-wider flex-shrink-0">${esc(st.lbl)}</span>
          </div>
          <div class="text-[11px] mt-0.5" style="color:var(--text-tertiary)">${it.createdAtUtc?timeAgo(it.createdAtUtc):''}${it.incidentId?(' · incidente '+esc(String(it.incidentId)).slice(0,8)):''}</div>
        </div>
        <div class="flex flex-wrap gap-1.5 flex-shrink-0">${btns}</div>
      </div>
      <details class="mt-2.5 rounded-lg border px-3 py-2" style="background:rgba(0,0,0,0.25);border-color:var(--border-color)">
        <summary class="cursor-pointer text-[11px] font-semibold" style="color:var(--text-tertiary)">Detalle y borrador</summary>
        <div class="text-xs mt-2 whitespace-pre-wrap" style="color:var(--text-secondary)">${esc(it.description||'Sin descripción.')}</div>
        ${draftStr?`<pre class="mt-2 text-[11px] font-mono whitespace-pre-wrap overflow-x-auto max-h-64 overflow-y-auto p-2.5 rounded-lg" style="background:rgba(0,0,0,0.35);color:var(--text-secondary);border:1px solid var(--border-color)">${esc(draftStr)}</pre>`:'<div class="text-[11px] mt-2" style="color:var(--text-tertiary)">Sin borrador todavía — usá "Generar borrador" para proponer el playbook.</div>'}
      </details>
    </div>`;
  }).join('');
  ctxImproveUpdateCount();
}
function ctxImproveFilterChange(){
  const sel=document.getElementById('ctx-improve-filter');
  s.ctxImproveFilter=sel?(sel.value||''):'';
  limpr();
}
async function ctxImproveScan(){
  const btn=document.getElementById('ctx-improve-scan-btn');
  const orig=btn?btn.innerHTML:'';
  try{
    if(btn){btn.disabled=true;btn.innerHTML='<svg class="w-3.5 h-3.5 inline-block animate-spin mr-1.5 -mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15"/></svg>Escaneando…';}
    const d=await api('POST','/support/improvements/scan');
    const created=d&&typeof d.created==='number'?d.created:0;
    toast(created>0?(created+' sugerencias nuevas'):'Sin nuevos fallos','success');
  }catch(e){toast(ctxImproveErrMsg(e,'Error escaneando fallos del agente'),'error');}
  finally{if(btn){btn.disabled=false;btn.innerHTML=orig;}}
  await limpr();
}
async function ctxImproveDraft(id,btn){
  const orig=btn?btn.innerHTML:'';
  try{
    if(btn){btn.disabled=true;btn.textContent='Generando…';}
    const d=await api('POST',`/support/improvements/${encodeURIComponent(id)}/draft`);
    if(d&&(s.ctxImprovements||[]).length){
      const i=(s.ctxImprovements||[]).findIndex(x=>String(x.id)===String(id));
      if(i>=0)s.ctxImprovements[i]=d;
    }
    toast('Borrador generado','success');
  }catch(e){toast(ctxImproveErrMsg(e,'No se pudo generar el borrador'),'error');}
  finally{if(btn){btn.disabled=false;btn.innerHTML=orig;}}
  renderCtxImprove();
}
async function ctxImproveAccept(id,btn){
  const orig=btn?btn.innerHTML:'';
  try{
    if(btn){btn.disabled=true;btn.textContent='Creando…';}
    const d=await api('POST',`/support/improvements/${encodeURIComponent(id)}/accept`);
    const code=(d&&(d.code||d.name))||'—';
    toast(`Playbook ${code} creado — revisalo en Playbooks`,'success');
  }catch(e){toast(ctxImproveErrMsg(e,'No se pudo crear el playbook'),'error');}
  finally{if(btn){btn.disabled=false;btn.innerHTML=orig;}}
  await limpr();
}
async function ctxImproveDismiss(id){
  const ok=await askConfirm({title:'Descartar sugerencia',detail:'Podés reactivarla después.',confirmLabel:'Descartar',danger:true});
  if(!ok)return;
  try{
    await api('POST',`/support/improvements/${encodeURIComponent(id)}/dismiss`);
    toast('Sugerencia descartada','success');
  }catch(e){toast(ctxImproveErrMsg(e,'No se pudo descartar la sugerencia'),'error');}
  await limpr();
}
async function ctxImproveReopen(id){
  try{
    await api('POST',`/support/improvements/${encodeURIComponent(id)}/reopen`);
    toast('Sugerencia reactivada','success');
  }catch(e){toast(ctxImproveErrMsg(e,'No se pudo reactivar la sugerencia'),'error');}
  await limpr();
}

// ─── LLM retry pool (tareas IA pendientes) ──────────────────────────────────
const LLM_TASK_TYPES={DocDistill:'Destilación doc',DbProfile:'Perfil de base',ImprovementDraft:'Borrador mejora'};
const LLM_TASK_STATUS={Pending:{cls:'bg-amber-500/15 text-amber-400 border-amber-500/25',lbl:'Pendiente'},Completed:{cls:'bg-emerald-500/15 text-emerald-400 border-emerald-500/25',lbl:'Completada'},DeadLetter:{cls:'bg-red-500/15 text-red-400 border-red-500/25',lbl:'Sin resolver'},Cancelled:{cls:'bg-gray-500/15 text-gray-400 border-gray-500/25',lbl:'Cancelada'}};

async function llt(){
  const el=document.getElementById('ctx-llmtasks-list');
  const cnt=document.getElementById('ctx-llmtasks-count');
  if(!el)return;
  if(!s.tenantId&&!s.isAdmin){
    el.innerHTML='<div class="text-xs py-6 text-center rounded-xl border border-dashed" style="border-color:var(--border-color);color:var(--text-secondary)">Seleccioná una cuenta para ver las tareas IA pendientes</div>';
    if(cnt)cnt.classList.add('hidden');
    return;
  }
  try{
    const d=await api('GET','/support/llm-tasks');
    s.ctxLlmTasks=Array.isArray(d)?d:[];
    renderLlmTasks();
  }catch(e){
    s.ctxLlmTasks=[];
    showWidgetError('ctx-llmtasks-list','No se pudieron cargar las tareas IA','llt()');
    llmTasksUpdateCount();
  }
}
function llmTasksUpdateCount(){
  const cnt=document.getElementById('ctx-llmtasks-count');
  if(!cnt)return;
  const n=(s.ctxLlmTasks||[]).filter(x=>x&&x.status==='Pending').length;
  cnt.textContent=String(n);
  cnt.classList.toggle('hidden',n===0);
}
function renderLlmTasks(){
  const el=document.getElementById('ctx-llmtasks-list');
  if(!el)return;
  const items=s.ctxLlmTasks||[];
  if(!items.length){
    el.innerHTML='<div class="text-xs py-6 text-center rounded-xl border border-dashed" style="border-color:var(--border-color);color:var(--text-secondary)">Sin tareas pendientes — toda la IA está al día</div>';
    llmTasksUpdateCount();
    return;
  }
  el.innerHTML=items.map(it=>{
    const tp=LLM_TASK_TYPES[it.taskType]||it.taskType||'—';
    const st=LLM_TASK_STATUS[it.status]||{cls:'bg-gray-500/15 text-gray-400 border-gray-500/25',lbl:it.status||'—'};
    const id=esc(String(it.id));
    const next=it.nextAttemptAtUtc?(' · próximo intento '+timeAgo(it.nextAttemptAtUtc)):'';
    const btns=it.status==='Pending'
      ?`<button type="button" onclick="llmTaskRetry('${id}')" class="text-[11px] px-2.5 py-1 rounded-lg bg-cyan-500/20 hover:bg-cyan-500/30 text-cyan-300 border border-cyan-500/30 flex-shrink-0 transition-all">Reintentar ahora</button>
        <button type="button" onclick="llmTaskCancel('${id}')" class="text-[11px] px-2.5 py-1 rounded-lg bg-red-500/10 hover:bg-red-500/20 text-red-300 border border-red-500/25 flex-shrink-0 transition-all">Cancelar</button>`
      :it.status==='DeadLetter'
      ?`<button type="button" onclick="llmTaskRetry('${id}')" class="text-[11px] px-2.5 py-1 rounded-lg btn-secondary flex-shrink-0 transition-all">Reabrir y reintentar</button>`
      :'';
    return `<div class="p-3 rounded-xl border" style="background:var(--bg-tertiary);border-color:var(--border-color)">
      <div class="flex items-center justify-between gap-3 flex-wrap">
        <div class="min-w-0 flex-1">
          <div class="flex items-center gap-2 min-w-0 flex-wrap">
            <span class="text-[10px] px-2 py-0.5 rounded-full border bg-cyan-500/15 text-cyan-300 border-cyan-500/25 font-semibold uppercase tracking-wider flex-shrink-0">${esc(tp)}</span>
            <span class="text-[10px] px-2 py-0.5 rounded-full border ${st.cls} font-semibold uppercase tracking-wider flex-shrink-0">${esc(st.lbl)}</span>
            <span class="text-[10px] px-1.5 py-0.5 rounded-full border border-gray-500/30 text-gray-300 font-semibold" title="Intentos">×${esc(String(it.attempts||0))}/${esc(String(it.maxAttempts||0))}</span>
          </div>
          <div class="text-[11px] mt-0.5 truncate" style="color:var(--text-tertiary)">${esc(it.dedupeKey||'')}${next}${it.lastError?(' · '+esc(String(it.lastError).slice(0,120))):''}</div>
        </div>
        <div class="flex flex-wrap gap-1.5 flex-shrink-0">${btns}</div>
      </div>
    </div>`;
  }).join('');
  llmTasksUpdateCount();
}
async function llmTaskRetry(id){
  try{
    await api('POST',`/support/llm-tasks/${encodeURIComponent(id)}/retry`);
    toast('Tarea reencolada — el worker la retoma en el próximo ciclo','success');
  }catch(e){toast(e.message||'No se pudo reencolar la tarea','error');}
  await llt();
}
async function llmTaskCancel(id){
  const ok=await askConfirm({title:'Cancelar tarea IA',detail:'El trabajo quedará sin terminar — hacelo manualmente.',confirmLabel:'Cancelar tarea',danger:true});
  if(!ok)return;
  try{
    await api('POST',`/support/llm-tasks/${encodeURIComponent(id)}/cancel`);
    toast('Tarea cancelada','success');
  }catch(e){toast(e.message||'No se pudo cancelar la tarea','error');}
  await llt();
}

