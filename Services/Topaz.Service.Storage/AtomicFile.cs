namespace Topaz.Service.Storage;

/// <summary>
/// Atomically (re)writes a file: writes a uniquely-named temp file in the same directory, then renames it over
/// the target. Rename is atomic on a single volume (NTFS MoveFileEx / POSIX rename), so a concurrent reader - or
/// a crash/interruption mid-write - never observes a torn or zero-byte file: the target is always either the
/// complete old content or the complete new content.
///
/// <para>Plain <see cref="File.WriteAllText(string, string?)"/> truncates-then-writes and is neither atomic nor
/// crash-safe. A write interrupted after the truncate (or two writers racing on the same path) leaves an
/// empty/partial file that then fails to deserialize on every later read. That is the durability bug that
/// stranded both a queue trigger message (corrupted JSON) and a table stored entity (0 bytes) and
/// wedged consumer deployments. Callers should still hold their per-entity/per-queue lock to serialise read-modify-write
/// sequences; this guarantees each individual write is observed atomically by readers.</para>
///
/// <para>The temp file is named <c>{guid}.tmp</c> - not <c>*.json</c> - so it is never picked up by the
/// entity/message directory scans (which enumerate <c>*.json</c>).</para>
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        var tempPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(tempPath, content);
        try
        {
            // Retry the atomic replace on transient sharing errors. In-process writers are already serialised by
            // the caller's per-entity/per-queue lock, so the only contention here is an EXTERNAL transient handle:
            // on Windows the AV/indexer routinely scans the just-written temp file and briefly fails
            // MoveFileEx(REPLACE_EXISTING) with ACCESS_DENIED / sharing violation. On Linux the first attempt wins.
            const int maxAttempts = 10;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < maxAttempts)
                {
                    Thread.Sleep(attempt * 5);
                }
            }
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best-effort temp cleanup */ }
            throw;
        }
    }
}
