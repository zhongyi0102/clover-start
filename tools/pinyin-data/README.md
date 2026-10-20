# 拼音数据（pinyin-data）

`src\PinyinData.cs` 的数据来源。**不要手工编辑 `pinyin-table.txt`**，改数据请重新生成。

## 来源与许可

- 项目：[pinyin-pro](https://github.com/zh-lx/pinyin-pro)
- 许可：**MIT License, Copyright (c) 2022-present zh-lx**
- 取用文件：`dist/esm/data/dict1.mjs`（单字字典，形如 `'bǎng páng pāng': ['膀'],`）

MIT 允许商用、修改和再分发，条件是保留版权声明。`src\PinyinData.cs` 的文件头已经写明来源与许可。

> 历史说明：早期版本用的是 `C:\Windows\System32\chs_singlechar_pinyin.dat`（微软拼音系统字库）。
> 那是微软的专有数据，派生表编译进 MIT 项目再公开分发有版权风险，2026-10 已替换。

## 重新生成流程

```powershell
# 1. 下载 pinyin-pro（任选其一）
npm pack pinyin-pro            # 得到 pinyin-pro-x.y.z.tgz
#   或直接从镜像下 tarball：
#   https://registry.npmmirror.com/pinyin-pro/-/pinyin-pro-<版本>.tgz
tar -xzf pinyin-pro-*.tgz      # 解开出 package\ 目录

# 2. 提取单字表（生成 pinyin-table.txt）
node tools\pinyin-data\extract.mjs <解开的 package 目录>

# 3. 压成 C# 数组（生成 src\PinyinData.cs）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-pinyin.ps1
```

`extract.mjs` 的规则：
- 声调符号去掉（`ā→a`、`ǖ→v`），只保留无调小写拼音
- 同字多音时保留字典里的**第一个**读音
- 只收 BMP 内字符（C# 侧用 `char` 单码元查表）
- 输出按码位升序

## 文件说明

| 文件 | 说明 |
|---|---|
| `pinyin-table.txt` | 提取结果，每行「十进制码位 空格 无调拼音」，约 2 万行 / 200 KB |
| `extract.mjs` | 从 pinyin-pro 提取上表的脚本，需要 Node.js |

`pinyin-table.txt` 入库是为了让 clone 下来的人不装 Node 也能直接跑 `make-pinyin.ps1`。
