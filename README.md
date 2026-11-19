# 河科大校园网助手

> 本项目由 ChatGPT 生成。

面向河南科技大学 `Haust-STU` 的 Windows 桌面工具。支持断线检测、Wi-Fi 重连、新版校园网网页认证、托盘监控和登录 Windows 时启动。

## 使用

1. 先在 Windows 连接并保存 `Haust-STU`。
2. 打开 `CampusConnector.exe`，在「账号设置」保存校园网学号、密码和运营商。
3. 点击「开始监控」。需要时勾选「登录 Windows 时自动启动」。

## 托盘与后台监控

- 勾选「启动时最小化到托盘」。已开启监控时，下次打开程序会直接进入托盘。若同时启用了开机自启，登录 Windows 时也会直接进入托盘。
- 点击窗口右上角的「最小化」可立即转到托盘。双击通知区域图标恢复窗口，右键菜单可暂停、恢复监控或退出程序。
- 联网稳定时每 60 秒检查一次。网络地址变化或电脑从睡眠唤醒时，会提前检查。需要恢复连接时约每 15 秒检查一次。

## 校园网认证

程序会自动识别校园网认证入口，入口暂时不可用时会尝试学校默认入口，无需填写认证地址。

账号和密码使用 Windows DPAPI 加密，绑定当前 Windows 用户。可在「账号设置」清除。遇到验证码或强制改密时，用「浏览器登录」完成学校要求的步骤。

## 构建与自测

在安装了 .NET Framework 4.8 的 Windows 10 或 11 上，从项目目录运行 PowerShell。

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /warnaserror /codepage:65001 /optimize+ /target:winexe /r:System.Web.dll /r:System.Web.Extensions.dll /r:System.Security.dll /win32manifest:App.manifest /out:CampusConnector.exe *.cs
if ($LASTEXITCODE) { throw 'Build failed' }
$test = Start-Process .\CampusConnector.exe -ArgumentList '--self-test' -Wait -PassThru
if ($test.ExitCode) { throw 'Self-test failed' }
```

移动程序时，请将 `CampusConnector.exe` 和 `CampusConnector.exe.config` 放在同一目录。配置文件启用高 DPI 支持。

参考 [河南科技大学入网说明](https://metic.haust.edu.cn/info/1002/4280.htm) 和 [haust-auto-login](https://github.com/heragehome/haust-auto-login) 的认证流程。
