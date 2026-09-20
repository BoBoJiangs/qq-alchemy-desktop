const $ = id => document.getElementById(id);
let currentSettings = null;

async function api(path, options = {}) {
  const response = await fetch(path, { headers: { 'Content-Type': 'application/json', ...(options.headers || {}) }, ...options });
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try { const body = await response.json(); message = body.detail || body.message || message; } catch {}
    throw new Error(message);
  }
  if (response.status === 204) return null;
  return response.json();
}

function toast(message, error = false) {
  const node = $('toast'); node.textContent = message; node.className = `toast${error ? ' error' : ''}`;
  setTimeout(() => node.classList.add('hidden'), 4200);
}

function stateClass(state) {
  if (['PausedCaptcha','PausedRecovery','Faulted'].includes(state)) return 'bad';
  if (['Idle','Completed'].includes(state)) return 'ok';
  return 'warn';
}

function list(container, entries, render) {
  container.innerHTML = '';
  container.classList.toggle('empty', entries.length === 0);
  if (!entries.length) { container.textContent = '暂无数据'; return; }
  entries.forEach(entry => { const row = document.createElement('div'); row.className = 'item'; row.innerHTML = render(entry); container.appendChild(row); });
}

async function refreshStatus() {
  try {
    const s = await api('/api/status');
    $('statePill').textContent = s.state; $('statePill').className = `pill ${stateClass(s.state)}`;
    $('qqConnected').textContent = s.qqConnected ? '已发现' : '未发现';
    $('calibrationValid').textContent = s.calibrationValid ? '已验证' : '未验证';
    $('task').textContent = s.task; $('dryRun').textContent = s.dryRun ? '演练' : '真实操作';
    $('step').textContent = s.step || '—'; $('updatedAt').textContent = new Date(s.updatedAt).toLocaleString();
    $('lastError').textContent = s.lastError || '暂无'; $('ocrText').textContent = s.lastOcrText || '暂无 OCR 结果';
    const alert = $('alert'); const dangerous = ['PausedCaptcha','PausedRecovery','Faulted'].includes(s.state);
    alert.classList.toggle('hidden', !dangerous); alert.textContent = dangerous ? `自动化已暂停：${s.lastError || s.step}` : '';
    const shot = $('failureShot'); shot.classList.toggle('hidden', !s.lastScreenshot); if (s.lastScreenshot) shot.src = `/api/screenshots/latest?t=${Date.now()}`;
    const inv = Object.entries(s.inventory || {}).sort((a,b) => a[0].localeCompare(b[0],'zh-CN'));
    $('inventoryCount').textContent = `${inv.length} 种`; list($('inventory'), inv, x => `<span>${escapeHtml(x[0])}</span><span class="tag">${x[1]}</span>`);
    $('candidateCount').textContent = `${s.candidates.length} 项`; list($('candidates'), s.candidates, x => `<span>${escapeHtml(x.herbName)} · 第 ${x.page} 页</span><span class="tag">${x.priceWan} 万</span>`);
    $('queueCount').textContent = `${s.alchemyQueue.length} 条`; list($('queue'), s.alchemyQueue, x => `<code>${escapeHtml(x)}</code>`);
  } catch (error) { $('statePill').textContent = '服务异常'; $('statePill').className = 'pill bad'; }
}

async function refreshSettings() {
  currentSettings = await api('/api/settings'); const s = currentSettings.alchemy || {};
  $('danNumber').value = s.danNumber ?? 6; $('alchemyMode').value = String(s.alchemy !== false); $('alchemyNumber').value = s.alchemyNumber ?? 30; $('makeNumber').value = s.makeNumber ?? 1000;
  $('taskPurchaseLimit').value = s.taskPurchaseLimit ?? 50; $('emptyRounds').value = s.emptyMarketRoundsBeforeStop ?? 3;
  $('limitHerbsCount').value = s.limitHerbsCount ?? 30; $('randomDelay').value = s.randomDelay ?? 0; $('dryRunSetting').checked = s.dryRun !== false;
  $('allowUnmentionedSetting').checked = s.allowUnmentionedCommands === true;
  $('rulesText').value = (currentSettings.purchaseRules || []).map(r => `${r.repeatPurchase ? 'repeat' : 'normal'}|${r.maxPriceWan}|${r.inventoryLimit}|${r.herbName}`).join('\n');
  const c = currentSettings.calibration; if (c) { $('groupName').value = c.groupName || ''; $('botName').value = c.gameBotDisplayName || '小小'; $('botQq').value = c.gameBotQq || 3889001741; }
}

