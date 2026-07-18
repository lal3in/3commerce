using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Entity;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Entity.Domain;
using ThreeCommerce.Entity.Infrastructure;

namespace ThreeCommerce.Entity.Api.Endpoints;

public static class EntityEndpoints
{
    public static RouteGroupBuilder MapEntities(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/entities")
            .WithTags("Entities")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/", List)
            .WithSummary("List tenant-scoped entity records.");
        group.MapPost("/", Create)
            .WithSummary("Create a minimal tenant-scoped entity record.");
        group.MapPut("/{id:guid}", Update)
            .WithSummary("Update an entity's legal/trading name.");
        group.MapGet("/{id:guid}", GetDetail)
            .WithSummary("Get an entity with its identifiers, contacts and addresses.");
        group.MapPost("/{id:guid}/identifiers", AddIdentifier)
            .WithSummary("Add a tax/registration identifier (ABN, ACN, GST, other).");
        group.MapPost("/{id:guid}/identifiers/{identifierId:guid}/verify", VerifyIdentifier)
            .WithSummary("Mark an identifier verified (operator attestation).");
        group.MapPost("/{id:guid}/contacts", AddContact)
            .WithSummary("Add a contact method (email/phone/website) with a purpose.");
        group.MapPost("/{id:guid}/addresses", AddAddress)
            .WithSummary("Add a current address for a given purpose.");
        group.MapPost("/{id:guid}/suppliers", StartSupplierOnboarding)
            .WithSummary("Start supplier onboarding for an entity.");
        group.MapGet("/{id:guid}/suppliers/readiness", CheckSupplierReadiness)
            .WithSummary("Check supplier onboarding readiness.");
        group.MapPost("/{id:guid}/suppliers/submit-verification", SubmitSupplierVerification)
            .WithSummary("Submit supplier onboarding for verification.");
        group.MapPost("/{id:guid}/suppliers/verification-complete", MarkSupplierVerificationComplete)
            .WithSummary("Mark supplier verification complete.");
        group.MapPost("/{id:guid}/suppliers/activate", ActivateSupplier)
            .WithSummary("Activate a verified supplier.");
        group.MapPost("/{id:guid}/suppliers/suspend", SuspendSupplier)
            .WithSummary("Suspend an active supplier.");
        group.MapPost("/{id:guid}/suppliers/archive", ArchiveSupplier)
            .WithSummary("Archive supplier onboarding.");
        group.MapPost("/{id:guid}/duplicate-warnings/scan", ScanDuplicates)
            .WithSummary("Scan an entity record for duplicate warnings.");
        group.MapPost("/duplicate-warnings/{warningId:guid}/override", OverrideDuplicateWarning)
            .WithSummary("Override a duplicate warning with a reason.");
        group.MapPost("/{id:guid}/suppliers/change-requests", OpenSupplierChangeRequest)
            .WithSummary("Raise a supplier change request (user/contact/bank).");
        group.MapGet("/suppliers/change-requests", ListSupplierChangeRequests)
            .WithSummary("List supplier change requests for the tenant.");
        group.MapPost("/suppliers/change-requests/{requestId:guid}/approve", ApproveSupplierChangeRequest)
            .WithSummary("Approve a pending supplier change request (maker-checker).");
        group.MapPost("/suppliers/change-requests/{requestId:guid}/reject", RejectSupplierChangeRequest)
            .WithSummary("Reject a pending supplier change request (maker-checker).");
        group.MapPost("/{id:guid}/customer-links", LinkCustomer)
            .WithSummary("Link a tenant customer to this entity with a typed role.");
        group.MapGet("/{id:guid}/customer-links", ListEntityCustomerLinks)
            .WithSummary("List customers linked to this entity.");
        group.MapGet("/customer-links", ListCustomerLinks)
            .WithSummary("List entities a customer is linked to.");
        group.MapPost("/customer-links/{linkId:guid}/unlink", UnlinkCustomer)
            .WithSummary("End a customer↔entity link (preserves history).");
        group.MapDelete("/{id:guid}", Archive)
            .WithSummary("Archive an entity record.");

        return group;
    }

