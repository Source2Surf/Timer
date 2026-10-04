# Timer 独立后端写入迁移方案

状态：实施中。只读 REST 后端、`surf_score_recalc_outbox`/leased worker、MagicOnion v1 契约、Submission Inbox、内部 SQLSugar 写入端口、默认关闭的 MagicOnion 写入入口及玩家资料 RPC 已完成。写入入口不内置 API key 或 ServerId；部署者负责网络隔离。按当前部署取舍，插件端不再使用 LiteDB；同进程内存重试、登录建档 RPC 和主图/阶段成绩远端路由已接通。直连 Kestrel 的 h2c 与 HTTPS/HTTP2 写入测试已通过，真实 TLS 反向代理链路验收、通用事件 Outbox 与其余读写迁移尚未完成。
目标架构：REST/JSON 公开查询 + MagicOnion 内部命令 + SQLSugar 事务存储 + SQL Outbox + 可选 MQ/StreamingHub。

## 1. 结论

- 保留现有 `Timer.Backend` 的 REST/JSON 只读 API，继续服务面板、第三方客户端和替代后端实现。
- 官方游戏插件的写入改为 MagicOnion Unary RPC。RPC 只有在核心 SQL 事务提交成功后才返回成功。
- SQL Outbox 是必选项，MQ 是可选项。第一版由后台 Worker 直接消费 SQL Outbox；只有出现多消费者、跨服务或独立扩缩容需求时才接 RabbitMQ/NATS JetStream。
- StreamingHub 只负责实时通知，不进入成绩可靠写入链路，也不承担离线堆积或重放。
- 游戏服务器不直连 MQ，不把 replay 二进制放进成绩提交 RPC。

```text
Timer 插件
  ├─ 进程内待发送队列（同一进程内 SubmissionId 不变）
  └─ MagicOnion Unary: SubmitRunAsync
          │
          ▼
Timer.Backend
  ├─ 协议与字段校验
  ├─ RunSubmissionService
  └─ SQLSugar ReadCommitted 事务
       ├─ submission 幂等记录
       ├─ surf_runs / surf_runs_segments
       ├─ surf_player_best_runs
       └─ surf_score_recalc_outbox
          │
          ├─ 第一阶段：Outbox Worker 直接执行积分重算等幂等任务
          └─ 扩展阶段：通用事件 Outbox → IEventPublisher → MQ → 多个消费者
                                      └─ StreamingHub 推送实时事件
```

## 2. 项目边界

### 保留

- `Timer.Backend.Contracts`：传输无关的公开 REST DTO；继续保持无 MagicOnion 包依赖。
- `Timer.Backend`：ASP.NET Core 宿主、现有 REST 读取端点、健康检查和新的 MagicOnion 服务。同一二进制支持 `ReadApi`、`WriteApi`、`OutboxWorker`、`Migration` 部署角色；小规模环境可以合并运行，生产环境可以独立扩缩容和授权。
- `Timer.RequestManager`：迁移期继续复用现有 SQLSugar 实体、查询和写事务，避免同时重写协议层和数据库语义。

### 新增

- `Timer.Backend.Rpc.Contracts`
  - 引用 `MagicOnion.Abstractions`。
  - 只包含版本化服务接口、命令/结果 DTO 和稳定枚举。
  - 不引用 ModSharp、SqlSugar 或后端实现。
- `Timer.Backend.Application`（写入逻辑变多时再拆）
  - 保存成绩、幂等、Outbox 和领域校验。
  - 同一个应用服务可被 MagicOnion 入口和迁移期本地 SQL 入口调用。
- 插件侧 `RunSubmissionSender`（成绩写入切片）
  - 复用进程生命周期内的 `GrpcChannel` 和客户端代理。
  - 负责内存提交队列、重试和 RPC 模型映射；不负责其他 `IRequestManager` 读写。

现有 `TimerBackendStorage` 的公开面保持只读；已新增独立 `TimerBackendWriteStorage` facade，并复用同一个受生命周期管理的 SQLSugar scope。各部署角色使用不同数据库权限：read 只允许查询，write 只允许必要 DML，worker 只允许消费表和对应派生表，migration 独占 DDL 权限。

长期再把 SQLSugar 持久化层从 `Timer.RequestManager` 抽成游戏无关项目。本轮先复用现有实现，以降低迁移风险；但写入入口不得继续泄漏 `SteamID`、`Vector` 等 ModSharp 类型。

## 3. RPC v1

建议将主图和阶段成绩合并为一个命令，由 `RunKind`、`Track`、`Stage` 区分：

