using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ForgeProtectedRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ForgeCapacity",
                columns: table => new
                {
                    OwnerScope = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProgrammeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpectedRevision = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    KeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    Tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    RetryVersion = table.Column<int>(type: "integer", nullable: false),
                    RetryKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RetryMac = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForgeCapacity", x => new { x.OwnerScope, x.ProgrammeId, x.Id, x.Revision });
                    table.CheckConstraint("CK_ForgeCapacity_Envelope", "\"Revision\">0 AND \"ExpectedRevision\">=0 AND \"ExpectedRevision\"::bigint+1=\"Revision\" AND \"Epoch\">=0\nAND \"Version\"=1 AND \"RetryVersion\"=1 AND octet_length(\"Nonce\")=12 AND octet_length(\"Tag\")=16\nAND octet_length(\"Ciphertext\")<=65536 AND octet_length(\"RetryMac\")=32");
                    table.ForeignKey(
                        name: "FK_ForgeCapacity_ForgeProgrammes_OwnerScope_ProgrammeId",
                        columns: x => new { x.OwnerScope, x.ProgrammeId },
                        principalTable: "ForgeProgrammes",
                        principalColumns: new[] { "OwnerScope", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ForgeCheckIns",
                columns: table => new
                {
                    OwnerScope = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProgrammeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpectedRevision = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    KeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    Tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    RetryVersion = table.Column<int>(type: "integer", nullable: false),
                    RetryKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RetryMac = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForgeCheckIns", x => new { x.OwnerScope, x.ProgrammeId, x.Id, x.Revision });
                    table.CheckConstraint("CK_ForgeCheckIns_Envelope", "\"Revision\">0 AND \"ExpectedRevision\">=0 AND \"ExpectedRevision\"::bigint+1=\"Revision\" AND \"Epoch\">=0\nAND \"Version\"=1 AND \"RetryVersion\"=1 AND octet_length(\"Nonce\")=12 AND octet_length(\"Tag\")=16\nAND octet_length(\"Ciphertext\")<=65536 AND octet_length(\"RetryMac\")=32");
                    table.ForeignKey(
                        name: "FK_ForgeCheckIns_ForgeProgrammes_OwnerScope_ProgrammeId",
                        columns: x => new { x.OwnerScope, x.ProgrammeId },
                        principalTable: "ForgeProgrammes",
                        principalColumns: new[] { "OwnerScope", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ForgeCapacity_OwnerScope_ProgrammeId_RequestId",
                table: "ForgeCapacity",
                columns: new[] { "OwnerScope", "ProgrammeId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForgeCheckIns_OwnerScope_ProgrammeId_RequestId",
                table: "ForgeCheckIns",
                columns: new[] { "OwnerScope", "ProgrammeId", "RequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never silently discard private ciphertext or historical retry results on downgrade.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "ForgeCheckIns") OR EXISTS (SELECT 1 FROM "ForgeCapacity") THEN
                        RAISE EXCEPTION 'Protected Forge records remain. Preserve a verified protected export and key recovery path, then explicitly remove these records before downgrade.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "ForgeCapacity");

            migrationBuilder.DropTable(
                name: "ForgeCheckIns");
        }
    }
}
