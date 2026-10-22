import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {spawn} from 'node:child_process';
import vm from 'node:vm';
const html=await readFile(new URL('../web/index.html',import.meta.url),'utf8');
const code=html.match(/<script>([\s\S]*?)<\/script>/)[1];
new vm.Script(code);
const helper=code.match(/function escapeHtml\(value\)\{.*\}/)[0];
const context=vm.createContext({});
vm.runInContext(helper,context);
assert.equal(vm.runInContext(`escapeHtml('<img src=x onerror="evil()">')`,context),'&lt;img src=x onerror=&quot;evil()&quot;&gt;');
console.log('PASS legacy web script syntax and escaping');
const child=spawn(process.execPath,[fileURLToPath(new URL('./server.mjs',import.meta.url)),'0'],{stdio:['ignore','pipe','pipe'],windowsHide:true});
try{
    const base=await new Promise((resolve,reject)=>{
        const timer=setTimeout(()=>reject(new Error('server start timeout')),5000);
        child.stdout.on('data',chunk=>{const match=String(chunk).match(/http:\/\/127\.0\.0\.1:\d+\//);if(match){clearTimeout(timer);resolve(match[0]);}});
        child.on('error',reject);
    });
    assert.equal((await fetch(base)).status,200);
    for(const path of ['config.json','../config.json','%2e%2e%5cconfig.json','index.html%00','index.html/anything']){
        assert.equal((await fetch(base+path)).status,403);
    }
    console.log('PASS static server exposes only two allowed files and rejects traversal');
}finally{child.kill();}
