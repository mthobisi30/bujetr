using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CognitiveBudget.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SharedBudgets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedBudgets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SharedBudgetMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SharedBudgetId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    InvitedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    InvitedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedBudgetMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharedBudgetMembers_SharedBudgets_SharedBudgetId",
                        column: x => x.SharedBudgetId,
                        principalTable: "SharedBudgets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SharedExpenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SharedBudgetId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaidByUserId = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedExpenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharedExpenses_SharedBudgets_SharedBudgetId",
                        column: x => x.SharedBudgetId,
                        principalTable: "SharedBudgets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SharedExpenseShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SharedExpenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedExpenseShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharedExpenseShares_SharedExpenses_SharedExpenseId",
                        column: x => x.SharedExpenseId,
                        principalTable: "SharedExpenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SharedBudgetMembers_InvitedEmail",
                table: "SharedBudgetMembers",
                column: "InvitedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_SharedBudgetMembers_SharedBudgetId_InvitedEmail",
                table: "SharedBudgetMembers",
                columns: new[] { "SharedBudgetId", "InvitedEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SharedBudgetMembers_UserId",
                table: "SharedBudgetMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SharedBudgets_OwnerId",
                table: "SharedBudgets",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_SharedExpenses_SharedBudgetId",
                table: "SharedExpenses",
                column: "SharedBudgetId");

            migrationBuilder.CreateIndex(
                name: "IX_SharedExpenseShares_SharedExpenseId",
                table: "SharedExpenseShares",
                column: "SharedExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_SharedExpenseShares_UserId",
                table: "SharedExpenseShares",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SharedBudgetMembers");

            migrationBuilder.DropTable(
                name: "SharedExpenseShares");

            migrationBuilder.DropTable(
                name: "SharedExpenses");

            migrationBuilder.DropTable(
                name: "SharedBudgets");
        }
    }
}
