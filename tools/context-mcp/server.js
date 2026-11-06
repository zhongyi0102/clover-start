#!/usr/bin/env node
/**
 * context-mcp — AO 多 agent 共享上下文的 MCP server
 *
 * 目的：让 AO 编排器派出的 DSH / Codex worker 共用一份上下文，
 * 而不是各自为战、互相不知道对方做了什么。
 *
 * 存储：项目根目录下的 .ao-context/ 目录（纯文件，可 git 追踪或 gitignore）
 *   state.json      当前任务、进度、各 worker 状态
 *   decisions.md    已定的技术决策（append-only）
 *   findings.md     各 worker 的发现（append-only）
 *
 * 传输：stdio（AO 通过 ACP session/new 的 mcpServers 注入）
 */

const { Server } = require('@modelcontextprotocol/sdk/server/index.js');
const { StdioServerTransport } = require('@modelcontextprotocol/sdk/server/stdio.js');
const {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} = require('@modelcontextprotocol/sdk/types.js');
const fs = require('fs');
const path = require('path');

// 上下文目录：优先环境变量，否则当前工作目录
const CONTEXT_DIR = process.env.AO_CONTEXT_DIR
  || path.join(process.cwd(), '.ao-context');

const STATE_FILE = path.join(CONTEXT_DIR, 'state.json');
const DECISIONS_FILE = path.join(CONTEXT_DIR, 'decisions.md');
const FINDINGS_FILE = path.join(CONTEXT_DIR, 'findings.md');

function ensureDir() {
  fs.mkdirSync(CONTEXT_DIR, { recursive: true });
}

function readState() {
  ensureDir();
  try {
    return JSON.parse(fs.readFileSync(STATE_FILE, 'utf8'));
  } catch {
    return { task: null, workers: {}, updatedAt: null };
  }
}

function writeState(state) {
  ensureDir();
  state.updatedAt = new Date().toISOString();
  fs.writeFileSync(STATE_FILE, JSON.stringify(state, null, 2), 'utf8');
}

function appendMarkdown(file, heading, body, author) {
  ensureDir();
  const stamp = new Date().toISOString();
  const block = `\n## ${heading}\n\n- 时间：${stamp}\n- 作者：${author || 'unknown'}\n\n${body}\n`;
  fs.appendFileSync(file, block, 'utf8');
}

function readMarkdown(file, maxChars = 8000) {
  try {
    const text = fs.readFileSync(file, 'utf8');
    return text.length > maxChars
      ? `...(前面省略 ${text.length - maxChars} 字符)\n` + text.slice(-maxChars)
      : text;
  } catch {
    return '(还没有内容)';
  }
}

