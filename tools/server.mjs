// GPU_PC_MGT · 极简静态服务器（只监听 127.0.0.1，不需要管理员权限）
// 用法: node tools/server.mjs [port]
import http from 'node:http';
import { readFile, realpath } from 'node:fs/promises';
import { extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = resolve(fileURLToPath(new URL('.', import.meta.url)));
const webRoot = resolve(here, '..', 'web');
const port = Number(process.argv[2] || process.env.PORT || 8791);

const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
};

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, 'http://127.0.0.1');
    let rel = decodeURIComponent(url.pathname);
    if (rel === '/' || rel === '') rel = '/index.html';
    if (!['/index.html', '/data.json'].includes(rel)) {
      res.writeHead(403).end('forbidden');
      return;
    }
    const target = join(webRoot, rel.slice(1));
    const actual = await realpath(target);
    const expected = join(await realpath(webRoot), rel.slice(1));
    const same = process.platform === 'win32' ? actual.toLowerCase() === expected.toLowerCase() : actual === expected;
    if (!same) {
      res.writeHead(403).end('forbidden');
      return;
    }
    const body = await readFile(target);
    const ext = extname(target).toLowerCase();
    res.writeHead(200, {
      'content-type': types[ext] || 'application/octet-stream',
      'cache-control': 'no-store, must-revalidate',
      'x-content-type-options': 'nosniff',
      'referrer-policy': 'no-referrer',
      'content-security-policy': "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'",
    });
    res.end(body);
  } catch (err) {
    const code = err && err.code === 'ENOENT' ? 404 : 500;
    res.writeHead(code, { 'content-type': 'text/plain; charset=utf-8', 'cache-control': 'no-store' });
    res.end(code === 404 ? 'not found' : 'request failed');
  }
});

server.listen(port, '127.0.0.1', () => {
  console.log(`[server] http://127.0.0.1:${server.address().port}/`);
});