    private static async Task<Ok<List<EntitySummaryResponse>>> List(
        Guid tenantId,
        EntityDbContext db,
        CancellationToken cancellationToken)
    {
        var records = await db.Entities.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Status != EntityRecordStatus.Archived)
            .OrderBy(e => e.DisplayName)
            .Select(e => new EntitySummaryResponse(e.Id, e.TenantId, e.Type, e.LegalName, e.TradingName, e.DisplayName, e.Status, e.CreatedAt, e.UpdatedAt))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(records);
    }

    private static async Task<Results<Created<EntitySummaryResponse>, ValidationProblem>> Create(
        CreateEntityRequest request,
        EntityDbContext db,
        IPublishEndpoint publish,
        AuditRecorder audit,
        ClaimsPrincipal user,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow();
            var entity = EntityRecord.Create(request.TenantId, request.Type, request.LegalName, request.TradingName, now, request.Roles ?? []);
            db.Entities.Add(entity);
            await publish.Publish(new EntityRecordCreated(entity.Id, entity.TenantId, entity.DisplayName, now), cancellationToken);
            await audit.RecordAsync(user.Mutation(
                entity.TenantId, "Entity", entity.Id.ToString(), "entity.entity.create", entity.DisplayName), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            return TypedResults.Created(
                $"/entities/{entity.Id}",
                new EntitySummaryResponse(entity.Id, entity.TenantId, entity.Type, entity.LegalName, entity.TradingName, entity.DisplayName, entity.Status, entity.CreatedAt, entity.UpdatedAt));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.LegalName)] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<EntitySummaryResponse>, NotFound, ValidationProblem>> Update(
        Guid id,
        UpdateEntityRequest request,
        EntityDbContext db,
        AuditRecorder audit,
        ClaimsPrincipal user,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entity = await db.Entities.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            entity.UpdateNames(request.LegalName, request.TradingName, timeProvider.GetUtcNow());
            await audit.RecordAsync(user.Mutation(
                entity.TenantId, "Entity", entity.Id.ToString(), "entity.entity.update", entity.DisplayName), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(new EntitySummaryResponse(
                entity.Id, entity.TenantId, entity.Type, entity.LegalName, entity.TradingName, entity.DisplayName, entity.Status, entity.CreatedAt, entity.UpdatedAt));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.LegalName)] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<EntityDetailResponse>, NotFound>> GetDetail(
        Guid id,
        EntityDbContext db,
        CancellationToken cancellationToken)
    {
        var entity = await LoadWithDetailsAsync(db.Entities.AsNoTracking(), id, cancellationToken);
        return entity is null ? TypedResults.NotFound() : TypedResults.Ok(ToDetail(entity));
    }

    private static async Task<Results<Ok<EntityDetailResponse>, NotFound, ValidationProblem>> AddIdentifier(
        Guid id,
        AddIdentifierRequest request,
        EntityDbContext db,
        AuditRecorder audit,
        ClaimsPrincipal user,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entity = await LoadWithDetailsAsync(db.Entities, id, cancellationToken);
        if (entity is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            var identifier = entity.AddIdentifier(request.Type, request.Value, timeProvider.GetUtcNow());
            // The child carries a client-generated Guid PK; added through the loaded navigation, EF
            // mis-detects it as Modified and emits an UPDATE that affects 0 rows (DbUpdateConcurrency).
            // Force Added so it INSERTs.
            db.Entry(identifier).State = EntityState.Added;
            await audit.RecordAsync(user.Mutation(
                entity.TenantId, "Entity", entity.Id.ToString(), "entity.identifier.add", $"{request.Type}"), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(ToDetail(entity));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Value)] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<EntityDetailResponse>, NotFound, ValidationProblem>> VerifyIdentifier(
        Guid id,
        Guid identifierId,
        EntityDbContext db,
        AuditRecorder audit,
        ClaimsPrincipal user,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entity = await LoadWithDetailsAsync(db.Entities, id, cancellationToken);
        if (entity is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            entity.MarkIdentifierVerified(identifierId, timeProvider.GetUtcNow());
            await audit.RecordAsync(user.Mutation(
                entity.TenantId, "Entity", entity.Id.ToString(), "entity.identifier.verify", identifierId.ToString()), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(ToDetail(entity));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["identifier"] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<EntityDetailResponse>, NotFound, ValidationProblem>> AddContact(
        Guid id,
        AddContactRequest request,
        EntityDbContext db,
        AuditRecorder audit,
        ClaimsPrincipal user,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entity = await LoadWithDetailsAsync(db.Entities, id, cancellationToken);
        if (entity is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            var contact = entity.AddContactMethod(request.Purpose, request.Kind, request.Value, timeProvider.GetUtcNow());
            db.Entry(contact).State = EntityState.Added; // client-generated PK via loaded nav — force INSERT (see AddIdentifier)
            await audit.RecordAsync(user.Mutation(
                entity.TenantId, "Entity", entity.Id.ToString(), "entity.contact.add", $"{request.Purpose}/{request.Kind}"), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(ToDetail(entity));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Value)] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<EntityDetailResponse>, NotFound, ValidationProblem>> AddAddress(
        Guid id,
        AddAddressRequest request,
        EntityDbContext db,
        AuditRecorder audit,
        ClaimsPrincipal user,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entity = await LoadWithDetailsAsync(db.Entities, id, cancellationToken);
        if (entity is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            var address = entity.AddAddress(request.Purpose, request.Line1, request.Line2, request.City, request.Region, request.Postcode, request.CountryCode, timeProvider.GetUtcNow());
            db.Entry(address).State = EntityState.Added; // only the NEW address; any superseded ones stay Modified (see AddIdentifier)
            await audit.RecordAsync(user.Mutation(
                entity.TenantId, "Entity", entity.Id.ToString(), "entity.address.add", $"{request.Purpose}"), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(ToDetail(entity));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Line1)] = [ex.Message] });
        }
    }

    private static Task<EntityRecord?> LoadWithDetailsAsync(IQueryable<EntityRecord> entities, Guid id, CancellationToken cancellationToken) =>
        entities
            .Include(e => e.Identifiers)
            .Include(e => e.ContactMethods)
            .Include(e => e.Addresses)
            .SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    private static EntityDetailResponse ToDetail(EntityRecord e) => new(
        e.Id, e.TenantId, e.Type, e.LegalName, e.TradingName, e.DisplayName, e.Status,
        [.. e.Identifiers.OrderBy(i => i.Type).Select(i => new EntityIdentifierResponse(i.Id, i.Type, i.Value, i.VerificationStatus))],
        [.. e.ContactMethods.OrderBy(c => c.Purpose).Select(c => new EntityContactResponse(c.Id, c.Purpose, c.Kind, c.Value))],
        [.. e.Addresses.Where(a => a.IsCurrent).OrderBy(a => a.Purpose).Select(a => new EntityAddressResponse(a.Id, a.Purpose, a.Line1, a.Line2, a.City, a.Region, a.Postcode, a.CountryCode))]);

    private static async Task<Ok<SupplierOnboardingResponse>> StartSupplierOnboarding(
        Guid id,
        SupplierOnboardingService suppliers,
        CancellationToken cancellationToken)
    {
        var onboarding = await suppliers.StartAsync(id, cancellationToken);
        return TypedResults.Ok(ToResponse(onboarding));
    }

    private static async Task<Ok<SupplierReadinessResponse>> CheckSupplierReadiness(
        Guid id,
        SupplierOnboardingService suppliers,
        CancellationToken cancellationToken)
    {
        var readiness = await suppliers.CheckReadinessAsync(id, cancellationToken);
        return TypedResults.Ok(new SupplierReadinessResponse(readiness.IsReady, readiness.MissingRequirements));
    }

    private static async Task<Results<Ok<SupplierOnboardingResponse>, ValidationProblem>> SubmitSupplierVerification(
        Guid id,
        SupplierOnboardingService suppliers,
        EntityDbContext db,
        AuditRecorder audit,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        try
        {
            var onboarding = await suppliers.SubmitForVerificationAsync(id, cancellationToken);
            await RecordSupplierAuditAsync(db, audit, user, onboarding, "entity.supplier.submit_verification", cancellationToken);
            return TypedResults.Ok(ToResponse(onboarding));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["supplier"] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<SupplierOnboardingResponse>, ValidationProblem>> MarkSupplierVerificationComplete(
        Guid id,
        SupplierOnboardingService suppliers,
        EntityDbContext db,
        AuditRecorder audit,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        try
        {
            var onboarding = await suppliers.MarkVerificationCompleteAsync(id, cancellationToken);
            await RecordSupplierAuditAsync(db, audit, user, onboarding, "entity.supplier.verify", cancellationToken);
            return TypedResults.Ok(ToResponse(onboarding));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["supplier"] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<SupplierOnboardingResponse>, ValidationProblem>> ActivateSupplier(
        Guid id,
        SupplierOnboardingService suppliers,
        EntityDbContext db,
        AuditRecorder audit,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        try
        {
            var onboarding = await suppliers.ActivateAsync(id, cancellationToken);
            await RecordSupplierAuditAsync(db, audit, user, onboarding, "entity.supplier.activate", cancellationToken);
            return TypedResults.Ok(ToResponse(onboarding));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["supplier"] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<SupplierOnboardingResponse>, ValidationProblem>> SuspendSupplier(
        Guid id,
        SuspendSupplierRequest request,
        SupplierOnboardingService suppliers,
        EntityDbContext db,
        AuditRecorder audit,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        try
        {
            var onboarding = await suppliers.SuspendAsync(id, request.Reason, cancellationToken);
            await RecordSupplierAuditAsync(db, audit, user, onboarding, "entity.supplier.suspend", cancellationToken, request.Reason);
            return TypedResults.Ok(ToResponse(onboarding));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Reason)] = [ex.Message] });
        }
    }

    private static async Task<Ok<SupplierOnboardingResponse>> ArchiveSupplier(
        Guid id,
        SupplierOnboardingService suppliers,
        EntityDbContext db,
        AuditRecorder audit,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var onboarding = await suppliers.ArchiveAsync(id, cancellationToken);
        await RecordSupplierAuditAsync(db, audit, user, onboarding, "entity.supplier.archive", cancellationToken);
        return TypedResults.Ok(ToResponse(onboarding));
    }

    // The onboarding service commits the transition itself, so the local hash-chained entry is staged
    // afterwards and committed with its own SaveChanges. The audit write never breaks the mutation.
    private static async Task RecordSupplierAuditAsync(
        EntityDbContext db, AuditRecorder audit, ClaimsPrincipal user, SupplierOnboarding onboarding,
        string action, CancellationToken ct, string? summary = null)
    {
        await audit.RecordAsync(user.Mutation(
            onboarding.TenantId, "SupplierOnboarding", onboarding.EntityId.ToString(), action, summary), ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task<Ok<List<DuplicateWarningResponse>>> ScanDuplicates(
        Guid id,
        DuplicateDetectionService duplicates,
        CancellationToken cancellationToken)
    {
        var warnings = await duplicates.DetectForEntityAsync(id, cancellationToken);
        return TypedResults.Ok(warnings.Select(ToResponse).ToList());
    }

    private static async Task<Results<NoContent, ValidationProblem>> OverrideDuplicateWarning(
        Guid warningId,
        OverrideDuplicateWarningRequest request,
        DuplicateDetectionService duplicates,
        CancellationToken cancellationToken)
    {
        try
        {
            await duplicates.OverrideAsync(warningId, request.Reason, cancellationToken);
            return TypedResults.NoContent();
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Reason)] = [ex.Message] });
        }
    }

    private static async Task<Results<NoContent, NotFound>> Archive(
        Guid id,
        EntityDbContext db,
        AuditRecorder audit,
        ClaimsPrincipal user,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entity = await db.Entities.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return TypedResults.NotFound();
        }

        entity.Archive(timeProvider.GetUtcNow());
        await audit.RecordAsync(user.Mutation(
            entity.TenantId, "Entity", entity.Id.ToString(), "entity.entity.archive", entity.DisplayName), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
    private static SupplierOnboardingResponse ToResponse(SupplierOnboarding onboarding) => new(
        onboarding.Id,
        onboarding.TenantId,
        onboarding.EntityId,
        onboarding.State,
        onboarding.CreatedAt,
        onboarding.UpdatedAt,
        onboarding.ActivatedAt,
        onboarding.ArchivedAt,
        onboarding.SuspensionReason);

    private static DuplicateWarningResponse ToResponse(DuplicateWarning warning) => new(
        warning.Id,
        warning.TenantId,
        warning.CandidateEntityId,
        warning.ExistingEntityId,
        warning.Kind,
        warning.MatchedValue,
        warning.Status,
        warning.CreatedAt);

    private static async Task<Results<Created<SupplierChangeRequestResponse>, ValidationProblem>> OpenSupplierChangeRequest(
        Guid id,
        Guid tenantId,
        OpenSupplierChangeRequestBody request,
        HttpContext http,
        SupplierChangeRequestService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await service.OpenAsync(tenantId, id, request.Type, request.Summary, request.Detail, ActingPrincipal(http), cancellationToken);
            return TypedResults.Created($"/entities/suppliers/change-requests/{created.Id}", ToResponse(created));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["changeRequest"] = [ex.Message] });
        }
    }

    private static async Task<Ok<List<SupplierChangeRequestResponse>>> ListSupplierChangeRequests(
        Guid tenantId,
        SupplierChangeRequestStatus? status,
        SupplierChangeRequestService service,
        CancellationToken cancellationToken)
    {
        var requests = await service.ListAsync(tenantId, status, cancellationToken);
        return TypedResults.Ok(requests.Select(ToResponse).ToList());
    }

    private static async Task<Results<Ok<SupplierChangeRequestResponse>, NotFound, ValidationProblem>> ApproveSupplierChangeRequest(
        Guid requestId,
        Guid tenantId,
        SupplierChangeDecisionBody request,
        HttpContext http,
        SupplierChangeRequestService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var decided = await service.ApproveAsync(tenantId, requestId, ActingPrincipal(http), IsAdmin(http), request.Reason, cancellationToken);
            return decided is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(decided));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["changeRequest"] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<SupplierChangeRequestResponse>, NotFound, ValidationProblem>> RejectSupplierChangeRequest(
        Guid requestId,
        Guid tenantId,
        RejectSupplierChangeRequestBody request,
        HttpContext http,
        SupplierChangeRequestService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var decided = await service.RejectAsync(tenantId, requestId, ActingPrincipal(http), IsAdmin(http), request.Reason, cancellationToken);
            return decided is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(decided));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["changeRequest"] = [ex.Message] });
        }
    }

    private static async Task<Results<Created<CustomerEntityLinkResponse>, ValidationProblem>> LinkCustomer(
        Guid id,
        Guid tenantId,
        LinkCustomerBody request,
        HttpContext http,
        CustomerEntityLinkService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var link = await service.LinkAsync(tenantId, id, request.CustomerPrincipalId, request.Role, ActingPrincipal(http), cancellationToken);
            return TypedResults.Created($"/entities/customer-links/{link.Id}", ToResponse(link));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["customerLink"] = [ex.Message] });
        }
    }

    private static async Task<Ok<List<CustomerEntityLinkResponse>>> ListEntityCustomerLinks(
        Guid id,
        Guid tenantId,
        bool? activeOnly,
        CustomerEntityLinkService service,
        CancellationToken cancellationToken)
    {
        var links = await service.ListForEntityAsync(tenantId, id, activeOnly ?? false, cancellationToken);
        return TypedResults.Ok(links.Select(ToResponse).ToList());
    }

    private static async Task<Ok<List<CustomerEntityLinkResponse>>> ListCustomerLinks(
        Guid tenantId,
        Guid customerPrincipalId,
        bool? activeOnly,
        CustomerEntityLinkService service,
        CancellationToken cancellationToken)
    {
        var links = await service.ListForCustomerAsync(tenantId, customerPrincipalId, activeOnly ?? false, cancellationToken);
        return TypedResults.Ok(links.Select(ToResponse).ToList());
    }

    private static async Task<Results<Ok<CustomerEntityLinkResponse>, NotFound>> UnlinkCustomer(
        Guid linkId,
        Guid tenantId,
        CustomerEntityLinkService service,
        CancellationToken cancellationToken)
    {
        var link = await service.UnlinkAsync(tenantId, linkId, cancellationToken);
        return link is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(link));
    }

    // The acting principal is taken from the internal claims (sub), never a client-supplied body,
    // so maker-checker (approver != requester) cannot be spoofed.
    private static Guid ActingPrincipal(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue("sub"), out var id) ? id : Guid.Empty;

    // Admins/operators are trusted to self-approve their own supplier change requests (maker-checker is
    // only enforced for non-admin requesters). These endpoints already require the admin role, so this
    // is true in practice — reading it keeps the rule explicit rather than hard-coded.
    private static bool IsAdmin(HttpContext http) => http.User.IsInRole("admin");

    private static CustomerEntityLinkResponse ToResponse(CustomerEntityLink link) => new(
        link.Id,
        link.TenantId,
        link.CustomerPrincipalId,
        link.EntityId,
        link.Role,
        link.IsActive,
        link.EffectiveFrom,
        link.EffectiveTo,
        link.LinkedByPrincipalId);

    private static SupplierChangeRequestResponse ToResponse(SupplierChangeRequest request) => new(
        request.Id,
        request.TenantId,
        request.EntityId,
        request.Type,
        request.Summary,
        request.Detail,
        request.Status,
        request.RequestedByPrincipalId,
        request.CreatedAt,
        request.DecidedByPrincipalId,
        request.DecisionReason,
        request.DecidedAt);
}

