# 数据库实现约束

- 数据库操作使用 SqlSugar 的强类型查询、写入、事务和维护 API。禁止手写或拼接 Raw SQL，包括锁、upsert、索引和迁移；也不要用字符串谓词、`SqlFunc.Mapping` 或修改生成的 SQL 绕过此限制。
- `Ado` 仅用于事务管理、事务状态检查和执行 `CachedRead`。测试可以检查 ORM 生成的 SQL。
- 热点读取可以用 `CachedRead`：SQL 仍由 SqlSugar 强类型查询按查询形状生成一次（`ToSql()`，参数取 `Sentinel` 值），之后经 `Ado` 绑定新参数执行，并用强类型 getter 读取结果。不要手写或修改这类 SQL；每个新形状都要用不同参数连续读取的测试覆盖 SQLite、MySQL 和 PostgreSQL（见 `CachedReadTests`）。写入不使用这种方式。
- 持久化实体和 SQL 投影中的 `SteamId` 使用 `long`，对应已有的有符号 `BIGINT`。在存储边界转换 ModSharp `SteamID`，不要重新引入自定义数据库类型转换器。
- 成绩写事务先锁地图主键，再按主键升序锁需要更新总分的玩家。积分明细及总分必须在同一个 `ReadCommitted` 事务中提交。
- 保持批量写入、跳过未变化数据；验证数据库并发行为时使用独立 SqlSugar 上下文以及隔离的 MySQL/PostgreSQL 测试库。
