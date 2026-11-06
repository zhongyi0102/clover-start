#!/usr/bin/env python3
"""
semantic-router-server — D 层语义兜底服务

用途：
  ao-dispatch.js 的 B 层（关键词）没命中时，调这个服务做语义分类。

原理：
  用 sentence-transformers 的轻量模型把请求和「路由样本」都转成向量，
  比较余弦相似度，返回最匹配的 route + 置信度。

  比关键词强的地方：能理解「这个函数的复杂度能不能优化」= 困难，
  即使它没包含「性能优化」这四个字。

启动：
  cd tools/semantic-router
  uv run server.py
  # 或 pip install -r requirements.txt && python server.py

端点：
  POST http://127.0.0.1:8765/route
  body: {"text": "...", "routes": ["image","hard","review",...]}
  resp: {"route": "hard", "confidence": 0.82, "scores": {...}}

  GET http://127.0.0.1:8765/health
  resp: {"ok": true, "model": "...", "routes": [...]}
"""

import json
import sys
from pathlib import Path

# ── 路由样本（用自然语言描述，不用写关键词）────────────────
# 想调整分配行为，改这里即可。
ROUTE_SAMPLES = {
    "image": [
        "看这张截图，界面哪里不对",
        "这个 UI 设计稿怎么实现",
        "图片里的报错信息是什么",
        "帮我看看这个界面截图",
        "这个原型图对应的页面布局",
        "图上的按钮位置有问题",
        "screenshot 里的错误",
        "这张 png 显示什么",
        "look at this screenshot",
        "what's wrong in this UI mockup",
        "check the image for errors",
    ],
    "hard": [
        "分析这个项目的并发模型有没有死锁风险",
        "设计一个缓存层，权衡内存占用和命中率",
        "这个性能瓶颈的根因是什么",
        "重构整个模块的架构",
        "为什么这里会有内存泄漏",
        "对比几种方案各自的利弊",
        "这个算法的时间复杂度能优化吗",
        "系统性的性能调优应该从哪里入手",
        "评估一下当前的架构设计是否合理",
        "这里的竞态条件怎么彻底解决",
        "全局搜索所有调用点并分析影响",
        "定位这个偶现 bug 的根本原因",
        "审查一下这个模块的设计模式",
        "选型：用 Redis 还是本地缓存",
        "复杂的分布式一致性问题怎么处理",
        "整体重构的可行性分析",
        "design a caching layer balancing memory and hit rate",
        "what is the root cause of this performance bottleneck",
        "analyze the concurrency model for deadlock risk",
        "compare the tradeoffs of these approaches",
    ],
    "review": [
        "审查这段代码",
        "review 一下这个 PR",
        "检查代码质量",
        "评估这段实现的健壮性",
        "找找这段代码有什么问题",
        "check this pull request for me",
        "review this PR",
        "look at this diff and find issues",
        "code review please",
        "is this implementation correct",
    ],
    "investigate": [
        "分析这个文件是干什么的",
        "调查这个函数被谁调用",
        "研究一下这个模块的职责",
        "解释这段逻辑",
        "找出这个变量在哪里赋值",
        "怎么会返回空",
        "看看这个类有哪些方法",
        "告诉我这个配置项的作用",
        "explain what this function does",
        "investigate where this is called from",
        "what does this module do",
        "find where this variable is assigned",
    ],
    "docs": [
        "更新 README 里的构建说明",
        "补一下 CHANGELOG",
        "给这个函数加注释",
        "把这段逻辑写清楚",
        "整理成文档",
        "写一下使用说明",
        "update the README",
        "add a note to CHANGELOG",
        "document this function",
    ],
    "test": [
        "给这个函数加单元测试",
        "补测试用例",
        "提高测试覆盖率",
        "加几个断言",
        "写一个 test",
        "这个 bug 要加回归测试",
        "add unit tests for this",
        "write a test case",
        "improve test coverage",
    ],
    "refactor": [
        "重构这个类",
        "把这段代码拆分成几个函数",
        "合并重复的逻辑",
        "抽取公共方法",
        "优化一下代码结构",
        "清理没用的代码",
        "重写这个模块",
        "refactor this class",
        "extract a helper method",
        "clean up dead code",
        "split this into smaller functions",
    ],
    "code": [
        "修复这个空引用",
        "实现一个新功能",
        "添加一个按钮",
        "新增一个配置项",
        "改一下这个变量的名字",
        "写一个函数计算总和",
        "开发这个页面",
        "这里报错了帮我修",
        "这个 bug 修一下",
        "调整一下这个参数",
        "fix this null reference",
        "implement a new feature",
        "add a button",
        "rename this variable",
        "fix the crash here",
    ],
}