public sealed record UpdateEntityRequest(
    [property: System.ComponentModel.DataAnnotations.Required] string LegalName,
    string? TradingName);

public sealed record CreateEntityRequest(
    [property: Required] Guid TenantId,
    EntityType Type,
    [property: Required, StringLength(200, MinimumLength = 2)] string LegalName,
    [property: StringLength(200, MinimumLength = 2)] string? TradingName,
    IReadOnlyList<EntityRoleKind>? Roles);

public sealed record EntitySummaryResponse(
    Guid Id,
    Guid TenantId,
    EntityType Type,
    string LegalName,
    string? TradingName,
    string DisplayName,
    EntityRecordStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AddIdentifierRequest(
    [property: Required] EntityIdentifierType Type,
    [property: Required, StringLength(80, MinimumLength = 2)] string Value);

public sealed record AddContactRequest(
    [property: Required] EntityContactPurpose Purpose,
    [property: Required] EntityContactKind Kind,
    [property: Required, StringLength(320, MinimumLength = 3)] string Value);

public sealed record AddAddressRequest(
    [property: Required] EntityAddressPurpose Purpose,
    [property: Required, StringLength(200, MinimumLength = 2)] string Line1,
    string? Line2,
    [property: Required, StringLength(200, MinimumLength = 2)] string City,
    string? Region,
    [property: Required, StringLength(200, MinimumLength = 2)] string Postcode,
    [property: Required, StringLength(2, MinimumLength = 2)] string CountryCode);

public sealed record EntityIdentifierResponse(Guid Id, EntityIdentifierType Type, string Value, EntityVerificationStatus VerificationStatus);
public sealed record EntityContactResponse(Guid Id, EntityContactPurpose Purpose, EntityContactKind Kind, string Value);
public sealed record EntityAddressResponse(Guid Id, EntityAddressPurpose Purpose, string Line1, string? Line2, string City, string? Region, string Postcode, string CountryCode);

public sealed record EntityDetailResponse(
    Guid Id,
    Guid TenantId,
    EntityType Type,
    string LegalName,
    string? TradingName,
    string DisplayName,
    EntityRecordStatus Status,
    IReadOnlyList<EntityIdentifierResponse> Identifiers,
    IReadOnlyList<EntityContactResponse> Contacts,
    IReadOnlyList<EntityAddressResponse> Addresses);

public sealed record OverrideDuplicateWarningRequest([property: Required, StringLength(500, MinimumLength = 8)] string Reason);

public sealed record SuspendSupplierRequest([property: Required, StringLength(500, MinimumLength = 8)] string Reason);

public sealed record SupplierReadinessResponse(bool IsReady, IReadOnlyList<string> MissingRequirements);

public sealed record SupplierOnboardingResponse(
    Guid Id,
    Guid TenantId,
    Guid EntityId,
    SupplierOnboardingState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? ArchivedAt,
    string? SuspensionReason);

public sealed record DuplicateWarningResponse(
    Guid Id,
    Guid TenantId,
    Guid CandidateEntityId,
    Guid ExistingEntityId,
    DuplicateWarningKind Kind,
    string MatchedValue,
    DuplicateWarningStatus Status,
    DateTimeOffset CreatedAt);

public sealed record OpenSupplierChangeRequestBody(
    [property: Required] SupplierChangeRequestType Type,
    [property: Required, StringLength(300, MinimumLength = 3)] string Summary,
    [property: StringLength(2000)] string? Detail);

public sealed record SupplierChangeDecisionBody(
    [property: StringLength(500)] string? Reason);

public sealed record RejectSupplierChangeRequestBody(
    [property: Required, StringLength(500, MinimumLength = 8)] string Reason);

public sealed record SupplierChangeRequestResponse(
    Guid Id,
    Guid TenantId,
    Guid EntityId,
    SupplierChangeRequestType Type,
    string Summary,
    string? Detail,
    SupplierChangeRequestStatus Status,
    Guid RequestedByPrincipalId,
    DateTimeOffset CreatedAt,
    Guid? DecidedByPrincipalId,
    string? DecisionReason,
    DateTimeOffset? DecidedAt);

public sealed record LinkCustomerBody(
    [property: Required] Guid CustomerPrincipalId,
    [property: Required] CustomerEntityRole Role);

public sealed record CustomerEntityLinkResponse(
    Guid Id,
    Guid TenantId,
    Guid CustomerPrincipalId,
    Guid EntityId,
    CustomerEntityRole Role,
    bool IsActive,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo,
    Guid LinkedByPrincipalId);
