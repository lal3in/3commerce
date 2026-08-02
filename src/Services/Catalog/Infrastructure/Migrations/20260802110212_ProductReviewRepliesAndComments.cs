using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class ProductReviewRepliesAndComments : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ProductReviews_ProductId_UserId",
            schema: "catalog",
            table: "ProductReviews");

        migrationBuilder.AlterColumn<int>(
            name: "Rating",
            schema: "catalog",
            table: "ProductReviews",
            type: "integer",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "integer");

        migrationBuilder.AddColumn<Guid>(
            name: "ParentId",
            schema: "catalog",
            table: "ProductReviews",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_ProductReviews_ParentId",
            schema: "catalog",
            table: "ProductReviews",
            column: "ParentId");

        migrationBuilder.CreateIndex(
            name: "IX_ProductReviews_ProductId_UserId",
            schema: "catalog",
            table: "ProductReviews",
            columns: new[] { "ProductId", "UserId" },
            unique: true,
            filter: "\"ParentId\" IS NULL AND \"Rating\" IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ProductReviews_ParentId",
            schema: "catalog",
            table: "ProductReviews");

        migrationBuilder.DropIndex(
            name: "IX_ProductReviews_ProductId_UserId",
            schema: "catalog",
            table: "ProductReviews");

        migrationBuilder.DropColumn(
            name: "ParentId",
            schema: "catalog",
            table: "ProductReviews");

        migrationBuilder.AlterColumn<int>(
            name: "Rating",
            schema: "catalog",
            table: "ProductReviews",
            type: "integer",
            nullable: false,
            defaultValue: 0,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_ProductReviews_ProductId_UserId",
            schema: "catalog",
            table: "ProductReviews",
            columns: new[] { "ProductId", "UserId" },
            unique: true);
    }
}
