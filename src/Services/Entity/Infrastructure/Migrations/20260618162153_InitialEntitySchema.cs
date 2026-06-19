using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ThreeCommerce.Entity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialEntitySchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "entity");

        migrationBuilder.CreateTable(
            name: "Entities",
            schema: "entity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_Entities", x => x.Id));

        migrationBuilder.Sql("""
            ALTER TABLE entity."Entities" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE entity."Entities" FORCE ROW LEVEL SECURITY;
            CREATE POLICY "TenantIsolation_Entities" ON entity."Entities"
                USING (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = current_setting('app.tenant_id', true)::uuid)
                WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = current_setting('app.tenant_id', true)::uuid);
            """);

        migrationBuilder.CreateTable(
            name: "InboxState",
            schema: "entity",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                ConsumerId = table.Column<Guid>(type: "uuid", nullable: false),
                LockId = table.Column<Guid>(type: "uuid", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                Received = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ReceiveCount = table.Column<int>(type: "integer", nullable: false),
                ExpirationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Consumed = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastSequenceNumber = table.Column<long>(type: "bigint", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InboxState", x => x.Id);
                table.UniqueConstraint("AK_InboxState_MessageId_ConsumerId", x => new { x.MessageId, x.ConsumerId });
            });

        migrationBuilder.CreateTable(
            name: "OutboxState",
            schema: "entity",
            columns: table => new
            {
                OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                LockId = table.Column<Guid>(type: "uuid", nullable: false),
                RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastSequenceNumber = table.Column<long>(type: "bigint", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_OutboxState", x => x.OutboxId));

        migrationBuilder.CreateTable(
            name: "OutboxMessage",
            schema: "entity",
            columns: table => new
            {
                SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                EnqueueTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                SentTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Headers = table.Column<string>(type: "text", nullable: true),
                Properties = table.Column<string>(type: "text", nullable: true),
                InboxMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                InboxConsumerId = table.Column<Guid>(type: "uuid", nullable: true),
                OutboxId = table.Column<Guid>(type: "uuid", nullable: true),
                MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                ContentType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                MessageType = table.Column<string>(type: "text", nullable: false),
                Body = table.Column<string>(type: "text", nullable: false),
                ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                CorrelationId = table.Column<Guid>(type: "uuid", nullable: true),
                InitiatorId = table.Column<Guid>(type: "uuid", nullable: true),
                RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                SourceAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                DestinationAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ResponseAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                FaultAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ExpirationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxMessage", x => x.SequenceNumber);
                table.ForeignKey(
                    name: "FK_OutboxMessage_InboxState_InboxMessageId_InboxConsumerId",
                    columns: x => new { x.InboxMessageId, x.InboxConsumerId },
                    principalSchema: "entity",
                    principalTable: "InboxState",
                    principalColumns: new[] { "MessageId", "ConsumerId" });
                table.ForeignKey(
                    name: "FK_OutboxMessage_OutboxState_OutboxId",
                    column: x => x.OutboxId,
                    principalSchema: "entity",
                    principalTable: "OutboxState",
                    principalColumn: "OutboxId");
            });

        migrationBuilder.CreateIndex("IX_Entities_TenantId_DisplayName", "Entities", new[] { "TenantId", "DisplayName" }, schema: "entity");
        migrationBuilder.CreateIndex("IX_Entities_TenantId_Status", "Entities", new[] { "TenantId", "Status" }, schema: "entity");
        migrationBuilder.CreateIndex("IX_InboxState_Delivered", "InboxState", "Delivered", schema: "entity");
        migrationBuilder.CreateIndex("IX_OutboxMessage_EnqueueTime", "OutboxMessage", "EnqueueTime", schema: "entity");
        migrationBuilder.CreateIndex("IX_OutboxMessage_ExpirationTime", "OutboxMessage", "ExpirationTime", schema: "entity");
        migrationBuilder.CreateIndex(
            "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber",
            "OutboxMessage",
            new[] { "InboxMessageId", "InboxConsumerId", "SequenceNumber" },
            unique: true,
            schema: "entity");
        migrationBuilder.CreateIndex(
            "IX_OutboxMessage_OutboxId_SequenceNumber",
            "OutboxMessage",
            new[] { "OutboxId", "SequenceNumber" },
            unique: true,
            schema: "entity");
        migrationBuilder.CreateIndex("IX_OutboxState_Created", "OutboxState", "Created", schema: "entity");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP POLICY IF EXISTS \"TenantIsolation_Entities\" ON entity.\"Entities\";");
        migrationBuilder.DropTable(name: "Entities", schema: "entity");
        migrationBuilder.DropTable(name: "OutboxMessage", schema: "entity");
        migrationBuilder.DropTable(name: "InboxState", schema: "entity");
        migrationBuilder.DropTable(name: "OutboxState", schema: "entity");
    }
}
