#!/usr/bin/env node
/**
 * ao-dispatch — AO 任务智能分配（B + D 混合）
 *
 * B 层（关键词粗筛，零依赖、毫秒级）
 *   从 ao-dispatch.rules.json 读规则，命中即定 agent。
 *
 * D 层（语义兜底，可选）
 *   关键词没命中 / 置信度低时，调本地 Semantic Router 服务做语义分类。
 *   服务没启动就降级到 default 规则，不阻塞派活。
 *
 * 用法：
 *   node tools/ao-dispatch.js plan "任务描述"          看决策
 *   node tools/ao-dispatch.js plan --json "..."        决策输出 JSON
 *   node tools/ao-dispatch.js run "任务描述"           决策 + 派活
 *   node tools/ao-dispatch.js run --dry-run "..."      只看会跑什么命令
 *   node tools/ao-dispatch.js rules                    打印规则表
 *   node tools/ao-dispatch.js rules --json             规则原文
 */

const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const os = require('os');

// ── 配置 ────────────────────────────────────────────────────
const CFG = {
  repo: 'E:\\CloverStart',
  project: 'cloverstart',
  aoBin: path.join(process.env.LOCALAPPDATA || '', 'Programs',
    'agent-orchestrator', 'resources', 'daemon', 'ao.exe'),
  rulesPath: path.join(__dirname, 'ao-dispatch.rules.json'),
};

// ── 规则加载 ────────────────────────────────────────────────
let RULES = null;

function loadRules() {
  if (RULES) return RULES;
  try {
    const raw = fs.readFileSync(CFG.rulesPath, 'utf8');
    RULES = JSON.parse(raw);
    // 按 priority 排序（小优先）
    RULES.rules.sort((a, b) => (a.priority || 999) - (b.priority || 999));
    return RULES;
  } catch (e) {
    console.error(`❌ 规则文件读取失败: ${CFG.rulesPath}`);
    console.error(`   ${e.message}`);
    console.error('   用默认规则继续（全部 → deepseek-harness）');
    RULES = {
      agents: {
        'deepseek-harness': {
          enabled: true, label: 'DeepSeek Harness', respectsWorktree: true,
          costTier: 1, models: { normal: '["jethub","buddy/deepseek-v4-flash"]' },
          notes: '默认',
        },
      },
      limits: { maxActiveSessions: 3, maxPerAgent: 2 },
      rules: [],
      default: { id: 'default', label: '默认', target: 'deepseek-harness', needsWrite: true, model: 'normal', reason: '规则文件缺失' },
      semantic: { enabled: false },
    };
    return RULES;
  }
}

// ── 工具 ────────────────────────────────────────────────────
function sh(cmd, args, opts = {}) {
  try {
    return {
      ok: true,
      out: execFileSync(cmd, args, {
        encoding: 'utf8',
        cwd: opts.cwd || CFG.repo,
        stdio: ['ignore', 'pipe', 'pipe'],
        timeout: opts.timeout || 60000,
      }).trim(),
    };
  } catch (e) {
    return { ok: false, err: ((e.stderr || '') + (e.stdout || '') || e.message || '').toString().trim() };
  }
}

function ao(args) { return sh(CFG.aoBin, args); }

// ── AO 状态查询 ─────────────────────────────────────────────
function getReadiness() {
  const r = ao(['agent', 'ls']);
  if (!r.ok) return {};
  const map = {};
  for (const line of r.out.split('\n')) {
    const m = line.match(/^(\S+)\s{2,}(\S.*?)\s{2,}(installed|needs install)\s{2,}(.+)$/);
    if (m) map[m[1]] = { id: m[1], label: m[2].trim(), installed: m[3] === 'installed', auth: m[4].trim() };
  }
  return map;
}

function getActiveSessions() {
  const r = ao(['session', 'ls', '--all', '--json']);
  if (!r.ok) return [];
  try {
    const d = JSON.parse(r.out);
    const list = Array.isArray(d) ? d : (Array.isArray(d.data) ? d.data : []);
    return list.filter(s => !(s.isTerminated === true || s.status === 'terminated' || s.activity === 'exited'));
  } catch { return []; }
}

