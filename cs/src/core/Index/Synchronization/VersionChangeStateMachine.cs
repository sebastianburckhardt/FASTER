﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.core
{
    /// <summary>
    /// A Version change captures a version on the log by forcing all threads to coordinate a move to the next
    /// version. It is used as the basis of many other tasks, which decides what they do with the captured
    /// version.
    /// </summary>
    internal sealed class VersionChangeTask : ISynchronizationTask
    {
        /// <inheritdoc />
        public void GlobalBeforeEnteringState<Key, Value>(
            SystemState next,
            FasterKV<Key, Value> faster)
        {
        }

        /// <inheritdoc />
        public void GlobalAfterEnteringState<Key, Value>(
            SystemState start,
            FasterKV<Key, Value> faster)
        {
        }

        /// <inheritdoc />
        public void OnThreadState<Key, Value, Input, Output, Context, FasterSession>(
            SystemState current, SystemState prev,
            FasterKV<Key, Value> faster,
            FasterKV<Key, Value>.FasterExecutionContext<Input, Output, Context> ctx,
            FasterSession fasterSession,
            List<ValueTask> valueTasks,
            CancellationToken token = default)
            where FasterSession : IFasterSession
        {
            switch (current.Phase)
            {
                case Phase.PREPARE:
                    if (ctx != null)
                    {
                        if (!ctx.markers[EpochPhaseIdx.Prepare])
                        {
                            if (!faster.RelaxedCPR)
                                faster.AcquireSharedLatchesForAllPendingRequests(ctx);
                            ctx.markers[EpochPhaseIdx.Prepare] = true;
                        }

                        faster.epoch.Mark(EpochPhaseIdx.Prepare, current.Version);
                    }

                    if (faster.epoch.CheckIsComplete(EpochPhaseIdx.Prepare, current.Version))
                        faster.GlobalStateMachineStep(current);
                    break;
                case Phase.IN_PROGRESS:
                    if (ctx != null)
                    {
                        // Need to be very careful here as threadCtx is changing
                        var _ctx = prev.Phase == Phase.IN_PROGRESS ? ctx.prevCtx : ctx;
                        var tokens = faster._hybridLogCheckpoint.info.checkpointTokens;
                        if (!faster.SameCycle(ctx, current) || tokens == null)
                            return;

                        if (!_ctx.markers[EpochPhaseIdx.InProgress])
                        {
                            faster.AtomicSwitch(ctx, ctx.prevCtx, _ctx.version, tokens);
                            faster.InitContext(ctx, ctx.prevCtx.guid, ctx.prevCtx.serialNum);

                            // Has to be prevCtx, not ctx
                            ctx.prevCtx.markers[EpochPhaseIdx.InProgress] = true;
                        }

                        faster.epoch.Mark(EpochPhaseIdx.InProgress, current.Version);
                    }

                    // Has to be prevCtx, not ctx
                    if (faster.epoch.CheckIsComplete(EpochPhaseIdx.InProgress, current.Version))
                        faster.GlobalStateMachineStep(current);
                    break;
                case Phase.WAIT_PENDING:
                    if (ctx != null)
                    {
                        if (!faster.RelaxedCPR && !ctx.prevCtx.markers[EpochPhaseIdx.WaitPending])
                        {
                            if (ctx.prevCtx.HasNoPendingRequests)
                                ctx.prevCtx.markers[EpochPhaseIdx.WaitPending] = true;
                            else
                                break;
                        }

                        faster.epoch.Mark(EpochPhaseIdx.WaitPending, current.Version);
                    }

                    if (faster.epoch.CheckIsComplete(EpochPhaseIdx.WaitPending, current.Version))
                        faster.GlobalStateMachineStep(current);
                    break;
                case Phase.REST:
                    break;
            }
        }
    }

    /// <summary>
    /// The FoldOver task simply sets the read only offset to the current end of the log, so a captured version
    /// is immutable and will eventually be flushed to disk.
    /// </summary>
    internal sealed class FoldOverTask : ISynchronizationTask
    {
        /// <inheritdoc />
        public void GlobalBeforeEnteringState<Key, Value>(
            SystemState next,
            FasterKV<Key, Value> faster)
        {
            if (next.Phase == Phase.REST)
                // Before leaving the checkpoint, make sure all previous versions are read-only.
                faster.hlog.ShiftReadOnlyToTail(out _, out _);
        }

        /// <inheritdoc />
        public void GlobalAfterEnteringState<Key, Value>(
            SystemState next,
            FasterKV<Key, Value> faster)
        { }

        /// <inheritdoc />
        public void OnThreadState<Key, Value, Input, Output, Context, FasterSession>(
            SystemState current,
            SystemState prev,
            FasterKV<Key, Value> faster,
            FasterKV<Key, Value>.FasterExecutionContext<Input, Output, Context> ctx,
            FasterSession fasterSession,
            List<ValueTask> valueTasks,
            CancellationToken token = default)
            where FasterSession : IFasterSession
        {
        }
    }

    /// <summary>
    /// A VersionChangeStateMachine orchestrates to capture a version, but does not flush to disk.
    /// </summary>
    internal class VersionChangeStateMachine : SynchronizationStateMachineBase
    {
        private long targetVersion;

        /// <summary>
        /// Construct a new VersionChangeStateMachine with the given tasks. Does not load any tasks by default.
        /// </summary>
        /// <param name="targetVersion">upper limit (inclusive) of the version included</param>
        /// <param name="tasks">The tasks to load onto the state machine</param>
        protected VersionChangeStateMachine(long targetVersion = -1, params ISynchronizationTask[] tasks) : base(tasks)
        {
            this.targetVersion = targetVersion;
        }

        /// <summary>
        /// Construct a new VersionChangeStateMachine that folds over the log at the end without waiting for flush. 
        /// </summary>
        /// <param name="targetVersion">upper limit (inclusive) of the version included</param>
        public VersionChangeStateMachine(long targetVersion = -1) : this(targetVersion, new VersionChangeTask(), new FoldOverTask()) { }

        /// <inheritdoc />
        public override SystemState NextState(SystemState start)
        {
            var nextState = SystemState.Copy(ref start);
            switch (start.Phase)
            {
                case Phase.REST:
                    nextState.Phase = Phase.PREPARE;
                    break;
                case Phase.PREPARE:
                    nextState.Phase = Phase.IN_PROGRESS;
                    // 13 bits of 1s --- FASTER records only store 13 bits of version number, and we need to ensure that
                    // the next version is distinguishable from the last in those 13 bits.
                    var bitMask = (1L << 13) - 1;
                    // If they are not distinguishable, simply increment target version to resolve this
                    if (((targetVersion - start.Version) & bitMask) == 0)
                        targetVersion++;

                    // TODO: Move to long for system state as well. 
                    SetToVersion(targetVersion == -1 ? start.Version + 1 : targetVersion);
                    nextState.Version = (int) ToVersion();
                    break;
                case Phase.IN_PROGRESS:
                    // This phase has no effect if using relaxed CPR model
                    nextState.Phase = Phase.WAIT_PENDING;
                    break;
                case Phase.WAIT_PENDING:
                    nextState.Phase = Phase.REST;
                    break;
                default:
                    throw new FasterException("Invalid Enum Argument");
            }

            return nextState;
        }
    }
}