// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Collections.Generic;
using Microsoft.Diagnostics.DataContractReader.Contracts.Extensions;
using Microsoft.Diagnostics.DataContractReader.Contracts.StackWalkHelpers;

namespace Microsoft.Diagnostics.DataContractReader.Contracts;

internal readonly struct StackWalk_1 : IStackWalk
{
    private readonly Target _target;

    internal StackWalk_1(Target target)
    {
        _target = target;
    }

    public enum StackWalkState
    {
        SW_COMPLETE,
        SW_ERROR,

        // The current Context is managed
        SW_FRAMELESS,

        // The current Context is unmanaged.
        // The next update will use a Frame to get a managed context
        // When SW_FRAME, the FrameAddress is valid
        SW_FRAME,
        SW_SKIPPED_FRAME,
    }

    private record StackDataFrameHandle(
        IPlatformAgnosticContext Context,
        StackWalkState State,
        TargetPointer FrameAddress) : IStackDataFrameHandle
    { }

    private class StackWalkData(IPlatformAgnosticContext context, StackWalkState state, FrameIterator frameIter)
    {
        public IPlatformAgnosticContext Context { get; set; } = context;
        public StackWalkState State { get; set; } = state;
        public FrameIterator FrameIter { get; set; } = frameIter;

        public StackDataFrameHandle ToDataFrame() => new(Context.Clone(), State, FrameIter.CurrentFrameAddress);
    }

    IEnumerable<IStackDataFrameHandle> IStackWalk.CreateStackWalk(ThreadData threadData)
    {
        IPlatformAgnosticContext context = IPlatformAgnosticContext.GetContextForPlatform(_target);
        FillContextFromThread(context, threadData);
        StackWalkState state = IsManaged(context.InstructionPointer, out _) ? StackWalkState.SW_FRAMELESS : StackWalkState.SW_FRAME;
        FrameIterator frameIterator = new(_target, threadData);

        // if the next Frame is not valid and we are not in managed code, there is nothing to return
        if (state == StackWalkState.SW_FRAME && !frameIterator.IsValid())
        {
            yield break;
        }

        StackWalkData stackWalkData = new(context, state, frameIterator);

        yield return stackWalkData.ToDataFrame();

        while (Next(stackWalkData))
        {
            yield return stackWalkData.ToDataFrame();
        }
    }

    private bool Next(StackWalkData handle)
    {
        switch (handle.State)
        {
            case StackWalkState.SW_FRAMELESS:
                try
                {
                    handle.Context.Unwind(_target);
                }
                catch
                {
                    handle.State = StackWalkState.SW_ERROR;
                    throw;
                }
                break;
            case StackWalkState.SW_SKIPPED_FRAME:
                handle.FrameIter.Next();
                break;
            case StackWalkState.SW_FRAME:
                handle.FrameIter.UpdateContextFromFrame(handle.Context);
                if (!handle.FrameIter.IsInlineCallFrameWithActiveCall())
                {
                    handle.FrameIter.Next();
                }
                break;
            case StackWalkState.SW_ERROR:
            case StackWalkState.SW_COMPLETE:
                return false;
        }
        UpdateState(handle);

        return handle.State is not (StackWalkState.SW_ERROR or StackWalkState.SW_COMPLETE);
    }

    private void UpdateState(StackWalkData handle)
    {
        // If we are complete or in a bad state, no updating is required.
        if (handle.State is StackWalkState.SW_ERROR or StackWalkState.SW_COMPLETE)
        {
            return;
        }

        bool isManaged = IsManaged(handle.Context.InstructionPointer, out _);
        bool validFrame = handle.FrameIter.IsValid();

        if (isManaged)
        {
            handle.State = StackWalkState.SW_FRAMELESS;
            if (CheckForSkippedFrames(handle))
            {
                handle.State = StackWalkState.SW_SKIPPED_FRAME;
                return;
            }
        }
        else
        {
            handle.State = validFrame ? StackWalkState.SW_FRAME : StackWalkState.SW_COMPLETE;
        }
    }

    /// <summary>
    /// If an explicit frame is allocated in a managed stack frame (e.g. an inlined pinvoke call),
    /// we may have skipped an explicit frame.  This function checks for them.
    /// </summary>
    /// <returns> true if there are skipped frames. </returns>
    private bool CheckForSkippedFrames(StackWalkData handle)
    {
        // ensure we can find the caller context
        Debug.Assert(IsManaged(handle.Context.InstructionPointer, out _));

        // if there are no more Frames, vacuously false
        if (!handle.FrameIter.IsValid())
        {
            return false;
        }

        // get the caller context
        IPlatformAgnosticContext parentContext = handle.Context.Clone();
        parentContext.Unwind(_target);

        return handle.FrameIter.CurrentFrameAddress.Value < parentContext.StackPointer.Value;
    }

    byte[] IStackWalk.GetRawContext(IStackDataFrameHandle stackDataFrameHandle)
    {
        StackDataFrameHandle handle = AssertCorrectHandle(stackDataFrameHandle);
        return handle.Context.GetBytes();
    }

    TargetPointer IStackWalk.GetFrameAddress(IStackDataFrameHandle stackDataFrameHandle)
    {
        StackDataFrameHandle handle = AssertCorrectHandle(stackDataFrameHandle);
        if (handle.State is StackWalkState.SW_FRAME or StackWalkState.SW_SKIPPED_FRAME)
        {
            return handle.FrameAddress;
        }
        return TargetPointer.Null;
    }

    string IStackWalk.GetFrameName(TargetPointer frameIdentifier)
        => FrameIterator.GetFrameName(_target, frameIdentifier);

    private bool IsManaged(TargetPointer ip, [NotNullWhen(true)] out CodeBlockHandle? codeBlockHandle)
    {
        IExecutionManager eman = _target.Contracts.ExecutionManager;
        TargetCodePointer codePointer = CodePointerUtils.CodePointerFromAddress(ip, _target);
        if (eman.GetCodeBlockHandle(codePointer) is CodeBlockHandle cbh && cbh.Address != TargetPointer.Null)
        {
            codeBlockHandle = cbh;
            return true;
        }
        codeBlockHandle = default;
        return false;
    }

    private unsafe void FillContextFromThread(IPlatformAgnosticContext context, ThreadData threadData)
    {
        byte[] bytes = new byte[context.Size];
        Span<byte> buffer = new Span<byte>(bytes);
        // The underlying ICLRDataTarget.GetThreadContext has some variance depending on the host.
        // SOS's managed implementation sets the ContextFlags to platform specific values defined in ThreadService.cs (diagnostics repo)
        // SOS's native implementation keeps the ContextFlags passed into this function.
        // To match the DAC behavior, the DefaultContextFlags are what the DAC passes in in DacGetThreadContext.
        // In most implementations, this will be overridden by the host, but in some cases, it may not be.
        if (!_target.TryGetThreadContext(threadData.OSId.Value, context.DefaultContextFlags, buffer))
        {
            throw new InvalidOperationException($"GetThreadContext failed for thread {threadData.OSId.Value}");
        }

        context.FillFromBuffer(buffer);
    }

    private static StackDataFrameHandle AssertCorrectHandle(IStackDataFrameHandle stackDataFrameHandle)
    {
        if (stackDataFrameHandle is not StackDataFrameHandle handle)
        {
            throw new ArgumentException("Invalid stack data frame handle", nameof(stackDataFrameHandle));
        }

        return handle;
    }
};