```csharp
public interface ITimerWriteServiceV1 : IService<ITimerWriteServiceV1>
{
    UnaryResult<EnsurePlayerProfileResponse> EnsurePlayerProfileAsync(EnsurePlayerProfileRequest request);
    UnaryResult<SubmitRunResponse> SubmitRunAsync(SubmitRunRequest request);
    UnaryResult<GetSubmissionStatusResponse> GetSubmissionStatusAsync(GetSubmissionStatusRequest request);
}
```

`SubmitRunRequest` 至少包含：

- `Guid SubmissionId`：插件生成一次并在重试中复用；跨游戏服全局唯一。
- `long SteamId`：在 ModSharp 边界转为有符号 `long`，与现有数据库约束一致。
- `string MapName`、`RunKind`、`Style`、`Track`、`Stage`。
- `long TimeMicros`：线上协议统一使用整数微秒，不直接传 `float` 时间。
- 跳跃、连跳、同步率和速度遥测。
- 有上限的 checkpoint 数组。
- `FinishedAtUtc`：保留断网补传前的真实完成时间；后端另记 `ReceivedAtUtc`。
- `ContractVersion`、`RulesetVersion` 和插件构建版本。破坏性变更新增 `ITimerWriteServiceV2`，不原地改变 v1 语义。

`SubmitRunResponse` 包含 `SubmissionId`、稳定的 `RunId`、`AttemptResult`、`Rank` 和服务端接收时间。同一 `SubmissionId` 的所有成功重试必须返回同一核心结果。

`GetSubmissionStatusAsync` 可供诊断已提交的 ID；它不返回原始 payload hash，不能证明该记录属于当前待发送请求。插件在超时后重发原请求，由后端比较 hash 并返回稳定结果或冲突。若 rank 不进入核心事务，应明确返回 `RankPending`，而不是在重复请求时返回不同的即时排名。

写入不要求 API key 或 ServerId；`SubmissionId` 自身是全局幂等键。v1 RPC 不接收分数或 `StyleFactor`；样式倍率与 ruleset 来自后端配置，地图 tier/base pot 来自后端数据库。旧直连 SQL 保存和手动重算命令仍可从插件传倍率，必须在远端模式隔离，不能把它们视为最终后端计分路径。

## 4. 核心事务与幂等

新增逻辑表：

### `surf_run_submissions`

- 自增主键。
- `SubmissionId` 单列唯一索引。
- 规范化请求的 `PayloadHash`。
- `RunId`、`AttemptResult`、`RankState`、`Rank`、`FinishedAtUtc`、`ReceivedAtUtc`。
- 成功响应所需的稳定字段。

`master` 分支的 `surf_runs` 没有 `ServerId` 或 `SubmissionId`。迁移不为成绩表增加幂等字段或索引；唯一 `SubmissionId` 只存于新建的 Inbox，关联保存后的 `RunId`。专用前置命令会逐行校验并把旧成绩的 `surf_runs.Date` 从 SQL date-time 改为 Unix 毫秒 `BIGINT`；除此之外不改写旧成绩内容。当前没有已发布的中间后端 schema 需要迁移。

同一个 ID、同一个 hash：返回已保存结果；同一个 ID、不同 hash：返回冲突，绝不覆盖旧提交。

### `surf_outbox`

- `EventId`、事件类型、版本、聚合键和序列化 payload。
- `OccurredAtUtc`、`AvailableAtUtc`、`AttemptCount`。
- `LeaseOwner`、`LeaseUntilUtc`、`ProcessedAtUtc`、`LastError`。
- “待处理 + 可执行时间”和 lease 查询所需索引。

积分重算另设可合并的 `surf_score_recalc_outbox`：以 `(MapId, Style, Track)` 为唯一键，维护 requested/processed generation 和 lease。完赛事务只推进 generation，worker 总是根据最新 best-run 投影重算，从而天然合并短时间内的多次 PB/WR。该表是已经落地的第一种专用 Outbox；它不替代后续承载 `RunCommittedV1` 等集成事件的通用 `surf_outbox`。

单次 `SubmitRun` 的处理顺序：

1. 在事务外完成消息大小、数字范围、NaN/Infinity、地图名和 checkpoint 结构校验。
2. 对已完成 submission 做一次无锁快速查询；命中相同 hash 时直接返回。
3. 解析已存在的地图。成绩提交不应因拼写错误自动创建地图。
4. 开启 SQLSugar `ReadCommitted` 事务。
5. 先锁地图主键；若以后需要更新玩家总分，再按玩家主键升序加锁，保持现有全局锁序。
6. 在事务内重新检查 submission，解决多副本并发重试竞争。
7. 查询 WR/PB，写入一条 run，批量写 checkpoints，并条件更新 best-run 投影。
8. 在同一事务中写 submission 稳定结果；主图 PB/WR 同时推进积分重算 Outbox。版本化 `RunCommittedV1` 通用事件留到后续切片。
9. 提交后才向 RPC 客户端返回成功。

