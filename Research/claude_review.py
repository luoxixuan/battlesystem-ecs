import subprocess
import os

os.chdir("/mnt/f/AI/BattleSystem-ECS")

prompt = """Review the full BattleSystem-ECS .NET project at /mnt/f/AI/BattleSystem-ECS. Output a detailed bug report in Markdown to /mnt/f/AI/BattleSystem-ECS/Research/bug-report-0515.md. Focus on: 1) ECS architecture and component store integrity, 2) Parallel safety (two phase pattern, damage queue, death resolution), 3) Bug risks (array bounds, null refs, race conditions), 4) Game logic correctness (tower attack, enemy AI, skill system, wave spawning, gold upgrade), 5) Configuration loading correctness (game_config.json parsing), 6) Memory safety in SOA component arrays. Every real bug: file path, line number if possible, description, severity (Critical or High or Medium or Low). Be thorough."""

env = os.environ.copy()
env["CLAUDE_CODE_SIMPLE"] = "1"

proc = subprocess.Popen(
    ["/root/.npm-global/bin/claude-code", "review", "/mnt/f/AI/BattleSystem-ECS", "--output-format", "text"],
    stdin=subprocess.DEVNULL,
    stdout=subprocess.PIPE,
    stderr=subprocess.STDOUT,
    env=env
)

for line in proc.stdout:
    print(line.decode("utf-8", errors="replace"), end="")

proc.wait()
print(f"\nExit code: {proc.returncode}")