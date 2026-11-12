using System.IO;
using VortexDownloader.Core.Common;
using Xunit;

namespace VortexDownloader.Tests;

public class DynamicSplitTests
{
    [Fact]
    public void SplitWorks_WhenChunkCountAlreadyEqualsMaxChunks_AndStaysContiguous()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vortex_split_" + Guid.NewGuid().ToString("N"));
        try
        {
            var chunks = ChunksManager.InitializeChunks(Guid.NewGuid(), 8 * 1024 * 1024, 4, dir);
            Assert.Equal(4, chunks.Count);

            var big = ChunksManager.FindMaxFreeChunk(chunks);
            Assert.NotNull(big);
            var split = ChunksManager.SplitChunk(big!, chunks.Count, Path.Combine(dir, "p4.bin"));
            Assert.NotNull(split);
            chunks.Add(split!.Value.NewChunk);

            Assert.Equal(5, chunks.Count);
            Assert.Equal(5, chunks.Select(c => c.Id).Distinct().Count());
            var sorted = chunks.OrderBy(c => c.StartPos).ToList();
            for (int i = 0; i < sorted.Count - 1; i++)
                Assert.Equal(sorted[i].EndPos + 1, sorted[i + 1].StartPos);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