async function refreshAudit() {
  try { const rows = await api('/api/audit?count=60'); $('audit').innerHTML = rows.map(x => `<tr><td>${new Date(x.occurredAt).toLocaleString()}</td><td>${escapeHtml(x.level)}</td><td>${escapeHtml(x.eventType)}</td><td>${escapeHtml(x.detail)}</td><td>${escapeHtml(x.actionId || '')}</td></tr>`).join(''); } catch {}
}

function escapeHtml(value) { return String(value).replace(/[&<>'"]/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[ch])); }
async function busy(button, action) { button.disabled = true; try { await action(); } catch (error) { toast(error.message, true); } finally { button.disabled = false; } }

document.querySelectorAll('[data-action]').forEach(button => button.addEventListener('click', () => busy(button, async () => { await api(button.dataset.action, { method:'POST' }); toast('操作已提交'); await refreshStatus(); await refreshAudit(); })));
$('importBtn').addEventListener('click', () => busy($('importBtn'), async () => { const result = await api('/api/import', { method:'POST', body:JSON.stringify({ sourceRoot:$('sourceRoot').value, accountId:$('accountId').value }) }); $('importResult').textContent = `已复制 ${result.import.copied.length} 个文件，生成 ${result.recipeCount} 条规范化配方。`; toast('旧数据导入完成'); await refreshSettings(); await refreshAudit(); }));
$('calibrateBtn').addEventListener('click', () => busy($('calibrateBtn'), async () => { const result = await api('/api/calibration/start', { method:'POST', body:JSON.stringify({ groupName:$('groupName').value, gameBotDisplayName:$('botName').value, gameBotQq:Number($('botQq').value) }) }); $('calibrationResult').textContent = `检测到 QQ ${result.qqVersion || '未知版本'}，窗口 ${result.windowWidth}×${result.windowHeight}，DPI ${result.dpi}。`; toast('窗口探测完成，请继续验证'); }));
$('verifyBtn').addEventListener('click', () => busy($('verifyBtn'), async () => {
  const alchemy = { ...(currentSettings?.alchemy || {}), allowUnmentionedCommands:$('allowUnmentionedSetting').checked };
  await api('/api/settings/alchemy', { method:'PUT', body:JSON.stringify(alchemy) });
  currentSettings.alchemy = alchemy;
  await api('/api/calibration/verify', { method:'POST' });
  $('calibrationResult').textContent = '校准验证通过。'; toast('校准验证通过'); await refreshStatus();
}));
$('saveSettingsBtn').addEventListener('click', () => busy($('saveSettingsBtn'), async () => { const s = { ...(currentSettings?.alchemy || {}), danNumber:Number($('danNumber').value), alchemy:$('alchemyMode').value === 'true', alchemyNumber:Number($('alchemyNumber').value), makeNumber:Number($('makeNumber').value), taskPurchaseLimit:Number($('taskPurchaseLimit').value), emptyMarketRoundsBeforeStop:Number($('emptyRounds').value), limitHerbsCount:Number($('limitHerbsCount').value), randomDelay:Number($('randomDelay').value), dryRun:$('dryRunSetting').checked, allowUnmentionedCommands:$('allowUnmentionedSetting').checked }; await api('/api/settings/alchemy', { method:'PUT', body:JSON.stringify(s) }); currentSettings.alchemy = s; toast('安全设置已保存'); await refreshStatus(); }));
$('saveRulesBtn').addEventListener('click', () => busy($('saveRulesBtn'), async () => {
  const rules = $('rulesText').value.split(/\r?\n/).map((line, index) => line.trim()).filter(Boolean).map((line, index) => {
    const [mode, price, limit, ...name] = line.split('|').map(x => x.trim());
    if (!name.length || !Number.isFinite(Number(price)) || !Number.isFinite(Number(limit))) throw new Error(`第 ${index + 1} 行采购规则格式错误`);
    return { herbName:name.join('|'), maxPriceWan:Number(price), inventoryLimit:Number(limit), order:index, repeatPurchase:mode.toLowerCase() === 'repeat' };
  });
  await api('/api/settings/purchase-rules', { method:'PUT', body:JSON.stringify(rules) }); currentSettings.purchaseRules = rules; toast(`已保存 ${rules.length} 条采购规则`); await refreshAudit();
}));

Promise.all([refreshStatus(), refreshSettings(), refreshAudit()]).catch(error => toast(error.message, true));
setInterval(refreshStatus, 1500); setInterval(refreshAudit, 8000);
