using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CognitiveBudget.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBillAutoPay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoPay",
                table: "Bills",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoPay",
                table: "Bills");
        }
    }
}
