# Motion Capture — 项目存档备注

> 日期：2026-09-09　｜　阶段：双摄动捕（MediaPipe → UDP → Unity）本地开发告一段落
> 这份文件用于「下次怎么找到它」。

## 一句话
所有代码改动都已提交并推送到两个仓库的 `codex/bootstrap-safe-checkpoints` 分支（另有一个工具分支 `codex/gh-auth-bootstrap`）；功能验收在 GitHub 之外，需用真机 + Unity Play 验证。

## 从哪里找到它

### Unity 仓库
- 目录：`D:\unityhub\1\My project (第三版)`
- 远程：`https://github.com/timi200603-maker/UNITY.git`
- 功能分支：`codex/bootstrap-safe-checkpoints`（含全部动捕/下蹲/诊断功能）
- 工具分支：`codex/gh-auth-bootstrap`（在其之上多一个 GitHub 认证引导脚本）

### Python 仓库
- 目录：`D:\PycharmProjects\PythonProject`
- 远程：`https://github.com/timi200603-maker/pose-estimation-dual.git`
- 分支：`codex/bootstrap-safe-checkpoints`

### 查看/找回命令
在对应仓库目录执行：
```powershell
git branch -a                 # 看所有分支
git log --oneline -12         # 看最近提交
git status -sb                # 看当前分支与是否干净
git remote -v                 # 看远程地址
```
用 GitHub CLI（已认证，无需手动 token）：
```powershell
gh pr list --repo timi200603-maker/UNITY --state open
gh pr list --repo timi200603-maker/pose-estimation-dual --state open
```

## 已实现的 PR
- Unity：`#1`（功能聚合：脚锁 + 下蹲 + 诊断回传）、`#3`（GitHub 认证引导工具）
- Python：`#1`（功能聚合：桌面监看接收 Unity 脚锁诊断）

## 本次功能要点
1. **Stage A 脚锁**：接触候选锚点(`anchorId` 递增)、抬脚/滑动/不可达释放、直解两骨腿 IK、板面贴地。
2. **下蹲支持**：自动站立基准(0.6s 站直)、骨盆升降、分腿置信度、1cm 死区/50ms 平滑/1.5m/s 上限、>250ms 暂停保持、enableSquat 开关。
3. **诊断回传**：Unity `FootIkUdpSender` → `127.0.0.1:8890` 10Hz；Python 桌面监看显示左右脚(解剖)锚点/释放原因 + 下蹲摘要。

## 待验证（GitHub 非验收前置）
- Unity：需运行 `Mocap → Add Foot IK UDP Sender` 后重编。
- 真机 Play 验收：站直建基准 → 半蹲/深蹲/保持/起身 → 单脚抬膝不误蹲 → 桌面监看诊断正确。
- 遗留风险：大腿/腰部外观若异常，需先对照骨架（髋/膝/踝 + 骨段连线），判断是运动计算还是蒙皮权重问题（轮次不动模型权重）。

## 当前状态
- 两个仓库工作区干净、已与远程同步（无未提交改动、无未推送提交）。
- `动捕.unity` 场景未被改动；1.5m×1.5m 标定、0.20m 保存上限、桌面监看、方向设置均保留。
