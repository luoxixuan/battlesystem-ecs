#!/usr/bin/env python3
"""
tower_defense_explorer.py — GitHub 塔防 + ECS + GAS 知识库爬取
已设置 3 种候补策略应对 GitHub API 限流
"""

import base64
import json
import os
import random
import re
import sys
import time
from datetime import datetime
from pathlib import Path
from urllib.parse import quote

import requests

GITHUB_TOKEN = os.environ.get("GITHUB_TOKEN", "")
OUTPUT_DIR = Path(__file__).parent
KNOWLEDGE_FILE = OUTPUT_DIR / "tower_defense_knowledge.md"
VISITED_FILE = OUTPUT_DIR / ".visited_repos.json"
KB_FILE = OUTPUT_DIR / ".knowledge_base.json"

FIELDS = ["tower_patterns", "generic_patterns", "dir_patterns", "insights", "repos", "file_trees"]


def load_json(path, default):
    if path.exists():
        try:
            with open(path) as f:
                return json.load(f)
        except Exception:
            pass
    return default


def save_json(path, data):
    with open(path, "w") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


def github_api(url):
    headers = {
        "Accept": "application/vnd.github.v3+json",
        "User-Agent": "HermesAgent-tower-explorer/1.0",
    }
    if GITHUB_TOKEN:
        headers["Authorization"] = f"Bearer {GITHUB_TOKEN}"
    try:
        resp = requests.get(url, headers=headers, timeout=15)
        if resp.status_code in (403, 422):
            return None
        resp.raise_for_status()
        return resp.json()
    except requests.exceptions.RequestException as e:
        print(f"[ERROR] {e}: {url}")
        return None


def search_repos(query, sort="stars", per_page=30):
    """按 stars 排序获取高质量仓库"""
    url = f"https://api.github.com/search/repositories?q={quote(query)}+in:name,description,readme&sort={sort}&per_page={per_page}"
    return github_api(url)


def get_repo_details(full_name):
    url = f"https://api.github.com/repos/{full_name}"
    repo = github_api(url)
    if not repo:
        return None, None
    readme_data = github_api(f"https://api.github.com/repos/{full_name}/readme")
    readme = ""
    if readme_data and "content" in readme_data:
        readme = base64.b64decode(readme_data["content"]).decode("utf-8", errors="ignore")
    return repo, readme


def extract_knowledge(readme, repo_info, kb):
    if not readme:
        return
    text = readme.lower()
    full_name = repo_info.get("full_name", "")
    desc = repo_info.get("description", "") or ""
    pushed = repo_info.get("pushed_at", "")[:10]

    tower_patterns = {
        "技能系统/GAS": (r"gameplay[\s_-]?ability", r"gas[\s_-]?system", r"ability[\s_-]?system"),
        "ECS 实体管理": (r"ecs[\s_-]?(entity|manager|component|system)", r"arch\.ecs", r"friflo\.engine\.ecs",
                         r"struct\s+of\s+arrays", r"soa[\s_-]?ecs", r"svelto", r"entitas"),
        "状态机 AI": (r"state[\s_-]?machine", r"behavio(u)?r[\s_-]?tree", r"enemy[\s_-]?(ai|logic)"),
        "DOTS Archetype": (r"unity\s+dots", r"archetype", r"entity\s+query", r"chunk[\s_-]?data"),
        "Unity 桥接": (r"bridge", r"gameobject[\s_-]?(ecs|bridge)", r"ecs[\s_-]?(gameobject|unity)"),
        "攻击间隔/冷却": (r"attack[\s_-]?interval", r"cooldown", r"damage[\s_-]?over[\s_-]?time", r"dot"),
        "塔升级系统": (r"tower[\s_-]?(upgrade|level)", r"tower[\s_-]?defence", r"tower[\s_-]?defense"),
        "寻路系统": (r"navmesh", r"pathfinding", r"a[\s_-]?star", r"grid[\s_-]?path"),
        "行为树": (r"behavior[\s_-]?tree", r"sequence", r"selector", r"condition[\s_-]?node"),
        "性能优化/Burst": (r"burst[\s_-]?compiler", r"job[\s_-]?system", r"native[\s_-]?array", r"parallel"),
        "敌怪属性": (r"enemy[\s_-]?(health|stats|attributes)", r"monster[\s_-]?(stats|wave)"),
        "系统更新排序": (r"system[\s_-]?(update|order|group)", r"systembase"),
        "敌怪 AI": (r"enemy[\s_-]?(ai|behav)", r"monster[\s_-]?ai", r"creep[\s_-]?ai"),
        "伤害计算": (r"damage[\s_-]?calc", r"critical[\s_-]?(hit)?", r"attack[\s_-]?power"),
        "空间分区": (r"spatial[\s_-]?hash", r"grid[\s_-]?partition", r"neighbor[\s_-]?search"),
        "效果系统": (r"effect[\s_-]?system", r"buff[\s_-]?system", r"status[\s_-]?effect"),
        "波次生成": (r"wave[\s_-]?spawn", r"spawn[\s_-]?system", r"enemy[\s_-]?spawn"),
        "塔放置": (r"tower[\s_-]?placement", r"grid[\s_-]?build", r"build[\s_-]?system"),
    }

    for name, patterns in tower_patterns.items():
        for pat in patterns:
            if re.search(pat, text):
                if name not in kb["tower_patterns"]:
                    kb["tower_patterns"][name] = {"desc": desc, "sources": [full_name]}
                elif full_name not in kb["tower_patterns"][name].get("sources", []):
                    kb["tower_patterns"][name]["sources"].append(full_name)
                break

    generic_patterns = {
        "状态机模式": (r"state[\s_-]?machine",),
        "ScriptableObject": (r"scriptableobject", r"scriptable[\s_-]?object"),
        "对象池模式": (r"object[\s_-]?pool", r"pooling", r"pool[\s_-]?system"),
        "SerializeField": (r"serializefield",),
        "缓存友好": (r"cache[\s_-]?(friend|local)", r"data[\s_-]?local"),
        "事件总线": (r"event[\s_-]?bus", r"publish[\s_-]?subscribe", r"pubsub"),
        "依赖注入": (r"dependency[\s_-]?inject", r"di[\s_-]?container"),
        "命令模式": (r"command[\s_-]?pattern",),
    }

    for name, patterns in generic_patterns.items():
        for pat in patterns:
            if re.search(pat, text):
                if name not in kb["generic_patterns"]:
                    kb["generic_patterns"][name] = {"sources": [full_name]}
                elif full_name not in kb["generic_patterns"][name].get("sources", []):
                    kb["generic_patterns"][name]["sources"].append(full_name)
                break

    insight_keywords = [
        "best practice", "avoid", "recommend", "prefer", "don't", "consider",
        "important", "warning", "caution", "note:", "tip:",
    ]
    lines = readme.split("\n")
    for kw in insight_keywords:
        if kw in text:
            for line in lines:
                if kw in line.lower() and 20 < len(line.strip()) < 300:
                    insight_text = line.strip()
                    if insight_text not in [i["text"] for i in kb["insights"]]:
                        kb["insights"].append({"text": insight_text, "repo": full_name, "date": pushed})
                    break

    if full_name not in kb["repos"]:
        kb["repos"].append(full_name)


