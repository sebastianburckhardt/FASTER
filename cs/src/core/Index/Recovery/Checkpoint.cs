﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

#pragma warning disable 0162

//#define WAIT_FOR_INDEX_CHECKPOINT

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.core
{
    /// <summary>
    /// Linked list (chain) of checkpoint info
    /// </summary>
    public struct LinkedCheckpointInfo
    {
        /// <summary>
        /// Next task in checkpoint chain
        /// </summary>
        public Task<LinkedCheckpointInfo> NextTask;
    }
    
    internal static class EpochPhaseIdx
    {
        public const int Prepare = 0;
        public const int InProgress = 1;
        public const int WaitPending = 2;
        public const int WaitFlush = 3;
        public const int CheckpointCompletionCallback = 4;
    }

    public partial class FasterKV<Key, Value>
    {
        
        internal TaskCompletionSource<LinkedCheckpointInfo> checkpointTcs
            = new TaskCompletionSource<LinkedCheckpointInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            
        internal Guid _indexCheckpointToken;
        internal Guid _hybridLogCheckpointToken;
        internal HybridLogCheckpointInfo _hybridLogCheckpoint;
        internal HybridLogCheckpointInfo _lastSnapshotCheckpoint;

        internal Task<LinkedCheckpointInfo> CheckpointTask => checkpointTcs.Task;

        internal void AcquireSharedLatchesForAllPendingRequests<Input, Output, Context>(FasterExecutionContext<Input, Output, Context> ctx)
        {
            foreach (var _ctx in ctx.retryRequests)
            {
                AcquireSharedLatch(_ctx.key.Get());
            }

            foreach (var _ctx in ctx.ioPendingRequests.Values)
            {
                AcquireSharedLatch(_ctx.key.Get());
            }
        }
        
        internal void WriteHybridLogMetaInfo()
        {
            var metadata = _hybridLogCheckpoint.info.ToByteArray();
            if (CommitCookie != null && CommitCookie.Length != 0)
            {
                var convertedCookie = Convert.ToBase64String(CommitCookie);
                metadata = metadata.Concat(Encoding.Default.GetBytes(convertedCookie)).ToArray();
            }
            checkpointManager.CommitLogCheckpoint(_hybridLogCheckpointToken, metadata);
        }

        internal void WriteHybridLogIncrementalMetaInfo(DeltaLog deltaLog)
        {
            var metadata = _hybridLogCheckpoint.info.ToByteArray();
            if (CommitCookie != null && CommitCookie.Length != 0)
            {
                var convertedCookie = Convert.ToBase64String(CommitCookie);
                metadata = metadata.Concat(Encoding.Default.GetBytes(convertedCookie)).ToArray();
            }
            checkpointManager.CommitLogIncrementalCheckpoint(_hybridLogCheckpointToken, _hybridLogCheckpoint.info.version, metadata, deltaLog);
        }

        internal void WriteIndexMetaInfo()
        {
            checkpointManager.CommitIndexCheckpoint(_indexCheckpointToken, _indexCheckpoint.info.ToByteArray());
        }

        internal bool ObtainCurrentTailAddress(ref long location)
        {
            var tailAddress = hlog.GetTailAddress();
            return Interlocked.CompareExchange(ref location, tailAddress, 0) == 0;
        }

        internal void InitializeIndexCheckpoint(Guid indexToken)
        {
            _indexCheckpoint.Initialize(indexToken, state[resizeInfo.version].size, checkpointManager);
        }

        internal void InitializeHybridLogCheckpoint(Guid hybridLogToken, int version)
        {
            _hybridLogCheckpoint.Initialize(hybridLogToken, version, checkpointManager);
        }

        // #endregion
    }
}