// ── B 层：关键词匹配 ────────────────────────────────────────
function matchByKeyword(prompt) {
  const R = loadRules();
  const lower = prompt.toLowerCase();

  for (const rule of R.rules) {
    // 关键词
    for (const kw of (rule.keywords || [])) {
      if (lower.includes(kw.toLowerCase())) {
        return { rule, matchedBy: `关键词「${kw}」`, layer: 'B-keyword' };
      }
    }
    // 扩展名
    for (const ext of (rule.extensions || [])) {
      if (lower.includes(ext.toLowerCase())) {
        return { rule, matchedBy: `扩展名「${ext}」`, layer: 'B-ext' };
      }
    }
    // 路径模式
    for (const p of (rule.pathPatterns || [])) {
      if (lower.includes(p.toLowerCase())) {
        return { rule, matchedBy: `路径「${p}」`, layer: 'B-path' };
      }
    }
  }

  return null;
}

// ── D 层：语义兜底 ──────────────────────────────────────────
// ── D 层：语义兜底（支持按需自启）───────────────────────────

/**
 * 检查语义服务是否在线。
 * @returns {Promise<boolean>}
 */
async function isSemanticAlive(endpoint, timeoutMs = 800) {
  // endpoint 形如 http://127.0.0.1:8765/route → 探 /health
  const healthUrl = endpoint.replace(/\/route\s*$/, '/health');
  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    const resp = await fetch(healthUrl, { signal: controller.signal });
    clearTimeout(timer);
    return resp.ok;
  } catch {
    return false;
  }
}

/**
 * 按需启动语义服务（后台，不阻塞）。
 * 用 uv run 启动，日志落到 %TEMP%。
 * @returns {boolean} 是否成功拉起进程
 */
function spawnSemanticServer() {
  const { spawn } = require('child_process');
  const serverDir = path.join(__dirname, 'semantic-router');
  const serverPy = path.join(serverDir, 'server.py');

  if (!fs.existsSync(serverPy)) {
    return { ok: false, reason: `找不到 ${serverPy}` };
  }

  const logPath = path.join(os.tmpdir(), 'ao-semantic-router.log');
  const logFd = (() => {
    try { return fs.openSync(logPath, 'a'); } catch { return 'ignore'; }
  })();

  try {
    const child = spawn('uv', ['run', 'server.py'], {
      cwd: serverDir,
      detached: true,
      stdio: ['ignore', logFd, logFd],
      shell: true,          // Windows 下 uv 是 .cmd，需要 shell
      windowsHide: true,
    });
    child.unref();
    return { ok: true, pid: child.pid, log: logPath };
  } catch (e) {
    return { ok: false, reason: e.message };
  }
}

/**
 * 等语义服务就绪（轮询 /health）。
 * @returns {Promise<boolean>}
 */
async function waitSemanticReady(endpoint, maxWaitMs = 60000) {
  const deadline = Date.now() + maxWaitMs;
  let delay = 500;
  while (Date.now() < deadline) {
    if (await isSemanticAlive(endpoint, 800)) return true;
    await new Promise(r => setTimeout(r, delay));
    delay = Math.min(delay * 1.5, 3000);
  }
  return false;
}

