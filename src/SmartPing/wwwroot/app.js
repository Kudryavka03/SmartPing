'use strict';

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const esc = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
const num = (value, digits = 1) => value === null || value === undefined || !Number.isFinite(Number(value)) ? '—' : Number(value).toLocaleString('zh-CN', {maximumFractionDigits:digits,minimumFractionDigits:digits});
const int = value => num(value, 0);
const date = (value, full = false) => value && !Number.isNaN(new Date(value).getTime()) ? new Date(value).toLocaleString('zh-CN', full ? {month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit',hour12:false} : {hour:'2-digit',minute:'2-digit',second:'2-digit',hour12:false}) : '尚无记录';
const paths = {
  overview:'<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>',
  target:'<circle cx="12" cy="12" r="8.5"/><circle cx="12" cy="12" r="4.5"/><path d="M12 1v4m0 14v4M1 12h4m14 0h4"/>',
  nodes:'<rect x="8" y="2" width="8" height="6" rx="1.5"/><rect x="2" y="16" width="8" height="6" rx="1.5"/><rect x="14" y="16" width="8" height="6" rx="1.5"/><path d="M12 8v4M6 16v-4h12v4"/>',
  route:'<circle cx="5" cy="5" r="2"/><circle cx="19" cy="19" r="2"/><path d="M7 5h9a4 4 0 0 1 0 8H8a3 3 0 0 0 0 6h9M5 7v4"/>',
  settings:'<path d="m9 3-1 3-3 1v3l-2 2 2 2v3l3 1 1 3h6l1-3 3-1v-3l2-2-2-2V7l-3-1-1-3Z"/><circle cx="12" cy="12" r="3"/>',
  plus:'<path d="M12 5v14M5 12h14"/>',refresh:'<path d="M20 7v5h-5M4 17v-5h5"/><path d="M6.1 7a7 7 0 0 1 11.7-1L20 9M4 15l2.2 3A7 7 0 0 0 18 17"/>',
  search:'<circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 4.5 4.5"/>',close:'<path d="m6 6 12 12M18 6 6 18"/>',
  edit:'<path d="m14 5 5 5M4 20l5-1L20 8a2 2 0 0 0-5-5L4 14Z"/>',trash:'<path d="M3 6h18M9 6V3h6v3M6 6l1 15h10l1-15M10 10v7m4-7v7"/>',
  pause:'<path d="M8 5v14M16 5v14"/>',play:'<path d="m8 4 12 8-12 8Z"/>',chart:'<path d="M3 3v18h18M6 14l4-5 4 3 6-8"/>',
  clock:'<circle cx="12" cy="12" r="9"/><path d="M12 6v6l4 2"/>',loss:'<path d="m3 6 6 6 4-4 7 10M15 18h5v-5"/>',
  info:'<circle cx="12" cy="12" r="9"/><path d="M12 11v6m0-10v.1"/>',server:'<rect x="3" y="3" width="18" height="7" rx="2"/><rect x="3" y="14" width="18" height="7" rx="2"/><path d="M7 6.5h.1M7 17.5h.1m4-11h6m-6 11h6"/>',
  history:'<path d="M3 3v6h6M3 9a9 9 0 1 1 0 6M12 7v5l3 2"/>',check:'<path d="m5 12 4 4L19 6"/>'
};
const icon = name => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${paths[name] || paths.target}</svg>`;
const state = {data:null,settings:null,hours:1,selected:'',view:'overview',search:'',group:'',node:'',protocol:'',loading:false,refreshPending:false,token:sessionStorage.getItem('smartping.adminToken') || '',history:null,historyTarget:'',lastSuccess:null};
const traceState = {job:null,submitting:false,cancelling:false,polling:false,timer:null,tool:null,toolLoading:false};
const labels = {overview:'监测概览',targets:'探测项目',nodes:'探测节点',trace:'路由追踪',settings:'设置与部署'};
let refreshTimer;

function fillIcons(root=document){ $$('[data-icon]',root).forEach(element=>element.innerHTML=icon(element.dataset.icon)); }
function toast(message,error=false){ const item=document.createElement('div');item.className=`toast${error?' error':''}`;item.textContent=message;$('#toast-region').append(item);setTimeout(()=>item.remove(),4500); }
function formError(id,message){const element=$(id);element.textContent=message || '';element.hidden=!message;}
function showDialog(dialog){if(!dialog.open)dialog.showModal();}
function nodeName(id){return state.data?.nodes.find(node=>node.id===id)?.name || id;}
function statusInfo(target){
  if(!target.enabled)return {key:'paused',label:'已暂停'};
  const status=target.stats?.status;
  if(['down','offline','failed','critical'].includes(status))return {key:'down',label:'异常'};
  if(['warning','degraded'].includes(status))return {key:'warning',label:'波动'};
  if(['pending','unknown','no-data','waiting'].includes(status)||!target.stats?.sent)return {key:'pending',label:'等待数据'};
  if(target.stats.lossPercent>=100)return {key:'down',label:'异常'};
  if(target.stats.lossPercent>0)return {key:'warning',label:'波动'};
  return {key:'healthy',label:'正常'};
}
async function api(path,options={}){
  const headers=new Headers(options.headers || {});
  if(options.body)headers.set('Content-Type','application/json');
  if(state.token)headers.set('Authorization',`Bearer ${state.token}`);
  let response;
  try{response=await fetch(path,{...options,headers,cache:'no-store'});}catch{throw new Error('无法连接监测中心，请检查服务是否运行。');}
  if(response.status===401){
    state.token='';sessionStorage.removeItem('smartping.adminToken');$('#auth-token').value='';
    showDialog($('#auth-dialog'));
    formError('#auth-error','需要有效的管理员令牌。');
    throw new Error('管理员令牌无效或尚未提供。');
  }
  const text=await response.text();let result;
  try{result=text?JSON.parse(text):null;}catch{throw new Error(`服务返回了无法解析的响应（${response.status}）。`);}
  if(!response.ok){const error=new Error(result?.error || `请求失败（${response.status}）`);error.status=response.status;throw error;}
  return result;
}
async function refresh({silent=false}={}){
  if(state.loading){if(!silent)state.refreshPending=true;return;}
  state.loading=true;$('#refresh-button').classList.add('spinning');
  try{
    const data=await api(`/api/dashboard?hours=${state.hours}${state.node?`&nodeId=${encodeURIComponent(state.node)}`:''}`);
    state.data=data;state.lastSuccess=new Date();
    if(!data.targets.some(target=>target.id===state.selected))state.selected=data.targets[0]?.id || '';
    $('#connection-error').hidden=true;$('#connection-label').textContent='监测中心已连接';$('#connection-dot').className='status-dot';
    $('#server-name').textContent=data.settings.nodeName || '本地监测中心';
    $('#last-updated').textContent=`更新于 ${date(data.serverTime)}`;
    $('#footer-clock').textContent=`服务时间 ${date(data.serverTime,true)}`;
    $('#nav-target-count').textContent=data.targets.length;$('#nav-node-count').textContent=data.nodes.length;
    renderSummary();renderFilters();renderTargets();renderChart();renderNodes();renderTraceNodes();
    if(state.view==='settings' && !state.settings)await loadSettings();
    if(state.historyTarget && state.view==='targets')await loadRecent(state.historyTarget,true);
  }catch(error){
    $('#connection-label').textContent='连接已中断';$('#connection-dot').className='status-dot offline';
    $('#connection-error-text').textContent=error.message+(state.lastSuccess?` 当前显示 ${date(state.lastSuccess)} 的数据。`:'');$('#connection-error').hidden=false;
    if(!state.data)renderInitialError();
    if(!silent && state.data)toast(error.message,true);
  }finally{state.loading=false;$('#refresh-button').classList.remove('spinning');if(state.refreshPending){state.refreshPending=false;refresh({silent:true});}}
}
function renderInitialError(){
  if(!$('#summary-stats').children.length)renderSummary();
  $('#chart-content').innerHTML=`<div class="chart-empty"><div class="empty-state"><div class="empty-icon">${icon('server')}</div><h3>等待连接监测中心</h3><p>服务连接成功后，这里会显示实际探测数据。</p></div></div>`;
}
function renderSummary(){
  const summary=state.data?.summary || {targets:0,nodes:0,onlineNodes:0,sent:0};
  const targets=state.data?.targets || [];
  const cards=[
    {name:'探测项目',value:int(summary.targets),unit:'个',icon:'target',detail:`<span class="good">${targets.filter(target=>target.enabled).length} 个运行中</span><span>· ${targets.filter(target=>!target.enabled).length} 个已暂停</span>`},
    {name:'平均延迟',value:num(summary.avgLatencyMs),unit:'ms',icon:'clock',detail:`成功探测均值，排除 TCP 疑似重传`},
    {name:'整体丢包率',value:num(summary.lossPercent),unit:'%',icon:'loss',detail:`已完成 ${int(summary.sent)} 次探测`},
    {name:'在线节点',value:int(summary.onlineNodes),unit:`/ ${int(summary.nodes)}`,icon:'nodes',detail:`<span class="good">${summary.onlineNodes?'节点持续上报中':'等待节点连接'}</span>`}
  ];
  $('#summary-stats').innerHTML=cards.map(card=>`<article class="stat-card"><div class="stat-top">${card.name}<span class="stat-icon">${icon(card.icon)}</span></div><div class="stat-value">${card.value}<small>${card.unit}</small></div><div class="stat-bottom">${card.detail}</div></article>`).join('');
}
function filtersMarkup(){
  const groups=[...new Set((state.data?.targets || []).map(target=>target.group || '默认分组'))].sort();
  return `<div class="filters"><div class="search-field">${icon('search')}<input class="search-input" type="search" aria-label="搜索探测项目" placeholder="搜索名称或主机地址…" value="${esc(state.search)}"></div><select class="filter-select group-filter" aria-label="按分组筛选"><option value="">全部分组</option>${groups.map(group=>`<option value="${esc(group)}"${group===state.group?' selected':''}>${esc(group)}</option>`).join('')}</select><select class="filter-select node-filter" aria-label="按执行节点筛选"><option value="">全部节点</option>${(state.data?.nodes || []).map(node=>`<option value="${esc(node.id)}"${node.id===state.node?' selected':''}>${esc(node.name)}</option>`).join('')}</select><select class="filter-select protocol-filter" aria-label="按协议筛选"><option value="">全部协议</option><option value="icmp"${state.protocol==='icmp'?' selected':''}>ICMP Ping</option><option value="tcp"${state.protocol==='tcp'?' selected':''}>TCP Ping</option></select>${state.search||state.group||state.node||state.protocol?'<button class="filter-clear">重置</button>':''}</div>`;
}
function renderFilters(){
  for(const container of [$('#overview-filters'),$('#target-filters')]){
    // Preserve search focus and caret across background refreshes.
    const focused=container.contains(document.activeElement)?document.activeElement:null;
    if(focused?.classList.contains('search-input'))continue;
    container.innerHTML=filtersMarkup();
    $('.search-input',container).addEventListener('input',event=>{state.search=event.target.value;renderTargets();syncSearch(event.target);});
    $('.group-filter',container).addEventListener('change',event=>{state.group=event.target.value;renderFilters();renderTargets();});
    $('.protocol-filter',container).addEventListener('change',event=>{state.protocol=event.target.value;renderFilters();renderTargets();});
    $('.node-filter',container).addEventListener('change',event=>{state.node=event.target.value;renderFilters();renderTargets();refresh();});
    $('.filter-clear',container)?.addEventListener('click',()=>{const hadNode=state.node;state.search=state.group=state.node=state.protocol='';renderFilters();renderTargets();if(hadNode)refresh();});
  }
}
function syncSearch(source){$$('.search-input').filter(input=>input!==source).forEach(input=>input.value=state.search);}
function filteredTargets(){const query=state.search.trim().toLocaleLowerCase();return (state.data?.targets || []).filter(target=>(!query||`${target.name} ${target.host}`.toLocaleLowerCase().includes(query))&&(!state.group||(target.group||'默认分组')===state.group)&&(!state.protocol||target.protocol===state.protocol)&&(!state.node||target.nodeIds.includes(state.node)));}
function sparkline(series){
  const points=series.slice(-30);const valid=points.filter(point=>Number.isFinite(point.latencyMs));if(!valid.length)return '<span class="muted">—</span>';
  const max=Math.max(...valid.map(point=>point.latencyMs),1),min=Math.min(...valid.map(point=>point.latencyMs),0);let path='',open=false;
  points.forEach((point,index)=>{if(point.latencyMs==null){open=false;return;}const x=2+index/Math.max(points.length-1,1)*84,y=23-(point.latencyMs-min)/(max-min)*18;path+=`${open?'L':'M'}${x.toFixed(1)},${y.toFixed(1)} `;open=true;});
  return `<svg class="sparkline" viewBox="0 0 88 26" aria-label="最近延迟趋势"><path d="${path}" fill="none" stroke="#5cb5a7" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>`;
}
function renderTargets(){
  const targets=filteredTargets();$('#overview-target-count').textContent=targets.length;
  const total=state.data?.targets.length || 0;
  let html;
  if(!total){html=`<div class="empty-state"><div class="empty-icon">${icon('target')}</div><h3>从第一条链路开始</h3><p>添加一个 IP 或域名，持续追踪延迟、丢包与链路变化。<br>支持 ICMP Ping 与 TCP 端口探测。</p><button class="button primary add-target">${icon('plus')}添加探测项目</button></div>`;}
  else if(!targets.length){html=`<div class="empty-state"><div class="empty-icon">${icon('search')}</div><h3>没有匹配的探测项目</h3><p>尝试其他关键词或调整筛选条件。</p><button class="button clear-all-filters">清除筛选</button></div>`;}
  else html=`<div class="table-scroll"><table><thead><tr><th>项目 / 目标地址</th><th>执行节点</th><th>平均延迟</th><th>丢包率</th><th>延迟趋势</th><th>状态</th><th style="text-align:right">操作</th></tr></thead><tbody>${targets.map(target=>{
    const status=statusInfo(target),stats=target.stats || {};return `<tr class="target-row${target.id===state.selected?' selected':''}" data-id="${esc(target.id)}" tabindex="0" aria-label="查看 ${esc(target.name)} 的延迟详情"><td><div class="target-name"><span class="target-status-indicator ${status.key}"></span><div><div class="target-name-line"><strong title="${esc(target.name)}">${esc(target.name)}</strong><span class="protocol-badge ${target.protocol}">${target.protocol==='tcp'?'TCP':'ICMP'}</span></div><small title="${esc(target.host)}">${esc(target.host)}${target.protocol==='tcp'?`:${target.port}`:''}<span> · ${esc(target.group || '默认分组')}</span></small></div></div></td><td><span class="node-chip" title="${esc(target.nodeIds.map(nodeName).join('、'))}">${esc(target.nodeIds.map(nodeName).join('、'))}</span></td><td><span class="latency-value">${num(stats.avgLatencyMs)}</span><span class="unit-muted">ms</span></td><td><span class="loss-value${stats.lossPercent>0?' bad':''}">${num(stats.lossPercent)}<span class="unit-muted">%</span></span></td><td>${sparkline(target.series || [])}</td><td><span class="status-pill ${status.key}"${stats.missingSources>0?` title="缺少 ${int(stats.missingSources)} 个节点的最新数据；丢包率按实际完成的探测计算。" aria-label="${status.label}，缺少 ${int(stats.missingSources)} 个节点数据"`:''}>${status.label}</span>${stats.missingSources>0?`<small class="missing-node-label">缺少 ${int(stats.missingSources)} 个节点数据</small>`:''}</td><td><div class="row-actions"><button class="icon-button" data-action="history" title="最近探测记录" aria-label="查看 ${esc(target.name)} 最近记录">${icon('history')}</button><button class="icon-button" data-action="toggle" title="${target.enabled?'暂停':'启用'}" aria-label="${target.enabled?'暂停':'启用'} ${esc(target.name)}">${icon(target.enabled?'pause':'play')}</button><button class="icon-button" data-action="edit" title="编辑" aria-label="编辑 ${esc(target.name)}">${icon('edit')}</button><button class="icon-button delete" data-action="delete" title="删除" aria-label="删除 ${esc(target.name)}">${icon('trash')}</button></div></td></tr>`;
  }).join('')}</tbody></table></div><div class="table-footer"><span>显示 ${targets.length} / ${total} 个探测项目</span><span>点击项目查看延迟详情</span></div>`;
  for(const container of [$('#overview-targets'),$('#all-targets')]){
    container.innerHTML=html;
    $$('.add-target',container).forEach(button=>button.addEventListener('click',()=>openTarget()));
    $('.clear-all-filters',container)?.addEventListener('click',()=>{state.search=state.group=state.node=state.protocol='';renderFilters();renderTargets();refresh();});
    $$('.target-row',container).forEach(row=>{
      const routeButton=document.createElement('button');routeButton.type='button';routeButton.className='icon-button';routeButton.dataset.action='trace';routeButton.title='路由追踪';routeButton.setAttribute('aria-label','追踪此目标的路由');routeButton.innerHTML=icon('route');$('.row-actions',row).prepend(routeButton);
      row.addEventListener('click',event=>{if(event.target.closest('button'))return;selectTarget(row.dataset.id);});
      row.addEventListener('keydown',event=>{if(event.target!==row)return;if(event.key==='Enter'||event.key===' '){event.preventDefault();selectTarget(row.dataset.id);}});
      $$('[data-action]',row).forEach(button=>button.addEventListener('click',()=>targetAction(row.dataset.id,button.dataset.action,button)));
    });
  }
}
function selectTarget(id){state.selected=id;location.hash='overview';renderChart();renderTargets();$('#chart-target').focus();}
async function targetAction(id,action,button){
  const target=state.data.targets.find(item=>item.id===id);if(!target)return;
  if(action==='edit')return openTarget(target);
  if(action==='trace')return openTrace(target);
  if(action==='history'){location.hash='targets';return loadRecent(id);}
  if(action==='delete')return confirmAction('删除探测项目',`确定删除「${target.name}」？该项目将停止探测，并删除相关历史记录。`,async()=>{await api(`/api/targets/${encodeURIComponent(id)}`,{method:'DELETE'});if(state.historyTarget===id){state.historyTarget='';$('#recent-panel').hidden=true;}toast('探测项目已删除');await refresh();});
  if(action==='toggle'){button.disabled=true;try{await api(`/api/targets/${encodeURIComponent(id)}`,{method:'PUT',body:JSON.stringify({...target,enabled:!target.enabled})});toast(target.enabled?'已暂停探测':'已启用探测');await refresh();}catch(error){toast(error.message,true);}finally{button.disabled=false;}}
}
function renderChart(){
  const select=$('#chart-target'),targets=state.data?.targets || [];
  select.innerHTML=targets.length?targets.map(target=>`<option value="${esc(target.id)}"${target.id===state.selected?' selected':''}>${esc(target.name)}</option>`).join(''):'<option value="">暂无探测项目</option>';
  const target=targets.find(item=>item.id===state.selected),badge=$('#chart-protocol');badge.hidden=!target;
  if(target){badge.textContent=target.protocol==='tcp'?'TCP':'ICMP';badge.className=`protocol-badge ${target.protocol}`;}
  const stats=target?.stats || {};
  const metrics=[['最小延迟',stats.minLatencyMs,'ms'],['最大延迟',stats.maxLatencyMs,'ms'],['平均抖动',stats.jitterMs,'ms'],['成功 / 总探测',`${int(stats.received)} / ${int(stats.sent)}`,''],target?.protocol==='tcp'?['疑似重传率',stats.retransmitPercent,'%']:['最近延迟',stats.lastLatencyMs,'ms']];
  $('#detail-metrics').innerHTML=metrics.map(([label,value,unit])=>`<div class="detail-metric"><span>${label}</span><strong>${typeof value==='string'?value:num(value)}<small>${unit}</small></strong></div>`).join('');
  const series=(target?.series || []).filter(point=>point.timestamp).sort((a,b)=>new Date(a.timestamp)-new Date(b.timestamp));
  if(!series.length){$('#chart-bucket-info').textContent='';$('#chart-content').innerHTML=`<div class="chart-empty"><div class="empty-state"><div class="empty-icon">${icon('chart')}</div><h3>${target?'等待探测数据':'让网络表现有迹可循'}</h3><p>${target?(target.enabled?'首次探测完成后，这里将展示真实的延迟与丢包历史。':'此项目已暂停，可在项目列表中重新启用。'):'创建探测项目后，在这里查看延迟趋势、丢包和链路波动。'}</p></div></div>`;return;}
  drawChart(series,target);
}
function drawChart(series,target){
  const host=$('#chart-content');
  const compact=host.clientWidth<560;
  const width=Math.max(340,host.clientWidth-(compact?10:38)),left=compact?38:49,right=15,top=25,bottom=compact?175:221,lossTop=compact?206:251,lossBottom=compact?247:299,totalHeight=compact?277:328,plotWidth=width-left-right;
  const now=new Date(state.data.serverTime).getTime(),start=now-state.hours*3600000,end=now;
  const x=timestamp=>left+Math.max(0,Math.min(1,(new Date(timestamp).getTime()-start)/(end-start)))*plotWidth;
  const maxLatency=Math.max(1,...series.map(point=>point.maxLatencyMs ?? point.latencyMs ?? 0));
  const rough=maxLatency*1.18,power=10**Math.floor(Math.log10(rough)),scale=Math.ceil(rough/power/2)*power*2;
  const y=value=>bottom-Math.max(0,value)/scale*(bottom-top);
  const grid=Array.from({length:5},(_,index)=>{const value=scale*index/4,position=y(value);return `<line x1="${left}" x2="${width-right}" y1="${position}" y2="${position}" stroke="#edf2f5" stroke-dasharray="3 5"/><text x="${left-11}" y="${position+3}" text-anchor="end" class="chart-label">${num(value,value<10?1:0)}</text>`;}).join('');
  let groups=[],current=[];
  const gapLimit=Math.max(1000,state.hours*3600000/240)*1.5;
  for(const point of series){
    if(current.length&&new Date(point.timestamp)-new Date(current[current.length-1].timestamp)>gapLimit){groups.push(current);current=[];}
    if(point.latencyMs==null){if(current.length)groups.push(current);current=[];}else current.push(point);
  }
  if(current.length)groups.push(current);
  const lines=groups.map(group=>{
    const high=group.map(point=>`${x(point.timestamp).toFixed(2)},${y(point.maxLatencyMs??point.latencyMs).toFixed(2)}`).join(' '),low=[...group].reverse().map(point=>`${x(point.timestamp).toFixed(2)},${y(point.minLatencyMs??point.latencyMs).toFixed(2)}`).join(' ');
    const line=group.map(point=>`${x(point.timestamp).toFixed(2)},${y(point.latencyMs).toFixed(2)}`).join(' ');
    return `<polygon points="${high} ${low}" fill="#3ba89c" opacity=".12"/><polyline points="${line}" fill="none" stroke="#2d9f93" stroke-width="1.8" stroke-linejoin="round" stroke-linecap="round"/>${group.length===1?`<circle cx="${x(group[0].timestamp)}" cy="${y(group[0].latencyMs)}" r="2.5" fill="#2d9f93"/>`:''}`;
  }).join('');
  const barWidth=Math.max(1,Math.min(14,plotWidth/Math.max(series.length,1)*.75));
  const bars=series.map(point=>{const pointX=x(point.timestamp),loss=Math.max(0,Math.min(100,point.lossPercent || 0)),retrans=Math.max(0,Math.min(100,point.retransmitPercent || 0));return `${loss?`<rect x="${pointX-barWidth/2}" y="${lossBottom-loss/100*(lossBottom-lossTop)}" width="${barWidth}" height="${loss/100*(lossBottom-lossTop)}" rx="1" fill="#df8790"/>`:''}${target.protocol==='tcp'&&retrans?`<rect x="${pointX-barWidth/2}" y="${lossBottom-retrans/100*(lossBottom-lossTop)}" width="${barWidth/2}" height="${retrans/100*(lossBottom-lossTop)}" rx="1" fill="#dda65a"/>`:''}`;}).join('');
  const ticks=compact?2:5;
  const timeLabels=Array.from({length:ticks+1},(_,index)=>{const time=new Date(start+(end-start)*index/ticks);const label=state.hours>=24?time.toLocaleString('zh-CN',{month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',hour12:false}):time.toLocaleTimeString('zh-CN',{hour:'2-digit',minute:'2-digit',hour12:false});return `<text x="${left+plotWidth*index/ticks}" y="${totalHeight-8}" text-anchor="${index===0?'start':index===ticks?'end':'middle'}" class="chart-label">${label}</text>`;}).join('');
  host.innerHTML=`<svg class="chart-svg" viewBox="0 0 ${width} ${totalHeight}" role="img" aria-label="${esc(target.name)} 最近 ${state.hours} 小时的延迟和丢包趋势"><defs><clipPath id="plot-clip"><rect x="${left-1}" y="${top-2}" width="${plotWidth+2}" height="${lossBottom-top+3}"/></clipPath></defs>${grid}<text x="${left}" y="12" class="chart-unit">延迟 / ms</text><g clip-path="url(#plot-clip)">${lines}${bars}</g><line x1="${left}" x2="${width-right}" y1="${lossBottom}" y2="${lossBottom}" stroke="#edf2f5"/><text x="${left}" y="${lossTop-9}" class="chart-unit">丢包率 / %${target.protocol==='tcp'?' · 橙色为疑似重传率':''}</text><text x="${left-11}" y="${lossTop+5}" text-anchor="end" class="chart-label">100</text><text x="${left-11}" y="${lossBottom+2}" text-anchor="end" class="chart-label">0</text>${timeLabels}<line id="chart-crosshair" x1="0" x2="0" y1="${top}" y2="${lossBottom}" stroke="#a9bfc4" stroke-dasharray="3 4" visibility="hidden"/><circle id="chart-point" r="3.5" fill="#fff" stroke="#2d9f93" stroke-width="2" visibility="hidden"/><rect id="chart-hit-area" x="${left}" y="${top}" width="${plotWidth}" height="${lossBottom-top}" fill="transparent" tabindex="0" aria-label="延迟图表，使用左右方向键查看探测详情"/></svg><div class="chart-tooltip" hidden></div>`;
  $('#chart-bucket-info').textContent=`${series.length} 个数据点`;
  const tooltip=$('.chart-tooltip',host),svg=$('svg',host),cross=$('#chart-crosshair'),pointEl=$('#chart-point'),hit=$('#chart-hit-area');let activeIndex=series.length-1;
  function showPoint(index){
    activeIndex=Math.max(0,Math.min(series.length-1,index));const point=series[activeIndex],pointX=x(point.timestamp),rect=svg.getBoundingClientRect(),hostRect=host.getBoundingClientRect();
    cross.setAttribute('x1',pointX);cross.setAttribute('x2',pointX);cross.setAttribute('visibility','visible');
    if(point.latencyMs!=null){pointEl.setAttribute('cx',pointX);pointEl.setAttribute('cy',y(point.latencyMs));pointEl.setAttribute('visibility','visible');}else pointEl.setAttribute('visibility','hidden');
    tooltip.innerHTML=`<strong>${esc(date(point.timestamp,true))}</strong><p><span>平均延迟</span>${num(point.latencyMs)} ms</p><p><span>丢包率</span>${num(point.lossPercent)} %</p>${target.protocol==='tcp'?`<p><span>疑似重传率</span>${num(point.retransmitPercent)} %</p>`:''}<p><span>探测次数</span>${int(point.sent)}</p>`;tooltip.hidden=false;
    const natural=rect.left-hostRect.left+pointX/width*rect.width,half=tooltip.offsetWidth/2;
    tooltip.style.left=`${Math.max(half+5,Math.min(hostRect.width-half-5,natural))}px`;tooltip.style.top='29px';
  }
  function hidePoint(){tooltip.hidden=true;cross.setAttribute('visibility','hidden');pointEl.setAttribute('visibility','hidden');}
  hit.addEventListener('pointermove',event=>{const rect=svg.getBoundingClientRect(),chartX=(event.clientX-rect.left)/rect.width*width;let nearest=0,distance=Infinity;series.forEach((point,index)=>{const next=Math.abs(x(point.timestamp)-chartX);if(next<distance){distance=next;nearest=index;}});showPoint(nearest);});
  hit.addEventListener('pointerleave',hidePoint);hit.addEventListener('blur',hidePoint);hit.addEventListener('focus',()=>showPoint(activeIndex));
  hit.addEventListener('keydown',event=>{if(event.key==='ArrowRight'||event.key==='ArrowLeft'){event.preventDefault();showPoint(activeIndex+(event.key==='ArrowRight'?1:-1));}if(event.key==='Escape')hidePoint();});
}
function renderNodes(){
  const nodes=state.data?.nodes || [];
  $('#nodes-grid').innerHTML=nodes.map(node=>`<article class="panel node-card"><div class="node-card-header"><div class="node-icon">${icon('server')}</div><div><h3>${esc(node.name)}${node.isLocal?'<span class="local-badge">本机</span>':''}</h3><span class="status-pill ${node.online?'healthy':'pending'}">${node.online?'在线':'离线'}</span></div>${node.isLocal?'':`<button class="icon-button delete-node" data-id="${esc(node.id)}" title="移除节点" aria-label="移除 ${esc(node.name)}">${icon('trash')}</button>`}</div><dl><dt>节点地址</dt><dd>${esc(node.address || '尚未配置')}</dd><dt>关联项目</dt><dd>${(state.data?.targets || []).filter(target=>target.nodeIds.includes(node.id)).length} 个</dd><dt>最近心跳</dt><dd>${esc(date(node.lastSeen,true))}</dd></dl><div class="node-card-footer"><button class="button node-targets" data-id="${esc(node.id)}">查看探测项目</button><button class="button node-add" data-id="${esc(node.id)}">${icon('plus')}分配新项目</button></div></article>`).join('') || `<div class="panel empty-state"><div class="empty-icon">${icon('nodes')}</div><h3>暂无探测节点</h3><p>启动监测中心或接入 Agent 后，节点会自动出现在这里。</p><a class="button" href="#settings">查看部署说明</a></div>`;
  $$('.delete-node').forEach(button=>button.addEventListener('click',()=>{const node=nodes.find(item=>item.id===button.dataset.id);confirmAction('移除探测节点',`确定移除「${node.name}」？请先停止该节点的 Agent，避免它自动重新注册。关联项目将移除此执行节点。`,async()=>{await api(`/api/nodes/${encodeURIComponent(node.id)}`,{method:'DELETE'});if(state.node===node.id)state.node='';toast('节点已移除');await refresh();});}));
  $$('.node-targets').forEach(button=>button.addEventListener('click',()=>{state.node=button.dataset.id;state.search=state.group=state.protocol='';location.hash='targets';renderFilters();renderTargets();refresh();}));
  $$('.node-add').forEach(button=>button.addEventListener('click',()=>openTarget(null,button.dataset.id)));
}
async function loadRecent(id,silent=false){
  state.historyTarget=id;const target=state.data?.targets.find(item=>item.id===id);if(!target)return;
  $('#recent-panel').hidden=false;$('#recent-heading').textContent=`${target.name} · 最近探测记录`;
  if(!silent)$('#recent-content').innerHTML='<div class="empty-state"><div class="loading-spinner"></div></div>';
  try{const data=await api(`/api/targets/${encodeURIComponent(id)}/history?hours=${state.hours}${state.node?`&nodeId=${encodeURIComponent(state.node)}`:''}`);if(state.historyTarget!==id)return;state.history=data;
    const samples=data.recent || [];
    $('#recent-content').innerHTML=samples.length?`<div class="table-scroll"><table><thead><tr><th>时间</th><th>执行节点</th><th>延迟</th><th>结果</th><th>详细信息</th></tr></thead><tbody>${samples.map(sample=>`<tr><td>${esc(date(sample.timestamp,true))}</td><td>${esc(nodeName(sample.nodeId))}</td><td>${num(sample.latencyMs)}<span class="unit-muted">ms</span></td><td><span class="status-pill ${sample.success?(sample.suspectedRetransmit?'warning':'healthy'):'down'}">${sample.success?(sample.suspectedRetransmit?'疑似重传':'成功'):'失败'}</span></td><td title="${esc(sample.error)}">${esc(sample.error || '—')}</td></tr>`).join('')}</tbody></table></div>`:'<div class="empty-state"><h3>所选时段暂无探测记录</h3><p>请等待首次探测完成，或切换更长的时间范围。</p></div>';
  }catch(error){if(!silent)$('#recent-content').innerHTML=`<div class="empty-state"><p>${esc(error.message)}</p></div>`;}
}
function protocolFields(){const tcp=$('input[name=protocol]:checked').value==='tcp';$$('.tcp-field').forEach(element=>element.hidden=!tcp);$$('.icmp-field').forEach(element=>element.hidden=tcp);$('#target-size').required=!tcp;$('#target-size').disabled=tcp;for(const id of ['target-port','target-threshold','target-timeout']){$('#'+id).required=tcp;$('#'+id).disabled=!tcp;}}
function nodeOptions(container,selected=[],mesh=false){container.innerHTML=(state.data?.nodes || []).map(node=>`<label class="checkbox-option"><input type="checkbox" value="${esc(node.id)}"${selected.includes(node.id)?' checked':''}${mesh&&!node.address?' disabled':''}><span>${esc(node.name)}</span><small>${mesh&&!node.address?'未配置地址':node.online?'在线':'离线'}</small></label>`).join('') || '<p class="field-help">尚无可用节点，请先检查监测中心。</p>';}
function openTarget(target=null,nodeId=null){
  const form=$('#target-form');form.reset();formError('#target-error','');
  $('#target-dialog-title').textContent=target?'编辑探测项目':'添加探测项目';$('#save-target').textContent=target?'保存更改':'创建项目';
  $('#target-id').value=target?.id || '';$('#target-name').value=target?.name || '';$('#target-host').value=target?.host || '';$('#target-group').value=target?.group || '';
  $(`input[name=protocol][value=${target?.protocol || 'icmp'}]`).checked=true;
  $('#target-port').value=target?.port || 443;$('#target-size').value=target?.packetSize??32;$('#target-interval').value=target?.intervalSeconds || 5;$('#target-timeout').value=target?.timeoutMs || 2000;$('#target-threshold').value=target?.retransmitThresholdMs || 300;$('#target-enabled').checked=target?.enabled??true;
  const nodes=state.data?.nodes || [],local=nodes.find(node=>node.isLocal)?.id || 'local';
  nodeOptions($('#target-node-options'),target?.nodeIds || [nodeId || local]);
  const groups=[...new Set((state.data?.targets || []).map(item=>item.group || '默认分组'))];$('#group-options').innerHTML=groups.map(group=>`<option value="${esc(group)}"></option>`).join('');
  protocolFields();showDialog($('#target-dialog'));setTimeout(()=>$('#target-name').focus(),30);
}
function validIPv4(host){const parts=host.split('.');return parts.length===4&&parts.every(part=>/^(0|[1-9][0-9]{0,2})$/.test(part)&&Number(part)<=255);}
function validProbeHost(host){
  if(!host||host.length>253||!/^[A-Za-z0-9.:-]+$/.test(host))return false;
  if(host.includes(':')){
    let address=host;
    if(address.includes('.')){const lastColon=address.lastIndexOf(':');if(!validIPv4(address.slice(lastColon+1)))return false;address=address.slice(0,lastColon+1)+'0:0';}
    if(!/^[A-Fa-f0-9:]+$/.test(address))return false;
    const compressed=address.includes('::'),halves=address.split('::');if(halves.length>2)return false;
    const parts=compressed?halves.flatMap(half=>half?half.split(':'):[]):address.split(':');
    return parts.every(part=>/^[A-Fa-f0-9]{1,4}$/.test(part))&&(compressed?parts.length<8:parts.length===8);
  }
  if(/^[0-9.]+$/.test(host))return validIPv4(host);
  const numericParts=host.split('.');
  if(numericParts.length<=4&&numericParts.every(part=>/^(?:0x[0-9a-f]+|[0-9]+)$/i.test(part))){
    const values=numericParts.map(part=>/^0x/i.test(part)?Number.parseInt(part.slice(2),16):part.length>1&&part.startsWith('0')?(/^[0-7]+$/.test(part)?Number.parseInt(part,8):NaN):Number(part));
    if(values.every((value,index)=>Number.isFinite(value)&&value<=(index===values.length-1?256**(5-values.length)-1:255)))return false;
  }
  const name=host.endsWith('.')?host.slice(0,-1):host;
  return name.length>0&&name.split('.').every(label=>label.length<=63&&/^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?$/.test(label));
}
const hostValidationMessage='请输入有效的域名、IPv4 或 IPv6；禁止空格、特殊字符、协议、端口和路径，IPv4 不支持前导零。';
function targetPayload(){const protocol=$('input[name=protocol]:checked').value;return {name:$('#target-name').value.trim(),host:$('#target-host').value,group:$('#target-group').value.trim()||'默认分组',protocol,port:protocol==='tcp'?Number($('#target-port').value):443,packetSize:protocol==='icmp'?Number($('#target-size').value):32,intervalSeconds:Number($('#target-interval').value),timeoutMs:protocol==='tcp'?Number($('#target-timeout').value):2000,retransmitThresholdMs:protocol==='tcp'?Number($('#target-threshold').value):300,enabled:$('#target-enabled').checked,nodeIds:$$('#target-node-options input:checked').map(input=>input.value)};}
async function saveTarget(event){
  event.preventDefault();const body=targetPayload(),id=$('#target-id').value;formError('#target-error','');
  if(!body.name||!body.host)return formError('#target-error','请输入项目名称和主机地址。');
  if(!validProbeHost(body.host))return formError('#target-error',hostValidationMessage);
  if(!body.nodeIds.length)return formError('#target-error','请至少选择一个执行节点。');
  if(body.protocol==='tcp'&&body.retransmitThresholdMs>=body.timeoutMs)return formError('#target-error','疑似重传阈值必须小于连接超时时间。');
  $('#save-target').disabled=true;
  try{const result=await api(id?`/api/targets/${encodeURIComponent(id)}`:'/api/targets',{method:id?'PUT':'POST',body:JSON.stringify(body)});$('#target-dialog').close();toast(id?'探测项目已更新':'探测项目已创建');if(!id&&result?.id)state.selected=result.id;await refresh();}
  catch(error){formError('#target-error',error.message);}finally{$('#save-target').disabled=false;}
}
function confirmAction(title,description,action){
  $('#confirm-title').textContent=title;$('#confirm-description').textContent=description;formError('#confirm-error','');
  const button=$('#confirm-action');button.disabled=false;button.onclick=async()=>{button.disabled=true;try{await action();$('#confirm-dialog').close();}catch(error){formError('#confirm-error',error.message);}finally{button.disabled=false;}};showDialog($('#confirm-dialog'));
}
function meshFields(){const tcp=$('#mesh-protocol').value==='tcp';$$('.mesh-tcp').forEach(element=>element.hidden=!tcp);$$('.mesh-icmp').forEach(element=>element.hidden=tcp);for(const id of ['mesh-port','mesh-timeout','mesh-threshold']){$('#'+id).required=tcp;$('#'+id).disabled=!tcp;}$('#mesh-size').required=!tcp;$('#mesh-size').disabled=tcp;}
function openMesh(){$('#mesh-form').reset();formError('#mesh-error','');nodeOptions($('#mesh-node-options'),[],true);meshFields();showDialog($('#mesh-dialog'));}
async function saveMesh(event){event.preventDefault();formError('#mesh-error','');const protocol=$('#mesh-protocol').value,body={nodeIds:$$('#mesh-node-options input:checked').map(input=>input.value),protocol,port:Number($('#mesh-port').value),packetSize:Number($('#mesh-size').value),intervalSeconds:Number($('#mesh-interval').value),timeoutMs:protocol==='tcp'?Number($('#mesh-timeout').value):2000,retransmitThresholdMs:Number($('#mesh-threshold').value)};
  if(body.nodeIds.length<2)return formError('#mesh-error','请选择至少两个已配置节点地址的节点。');
  if(protocol==='tcp'&&body.retransmitThresholdMs>=body.timeoutMs)return formError('#mesh-error','疑似重传阈值必须小于连接超时时间。');
  $('#save-mesh').disabled=true;try{const result=await api('/api/mesh',{method:'POST',body:JSON.stringify(body)});$('#mesh-dialog').close();toast(`已创建 ${typeof result.created==='number'?result.created:Array.isArray(result.created)?result.created.length:'所选'} 条互测链路`);await refresh();}catch(error){formError('#mesh-error',error.message);}finally{$('#save-mesh').disabled=false;}}
function traceActive(){return ['queued','running'].includes(traceState.job?.status);}
function traceNodeLabel(id){const node=state.data?.nodes.find(item=>item.id===id);return node?`${node.name}${node.isLocal?'（本机）':''}`:id==='local'?'本机节点':id;}
function traceCapabilities(){
  const nodeId=$('#trace-node').value,localId=state.data?.nodes.find(node=>node.isLocal)?.id||'local';
  if(nodeId===localId){
    if(!traceState.tool)return {available:false,supportsTcp:false,supportsPsize:false,message:'正在检测本机追踪工具，请稍候。'};
    if(!traceState.tool.available)return {available:false,supportsTcp:false,supportsPsize:false,message:'本机未找到可用的 ntrace 或 Windows tracert，请安装工具后刷新页面。'};
    const supportsTcp=traceState.tool.supportsTcp===true,supportsPsize=traceState.tool.supportsPsize===true;
    return {available:true,supportsTcp,supportsPsize,message:supportsTcp?'本机使用 ntrace，支持 ICMP、TCP 和自定义数据包总大小。':'本机使用 Windows tracert，仅支持 ICMP；TCP 和自定义包大小需要安装 ntrace。'};
  }
  const node=state.data?.nodes.find(item=>item.id===nodeId);
  if(!node||!node.online)return {available:false,supportsTcp:false,supportsPsize:false,message:'请选择在线的执行节点。'};
  const version=Number(node.traceVersion)||0;
  if(version<2)return {available:true,supportsTcp:false,supportsPsize:false,message:'该节点的 Agent 版本较旧或未声明新参数支持，仅支持基础 ICMP；使用 TCP 或自定义包大小前请更新并重启 Agent。'};
  return {available:true,supportsTcp:true,supportsPsize:true,message:'远程 Agent 已支持新参数；请保留发布包随附的 ntrace 目录，TCP 追踪还需管理员权限，工具不可用时会明确报错。'};
}
function traceMinimumPsize(host,protocol){return (host.includes(':')?40:20)+(protocol==='tcp'?20:8);}
function renderTracePacketHelp(){
  const host=$('#trace-host').value,protocol=$('#trace-protocol').value,min=traceMinimumPsize(host,protocol),literalV6=host.includes(':'),literalV4=/^\d+(?:\.\d+){3}$/.test(host);
  $('#trace-psize-help').textContent=`数据包总大小含 IP 和协议头部；${literalV6?'IPv6':literalV4?'IPv4':'IPv4'} ${protocol.toUpperCase()} 为 ${min}–65,535 bytes。${!literalV4&&!literalV6?'域名按 IPv4 最小值预检，实际地址族由 ntrace 校验。':''}留空使用 ntrace 默认值。`;
}
function renderTraceNodes(){
  const select=$('#trace-node'),previous=select.value||'local',nodes=state.data?.nodes||[],local=nodes.find(node=>node.isLocal);
  const options=[...(local?[local]:[{id:'local',name:'本机节点',isLocal:true,online:true}]),...nodes.filter(node=>!node.isLocal)];
  select.innerHTML=options.map(node=>`<option value="${esc(node.id)}"${node.id===previous?' selected':''}${!node.isLocal&&!node.online?' disabled':''}>${esc(node.name)}${node.isLocal?'（本机）':node.online?'':'（离线）'}</option>`).join('');
  if(!options.some(node=>node.id===previous)){
    if(previous!=='local'){const option=document.createElement('option');option.value=previous;option.textContent=`${previous}（节点已移除）`;option.disabled=true;option.selected=true;select.append(option);}
    else select.value=local?.id||'local';
  }
  if(!traceState.job){$('#trace-source-label').textContent=traceNodeLabel(select.value);renderTraceTool();}
  renderTraceControls();
}
function renderTraceTool(){
  if(traceState.job)return;
  const local=state.data?.nodes.find(node=>node.isLocal)?.id||'local';
  $('#trace-tool-label').textContent=$('#trace-node').value!==local?'由执行节点选择':traceState.tool?(traceState.tool.available?(traceState.tool.tool||'自动选择'):'工具不可用'):'自动选择';
}
async function loadTraceTools(){
  if(traceState.toolLoading)return;traceState.toolLoading=true;
  try{traceState.tool=await api('/api/traces/tools');renderTraceTool();}catch(error){formError('#trace-error',error.message);}finally{traceState.toolLoading=false;renderTraceControls();}
}
function renderTraceControls(){
  const active=traceActive(),locked=active||traceState.submitting,capabilities=traceCapabilities(),tcp=$('#trace-protocol').value==='tcp',hasPsize=$('#trace-psize').value!=='';
  $$('#trace-form input, #trace-form select').forEach(input=>input.disabled=locked);
  $('#trace-protocol option[value="tcp"]').disabled=!capabilities.supportsTcp;
  $('#trace-protocol-field').classList.toggle('full',!tcp);$('#trace-port-field').hidden=!tcp;
  $('#trace-port').required=tcp;$('#trace-port').disabled=locked||!tcp||!capabilities.supportsTcp;
  $('#trace-psize').disabled=locked||!capabilities.supportsPsize;
  $('#trace-reset-psize').hidden=capabilities.supportsPsize||!hasPsize;$('#trace-reset-psize').disabled=locked;
  const incompatible=tcp&&!capabilities.supportsTcp||hasPsize&&!capabilities.supportsPsize;
  $('#trace-capability-help').textContent=capabilities.message+(tcp&&!capabilities.supportsTcp?' 当前仍选中 TCP，请选择支持的节点或手动切换为 ICMP。':'')+(hasPsize&&!capabilities.supportsPsize?' 请清空自定义数据包大小或选择支持的节点。':'');
  $('#trace-capability-help').classList.toggle('trace-capability-warning',incompatible||!capabilities.available);
  $('#start-trace').disabled=locked||!capabilities.available||incompatible;$('#cancel-trace').disabled=!active||traceState.cancelling;
  $('#start-trace').innerHTML=icon('play')+(traceState.submitting?'正在启动…':active?'追踪进行中':'开始追踪');
  $('#cancel-trace').innerHTML=icon('close')+(traceState.cancelling?'正在停止…':'停止');
  renderTracePacketHelp();
}
function renderTraceJob(){
  const job=traceState.job;if(!job)return;
  const statuses={queued:['pending','等待执行'],running:['healthy','正在追踪'],completed:['healthy','已完成'],failed:['down','执行失败'],timedOut:['warning','已超时'],cancelled:['paused','已停止']};
  const [kind,label]=statuses[job.status]||['pending','等待状态'];
  $('#trace-status').className=`status-pill ${kind}${traceActive()?' trace-active-status':''}`;$('#trace-status').textContent=label;
  $('#trace-source-label').textContent=traceNodeLabel(job.nodeId);$('#trace-host-label').textContent=job.host;$('#trace-tool-label').textContent=job.tool||(['queued','running'].includes(job.status)?'等待执行节点选择':'—');
  const protocol=job.protocol||'icmp';$('#trace-protocol-label').textContent=protocol.toUpperCase();$('#trace-port-label').textContent=protocol==='tcp'?String(job.port??'—'):'不适用';$('#trace-psize-label').textContent=job.psize!=null?`${job.psize} bytes`:'工具默认值';
  const output=$('#trace-output'),stick=output.scrollHeight-output.scrollTop-output.clientHeight<40;
  const next=job.output||(job.status==='queued'?'任务已排队，等待执行节点领取…':job.status==='running'?'正在启动路由追踪，等待工具输出…':'此次任务未产生输出。');
  if(output.textContent!==next){output.textContent=next;if(stick)output.scrollTop=output.scrollHeight;}
  $('#trace-output-limit').textContent=job.truncated?'输出已截断（65,536 字符上限）':'输出上限 65,536 字符';$('#trace-output-limit').classList.toggle('trace-truncated',Boolean(job.truncated));
  formError('#trace-job-error',job.error||'');
  const started=job.startedAt?`开始于 ${date(job.startedAt)}`:`提交于 ${date(job.createdAt)}`;
  $('#trace-timing').textContent=`${started}${job.finishedAt?` · 结束于 ${date(job.finishedAt)}`:''}${job.exitCode!=null?` · 退出码 ${job.exitCode}`:''}`;
  renderTraceControls();
}
function acceptTraceSnapshot(job){if(traceState.job?.id===job.id&&!traceActive()&&['queued','running'].includes(job.status))return;traceState.job=job;renderTraceJob();}
function scheduleTracePoll(delay=1000){clearTimeout(traceState.timer);if(traceActive())traceState.timer=setTimeout(pollTrace,delay);}
async function pollTrace(){
  if(!traceActive()||traceState.polling)return;if($('#auth-dialog').open){scheduleTracePoll();return;}traceState.polling=true;const id=traceState.job.id;
  try{const job=await api(`/api/traces/${encodeURIComponent(id)}`);if(traceState.job?.id===id){formError('#trace-error','');acceptTraceSnapshot(job);}}
  catch(error){if(error.status===404&&traceState.job?.id===id){acceptTraceSnapshot({...traceState.job,status:'failed',finishedAt:new Date().toISOString(),error:'追踪任务已不存在，监测中心可能已重启。请重新开始追踪。'});formError('#trace-error','');}else formError('#trace-error',`状态刷新失败：${error.message} 将自动重试。`);}
  finally{traceState.polling=false;scheduleTracePoll();}
}
async function startTrace(event){
  event.preventDefault();if(traceActive()||traceState.submitting)return;formError('#trace-error','');
  const host=$('#trace-host').value,nodeId=$('#trace-node').value,maxHops=Number($('#trace-hops').value),timeoutMs=Number($('#trace-timeout').value),protocol=$('#trace-protocol').value,portText=$('#trace-port').value,psizeText=$('#trace-psize').value;
  if(!validProbeHost(host))return formError('#trace-error',hostValidationMessage);
  if(!['icmp','tcp'].includes(protocol))return formError('#trace-error','探测协议必须是 ICMP 或 TCP。');
  const port=protocol==='tcp'?Number(portText):null,psize=psizeText===''?null:Number(psizeText),minPsize=traceMinimumPsize(host,protocol);
  if(protocol==='tcp'&&(!/^[0-9]+$/.test(portText)||!Number.isInteger(port)||port<1||port>65535))return formError('#trace-error','TCP 端口为必填项，必须是 1–65535 之间的纯数字整数，默认 443。');
  if(psizeText!==''&&(!/^[0-9]+$/.test(psizeText)||!Number.isInteger(psize)||psize<minPsize||psize>65535))return formError('#trace-error',`数据包总大小必须是 ${minPsize}–65535 之间的纯数字整数（包含 IP 和协议头部），或留空使用默认值。`);
  if(!Number.isInteger(maxHops)||maxHops<1||maxHops>64)return formError('#trace-error','最大跳数必须是 1–64 之间的整数。');
  if(!Number.isInteger(timeoutMs)||timeoutMs<100||timeoutMs>5000)return formError('#trace-error','每跳超时必须是 100–5000 ms 之间的整数。');
  const node=state.data?.nodes.find(item=>item.id===nodeId);
  if(!nodeId||(node&&!node.isLocal&&!node.online)||$('#trace-node').selectedOptions[0]?.disabled)return formError('#trace-error','请选择在线的执行节点。');
  const capabilities=traceCapabilities();
  if(!capabilities.available||protocol==='tcp'&&!capabilities.supportsTcp||psize!==null&&!capabilities.supportsPsize)return formError('#trace-error',capabilities.message);
  traceState.submitting=true;renderTraceControls();
  try{const job=await api('/api/traces',{method:'POST',body:JSON.stringify({host,nodeId,maxHops,timeoutMs,protocol,port,psize})});traceState.job=job;$('#trace-output').scrollTop=0;renderTraceJob();scheduleTracePoll();}
  catch(error){formError('#trace-error',error.message);}
  finally{traceState.submitting=false;renderTraceControls();}
}
async function cancelTrace(){
  if(!traceActive()||traceState.cancelling)return;traceState.cancelling=true;renderTraceControls();const id=traceState.job.id;
  try{const job=await api(`/api/traces/${encodeURIComponent(id)}`,{method:'DELETE'});if(traceState.job?.id===id){formError('#trace-error','');acceptTraceSnapshot(job);scheduleTracePoll();}}
  catch(error){formError('#trace-error',error.message);}
  finally{traceState.cancelling=false;renderTraceControls();}
}
function openTrace(target){
  location.hash='trace';if(traceActive()||traceState.submitting){toast('当前追踪正在执行，请结束后再选择新的目标。');return;}
  $('#trace-host').value=target.host;renderTraceNodes();
  const nodes=state.data?.nodes||[],preferred=nodes.find(node=>target.nodeIds.includes(node.id)&&(node.isLocal||node.online));
  if(preferred)$('#trace-node').value=preferred.id;
  $('#trace-protocol').value=target.protocol==='tcp'?'tcp':'icmp';$('#trace-port').value=String(target.protocol==='tcp'?(target.port||443):443);renderTraceControls();renderTraceTool();
  formError('#trace-error','');$('#trace-host').focus();
}
async function loadSettings(){try{state.settings=await api('/api/settings');renderSettings();}catch(error){toast(error.message,true);}}
function renderSettings(){
  const settings=state.settings || state.data?.settings || {};
  const rows=[['运行模式',settings.mode==='agent'?'Agent 探测节点':'Server 监测中心'],['节点名称',settings.nodeName || '—'],['节点地址',settings.nodeAddress || '尚未配置'],['历史保留',`${settings.retentionDays??'—'} 天`],['管理员验证',settings.adminTokenConfigured?'已启用':'未配置'],['Agent 验证',settings.agentTokenConfigured?'已启用':'未配置']];
  $('#settings-details').innerHTML=rows.map(([label,value])=>`<dt>${label}</dt><dd>${esc(value)}</dd>`).join('');
  const url=settings.serverUrl && !/127\.0\.0\.1|localhost/.test(settings.serverUrl)?settings.serverUrl:'http://center.example.test:5080';
  $('#agent-command').textContent=`$env:SmartPing__Mode = "agent"\n$env:SmartPing__ServerUrl = "${url.replace(/["\r\n`$]/g,'')}"\n$env:SmartPing__NodeName = "Agent A"\n$env:SmartPing__NodeAddress = "agent-a.example.test"\n$env:SmartPing__AgentToken = "替换为与服务器相同的令牌"\n.\\SmartPing.exe`;
}
function setToken(token){state.token=token.trim();if(state.token)sessionStorage.setItem('smartping.adminToken',state.token);else sessionStorage.removeItem('smartping.adminToken');}
async function authenticate(event){event.preventDefault();setToken($('#auth-token').value);formError('#auth-error','');const button=$('button[type=submit]',$('#auth-form'));button.disabled=true;try{await api('/api/settings');$('#auth-dialog').close();state.settings=null;await refresh();toast('已连接监测中心');}catch(error){formError('#auth-error',error.message);}finally{button.disabled=false;}}
function navigate(){let view=location.hash.slice(1);if(!labels[view])view='overview';state.view=view;$$('.view').forEach(section=>section.hidden=section.id!==`view-${view}`);$$('.nav-link').forEach(link=>{const active=link.dataset.view===view;link.classList.toggle('active',active);if(active)link.setAttribute('aria-current','page');else link.removeAttribute('aria-current');});$('#breadcrumb').textContent=labels[view];document.title=`${labels[view]} · SmartPing`;if(view==='settings')loadSettings();if(view==='trace'){renderTraceNodes();loadTraceTools();}}
function start(){
  fillIcons();renderSummary();renderInitialError();renderFilters();renderTargets();renderSettings();renderTraceControls();navigate();
  $$('.add-target').forEach(button=>button.addEventListener('click',()=>openTarget()));
  $$('.close-modal').forEach(button=>button.addEventListener('click',()=>button.closest('dialog').close()));
  $$('dialog').forEach(dialog=>dialog.addEventListener('click',event=>{if(event.target===dialog&&dialog.id!=='auth-dialog'){const rect=dialog.getBoundingClientRect();if(event.clientX<rect.left||event.clientX>rect.right||event.clientY<rect.top||event.clientY>rect.bottom)dialog.close();}}));
  $('#auth-dialog').addEventListener('cancel',event=>event.preventDefault());
  $('#refresh-button').addEventListener('click',()=>refresh());$('#retry-button').addEventListener('click',()=>refresh());
  $$('.range-selector button').forEach(button=>button.addEventListener('click',()=>{state.hours=Number(button.dataset.hours);$$('.range-selector button').forEach(item=>item.classList.toggle('active',item===button));refresh();}));
  $('#chart-target').addEventListener('change',event=>{state.selected=event.target.value;renderChart();renderTargets();});
  $('#target-form').addEventListener('submit',saveTarget);$$('input[name=protocol]').forEach(input=>input.addEventListener('change',protocolFields));
  $('#trace-form').addEventListener('submit',startTrace);$('#cancel-trace').addEventListener('click',cancelTrace);
  $('#trace-node').addEventListener('change',()=>{if(!traceState.job){$('#trace-source-label').textContent=traceNodeLabel($('#trace-node').value);renderTraceTool();}renderTraceControls();});
  $('#trace-protocol').addEventListener('change',renderTraceControls);$('#trace-host').addEventListener('input',renderTracePacketHelp);
  $('#trace-psize').addEventListener('input',renderTraceControls);$('#trace-reset-psize').addEventListener('click',()=>{$('#trace-psize').value='';renderTraceControls();formError('#trace-error','');});
  for(const id of ['trace-port','trace-psize']){
    const input=$('#'+id),message='端口和数据包大小仅允许纯数字整数，禁止空格、小数、正负号、科学计数法和特殊字符。';
    input.addEventListener('beforeinput',event=>{const inserted=event.data??event.dataTransfer?.getData('text/plain');if(typeof inserted==='string'&&/[^0-9]/.test(inserted)){event.preventDefault();formError('#trace-error',message);}});
    input.addEventListener('paste',event=>{const inserted=event.clipboardData?.getData('text');if(inserted&&/[^0-9]/.test(inserted)){event.preventDefault();formError('#trace-error',message);}});
    input.addEventListener('drop',event=>{const inserted=event.dataTransfer?.getData('text/plain');if(inserted&&/[^0-9]/.test(inserted)){event.preventDefault();formError('#trace-error',message);}});
  }
  for(const id of ['target-host','trace-host']){
    const input=$('#'+id),errorId=id==='trace-host'?'#trace-error':'#target-error';
    input.addEventListener('beforeinput',event=>{const inserted=event.data??event.dataTransfer?.getData('text/plain');if(typeof inserted==='string'&&/[^A-Za-z0-9.:-]/.test(inserted)){event.preventDefault();formError(errorId,'禁止输入空格或特殊字符；主机地址仅允许 ASCII 字母、数字、点、短横线和冒号。');}});
    input.addEventListener('paste',event=>{const pasted=event.clipboardData?.getData('text');if(pasted&&/[^A-Za-z0-9.:-]/.test(pasted)){event.preventDefault();formError(errorId,'粘贴内容包含空格或禁止的字符，请输入完整的纯主机地址。');}});
    input.addEventListener('drop',event=>{const dropped=event.dataTransfer?.getData('text/plain');if(dropped&&/[^A-Za-z0-9.:-]/.test(dropped)){event.preventDefault();formError(errorId,'拖放内容包含空格或禁止的字符，请输入完整的纯主机地址。');}});
  }
  $('#mesh-button').addEventListener('click',openMesh);$('#mesh-protocol').addEventListener('change',meshFields);$('#mesh-form').addEventListener('submit',saveMesh);
  $('#close-recent').addEventListener('click',()=>{state.historyTarget='';$('#recent-panel').hidden=true;});
  $('#auth-form').addEventListener('submit',authenticate);
  $('#token-settings-form').addEventListener('submit',async event=>{event.preventDefault();setToken($('#settings-token').value);$('#settings-token').value='';state.settings=null;await refresh();if(!$('#auth-dialog').open)toast('会话凭据已更新');});
  $('#clear-token').addEventListener('click',()=>{setToken('');$('#settings-token').value='';toast('会话凭据已清除');refresh();});
  $('#copy-agent').addEventListener('click',async()=>{try{await navigator.clipboard.writeText($('#agent-command').textContent);toast('部署命令已复制');}catch{const selection=window.getSelection(),range=document.createRange();range.selectNodeContents($('#agent-command'));selection.removeAllRanges();selection.addRange(range);toast('命令已选中，请按 Ctrl+C 复制');}});
  window.addEventListener('hashchange',navigate);document.addEventListener('visibilitychange',()=>{if(!document.hidden)refresh({silent:true});});
  let resizeTimer;window.addEventListener('resize',()=>{clearTimeout(resizeTimer);resizeTimer=setTimeout(()=>{if(state.data)renderChart();},160);});
  refresh();refreshTimer=setInterval(()=>{if(!document.hidden&&!$$('dialog').some(dialog=>dialog.open))refresh({silent:true});},5000);
}
start();
