#!/usr/bin/env python3
"""
塔防游戏 ECS + GAS 专项探索 v3
聚焦：tower defense + ECS architecture + ability system + Unity
目标：为 BattleSystem-ECS 项目提供可落地的架构参考 + 具体游戏机制

用法：
    python tower_defense_explorer.py           # 轻量模式（只拉 README）
    python tower_defense_explorer.py --deep     # 深度模式（README + 文件树 + 源码片段）
"""

import base64
import json
import os
import random
import re
import sys
import time
from datetime import datetime

import requests

OUTPUT_DIR = os.path.expanduser("~/.hermes/cron/output/tower_defense_explorer")
os.makedirs(OUTPUT_DIR, exist_ok=True)

HISTORY_FILE = os.path.join(OUTPUT_DIR, "visited_repos.json")
KB_FILE = os.path.join(OUTPUT_DIR, "knowledge_base.json")

# Token 优先级：环境变量 > ~/.hermes/github_token
GITHUB_TOKEN = os.environ.get("GITHUB_TOKEN", "")
if not GITHUB_TOKEN:
    TOKEN_FILE = os.path.expanduser("~/.hermes/github_token")
    if os.path.exists(TOKEN_FILE):
        with open(TOKEN_FILE, "r", encoding="utf-8") as f:
            GITHUB_TOKEN = f.read().strip()

HEADERS = {"Accept": "application/vnd.github.v3+json", "User-Agent": "HermesAgent-tower-explorer/3.0"}
if GITHUB_TOKEN:
    HEADERS["Authorization"] = f"token {GITHUB_TOKEN}"


# ============ 塔防专项知识提取规则 ============