async function matchBySemantic(prompt) {
  const R = loadRules();
  const sem = R.semantic || {};
  if (!sem.enabled) return { skipped: '未启用' };

  const endpoint = sem.endpoint;

  // ── 按需自启 ──
  let autostartNote = '';
  if (!(await isSemanticAlive(endpoint))) {
    const started = spawnSemanticServer();
    if (!started.ok) {
      return { error: `服务未运行且启动失败：${started.reason}` };
    }
    autostartNote = `已拉起服务(PID ${started.pid})，等待就绪…`;
    const ready = await waitSemanticReady(endpoint, sem.autostartTimeoutMs || 60000);
    if (!ready) {
      return { error: `服务启动超时（日志：${started.log}）`, autostartNote };
    }
    autostartNote += ' 就绪';
  }

  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), sem.timeoutMs || 2000);

    const resp = await fetch(endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text: prompt, routes: R.rules.map(r => r.id) }),
      signal: controller.signal,
    });
    clearTimeout(timer);

    if (!resp.ok) return { error: `HTTP ${resp.status}`, autostartNote };
    const data = await resp.json();

    if (!data.route || typeof data.confidence !== 'number') {
      return { error: '响应格式不对（需要 {route, confidence}）', autostartNote };
    }
    if (data.confidence < (sem.threshold || 0.6)) {
      return { lowConfidence: true, route: data.route, confidence: data.confidence, autostartNote };
    }

    const rule = R.rules.find(r => r.id === data.route);
    if (!rule) return { error: `未知 route: ${data.route}`, autostartNote };

    return {
      rule,
      matchedBy: `语义「${data.route}」conf=${data.confidence.toFixed(2)}`,
      layer: 'D-semantic',
      confidence: data.confidence,
      autostartNote,
    };
  } catch (e) {
    return { error: e.name === 'AbortError' ? '超时' : e.message, autostartNote };
  }
}

// ── 决策 ────────────────────────────────────────────────────
async function decide(prompt) {
  const R = loadRules();
  const trace = [];
  const readiness = getReadiness();
  const active = getActiveSessions();
  const activeByHarness = {};
  for (const s of active) activeByHarness[s.harness] = (activeByHarness[s.harness] || 0) + 1;

  trace.push(`输入：${prompt.slice(0, 80)}${prompt.length > 80 ? '…' : ''}`);
  trace.push('');

  // B 层
  trace.push('【B 层 · 关键词】');
  const kw = matchByKeyword(prompt);
  let rule, matchedBy, layer;

  if (kw) {
    rule = kw.rule;
    matchedBy = kw.matchedBy;
    layer = kw.layer;
    trace.push(`  ✓ 命中 ${rule.label}（${matchedBy}）`);
  } else {
    trace.push('  ✗ 未命中任何关键词规则');

    // D 层兜底
    trace.push('');
    trace.push('【D 层 · 语义兜底】');
    const sem = await matchBySemantic(prompt);

    if (sem.autostartNote) {
      trace.push(`  ⚙️  ${sem.autostartNote}`);
    }

    if (sem.rule) {
      rule = sem.rule;
      matchedBy = sem.matchedBy;
      layer = sem.layer;
      trace.push(`  ✓ ${sem.matchedBy}`);
    } else if (sem.skipped) {
      trace.push(`  ⏭  跳过（${sem.skipped}）`);
      rule = R.default;
      matchedBy = '(default)';
      layer = 'fallback';
    } else if (sem.lowConfidence) {
      trace.push(`  ⚠️  置信度不足（${sem.route} conf=${sem.confidence.toFixed(2)} < ${R.semantic.threshold}）`);
      rule = R.default;
      matchedBy = '(default，置信度不足)';
      layer = 'fallback';
    } else {
      trace.push(`  ⚠️  不可用（${sem.error}）`);
      rule = R.default;
      matchedBy = '(default，语义服务不可用)';
      layer = 'fallback';
    }
  }

  trace.push('');
  trace.push(`规则：${rule.label}（id=${rule.id}）`);
  trace.push(`目标：${rule.target}`);
  trace.push(`是否写文件：${rule.needsWrite ? '是' : '否'}`);

  // ── 约束检查 ──
  const agent = R.agents[rule.target];
  if (!agent) {
    return { ok: false, trace, rule, error: `规则 ${rule.id} 指向的 agent ${rule.target} 不在配置里` };
  }
  if (agent.enabled === false) {
    return { ok: false, trace, rule, error: `${agent.label} 已禁用（agents.${rule.target}.enabled=false）` };
  }

  const ready = readiness[rule.target];
  if (!ready) return { ok: false, trace, rule, error: `${agent.label}：AO 未识别` };
  if (!ready.installed) return { ok: false, trace, rule, error: `${agent.label}：未安装` };
  if (/needs auth/i.test(ready.auth)) return { ok: false, trace, rule, error: `${agent.label}：需要授权` };

  // Codex 不尊重 worktree → 只允许只读
  if (!agent.respectsWorktree && rule.needsWrite) {
    return {
      ok: false, trace, rule,
      error: `${agent.label} 不尊重 worktree，但规则 ${rule.id} 要求写文件。\n` +
        `  处理：\n` +
        `    1. 把任务改成只读措辞（"只分析不改"）\n` +
        `    2. 或手动 --harness deepseek-harness\n` +
        `    3. 或改规则文件把 ${rule.id} 的 target 换成 deepseek-harness`,
    };
  }

  const load = activeByHarness[rule.target] || 0;
  const maxPerAgent = R.limits?.maxPerAgent ?? 2;
  if (load >= maxPerAgent) {
    return { ok: false, trace, rule, error: `${agent.label} 已有 ${load} 个活跃会话（上限 ${maxPerAgent}）` };
  }

  const maxActive = R.limits?.maxActiveSessions ?? 3;
  if (active.length >= maxActive) {
    return { ok: false, trace, rule, error: `活跃会话 ${active.length} 个已达上限 ${maxActive}，先清理再派活` };
  }

  const model = agent.models?.[rule.model] || agent.models?.normal || null;

  trace.push('');
  trace.push(`就绪：${agent.label}（load=${load}/${maxPerAgent}）`);
  trace.push(`模型：${model || '(默认)'}`);

  return {
    ok: true,
    trace,
    rule,
    layer,
    matchedBy,
    agent,
    harness: rule.target,
    model,
    needsWrite: rule.needsWrite,
    activeCount: active.length,
    maxActive,
  };
}