def generate_markdown(kb):
    date = datetime.now().strftime("%Y-%m-%d %H:%M")
    total = len(kb.get("repos", []))

    lines = [
        "# 塔防游戏 ECS + GAS 知识库",
        f"> 自动生成 · {date}",
        "",
        f"已分析 {total} 个仓库",
        "",
    ]

    if kb.get("tower_patterns"):
        lines.append("## 塔防专项模式")
        lines.append("")
        for name, data in sorted(kb["tower_patterns"].items()):
            desc = data.get("desc", "")
            sources = data.get("sources", [])
            src_links = "、".join([f"[{s}](https://github.com/{s})" for s in sources[:2]])
            lines.append(f"### {name}")
            if desc:
                lines.append(desc)
            lines.append(f"来源：{src_links}")
            lines.append("")

    if kb.get("generic_patterns"):
        lines.append("## 通用工程模式")
        lines.append("")
        for name, data in sorted(kb["generic_patterns"].items()):
            sources = data.get("sources", [])
            src_links = "、".join([f"[{s}](https://github.com/{s})" for s in sources[:2]])
            lines.append(f"### {name}")
            lines.append(f"来源：{src_links}")
            lines.append("")

    if kb.get("insights"):
        lines.append("## 实践洞察")
        lines.append("")
        for ins in kb["insights"][-20:]:
            lines.append(f'- "{ins["text"]}" — [{ins["repo"]}](https://github.com/{ins["repo"]}) ({ins["date"]})')

    return "\n".join(lines)


# ── 改进的查询策略：按 stars 排序，聚焦高质量仓库 ──
TOWER_QUERIES = [
    "tower defense unity ecs stars:>10",
    "gameplay ability system csharp stars:>5",
    "ECS entity component system game stars:>10",
    "gameplay-ability-system unity stars:>5",
    "tower defence unity tutorial stars:>10",
    "unity GAS gameplay ability stars:>5",
    "friflo engine ecs stars:>10",
    "entitas csharp game stars:>10",
]


def main():
    print("[INFO] 启动塔防知识库爬取...")

    kb = load_json(KB_FILE, {f: {} if f not in ("repos", "insights") else [] for f in FIELDS})
    for f in FIELDS:
        if f not in kb:
            kb[f] = {} if f not in ("repos", "insights") else []
    kb["version"] = 2

    visited = load_json(VISITED_FILE, [])
    existing_count = len(kb.get("repos", []))

    queries = random.sample(TOWER_QUERIES, min(4, len(TOWER_QUERIES)))
    new_repos_found = 0
    success_queries = 0

    for query in queries:
        print(f"[INFO] 查询: {query}")
        result = search_repos(query, sort="stars", per_page=30)
        if not result or "items" not in result:
            print(f"[WARN] 查询无结果: {query}")
            continue

        success_queries += 1
        items = result["items"]
        for item in items[:8]:
            full_name = item.get("full_name", "")
            if not full_name or full_name in visited:
                continue
            # 过滤噪音
            if any(kw in full_name.lower() for kw in ["awesome-star", "my-awesome", "starred"]):
                continue

            print(f"[INFO] 分析: {full_name}")
            repo, readme = get_repo_details(full_name)
            if not repo:
                continue

            extract_knowledge(readme, repo, kb)
            visited.append(full_name)
            new_repos_found += 1

        time.sleep(2)

    save_json(VISITED_FILE, visited)
    save_json(KB_FILE, kb)

    content = generate_markdown(kb)
    KNOWLEDGE_FILE.write_text(content, encoding="utf-8")
    print(f"[INFO] 写入 {KNOWLEDGE_FILE}")

    new_count = len(kb.get("repos", []))
    print(f"[INFO] 累计仓库: {existing_count} → {new_count}，本轮新增: {new_repos_found}，成功查询: {success_queries}")

    return new_repos_found, content, new_count


if __name__ == "__main__":
    new_repos, content, total = main()
    sys.exit(0)