TOWER_PATTERNS = [
    # ── 波次与生成 ──
    (r"wave\s*(spawn|system|generator|manager)", "波次生成系统", "塔防核心：波次配置化、动态难度、敌人生成调度"),
    (r"spawn\s*(pool|manager|system)", "敌人生成池", "对象池化生成，支持波次间复用"),
    (r"enemy\s*(pool|manager|registry)", "敌怪注册表", "统一管理所有敌怪实体，支持快速查询与销毁"),
    (r"wave\s*config|wave\s*data|wave\s*definition", "波次配置化", "波次属性（敌人类型、数量、间隔、Buff）用 JSON/SO 管理"),

    # ── 塔与攻击 ──
    (r"tower\s*(upgrade|level|stats)", "塔升级系统", "塔等级/星级/进阶，属性成长曲线配置化"),
    (r"tower\s*(attack|target|range)", "塔攻击系统", "射程检测、目标选择策略（最近/血量最低/随机）"),
    (r"projectile\s*(pool|manager)", "弹道对象池", "子弹/技能弹道复用，减少 GC"),
    (r"damage\s*(calculation|formula)", "伤害计算", "攻击/防御/暴击/属性缩放公式"),
    (r"attack\s*(speed|rate|interval)", "攻击间隔", "攻速属性、独立冷却管理"),
    (r"tower\s*(sell|refund|salvage)", "塔出售/回收", "出售塔返还部分金币，支持衰减曲线"),

    # ── 技能与 Buff ──
    (r"ability\s*(system|manager|component)", "技能系统", "GAS 风格 Ability + Modifier 分离"),
    (r"buff\s*(system|stack|debuff)", "Buff/Debuff 系统", "叠加层数、持续时间、效果叠加规则"),
    (r"cooldown\s*(manager|system|reduction)", "冷却管理", "全局/独立冷却，CDR 缩减"),
    (r"modifier\s*(system|stack)", "属性修正系统", "加减乘除多段修正，优先级/覆盖规则"),
    (r"effect\s*(system|queue)", "效果系统", "伤害/治疗/控制效果排队执行"),

    # ── ECS 架构 ──
    (r"entity\s*(manager|registry|component)", "实体管理器", "ECS 风格：实体创建/销毁/查询"),
    (r"component\s*(store|registry|bank)", "组件存储", "SOA 数组布局，避免 Dictionary GC"),
    (r"system\s*update|system\s*base", "系统更新", "SystemBase 按组排序更新，数据逻辑分离"),
    (r"spatial\s*(hash|grid|partition)", "空间分区", "GridSpatialHash O(1) 邻域查询，避免全量遍历"),
    (r"archetype|entity\s*query|chunk", "Unity DOTS Archetype", "DOTS 模式：chunk data layout + entity query"),

    # ── 寻路与地图 ──
    (r"pathfinding|navmesh|grid\s*path", "寻路系统", "A*/BFS/网格寻路，敌人沿路径移动"),
    (r"waypoint\s*(system|path)", "路径点系统", "预定义路径点序列，支持分支路径"),
    (r"map\s*(grid|cell|tile)", "地图网格", "格子系统，10x50 或任意尺寸配置化"),

    # ── 数值与成长 ──
    (r"stat\s*(system|modifier|attribute)", "属性系统", "攻击力/血量/攻速等属性，支持加成/缩放"),
    (r"level\s*(curve|scaling|progression)", "成长曲线", "经验/等级/敌人强度曲线配置化"),
    (r"gold\s*(reward|system|economy|steal)", "金币经济", "击杀奖励/升级消耗/商店/偷金"),
    (r"enemy\s*(health|scaling|stats)", "敌怪属性", "敌怪血量/攻击/速度随波次成长"),

    # ── 状态机与 AI ──
    (r"state\s*(machine|ai|behaviour)", "状态机 AI", "敌怪状态机：移动/攻击/死亡"),
    (r"behavior\s*(tree|node|selector)", "行为树", "行为树节点：Sequence/Selector/Condition/Action"),
    (r"enemy\s*(ai|brain|controller)", "敌怪 AI", "AI 决策：追踪/逃跑/施法/躲避"),

    # ══════════════════════════════════════════
    # 具体游戏机制（v3 新增 35 项）
    # ══════════════════════════════════════════

    # ── 环境/天气/地形 ──
    (r"weather\s*(system|effect|rain|snow|wind)", "天气系统", "雨天减速/雪天减速/风暴，影响移速和攻速"),
    (r"terrain\s*(type|effect|modifier|mud|ice|swamp)", "地形效果", "泥地减速/冰面滑行/沼泽伤害，格子属性"),
    (r"day\s*night|night\s*cycle|diurnal", "昼夜循环", "白天/夜晚切换影响视野、敌人强度、塔属性"),
    (r"fog\s*(of\s*)?war|visibility|line\s*of\s*sight", "战争迷雾/视野", "未探索区域不可见，塔/单位提供视野范围"),

    # ── 弹道类型 ──
    (r"chain\s*(lightning|damage|attack|link)", "连锁攻击", "弹道命中后弹跳到附近敌人，衰减伤害"),
    (r"bounce|ricochet|rebound", "弹跳弹道", "子弹碰到目标后弹向下一目标"),
    (r"piercing|penetrat(e|ing)\s*(projectile|shot)", "穿透弹道", "子弹穿过敌人继续飞行，线性伤害"),
    (r"splash|aoe\s*damage|area\s*of\s*effect", "范围溅射", "命中目标后对周围敌人造成范围伤害"),
    (r"mortar|arcing|parabolic|lob", "弧线弹道", "迫击炮/抛射弹道，无视地形障碍"),
    (r"homing|tracking\s*projectile|seeking", "追踪弹道", "子弹自动追踪目标，拐弯"),

    # ── 塔特殊机制 ──
    (r"beam|laser\s*(tower|damage)|continuous\s*damage", "光束/激光塔", "持续照射敌人，每帧 tick 伤害。预热/过热机制"),
    (r"overheat|heat\s*(system|gauge|mechanic)", "过热/热量系统", "连续攻击积累热量，过高时降低攻速或停火冷却"),
    (r"energy|mana\s*(tower|system|cost)", "塔能量/法力", "塔消耗法力攻击，法力恢复/消耗管理"),
    (r"patrol|moving\s*tower|mobile\s*tower", "巡逻/移动塔", "塔可沿路径移动，动态调整防守位置"),
    (r"trap\s*(tower|deploy|placement)", "陷阱塔", "一次性触发类型塔，敌人经过时引爆/触发效果"),
    (r"morph|transform\s*(tower|mode)", "塔变形/形态切换", "塔可在多种形态间切换（对单/对群/控制）"),
    (r"totem|summon\s*(tower|turret)", "图腾/召唤塔", "玩家放置图腾产生光环或召唤物"),
    (r"lure|decoy|bait\s*(tower|entity)", "诱饵/吸引塔", "吸引敌人偏离路径或攻击诱饵而非基地"),
    (r"stealth|invisib|camouflage\s*(tower|enemy)", "隐形/伪装", "塔或敌人进入隐形状态，需反隐手段"),
    (r"chrono|time\s*(slow|stop|speed|dilation)", "时间操纵", "子弹时间/时间减速/加速，影响全局或区域"),
    (r"path\s*(block|modif|redirect|alter)", "路径修改", "塔/技能改变敌人移动路径，临时封锁格子"),
    (r"build\s*time|construction\s*(delay|progress)", "塔建造延迟", "塔放置后有建造时间，期间可被摧毁"),

    # ── 敌人特殊机制 ──
    (r"phase|ghost|ethereal|intangible", "相位/幽灵敌人", "敌人可穿越塔/障碍，免疫物理伤害"),
    (r"fear|confus|panic|rout", "恐惧/混乱", "敌人反向逃跑或随机移动，CC 状态"),
    (r"lifesteal|leech|vampir", "敌人吸血", "敌人攻击时回复自身血量百分比"),
    (r"burrow|underground|tunnel", "钻地/潜行", "敌人钻入地下躲避攻击，然后冒出"),
    (r"necromancer|revive|resurrect|reanimate", "复活/亡灵法师", "敌人死后被复活，或从尸体生成新敌人"),
    (r"vanguard|protector|shield\s*guard", "肉盾/守卫", "坦克敌人替附近友方吸收伤害"),
    (r"healer|heal\s*enemy|regen\s*aura", "敌人治疗", "敌方治疗单位为周围敌人回血"),
    (r"split|fission|multiply|clone", "分裂/克隆", "敌人死后分裂为多个小怪，或主动克隆"),
    (r"shield|barrier|hit\s*count|damage\s*threshold", "N击护盾/屏障", "需N次命中击破的护盾，或伤害阈值盾"),
    (r"elemental\s*(shield|immune|react)", "元素护盾/免疫", "火焰/冰霜/雷电护盾，特定伤害类型免疫"),
    (r"stacking\s*penalty|overcrowd|congestion", "堆叠惩罚", "同格敌人过多时减速/持续受伤"),
    (r"last\s*stand|death\s*rattle|enrage|frenzy", "末段狂暴/背水", "敌人低血量时攻速/移速暴增"),
    (r"aggro\s*(leash|range|radius)", "仇恨/脱战范围", "敌人追击距离限制，超距后回归路径"),
    (r"one\s*shot\s*protect|hp\s*floor|damage\s*cap", "一击必杀保护", "单次伤害不超过最大血量百分比"),

    # ── 资源/经济 ──
    (r"prestige|meta\s*progress|persistent\s*upgrade", "元进度/声望", "跨局永久升级，局外成长"),
    (r"shop\s*(reroll|refresh|restock)", "商店洗牌", "刷新可购买塔/道具选项，消耗金币"),
    (r"kill\s*(reward|bonus|reset\s*cooldown)", "击杀奖励/冷却重置", "击杀敌人触发额外金币或重置技能CD"),
    (r"sell\s*(decay|depreciat)", "出售衰减", "塔放置越久出售价格越低"),

    # ── 协同/光环 ──
    (r"aura\s*(effect|system|buff|curse|debuff)", "光环系统", "塔或敌人产生范围光环，增益/减益周围单位"),
    (r"synergy|combo\s*(tower|element)", "塔协同/连携", "同类型或相邻塔产生属性加成"),
    (r"reflect|retaliat|thorns|spike\s*damage", "伤害反弹/反击", "受到攻击时反弹百分比伤害给攻击者"),
    (r"pull|vortex|vacuum|suction", "拉扯/吸引", "将敌人拉向塔或特定位置"),

    # ── 战斗机制 ──
    (r"overkill|excess\s*damage|overflow", "过量伤害/溢出", "伤害超过目标血量时溢出给周围敌人"),
    (r"execute|finish|death\s*mark|cull", "处决/死亡标记", "目标血量低于阈值自动处决，额外金币"),
    (r"stagger|poise|break|knockback", "失衡/破防/击退", "累积伤害触发硬直/击退/打断施法"),
    (r"banish|exile|phase\s*out", "放逐/移除", "暂时移除敌人，N秒后返回"),
    (r"bleed|wound|cripple|slow\s*on\s*hit", "流血/受伤减速", "攻击附带 DoT 或减速效果"),
    (r"elemental\s*react|status\s*combo", "元素反应", "冰+火=融化，火+电=超载，特定组合触发额外效果"),
    (r"healing\s*zone|heal\s*area|regen\s*field", "治疗区域", "范围持续治疗友方单位"),
    (r"bullet\s*time|slow\s*motion|frame\s*skip", "子弹时间", "全局/局部时间减速，仅敌人受影响"),

    # ── 资源系统 ──
    (r"mana\s*burn|resource\s*drain|stat\s*drain", "法力燃烧/资源剥夺", "攻击降低目标法力/属性"),
    (r"heal\s*supress|heal\s*reduc|grievous\s*wound", "治疗抑制/重伤", "降低目标受到的治疗效果"),
    (r"replay|record|telemetry|frame\s*data", "回放/录像", "记录每帧数据用于回放分析"),

    # ── 敌人特殊能力 ──
    (r"channel|interrupt|cast\s*time", "施法可打断", "敌人施法有前摇，可被CC打断"),
    (r"trample|charge|rush\s*attack", "踩踏/冲锋", "Boss 直线冲锋伤害路径上的单位"),
    (r"strafe|sidestep|dodge|evade", "闪避/侧移", "敌人侧向移动躲避弹道"),
    (r"tether|link|bond|leash\s*enemy", "锁链/连接", "两个敌人生命/伤害共享，强制绑定"),
]

