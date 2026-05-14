$ErrorActionPreference = "Stop"
$outDir = "F:\AI\BattleSystem-ECS"
$sw = [Diagnostics.Stopwatch]::StartNew()

$TowerTypes   = @("Pulse","Tesla","Plasma","Cryo","Acid","Nano","Stun","EMP","Gravity","Leech","Firewall","Repair","Hologram","Phase","Doom","Railgun","Ion","Cyber","Hacker","Shock","Neon","Chrome","Virus","Drone","Mech")
$TowerSuffixes=@("Cannon","Coil","Node","Sprayer","Swarm","Grid","Tower","Bastion","Drone","Decoy","Core","Lance","Storm","Beacon","Array","Turret","Emitter","Field","Matrix","Blaster")

$MonsterTypes = @("Normal","Fast","Strong","Swarm","Electric","Stealth","Healer","Tank","Clone","Assassin","Heavy","Support","Virus","Exo","Summoner","Cryo","Fire","Debuff","Boss","Rogue","Phantom","Glitch","Mech","Drone","Titan")
$MonsterPrefixes=@("Chrome","Neon","Cyber","Data","Nano","Plasma","Shock","Stealth","Mirror","Fire","Cryo","Dark","Quantum","Void","Flux","Rogue","Hyper","Omega","Alpha","Beta","Delta","Sigma","Titan","Doom","Mega")

$SkillTypes   = @("Circuit","Plasma","Railgun","Ion","Cyber","Firewall","Phase","Overclock","System","Null","Data","Warp","Blackout","Neural","Doom","Shock","Neon","Chrome","Virus","Quantum")
$SkillEffects = @("Breaker","Surge","Shot","Storm","Worm","Burst","Dash","Strike","Crash","Zone","Leech","Gate","Protocol","Spike","Bomb","Wave","Nova","Pulse","Flux","Beam","Cascade","Surge","Blast","Field")

function Make-TowerJson($i) {
    $t = $TowerTypes[($i-1) % $TowerTypes.Length]
    $s = $TowerSuffixes[($i-1) % $TowerSuffixes.Length]
    $baseDps = 5 + (($i-1) % 196)
    $damage  = [int]($baseDps * (0.8 + (($i % 5) * 0.05)))
    $range   = 1 + (($i-1) % 15)
    $spd     = [math]::Round(0.5 + (($i % 10) * 0.15), 2)
    $cost    = 20 + (($i-1) % 1981)
    @{Name="$t $s #$i";Type=$t;Damage=$damage;Range=$range;AttackSpeed=$spd;Cost=$cost;UpgradeCost=[int]($cost*0.6)} | ConvertTo-Json -Compress
}

function Make-MonsterJson($i) {
    $t = $MonsterTypes[($i-1) % $MonsterTypes.Length]
    $p = $MonsterPrefixes[($i-1) % $MonsterPrefixes.Length]
    $hp  = 10 + (($i-1) % 49991)
    $spd = [math]::Round(0.2 + (($i % 48) * 0.1), 2)
    @{Name="$p $t #$i";Type=$t;Health=$hp;MaxHealth=$hp;Damage=[int](1+(($i-1)%49));MoveSpeed=$spd;AttackRange=1+(($i-1)%4);AttackInterval=[math]::Round(0.5+(($i%15)*0.2),2);GoldReward=5+(($i-1)%4996);Skills=@("Normal Attack")} | ConvertTo-Json -Compress
}

function Make-SkillJson($i) {
    $t = $SkillTypes[($i-1) % $SkillTypes.Length]
    $e = $SkillEffects[($i-1) % $SkillEffects.Length]
    $area = 1 + (($i-1) % 9)
    $hkIdx = ($i - 1) % 35
    $hotkey = if ($hkIdx -lt 9) { [string]($hkIdx + 1) } else { [char]([int][char]'A' + $hkIdx - 9) }
    @{Name="$t $e #$i";Description="${area}x${area} ${t} ${e}";DamageMultiplier=[math]::Round(0.2+(($i%78)*0.1),2);AreaWidth=$area;AreaHeight=$area;AttackRange=3+(($i-1)%12);Cooldown=[math]::Round(0.5+(($i%295)*0.1),1);AutoCast=$false;Hotkey=$hotkey} | ConvertTo-Json -Compress
}

# ==============================================================
# 1. Generate 150 tower files
# ==============================================================
Write-Host "Generating 150 towers..."
$towersDir = "$outDir\Towers"
$null = New-Item -ItemType Directory -Force -Path $towersDir | Out-Null

for ($i = 1; $i -le 150; $i++) {
    $path = "$towersDir\tower_$($i.ToString('000')).json"
    Make-TowerJson $i | Set-Content -Path $path -Encoding UTF8
    if ($i % 50 -eq 0) { Write-Host "  Towers $i/150" }
}

# ==============================================================
# 2. Generate 200 monster files
# ==============================================================
Write-Host "Generating 200 monsters..."
$monstersDir = "$outDir\Monsters"
$null = New-Item -ItemType Directory -Force -Path $monstersDir | Out-Null

