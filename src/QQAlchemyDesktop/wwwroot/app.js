const $ = id => document.getElementById(id);
let currentSettings = null;
let herbCatalog = [];
let purchaseRules = [];

const gradeNames = ['', '一品', '二品', '三品', '四品', '五品', '六品', '七品', '八品', '九品'];

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
  currentSettings = await api('/api/settings');
  try { herbCatalog = await api('/api/herbs/catalog'); } catch { herbCatalog = []; }
  purchaseRules = (currentSettings.purchaseRules || []).map((rule, index) => ({ ...rule, order: index }));
  renderGradeFilter(); renderPurchaseRules();
  const s = currentSettings.alchemy || {};
  $('danNumber').value = s.danNumber ?? 6; $('alchemyMode').value = String(s.alchemy !== false); $('alchemyNumber').value = s.alchemyNumber ?? 30; $('makeNumber').value = s.makeNumber ?? 1000;
  $('taskPurchaseLimit').value = s.taskPurchaseLimit ?? 50; $('emptyRounds').value = s.emptyMarketRoundsBeforeStop ?? 3;
  $('limitHerbsCount').value = s.limitHerbsCount ?? 30; $('randomDelay').value = s.randomDelay ?? 0; $('dryRunSetting').checked = s.dryRun !== false;
  $('allowUnmentionedSetting').checked = s.allowUnmentionedCommands === true;
  const c = currentSettings.calibration; if (c) { $('groupName').value = c.groupName || ''; $('botName').value = c.gameBotDisplayName || '小小'; $('botQq').value = c.gameBotQq || 3889001741; }
}

async function refreshAudit() {
  try { const rows = await api('/api/audit?count=60'); $('audit').innerHTML = rows.map(x => `<tr><td>${new Date(x.occurredAt).toLocaleString()}</td><td>${escapeHtml(x.level)}</td><td>${escapeHtml(x.eventType)}</td><td>${escapeHtml(x.detail)}</td><td>${escapeHtml(x.actionId || '')}</td></tr>`).join(''); } catch {}
}

