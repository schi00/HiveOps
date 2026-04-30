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
s.user={username:d.displayName||d.username||'',role:d.role};
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
setTimeout(()=>{loadTenants();n('dashboard');ld();initRealtime();},100);
}catch(e){console.error('Error in showApp:',e);showLogin();}
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
ic.className='w-6 h-6 rounded-full flex items-center justify-center text-sm '+(t==='success'?'bg-emerald-500/20 text-emerald-400':t==='error'?'bg-red-500/20 text-red-400':'bg-amber-500/20 text-amber-400');
ic.innerHTML=t==='success'?'<svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M5 13l4 4L19 7"/></svg>':t==='error'?'<svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"/></svg>':'<svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"/></svg>';
el.classList.add('show');
setTimeout(()=>el.classList.remove('show'),4000);
}
function showToast(msg,t){toast(msg,t);}
function n(v){
document.querySelectorAll('.view-section').forEach(el=>{el.classList.remove('active');el.style.opacity='0';});
document.querySelectorAll('.nav-item').forEach(el=>{el.classList.remove('active');});
const viewMap={dashboard:'dash',incidents:'inc',kb:'kb',config:'cfg',admin:'adm'};
const viewId=viewMap[v]||v;
const t=document.getElementById('v-'+viewId)||document.getElementById('v-'+v);
if(t){t.classList.add('active');
anime({targets:t,opacity:[0,1],translateY:[16,0],duration:450,easing:'easeOutCubic'});}
else{console.warn('Dashboard view not found for',v,viewId);}
const nb=document.getElementById('n-'+viewId)||document.getElementById('n-'+v);
if(nb){nb.classList.add('active');}
if(v==='incidents')li();if(v==='kb')lk();if(v==='config')lc();if(v==='admin'){lm();lh();lb();}
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
el.innerHTML=list.length?list.map(k=>`<div class="hive-card p-5 cursor-pointer group"><div class="flex justify-between items-start mb-3"><h4 class="font-semibold group-hover:text-amber-500 transition-colors" style="color:var(--text-primary)">${esc(k.title)}</h4>${k.isPublished?'<span class="text-xs px-2.5 py-1 rounded-full bg-emerald-500/15 text-emerald-400 border border-emerald-500/20">Publicado</span>':'<span class="text-xs px-2.5 py-1 rounded-full bg-neutral-700 text-neutral-400">Borrador</span>'}</div><div class="text-sm line-clamp-3 mb-4" style="color:var(--text-secondary)">${esc(k.content||'')}</div><div class="flex flex-wrap gap-2">${(k.tags||[]).map(t=>`<span class="text-xs px-2 py-1 rounded-lg bg-amber-500/10 text-amber-500 border border-amber-500/20">${esc(t)}</span>`).join('')}</div></div>`).join(''):'<div class="hive-card p-8 text-center col-span-full" style="color:var(--text-secondary)">Sin articulos</div>';
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
el.innerHTML=(d||[]).length?d.map(m=>`<div class="hive-card p-4 flex items-center justify-between"><div><div class="font-semibold text-sm" style="color:var(--text-primary)">${esc(m.title||'Fix')}</div><div class="text-xs mt-1" style="color:var(--text-tertiary)">Branch: <code class="text-amber-500 font-mono">${esc(m.branch)}</code></div></div><button onclick="approveMerge('${m.id}')" class="px-4 py-2 rounded-lg bg-emerald-600 hover:bg-emerald-500 text-white text-xs font-medium transition-all">Aprobar</button></div>`).join(''):`<div class="text-center py-6" style="color:var(--text-secondary)">Cola vacia</div>`;
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
const stColors={Open:'text-amber-500',InProgress:'text-amber-400',Resolved:'text-emerald-500',Closed:'text-neutral-400'};
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
return `<div onclick="od('${i.id}')" class="p-4 hover:bg-amber-500/5 transition-all cursor-pointer flex items-center gap-4 group ${compact?'':'border-b'}" style="border-color:var(--border-color)"><div class="w-2 h-2 rounded-full ${sev} flex-shrink-0 status-pulse"></div><div class="flex-1 min-w-0"><div class="font-medium text-sm truncate flex items-center gap-2" style="color:var(--text-primary)"><span>${esc(i.title)} ${deployBadge}</span><span id="rt-${i.id}" class="inline-block"></span></div><div class="text-xs mt-0.5" style="color:var(--text-tertiary)">${esc(i.category||'Other')} · ${timeAgo(i.createdAt)}</div></div><div class="text-xs font-medium ${st}">${i.status}</div></div>`;
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
    const boxCls= auto? 'border-l-2 border-amber-500/60 bg-white/5' : '';
    return `<div class="p-3 rounded-lg mb-2 ${boxCls}"><div class="text-xs mb-1" style="color:var(--text-tertiary)">${esc(m.role)} · ${new Date(m.createdAt).toLocaleString()} ${pill}</div><div class="text-sm" style="color:var(--text-secondary)">${esc(m.content||'')}</div></div>`;
  }).join('');
}
async function od(id){
try{
const d=await api('GET','/support/incidents/'+id);
const sevColors={Critical:'bg-red-500',High:'bg-amber-500',Medium:'bg-yellow-500',Low:'bg-neutral-500'};
const sev=sevColors[d.severity]||'bg-neutral-500';
const stColors={Open:'text-amber-500',InProgress:'text-amber-400',Resolved:'text-emerald-500',Closed:'text-neutral-400'};
const st=stColors[d.status]||'text-neutral-400';
let actions='';
if(d.status==='Open'||d.status==='InProgress'){
actions+=`<div class="flex gap-3 mt-5"><button onclick="upd('${id}','InProgress')" class="btn-primary px-4 py-2 rounded-lg text-xs flex-1">Marcar En Progreso</button><button onclick="upd('${id}','Resolved')" class="btn-secondary px-4 py-2 rounded-lg text-xs flex-1" style="background:var(--bg-tertiary);color:#10b981;border-color:#10b981">Resolver</button></div>`;
}
if(d.suggestedFixSql){
actions+=`<div class="mt-5 p-4 rounded-xl hive-card" style="background:rgba(245,158,11,0.05)"><div class="text-xs mb-2" style="color:var(--text-tertiary)">Fix sugerido (SQL)</div><pre class="text-xs text-amber-400 overflow-x-auto font-mono">${esc(d.suggestedFixSql)}</pre><div class="flex gap-2 mt-3"><button onclick="appr('${id}')" class="px-3 py-1.5 rounded-lg bg-emerald-600 hover:bg-emerald-500 text-white text-xs">Aprobar</button><button onclick="rej('${id}')" class="px-3 py-1.5 rounded-lg bg-red-600 hover:bg-red-500 text-white text-xs">Rechazar</button></div></div>`;
}
document.getElementById('db').innerHTML=`<div class="flex items-center gap-3 mb-5"><div class="w-3 h-3 rounded-full ${sev} status-pulse"></div><div class="text-2xl font-bold" style="color:var(--text-primary)">${esc(d.title)}</div></div><div class="text-sm mb-1" style="color:var(--text-secondary)">Estado: <span class="${st} font-medium">${d.status}</span></div><div class="text-sm mb-5" style="color:var(--text-tertiary)">Creado: ${new Date(d.createdAt).toLocaleString()}</div><div class="hive-card p-4 mb-4"><div class="text-xs uppercase tracking-wider mb-2" style="color:var(--text-tertiary)">Descripcion</div><div class="text-sm leading-relaxed whitespace-pre-wrap" style="color:var(--text-secondary)">${esc(d.description)}</div></div>${d.resolutionNotes?`<div class=\"hive-card p-4 mb-4\"><div class=\"text-xs uppercase tracking-wider mb-2\" style=\"color:var(--text-tertiary)\">Notas de resolucion</div><div class=\"text-sm whitespace-pre-wrap\" style=\"color:var(--text-secondary)\">${esc(d.resolutionNotes)}</div></div>`:''}${d.reasoning?`<div class=\"hive-card p-4 mb-4 border-l-2 border-amber-500\" style=\"background:rgba(245,158,11,0.03)\"><div class=\"text-xs text-amber-500 uppercase tracking-wider mb-2\">Analisis IA</div><div class=\"text-sm italic\" style=\"color:var(--text-secondary)\">${esc(d.reasoning)}</div></div>`:''}${d.gitBranch?`<div class=\"text-xs\" style=\"color:var(--text-tertiary)\">Branch: <code class=\"text-amber-500 font-mono\">${esc(d.gitBranch)}</code> · Commit: <code class=\"font-mono\" style=\"color:var(--text-secondary)\">${esc((d.gitCommitHash||'').substring(0,8))}</code></div>`:''}${actions}<div id=\"msgs\" class=\"hive-card p-4 mt-4\"><div class=\"text-xs uppercase tracking-wider mb-2\" style=\"color:var(--text-tertiary)\">Mensajes</div><div id=\"ml\"><div class=\"text-sm\" style=\"color:var(--text-secondary)\">Cargando mensajes...</div></div></div>`;

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
}catch(e){console.error('Error loading tenants:',e);toast('Error al cargar tenants','error');}
}
function changeTenant(){
const sel=document.getElementById('tenant-select');
s.tenantId=sel.value;
if(s.tenantId){
localStorage.setItem('tenantId',s.tenantId);
ld();li();
 if(hubConn&&hubConn.invoke){hubConn.invoke('JoinTenantGroup', s.tenantId).catch(()=>{});} 
}else{
localStorage.removeItem('tenantId');
}
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
toast('Preconfiguración '+preset+' aplicada (guardá LLM para persistir)','success');
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