// ── 输出 ────────────────────────────────────────────────────
function printPlan(plan) {
  console.log('═══════════════════════════════════════════════════');
  console.log('  AO 任务分配决策');
  console.log('═══════════════════════════════════════════════════');
  console.log('');
  plan.trace.forEach(l => console.log(l ? '  ' + l : ''));

  if (!plan.ok) {
    console.log('');
    console.log(`  ❌ ${plan.error}`);
    return;
  }

  console.log('');
  console.log('  ─────────────────────────────────────────────');
  console.log(`  harness   : ${plan.harness}`);
  console.log(`  model     : ${plan.model || '(默认)'}`);
  console.log(`  决策层    : ${plan.layer}`);
  console.log(`  活跃会话  : ${plan.activeCount}/${plan.maxActive}`);
  console.log('');
}

// ── 派活 ────────────────────────────────────────────────────
async function run(prompt, opts) {
  const plan = await decide(prompt);

  if (opts.json) {
    console.log(JSON.stringify(plan, null, 2));
    return plan.ok ? 0 : 1;
  }

  printPlan(plan);
  if (!plan.ok) return 1;

  const name = opts.name || autoName(plan.rule);
  const args = ['spawn', '--project', CFG.project, '--harness', plan.harness,
    '--name', name, '--prompt', prompt];
  if (plan.model) args.push('--model', plan.model);

  if (opts.dryRun) {
    console.log('  （--dry-run，未派活）');
    console.log('  实际会跑：');
    console.log(`    ao ${args.map(a => a.includes(' ') ? JSON.stringify(a) : a).join(' ')}`);
    return 0;
  }

  console.log('  派活中…');
  const r = ao(args);
  if (!r.ok) { console.log(`  ❌ ${r.err}`); return 1; }
  console.log(`  ✅ ${r.out}`);
  return 0;
}

function autoName(rule) {
  const stamp = new Date().toISOString().slice(11, 19).replace(/:/g, '');
  return `${rule.id}-${stamp}`;
}