GENERIC_PATTERNS = [
    (r"object\s*pool", "对象池模式", "复用对象，减少 Instantiate/Destroy"),
    (r"event\s*(bus|dispatch)", "事件总线", "解耦系统通信，Publish/Subscribe"),
    (r"command\s*(pattern|queue)", "命令模式", "操作封装，支持撤销/重做"),
    (r"state\s*machine", "状态机模式", "状态转换清晰，可视化"),
    (r"factory\s*(method|abstract)", "工厂模式", "复杂对象创建逻辑封装"),
    (r"struct\s*vs\s*class|value\s*type", "结构体优先", "小型固定数据用 struct，避免 GC"),
    (r"cache\s*(friendly|locality)", "缓存友好", "数据连续布局，缓存命中优先"),
    (r"gc\s*(pressure|avoid|optim)", "GC 优化", "对象池、数组复用、避免每帧 new"),
    (r"serializefield", "SerializeField", "Inspector 调试，保留封装"),
    (r"scriptableobject", "ScriptableObject", "数据资产化，配置与代码分离"),
    (r"async\s*(load|operation)", "异步加载", "协程/UniTask 异步加载资源"),
]

# 文件树中的关键目录模式（深度模式下提取）
TREE_DIR_PATTERNS = [
    (r"(ECS|EntityComponent)", "ECS 架构"),
    (r"(Systems|Components)", "系统/组件分离"),
    (r"(GAS|Abilities|GameplayAbilities)", "GAS 技能系统"),
    (r"(Wave|Waves|Spawn)", "波次系统"),
    (r"(Tower|Towers)", "塔系统"),
    (r"(Enemy|Enemies|Monster)", "敌怪系统"),
    (r"(Skill|Ability|AbilitySystem)", "技能/能力系统"),
    (r"(Buff|Debuff|Status)", "Buff/Debuff 系统"),
    (r"(Pathfinding|Navigation|Navmesh)", "寻路系统"),
    (r"(Projectile|Bullet)", "弹道系统"),
    (r"(Pool|ObjectPool)", "对象池"),
    (r"(Event|Bus|Message)", "事件系统"),
    (r"(Config|Data)", "配置数据"),
    (r"(BehaviorTree|AI|StateMachine)", "AI/行为树"),
    (r"(Benchmark|Performance|Profiler)", "性能优化"),
]

