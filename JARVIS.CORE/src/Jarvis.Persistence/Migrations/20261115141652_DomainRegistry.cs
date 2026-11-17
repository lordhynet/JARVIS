using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jarvis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DomainRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DomainApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Decision = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AppliedRevision = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DomainApprovals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DomainAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Actor = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DetailsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DomainAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DomainCapabilityBindings",
                columns: table => new
                {
                    DomainVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CapabilityId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    VersionConstraint = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DomainCapabilityBindings", x => new { x.DomainVersionId, x.CapabilityId });
                });

            migrationBuilder.CreateTable(
                name: "DomainDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    WorkflowKey = table.Column<string>(type: "character varying(90)", maxLength: 90, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LatestVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DomainDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DomainVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    ManifestJson = table.Column<string>(type: "jsonb", nullable: false),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DomainVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DomainVersions_DomainDefinitions_DomainId",
                        column: x => x.DomainId,
                        principalTable: "DomainDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserDomainState",
                columns: table => new
                {
                    SubjectId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    DomainId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    PreferencesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ProgressRef = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserDomainState", x => new { x.SubjectId, x.DomainId });
                    table.ForeignKey(
                        name: "FK_UserDomainState_DomainDefinitions_DomainId",
                        column: x => x.DomainId,
                        principalTable: "DomainDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DomainApprovals_DomainVersionId",
                table: "DomainApprovals",
                column: "DomainVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_DomainApprovals_RequestId",
                table: "DomainApprovals",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DomainAuditEvents_DomainId_OccurredAt",
                table: "DomainAuditEvents",
                columns: new[] { "DomainId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DomainAuditEvents_RequestId",
                table: "DomainAuditEvents",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DomainDefinitions_CurrentVersionId",
                table: "DomainDefinitions",
                column: "CurrentVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_DomainDefinitions_Key",
                table: "DomainDefinitions",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DomainDefinitions_Status",
                table: "DomainDefinitions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DomainDefinitions_WorkflowKey",
                table: "DomainDefinitions",
                column: "WorkflowKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DomainVersions_DomainId_Version",
                table: "DomainVersions",
                columns: new[] { "DomainId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserDomainState_DomainId",
                table: "UserDomainState",
                column: "DomainId");

            migrationBuilder.CreateIndex(
                name: "IX_UserDomainState_Enabled",
                table: "UserDomainState",
                column: "Enabled");

            migrationBuilder.AddForeignKey(
                name: "FK_DomainApprovals_DomainVersions_DomainVersionId",
                table: "DomainApprovals",
                column: "DomainVersionId",
                principalTable: "DomainVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DomainAuditEvents_DomainDefinitions_DomainId",
                table: "DomainAuditEvents",
                column: "DomainId",
                principalTable: "DomainDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DomainCapabilityBindings_DomainVersions_DomainVersionId",
                table: "DomainCapabilityBindings",
                column: "DomainVersionId",
                principalTable: "DomainVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DomainDefinitions_DomainVersions_CurrentVersionId",
                table: "DomainDefinitions",
                column: "CurrentVersionId",
                principalTable: "DomainVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DomainDefinitions_DomainVersions_CurrentVersionId",
                table: "DomainDefinitions");

            migrationBuilder.DropTable(
                name: "DomainApprovals");

            migrationBuilder.DropTable(
                name: "DomainAuditEvents");

            migrationBuilder.DropTable(
                name: "DomainCapabilityBindings");

            migrationBuilder.DropTable(
                name: "UserDomainState");

            migrationBuilder.DropTable(
                name: "DomainVersions");

            migrationBuilder.DropTable(
                name: "DomainDefinitions");
        }
    }
}