// ── 规则表 ──────────────────────────────────────────────────
function printRules(asJson) {
  const R = loadRules();
  if (asJson) { console.log(JSON.stringify(R, null, 2)); return; }

  console.log('═══════════════════════════════════════════════════');
  console.log('  AO 任务分配规则');
  console.log('═══════════════════════════════════════════════════');
  console.log(`  规则文件：${CFG.rulesPath}`);
  console.log('');

  console.log('【规则（按 priority 排序，小优先）】');
  for (const r of R.rules) {
    console.log(`  ${String(r.priority).padStart(3)}  ${r.id.padEnd(12)} → ${r.target.padEnd(18)} needsWrite=${r.needsWrite ? 'Y' : 'N'}  model=${r.model || '-'}`);
    console.log(`       ${r.reason}`);
    const sig = [...(r.keywords || []), ...(r.extensions || []), ...(r.pathPatterns || [])];
    console.log(`       ${sig.slice(0, 10).join(' / ')}${sig.length > 10 ? ` …(+${sig.length - 10})` : ''}`);
  }
  console.log(`  ---  ${R.default.id.padEnd(12)} → ${R.default.target.padEnd(18)} needsWrite=${R.default.needsWrite ? 'Y' : 'N'}`);
  console.log(`       ${R.default.reason}`);
  console.log('');

  console.log('【Agent】');
  for (const [id, a] of Object.entries(R.agents)) {
    const st = a.enabled === false ? '⛔ 禁用' : '✅ 启用';
    const wt = a.respectsWorktree ? '✅' : '❌';
    console.log(`  ${st}  ${a.label.padEnd(20)} worktree=${wt}  cost=${a.costTier}`);
    if (a.enabled !== false) console.log(`        ${a.notes}`);
  }
  console.log('');

  console.log('【限制】');
  console.log(`  活跃会话上限: ${R.limits.maxActiveSessions}`);
  console.log(`  单 agent 上限: ${R.limits.maxPerAgent}`);
  console.log('');

  const sem = R.semantic || {};
  console.log('【D 层语义兜底】');
  console.log(`  状态: ${sem.enabled ? '✅ 启用' : '⛔ 未启用'}`);
  if (sem.enabled) {
    console.log(`  端点: ${sem.endpoint}`);
    console.log(`  阈值: ${sem.threshold}`);
  } else {
    console.log('  启用方式: 改 ao-dispatch.rules.json 的 semantic.enabled = true');
    console.log('  并启动: python tools/semantic-router/server.py');
  }
  console.log('');
}

// ── 入口 ────────────────────────────────────────────────────
async function main() {
  const argv = process.argv.slice(2);
  const cmd = argv[0];
  const opts = {
    json: argv.includes('--json'),
    dryRun: argv.includes('--dry-run'),
    name: (() => { const i = argv.indexOf('--name'); return i >= 0 ? argv[i + 1] : null; })(),
  };

  const flagValues = new Set();
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--name') flagValues.add(i + 1);
  }
  const prompt = argv.slice(1)
    .filter((a, i) => !a.startsWith('--') && !flagValues.has(i + 1))
    .join(' ')
    .trim();

  switch (cmd) {
    case 'rules':
      printRules(opts.json);
      break;

    case 'plan':
      if (!prompt) { console.error('用法: plan "任务描述"'); process.exit(1); }
      {
        const plan = await decide(prompt);
        if (opts.json) console.log(JSON.stringify(plan, null, 2));
        else printPlan(plan);
        process.exit(plan.ok ? 0 : 1);
      }
      break;

    case 'run':
      if (!prompt) { console.error('用法: run "任务描述"'); process.exit(1); }
      process.exit(await run(prompt, opts));
      break;

    case undefined:
    case 'help':
    case '--help':
    case '-h':
      console.log(`
ao-dispatch — AO 任务智能分配（B 关键词 + D 语义兜底）

用法:
  node tools/ao-dispatch.js plan "任务描述"        看决策
  node tools/ao-dispatch.js plan --json "..."      决策输出 JSON
  node tools/ao-dispatch.js run "任务描述"         决策 + 派活
  node tools/ao-dispatch.js run --dry-run "..."    只看会跑什么命令
  node tools/ao-dispatch.js rules                  打印规则表
  node tools/ao-dispatch.js rules --json           规则原文

改规则:
  直接编辑 tools/ao-dispatch.rules.json，不用改代码。

启用 D 层语义兜底:
  1. 改 rules.json: "semantic": { "enabled": true }
  2. 启动服务: python tools/semantic-router/server.py
`);
      break;

    default:
      if (cmd && !cmd.startsWith('-')) {
        process.exit(await run([cmd, ...argv.slice(1)].join(' '), opts));
      }
      console.error(`未知命令: ${cmd}`);
      process.exit(1);
  }
}

main();
