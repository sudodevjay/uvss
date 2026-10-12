using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UvssService.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSimulationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SimulatedFrameHeightPx",
                table: "UvssCameras");

            migrationBuilder.DropColumn(
                name: "SimulatedFramesPerSecond",
                table: "UvssCameras");

            migrationBuilder.DropColumn(
                name: "TestImagePath",
                table: "UvssCameras");

            migrationBuilder.DropColumn(
                name: "TestImageDir",
                table: "DriverCameras");

            migrationBuilder.DropColumn(
                name: "SimulatedIdleSeconds",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "SimulatedSegmentSeconds",
                table: "Controllers");

            migrationBuilder.DropColumn(
                name: "TestImageDir",
                table: "AnprCameras");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
                name: "TestImagePath",
                table: "UvssCameras",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "TestImageDir",
                table: "DriverCameras",
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

            migrationBuilder.AddColumn<string>(
                name: "TestImageDir",
                table: "AnprCameras",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");
        }
    }
}
