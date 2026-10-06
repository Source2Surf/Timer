# Timer.Backend 源码测试包

本包包含当前后端源码、构建所需的存储层/共享模型/协议项目、两套后端测试、数据库迁移脚本和原项目许可证。入口解决方案为 `Timer.Backend.slnx`。

## 环境与构建

- 安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，仅安装 Runtime 不够。打包验证使用 SDK 10.0.401。
- 首次还原需要访问 nuget.org；`ModSharp.Sharp.Shared` 等依赖由 NuGet 获取，构建和运行后端不需要安装 CS2 服务器。
- 运行 API 需要单独准备 MySQL/MariaDB 或 PostgreSQL 数据库。此前真实数据库验收使用 MySQL 9.0.0、PostgreSQL 18.4；其他版本没有在本次交付中验证。
- 执行 `.ps1` 脚本需要 PowerShell 7；常规 `dotnet` 命令可在 Windows/Linux 使用。

在解压后的根目录执行：

```sh
dotnet restore Timer.Backend.slnx
dotnet build Timer.Backend.slnx -c Release --no-restore
dotnet test Timer.Backend.slnx -c Release --no-build
```

未提供 `TIMER_TEST_MYSQL`、`TIMER_TEST_POSTGRES` 时，真实数据库集成测试会跳过；这时结果只说明不依赖外部数据库的测试通过。Windows TLS 用例还需要在可访问证书私钥的完整用户会话中设置 `TIMER_TEST_TLS=1`。

## 从上一版测试包升级

当前源码增加了可空的 `surf_players.JoinedAtUtc`；之前的 `surf_score_recalc_outbox.PendingSinceUtc` 迁移也仍适用。首次加入时间从已有 `UpdatedAt` 回填一次，之后不随改名或积分变化。迁移还会将无效游玩时长归零、保留游玩次数并记录修复数量。使用上一版测试库时，先停止写入端和 worker、备份数据库，再用本版代码执行 `migrate`，完成后启动服务。已转换过的 `surf_runs.Date` 不需要再次执行日期转换。启动检查会拒绝尚未升级的写入数据库，详细步骤见 [更新旧测试包](../Backend/Timer.Backend/README.md#update-an-earlier-backend-test-bundle)。

本次修复了长耗时重算在租约接管后的完成记账、并发区域保存、持续失败任务的积压告警，以及管理操作的取消和释放时序。删除成绩后仍保留提交回执，防止旧请求重放复活已删除的成绩。

2026-09-17 的对抗审查还修复了批量预租导致未执行任务耗尽重试、写就绪漏查积分配置表、MySQL 提交回执时间不一致，以及旧插件回放重试误删/截断已保存文件。回放现在为每次上传使用独立对象键；SQL 提交结果不确定时保留对象，后续可能需要清理无引用文件。本轮没有增加数据库列。

## 启动 API 测试

1. 创建专用测试数据库和测试用户；源码包不包含数据库密码或数据。
2. 复制 `Backend/Timer.Backend/appsettings.example.json` 为 `Backend/Timer.Backend/appsettings.Production.json`，填写 `TimerBackend:Database:Type`（`postgresql` 或 `mysql`）及 `ConnectionString`。
3. 后端每次启动都会补建缺少的表和列，全新的空测试库无需额外设置。已有 Timer 数据库应按 [后端迁移说明](../Backend/Timer.Backend/README.md#upgrade-an-existing-master-sql-database) 升级，不要用新库初始化步骤替代迁移。
4. 测试写入时设置 `TimerBackend:WriteApi:Enabled: true`。这会同时启用积分 Outbox worker；默认只开放读取，写 API 关闭。
5. 确保环境为 `Production` 后，从根目录运行：

```sh
dotnet run --no-build --no-restore -c Release --project Backend/Timer.Backend/Timer.Backend.csproj
```

PowerShell 可用 `$env:DOTNET_ENVIRONMENT='Production'` 设置环境；Linux shell 使用 `export DOTNET_ENVIRONMENT=Production`。配置示例仅监听本机，读取端口为 5081，写入端口为 5082。

启动后访问：

- `http://127.0.0.1:5081/health/live`：进程存活。
- `http://127.0.0.1:5081/health/ready`：数据库和当前角色所需结构已就绪。
- `http://127.0.0.1:5081/health/worker`：启用写入时应为 `healthy`；只读实例为 `disabled`。
- `http://127.0.0.1:5081/api/v1/maps`：地图列表，新空库返回空列表。

写入服务使用 MagicOnion/HTTP/2，接口位于 `Timer.Backend.Rpc.Contracts`，不能用普通 REST POST 调用。提交成绩前地图必须已存在、玩家须经登录接口创建；自动集成测试会准备这些数据。该测试配置没有写入身份认证，保持回环地址，跨主机测试只开放给可信测试网络。

## 完整后端数据库验收

准备两个**可丢弃且已创建**的本机数据库，库名必须包含 `test`。下面的脚本会重建它们的表和测试数据；不要指向业务数据库。

```powershell
$env:TIMER_TEST_MYSQL = 'Server=127.0.0.1;Port=3306;Database=timer_backend_test;User ID=TEST_USER;Password=CHANGE_ME'
$env:TIMER_TEST_POSTGRES = 'Host=127.0.0.1;Port=5432;Database=timer_backend_test;Username=TEST_USER;Password=CHANGE_ME'
pwsh -File scripts/test-backend-acceptance.ps1 -DisposableDatabases -BackendOnly
```

`-BackendOnly` 执行数据库迁移、存储层和后端 API 测试。此包不含游戏插件及 `Timer.Tests`，因此不要省略该选项。验收脚本会启用迁移/TLS 用例，并将任何跳过视为失败；报告保存在 `artifacts/backend-acceptance/`。上述环境变量只用于测试程序集，日常 API 使用自己的 `TimerBackend:Database` 配置。

上一版 ZIP 交付时，仓库完整验收为 351 项通过、0 跳过，其中该包覆盖的后端/存储/迁移测试为 270 项，其余 81 项属于游戏插件。当前源码在数据库边界修复后的完整验收为 449 项通过、0 跳过，详见 [修复记录](../analysis/database-edge-fixes-20260917.md)；现有 ZIP 的校验记录仍对应其打包时版本。本次结果见 `verification/baseline.json`；源码包解压后的独立构建与测试结果会另附在 ZIP 同目录的校验报告中。以上结果不代表已完成真实生产负载压测或长期运行验证。

本包已排除 `.git`、IDE 缓存、`bin/obj`、本机配置、密钥、数据库文件、日志及旧测试产物。`MANIFEST.sha256` 记录每个交付文件的校验值。授权条款保留在 `License`。
