using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TGYHTurkishPatch;

// Usage: TextureBuilder <manifest.tsv> <output dir>
// Manifest lines: <png path> TAB <width> TAB <height> TAB <RawFormat number> TAB <output file name>
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: TextureBuilder <manifest.tsv> <output dir>");
            return 2;
        }

        string[] lines = File.ReadAllLines(args[0]);
        string outDir = args[1];
        Directory.CreateDirectory(outDir);

        int built = 0, failed = 0;
        long bytes = 0;
        Parallel.ForEach(lines, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, line =>
        {
            if (line.Length == 0) return;
            string[] f = line.Split('\t');
            string png = f[0], target = Path.Combine(outDir, f[4]);
            try
            {
                int width = int.Parse(f[1]), height = int.Parse(f[2]);
                var format = (RawFormat)int.Parse(f[3]);
                if (!TextureEncoder.TryEncode(png, width, height, format, out byte[] raw, out string reason))
                    throw new InvalidDataException(reason);
                TextureCacheFile.Write(target, 0, 0, width, height, format, raw, true);
                Interlocked.Add(ref bytes, new FileInfo(target).Length);
                Interlocked.Increment(ref built);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                Console.Error.WriteLine($"FAILED {Path.GetFileName(png)}: {ex.Message}");
            }
        });

        Console.WriteLine($"Built {built} textures ({bytes / 1048576} MB), {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
}
