using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewCoach.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMockSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MockSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileName = table.Column<string>(type: "TEXT", nullable: false),
                    JobRole = table.Column<string>(type: "TEXT", nullable: false),
                    RoundType = table.Column<string>(type: "TEXT", nullable: false),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ElapsedSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Finished = table.Column<bool>(type: "INTEGER", nullable: false),
                    PlanJson = table.Column<string>(type: "TEXT", nullable: false),
                    TurnsJson = table.Column<string>(type: "TEXT", nullable: false),
                    DebriefJson = table.Column<string>(type: "TEXT", nullable: false),
                    ThreadsJson = table.Column<string>(type: "TEXT", nullable: false),
                    HireSignal = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MockSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MockSessions_StartedAt",
                table: "MockSessions",
                column: "StartedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MockSessions");
        }
    }
}