# 源码中值得提取的模式（深度模式读取 .cs 文件后匹配）
CODE_PATTERNS = [
    (r'(?:class|struct)\s+(\w+Config)', "配置结构"),
    (r'(?:enum)\s+(\w+Type)', "枚举类型"),
    (r'float\[\]\s+(\w+)', "SOA 浮点数组"),
    (r'int\[\]\s+(\w+)', "SOA 整型数组"),
    (r'bool\[\]\s+(\w+)', "SOA 布尔数组"),
    (r'Parallel\.For', "并行化"),
    (r'Active(\w+)Ids', "活跃ID缓存"),
    (r'DestroyEntity|QueueEnemyDeath', "实体销毁"),
    (r'(damage|health|armor|speed|range)\s*\*=', "属性倍率修正"),
]


def load_json(path, default):
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    return default


def save_json(path, data):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


def load_history():
    return load_json(HISTORY_FILE, [])


def save_history(history):
    save_json(HISTORY_FILE, list(set(history)))


def load_kb():
    kb = load_json(KB_FILE, {
        "tower_patterns": {},
        "generic_patterns": {},
        "dir_patterns": {},
        "insights": [],
        "repos": [],
        "file_trees": {},
        "code_snippets": {},  # v3 新增：源码片段
        "version": 3,
    })
    if "dir_patterns" not in kb:
        kb["dir_patterns"] = {}
    if "file_trees" not in kb:
        kb["file_trees"] = {}
    if "code_snippets" not in kb:
        kb["code_snippets"] = {}
    return kb