submission、run、best-run 和本次需要的积分 Outbox 要么全部提交，要么全部回滚。提交结果不明时，后端不盲目重试整个事务；插件使用原 `SubmissionId` 重试，由幂等表判断提交是否已经成功。

所有 schema/index 变更由一次性 migration 角色显式执行，普通 API/worker 副本不得并发运行 CodeFirst DDL。

切换前先运行 `Timer.Backend migrate` 一次性迁移命令：它增量升级 master SQL schema，并逐地图补种历史 best-run 投影；普通后端副本继续保持 `InitializeSchema=false`、`AllowReadRepair=false`。目前尚未提供投影逐行一致性及旧积分 ruleset 的完整审计，正式切换前仍需在数据库副本上验收。当前写入适配器另有按榜单的冷路径补种保护，避免遗漏补种时历史 run 被误判为 WR；同一进程命中补种缓存后的稳态写入不再产生这部分 SQL。以后可用持久化迁移门闩替代这道兼容保护。

## 5. Outbox、MQ 和实时通知

### 第一阶段：只使用 SQL Outbox

- Worker 每次读取一个有上限的批次，使用带过期时间的条件 lease 抢占；整个流程只使用强类型 SQLSugar 查询和更新。
- 积分重算按 `(MapId, Style, Track)` 合并，避免连续 PB 触发重复全量重算。
- handler 必须幂等。进程在执行成功、标记完成前崩溃时允许再次执行。
- 达到重试上限的事件进入持久化 dead-letter 状态并告警，不能静默丢弃。
- 多个 worker 的并行 job 使用独立 SqlSugar scope/连接，不能在同一事务上下文中 `Task.WhenAll`。

### 需要 MQ 时

- `IEventPublisher` 增加 RabbitMQ 或 NATS JetStream 实现。
- Outbox publisher 获得 broker confirm/ack 后再把消息标为已处理。
- “消息已发布但数据库尚未标记”仍会产生重复消息，因此每个消费者用 `ConsumerName + EventId` 唯一 inbox 去重。
- broker 故障只让 Outbox 积压，不影响已经成功提交的成绩 RPC。

### StreamingHub

StreamingHub 订阅 `RunCommittedV1`、`BestRunChangedV1` 等事件，向在线管理面板或游戏服广播。客户端断线后重新连接并主动刷新 REST 快照；不依赖 Hub 补齐离线消息。

## 6. 插件侧内存队列与失效边界

成绩完成后先生成 `SubmissionId` 并放入有容量上限的进程内队列，再由单独 sender 调用 MagicOnion。同一进程内的连接中断、超时及响应丢失以原 ID 重试；重连测试验证了排队项在 ACK 前保留并沿用同一 ID。不引入 LiteDB 或自制文件 WAL。用户选择接受的代价是：**游戏进程崩溃、重启或关服时，尚未得到后端确认的队列会丢失**；正常关服会记录未确认队列数量作为诊断。后端 submission inbox 只能保护已经抵达后端的请求，SQL Outbox 也只能保护已入库后的积分任务。

状态建议为：

```text
Pending ──发送──> InFlight ──确认──> 删除
   ▲                  │
   └──同进程瞬时错误/重连┘

永久协议错误 ──> Failed（内存隔离并告警）
```

- `Unavailable`、`DeadlineExceeded`、连接中断和不明确的提交结果：指数退避加抖动，以原 ID 重试。
- `Unauthenticated`、`PermissionDenied`、`InvalidArgument`：不无限重试，进入隔离队列。
- 队列必须有容量、待提交数量、最老消息年龄与拒绝入队告警；关闭时报告未确认数量，不得伪称已保存。
- 一个插件生命周期只创建一个 gRPC channel；不得每次完赛重新建连。
- sender 使用后台异步队列，替代当前围绕异步 I/O 的额外 `Task.Run`。
- 自动回退到直连 SQL 禁止。显式回滚前先暂停新提交并尽量排空内存 pending；不能排空时要明确记录可能丢失的成绩，避免两条写路径各自提交。
- 只有收到 canonical `Accepted`/`AlreadyApplied` 响应后，才触发当前 `OnRecordSaved` 事件并刷新缓存；RPC 异常不能被“只记日志”后当作已保存。

## 7. Replay 和其他写入

