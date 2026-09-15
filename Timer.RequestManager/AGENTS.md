# 数据库实现约束

- 数据库操作使用 SqlSugar 的强类型查询、写入、事务和维护 API。禁止手写或拼接 Raw SQL，包括锁、upsert、索引和迁移；也不要用字符串谓词、`SqlFunc.Mapping` 或修改生成的 SQL 绕过此限制。
- `Ado` 仅用于事务管理和事务状态检查。测试可以检查 ORM 生成的 SQL。
- 持久化实体和 SQL 投影中的 `SteamId` 使用 `long`，对应已有的有符号 `BIGINT`。在存储边界转换 ModSharp `SteamID`，不要重新引入自定义数据库类型转换器。
- 成绩写事务先锁地图主键，再按主键升序锁需要更新总分的玩家。积分明细及总分必须在同一个 `ReadCommitted` 事务中提交。
- 保持批量写入、跳过未变化数据；验证数据库并发行为时使用独立 SqlSugar 上下文以及隔离的 MySQL/PostgreSQL 测试库。
