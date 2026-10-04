// 临时自检：语法检查 index.html 内联脚本 + 用真实 data.json 跑一遍页面数据模型
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const html = await readFile(new URL('../web/index.html', import.meta.url), 'utf8');
const m = html.match(/<script>([\s\S]*?)<\/script>/);
if (!m) throw new Error('index.html 里找不到 <script> 块');
const code = m[1];

new vm.Script(code);
console.log('✓ index.html 内联脚本语法检查通过');

const pure = code.split('/* ---------- DOM ---------- */')[0];
if (pure === code) throw new Error('找不到 DOM 分隔标记');
const ctx = { console, window: { devicePixelRatio: 1 } };
vm.createContext(ctx);
new vm.Script(pure + '\nglobalThis.buildModel = buildModel;').runInContext(ctx);

const payload = JSON.parse(await readFile(new URL('../web/data.json', import.meta.url), 'utf8'));
console.log(`✓ data.json 解析通过  generated=${payload.generated}  collectMs=${payload.collectMs}`);
for (const t of payload.targets) {
  if (!t.ok) { console.log(`  [${t.name}] 采集失败: ${t.error}`); continue; }
  const v = ctx.buildModel(t.data, t.info || {});
  console.log(`  [${t.name}] channel=${t.channel} collectMs=${t.collectMs} 计数器=${t.data.counters.length}`);
  console.log('   ', JSON.stringify({
    host: v.host, os: v.osCaption, cpuName: v.cpuName,
    cpuPct: +v.cpuPct.toFixed(1), cpuGHz: v.cpuMhz ? +(v.cpuMhz / 1000).toFixed(2) : null,
    cores: v.cores.length, ramGB: v.ramTotalBytes ? +(v.ramTotalBytes / 1073741824).toFixed(1) : null,
    memPct: v.memPct == null ? null : +v.memPct.toFixed(1),
    memUsedGB: v.memUsedBytes ? +(v.memUsedBytes / 1073741824).toFixed(1) : null,
    diskActive: +v.diskActive.toFixed(1), diskR: Math.round(v.diskReadBps || 0), diskW: Math.round(v.diskWriteBps || 0),
    netRxKBs: Math.round((v.netRxBps || 0) / 1024), netAdapter: v.netAdapter,
    gpuPct: +v.gpuPct.toFixed(1), engines: v.engines, hasGpu: v.hasGpu,
    vramUsedMB: v.vramUsedBytes ? +(v.vramUsedBytes / 1048576).toFixed(0) : null,
    sensorSource: v.sensors.source, temp: v.sensors.temp, power: v.sensors.power, fan: v.sensors.fan,
    volumes: v.volumes.map(x => x.drive + ':' + (x.sizeBytes ? (x.sizeBytes / 1073741824).toFixed(0) : '?') + 'GB'),
    gpuNames: v.gpuNames
  }));
}
