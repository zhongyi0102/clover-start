# 技术决策记录

> 本文件由所有 agent 共享。**只追加，不删改已有条目。**
> 有异议请在 `findings.md` 里写，不要直接改这里。

## 用 Windows PowerShell 5.1 构建（不是 pwsh 7）

- 时间：2026-11-06
- 决策者：brain

`build.ps1` 的 `Resolve-Refs` 在 PowerShell 7 下会回退到 pwsh 自带的 .NET 10 WPF 程序集，
与 .NET Framework 4.8 的 `csc.exe` 不兼容，报 `CS0012`。

正确调用：

```powershell
C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1
```