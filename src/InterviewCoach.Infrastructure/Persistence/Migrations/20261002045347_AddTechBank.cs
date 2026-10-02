using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewCoach.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTechBank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JdTechnologies",
                columns: table => new
                {
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    TechnologiesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JdTechnologies", x => x.Fingerprint);
                });

            migrationBuilder.CreateTable(
                name: "TechQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TechnologyKey = table.Column<string>(type: "TEXT", nullable: false),
                    Technology = table.Column<string>(type: "TEXT", nullable: false),
                    Seniority = table.Column<string>(type: "TEXT", nullable: false),
                    QuestionKey = table.Column<string>(type: "TEXT", nullable: false),
                    Question = table.Column<string>(type: "TEXT", nullable: false),
                    Focus = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TechQuestions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TechAnswers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TechQuestionId = table.Column<int>(type: "INTEGER", nullable: false),
                    AnswerWords = table.Column<int>(type: "INTEGER", nullable: false),
                    CoachJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TechAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TechAnswers_TechQuestions_TechQuestionId",
                        column: x => x.TechQuestionId,
                        principalTable: "TechQuestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TechAnswers_TechQuestionId_AnswerWords",
                table: "TechAnswers",
                columns: new[] { "TechQuestionId", "AnswerWords" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TechQuestions_TechnologyKey_Seniority_QuestionKey",
                table: "TechQuestions",
                columns: new[] { "TechnologyKey", "Seniority", "QuestionKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JdTechnologies");

            migrationBuilder.DropTable(
                name: "TechAnswers");

            migrationBuilder.DropTable(
                name: "TechQuestions");
        }
    }
}
