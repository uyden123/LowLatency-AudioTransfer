using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AudioTransfer.Core.Buffers
{
    /// <summary>
    /// High-performance circular buffer using unmanaged memory.
    /// Designed for single-producer (WASAPI capture thread) / single-consumer (TCP sender thread).
    /// Uses volatile reads/writes for lock-free synchronization.
    /// </summary>
    public sealed unsafe class UnsafeCircularBuffer : IDisposable
    {
        private readonly byte* _buffer;
        private readonly int _capacity;

        private volatile int _writePos;
        private volatile int _readPos;
        private volatile int _dataAvailable;

        /// <summary>
        /// Spinlock for protecting write/read position updates.
        /// 0 = unlocked, 1 = locked.
        /// </summary>
        private int _spinLock;

        private bool _disposed;

        /// <summary>
        /// Number of bytes currently available for reading.
        /// </summary>
        public int Available => _dataAvailable;

        /// <summary>
        /// Total capacity in bytes.
        /// </summary>
        public int Capacity => _capacity;

        /// <summary>
        /// Create a circular buffer with the specified capacity in bytes.
        /// Memory is allocated from unmanaged heap for zero-GC operation.
        /// </summary>
        public UnsafeCircularBuffer(int capacityBytes)
        {
            if (capacityBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacityBytes));

            _capacity = capacityBytes;
            _buffer = (byte*)Marshal.AllocHGlobal(capacityBytes);

            // Zero-init
            new Span<byte>(_buffer, capacityBytes).Clear();

            _writePos = 0;
            _readPos = 0;
            _dataAvailable = 0;
            _spinLock = 0;
        }

        /// <summary>
        /// Acquire the spinlock. Spins until the lock is obtained.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void AcquireLock()
        {
            // SpinWait provides efficient spinning with backoff
            var spinner = new SpinWait();
            while (Interlocked.CompareExchange(ref _spinLock, 1, 0) != 0)
            {
                spinner.SpinOnce();
            }
        }

        /// <summary>
        /// Release the spinlock.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void ReleaseLock()
        {
            Volatile.Write(ref _spinLock, 0);
        }

        /// <summary>
        /// Write data into the circular buffer (producer side).
        /// If the buffer is full, oldest data will be overwritten.
        /// </summary>
        /// <param name="data">Source data pointer</param>
        /// <param name="length">Number of bytes to write</param>
        public void Write(byte* data, int length)
        {
            if (length <= 0 || length > _capacity) return;

            AcquireLock();
            try
            {
                // If not enough space, advance read pointer (drop oldest data)
                int space = _capacity - _dataAvailable;
                if (length > space)
                {
                    int overflow = length - space;
                    _readPos = (_readPos + overflow) % _capacity;
                    _dataAvailable -= overflow;
                }

                // Write data in up to two chunks (wrap-around)
                int firstChunk = Math.Min(length, _capacity - _writePos);
                Buffer.MemoryCopy(data, _buffer + _writePos, firstChunk, firstChunk);

                if (length > firstChunk)
                {
                    int secondChunk = length - firstChunk;
                    Buffer.MemoryCopy(data + firstChunk, _buffer, secondChunk, secondChunk);
                }

                _writePos = (_writePos + length) % _capacity;
                _dataAvailable += length;
            }
            finally
            {
                ReleaseLock();
            }
        }

        /// <summary>
        /// Write data from a managed byte array into the circular buffer.
        /// </summary>
        public void Write(byte[] data, int offset, int length)
        {
            if (length <= 0 || length > _capacity) return;

            fixed (byte* src = &data[offset])
            {
                Write(src, length);
            }
        }

        /// <summary>
        /// Read exactly 'length' bytes from the buffer into destination.
        /// Returns actual bytes read (may be less than requested if not enough data).
        /// </summary>
        /// <param name="dest">Destination pointer</param>
        /// <param name="length">Number of bytes to read</param>
        /// <returns>Actual bytes read</returns>
        public int Read(byte* dest, int length)
        {
            AcquireLock();
            try
            {
                int toRead = Math.Min(length, _dataAvailable);
                if (toRead <= 0) return 0;

                // Read in up to two chunks (wrap-around)
                int firstChunk = Math.Min(toRead, _capacity - _readPos);
                Buffer.MemoryCopy(_buffer + _readPos, dest, firstChunk, firstChunk);

                if (toRead > firstChunk)
                {
                    int secondChunk = toRead - firstChunk;
                    Buffer.MemoryCopy(_buffer, dest + firstChunk, secondChunk, secondChunk);
                }

                _readPos = (_readPos + toRead) % _capacity;
                _dataAvailable -= toRead;
                return toRead;
            }
            finally
            {
                ReleaseLock();
            }
        }

        /// <summary>
        /// Read data into a managed byte array.
        /// </summary>
        public int Read(byte[] dest, int offset, int length)
        {
            fixed (byte* dst = &dest[offset])
            {
                return Read(dst, length);
            }
        }

        /// <summary>
        /// Peek at available data count without locking.
        /// Use for checking if enough data is ready before calling Read.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public int PeekAvailable()
        {
            return Volatile.Read(ref _dataAvailable);
        }

        /// <summary>
        /// Clear all data in the buffer.
        /// </summary>
        public void Clear()
        {
            AcquireLock();
            try
            {
                _writePos = 0;
                _readPos = 0;
                _dataAvailable = 0;
            }
            finally
            {
                ReleaseLock();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Marshal.FreeHGlobal((IntPtr)_buffer);
            }
        }

        ~UnsafeCircularBuffer()
        {
            Dispose();
        }
    }
}
