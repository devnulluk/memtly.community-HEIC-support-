namespace Memtly.Community.Wedding;

/// <summary>
/// Single-process admission guard on the filesystem containing private wedding media.
/// Reservations cover bytes not yet written, avoiding double-counting data already on disk.
/// Other processes can still consume space: this is a safeguard, not a filesystem quota.
/// </summary>
public sealed class WeddingDiskSpace
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, long> pending = new();
    private readonly Func<long> available;
    private readonly long minimum;

    public WeddingDiskSpace(string path, long minimum, Func<long>? available = null)
    {
        var fullPath = Path.GetFullPath(path);
        this.minimum = minimum;
        this.available = available ?? (() => new DriveInfo(OperatingSystem.IsWindows()
            ? Path.GetPathRoot(fullPath)! : fullPath).AvailableFreeSpace);
    }

    private void Ensure(long additional = 0)
    {
        if ((decimal)available() - pending.Values.Sum(v => (decimal)v) - additional < minimum)
            throw new WeddingUploadException(507, "There isn't enough free space to save this file safely. Please keep your original and let Mark or Keziah know.");
    }

    public Reservation Reserve(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (gate)
        {
            Ensure(bytes);
            var id = Guid.NewGuid();
            pending.Add(id, bytes);
            return new Reservation(this, id);
        }
    }

    public sealed class Reservation : IDisposable
    {
        private readonly WeddingDiskSpace space;
        private readonly Guid id;
        private bool disposed;
        internal Reservation(WeddingDiskSpace space, Guid id) { this.space = space; this.id = id; }
        public void Written(long bytes)
        {
            lock (space.gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (bytes < 0 || bytes > space.pending[id]) throw new ArgumentOutOfRangeException(nameof(bytes));
                space.pending[id] -= bytes;
            }
        }
        public void Check()
        {
            lock (space.gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                space.Ensure();
            }
        }
        public void Dispose()
        {
            lock (space.gate)
            {
                if (disposed) return;
                space.pending.Remove(id);
                disposed = true;
            }
        }
    }
}
