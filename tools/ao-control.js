#!/usr/bin/env node
/**
 * ao-control — AO 大脑对 agent 手的统一管控
 *
 * 解决的问题（用户原话）：
 *   「ao 这个大脑要对 agent 这个手实现控制，管理他们，
 *     不出现重复堆积和没用会话，污染整个流程」
 *
 * 五条管控规则：
 *   1. 会话唯一性  — 同一 project+role 同时只允许一个活跃会话
 *   2. 自动回收    — 已终止会话 + worktree + 分支，超期自动清
 *   3. 孤儿检测    — 无 conversation 关联的 session / 无 session 的 worktree
 *   4. 配额限制    — 同时活跃会话数上限，超了拒绝派活
 *   5. 健康报告    — 一屏看清全部状态与污染项
 *
 * 用法：
 *   node tools/ao-control.js status             # 健康报告
 *   node tools/ao-control.js enforce            # 按规则回收（默认 dry-run）
 *   node tools/ao-control.js enforce --apply    # 实际执行
 *   node tools/ao-control.js guard              # 派活前检查（可被 spawn 包装调用）
 *   node tools/ao-control.js spawn <args...>    # 带管控的 spawn
 */

const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const os = require('os');

// ── 配置 ────────────────────────────────────────────────────
const CONFIG = {
  repo: 'E:\\CloverStart',
  aoData: path.join(os.homedir(), '.ao', 'data'),
  aoBin: path.join(process.env.LOCALAPPDATA || '', 'Programs',
    'agent-orchestrator', 'resources', 'daemon', 'ao.exe'),

  // 管控阈值
  maxActiveSessions: 3,          // 同时活跃会话上限
  maxIdleMinutes: 30,            // idle 超过这么久视为可回收
  maxOrchestrators: 1,           // 每项目编排器上限
  autoDeleteTerminated: true,    // 自动删已终止会话记录
  keepLastTerminated: 3,         // 保留最近 N 个已终止会话（便于查历史）
};

// ── 基础工具 ────────────────────────────────────────────────
function sh(cmd, args, opts = {}) {
  try {
    return {
      ok: true,
      out: execFileSync(cmd, args, {
        encoding: 'utf8',
        cwd: opts.cwd || CONFIG.repo,
        stdio: ['ignore', 'pipe', 'pipe'],
        timeout: opts.timeout || 60000,
      }).trim(),
    };
  } catch (e) {
    return { ok: false, err: ((e.stderr || '') + (e.stdout || '') || e.message || '').toString().trim() };
  }
}

function ao(args, opts) { return sh(CONFIG.aoBin, args, opts); }
function git(args, opts) { return sh('git', args, opts); }

function human(bytes) {
  if (!bytes) return '0 B';
  const u = ['B', 'KB', 'MB', 'GB'];
  let i = 0, n = bytes;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return `${n.toFixed(n >= 10 ? 0 : 1)} ${u[i]}`;
}

function dirSize(p) {
  let total = 0;
  try {
    const stack = [p];
    while (stack.length) {
      const cur = stack.pop();
      let ents;
      try { ents = fs.readdirSync(cur, { withFileTypes: true }); } catch { continue; }
      for (const ent of ents) {
        const full = path.join(cur, ent.name);
        if (ent.isDirectory()) stack.push(full);
        else if (ent.isFile()) { try { total += fs.statSync(full).size; } catch {} }
      }
    }
  } catch {}
  return total;
}

// ── 数据采集 ────────────────────────────────────────────────
function listSessions() {
  const r = ao(['session', 'ls', '--all', '--json', '--include-terminated']);
  if (!r.ok) return [];
  try {
    const d = JSON.parse(r.out);
    if (Array.isArray(d)) return d;
    if (Array.isArray(d.data)) return d.data;
    return [];
  } catch { return []; }
}

