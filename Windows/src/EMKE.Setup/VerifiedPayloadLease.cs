using Microsoft.Win32.SafeHandles;

namespace EMKE.Setup;

internal sealed class VerifiedPayloadLease : IDisposable
{
    private readonly SafeFileHandle _handle;
    private bool _closed;

    public VerifiedPayloadLease(
        SafeFileHandle handle,
        string logicalName,
        string displayPath)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayPath);
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new ArgumentException(
                "The payload lease requires an open file handle.",
                nameof(handle));
        }

        _handle = handle;
        LogicalName = logicalName;
        DisplayPath = displayPath;
    }

    public string LogicalName { get; }

    public string DisplayPath { get; }

    internal bool Closed => _closed;

    public T UseHandle<T>(Func<SafeFileHandle, T> action)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        ArgumentNullException.ThrowIfNull(action);
        return action(_handle);
    }

    public Stream OpenReadView()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        return new HandleReadView(this);
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _handle.Dispose();
    }

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
    }

    private sealed class HandleReadView : Stream
    {
        private readonly VerifiedPayloadLease _lease;
        private readonly long _length;
        private long _position;
        private bool _disposed;

        public HandleReadView(VerifiedPayloadLease lease)
        {
            _lease = lease;
            _length = lease.UseHandle(RandomAccess.GetLength);
        }

        public override bool CanRead => !_disposed && !_lease.Closed;

        public override bool CanSeek => !_disposed && !_lease.Closed;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                ThrowIfUnavailable();
                return _length;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfUnavailable();
                return _position;
            }
            set => _ = Seek(value, SeekOrigin.Begin);
        }

        public override void Flush()
        {
            ThrowIfUnavailable();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowIfUnavailable();
            int read = RandomAccess.Read(_lease._handle, buffer, _position);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfUnavailable();
            long next;
            try
            {
                next = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => checked(_position + offset),
                    SeekOrigin.End => checked(_length + offset),
                    _ => throw new ArgumentOutOfRangeException(nameof(origin)),
                };
            }
            catch (OverflowException exception)
            {
                throw new IOException(
                    "Cannot seek outside the verified payload.",
                    exception);
            }

            if (next < 0 || next > _length)
            {
                throw new IOException("Cannot seek outside the verified payload.");
            }

            _position = next;
            return _position;
        }

        public override void SetLength(long value)
        {
            ThrowIfUnavailable();
            throw new NotSupportedException("The payload read view is read-only.");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfUnavailable();
            throw new NotSupportedException("The payload read view is read-only.");
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfUnavailable();
            throw new NotSupportedException("The payload read view is read-only.");
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }

        private void ThrowIfUnavailable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lease.ThrowIfClosed();
        }
    }
}
