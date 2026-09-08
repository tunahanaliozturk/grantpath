using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GrantPath.Data.Migrations
{
    /// <summary>
    /// Puts the authorization decision inside the database, where it applies to every statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The application already refuses to serve a document the subject may not read. This migration makes
    /// that true of the database as well, so a reporting job, an ad hoc query, or a code path that forgets
    /// to call the facade is bounded by the same decision. Application-layer discipline is a convention;
    /// a policy on the table is a rule.
    /// </para>
    /// <para>
    /// FORCE is deliberate. Without it the table owner bypasses its own policies, and since the service
    /// connects as the owner, the protection would be decorative. The write policies are separate and
    /// unrestricted: what may be written is the facade's decision, and adding a second opinion here would
    /// mean the projection has to be current before anything can be inserted.
    /// </para>
    /// </remarks>
    public partial class RowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                -- A role that can read the content tables and nothing else, and that carries neither
                -- SUPERUSER nor BYPASSRLS. Postgres exempts both from every policy, so a service that
                -- queries as one has row-level security that reads correctly in the schema and enforces
                -- nothing at all. Queries that are meant to be bounded switch to this role first.
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'grantpath_reader') THEN
                        CREATE ROLE grantpath_reader NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT;
                    END IF;
                END
                $$;

                GRANT USAGE ON SCHEMA public TO grantpath_reader;
                GRANT SELECT ON documents TO grantpath_reader;
                GRANT SELECT ON document_permission_cache TO grantpath_reader;
                GRANT grantpath_reader TO CURRENT_USER;

                ALTER TABLE documents ENABLE ROW LEVEL SECURITY;
                ALTER TABLE documents FORCE ROW LEVEL SECURITY;

                -- A row is visible only if the projection says this subject may view it. The setting is
                -- read with missing_ok, so a session that never named a subject sees nothing rather than
                -- failing: deny by default, at the database as well.
                CREATE POLICY documents_visible_to_subject ON documents
                    FOR SELECT
                    USING (
                        EXISTS (
                            SELECT 1
                            FROM document_permission_cache cache
                            WHERE cache.document_id = documents.id
                              AND cache.subject = current_setting('grantpath.subject', true)
                        )
                    );

                CREATE POLICY documents_insert ON documents FOR INSERT WITH CHECK (true);
                CREATE POLICY documents_update ON documents FOR UPDATE USING (true) WITH CHECK (true);
                CREATE POLICY documents_delete ON documents FOR DELETE USING (true);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS documents_delete ON documents;
                DROP POLICY IF EXISTS documents_update ON documents;
                DROP POLICY IF EXISTS documents_insert ON documents;
                DROP POLICY IF EXISTS documents_visible_to_subject ON documents;

                ALTER TABLE documents NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE documents DISABLE ROW LEVEL SECURITY;

                REVOKE ALL ON document_permission_cache FROM grantpath_reader;
                REVOKE ALL ON documents FROM grantpath_reader;
                REVOKE USAGE ON SCHEMA public FROM grantpath_reader;
                """);
        }
    }
}
