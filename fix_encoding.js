const fs = require('fs');
const { execSync } = require('child_process');
const path = 'F:\\AI\\BattleSystem-ECS\\Core\\GameManager.cs';
const b = execSync('git -C "F:\\AI\\BattleSystem-ECS" cat-file -p HEAD:Core/GameManager.cs');
fs.writeFileSync(path, b);
const r = fs.readFileSync(path);
console.log('BOM:', r.slice(0, 4).toString('hex'), 'len:', r.length);