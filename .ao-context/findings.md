# Worker 发现记录

> 本文件由所有 agent 共享。**只追加，不删改已有条目。**
> 格式：标题 + 时间 + 作者 + 内容。

## （示例）AO 的 MCP 配置是运行时发现，不是用户可配

- 时间：2026-11-06
- 作者：brain

逆向结论：
- 项目配置 `projects.config` 里**没有** MCP 字段
- 会话状态 `conversations.mcp_servers_json` 只有 `{name, status}`，没有 command/args
- 全局 `app_settings` 里也没有 MCP
- AO 通过 ACP `session/new` 的 `mcpServers` 参数从 **agent 侧**读取再回传

因此要让 MCP 生效，必须配在 **agent 自己的配置文件**里。