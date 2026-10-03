using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewCoach.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPracticeAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PracticeAttempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Question = table.Column<string>(type: "TEXT", nullable: false),
                    QuestionType = table.Column<string>(type: "TEXT", nullable: false),
                    Technology = table.Column<string>(type: "TEXT", nullable: false),
                    Seniority = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    IsFollowUp = table.Column<bool>(type: "INTEGER", nullable: false),
                    ParentQuestion = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileName = table.Column<string>(type: "TEXT", nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    AnswerText = table.Column<string>(type: "TEXT", nullable: false),
                    InputMethod = table.Column<string>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    WordCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CoachJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PracticeAttempts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PracticeAttempts_CreatedAt",
                table: "PracticeAttempts",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PracticeAttempts");
        }
    }
}
