using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UvssService.Migrations
{
    /// <inheritdoc />
    public partial class MoveLaneConfigToDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SimulatedFrameHeightPx",
                table: "UvssCameras",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SimulatedFramesPerSecond",
                table: "UvssCameras",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "UvssCameras",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "TestImagePath",
                table: "UvssCameras",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "DriverCameras",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "DriverCameras",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "TestImageDir",
                table: "DriverCameras",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "DebounceMs",
                table: "Controllers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "Controllers",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "InterlockSource",
                table: "Controllers",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "LoopPositionsMetres",
                table: "Controllers",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<double>(
                name: "SimulatedIdleSeconds",
                table: "Controllers",
                type: "double",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "SimulatedSegmentSeconds",
                table: "Controllers",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "TcpPort",
                table: "Controllers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "AnprCameras",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "AnprCameras",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "TestImageDir",
                table: "AnprCameras",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "OverviewCameras",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    LaneId = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CameraName = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Ip = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Username = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Password = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Source = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TestImageDir = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OverviewCameras", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OverviewCameras");

            migrationBuilder.DropColumn(
                name: "SimulatedFrameHeightPx",
                table: "UvssCameras");

            migrationBuilder.DropColumn(
                name: "SimulatedFramesPerSecond",
                table: "UvssCameras");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "UvssCameras");

            migrationBuilder.DropColumn(
                name: "TestImagePath",
                table: "UvssCameras");

            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "DriverCameras");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "DriverCameras");

            migrationBuilder.DropColumn(
                name: "TestImageDir",
                table: "DriverCameras");

            migrationBuilder.DropColumn(
                name: "DebounceMs",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "InterlockSource",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "LoopPositionsMetres",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "SimulatedIdleSeconds",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "SimulatedSegmentSeconds",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "TcpPort",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "AnprCameras");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "AnprCameras");

            migrationBuilder.DropColumn(
                name: "TestImageDir",
                table: "AnprCameras");
        }
    }
}
