# Mini-EAP 半导体设备自动化控制系统（原型）

![License](https://img.shields.io/badge/license-MIT-blue)
![Python](https://img.shields.io/badge/Python-3.8%2B-3776AB?logo=python&logoColor=white)
![C%23](https://img.shields.io/badge/C%23-.NET%208-512BD4?logo=csharp&logoColor=white)
![WinForms](https://img.shields.io/badge/UI-WinForms-512BD4?logo=windows&logoColor=white)
![ASP.NET%20Core](https://img.shields.io/badge/Web-ASP.NET%20Core%208-512BD4?logo=dotnet&logoColor=white)
![MySQL](https://img.shields.io/badge/DB-MySQL-4479A1?logo=mysql&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey)

面向半导体 CIM / EAP 场景的迷你原型：**设备模拟器 ↔ EAP 主控端（C/S）↔ MySQL 存储过程 ↔ Web 报表端（B/S）**。
对应工业现场职责：设备自动化（EAP）、Recipe 管理、报警闭环、FDC 阈值监控、设备远程操作。

> 工业标准协议为 SECS/GEM（HSMS + SML 报文），本原型用自定义 TCP JSON 行协议做等价简化（消息模型一一对应，见下文面试话术）。

## 目录结构

```
mini_eap/
├─ simulator/device_simulator.py   设备模拟器（Python 标准库，TCP JSON 行协议）✅ 已实测
├─ database/
│  ├─ 01_init.sql                  建库建表（equipment / recipe / run_history / alarm_history）
│  ├─ 02_procedures.sql            存储过程（sp_add_alarm / sp_clear_alarm / sp_finish_run / sp_alarm_stats）
│  └─ 03_seed.sql                  演示数据
├─ MiniEapHost/                    C# WinForm 主控端（.NET 8 + MySqlConnector）✅ 编译通过 0 警告 0 错误
│  ├─ MainForm.cs                  主界面：状态面板着色 / 命令区 / 报警确认 / 日志 / 存储状态栏
│  ├─ TcpDeviceClient.cs           TCP JSON 行协议客户端（异步读循环）
│  ├─ Db.cs                        存储抽象：MySQL 存储过程优先，不可用自动降级本地 JSON 文件
│  └─ Models.cs                    事件与应答模型
├─ WebReport/                      ASP.NET Core 8 报表端（B/S）✅ 已实测
│  └─ Program.cs                   报表页 + /api/runs /api/alarms /api/stats
└─ docs/                           实施方案 / 部署手册 / 操作手册（交付三件套）
```

## 5 分钟跑通

```bash
# 1) 建库（账号密码按现场调整）
mysql -uroot -p123456 --default-character-set=utf8mb4 < database/01_init.sql
mysql -uroot -p123456 --default-character-set=utf8mb4 < database/02_procedures.sql
mysql -uroot -p123456 --default-character-set=utf8mb4 < database/03_seed.sql

# 2) 设备模拟器
python simulator/device_simulator.py --port 9000

# 3) EAP 主控端（连接 127.0.0.1:9000）
dotnet run --project MiniEapHost

# 4) Web 报表端
dotnet run --project WebReport --urls http://127.0.0.1:5050
```

验证链路：加载 Recipe → START → 状态推进 PROCESSING → 腔温漂移连续超限触发 E1024 报警（入库）→ 复位报警（入库）→ Web 报表查看运行/报警记录。
**MySQL 未安装也能演示**：存储层自动降级为本地 JSON 事件文件，功能不受影响。

## 架构与协议

- **设备协议**：TCP 长连接 + JSON 行协议。事件上行：`status`（2 秒心跳）/ `alarm` / `alarm_clear` / `run_done`；命令下行：`LOAD_RECIPE` `START` `PAUSE` `STOP` `RESET`。
- **FDC 简化版**：腔温漂移连续 3 个周期超上限 → 触发 E1024 报警（工业 FDC 阈值规则思想）。
- **存储设计**：写操作全部走存储过程，界面层不直接拼 SQL；SQL 为标准写法，便于迁移 Oracle。

## 与简历 / 面试的对应

| 简历/面试考点 | 项目对应 |
|---|---|
| C# / WinForm / Visual Studio | MiniEapHost 主控端 |
| ASP.NET / B/S 架构 | WebReport 报表端 |
| C/S 架构 | 主控端 ↔ 模拟器 |
| Oracle / 存储过程 / ADO.NET | 存储过程 + MySqlConnector（ADO.NET）调用 |
| EAP 设备自动化 / 远程操作 | 命令下发与报警闭环全链路 |
| FDC | 温度阈值报警规则 |

**被问"为什么不用 SECS/GEM"**：SECS/GEM 是 SEMI 标准协议栈（HSMS-SS 传输 + SML 报文），学习成本高；本原型的 JSON 行协议在消息模型上与 SECS 对应——`status`≈S1F3 状态查询、`alarm`≈S5F1 报警上报、`LOAD_RECIPE/START`≈S2 控制与 Recipe 报文。先理解模型，协议栈是工程化替换的问题。

## 后续可选扩展（不阻塞简历）

- Recipe 参数管理页（WinForm 二级窗口 + DataGridView）
- 报表端 jQuery/EasyUI 交互升级
- 迁移 Oracle XE 验证存储过程兼容性
