using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LearningCreationReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "RuntimeLearningRows" WHERE "Kind"='CreationReceipts') THEN
                        RAISE EXCEPTION 'Refusing creation-receipt downgrade while receipts remain';
                    END IF;
                END $$;
                """);
        }
    }
}