for ($i = 1; $i -le 200; $i++) {
    $path = "$monstersDir\monster_$($i.ToString('000')).json"
    Make-MonsterJson $i | Set-Content -Path $path -Encoding UTF8
    if ($i % 50 -eq 0) { Write-Host "  Monsters $i/200" }
}

# ==============================================================
# 3. Generate 150 skill files
# ==============================================================
Write-Host "Generating 150 skills..."
$skillsDir = "$outDir\Skills"
$null = New-Item -ItemType Directory -Force -Path $skillsDir | Out-Null

for ($i = 1; $i -le 150; $i++) {
    $path = "$skillsDir\skill_$($i.ToString('000')).json"
    Make-SkillJson $i | Set-Content -Path $path -Encoding UTF8
    if ($i % 50 -eq 0) { Write-Host "  Skills $i/150" }
}

# ==============================================================
# 4. Rewrite game_config.json
# ==============================================================
Write-Host "Rewriting game_config.json..."

$allTowers = for ($i = 1; $i -le 150; $i++) {
    $t = $TowerTypes[($i-1) % $TowerTypes.Length]
    $s = $TowerSuffixes[($i-1) % $TowerSuffixes.Length]
    $baseDps = 5 + (($i-1) % 196)
    $damage  = [int]($baseDps * (0.8 + (($i % 5) * 0.05)))
    $range   = 1 + (($i-1) % 15)
    $spd     = [math]::Round(0.5 + (($i % 10) * 0.15), 2)
    $cost    = 20 + (($i-1) % 1981)
    @{Name="$t $s #$i";Type=$t;Damage=$damage;Range=$range;AttackSpeed=$spd;Cost=$cost;UpgradeCost=[int]($cost*0.6)}
}

$allMonsters = for ($i = 1; $i -le 200; $i++) {
    $t = $MonsterTypes[($i-1) % $MonsterTypes.Length]
    $p = $MonsterPrefixes[($i-1) % $MonsterPrefixes.Length]
    $hp  = 10 + (($i-1) % 49991)
    $spd = [math]::Round(0.2 + (($i % 48) * 0.1), 2)
    @{Name="$p $t #$i";Type=$t;Health=$hp;MaxHealth=$hp;Damage=[int](1+(($i-1)%49));MoveSpeed=$spd;AttackRange=1+(($i-1)%4);AttackInterval=[math]::Round(0.5+(($i%15)*0.2),2);GoldReward=5+(($i-1)%4996);Skills=@("Normal Attack")}
}

$allSkills = for ($i = 1; $i -le 150; $i++) {
    $t = $SkillTypes[($i-1) % $SkillTypes.Length]
    $e = $SkillEffects[($i-1) % $SkillEffects.Length]
    $area   = 1 + (($i-1) % 9)
    $hkIdx  = ($i - 1) % 35
    $hotkey = if ($hkIdx -lt 9) { [string]($hkIdx + 1) } else { [char]([int][char]'A' + $hkIdx - 9) }
    @{Name="$t $e #$i";Description="${area}x${area} ${t} ${e}";DamageMultiplier=[math]::Round(0.2+(($i%78)*0.1),2);AreaWidth=$area;AreaHeight=$area;AttackRange=3+(($i-1)%12);Cooldown=[math]::Round(0.5+(($i%295)*0.1),1);AutoCast=$false;Hotkey=$hotkey}
}

$levelNames = @("Neon District","Data Center Siege","Blackout Protocol","Elemental Chaos","Corporate Warfare")
$levelDescs = @("Glitch Runners flood the streets.","Nano Swarms overwhelm.","Stealth Infiltrators slip past.","Temperature warfare.","The final stand.")

$levels = for ($l = 1; $l -le 5; $l++) {
    $waves = for ($w = 1; $w -le 10; $w++) {
        @{WaveNumber=$w;MonsterType=$MonsterTypes[($w-1)%$MonsterTypes.Length];EnemyCount=30}
    }
    @{LevelNumber=$l;Name=$levelNames[$l-1];Description=$levelDescs[$l-1];WaveCount=10;Waves=$waves}
}

$startingSkills = for ($k = 1; $k -le 15; $k++) {
    "$($SkillTypes[($k-1)%$SkillTypes.Length]) $($SkillEffects[($k-1)%$SkillEffects.Length])"
}

$config = @{
    Player = @{
        Name="Player";Type="Tower";AttackRange=3;AttackInterval=1;AttackDamage=10;MaxHealth=200;CurrentLevel=1;UpgradeThreshold=1000;
        StartingSkills = $startingSkills
    }
    Towers = $allTowers
    Skills = $allSkills
    MonsterTypes = $allMonsters
    Levels = $levels
}

$json = $config | ConvertTo-Json -Depth 20
Set-Content -Path "$outDir\game_config.json" -Value $json -Encoding UTF8

$tCount = (Get-ChildItem "$towersDir\tower_*.json").Count
$mCount = (Get-ChildItem "$monstersDir\monster_*.json").Count
$sCount = (Get-ChildItem "$skillsDir\skill_*.json").Count
Write-Host "Done! Towers=$tCount Monsters=$mCount Skills=$sCount"
$sw.Stop()
Write-Host "Total time: $($sw.ElapsedMilliseconds)ms"