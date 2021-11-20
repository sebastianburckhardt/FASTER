﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace FASTER.core
{
    /// <summary>
    /// Wrapper to process log-related commands
    /// </summary>
    /// <typeparam name="Key"></typeparam>
    /// <typeparam name="Value"></typeparam>
    public sealed class LogAccessor<Key, Value> : IObservable<IFasterScanIterator<Key, Value>>
    {
        private readonly FasterKV<Key, Value> fht;
        private readonly AllocatorBase<Key, Value> allocator;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="fht"></param>
        /// <param name="allocator"></param>
        public LogAccessor(FasterKV<Key, Value> fht, AllocatorBase<Key, Value> allocator)
        {
            this.fht = fht;
            this.allocator = allocator;
        }

        /// <summary>
        /// Tail address of log
        /// </summary>
        public long TailAddress => allocator.GetTailAddress();

        /// <summary>
        /// Read-only address of log, i.e. boundary between read-only region and mutable region
        /// </summary>
        public long ReadOnlyAddress => allocator.ReadOnlyAddress;

        /// <summary>
        /// Safe read-only address of log, i.e. boundary between read-only region and mutable region
        /// </summary>
        public long SafeReadOnlyAddress => allocator.SafeReadOnlyAddress;

        /// <summary>
        /// Head address of log, i.e. beginning of in-memory regions
        /// </summary>
        public long HeadAddress => allocator.HeadAddress;

        /// <summary>
        /// Beginning address of log
        /// </summary>
        public long BeginAddress => allocator.BeginAddress;

        /// <summary>
        /// Get the bytes used on the primary log by every record. Does not include
        /// the size of variable-length inline data. Note that class objects occupy
        /// 8 bytes (reference) on the main log (i.e., the heap space occupied by
        /// class objects is not included in the result of this call).
        /// </summary>
        public int FixedRecordSize => allocator.GetFixedRecordSize();

        /// <summary>
        /// Number of pages left empty or unallocated in the in-memory buffer (between 0 and BufferSize-1)
        /// </summary>
        public int EmptyPageCount
        {
            get => allocator.EmptyPageCount;
            set { allocator.EmptyPageCount = value; }
        }

        /// <summary>
        /// Set empty page count in allocator
        /// </summary>
        /// <param name="pageCount">New empty page count</param>
        /// <param name="wait">Whether to wait for shift addresses to complete</param>
        public void SetEmptyPageCount(int pageCount, bool wait = false)
        {
            allocator.EmptyPageCount = pageCount;
            if (wait)
            {
                long newHeadAddress = (allocator.GetTailAddress() & ~allocator.PageSizeMask) - allocator.HeadOffsetLagAddress;
                ShiftHeadAddress(newHeadAddress, wait);
            }
        }

        /// <summary>
        /// Total in-memory circular buffer capacity (in number of pages)
        /// </summary>
        public int BufferSize => allocator.BufferSize;

        /// <summary>
        /// Actual memory used by log (not including heap objects)
        /// </summary>
        public long MemorySizeBytes => ((long)(allocator.AllocatedPageCount + allocator.OverflowPageCount)) << allocator.LogPageSizeBits;

        /// <summary>
        /// Memory allocatable on the log (not including heap objects)
        /// </summary>
        public long AllocatableMemorySizeBytes => ((long)(allocator.BufferSize - allocator.EmptyPageCount + allocator.OverflowPageCount)) << allocator.LogPageSizeBits;

        /// <summary>
        /// Truncate the log until, but not including, untilAddress. Make sure address corresponds to record boundary if snapToPageStart is set to false.
        /// </summary>
        /// <param name="untilAddress">Address to shift begin address until</param>
        /// <param name="snapToPageStart">Whether given address should be snapped to nearest earlier page start address</param>
        public void ShiftBeginAddress(long untilAddress, bool snapToPageStart = false)
        {
            if (snapToPageStart)
                untilAddress &= ~allocator.PageSizeMask;

            allocator.ShiftBeginAddress(untilAddress);
        }

        /// <summary>
        /// Shift log head address to prune memory foorprint of hybrid log
        /// </summary>
        /// <param name="newHeadAddress">Address to shift head until</param>
        /// <param name="wait">Wait for operation to complete (may involve page flushing and closing)</param>
        public void ShiftHeadAddress(long newHeadAddress, bool wait)
        {
            // First shift read-only
            // Force wait so that we do not close unflushed page
            ShiftReadOnlyAddress(newHeadAddress, true);

            // Then shift head address
            if (!fht.epoch.ThisInstanceProtected())
            {
                try
                {
                    fht.epoch.Resume();
                    allocator.ShiftHeadAddress(newHeadAddress);
                }
                finally
                {
                    fht.epoch.Suspend();
                }

                while (wait && allocator.SafeHeadAddress < newHeadAddress) Thread.Yield();
            }
            else
            {
                allocator.ShiftHeadAddress(newHeadAddress);
                while (wait && allocator.SafeHeadAddress < newHeadAddress)
                    fht.epoch.ProtectAndDrain();
            }
        }

        /// <summary>
        /// Subscribe to records (in batches) as they become read-only in the log
        /// Currently, we support only one subscriber to the log (easy to extend)
        /// Subscriber only receives new log updates from the time of subscription onwards
        /// To scan the historical part of the log, use the Scan(...) method
        /// </summary>
        /// <param name="readOnlyObserver">Observer to which scan iterator is pushed</param>
        public IDisposable Subscribe(IObserver<IFasterScanIterator<Key, Value>> readOnlyObserver)
        {
            allocator.OnReadOnlyObserver = readOnlyObserver;
            return new LogSubscribeDisposable(allocator, true);
        }

        /// <summary>
        /// Subscribe to records (in batches) as they get evicted from main memory.
        /// Currently, we support only one subscriber to the log (easy to extend)
        /// Subscriber only receives eviction updates from the time of subscription onwards
        /// To scan the historical part of the log, use the Scan(...) method
        /// </summary>
        /// <param name="evictionObserver">Observer to which scan iterator is pushed</param>
        public IDisposable SubscribeEvictions(IObserver<IFasterScanIterator<Key, Value>> evictionObserver)
        {
            allocator.OnEvictionObserver = evictionObserver;
            return new LogSubscribeDisposable(allocator, false);
        }

        /// <summary>
        /// Wrapper to help dispose the subscription
        /// </summary>
        class LogSubscribeDisposable : IDisposable
        {
            private readonly AllocatorBase<Key, Value> allocator;
            private readonly bool readOnly;

            public LogSubscribeDisposable(AllocatorBase<Key, Value> allocator, bool readOnly)
            {
                this.allocator = allocator;
                this.readOnly = readOnly;
            }

            public void Dispose()
            {
                if (readOnly)
                    allocator.OnReadOnlyObserver = null;
                else
                    allocator.OnEvictionObserver = null;
            }
        }

        /// <summary>
        /// Shift log read-only address
        /// </summary>
        /// <param name="newReadOnlyAddress">Address to shift read-only until</param>
        /// <param name="wait">Wait to ensure shift is complete (may involve page flushing)</param>
        public void ShiftReadOnlyAddress(long newReadOnlyAddress, bool wait)
        {
            if (!fht.epoch.ThisInstanceProtected())
            {
                try
                {
                    fht.epoch.Resume();
                    allocator.ShiftReadOnlyAddress(newReadOnlyAddress);
                }
                finally
                {
                    fht.epoch.Suspend();
                }

                // Wait for flush to complete
                while (wait && allocator.FlushedUntilAddress < newReadOnlyAddress) Thread.Yield();
            }
            else
            {
                allocator.ShiftReadOnlyAddress(newReadOnlyAddress);

                // Wait for flush to complete
                while (wait && allocator.FlushedUntilAddress < newReadOnlyAddress)
                    fht.epoch.ProtectAndDrain();
            }
        }

        /// <summary>
        /// Scan the log given address range, returns all records with address less than endAddress
        /// </summary>
        /// <param name="beginAddress"></param>
        /// <param name="endAddress"></param>
        /// <param name="scanBufferingMode"></param>
        /// <returns></returns>
        public IFasterScanIterator<Key, Value> Scan(long beginAddress, long endAddress, ScanBufferingMode scanBufferingMode = ScanBufferingMode.DoublePageBuffering)
        {
            return allocator.Scan(beginAddress, endAddress, scanBufferingMode);
        }

        /// <summary>
        /// Flush log until current tail (records are still retained in memory)
        /// </summary>
        /// <param name="wait">Synchronous wait for operation to complete</param>
        public void Flush(bool wait)
        {
            ShiftReadOnlyAddress(allocator.GetTailAddress(), wait);
        }

        /// <summary>
        /// Flush log and evict all records from memory
        /// </summary>
        /// <param name="wait">Wait for operation to complete</param>
        public void FlushAndEvict(bool wait)
        {
            ShiftHeadAddress(allocator.GetTailAddress(), wait);
        }

        /// <summary>
        /// Delete log entirely from memory. Cannot allocate on the log
        /// after this point. This is a synchronous operation.
        /// </summary>
        public void DisposeFromMemory()
        {
            // Ensure we have flushed and evicted
            FlushAndEvict(true);

            // Delete from memory
            allocator.DeleteFromMemory();
        }

        /// <summary>
        /// Compact the log until specified address, moving active records to the tail of the log.
        /// </summary>
        /// <param name="functions">Functions used to manage key-values during compaction</param>
        /// <param name="untilAddress">Compact log until this address</param>
        /// <param name="compactionType">Compaction type (whether we lookup records or scan log for liveness checking)</param>
        /// <param name="sessionVariableLengthStructSettings">Session variable length struct settings</param>
        /// <returns>Address until which compaction was done, caller is responsible for shifting begin address</returns>
        public long Compact<Input, Output, Context, Functions>(Functions functions, long untilAddress, CompactionType compactionType, SessionVariableLengthStructSettings<Value, Input> sessionVariableLengthStructSettings = null)
            where Functions : IFunctions<Key, Value, Input, Output, Context>
            => Compact<Input, Output, Context, Functions, DefaultCompactionFunctions<Key, Value>>(functions, default, untilAddress, compactionType, sessionVariableLengthStructSettings);

        /// <summary>
        /// Compact the log until specified address, moving active records to the tail of the log.
        /// </summary>
        /// <param name="functions">Functions used to manage key-values during compaction</param>
        /// <param name="cf">User provided compaction functions (see <see cref="ICompactionFunctions{Key, Value}"/>)</param>
        /// <param name="untilAddress">Compact log until this address</param>
        /// <param name="compactionType">Compaction type (whether we lookup records or scan log for liveness checking)</param>
        /// <param name="sessionVariableLengthStructSettings">Session variable length struct settings</param>
        /// <returns>Address until which compaction was done, caller is responsible for shifting begin address</returns>
        public long Compact<Input, Output, Context, Functions, CompactionFunctions>(Functions functions, CompactionFunctions cf, long untilAddress, CompactionType compactionType, SessionVariableLengthStructSettings<Value, Input> sessionVariableLengthStructSettings = null)
            where Functions : IFunctions<Key, Value, Input, Output, Context>
            where CompactionFunctions : ICompactionFunctions<Key, Value>
            => fht.Compact<Input, Output, Context, Functions, CompactionFunctions>(functions, cf, untilAddress, compactionType, sessionVariableLengthStructSettings);
    }
}
