# AGENTS.md — CloverStart 开发约定

> 本文件供 AI agent（Claude Code / Codex / DeepSeek Harness 等）读取。
> 人工开发同样适用。

## 构建与测试：必须用 Windows PowerShell 5.1

**不要用 PowerShell 7（`pwsh`）跑 `build.ps1` 或 `tests\run-tests.ps1`。**

```powershell
# ✅ 正确
C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1
C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1

# ❌ 错误（会报 CS0012）
pwsh -File build.ps1
```

### 为什么

`build.ps1:59` 的 `Resolve-Refs` 逻辑：

1. 先找 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\<name>.dll`
2. 找不到就回退 `[System.Reflection.Assembly]::LoadWithPartialName($name)`

WPF 的三个程序集**不在第 1 步那个目录**：

| 程序集 | Framework64\v4.0.30319\ 下有吗 |
|---|---|
| `System.dll` | ✅ |
| `PresentationFramework.dll` | ❌ |
| `PresentationCore.dll` | ❌ |
| `WindowsBase.dll` | ❌ |

它们在 GAC：`C:\Windows\Microsoft.NET\assembly\GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\`

于是走第 2 步回退，而 **PowerShell 7 的 `LoadWithPartialName` 解析到它自己携带的 .NET 10 WPF**：

```
<PowerShell 安装目录>\PresentationFramework.dll   (.NET 10)
```

`csc.exe` 是 .NET Framework 4.8 的编译器，与 .NET 10 程序集不兼容：

```
error CS0012: 类型"System.Object"在未引用的程序集中定义
  System.Runtime, Version=10.0.0.0
```

## 编译前必须关闭正在运行的程序

```powershell
Get-Process -Name "CloverStart" -ErrorAction SilentlyContinue | Stop-Process -Force
```

否则：

```
error CS0016: 无法写入输出文件 ... 另一个进程正在使用此文件
```

## 项目结构

```
src\core\        纯逻辑层（零 WPF 依赖，可单独编译成 DLL 做分层校验）
src\ui\          界面层（依赖 WPF）
tests\           测试套件（run-tests.ps1 统一驱动）
build.ps1        编译（直接调 csc.exe，不用 MSBuild / csproj）
dev-run.ps1      改完即跑：停进程 → 编译 → 重启 → 弹面板
install.ps1      创建开机自启
```

- **无 csproj / sln / NuGet 清单**，构建完全靠 `build.ps1`
- `src\core\Version.cs` 由 `build.ps1` 每次重写，**不入库**（在 `.gitignore` 里）
- 因此 `git clone` 后必须先跑一次 `build.ps1`，否则缺 `BuildInfo` 类

## 两道闸门

改动完成前必须两道都过：

| 闸门 | 命令 | 通过标准 |
|---|---|---|
| 1. 分层校验 + 编译 | `build.ps1` | exit 0，输出 `OK: ...\CloverStart.exe` |
| 2. 全部测试 | `tests\run-tests.ps1` | 输出「通过 N 项，失败 0 项」 |

分层校验的含义：`src\core` 只允许引用 `System` / `System.Core` / `System.Web.Extensions`，
不允许出现 `Window`、`BitmapImage`、`DispatcherTimer`、`Ui.Log` 等 WPF/UI 层内容。
违反即编译失败并中止。

## 改代码的边界

- **`src\core` 不许依赖 WPF**：分层校验会拦下来
- **不要手动改 `src\core\Version.cs`**：它由构建生成
- **`tests\run-tests.ps1` 里新增套件**要同时加 `Invoke-Suite` 调用，否则不会被跑到
- 隐藏语义（`AppItem.Hidden`）是「不在任何分类里显示」，任何列表渲染都要过滤它

## 提交前自检

```powershell
# 1. 编译
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1

# 2. 测试
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1

# 3. 工作区干净
git status --porcelain
```
