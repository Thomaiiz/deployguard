using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeployGuard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicStatusPage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                table: "applications",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPublic",
                table: "applications");
        }
    }
}
