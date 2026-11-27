using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Support attachments: a customer uploads a file against a ticket/RMA (images or PDF, size-capped),
/// it's stored and streamable back; anonymous callers can't upload, and junk types are rejected.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class SupportAttachmentTests(Phase4Fixture fixture)
{
    private sealed record AttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes);

    private HttpClient Customer()
    {
        var c = fixture.Support.CreateClient();
        c.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.CustomerClaims());
        return c;
    }

    private static MultipartFormDataContent FilePart(byte[] bytes, string contentType, string fileName)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [Fact]
    public async Task Customer_uploads_a_ticket_attachment_and_streams_it_back()
    {
        var customer = Customer();
        var ticketId = await OwnTicketAsync(customer);
        var upload = await customer.PostAsync($"/tickets/{ticketId}/attachments", FilePart(Png, "image/png", "damage.png"));
        upload.EnsureSuccessStatusCode();
        var dto = await upload.Content.ReadFromJsonAsync<AttachmentDto>();
        Assert.Equal("damage.png", dto!.FileName);
        Assert.Equal(Png.Length, dto.SizeBytes);

        var download = await customer.GetAsync($"/attachments/{dto.Id}");
        download.EnsureSuccessStatusCode();
        Assert.Equal("image/png", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Anonymous_cannot_upload()
    {
        var anon = fixture.Support.CreateClient();
        var upload = await anon.PostAsync($"/tickets/{Guid.CreateVersion7()}/attachments", FilePart(Png, "image/png", "x.png"));
        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
    }

    [Fact]
    public async Task Disallowed_type_is_rejected()
    {
        var customer = Customer();
        var ticketId = await OwnTicketAsync(customer);
        var upload = await customer.PostAsync($"/tickets/{ticketId}/attachments", FilePart([1, 2, 3, 4], "application/x-msdownload", "evil.exe"));
        Assert.Equal(HttpStatusCode.BadRequest, upload.StatusCode);
    }

    // Owner-scoped (ADR-0018 amendment 2026-11-27): an attachment goes only onto the caller's own ticket / RMA, and
    // only its owner or an operator reads it back — anyone else gets 404, as if neither existed.
    [Fact]
    public async Task Another_customer_can_neither_attach_to_nor_download_from_someone_elses_ticket()
    {
        var owner = Customer();
        var ticketId = await OwnTicketAsync(owner);
        var upload = await owner.PostAsync($"/tickets/{ticketId}/attachments", FilePart(Png, "image/png", "mine.png"));
        upload.EnsureSuccessStatusCode();
        var dto = (await upload.Content.ReadFromJsonAsync<AttachmentDto>())!;

        var stranger = fixture.Support.CreateClient();
        stranger.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.Claims("customer"));
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.PostAsync($"/tickets/{ticketId}/attachments", FilePart(Png, "image/png", "x.png"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/attachments/{dto.Id}")).StatusCode);
        // An unknown ticket / RMA is the same 404 — the id never confirms anything exists.
        Assert.Equal(HttpStatusCode.NotFound,
            (await owner.PostAsync($"/rma/{Guid.CreateVersion7()}/attachments", FilePart(Png, "image/png", "x.png"))).StatusCode);

        // The Admin console reads attachments through this customer route as an operator.
        var admin = fixture.Support.CreateClient();
        admin.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.Claims("admin"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/attachments/{dto.Id}")).StatusCode);
    }

    private async Task<Guid> OwnTicketAsync(HttpClient customer)
    {
        var orderId = Guid.CreateVersion7();
        await fixture.SeedOrderSnapshotAsync(orderId, 1000);
        var open = await customer.PostAsJsonAsync("/tickets", new
        {
            orderId,
            email = "buyer@example.com",
            reason = 1,
            message = "It arrived damaged.",
        });
        open.EnsureSuccessStatusCode();
        return (await open.Content.ReadFromJsonAsync<TicketIdDto>())!.Id;
    }

    private sealed record TicketIdDto(Guid Id);
}
