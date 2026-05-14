$towers = @()

# Energy category (40 towers)
$energyTypes = @("Pulse", "Tesla", "Plasma", "Ion", "Fusion", "Quantum", "Singularity", "Void", "Nebula", "Photon",
                 "Arc", "Ember", "Flux", "Surge", "Storm", "Blast", "Bolt", "Shock", "Aura", "Core",
                 "Nova", "Spark", "Ray", "Beam", "Wave", "Pulse", "Vector", "Matrix", "Reactor", "Capacitor",
                 "Dynamo", "Node", "Grid", "Array", "Module", "Unit", "Station", "Hub", "Nexus", "Prime")
$energyPrefix = @("Energy", "Cyber", "Electro", "Quantum", "Plasma", "Photon", "Ion", "Void", "Nebula", "Arc")

for ($i = 0; $i -lt 40; $i++) {
    $baseName = $energyTypes[$i % $energyTypes.Length]
    $prefix = $energyPrefix[$i / $energyTypes.Length % $energyPrefix.Length]
    $name = if ($i -lt 10) { "$prefix $baseName" } else { "$baseName-$($i+1)" }
    $dmg = 8 + ($i * 4)
    $range = 2 + ($i % 4)
    $speed = [Math]::Round(0.5 + ($i % 10) * 0.15, 2)
    $cost = 30 + ($i * 45)
    $upg = [int]($cost * 0.6)
    $towers += @{
        Name = $name
        Type = "Energy"
        Damage = $dmg
        Range = $range
        AttackSpeed = $speed
        Cost = $cost
        UpgradeCost = $upg
    }
}

# Weapon category (40 towers)
$weaponTypes = @("Cannon", "Railgun", "Laser", "Missile", "Swarm", "Nano", "Acid", "Cryo", "Fire", "Shock",
                 "Launcher", "Cannon", "Repeater", "Splitter", "Needler", "Repeater", "Gatling", "Cannon", "Turret", "Pod",
                 "Battery", "Cannon", "Rail", "Gun", "Discharger", "Thrower", "Cannon", "Cluster", "Rail", "Auto")
$weaponPrefix = @("Heavy", "Rapid", "Sniper", "Devastator", "Siege", "Assault", "Overwatch", "Siege", "Heavy", "Devastator")

for ($i = 0; $i -lt 40; $i++) {
    $baseName = $weaponTypes[$i % $weaponTypes.Length]
    $prefix = $weaponPrefix[$i / $weaponTypes.Length % $weaponPrefix.Length]
    $name = if ($i -lt 10) { "$prefix $baseName" } else { "$baseName-$($i+1)" }
    $dmg = 10 + ($i * 5)
    $range = 2 + ($i % 5)
    $speed = [Math]::Round(0.4 + ($i % 12) * 0.12, 2)
    $cost = 40 + ($i * 48)
    $upg = [int]($cost * 0.6)
    $towers += @{
        Name = $name
        Type = "Weapon"
        Damage = $dmg
        Range = $range
        AttackSpeed = $speed
        Cost = $cost
        UpgradeCost = $upg
    }
}

# Defense category (35 towers)
$defenseTypes = @("Bastion", "Shield", "Wall", "Barricade", "Drone", "Repair", "Decoy", "Hologram", "Phase", "Guardian",
                   "Fortress", "Bunker", "Citadel", "Rampart", "Tower", "Sentry", "Watchtower", "Outpost", "Fort", "Hold",
                   "Aegis", "Barrier", "Wall", "Bulwark", "Blockade", "Keep", "Hold", "Defense", "Fort", "Rampart",
                   "Plating", "Alloy", "Titan", "Armored", "Hardened")
$defensePrefix = @("Fortified", "Reinforced", "Hardened", "Armored", "Plated", "Shielded", "Guardian", "Defender", "Protector", "Keeper")

for ($i = 0; $i -lt 35; $i++) {
    $baseName = $defenseTypes[$i % $defenseTypes.Length]
    $prefix = $defensePrefix[$i / $defenseTypes.Length % $defensePrefix.Length]
    $name = if ($i -lt 10) { "$prefix $baseName" } else { "$baseName-$($i+1)" }
    $dmg = 5 + ($i * 3)
    $range = 2 + ($i % 3)
    $speed = [Math]::Round(0.6 + ($i % 8) * 0.1, 2)
    $cost = 35 + ($i * 40)
    $upg = [int]($cost * 0.6)
    $towers += @{
        Name = $name
        Type = "Defense"
        Damage = $dmg
        Range = $range
        AttackSpeed = $speed
        Cost = $cost
        UpgradeCost = $upg
    }
}

# Special category (35 towers)
$specialTypes = @("Gravity", "EMP", "Blackout", "Warp", "Hack", "Virus", "Leech", "Drain", "Overclock", "Doom",
                  "Singularity", "Null", "Pandemonium", "Chaos", "Abyss", "Rift", "Breach", "Glitch", "Corrupt", "Infect",
                  "Nano", "Swarm", "Acid", "Plague", "Toxic", "Corrosive", "Venom", "Poison", "Burn", "Freeze",
                  "Paradox", "Anomaly", "Distortion", "Fractal", "Echo")
$specialPrefix = @("Cyber", "Viral", "Neural", "Data", "Binary", "Quantum", "Dark", "Void", "Abyss", "Doom")

for ($i = 0; $i -lt 35; $i++) {
    $baseName = $specialTypes[$i % $specialTypes.Length]
    $prefix = $specialPrefix[$i / $specialTypes.Length % $specialPrefix.Length]
    $name = if ($i -lt 10) { "$prefix $baseName" } else { "$baseName-$($i+1)" }
    $dmg = 12 + ($i * 5)
    $range = 3 + ($i % 5)
    $speed = [Math]::Round(0.3 + ($i % 10) * 0.1, 2)
    $cost = 50 + ($i * 50)
    $upg = [int]($cost * 0.6)
    $towers += @{
        Name = $name
        Type = "Special"
        Damage = $dmg
        Range = $range
        AttackSpeed = $speed
        Cost = $cost
        UpgradeCost = $upg
    }
}

$towers | ConvertTo-Json -Depth 10 | Out-File -FilePath "F:\AI\BattleSystem-ECS\Towers\all_towers.json" -Encoding UTF8
Write-Host "Generated $($towers.Count) towers"