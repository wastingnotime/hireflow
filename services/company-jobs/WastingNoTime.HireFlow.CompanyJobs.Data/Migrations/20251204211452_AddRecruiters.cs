using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WastingNoTime.HireFlow.CompanyJobs.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecruiters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RecruiterId",
                schema: "companyjobs",
                table: "jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "recruiters",
                schema: "companyjobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recruiters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recruiters_companies_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "companyjobs",
                        principalTable: "companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_RecruiterId",
                schema: "companyjobs",
                table: "jobs",
                column: "RecruiterId");

            migrationBuilder.CreateIndex(
                name: "IX_recruiters_CompanyId_Email",
                schema: "companyjobs",
                table: "recruiters",
                columns: new[] { "CompanyId", "Email" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_jobs_recruiters_RecruiterId",
                schema: "companyjobs",
                table: "jobs",
                column: "RecruiterId",
                principalSchema: "companyjobs",
                principalTable: "recruiters",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_jobs_recruiters_RecruiterId",
                schema: "companyjobs",
                table: "jobs");

            migrationBuilder.DropTable(
                name: "recruiters",
                schema: "companyjobs");

            migrationBuilder.DropIndex(
                name: "IX_jobs_RecruiterId",
                schema: "companyjobs",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "RecruiterId",
                schema: "companyjobs",
                table: "jobs");
        }
    }
}
