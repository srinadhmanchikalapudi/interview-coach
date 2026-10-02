using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewCoach.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLearnHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LearnHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    QuestionKey = table.Column<string>(type: "TEXT", nullable: false),
                    Question = table.Column<string>(type: "TEXT", nullable: false),
                    QuestionType = table.Column<string>(type: "TEXT", nullable: false),
                    Technology = table.Column<string>(type: "TEXT", nullable: false),
                    Seniority = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    IsFollowUp = table.Column<bool>(type: "INTEGER", nullable: false),
                    ParentQuestion = table.Column<string>(type: "TEXT", nullable: false),
                    ParentKey = table.Column<string>(type: "TEXT", nullable: false),
                    IsGeneral = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProfileName = table.Column<string>(type: "TEXT", nullable: false),
                    CoachJson = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TimesSeen = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LearnHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LearnHistory_LastSeenAt",
                table: "LearnHistory",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_LearnHistory_QuestionKey_ParentKey_IsGeneral_ProfileName",
                table: "LearnHistory",
                columns: new[] { "QuestionKey", "ParentKey", "IsGeneral", "ProfileName" },
                unique: true);

            // Technical questions already saved in the technology bank were shown in earlier sessions, so they start the
            // library off. Only questions that have a saved answer count as seen (a question written in advance but never
            // shown has none). The answer preferred is the "interviewer norm" one, else the newest. OR IGNORE skips the rare
            // case of the same question stored under two technologies or levels.
            migrationBuilder.Sql(
                @"INSERT OR IGNORE INTO ""LearnHistory""
                    (""QuestionKey"", ""Question"", ""QuestionType"", ""Technology"", ""Seniority"", ""Source"", ""IsFollowUp"", ""ParentQuestion"", ""ParentKey"",
                     ""IsGeneral"", ""ProfileName"", ""CoachJson"", ""FirstSeenAt"", ""LastSeenAt"", ""TimesSeen"")
                  SELECT q.""QuestionKey"", q.""Question"", 'technical_concept', q.""Technology"", q.""Seniority"", 'fundamentals', 0, '', '',
                         1, '',
                         (SELECT a.""CoachJson"" FROM ""TechAnswers"" a WHERE a.""TechQuestionId"" = q.""Id""
                          ORDER BY (a.""AnswerWords"" = -1) DESC, a.""CreatedAt"" DESC LIMIT 1),
                         q.""CreatedAt"", q.""CreatedAt"", 1
                  FROM ""TechQuestions"" q
                  WHERE EXISTS (SELECT 1 FROM ""TechAnswers"" a WHERE a.""TechQuestionId"" = q.""Id"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LearnHistory");
        }
    }
}
