using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LearningReviewCardIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX "IX_RuntimeLearningRows_ReviewCard"
                ON "RuntimeLearningRows" (lower("Json"->'Event'->>'LearningItemId'))
                WHERE "Kind"='Reviews';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX "IX_RuntimeLearningRows_ReviewCard";""");
        }
    }
}
