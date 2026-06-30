using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CognitiveBudget.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantKeywordDropTriggerTransaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TriggerTransactions");

            migrationBuilder.AlterColumn<string>(
                name: "Category",
                table: "CommitmentDevices",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MerchantKeyword",
                table: "CommitmentDevices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MerchantKeyword",
                table: "CommitmentDevices");

            migrationBuilder.AlterColumn<string>(
                name: "Category",
                table: "CommitmentDevices",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "TriggerTransactions",
                columns: table => new
                {
                    SpendingTriggerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerTransactions", x => new { x.SpendingTriggerId, x.TransactionId });
                    table.ForeignKey(
                        name: "FK_TriggerTransactions_SpendingTriggers_SpendingTriggerId",
                        column: x => x.SpendingTriggerId,
                        principalTable: "SpendingTriggers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TriggerTransactions_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerTransactions_TransactionId",
                table: "TriggerTransactions",
                column: "TransactionId");
        }
    }
}
