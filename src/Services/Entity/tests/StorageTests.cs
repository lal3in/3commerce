using ThreeCommerce.BuildingBlocks.Infrastructure.Storage;

namespace ThreeCommerce.Entity.Tests;

public class StorageTests
{
    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "3c-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Local_store_round_trips_put_get_exists_delete()
    {
        var root = TempRoot();
        try
        {
            var store = new LocalFileObjectStore(root);
            const string key = "t1/product-images/p1/pic.png";
            var bytes = new byte[] { 1, 2, 3, 4 };

            await store.PutAsync(key, new MemoryStream(bytes), "image/png", default);
            Assert.True(await store.ExistsAsync(key, default));

            await using (var read = await store.GetAsync(key, default))
            {
                var buffer = new MemoryStream();
                await read!.CopyToAsync(buffer);
                Assert.Equal(bytes, buffer.ToArray());
            }

            await store.DeleteAsync(key, default);
            Assert.False(await store.ExistsAsync(key, default));
            Assert.Null(await store.GetAsync(key, default));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Local_store_refuses_a_path_that_escapes_the_root()
    {
        var root = TempRoot();
        try
        {
            var store = new LocalFileObjectStore(root);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.PutAsync("../escape.txt", new MemoryStream([0]), "text/plain", default));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Keys_are_tenant_scoped_and_strip_traversal()
    {
        var tenant = Guid.NewGuid();
        var key = StoredObjectKey.For(tenant, "Product Images", "p-1", "../../etc/passwd");

        Assert.StartsWith(tenant.ToString("N"), key);
        Assert.DoesNotContain("..", key);
        Assert.EndsWith("/passwd", key);
        Assert.Contains("/product-images/", key);
    }

    [Theory]
    [InlineData("image/png", 1000, true)]
    [InlineData("image/jpeg", 1000, true)]
    [InlineData("image/webp", 1000, true)]
    [InlineData("image/svg+xml", 1000, false)] // active content
    [InlineData("text/html", 1000, false)]
    [InlineData("application/octet-stream", 1000, false)]
    [InlineData("image/png", 0, false)]                       // empty
    [InlineData("image/png", 11 * 1024 * 1024, false)]        // oversize
    public void Upload_validation_allow_lists_safe_images(string contentType, long size, bool allowed)
    {
        var ok = UploadPolicy.ValidateImage(contentType, size, out var error);
        Assert.Equal(allowed, ok);
        Assert.Equal(allowed, error is null);
    }

    [Fact]
    public void Image_variant_keys_and_sizes_are_derived()
    {
        Assert.Equal("t/cat/p1/pic@medium.png", ImageVariants.KeyFor("t/cat/p1/pic.png", ImageVariant.Medium));
        Assert.Equal("noext@thumbnail", ImageVariants.KeyFor("noext", ImageVariant.Thumbnail));
        Assert.True(ImageVariants.MaxDimension(ImageVariant.Thumbnail) < ImageVariants.MaxDimension(ImageVariant.Large));
    }
}