# ── 依赖检查 ────────────────────────────────────────────────
try:
    from fastapi import FastAPI
    from pydantic import BaseModel
    import uvicorn
    from sentence_transformers import SentenceTransformer
    import numpy as np
except ImportError as e:
    print(f"❌ 缺少依赖: {e.name}", file=sys.stderr)
    print("", file=sys.stderr)
    print("安装方式（二选一）：", file=sys.stderr)
    print("  uv run server.py                    # uv 自动装", file=sys.stderr)
    print("  pip install -r requirements.txt     # 手动装", file=sys.stderr)
    sys.exit(1)


# ── 配置 ────────────────────────────────────────────────────
MODEL_NAME = "BAAI/bge-small-zh-v1.5"   # 中文小模型，~95MB，CPU 够快
PORT = 8765
HOST = "127.0.0.1"

# ── 初始化 ──────────────────────────────────────────────────
print(f"加载模型 {MODEL_NAME} …", flush=True)
model = SentenceTransformer(MODEL_NAME)

# 预计算每条样本的向量
route_vectors = {}
for route, samples in ROUTE_SAMPLES.items():
    vecs = model.encode(samples, normalize_embeddings=True, show_progress_bar=False)
    route_vectors[route] = np.asarray(vecs, dtype=np.float32)

print(f"就绪：{len(ROUTE_SAMPLES)} 条路由，"
      f"{sum(len(v) for v in ROUTE_SAMPLES.values())} 条样本", flush=True)

app = FastAPI(title="AO Semantic Router", version="0.1.0")


class RouteRequest(BaseModel):
    text: str
    routes: list[str] | None = None


@app.get("/health")
def health():
    return {
        "ok": True,
        "model": MODEL_NAME,
        "routes": list(ROUTE_SAMPLES.keys()),
        "samples": {k: len(v) for k, v in ROUTE_SAMPLES.items()},
    }


@app.post("/route")
def route(req: RouteRequest):
    """
    返回最匹配的 route 和置信度。

    置信度算法：
      对每条 route，取该 route 所有样本相似度的**最大值**（最近邻）。
      再用 softmax 把各 route 的最大相似度归一化成概率。
      这样 confidence 有概率含义，可以设阈值。
    """
    if not req.text or not req.text.strip():
        return {"route": None, "confidence": 0.0, "error": "empty text"}

    candidates = req.routes if req.routes else list(ROUTE_SAMPLES.keys())

    q = model.encode([req.text], normalize_embeddings=True, show_progress_bar=False)[0]
    q = np.asarray(q, dtype=np.float32)

    best_scores = {}
    for r in candidates:
        if r not in route_vectors:
            continue
        sims = route_vectors[r] @ q        # 余弦相似度（已归一化）
        best_scores[r] = float(np.max(sims))

    if not best_scores:
        return {"route": None, "confidence": 0.0, "error": "no valid routes"}

    # softmax（温度可调：温度低 → 更锐利）
    temperature = 0.05
    names = list(best_scores.keys())
    vals = np.array([best_scores[n] for n in names], dtype=np.float64)
    exp = np.exp((vals - vals.max()) / temperature)
    probs = exp / exp.sum()

    order = np.argsort(-probs)
    top_route = names[order[0]]
    confidence = float(probs[order[0]])

    return {
        "route": top_route,
        "confidence": round(confidence, 4),
        "scores": {n: round(float(p), 4) for n, p in zip(names, probs)},
        "similarities": {n: round(best_scores[n], 4) for n in names},
    }


if __name__ == "__main__":
    print(f"监听 http://{HOST}:{PORT}", flush=True)
    uvicorn.run(app, host=HOST, port=PORT, log_level="warning")