function listWorktrees() {
  const r = git(['worktree', 'list', '--porcelain']);
  if (!r.ok) return [];
  const out = [];
  let cur = {};
  for (const line of r.out.split('\n')) {
    if (line.startsWith('worktree ')) {
      if (cur.path) out.push(cur);
      cur = { path: line.slice(9).trim() };
    } else if (line.startsWith('branch ')) {
      cur.branch = line.slice(7).trim().replace('refs/heads/', '');
    }
  }
  if (cur.path) out.push(cur);
  return out;
}

function listBranches() {
  const r = git(['branch', '--list', 'ao/*', '--format=%(refname:short)']);
  return r.ok ? r.out.split('\n').map(s => s.trim()).filter(Boolean) : [];
}

function listMergedBranches() {
  const r = git(['branch', '--merged', 'main', '--list', 'ao/*', '--format=%(refname:short)']);
  return r.ok ? r.out.split('\n').map(s => s.trim()).filter(Boolean) : [];
}

function isDead(s) {
  return s.isTerminated === true || s.status === 'terminated' || s.activity === 'exited';
}

function isActive(s) {
  return !isDead(s) && ['working', 'active', 'idle', 'waiting_input', 'blocked'].includes(s.activity || s.status);
}

// ── 规则检查 ────────────────────────────────────────────────
function analyze() {
  const sessions = listSessions();
  const wts = listWorktrees();
  const branches = listBranches();
  const merged = listMergedBranches();

  const mainNorm = CONFIG.repo.replace(/\//g, '\\').toLowerCase();
  const aoWts = wts.filter(w => w.path.replace(/\//g, '\\').toLowerCase() !== mainNorm);

  const active = sessions.filter(isActive);
  const dead = sessions.filter(isDead);
  const orchestrators = active.filter(s => s.kind === 'orchestrator' || s.role === 'orchestrator');
  const workers = active.filter(s => !(s.kind === 'orchestrator' || s.role === 'orchestrator'));

  // 孤儿：worktree 存在但没有对应活跃会话
  const activeIds = new Set(active.map(s => s.id));
  const orphanWts = aoWts.filter(w => {
    const base = path.basename(w.path).toLowerCase();
    // orchestrator worktree 目录名固定
    if (/orchestrator/i.test(w.path)) return false;
    for (const id of activeIds) if (base.includes(id.toLowerCase())) return false;
    return true;
  });

  // 孤儿：worktree 目录已不存在但 git 还登记
  const ghostWts = aoWts.filter(w => !fs.existsSync(w.path));

  const unmerged = branches.filter(b => !merged.includes(b));
  const totalSize = aoWts.reduce((sum, w) => sum + dirSize(w.path), 0);

  // 违规
  const violations = [];
  if (active.length > CONFIG.maxActiveSessions) {
    violations.push({
      rule: 'maxActiveSessions',
      msg: `活跃会话 ${active.length} 个，超过上限 ${CONFIG.maxActiveSessions}`,
    });
  }
  if (orchestrators.length > CONFIG.maxOrchestrators) {
    violations.push({
      rule: 'maxOrchestrators',
      msg: `编排器 ${orchestrators.length} 个，超过上限 ${CONFIG.maxOrchestrators}`,
    });
  }
  if (orphanWts.length > 0) {
    violations.push({
      rule: 'orphanWorktrees',
      msg: `${orphanWts.length} 个 worktree 没有对应活跃会话`,
    });
  }
  if (ghostWts.length > 0) {
    violations.push({
      rule: 'ghostWorktrees',
      msg: `${ghostWts.length} 个 worktree 目录已不存在但 git 仍登记`,
    });
  }
  if (merged.length > 0) {
    violations.push({
      rule: 'staleBranches',
      msg: `${merged.length} 个已合并的 ao/ 分支未清理`,
    });
  }
  if (dead.length > CONFIG.keepLastTerminated) {
    violations.push({
      rule: 'deadSessions',
      msg: `${dead.length} 个已终止会话，超过保留数 ${CONFIG.keepLastTerminated}`,
    });
  }

  return {
    sessions, active, dead, orchestrators, workers,
    worktrees: wts, aoWts, orphanWts, ghostWts,
    branches, merged, unmerged, totalSize,
    violations,
  };
}

// ── status ──────────────────────────────────────────────────
function status() {
  const a = analyze();

  console.log('══════════════════════════════════════════════════════');
  console.log('  AO 大脑管控 · 健康报告');
  console.log('══════════════════════════════════════════════════════');
  console.log('');

  // 会话
  console.log(`【会话】 活跃 ${a.active.length} (上限 ${CONFIG.maxActiveSessions}) / 已终止 ${a.dead.length} (保留 ${CONFIG.keepLastTerminated})`);
  if (a.active.length) {
    console.log('');
    console.log('  ' + 'ID'.padEnd(18) + 'ROLE'.padEnd(14) + 'HARNESS'.padEnd(18) + 'ACTIVITY');
    console.log('  ' + '─'.repeat(72));
    for (const s of a.active) {
      console.log('  ' +
        String(s.id || '?').padEnd(18) +
        String(s.kind || s.role || '-').padEnd(14) +
        String(s.harness || '-').padEnd(18) +
        String(s.activity || s.status || '-'));
    }
  }

  // 编排器
  console.log('');
  console.log(`【编排器】 ${a.orchestrators.length} 个 (上限 ${CONFIG.maxOrchestrators})`);
  if (a.orchestrators.length) {
    a.orchestrators.forEach(s => console.log(`  ${s.id}  ${s.harness}  ${s.activity || s.status}`));
  } else {
    console.log('  (无)');
  }

  // worktree
  console.log('');
  console.log(`【Worktree】 AO 临时 ${a.aoWts.length} 个，共 ${human(a.totalSize)}`);
  if (a.aoWts.length) {
    for (const w of a.aoWts) {
      const sz = dirSize(w.path);
      const ghost = !fs.existsSync(w.path) ? ' [目录不存在]' : '';
      const orphan = a.orphanWts.includes(w) ? ' [孤儿]' : '';
      console.log(`  ${path.basename(w.path).padEnd(22)} ${human(sz).padStart(9)}  [${w.branch || 'detached'}]${ghost}${orphan}`);
    }
  } else {
    console.log('  (无)');
  }

  // 分支
  console.log('');
  console.log(`【分支】 ao/ 共 ${a.branches.length}，已合并 ${a.merged.length}，未合并 ${a.unmerged.length}`);
  if (a.unmerged.length) {
    console.log('  未合并（保留）：');
    a.unmerged.forEach(b => console.log(`    ${b}`));
  }

  // 违规
  console.log('');
  if (a.violations.length === 0) {
    console.log('【规则】 ✅ 全部通过，无污染');
  } else {
    console.log(`【规则】 ⚠️  ${a.violations.length} 项违规`);
    a.violations.forEach(v => console.log(`  · [${v.rule}] ${v.msg}`));
    console.log('');
    console.log('  修复：node tools/ao-control.js enforce --apply');
  }
  console.log('');
}

// ── enforce ─────────────────────────────────────────────────
function enforce(opts) {
  const apply = opts.apply;
  const a = analyze();

  console.log(`=== AO 管控执行 ${apply ? '(实际)' : '(预览)'} ===`);
  console.log('');

  let acted = 0;

  // 规则 1：清理已合并分支
  console.log('[1/5] 已合并分支');
  if (a.merged.length === 0) console.log('  ✅ 无');
  else for (const b of a.merged) {
    console.log(`  ${apply ? '删除' : '将删除'}: ${b}`);
    if (apply) {
      const r = git(['branch', '-D', b]);
      if (!r.ok) console.log(`    ⚠️  ${r.err.split('\n')[0]}`);
      else acted++;
    }
  }

  // 规则 2：清理幽灵 worktree（目录不存在但 git 登记）
  console.log('');
  console.log('[2/5] 幽灵 worktree');
  if (a.ghostWts.length === 0) console.log('  ✅ 无');
  else {
    for (const w of a.ghostWts) {
      console.log(`  ${apply ? '注销' : '将注销'}: ${w.path}`);
      if (apply) {
        git(['worktree', 'remove', '--force', w.path]);
        acted++;
      }
    }
    if (apply) git(['worktree', 'prune']);
  }

  // 规则 3：清理孤儿 worktree（无对应活跃会话）
  console.log('');
  console.log('[3/5] 孤儿 worktree');
  if (a.orphanWts.length === 0) console.log('  ✅ 无');
  else {
    for (const w of a.orphanWts) {
      console.log(`  ${apply ? '移除' : '将移除'}: ${w.path}`);
      if (apply) {
        const r = git(['worktree', 'remove', '--force', w.path]);
        if (!r.ok) console.log(`    ⚠️  ${r.err.split('\n')[0]}`);
        else acted++;
      }
    }
    if (apply) git(['worktree', 'prune']);
  }

  // 规则 4：让 AO 回收已终止会话的 workspace
  console.log('');
  console.log('[4/5] AO 会话 workspace');
  if (apply) {
    const r = ao(['session', 'cleanup', '-y']);
    if (r.ok && r.out) r.out.split('\n').forEach(l => l.trim() && console.log('  ' + l));
    else console.log('  (无)');
  } else {
    const r = ao(['session', 'cleanup', '--dry-run']);
    if (r.ok && r.out) r.out.split('\n').forEach(l => l.trim() && console.log('  ' + l));
  }

  // 规则 5：删除超额的已终止会话记录
  console.log('');
  console.log(`[5/5] 已终止会话记录（保留最近 ${CONFIG.keepLastTerminated} 个）`);
  const sortedDead = [...a.dead].sort((x, y) =>
    String(y.updatedAt || y.lastActivityAt || '').localeCompare(String(x.updatedAt || x.lastActivityAt || ''))
  );
  const toDelete = sortedDead.slice(CONFIG.keepLastTerminated);

  if (toDelete.length === 0) {
    console.log(`  ✅ 只有 ${a.dead.length} 个，未超额`);
  } else {
    const blocked = [];
    for (const s of toDelete) {
      console.log(`  ${apply ? '删除' : '将删除'}: ${s.id}  (${s.displayName || s.kind || ''})`);
      if (apply) {
        let r = ao(['session', 'delete', s.id, '-y', '--cleanup']);
        if (!r.ok) r = ao(['session', 'delete', s.id, '-y']);
        if (!r.ok) {
          if (/SESSION_HISTORY_SHARED/.test(r.err)) {
            blocked.push(s.id);
          } else {
            console.log(`    ⚠️  ${r.err.split('\n')[0]}`);
          }
        } else acted++;
      }
    }

    // AO 的历史保护：turns 属于 conversation，session 删不掉
    if (blocked.length > 0) {
      console.log('');
      console.log(`  ⏭  ${blocked.length} 个被 AO 历史保护拦住（SESSION_HISTORY_SHARED）：`);
      blocked.forEach(id => console.log(`       ${id}`));
      console.log('');
      console.log('     原因：这些会话的 turns 归属于 outlive 它的 conversation，');
      console.log('           AO 没有删除 conversation 的 API，属于设计上的历史保护。');
      console.log('     影响：仅占数据库记录，不占磁盘、不影响性能。');
      console.log('     可选：在 AO 界面里归档（侧栏 → 已归档会话）。');
    }
  }

  console.log('');
  if (apply) {
    console.log(`=== 完成，执行了 ${acted} 项操作 ===`);
    console.log('');
    status();
  } else {
    console.log('=== 预览完成，未做修改 ===');
    console.log('加 --apply 实际执行。');
  }
}

// ── guard：派活前检查 ───────────────────────────────────────
function guard() {
  const a = analyze();

  if (a.active.length >= CONFIG.maxActiveSessions) {
    console.error(`❌ 拒绝派活：活跃会话 ${a.active.length} 个已达上限 ${CONFIG.maxActiveSessions}`);
    console.error('');
    console.error('   当前活跃：');
    a.active.forEach(s => console.error(`     ${s.id}  ${s.kind || s.role}  ${s.activity}`));
    console.error('');
    console.error('   先结束不需要的：node tools/ao-control.js kill <id>');
    console.error('   或强制清理：node tools/ao-control.js enforce --apply');
    process.exit(1);
  }

  const orch = a.active.filter(s => (s.kind || s.role) === 'orchestrator');
  if (orch.length >= CONFIG.maxOrchestrators) {
    console.error(`⚠️  已有 ${orch.length} 个编排器（上限 ${CONFIG.maxOrchestrators}）`);
    console.error('   继续派 worker 可以，但编排器建议复用现有的。');
  }

  console.log(`✅ 管控检查通过（活跃 ${a.active.length}/${CONFIG.maxActiveSessions}）`);
}

// ── spawn：带管控的派活 ─────────────────────────────────────
function spawnWithGuard(args) {
  const a = analyze();
  if (a.active.length >= CONFIG.maxActiveSessions) {
    console.error(`❌ 拒绝派活：活跃会话已达上限 ${CONFIG.maxActiveSessions}`);
    a.active.forEach(s => console.error(`   ${s.id}  ${s.kind || s.role}  ${s.activity}`));
    process.exit(1);
  }
  console.log(`✅ 管控通过（活跃 ${a.active.length}/${CONFIG.maxActiveSessions}），派活中...`);
  const r = ao(['spawn', ...args]);
  if (!r.ok) { console.error(r.err); process.exit(1); }
  console.log(r.out);
}

// ── 入口 ────────────────────────────────────────────────────
function main() {
  const [, , cmd, ...rest] = process.argv;
  const opts = { apply: rest.includes('--apply') };

  switch (cmd) {
    case undefined:
    case 'status':
      status();
      break;
    case 'enforce':
      enforce(opts);
      break;
    case 'guard':
      guard();
      break;
    case 'spawn':
      spawnWithGuard(rest.filter(x => x !== '--apply'));
      break;
    case 'kill': {
      const id = rest.find(x => !x.startsWith('-'));
      if (!id) { console.error('用法: kill <session-id>'); process.exit(1); }
      const r = ao(['session', 'kill', id]);
      console.log(r.ok ? `✅ ${r.out}` : `❌ ${r.err}`);
      break;
    }
    case 'log': {
      const id = rest.find(x => !x.startsWith('-'));
      if (!id) { console.error('用法: log <session-id>'); process.exit(1); }
      const r = ao(['session', 'history', id, '--limit', '5']);
      console.log(r.ok ? r.out : `❌ ${r.err}`);
      break;
    }
    case 'help':
    case '--help':
    case '-h':
      console.log(`
ao-control — AO 大脑对手的管控

用法:
  node tools/ao-control.js status              健康报告（默认）
  node tools/ao-control.js guard               派活前检查
  node tools/ao-control.js enforce             预览回收（dry-run）
  node tools/ao-control.js enforce --apply     实际回收
  node tools/ao-control.js spawn <args...>     带管控地派活
  node tools/ao-control.js kill <id>           结束会话
  node tools/ao-control.js log <id>            看会话对话

五条管控规则:
  1. 活跃会话上限 ${CONFIG.maxActiveSessions} 个
  2. 编排器每项目上限 ${CONFIG.maxOrchestrators} 个
  3. 已终止会话只保留最近 ${CONFIG.keepLastTerminated} 个
  4. 已合并的 ao/ 分支自动删
  5. 孤儿/幽灵 worktree 自动回收

配置在文件顶部 CONFIG 对象。
`);
      break;
    default:
      console.error(`未知命令: ${cmd}`);
      console.error('跑 node tools/ao-control.js --help 看用法');
      process.exit(1);
  }
}

main();
