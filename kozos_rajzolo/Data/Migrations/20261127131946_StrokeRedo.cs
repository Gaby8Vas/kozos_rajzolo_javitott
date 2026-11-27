using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KozosRajzolo.Data.Migrations
{
    /// <inheritdoc />
    public partial class StrokeRedo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "UndoneAtUtc",
                table: "Strokes",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UndoneAtUtc",
                table: "Strokes");
        }
    }
}