def save_kb(kb):
    save_json(KB_FILE, kb)


def normalize_id(text):
    text = text.lower()
    text = re.sub(r'[^\w\u4e00-\u9fff]', '_', text)
    text = re.sub(r'_+', '_', text)
    return text[:50]


def github_api(url):
    try:
        resp = requests.get(url, headers=HEADERS, timeout=15)
        if resp.status_code in (403, 422):
            return None
        resp.raise_for_status()
        return resp.json()
    except Exception:
        return None


def search_repos(query, per_page=30, page=None):
    url = "https://api.github.com/search/repositories"
    params = {"q": query, "sort": "stars", "order": "desc", "per_page": per_page}
    if page:
        params["page"] = page
    try:
        resp = requests.get(url, headers=HEADERS, params=params, timeout=30)
        resp.raise_for_status()
        return resp.json().get("items", [])
    except Exception as e:
        print(f"  搜索失败 [{query[:50]}]: {e}")
        return []


def get_readme(owner, repo):
    try:
        url = f"https://api.github.com/repos/{owner}/{repo}/readme"
        resp = requests.get(url, headers=HEADERS, timeout=15)
        if resp.status_code == 200:
            content = resp.json().get("content", "")
            return base64.b64decode(content).decode("utf-8", errors="ignore")
    except Exception:
        pass
    return ""


