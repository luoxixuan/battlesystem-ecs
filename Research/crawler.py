"""
GitHub Tower Defense Project Crawler
=====================================
Crawls GitHub for high-star tower defense projects, deduplicates,
extracts architecture patterns, and generates analysis reports.

Usage:
    python crawler.py              # Crawl once
    python crawler.py --analyze    # Analyze existing crawled data
"""
import json
import os
import sys
import time
import urllib.request
import urllib.error
from datetime import datetime
from pathlib import Path

# ── Config ─────────────────────────────────────────────────
SCRIPT_DIR = Path(__file__).parent
CRAWLED_FILE = SCRIPT_DIR / "crawled.json"
FINDINGS_DIR = SCRIPT_DIR / "findings"
REQUEST_DELAY = 2.0  # seconds between API calls (respect rate limits)

# Search queries — broad + language-specific
QUERIES = [
    "tower+defense+game+stars:>30",
    "tower+defense+game+stars:>10+language:C%23",
    "tower+defense+game+stars:>10+language:Python",
    "tower+defense+ECS+game",
    "tower+defense+roguelike+game",
    "tower+defense+game+framework+architecture",
]

# Architecture patterns to look for in README/files
ARCH_PATTERNS = [
    "ECS", "Entity Component", "entity-component",
    "MVC", "Model View Controller",
    "state machine", "FSM", "finite state",
    "factory pattern", "abstract factory",
    "observer pattern", "event system", "pub sub", "event bus",
    "object pool", "pooling",
    "command pattern",
    "strategy pattern",
    "component based", "component-based",
    "data oriented", "data-driven",
    "Dependency Injection", "IoC", "DI container",
    "modular", "plugin system",
    "wave system", "wave manager",
    "tower placement", "grid system",
    "upgrade tree", "tech tree", "skill tree",
    "buff system", "debuff system",
    "pathfinding", "A*", "Dijkstra",
    "config driven", "data driven", "JSON config",
    "ScriptableObject", "SO",
]


