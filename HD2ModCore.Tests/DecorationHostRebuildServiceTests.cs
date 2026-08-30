using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class DecorationHostRebuildServiceTests
{
    [Fact]
    public void RemoveGeneratedDecorationOverwrite_RemovesOnlyDecorationMarkedDirectory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var generated = Path.Combine(root, "generated", "Overwrite");
            Directory.CreateDirectory(generated);
            File.WriteAllText(Path.Combine(generated, DecorationHostRebuildService.DecorationOverwriteMarkerName), "generated");
            File.WriteAllText(Path.Combine(generated, "patch_0"), "payload");

            var unknown = Path.Combine(root, "unknown", "Overwrite");
            Directory.CreateDirectory(unknown);
            File.WriteAllText(Path.Combine(unknown, "user-file.txt"), "keep");

            var removed = DecorationHostRebuildService.RemoveGeneratedDecorationOverwrite(generated);
            var preserved = DecorationHostRebuildService.RemoveGeneratedDecorationOverwrite(unknown);

            Assert.True(removed.Removed);
            Assert.True(removed.WasManaged);
            Assert.False(Directory.Exists(generated));
            Assert.False(preserved.Removed);
            Assert.False(preserved.WasManaged);
            Assert.True(File.Exists(Path.Combine(unknown, "user-file.txt")));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void RemoveGeneratedDecorationOverwrite_DoesNotTreatHelmetOnlyOverwriteAsDecorationOwned()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var overwrite = Path.Combine(root, "host", "Overwrite");
            Directory.CreateDirectory(overwrite);
            File.WriteAllText(Path.Combine(overwrite, DecorationHostRebuildService.HelmetMergeOverwriteMarkerName), "generated");

            var result = DecorationHostRebuildService.RemoveGeneratedDecorationOverwrite(overwrite);

            Assert.False(result.Removed);
            Assert.False(result.WasManaged);
            Assert.True(File.Exists(Path.Combine(overwrite, DecorationHostRebuildService.HelmetMergeOverwriteMarkerName)));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2-decoration-host-rebuild-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryDirectory(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
