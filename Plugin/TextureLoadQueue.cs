using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace TGYHTurkishPatch
{
    /// <summary>One texture to prepare off the main thread. The worker fills in the result fields.</summary>
    internal sealed class TextureJob
    {
        // Request
        public IntPtr Target;
        public int Generation;
        public string PngPath;          // source file: a PNG, or a prebuilt .tgyc
        public bool Prebuilt;
        public string CacheFile;        // null = no disk cache
        public int Width;
        public int Height;
        public RawFormat Format;        // None = hand the PNG bytes to Unity's loader

        // Result
        public byte[] Raw;              // ready for LoadRawTextureData
        public byte[] PngBytes;         // fallback payload for ImageConversion.LoadImage
        public bool FromCache;
        public string Note;             // why the fast path was skipped
        public string Error;

        public long PayloadBytes => (Raw?.Length ?? 0) + (PngBytes?.Length ?? 0);
    }

    /// <summary>
    /// Background workers that decode/encode replacement textures on demand. Nothing in here
    /// touches Unity or IL2CPP. Finished-but-unapplied payloads are capped so a burst of large
    /// atlases cannot exhaust the 32-bit address space.
    /// </summary>
    internal static class TextureLoadQueue
    {
        private const long MaxPendingResultBytes = 128L * 1024 * 1024;

        private static readonly object _lock = new object();
        private static readonly Queue<TextureJob> _jobs = new Queue<TextureJob>();
        private static readonly Queue<TextureJob> _done = new Queue<TextureJob>();
        private static readonly object[] _pathLocks = CreateLocks(16);
        private static long _doneBytes;
        private static int _running;
        private static int _generation;
        private static bool _started;
        private static volatile bool _cacheWritable = true;

        public static int Generation => Volatile.Read(ref _generation);

        /// <summary>Jobs queued, in progress, or finished but not yet collected.</summary>
        public static int Outstanding
        {
            get { lock (_lock) return _jobs.Count + _running + _done.Count; }
        }

        private static object[] CreateLocks(int count)
        {
            var locks = new object[count];
            for (int i = 0; i < count; i++) locks[i] = new object();
            return locks;
        }

        public static void Start(int threads)
        {
            lock (_lock)
            {
                if (_started) return;
                _started = true;
            }
            for (int i = 0; i < threads; i++)
            {
                var thread = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "TGY texture worker " + i,
                    Priority = ThreadPriority.BelowNormal,
                };
                thread.Start();
            }
        }

        public static void Enqueue(TextureJob job)
        {
            lock (_lock)
            {
                job.Generation = _generation;
                _jobs.Enqueue(job);
                Monitor.PulseAll(_lock);
            }
        }

        public static bool TryDequeueDone(out TextureJob job)
        {
            lock (_lock)
            {
                while (_done.Count > 0)
                {
                    job = _done.Dequeue();
                    _doneBytes -= job.PayloadBytes;
                    Monitor.PulseAll(_lock);
                    if (job.Generation == _generation) return true;
                }
            }
            job = null;
            return false;
        }

        /// <summary>Drops queued work and makes in-flight results stale.</summary>
        public static void CancelAll()
        {
            lock (_lock)
            {
                _generation++;
                _jobs.Clear();
                _done.Clear();
                _doneBytes = 0;
                Monitor.PulseAll(_lock);
            }
        }

        private static void WorkerLoop()
        {
            while (true)
            {
                TextureJob job;
                lock (_lock)
                {
                    while (_jobs.Count == 0 || _doneBytes > MaxPendingResultBytes)
                        Monitor.Wait(_lock);
                    job = _jobs.Dequeue();
                    _running++;
                }

                try
                {
                    if (job.Generation == Generation) Process(job);
                }
                catch (OutOfMemoryException)
                {
                    job.Raw = null;
                    job.PngBytes = null;
                    job.Error = "out of memory";
                    GC.Collect();
                }
                catch (Exception ex)
                {
                    job.Raw = null;
                    job.PngBytes = null;
                    job.Error = ex.Message;
                }

                lock (_lock)
                {
                    _running--;
                    if (job.Generation == _generation)
                    {
                        _done.Enqueue(job);
                        _doneBytes += job.PayloadBytes;
                    }
                }
            }
        }

        private static void Process(TextureJob job)
        {
            var info = new FileInfo(job.PngPath);
            if (!info.Exists)
            {
                job.Error = "file not found";
                return;
            }

            if (job.Prebuilt)
            {
                // Already in GPU form; there is no PNG to fall back to if it doesn't fit.
                if (job.Format == RawFormat.None)
                    job.Error = "the game's texture format is not supported by prebuilt textures";
                else if (TextureCacheFile.TryRead(job.PngPath, false, 0, 0, job.Width, job.Height, job.Format, out var prebuilt, out var mismatch))
                    job.Raw = prebuilt;
                else
                    job.Error = mismatch ?? "prebuilt texture file is corrupt";
                return;
            }

            if (job.Format != RawFormat.None)
            {
                // Several bundles ship the same atlas; serialise per file so the second
                // request finds the cache entry instead of encoding the PNG again.
                lock (_pathLocks[(job.PngPath.GetHashCode() & 0x7FFFFFFF) % _pathLocks.Length])
                {
                    if (job.CacheFile != null && TryReadCache(job, info))
                    {
                        job.FromCache = true;
                        return;
                    }

                    if (TextureEncoder.TryEncode(job.PngPath, job.Width, job.Height, job.Format, out var raw, out var reason))
                    {
                        job.Raw = raw;
                        if (job.CacheFile != null && _cacheWritable) WriteCache(job, info);
                        return;
                    }
                    job.Note = reason;
                }
            }

            job.PngBytes = File.ReadAllBytes(job.PngPath);
        }

        private static bool TryReadCache(TextureJob job, FileInfo png)
        {
            try
            {
                if (!File.Exists(job.CacheFile)) return false;
                if (!TextureCacheFile.TryRead(job.CacheFile, true, png.Length, png.LastWriteTimeUtc.Ticks,
                        job.Width, job.Height, job.Format, out var data, out _))
                    return false;
                job.Raw = data;
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static void WriteCache(TextureJob job, FileInfo png)
        {
            string temp = job.CacheFile + ".tmp";
            try
            {
                TextureCacheFile.Write(temp, png.Length, png.LastWriteTimeUtc.Ticks, job.Width, job.Height, job.Format, job.Raw, false);
                if (File.Exists(job.CacheFile)) File.Delete(job.CacheFile);
                File.Move(temp, job.CacheFile);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Read-only install or full disk: keep working without the cache.
                _cacheWritable = false;
                job.Note = "texture cache disabled: " + ex.Message;
                try { File.Delete(temp); } catch { }
            }
        }
    }
}