def load_crawled():
    """Load the dedup registry."""
    if CRAWLED_FILE.exists():
        with open(CRAWLED_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    return {"repos": {}, "last_crawl": None, "total_crawled": 0}


def save_crawled(registry):
    """Save the dedup registry."""
    registry["last_crawl"] = datetime.now().isoformat()
    with open(CRAWLED_FILE, "w", encoding="utf-8") as f:
        json.dump(registry, f, indent=2, ensure_ascii=False)


def github_api(url):
    """Call GitHub API with rate-limit awareness."""
    req = urllib.request.Request(url)
    req.add_header("Accept", "application/vnd.github.v3+json")
    req.add_header("User-Agent", "TD-Research-Crawler/1.0")

    # Check for GitHub token in env
    token = os.environ.get("GITHUB_TOKEN", "")
    if token:
        req.add_header("Authorization", f"Bearer {token}")

    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            remaining = int(resp.headers.get("X-RateLimit-Remaining", 0))
            reset_time = int(resp.headers.get("X-RateLimit-Reset", 0))
            print(f"  [API] Rate limit: {remaining} remaining")

            # If rate limit is low, warn
            if remaining < 5:
                wait = max(reset_time - int(time.time()), 0) + 5
                print(f"  [API] Rate limit nearly exhausted. Would need to wait {wait}s.")
                print(f"  [API] Tip: Set GITHUB_TOKEN env var for 5000 req/hour.")

            return json.loads(resp.read().decode())
    except urllib.error.HTTPError as e:
        if e.code == 403:
            # Check if it's rate limiting
            remaining = int(e.headers.get("X-RateLimit-Remaining", -1))
            if remaining == 0:
                reset_time = int(e.headers.get("X-RateLimit-Reset", 0))
                wait = max(reset_time - int(time.time()), 60) + 5
                print(f"  [API] Rate limited! Reset in {wait}s. Stopping crawl.")
                return None
            print(f"  [API] HTTP 403: {e.reason}")
            return None
        elif e.code == 404:
            return None
        print(f"  [API] HTTP Error {e.code}: {e.reason}")
        return None
    except Exception as e:
        print(f"  [API] Error: {e}")
        return None


def search_repos(query, page=1, per_page=30):
    """Search GitHub repositories."""
    url = f"https://api.github.com/search/repositories?q={query}&sort=stars&order=desc&page={page}&per_page={per_page}"
    return github_api(url)


def fetch_readme(full_name):
    """Fetch README content (as raw text)."""
    # Try to get README via contents API
    url = f"https://api.github.com/repos/{full_name}/readme"
    data = github_api(url)
    if not data:
        return None

    download_url = data.get("download_url", "")
    if not download_url:
        return None

    try:
        req = urllib.request.Request(download_url)
        req.add_header("User-Agent", "TD-Research-Crawler/1.0")
        with urllib.request.urlopen(req, timeout=15) as resp:
            return resp.read().decode("utf-8", errors="replace")
    except Exception:
        return None


def fetch_file_tree(full_name):
    """Fetch top-level directory structure."""
    url = f"https://api.github.com/repos/{full_name}/contents/"
    data = github_api(url)
    if not data:
        return []
    return [item["name"] for item in data if isinstance(item, dict)]


def analyze_readme(readme_text):
    """Extract architecture patterns from README."""
    if not readme_text:
        return []

    text_lower = readme_text.lower()
    found = []
    for pattern in ARCH_PATTERNS:
        if pattern.lower() in text_lower:
            found.append(pattern)
    return found


def crawl(light_mode=False):
    """Main crawl function."""
    registry = load_crawled()
    print(f"=== Tower Defense Research Crawler ===")
    print(f"Mode: {'Light (search only)' if light_mode else 'Full (with deep dive)'}")
    print(f"Already tracked: {registry['total_crawled']} repos")
    print()

    new_repos = []
    session_id = datetime.now().strftime("%Y%m%d_%H%M%S")
    stopped_early = False

    for query in QUERIES:
        if stopped_early:
            break
        print(f"[Query] {query}")
        for page in range(1, 3):  # Max 2 pages per query (60 results)
            result = search_repos(query, page=page)
            if not result:
                stopped_early = True
                break
            if "items" not in result:
                break

            for item in result["items"]:
                repo_id = str(item["id"])
                full_name = item["full_name"]

                # Skip if already crawled
                if repo_id in registry["repos"]:
                    continue

                print(f"  [+] New: {full_name} ({item['stargazers_count']} stars, {item['language']})")

                # Deep dive: fetch README + file tree (skip in light mode)
                if not light_mode:
                    time.sleep(REQUEST_DELAY)
                    readme = fetch_readme(full_name)

                    time.sleep(REQUEST_DELAY)
                    file_tree = fetch_file_tree(full_name)

                    patterns = analyze_readme(readme)
                else:
                    readme = None
                    file_tree = []
                    patterns = []

                repo_data = {
                    "id": repo_id,
                    "full_name": full_name,
                    "html_url": item["html_url"],
                    "description": item.get("description", ""),
                    "stars": item["stargazers_count"],
                    "language": item.get("language", ""),
                    "topics": item.get("topics", []),
                    "created_at": item.get("created_at", ""),
                    "updated_at": item.get("updated_at", ""),
                    "license": item.get("license", {}).get("spdx_id", "") if item.get("license") else "",
                    "file_tree": file_tree,
                    "architecture_patterns": patterns,
                    "crawled_at": datetime.now().isoformat(),
                }

                registry["repos"][repo_id] = repo_data
                new_repos.append(repo_data)
                registry["total_crawled"] = len(registry["repos"])

                # Incremental save — prevent data loss on interrupt
                save_crawled(registry)

                time.sleep(REQUEST_DELAY)

            # Check if more pages exist
            if len(result.get("items", [])) < 30:
                break
            time.sleep(REQUEST_DELAY)

    # Report (use final registry state)
    registry = load_crawled()
    print(f"\n=== Crawl Complete ===")
    print(f"Total tracked: {registry['total_crawled']}")

    # Generate session report
    if new_repos:
        generate_report(new_repos, session_id)

    return new_repos


def generate_report(new_repos, session_id):
    """Generate a Markdown analysis report for this crawl session."""
    FINDINGS_DIR.mkdir(parents=True, exist_ok=True)
    report_path = FINDINGS_DIR / f"crawl_{session_id}.md"

    # Aggregate patterns
    all_patterns = {}
    for repo in new_repos:
        for p in repo.get("architecture_patterns", []):
            all_patterns[p] = all_patterns.get(p, 0) + 1

    # Sort by frequency
    sorted_patterns = sorted(all_patterns.items(), key=lambda x: -x[1])

    # Language stats
    lang_stats = {}
    for repo in new_repos:
        lang = repo.get("language", "Unknown")
        lang_stats[lang] = lang_stats.get(lang, 0) + 1

    with open(report_path, "w", encoding="utf-8") as f:
        f.write(f"# TD Research Report — {datetime.now().strftime('%Y-%m-%d %H:%M')}\n\n")
        f.write(f"**New repos**: {len(new_repos)} | **Total tracked**: {load_crawled()['total_crawled']}\n\n")

        f.write("## New Repositories\n\n")
        f.write("| # | Project | Stars | Language | Patterns |\n")
        f.write("|---|---------|-------|----------|----------|\n")
        for i, repo in enumerate(new_repos[:30], 1):
            patterns_str = ", ".join(repo.get("architecture_patterns", [])[:3])
            f.write(f"| {i} | [{repo['full_name']}]({repo['html_url']}) | {repo['stars']} | {repo['language']} | {patterns_str} |\n")

        f.write(f"\n## Architecture Pattern Frequency\n\n")
        for pattern, count in sorted_patterns:
            f.write(f"- **{pattern}**: {count} repos\n")

        f.write(f"\n## Language Distribution\n\n")
        for lang, count in sorted(lang_stats.items(), key=lambda x: -x[1]):
            f.write(f"- **{lang}**: {count} repos\n")

        f.write(f"\n## Top Projects (Deep Dive)\n\n")
        for repo in sorted(new_repos, key=lambda r: -r.get("stars", 0))[:5]:
            f.write(f"### [{repo['full_name']}]({repo['html_url']}) — {repo['stars']} ⭐\n\n")
            f.write(f"- **Language**: {repo['language']}\n")
            f.write(f"- **License**: {repo['license']}\n")
            f.write(f"- **Description**: {repo['description']}\n")
            file_tree = repo.get("file_tree", [])
            if file_tree:
                f.write(f"- **Top-level structure**: {', '.join(file_tree[:20])}\n")
            patterns = repo.get("architecture_patterns", [])
            if patterns:
                f.write(f"- **Patterns detected**: {', '.join(patterns)}\n")
            f.write("\n")

    print(f"Report saved: {report_path}")


def analyze_all():
    """Analyze all crawled repos for patterns and generate summary."""
    registry = load_crawled()
    repos = list(registry["repos"].values())

    if not repos:
        print("No crawled data yet. Run crawler first.")
        return

    # Aggregate all patterns
    pattern_freq = {}
    lang_freq = {}
    topic_freq = {}
    total_stars = 0

    for repo in repos:
        for p in repo.get("architecture_patterns", []):
            pattern_freq[p] = pattern_freq.get(p, 0) + 1
        lang = repo.get("language", "Unknown")
        lang_freq[lang] = lang_freq.get(lang, 0) + 1
        for t in repo.get("topics", []):
            topic_freq[t] = topic_freq.get(t, 0) + 1
        total_stars += repo.get("stars", 0)

    report_path = FINDINGS_DIR / f"summary_{datetime.now().strftime('%Y%m%d_%H%M%S')}.md"

    with open(report_path, "w", encoding="utf-8") as f:
        f.write(f"# TD Research Summary\n\n")
        f.write(f"**Total repos tracked**: {len(repos)} | **Total stars**: {total_stars:,}\n")
        f.write(f"**Last crawl**: {registry.get('last_crawl', 'Never')}\n\n")

        f.write("## Architecture Patterns (Global)\n\n")
        for p, c in sorted(pattern_freq.items(), key=lambda x: -x[1]):
            pct = c / len(repos) * 100
            f.write(f"- **{p}**: {c} ({pct:.0f}%)\n")

        f.write(f"\n## Language Distribution\n\n")
        for lang, c in sorted(lang_freq.items(), key=lambda x: -x[1]):
            f.write(f"- **{lang}**: {c}\n")

        f.write(f"\n## Common Topics\n\n")
        for t, c in sorted(topic_freq.items(), key=lambda x: -x[1])[:20]:
            f.write(f"- **{t}**: {c}\n")

        f.write(f"\n## Recommendations for BattleSystem-ECS\n\n")
        generate_recommendations(f, repos, pattern_freq)

    print(f"Summary saved: {report_path}")


def generate_recommendations(f, repos, pattern_freq):
    """Generate actionable recommendations based on crawled data."""
    # Find C# projects specifically
    csharp_repos = [r for r in repos if r.get("language") == "C#"]

    f.write("### Based on top C# tower defense projects:\n\n")

    # Common C# patterns
    csharp_patterns = {}
    for r in csharp_repos:
        for p in r.get("architecture_patterns", []):
            csharp_patterns[p] = csharp_patterns.get(p, 0) + 1

    for p, c in sorted(csharp_patterns.items(), key=lambda x: -x[1])[:5]:
        f.write(f"- **{p}** found in {c} C# projects\n")

    # Look for ECS specifically (most relevant to our architecture)
    ecs_repos = [r for r in repos if "ECS" in str(r.get("architecture_patterns", []))
                 or "Entity Component" in str(r.get("architecture_patterns", []))]

    if ecs_repos:
        f.write(f"\n### ECS-based projects ({len(ecs_repos)} found):\n\n")
        for r in ecs_repos[:5]:
            f.write(f"- [{r['full_name']}]({r['html_url']}) ({r['stars']} ⭐)\n")
    else:
        f.write(f"\n### Note: No dedicated ECS tower defense projects found in current dataset.\n")
        f.write(f"Consider studying general ECS frameworks (LeoECS, Entitas, Unity DOTS) for patterns.\n")

    # Generic recommendations
    f.write(f"\n### General Improvements to Consider:\n\n")
    f.write(f"1. **Config-driven architecture** — Most high-star projects separate game data from code (JSON/ScriptableObject). Already partially done.\n")
    f.write(f"2. **Event bus / Observer pattern** — Decouple systems with events (enemy killed → gold reward → UI update).\n")
    f.write(f"3. **Object pooling** — Reuse enemy/tower entities instead of constantly creating/destroying (performance for large waves).\n")
    f.write(f"4. **State machine for game phases** — BuildPhase → WavePhase → Intermission → NextLevel. Cleaner than boolean flags.\n")
    f.write(f"5. **Modular buff/debuff system** — Many top projects have composable effects (slow, poison, burn, stun) as independent components.\n")
    f.write(f"6. **Upgrade/tech tree** — Players love progression systems. Unlock tower types, upgrade paths, passive bonuses.\n")


if __name__ == "__main__":
    if "--analyze" in sys.argv:
        analyze_all()
    elif "--light" in sys.argv:
        crawl(light_mode=True)
    else:
        crawl()