Replay 文件不放入 `SubmitRun`，否则会扩大 RPC 消息、事务和失败重试成本。建议流程为：

1. 先提交成绩并取得 `RunId`。
2. 插件继续通过现有 HTTP blob storage 上传 replay，或向后端请求预签名上传地址。
3. 使用另一个幂等 Unary RPC 关联 `RunId + URL + Hash + Size`。
4. Outbox/reconciliation 处理孤儿文件和缺失元数据。

后续写入迁移顺序：

1. 主图和阶段成绩。
2. replay 元数据。
3. 玩家资料和地图 session 统计；高频 playtime 增量按服务器批次和序列号提交。
4. 地图资料、zones 等低频配置。
5. 删除记录、强制重算等管理命令，使用独立权限和审计日志。

## 8. 安全与部署

- 默认允许直接 h2c，不要求 API key 或 TLS 代理；网络隔离、TLS、代理和访问控制由部署者决定。任何能连接写入端口的人都能提交数据。
- 写入端口不内置认证；如需认证由部署者在网络或代理层实现。
- 根据部署需要自行加防火墙、代理限流与审计；默认不强制这些额外组件。
- 限制请求字节数、checkpoint 数、地图名长度、时间范围和所有浮点值；拒绝 NaN/Infinity。
- 固定 MagicOnion、MessagePack、SqlSugar 和 ModSharp 依赖版本，不在生产项目继续使用浮动 `*`。
- 写入启用前先解决当前后端停机时 in-flight 请求与 storage dispose 的排空竞争。
- 独立监控 idempotency hit/hash mismatch、RPC unknown、map 锁等待、插件内存 pending 数量/年龄、outbox age、dead-letter、积分重算耗时和 Hub 重连率。

## 9. 性能原则

- 一次完赛只发一个 Unary RPC；checkpoint 批量随命令提交，不逐个调用。
- SQL 热路径保持一次地图锁、有限次索引查询、一次 run insert、一次 checkpoint batch insert、一次 best 条件写和一次 outbox insert。
- 读请求不加事务锁；现有 REST 的 SQL 过滤、上限、ETag 和压缩策略继续保留。
- 同地图写入暂时沿用地图级串行化。完赛频率远低于帧事件，先以正确性为主；只有压测确认其为瓶颈后再缩小锁粒度。
- Outbox 分页、短 lease、有限批次；数据库连接池总量按“副本数 × 每副本上限”控制。
- 日志不输出完整 payload 或 replay 内容，使用 `SubmissionId`、`EventId` 和 trace id 串联诊断。

## 10. 分阶段实施与验收

### 阶段 A：契约和数据库基础

当前进度：RPC v1 契约、Submission Inbox 单列唯一键及 `RunId` 关联、内部 write facade、canonical hash 与 MySQL/PostgreSQL 并发验收已完成；旧本地写路径尚未生成稳定 `SubmissionId`。

- 新建 RPC contracts、submission/outbox 实体和索引。
- 把保存成绩整理成传输无关的应用命令，并让旧本地路径也能传入稳定 `SubmissionId`。
- 保持旧行为默认启用，新表为纯增量迁移。
- 在 Inbox 上建立 submission 单列唯一约束，不为 master 的 `surf_runs` 增加幂等字段；迁移只由专用 migration 角色运行。

验收：MySQL/PostgreSQL 实库中，同一请求并发 100 次只有一条 run、一组 segments、一个 best 结果和一条 outbox；相同 ID 不同 payload 必须冲突。

### 阶段 B：MagicOnion 后端入口

当前进度：feature flag、服务注册/映射、服务端 ruleset/style policy、64 KiB 消息上限、稳定 gRPC 错误分类、独立 HTTP/2 loopback 配置样例、玩家资料 RPC 和真实 h2c loopback 无凭据/超限测试已完成。直连 Kestrel HTTPS/HTTP2 的无凭据请求校验，以及 disposable PostgreSQL/MySQL Profile → Submit → 不读取首回复/关闭连接 → 同 ID 重试 → Status 测试已覆盖；Windows 受限沙箱无法载入 SChannel 测试私钥，默认跳过 TLS 用例，需要完整用户环境设置 `TIMER_TEST_TLS` 显式执行。外部 TLS 代理 smoke test 尚未完成；选择 TLS 代理部署时仍需在目标环境验收。

- 注册 MagicOnion、请求限制和 `ITimerWriteServiceV1`。
- 写入使用 feature flag，默认关闭。
- 补齐直接 HTTP/2 部署样例和健康检查；TLS 代理是可选扩展。

验收：正常、非法、超限、超时后重试和服务端提交后客户端断线场景全部通过。