def get_file_tree(owner, repo, max_depth=2, delay=1.0):
    """拉取目录结构 + 读取关键源码文件内容"""
    result = {"dirs": [], "key_files": [], "code_extracts": []}

    def _walk(path="", depth=0):
        if depth > max_depth:
            return
        try:
            url = f"https://api.github.com/repos/{owner}/{repo}/contents/{path}"
            resp = requests.get(url, headers=HEADERS, timeout=15)
            time.sleep(delay)
            if resp.status_code != 200:
                return
            items = resp.json()
            if not isinstance(items, list):
                return

            for item in items:
                name = item.get("name", "")
                item_type = item.get("type", "file")

                if item_type == "dir":
                    for pattern, label in TREE_DIR_PATTERNS:
                        if re.search(pattern, name, re.IGNORECASE):
                            result["dirs"].append({"name": name, "label": label, "path": f"{path}{name}"})
                            break
                    # 只递归第一层
                    if depth == 0:
                        _walk(f"{name}/", depth + 1)

                else:
                    ext = os.path.splitext(name)[1].lower()
                    if ext in (".cs", ".py", ".go", ".rs", ".cpp", ".h", ".hpp"):
                        fp = f"{path}{name}"
                        result["key_files"].append(fp)

                        # v3: 读取源码内容（限 5 个文件）
                        if ext in (".cs",) and len(result["code_extracts"]) < 5:
                            code = _fetch_file_content(owner, repo, fp)
                            if code:
                                result["code_extracts"].append({
                                    "path": fp,
                                    "content": code[:3000],  # 截断防过大
                                    "size": len(code),
                                })

        except Exception:
            return

    _walk()
    return result


def _fetch_file_content(owner, repo, path):
    """读取单个文件内容"""
    try:
        url = f"https://api.github.com/repos/{owner}/{repo}/contents/{path}"
        resp = requests.get(url, headers=HEADERS, timeout=15)
        time.sleep(0.5)
        if resp.status_code == 200:
            data = resp.json()
            content = data.get("content", "")
            if content:
                return base64.b64decode(content).decode("utf-8", errors="ignore")
    except Exception:
        pass
    return None


def extract_code_patterns(code_text):
    """从源码中提取有价值的模式"""
    findings = []
    for pattern, label in CODE_PATTERNS:
        matches = re.findall(pattern, code_text, re.IGNORECASE)
        for m in matches[:3]:
            m_str = m if isinstance(m, str) else str(m)
            findings.append({"label": label, "match": m_str[:60]})
    return findings


def extract_knowledge(readme, repo_info, file_tree=None):
    kb = load_kb()
    readme_lower = readme.lower()
    repo_name = repo_info.get("full_name", "")
    repo_url = repo_info.get("html_url", "")
    stars = repo_info.get("stargazers_count", 0)
    source = {"repo": repo_name, "url": repo_url, "stars": stars}

    new_counts = {"tower": 0, "generic": 0, "dir": 0, "code": 0}

    # 塔防专项提取
    for pattern, title, content in TOWER_PATTERNS:
        if re.search(pattern, readme_lower, re.IGNORECASE):
            pid = normalize_id(title)
            if pid not in kb["tower_patterns"]:
                kb["tower_patterns"][pid] = {"title": title, "content": content, "sources": []}
            if source not in kb["tower_patterns"][pid]["sources"]:
                kb["tower_patterns"][pid]["sources"].append(source)
                new_counts["tower"] += 1

    # 通用模式提取
    for pattern, title, content in GENERIC_PATTERNS:
        if re.search(pattern, readme_lower, re.IGNORECASE):
            pid = normalize_id(title)
            if pid not in kb["generic_patterns"]:
                kb["generic_patterns"][pid] = {"title": title, "content": content, "sources": []}
            if source not in kb["generic_patterns"][pid]["sources"]:
                kb["generic_patterns"][pid]["sources"].append(source)
                new_counts["generic"] += 1

    # 文件树模式提取
    if file_tree and file_tree.get("dirs"):
        for d in file_tree["dirs"]:
            pid = normalize_id(d["label"])
            if pid not in kb["dir_patterns"]:
                kb["dir_patterns"][pid] = {"label": d["label"], "sources": []}
            src = {"repo": repo_name, "path": d["name"], "stars": stars}
            if src not in kb["dir_patterns"][pid]["sources"]:
                kb["dir_patterns"][pid]["sources"].append(src)
                new_counts["dir"] += 1

        kb["file_trees"][repo_name] = {
            "dirs": file_tree.get("dirs", []),
            "key_files": file_tree.get("key_files", [])[:15],
            "stars": stars,
            "date": datetime.now().strftime("%Y-%m-%d"),
        }

    # v3: 源码片段提取
    if file_tree and file_tree.get("code_extracts"):
        snippets = []
        for ext in file_tree["code_extracts"]:
            patterns = extract_code_patterns(ext["content"])
            if patterns:
                snippets.append({
                    "path": ext["path"],
                    "patterns": patterns,
                })
        if snippets:
            kb["code_snippets"][repo_name] = {
                "snippets": snippets,
                "stars": stars,
                "date": datetime.now().strftime("%Y-%m-%d"),
            }
            new_counts["code"] = len(snippets)

    # 洞察提取
    insight_keywords = ["tip", "best practice", "recommend", "avoid", "gotcha", "lesson", "insight"]
    for kw in insight_keywords:
        if re.search(r'\b' + kw + r'\b', readme_lower):
            sentences = re.findall(r'[^.!?\n]{30,200}[.!?]', readme)
            for sent in sentences:
                if kw in sent.lower() and 'http' not in sent.lower() and '```' not in sent:
                    key = sent.lower()[:80]
                    if not any(i['text'][:80] == key for i in kb["insights"][-20:]):
                        kb["insights"].append({
                            "text": sent.strip()[:200],
                            "repo": repo_name,
                            "date": datetime.now().strftime("%Y-%m-%d")
                        })
                        break

    if len(kb["insights"]) > 60:
        kb["insights"] = kb["insights"][-60:]

    if repo_name not in kb["repos"]:
        kb["repos"].append(repo_name)

    save_kb(kb)
    return new_counts


