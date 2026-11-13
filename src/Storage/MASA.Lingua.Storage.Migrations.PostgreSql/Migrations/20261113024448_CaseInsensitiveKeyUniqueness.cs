using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MASA.Lingua.Storage.Migrations.PostgreSql.Migrations
{
    /// <summary>
    /// 命名空间编码与词条 Key 改为「忽略大小写唯一」：
    /// <c>translation_entries</c> 的 (namespace_id, key)、<c>translation_namespaces</c> 的 (project_id, code) 与共享命名空间的 (code)
    /// 三条唯一索引改成对 <c>lower(...)</c> 建的表达式唯一索引（<c>Confirm</c> 与 <c>confirm</c> 是同一个词条 / 命名空间）。
    /// <para>
    /// EF Core 的 Fluent API 无法声明表达式索引，所以模型（及快照）里这三条索引保持原样（同名、同列），
    /// 这里用原生 SQL 把它们在库里替换成表达式版本。索引名不变，因此模型与快照不会出现差异。
    /// </para>
    /// <para>
    /// 升级前库里若已经存在「仅大小写不同」的重复项，新的唯一索引无法建立：迁移会先检查，
    /// 并以列出重复项的错误信息中止（<b>不会修改或删除任何数据</b>），由维护者决定保留哪一个、合并或删除其余的，再重新启动即可。
    /// </para>
    /// </summary>
    public partial class CaseInsensitiveKeyUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) 先检查已有数据：有仅大小写不同的重复项就中止，并把要处理的项目逐条列出来（最多各 20 组）
            migrationBuilder.Sql("""
                DO $lingua$
                DECLARE
                    ns_total integer;
                    ns_list text;
                    key_total integer;
                    key_list text;
                BEGIN
                    SELECT count(*) INTO ns_total FROM (
                        SELECT 1 FROM translation_namespaces
                        GROUP BY project_id, lower(code)
                        HAVING count(*) > 1) t;

                    SELECT string_agg(format('  [%s] %s', scope, variants), E'\n' ORDER BY scope, variants) INTO ns_list FROM (
                        SELECT coalesce(p.code, '共享命名空间') AS scope,
                               string_agg('"' || n.code || '"', ' / ' ORDER BY n.code) AS variants
                        FROM translation_namespaces n
                        LEFT JOIN projects p ON p.id = n.project_id
                        GROUP BY n.project_id, p.code, lower(n.code)
                        HAVING count(*) > 1
                        ORDER BY 1, 2
                        LIMIT 20) d;

                    SELECT count(*) INTO key_total FROM (
                        SELECT 1 FROM translation_entries
                        GROUP BY namespace_id, lower("key")
                        HAVING count(*) > 1) t;

                    SELECT string_agg(format('  [%s / %s] %s', scope, ns_code, variants), E'\n' ORDER BY scope, ns_code, variants) INTO key_list FROM (
                        SELECT coalesce(p.code, '共享命名空间') AS scope,
                               CASE WHEN n.code = '' THEN '(项目根)' ELSE n.code END AS ns_code,
                               string_agg('"' || e."key" || '"', ' / ' ORDER BY e."key") AS variants
                        FROM translation_entries e
                        JOIN translation_namespaces n ON n.id = e.namespace_id
                        LEFT JOIN projects p ON p.id = n.project_id
                        GROUP BY e.namespace_id, n.code, p.code, lower(e."key")
                        HAVING count(*) > 1
                        ORDER BY 1, 2, 3
                        LIMIT 20) d;

                    IF ns_total > 0 OR key_total > 0 THEN
                        RAISE EXCEPTION E'命名空间 / Key 已改为忽略大小写唯一，但数据库里还有仅大小写不同的重复项，升级已中止（没有修改任何数据）。\n请在管理后台或直接用 SQL 保留其中一个、合并或删除其余的，然后重新启动。\n仅大小写不同的命名空间：% 组（最多列出 20 组）\n%\n仅大小写不同的词条：% 组（最多列出 20 组）\n%',
                            ns_total, coalesce(ns_list, '  （无）'), key_total, coalesce(key_list, '  （无）');
                    END IF;
                END
                $lingua$;
                """);

            // 2) 用表达式唯一索引替换原来的唯一索引（同名，模型里的声明不变）
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_translation_entries_namespace_id_key";
                CREATE UNIQUE INDEX "IX_translation_entries_namespace_id_key"
                    ON translation_entries (namespace_id, lower("key"));

                DROP INDEX IF EXISTS "IX_translation_namespaces_project_id_code";
                CREATE UNIQUE INDEX "IX_translation_namespaces_project_id_code"
                    ON translation_namespaces (project_id, lower(code))
                    WHERE project_id IS NOT NULL;

                DROP INDEX IF EXISTS "IX_translation_namespaces_code";
                CREATE UNIQUE INDEX "IX_translation_namespaces_code"
                    ON translation_namespaces (lower(code))
                    WHERE project_id IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_translation_entries_namespace_id_key";
                CREATE UNIQUE INDEX "IX_translation_entries_namespace_id_key"
                    ON translation_entries (namespace_id, "key");

                DROP INDEX IF EXISTS "IX_translation_namespaces_project_id_code";
                CREATE UNIQUE INDEX "IX_translation_namespaces_project_id_code"
                    ON translation_namespaces (project_id, code)
                    WHERE project_id IS NOT NULL;

                DROP INDEX IF EXISTS "IX_translation_namespaces_code";
                CREATE UNIQUE INDEX "IX_translation_namespaces_code"
                    ON translation_namespaces (code)
                    WHERE project_id IS NULL;
                """);
        }
    }
}
