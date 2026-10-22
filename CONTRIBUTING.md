# 贡献与反馈

这是个人实验项目，不承诺响应时间、生产可用性或覆盖全部设备。请先创建不含隐私的最小复现，再提交改动。

- Windows x64，.NET Framework 4.8，Windows PowerShell 5.1。提交前从干净目录构建。
- 运行 `tools/run-tests.ps1`；在实际桌面额外运行同一脚本且不传 `-SkipDesktop`。CI 不等于真实托盘验收。
- 运行 `python tools/check-public-content.py --history --working-tree`。不要为了让检查通过而忽略真实秘密。
- 不提交实际配置、DPAPI 密文、令牌、SSH 文件、日志、设备截图、导出数据或编译产物；需要样例时使用合成值及文档专用地址。
- 修改协议需增加错误输入、超时、权限和兼容性测试。不要添加自动关闭 SSH 主机验证或 TLS 降级。
- 安全漏洞按 SECURITY.md 私密报告，不把漏洞凭据放进公开 Issue。

提交贡献意味着你有权以项目的 MIT 许可提供相应内容。引用第三方代码或资源时附来源与许可，不以 AI 生成作为可随意使用第三方材料的依据。
