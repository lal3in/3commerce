using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <summary>
/// Search schema (ADR-0020): weighted generated tsvector + GIN indexes, kept out
/// of the EF model on purpose — search SQL owns these columns.
/// pg_trgm/citext extensions are created by infra/postgres/init-databases.sql
/// (superuser); guarded here for environments that pre-provision them.
/// </summary>
public partial class SearchSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "Products" ADD COLUMN search_vector tsvector
            GENERATED ALWAYS AS (
                setweight(to_tsvector('english', coalesce("Title", '')), 'A') ||
                setweight(to_tsvector('english', coalesce("Brand", '')), 'B') ||
                setweight(to_tsvector('english', coalesce("Description", '')), 'C')
            ) STORED;
            """);

        migrationBuilder.Sql("""
            CREATE INDEX "IX_Products_search_vector" ON "Products" USING GIN (search_vector);
            """);

        migrationBuilder.Sql("""
            CREATE INDEX "IX_Products_Title_trgm" ON "Products" USING GIN ("Title" gin_trgm_ops);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Products_Title_trgm";""");
        migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Products_search_vector";""");
        migrationBuilder.Sql("""ALTER TABLE "Products" DROP COLUMN IF EXISTS search_vector;""");
    }
}
