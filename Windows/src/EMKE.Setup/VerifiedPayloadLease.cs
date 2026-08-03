using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EMKE.Setup;

internal sealed class VerifiedPayloadLease : IDisposable
{
    private const uint FileAttributeNormal = 0x00000080;
    private readonly object _gate = new();
    private readonly SafeFileHandle _handle;
    private bool _closed;
    private bool? _cleanupSucceeded;

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

    internal bool Closed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    public T UseHandle<T>(Func<SafeFileHandle, T> action)
    {
        HandleBorrow borrow;
        lock (_gate)
        {
            ThrowIfClosedWithoutLock();
            ArgumentNullException.ThrowIfNull(action);
            borrow = new HandleBorrow(_handle);
        }

        using (borrow)
        {
            return action(borrow.Handle);
        }
    }

    public Stream OpenReadView()
    {
        lock (_gate)
        {
            ThrowIfClosedWithoutLock();
            return new HandleReadView(this);
        }
    }

    internal bool Cleanup()
    {
        lock (_gate)
        {
            if (_cleanupSucceeded.HasValue)
            {
                return _cleanupSucceeded.Value;
            }

            if (_closed)
            {
                _cleanupSucceeded = false;
                return false;
            }

            bool deleteMarked = SetFileAttributesByHandle(
                    _handle,
                    FileAttributeNormal)
                && TrySetDeleteDisposition(_handle);
            _cleanupSucceeded = deleteMarked;
            if (deleteMarked)
            {
                CloseOwnerHandleWithoutLock();
            }

            return deleteMarked;
        }
    }

    internal void CloseAfterResidual()
    {
        lock (_gate)
        {
            CloseOwnerHandleWithoutLock();
        }
    }

    public void Dispose()
    {
        if (Cleanup())
        {
            return;
        }

        CloseAfterResidual();
    }

    private void CloseOwnerHandleWithoutLock()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _handle.Dispose();
    }

    private static bool SetFileAttributesByHandle(
        SafeFileHandle handle,
        uint attributes)
    {
        FileBasicInformation information = new()
        {
            FileAttributes = attributes,
        };
        return SetFileBasicInformationByHandle(
            handle,
            FileInformationClass.FileBasicInfo,
            ref information,
            checked((uint)Marshal.SizeOf<FileBasicInformation>()));
    }

    private static bool TrySetDeleteDisposition(SafeFileHandle handle)
    {
        FileDispositionInformation information = new()
        {
            DeleteFile = true,
        };
        return SetFileDispositionByHandle(
            handle,
            FileInformationClass.FileDispositionInfo,
            ref information,
            checked((uint)Marshal.SizeOf<FileDispositionInformation>()));
    }

    private void ThrowIfClosed()
    {
        lock (_gate)
        {
            ThrowIfClosedWithoutLock();
        }
    }

    private void ThrowIfClosedWithoutLock()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
    }

    private int Read(Span<byte> buffer, long fileOffset)
    {
        HandleBorrow borrow;
        lock (_gate)
        {
            ThrowIfClosedWithoutLock();
            borrow = new HandleBorrow(_handle);
        }

        using (borrow)
        {
            return RandomAccess.Read(borrow.Handle, buffer, fileOffset);
        }
    }

    private sealed class HandleBorrow : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private Action? _releaseOwnerReference;

        public HandleBorrow(SafeFileHandle owner)
        {
            bool referenceAdded = false;
            try
            {
                owner.DangerousAddRef(ref referenceAdded);
                _handle = new SafeFileHandle(
                    owner.DangerousGetHandle(),
                    ownsHandle: false);
                _releaseOwnerReference = owner.DangerousRelease;
            }
            catch
            {
                if (referenceAdded)
                {
                    owner.DangerousRelease();
                }
                throw;
            }
        }

        public SafeFileHandle Handle => _handle;

        public void Dispose()
        {
            Action? releaseOwnerReference = Interlocked.Exchange(
                ref _releaseOwnerReference,
                null);
            if (releaseOwnerReference is null)
            {
                return;
            }

            try
            {
                _handle.Dispose();
            }
            finally
            {
                releaseOwnerReference();
            }
        }
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
            int read = _lease.Read(buffer, _position);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool DeleteFile;
    }

    private enum FileInformationClass
    {
        FileBasicInfo,
        FileStandardInfo,
        FileNameInfo,
        FileRenameInfo,
        FileDispositionInfo,
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "SetFileInformationByHandle",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileBasicInformationByHandle(
        SafeFileHandle file,
        FileInformationClass fileInformationClass,
        ref FileBasicInformation fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "SetFileInformationByHandle",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileDispositionByHandle(
        SafeFileHandle file,
        FileInformationClass fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);
}
