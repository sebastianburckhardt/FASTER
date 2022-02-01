﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace FASTER.core
{
    /// <summary>
    /// Provides thread management and callback to checkpoint completion (called state machine).
    /// </summary>
    // This is split to two interfaces just to limit infection of <Key, Value, Input, Output, Context> type parameters
    internal interface IFasterSession
    {
        void UnsafeResumeThread();
        void UnsafeSuspendThread();
        void CheckpointCompletionCallback(string sessionId, CommitPoint commitPoint);
    }

    /// <summary>
    /// Provides thread management and all callbacks. A wrapper for IFunctions and additional methods called by FasterImpl; the wrapped
    /// IFunctions methods provide additional parameters to support the wrapper functionality, then call through to the user implementations. 
    /// </summary>
    /// <typeparam name="Key"></typeparam>
    /// <typeparam name="Value"></typeparam>
    /// <typeparam name="Input"></typeparam>
    /// <typeparam name="Output"></typeparam>
    /// <typeparam name="Context"></typeparam>
    internal interface IFasterSession<Key, Value, Input, Output, Context> : IFasterSession, IVariableLengthStruct<Value, Input>
    {
        #region Optional features supported by this implementation
        bool DisableLocking { get; }

        bool IsManualLocking { get; }
        #endregion Optional features supported by this implementation

        #region Reads
        bool SingleReader(ref Key key, ref Input input, ref Value value, ref Output dst, ref RecordInfo recordInfo, long address);
        bool ConcurrentReader(ref Key key, ref Input input, ref Value value, ref Output dst, ref RecordInfo recordInfo, long address, out bool lockFailed);
        void ReadCompletionCallback(ref Key key, ref Input input, ref Output output, Context ctx, Status status, RecordMetadata recordMetadata);
        #endregion reads

        #region Upserts
        void SingleWriter(ref Key key, ref Input input, ref Value src, ref Value dst, ref Output output, ref RecordInfo recordInfo, long address, WriteReason reason);
        void PostSingleWriter(ref Key key, ref Input input, ref Value src, ref Value dst, ref Output output, ref RecordInfo recordInfo, long address, WriteReason reason);
        bool ConcurrentWriter(ref Key key, ref Input input, ref Value src, ref Value dst, ref Output output, ref RecordInfo recordInfo, long address, out bool lockFailed);
        void UpsertCompletionCallback(ref Key key, ref Input input, ref Value value, Context ctx);
        #endregion Upserts

        #region RMWs
        #region InitialUpdater
        bool NeedInitialUpdate(ref Key key, ref Input input, ref Output output);
        void InitialUpdater(ref Key key, ref Input input, ref Value value, ref Output output, ref RecordInfo recordInfo, long address);
        void PostInitialUpdater(ref Key key, ref Input input, ref Value value, ref Output output, ref RecordInfo recordInfo, long address);
        #endregion InitialUpdater

        #region CopyUpdater
        bool NeedCopyUpdate(ref Key key, ref Input input, ref Value oldValue, ref Output output);
        void CopyUpdater(ref Key key, ref Input input, ref Value oldValue, ref Value newValue, ref Output output, ref RecordInfo recordInfo, long address);
        bool PostCopyUpdater(ref Key key, ref Input input, ref Value oldValue, ref Value newValue, ref Output output, ref RecordInfo recordInfo, long address);
        #endregion CopyUpdater

        #region InPlaceUpdater
        bool InPlaceUpdater(ref Key key, ref Input input, ref Value value, ref Output output, ref RecordInfo recordInfo, long address, out bool lockFailed);
        #endregion InPlaceUpdater

        void RMWCompletionCallback(ref Key key, ref Input input, ref Output output, Context ctx, Status status, RecordMetadata recordMetadata);
        #endregion RMWs

        #region Deletes
        void SingleDeleter(ref Key key, ref Value value, ref RecordInfo recordInfo, long address);
        void PostSingleDeleter(ref Key key, ref RecordInfo recordInfo, long address);
        bool ConcurrentDeleter(ref Key key, ref Value value, ref RecordInfo recordInfo, long address, out bool lockFailed);
        void DeleteCompletionCallback(ref Key key, Context ctx);
        #endregion Deletes

        #region Key and Value management
        void DisposeKey(ref Key key);
        void DisposeValue(ref Value value);
        #endregion Key and Value management

        bool CompletePendingWithOutputs(out CompletedOutputIterator<Key, Value, Input, Output, Context> completedOutputs, bool wait = false, bool spinWaitForCommit = false);

        IHeapContainer<Input> GetHeapContainer(ref Input input);

        ITraceListener<Key> TraceListener { get; }
    }
}