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
        c.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.Claims("customer"));
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
        var upload = await customer.PostAsync($"/tickets/{Guid.CreateVersion7()}/attachments", FilePart(Png, "image/png", "damage.png"));
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
        var upload = await customer.PostAsync($"/rma/{Guid.CreateVersion7()}/attachments", FilePart([1, 2, 3, 4], "application/x-msdownload", "evil.exe"));
        Assert.Equal(HttpStatusCode.BadRequest, upload.StatusCode);
    }
}
