using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Entity;
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
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow();
            var entity = EntityRecord.Create(request.TenantId, request.Type, request.LegalName, request.TradingName, now, request.Roles ?? []);
            db.Entities.Add(entity);
            await publish.Publish(new EntityRecordCreated(entity.Id, entity.TenantId, entity.DisplayName, now), cancellationToken);
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
        CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(ToResponse(await suppliers.SubmitForVerificationAsync(id, cancellationToken)));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["supplier"] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<SupplierOnboardingResponse>, ValidationProblem>> MarkSupplierVerificationComplete(
        Guid id,
        SupplierOnboardingService suppliers,
        CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(ToResponse(await suppliers.MarkVerificationCompleteAsync(id, cancellationToken)));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["supplier"] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<SupplierOnboardingResponse>, ValidationProblem>> ActivateSupplier(
        Guid id,
        SupplierOnboardingService suppliers,
        CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(ToResponse(await suppliers.ActivateAsync(id, cancellationToken)));
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
        CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(ToResponse(await suppliers.SuspendAsync(id, request.Reason, cancellationToken)));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Reason)] = [ex.Message] });
        }
    }

    private static async Task<Ok<SupplierOnboardingResponse>> ArchiveSupplier(
        Guid id,
        SupplierOnboardingService suppliers,
        CancellationToken cancellationToken)
    {
        var onboarding = await suppliers.ArchiveAsync(id, cancellationToken);
        return TypedResults.Ok(ToResponse(onboarding));
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
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entity = await db.Entities.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return TypedResults.NotFound();
        }

        entity.Archive(timeProvider.GetUtcNow());
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
            var decided = await service.ApproveAsync(tenantId, requestId, ActingPrincipal(http), request.Reason, cancellationToken);
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
            var decided = await service.RejectAsync(tenantId, requestId, ActingPrincipal(http), request.Reason, cancellationToken);
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