### 阶段 C：插件远程适配器

当前进度：LiteDB 的权威成绩 fallback 已移除；sender 已改为有界内存队列，`remote-write` 显式模式与本地 SQL 模式隔离，登录经远端建档，主图/阶段成绩先入队，得到后端权威 ACK 才触发保存事件及缓存投影。其他 `IRequestManager` 读写仍依赖外部 SQL provider，这是成绩切片而非完整无数据库凭据的插件。

2026-10-04：`local-sql` 模式已移除。SQL 存储移至 `Backend/Timer.Backend.Storage`，`Timer.RequestManager` 模块删除；插件的 `IRequestManager` 与回放 URL 均经 `ITimerStorageServiceV1` 访问后端，游戏服不再持有数据库凭据。

插件 canary 的最简配置：

```json
{
  "Timer": {
    "ScoreWrite": {
      "Mode": "remote-write"
    },
    "RunSubmissionSender": {
      "Endpoint": "http://127.0.0.1:5082"
    }
  }
}
```

后端必须先由 migration 角色建表，再启用 `TimerBackend:WriteApi:Enabled=true`；规则版本和主样式倍率默认均为 1，可按需覆盖。`Servers` hash、插件 `ApiKey` 和 `ServerId` 已移除。后端写入开关默认关闭，插件未配置 `Endpoint` 时 sender 默认关闭。旧 `run-submissions.db` 如存在，远端 sender 会拒绝启动但不读取/删除它。远端 ACK 遇到地图切换时，只向 ReplayRecorder 发 replay-only 事件，由旧 `MapId`/`AttemptId` 关联已有回放：在 fallback TTL 内仍可按捕获的旧地图路径保存 replay，同时不更新新地图的榜单、玩家缓存或 playback cache；超过 TTL 的 replay 仍可能丢失，但已入库成绩不受影响。跨地图 main/stage 回放的模块测试已覆盖该隔离，真实游戏服时序及可选的外部 TLS 代理 smoke test 尚未验收。

- 新增内存队列、MagicOnion 重试 sender 和成绩写入专用适配器。
- 配置支持 `local-sql` 与 `remote-write` 两种显式模式；不做无条件双写。
- 先迁移一个 canary 游戏服。

验收：后端/网络中断后同进程重连可补发且不重复；游戏进程重启前未确认成绩会丢失，这是此部署策略的显式限制。完整无数据库凭据还需迁移地图、榜单、玩家与其他 `IRequestManager` 读写。

### 阶段 D：Outbox Worker

- 已迁移当前进程内积分调度到持久化、generation 合并的 SQLSugar Outbox 消费；PB/WR 与请求推进处于同一事务，Channel 仅作 Wake。
- 增加积压量、最老事件年龄、失败数、处理耗时和 dead-letter 指标。

验收：Worker 崩溃、重复执行、数据库短暂不可用均可恢复；成绩提交不等待积分重算完成。

### 阶段 E：扩容能力

- 有真实需求时再实现 MQ publisher/consumer inbox。
- StreamingHub 提供实时通知，REST 仍是断线后的权威快照。
- 逐步迁移 replay、统计、地图和管理写入。

验收：broker 停机不阻塞成绩入库；恢复后事件最终送达；重复投递不会重复产生业务副作用。

## 11. 发布与回滚门槛

- 发布前：运行历史 best-run 一次性补种与一致性校验；关闭线上 read repair。
- canary 期间同时观察 RPC 错误率、p95/p99 延迟、数据库锁等待、内存 pending/outbox backlog 和重复冲突数。
- 扩服前：所有数据库并发测试、MagicOnion 端到端测试和 REST 回归均通过；只有选用代理时才需要代理环境 smoke test。
- 回滚只切换入口，不回滚新增表。切回本地 SQL 前必须暂停新提交并尽量排空内存 pending；未确认项不可在关服后恢复，必须将损失风险写入回滚记录。

## 12. 默认技术选择

- RPC：MagicOnion Unary；StreamingHub 仅推送。
- ORM：SqlSugar 强类型 API，`Ado` 仅用于事务控制；禁止 Raw SQL。
- 事务：MySQL/PostgreSQL 使用 `ReadCommitted`，锁序为地图后玩家。
- 可靠性：同进程内存重试 + 服务端 submission inbox + SQL Outbox；不保证插件重启前未送达成绩的恢复。
- MQ：MVP 不部署；通过 `IEventPublisher` 保留 RabbitMQ/NATS JetStream 接口。
- 时间：协议使用整数微秒；现有 `float` 列先在存储边界兼容，后续以独立迁移升级为整数列。
