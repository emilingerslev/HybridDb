using System.IO;
using HybridDb.Migrations;
using Shouldly;
using Xunit;

namespace HybridDb.Tests
{
    public class FileBackupWriterTests
    {
        [Fact]
        public void WritesToFile()
        {
            var writer = new FileBackupWriter(".");
            writer.Write("hans.bak", new byte[]{ 1, 2, 3 });

            var bytes = File.ReadAllBytes("hans.bak");
            bytes.ShouldBe(new byte[] { 1, 2, 3 });
        }

        [Fact]
        public void CanWriteSameDocumentTwice()
        {
            var writer = new FileBackupWriter(".");
            writer.Write("jacob.bak", new byte[]{ 1, 2, 3 });
            
            Should.NotThrow(() => writer.Write("jacob.bak", new byte[]{ 1, 2, 3 }));

            var bytes = File.ReadAllBytes("jacob.bak");
            bytes.ShouldBe(new byte[] { 1, 2, 3 });
        }

        [Fact]
        public void ConcurrentWritesDoesNotFail()
        {
            using (File.Create("jacob.bak"))
            {
                var writer = new FileBackupWriter(".");
                Should.NotThrow(() => writer.Write("jacob.bak", new byte[] { 1, 2, 3 }));
            }
        }
    }
}