# GPU_PC_MGT — Experimental / 实验性测试项目

个人 Windows GPU/电脑性能监控工具，采用 WinForms 和系统 .NET Framework。**实验性源码，不是生产监控、硬件保护或故障诊断保证。** 温度不可获取不等于正常，连接异常不能单独证明设备过热或重启。源码及仓库生成图标采用 [MIT](LICENSE) 许可。

## 功能

本机、局域网 TLS 探针及 SSH 采样；CPU、内存、磁盘、网络、GPU、显存及驱动可提供的温度。支持 Windows 原生托盘、可置顶贴边小部件、一次性异常提醒、故障前最多 5 分钟的表格和曲线。历史仅在内存保留，最多 10 条、24 小时，退出清除。

探针提供性能提醒及“最佳能效 / 平衡 / 最佳性能”，插电与电池分别设置。节能模式是独立开关；程序不会自动改变睡眠、休眠或网络设置。托盘提示使用系统原生分行文本，最多 127 字符，完整指标见主窗口。

## 环境与构建

已验证 Windows 11 x64 桌面，需要 .NET Framework 4.8、Windows PowerShell 5.1；SSH 功能另需 OpenSSH。未完整验证其他 Windows、GPU、驱动或远程桌面组合；不可用的电源 API 显示不可用，不伪造状态。没有稳定版、签名 EXE 或自动更新。

```bat
build.cmd
start-exe.cmd
```

普通终端即可构建，无 NuGet 依赖。启动脚本只在缺少配置时复制 `config.example.json`，不覆盖已有配置。示例仅启用本机。生成四个程序：GPUPCMonitor.exe、gpm-remote.exe、gpm-probe-tray.exe、ssh-askpass.exe。产物不提交；桌面程序设为 Medium 完整性，避免低完整性导致托盘注册失败。

## 安全连接探针

新配置仅监听回环。局域网使用 TLS 1.2，监控端固定证书 SHA-256 指纹并检查有效期，不接受错误指纹或自动降级。指纹必须通过可信方式交换。

在探针设备构建后，以日常显示托盘的账号打开**管理员 Windows PowerShell 5.1**：

```powershell
.\tools\install-probe.ps1 -MonitorAddress 192.0.2.20 -HostAddress 192.0.2.10 -WhatIf
```

替换文档专用地址，确认后去掉 `-WhatIf`。脚本创建 TLS 证书、隔离程序目录、共享配置、精确监控端 IP 防火墙规则，以及开机采集/登录托盘任务。详见 [安装、升级与卸载](docs/OPERATIONS.md)。

从探针托盘设置复制连接凭据，在监控端粘贴、测试并保存。**凭据含秘密令牌，不可公开粘贴；注意剪贴板历史和跨设备同步。** 监控端令牌和 SSH 密码使用当前用户 DPAPI，旧明文 token 首次成功加载时迁移；加密配置仍不能上传或直接换用户使用。旧探针需升级协议并重新复制 TLS 凭据。

不建议直接暴露公网。没有互联网攻击下的可用性保证、集中身份管理或服务等级承诺。

## SSH

先配置目标 OpenSSH Server、低权限采样账号及精确防火墙规则。通过独立可信渠道核对主机指纹，使用系统 ssh 首次确认并保存。应用使用 StrictHostKeyChecking=yes，不自动接受未知/变化的主机密钥。

SSH 会在远端指定目录部署采集器；账号需该目录写入和采样权限，不要求管理员权限。程序不会自动启用 SSH。SSH 参数校验不取代对导入配置来源的信任。

## 数据与提醒

日志默认在配置 dataDir：runtime.log 限 256 KiB，其他诊断日志 64 KiB，各一份轮换，已知诊断文件 7 天过期。日志和报告仍可能包含主机名、地址、账号或路径，分享前人工脱敏；诊断不再写完整 SSH 命令或原始响应。强制终止不保证退出日志。

连接失败、85°C 温度持续 5 秒、95% CPU/GPU/磁盘或 90% 内存持续 30 秒时提醒；未知/过期采样不算持续超限。稳定恢复后才重新允许提醒。气泡受 Windows 通知设置影响；小部件约 8 秒收回，不打开已关闭的小部件。这些阈值不是硬件安全界限。

## 测试

```powershell
.\tools\run-tests.ps1
.\tools\run-tests.ps1 -SkipDesktop
python tools/check-public-content.py --history --working-tree
.\tools\check-secrets.ps1
node tools/web-security-test.mjs
```

完整测试需要真实桌面；SkipDesktop 用于 CI，不宣称验证真实托盘和窗口顺序。协议、安全、TLS 测试使用合成凭据及本机模拟端点，不操作实际远端。TLS 测试在当前用户 My 证书库生成一天有效的临时证书，结束后删除，不改变系统根信任。输出位于忽略的 .tmp。

## 维护

[安全策略](SECURITY.md) · [贡献说明](CONTRIBUTING.md) · [变更记录](CHANGELOG.md)

src 为当前桌面/探针，assets 为源码生成图标，tools 为安装与测试。web 和 start-dashboard.cmd 为旧版兼容实现，不是主入口、不建议新部署；已限制静态文件服务范围并转义动态文本。

目前仅发布源码。未来发布二进制需附版本、哈希与构建来源并单独扫描成品；不要压缩整个工作目录当发布包。