function escapeHtml(value) { return String(value).replace(/[&<>'"]/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[ch])); }
async function busy(button, action) { button.disabled = true; try { await action(); } catch (error) { toast(error.message, true); } finally { button.disabled = false; } }

function gradeLabel(grade) { return gradeNames[Number(grade)] || '未分级'; }

function herbMeta(name) {
  return herbCatalog.find(item => item.name === name) || { name, price: 0, grade: 0 };
}

function renderGradeFilter() {
  const selected = $('ruleGradeFilter').value;
  $('ruleGradeFilter').innerHTML = '<option value="">全部品级</option>' +
    herbCatalog.map(item => item.grade).filter((value, index, values) => value > 0 && values.indexOf(value) === index)
      .sort((a, b) => a - b)
      .map(grade => `<option value="${grade}">${gradeLabel(grade)}药材</option>`).join('');
  $('ruleGradeFilter').value = selected;
}

function renderNewRuleHerbs() {
  const selected = $('newRuleHerb').value;
  const used = new Set(purchaseRules.map(rule => rule.herbName));
  const available = herbCatalog.filter(item => !used.has(item.name));
  $('newRuleHerb').innerHTML = '<option value="">请选择药材</option>' + available
    .map(item => `<option value="${escapeHtml(item.name)}">${escapeHtml(item.name)} · ${gradeLabel(item.grade)} · 参考 ${item.price || '未设置'} 万</option>`).join('');
  if (available.some(item => item.name === selected)) $('newRuleHerb').value = selected;
  if (!$('newRuleHerb').value && available.length) $('newRuleHerb').value = available[0].name;
  updateNewRulePrice();
}

function updateNewRulePrice() {
  const item = herbMeta($('newRuleHerb').value);
  if (item.price > 0 && (!$('newRulePrice').value || $('newRulePrice').value === '0')) $('newRulePrice').value = item.price;
}

function renderPurchaseRules() {
  const search = $('ruleSearch').value.trim().toLocaleLowerCase('zh-CN');
  const grade = $('ruleGradeFilter').value;
  const mode = $('ruleModeFilter').value;
  const sort = $('ruleSort').value;
  const rows = purchaseRules.map((rule, index) => ({ rule, index, meta: herbMeta(rule.herbName) }))
    .filter(row => !search || row.rule.herbName.toLocaleLowerCase('zh-CN').includes(search))
    .filter(row => !grade || String(row.meta.grade) === grade)
    .filter(row => !mode || (row.rule.repeatPurchase ? 'repeat' : 'normal') === mode)
    .sort((a, b) => {
      if (sort === 'price-asc') return a.rule.maxPriceWan - b.rule.maxPriceWan || a.rule.herbName.localeCompare(b.rule.herbName, 'zh-CN');
      if (sort === 'grade-asc') return a.meta.grade - b.meta.grade || b.rule.maxPriceWan - a.rule.maxPriceWan;
      if (sort === 'name') return a.rule.herbName.localeCompare(b.rule.herbName, 'zh-CN');
      return b.rule.maxPriceWan - a.rule.maxPriceWan || a.rule.herbName.localeCompare(b.rule.herbName, 'zh-CN');
    });

  $('ruleSummary').textContent = `共 ${purchaseRules.length} 条 · 当前显示 ${rows.length} 条`;
  $('rulesEmpty').classList.toggle('hidden', rows.length > 0);
  $('purchaseRulesTable').innerHTML = rows.map(({ rule, index, meta }) => `
    <tr data-rule-index="${index}">
      <td><div class="rule-name">${escapeHtml(rule.herbName)}</div><span class="rule-grade">${gradeLabel(meta.grade)}药材</span></td>
      <td class="reference-price">${meta.price > 0 ? `${meta.price} 万` : '未设置'}</td>
      <td><input class="table-input" data-rule-field="maxPriceWan" type="number" min="0" value="${rule.maxPriceWan}"></td>
      <td><input class="table-input" data-rule-field="inventoryLimit" type="number" min="0" value="${rule.inventoryLimit}"></td>
      <td><select class="table-select" data-rule-field="repeatPurchase"><option value="normal" ${rule.repeatPurchase ? '' : 'selected'}>普通采购</option><option value="repeat" ${rule.repeatPurchase ? 'selected' : ''}>重复采购</option></select></td>
      <td><button class="row-delete" data-remove-rule type="button">删除</button></td>
    </tr>`).join('');
}

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
$('ruleSearch').addEventListener('input', renderPurchaseRules);
$('ruleGradeFilter').addEventListener('change', renderPurchaseRules);
$('ruleModeFilter').addEventListener('change', renderPurchaseRules);
$('ruleSort').addEventListener('change', renderPurchaseRules);
$('purchaseRulesTable').addEventListener('change', event => {
  const target = event.target;
  const row = target.closest('tr');
  if (!row) return;
  const index = Number(row.dataset.ruleIndex);
  const field = target.dataset.ruleField;
  if (!purchaseRules[index] || !field) return;
  if (field === 'repeatPurchase') purchaseRules[index][field] = target.value === 'repeat';
  else purchaseRules[index][field] = Math.max(0, Number(target.value) || 0);
  renderPurchaseRules();
});
$('purchaseRulesTable').addEventListener('click', event => {
  if (!event.target.matches('[data-remove-rule]')) return;
  const row = event.target.closest('tr');
  const index = Number(row.dataset.ruleIndex);
  purchaseRules.splice(index, 1);
  renderNewRuleHerbs(); renderPurchaseRules();
});
$('importRuleFileBtn').addEventListener('click', () => $('rulePriceFile').click());
$('rulePriceFile').addEventListener('change', event => {
  const input = event.target;
  const file = input.files?.[0];
  if (!file) return;
  busy($('importRuleFileBtn'), async () => {
    const text = await file.text();
    const result = await api('/api/settings/purchase-rules/import', {
      method: 'POST',
      body: JSON.stringify({ text })
    });
    purchaseRules = (result.rules || []).map((rule, index) => ({ ...rule, order: index }));
    currentSettings.purchaseRules = purchaseRules;
    renderNewRuleHerbs(); renderPurchaseRules();
    const invalid = result.invalidLines?.length ? `，忽略 ${result.invalidLines.length} 行` : '';
    toast(`已导入 ${result.importedCount} 条价格，更新 ${result.updatedCount} 条，新增 ${result.addedCount} 条${invalid}`);
    await refreshAudit();
  }).finally(() => { input.value = ''; });
});
$('addRuleBtn').addEventListener('click', () => { $('ruleEditor').classList.remove('hidden'); renderNewRuleHerbs(); });
$('cancelAddRule').addEventListener('click', () => $('ruleEditor').classList.add('hidden'));
$('newRuleHerb').addEventListener('change', updateNewRulePrice);
$('confirmAddRule').addEventListener('click', () => {
  const herbName = $('newRuleHerb').value;
  if (!herbName) { toast('请选择要添加的药材', true); return; }
  if (purchaseRules.some(rule => rule.herbName === herbName)) { toast('这味药材已经在规则列表中', true); return; }
  purchaseRules.push({ herbName, maxPriceWan:Math.max(0, Number($('newRulePrice').value) || 0), inventoryLimit:Math.max(0, Number($('newRuleLimit').value) || 0), order:purchaseRules.length, repeatPurchase:$('newRuleMode').value === 'repeat' });
  $('ruleEditor').classList.add('hidden'); renderNewRuleHerbs(); renderPurchaseRules();
});
$('saveRulesBtn').addEventListener('click', () => busy($('saveRulesBtn'), async () => {
  const rules = purchaseRules.map((rule, index) => ({ ...rule, order:index }));
  const saved = await api('/api/settings/purchase-rules', { method:'PUT', body:JSON.stringify(rules) });
  purchaseRules = saved; currentSettings.purchaseRules = saved; renderNewRuleHerbs(); renderPurchaseRules(); toast(`已保存 ${saved.length} 条采购规则`); await refreshAudit();
}));

Promise.all([refreshStatus(), refreshSettings(), refreshAudit()]).catch(error => toast(error.message, true));
setInterval(refreshStatus, 1500); setInterval(refreshAudit, 8000);
