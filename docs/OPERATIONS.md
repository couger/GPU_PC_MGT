# 安装、升级与卸载

## 安装

1. 在目标 Windows 11 x64 设备构建四个程序，使用可信源码。以实际显示托盘的日常账号打开管理员 Windows PowerShell 5.1，不能用另一个管理员账号代替。
2. tools/install-probe.ps1 -MonitorAddress 192.0.2.20 -HostAddress 192.0.2.10 -WhatIf；替换文档地址，确认后去掉 WhatIf。
3. 安装创建 LocalMachine/My 两年有效证书、开机 SYSTEM 采集任务、登录用户 Limited 托盘任务。采集放在 ProgramData/GPUPCMGT/bin，共享配置在 config 子目录，托盘在用户 LocalAppData/GPUPCMGT。bin 对普通用户只读，仅配置子目录允许操作者修改。
4. 防火墙默认仅 Private 网络、指定监控端 IP、端口及 EXE。若目标实际网络被系统标为 Public，核实可信局域网后显式传入 `-FirewallProfile Public`，仍只放行该监控端。脚本不改变网络类别、睡眠、路由器或 SSH。
5. 从探针设置复制 TLS 连接凭据，可信地交给监控端粘贴、测试、保存。不要公开凭据。

## 升级

同样运行统一安装脚本并加 -Upgrade。只接管已知 GPUPCMGT 路径的任务。保留原令牌和采样周期，按输入重新限制允许列表，生成新证书并要求重新复制指纹。旧协议不自动降级。先在可恢复测试环境验证。

只保留 previous-install 一套回滚文件，包含敏感配置/任务 XML，不能上传。失败尝试恢复文件、任务和命名防火墙规则。旧 install-probe-tray.ps1 / upgrade-probe-config.ps1 已停用，统一用 install-probe.ps1 -Upgrade；update-probe-tray.ps1 只更新同路径托盘，不升级采集协议。

## 证书和凭据轮换

证书不加入系统根信任；到期或私钥疑似泄露时运行升级，更换证书并更新所有监控端指纹。需要撤销令牌时，在探针设置重新生成并更新所有客户端。只换令牌不能补救证书私钥泄露。

监控端明文 token 在首次成功加载/保存时迁移为当前用户 DPAPI，不创建明文备份。换 Windows 用户不能直接复制加密配置使用。

## 停用卸载

安装账号管理员 PowerShell 运行 tools/uninstall-probe.ps1 -WhatIf，再去掉 WhatIf。核对任务路径后停止进程，移除任务和命名防火墙规则；保留配置、证书和回滚数据避免误删。

若确定不需恢复，再手动删除准确的 GPUPCMGT 目录，并按 installation.json 的精确指纹删除 Personal/My 对应证书；不要批量删除证书库、ProgramData 或 LocalAppData。转交设备前撤销令牌。

## 验证与排查

检查 GPUPCMGT-Probe 开机任务和 GPUPCMGT-ProbeTray 登录任务。未登录时无托盘不等于采集未运行。检查实际 IP、精确防火墙范围、Private 网络、证书有效期及客户端指纹；不要把握手失败当作没开机。

管理员安装/升级/卸载涉及任务、证书和防火墙，尚未覆盖所有真实设备组合；WhatIf 不是实际安装成功证明。先在可恢复环境验收。故障历史仍只在内存，不因安装备份而永久保存。
