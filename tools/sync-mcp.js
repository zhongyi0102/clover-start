#!/usr/bin/env node
/**
 * sync-mcp — 把共享 MCP server 配置写进各个 agent
 *
 * 背景：AO 自己不存 MCP 配置，只从 agent 侧发现。
 * 所以「一个大脑多只手共享上下文」的正确做法是：
 *   把同一个 MCP server 写进每个 agent 的配置文件。
 *
 * 用法：
 *   node tools/sync-mcp.js            # 同步所有已知 agent
 *   node tools/sync-mcp.js --dry-run  # 只看会改什么
 *   node tools/sync-mcp.js --agent dsh,codex
 *
 * 未来加新 agent：在 AGENTS 数组里加一条即可。
 */

const fs = require('fs');
const path = require('path');
const os = require('os');

// ── 共享 MCP server 定义（唯一真相源） ──────────────────────
const SERVER_NAME = 'ao-context';
const SERVER_CMD = 'node';
const SERVER_ARGS = ['E:\\CloverStart\\tools\\context-mcp\\server.js'];
const SERVER_ENV = { AO_CONTEXT_DIR: 'E:\\CloverStart\\.ao-context' };

// ── agent 配置适配器 ────────────────────────────────────────
const AGENTS = [
  {
    id: 'dsh',
    label: 'DeepSeek Harness (acp profile)',
    configPath: path.join(os.homedir(), '.dsh', 'profiles', 'acp', 'cordis.patch.yml'),
    format: 'yaml-patch',
  },
  {
    id: 'codex',
    label: 'Codex CLI',
    configPath: path.join(process.env.CODEX_HOME || path.join(os.homedir(), '.codex'), 'config.toml'),
    format: 'toml',
  },
  // 未来加新 agent，在这里加一条：
  // { id: 'claude', label: 'Claude Code', configPath: ..., format: 'json' },
];

// ── 各格式的写入实现 ────────────────────────────────────────

function yamlPatchBlock() {
  return `
# ── 共享上下文 MCP（由 tools/sync-mcp.js 生成，勿手改） ──
- id: mcp-client
  name: "@deepseek-ai/dsh-mcp-client"
  config:
    transport: stdio
    serverName: ${SERVER_NAME}
    command: ${SERVER_CMD}
    args:
${SERVER_ARGS.map(a => `      - ${JSON.stringify(a)}`).join('\n')}
    env:
${Object.entries(SERVER_ENV).map(([k, v]) => `      ${k}: ${JSON.stringify(v)}`).join('\n')}
    failOnStartupError: false
`;
}

function writeDshYaml(filePath, dryRun) {
  let content = '';
  if (fs.existsSync(filePath)) {
    content = fs.readFileSync(filePath, 'utf8');
    if (content.includes(`# ── 共享上下文 MCP`)) {
      return { status: 'skip', reason: '已有 ao-context 配置' };
    }
  } else {
    return { status: 'error', reason: `文件不存在: ${filePath}` };
  }

  // 关键：必须用 insert 形式。
  // `- id: mcp-client` 只能覆盖已存在的条目，acp profile 里没有它，
  // DSH 会报 `patch: entry "mcp-client" not found` 并静默丢弃。
  const block = yamlPatchBlock();
  const next = content.trimEnd() + '\n' + block;

  if (!dryRun) {
    fs.writeFileSync(filePath + '.bak-syncmcp', content, 'utf8');
    fs.writeFileSync(filePath, next, 'utf8');
  }
  return { status: 'written', bytes: block.length };
}

function tomlBlock() {
  const envLines = Object.entries(SERVER_ENV)
    .map(([k, v]) => `${k} = ${JSON.stringify(v)}`)
    .join('\n');
  const argsLines = SERVER_ARGS.map(a => JSON.stringify(a)).join(', ');
  return `
# ── 共享上下文 MCP（由 tools/sync-mcp.js 生成，勿手改） ──
[mcp_servers.${SERVER_NAME}]
command = ${JSON.stringify(SERVER_CMD)}
args = [${argsLines}]

[mcp_servers.${SERVER_NAME}.env]
${envLines}
`;
}

function writeCodexToml(filePath, dryRun) {
  let content = '';
  if (fs.existsSync(filePath)) {
    content = fs.readFileSync(filePath, 'utf8');
    if (content.includes(`[mcp_servers.${SERVER_NAME}]`)) {
      return { status: 'skip', reason: '已有 ao-context 配置' };
    }
  } else {
    // 文件不存在则创建
    content = '';
  }

  const block = tomlBlock();
  const next = content.trimEnd() + (content ? '\n' : '') + block;

  if (!dryRun) {
    if (content) fs.writeFileSync(filePath + '.bak-syncmcp', content, 'utf8');
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, next, 'utf8');
  }
  return { status: 'written', bytes: block.length };
}

// ── 主流程 ──────────────────────────────────────────────────

function main() {
  const args = process.argv.slice(2);
  const dryRun = args.includes('--dry-run');
  const agentFilter = (() => {
    const i = args.indexOf('--agent');
    return i >= 0 && args[i + 1] ? args[i + 1].split(',').map(s => s.trim()) : null;
  })();

  console.log('=== sync-mcp: 把 ao-context 写进各 agent ===');
  console.log(`模式: ${dryRun ? '预览' : '实际写入'}`);
  console.log(`MCP:  ${SERVER_NAME} → ${SERVER_CMD} ${SERVER_ARGS.join(' ')}`);
  console.log(`数据: ${SERVER_ENV.AO_CONTEXT_DIR}`);
  console.log('');

  const targets = agentFilter
    ? AGENTS.filter(a => agentFilter.includes(a.id))
    : AGENTS;

  if (targets.length === 0) {
    console.log('没有匹配的 agent。已知:', AGENTS.map(a => a.id).join(', '));
    process.exit(1);
  }

  let changed = 0, skipped = 0, failed = 0;

  for (const agent of targets) {
    console.log(`[${agent.id}] ${agent.label}`);
    console.log(`  配置: ${agent.configPath}`);

    let result;
    try {
      if (agent.format === 'yaml-patch') {
        result = writeDshYaml(agent.configPath, dryRun);
      } else if (agent.format === 'toml') {
        result = writeCodexToml(agent.configPath, dryRun);
      } else {
        result = { status: 'error', reason: `未知格式: ${agent.format}` };
      }
    } catch (err) {
      result = { status: 'error', reason: err.message };
    }

    if (result.status === 'written') {
      console.log(`  ✅ ${dryRun ? '将写入' : '已写入'} ${result.bytes} 字节`);
      changed++;
    } else if (result.status === 'skip') {
      console.log(`  ⏭  跳过：${result.reason}`);
      skipped++;
    } else {
      console.log(`  ❌ 失败：${result.reason}`);
      failed++;
    }
    console.log('');
  }

  console.log(`=== 完成：写入 ${changed} / 跳过 ${skipped} / 失败 ${failed} ===`);
  if (dryRun && changed > 0) {
    console.log('（预览模式，未实际修改。去掉 --dry-run 执行）');
  }
  if (changed > 0 && !dryRun) {
    console.log('');
    console.log('⚠️  需要重启对应的 agent 才生效：');
    console.log('   - DSH: 重启 DSH 桌面端');
    console.log('   - Codex: 下次启动自动生效');
  }
}

main();