def explore(deep=False):
    history = load_history()

    queries = [
        "tower defense stars:>10",
        "unity ecs stars:>15",
        "gameplay ability system stars:>10",
        "roguelike tower defense stars:>5",
        "unity dots entity component stars:>10",
        "tower defense pathfinding stars:>10",
        "unity ability system gas stars:>5",
        "tower upgrade game stars:>5",
        # v3 新增搜索方向
        "tower defense game mechanic stars:>5",
        "unity tower defense gameplay ability stars:>5",
        "csharp ecs tower defense stars:>5",
    ]

    selected = random.sample(queries, min(5, len(queries)))
    all_repos = []
    for q in selected:
        repos = search_repos(q)
        if repos:
            all_repos.extend(repos)
            print(f"  [{q[:40]}] → {len(repos)} repos")
        else:
            print(f"  [{q[:40]}] → 无结果")

    seen = set(history)
    new_repos = []
    for r in all_repos:
        name = r.get("full_name", "")
        if name and name not in seen:
            seen.add(name)
            new_repos.append(r)

    random.shuffle(new_repos)
    new_repos = new_repos[:10]  # v3: 8 → 10

    history.extend([r.get("full_name") for r in new_repos])
    save_history(history)

    results = []
    for r in new_repos:
        name = r.get("full_name", "")
        owner, repo = name.split("/") if "/" in name else (name, name)
        readme = get_readme(owner, repo)
        file_tree = None

        if deep:
            print(f"  🌲 深度抓取 {name}...")
            file_tree = get_file_tree(owner, repo)

        counts = extract_knowledge(readme, r, file_tree)
        results.append({
            "repo": name,
            "stars": r.get("stargazers_count", 0),
            "url": r.get("html_url", ""),
            "description": (r.get("description") or "")[:100],
            "new": counts,
            "deep": deep,
        })
        code_str = f"/+{counts.get('code',0)}源码" if counts.get('code', 0) > 0 else ""
        print(f"  ✓ {name} ({r.get('stargazers_count', 0)}⭐) [+{counts['tower']}塔防/+{counts['generic']}通用/+{counts['dir']}架构{code_str}]")

    kb = load_kb()
    return {
        "timestamp": datetime.now().strftime("%Y-%m-%d %H:%M"),
        "repos_found": len(new_repos),
        "tower_patterns_count": len(kb["tower_patterns"]),
        "generic_patterns_count": len(kb["generic_patterns"]),
        "dir_patterns_count": len(kb["dir_patterns"]),
        "insights_count": len(kb["insights"]),
        "code_snippets_count": len(kb.get("code_snippets", {})),
        "results": results,
    }