const server = new Server(
  { name: 'context-mcp', version: '0.1.0' },
  { capabilities: { tools: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: [
    {
      name: 'get_context',
      description:
        '读取共享上下文：当前任务、各 worker 状态、已定决策、所有发现。' +
        '开工前必须先调用这个，否则你不知道别人做了什么。',
      inputSchema: { type: 'object', properties: {}, required: [] },
    },
    {
      name: 'set_task',
      description:
        '设置当前共享任务（通常只有编排器用）。覆盖 state.json 的 task 字段。',
      inputSchema: {
        type: 'object',
        properties: {
          task: { type: 'string', description: '任务描述' },
          scope: { type: 'string', description: '范围/边界，可选' },
        },
        required: ['task'],
      },
    },
    {
      name: 'update_worker',
      description:
        '更新某个 worker 的状态。worker 自己开工/完工时调用。',
      inputSchema: {
        type: 'object',
        properties: {
          name: { type: 'string', description: 'worker 名（如 dsh-1 / codex-1）' },
          status: {
            type: 'string',
            enum: ['working', 'done', 'blocked', 'failed'],
            description: '当前状态',
          },
          note: { type: 'string', description: '补充说明，可选' },
        },
        required: ['name', 'status'],
      },
    },
    {
      name: 'append_finding',
      description:
        '把你的发现追加进 findings.md。比如「src/core/SafeCall.cs 第 42 行有空引用风险」。' +
        '不要覆盖别人的条目，只追加。',
      inputSchema: {
        type: 'object',
        properties: {
          title: { type: 'string', description: '发现的一句话标题' },
          body: { type: 'string', description: '详细内容（Markdown）' },
          author: { type: 'string', description: '你的身份（worker 名）' },
        },
        required: ['title', 'body'],
      },
    },
    {
      name: 'append_decision',
      description:
        '记录一个已定的技术决策，追加进 decisions.md。' +
        '只有编排器或明确被授权的 worker 才该调用。',
      inputSchema: {
        type: 'object',
        properties: {
          title: { type: 'string', description: '决策标题' },
          body: { type: 'string', description: '决策内容与理由' },
          author: { type: 'string', description: '你的身份' },
        },
        required: ['title', 'body'],
      },
    },
    {
      name: 'read_decisions',
      description: '读所有已定决策（decisions.md）。动手前该看一眼，别推翻已有决策。',
      inputSchema: { type: 'object', properties: {}, required: [] },
    },
    {
      name: 'read_findings',
      description: '读所有 worker 的发现（findings.md）。',
      inputSchema: { type: 'object', properties: {}, required: [] },
    },
  ],
}));

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args = {} } = request.params;

  try {
    switch (name) {
      case 'get_context': {
        const state = readState();
        return {
          content: [
            {
              type: 'text',
              text: [
                '# 共享上下文',
                '',
                `## 当前任务`,
                state.task ? JSON.stringify(state.task, null, 2) : '(未设置)',
                '',
                `## Worker 状态`,
                Object.keys(state.workers).length
                  ? Object.entries(state.workers)
                      .map(([k, v]) => `- **${k}**: ${v.status}${v.note ? ` — ${v.note}` : ''} (${v.updatedAt})`)
                      .join('\n')
                  : '(还没有 worker)',
                '',
                `## 已定决策`,
                readMarkdown(DECISIONS_FILE, 4000),
                '',
                `## 所有发现`,
                readMarkdown(FINDINGS_FILE, 6000),
                '',
                `最后更新：${state.updatedAt || '(从未)'}`,
              ].join('\n'),
            },
          ],
        };
      }

      case 'set_task': {
        const state = readState();
        state.task = {
          text: args.task,
          ...(args.scope ? { scope: args.scope } : {}),
          setAt: new Date().toISOString(),
        };
        writeState(state);
        return {
          content: [{ type: 'text', text: `已设置任务：${args.task}` }],
        };
      }

      case 'update_worker': {
        const state = readState();
        state.workers[args.name] = {
          status: args.status,
          ...(args.note ? { note: args.note } : {}),
          updatedAt: new Date().toISOString(),
        };
        writeState(state);
        return {
          content: [{ type: 'text', text: `已更新 ${args.name} → ${args.status}` }],
        };
      }

      case 'append_finding': {
        appendMarkdown(FINDINGS_FILE, args.title, args.body, args.author);
        return {
          content: [{ type: 'text', text: `已追加发现：${args.title}` }],
        };
      }

      case 'append_decision': {
        appendMarkdown(DECISIONS_FILE, args.title, args.body, args.author);
        return {
          content: [{ type: 'text', text: `已追加决策：${args.title}` }],
        };
      }

      case 'read_decisions': {
        return {
          content: [{ type: 'text', text: readMarkdown(DECISIONS_FILE, 10000) }],
        };
      }

      case 'read_findings': {
        return {
          content: [{ type: 'text', text: readMarkdown(FINDINGS_FILE, 12000) }],
        };
      }

      default:
        return {
          content: [{ type: 'text', text: `未知工具：${name}` }],
          isError: true,
        };
    }
  } catch (err) {
    return {
      content: [{ type: 'text', text: `出错：${err.message}` }],
      isError: true,
    };
  }
});

const transport = new StdioServerTransport();
server.connect(transport).catch((err) => {
  console.error('context-mcp 启动失败：', err);
  process.exit(1);
});
