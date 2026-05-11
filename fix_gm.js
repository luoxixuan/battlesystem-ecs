const { spawn } = require('child_process');
const fs = require('fs');

const git = spawn('git', ['-C', 'F:\\AI\\BattleSystem-ECS', 'cat-file', '-p', 'HEAD:Core/GameManager.cs']);

const chunks = [];
git.stdout.on('data', chunk => chunks.push(chunk));

git.on('close', () => {
    const buf = Buffer.concat(chunks);
    // Prepend UTF-8 BOM
    const bom = Buffer.from([0xEF, 0xBB, 0xBF]);
    const withBom = Buffer.concat([bom, buf]);
    fs.writeFileSync('F:\\AI\\BattleSystem-ECS\\Core\\GameManager.cs', withBom);
    const r = fs.readFileSync('F:\\AI\\BattleSystem-ECS\\Core\\GameManager.cs');
    console.log('Written', withBom.length, 'bytes, BOM:', r.slice(0, 4).toString('hex'));
});