def generate_doc():
    kb = load_kb()
    lines = [
        "# 塔防游戏 ECS + GAS 知识库",
        f"> 自动生成 · {datetime.now().strftime('%Y-%m-%d %H:%M')} · v3",
        "",
        f"已分析 {len(kb['repos'])} 个仓库",
        "",
    ]

    # ═══ 塔防专项 ═══
    if kb["tower_patterns"]:
        lines.extend(["## 塔防专项模式", ""])
        for pid, p in sorted(kb["tower_patterns"].items(), key=lambda x: len(x[1]["sources"]), reverse=True):
            src = ", ".join([f"[{s['repo']}](https://github.com/{s['repo']})" for s in p["sources"][:3]])
            lines.append(f"### {p['title']}")
            lines.append(f"> {p['content']}")
            if src:
                lines.append(f"来源：{src}")
            lines.append("")

    # ═══ 源码片段（v3 新增） ═══
    code_snippets = kb.get("code_snippets", {})
    if code_snippets:
        lines.extend(["## 源码结构参考", ""])
        for repo_name, data in sorted(code_snippets.items(), key=lambda x: x[1].get("stars", 0), reverse=True):
            lines.append(f"### {repo_name} ({data.get('stars', 0)}⭐)")
            for snip in data.get("snippets", [])[:5]:
                path = snip["path"]
                pats = ", ".join([f"`{p['match']}`" for p in snip["patterns"][:5]])
                if pats:
                    lines.append(f"- `{path}` — {pats}")
            lines.append("")

    # ═══ 项目架构 ═══
    if kb["dir_patterns"]:
        lines.extend(["## 项目架构线索", ""])
        for pid, p in sorted(kb["dir_patterns"].items(), key=lambda x: len(x[1]["sources"]), reverse=True):
            src = ", ".join([f"[{s['repo']}](https://github.com/{s['repo']})" for s in p["sources"][:3]])
            lines.append(f"### {p['label']}")
            if src:
                lines.append(f"来源：{src}")
            lines.append("")

    # ═══ 通用工程 ═══
    if kb["generic_patterns"]:
        lines.extend(["## 通用工程模式", ""])
        for pid, p in sorted(kb["generic_patterns"].items(), key=lambda x: len(x[1]["sources"]), reverse=True):
            src = ", ".join([f"[{s['repo']}](https://github.com/{s['repo']})" for s in p["sources"][:3]])
            lines.append(f"### {p['title']}")
            lines.append(f"> {p['content']}")
            if src:
                lines.append(f"来源：{src}")
            lines.append("")

    # ═══ 洞察 ═══
    if kb["insights"]:
        lines.extend(["## 实践洞察", ""])
        for ins in kb["insights"][-15:]:
            lines.append(f"- \"{ins['text'][:180]}\" — [{ins['repo']}](https://github.com/{ins['repo']}) ({ins['date']})")
        lines.append("")

    doc_path = os.path.join(OUTPUT_DIR, "tower_defense_knowledge.md")
    with open(doc_path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    # 同步到 BattleSystem-ECS/Research/
    dest = "/mnt/f/AI/BattleSystem-ECS/Research/tower_defense_knowledge.md"
    import shutil
    shutil.copy(doc_path, dest)
    print(f"  ✅ 已同步到：{dest}")

    return doc_path


def main():
    deep = "--deep" in sys.argv

    print(f"🔍 塔防 + ECS + GAS 专项探索 v3... {'[深度模式 🌲]' if deep else '[轻量模式]'}")
    result = explore(deep=deep)
    code_count = result.get("code_snippets_count", 0)
    code_str = f" + {code_count} 源码分析" if code_count > 0 else ""
    print(f"\n📊 本次 +{result['repos_found']} repos | 知识库 {result['tower_patterns_count']} 专项 + {result['generic_patterns_count']} 通用 + {result['dir_patterns_count']} 架构{code_str}")
    doc_path = generate_doc()
    print(f"✅ 文档：{doc_path}")
    return doc_path


if __name__ == "__main__":
    main()
