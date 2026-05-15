import subprocess

prompt = """Review the full BattleSystem-ECS .NET project at /mnt/f/AI/BattleSystem-ECS. Focus on: 1) ECS architecture and component store integrity, 2) Parallel safety (two phase pattern, damage queue, death resolution), 3) Bug risks (array bounds, null refs, race conditions), 4) Game logic correctness (tower attack, enemy AI, skill system, wave spawning, gold upgrade), 5) Configuration loading correctness (game_config.json parsing), 6) Memory safety in SOA component arrays. Output a detailed bug report in Markdown to /mnt/f/AI/BattleSystem-ECS/Research/bug-report-0515.md. Every real bug: file path, line number if possible, description, severity (Critical or High or Medium or Low). Be thorough."""

result = subprocess.run(
    ["/root/.npm-global/bin/claude-code", "-p", prompt, "--add-dir", "/mnt/f/AI/BattleSystem-ECS"],
    capture_output=True, text=True, timeout=300
)
print("STDOUT:", result.stdout[:5000])
print("STDERR:", result.stderr[:2000])
print("RC:", result.returncode)