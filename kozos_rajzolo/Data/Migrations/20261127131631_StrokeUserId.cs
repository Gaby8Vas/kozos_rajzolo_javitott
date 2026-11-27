using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KozosRajzolo.Data.Migrations
{
    /// <inheritdoc />
    public partial class StrokeUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "Strokes",
                type: "varchar(450)",
                maxLength: 450,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Strokes_RoomCode_UserId",
                table: "Strokes",
                columns: new[] { "RoomCode", "UserId" });

            // Régi vonalak: a tárolt e-mail-cím alapján kitöltjük a fiókazonosítót,
            // és a szerzőnév helyére a megjelenített név kerül.
            migrationBuilder.Sql(
                "UPDATE `Strokes` s JOIN `AspNetUsers` u ON u.`UserName` = s.`UserName` " +
                "SET s.`UserId` = u.`Id`, s.`UserName` = LEFT(COALESCE(NULLIF(u.`DisplayName`, ''), u.`UserName`), 60) " +
                "WHERE s.`UserId` IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Strokes_RoomCode_UserId",
                table: "Strokes");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "Strokes");
        }
    }
}
