using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DomainApprovalRisk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RiskClass",
                table: "DomainApprovals",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "DomainApprovals" AS approval
                SET "RiskClass" = CASE
                    WHEN version."ManifestJson"::text ~* '\m(law|legal|court|litigation|solicitor|jurisdiction)\M' THEN 2
                    WHEN version."ManifestJson"::text ~* '\m(health|medical|clinical|diagnosis|treatment|medicine)\M' THEN 3
                    WHEN version."ManifestJson"::text ~* '\m(finance|financial|investment|trading|stocks|tax|banking)\M' THEN 1
                    ELSE 0
                END
                FROM "DomainVersions" AS version
                WHERE version."Id" = approval."DomainVersionId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RiskClass",
                table: "DomainApprovals");
        }
    }
}
