// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;

namespace System.Threading
{
    //
    // Implementation of ThreadPoolBoundHandle that sits on top of the Win32 ThreadPool
    //
    public sealed class ThreadPoolBoundHandle : IDisposable, IDeferredDisposable
    {
        private bool _isDisposed;
        private readonly SafeHandle _handle;
        private DeferredDisposableLifetime<ThreadPoolBoundHandle> _lifetime;

        private ThreadPoolBoundHandle(SafeHandle handle)
        {
            _handle = handle;
        }

        public SafeHandle Handle
        {
            get { return _handle; }
        }

        private static ThreadPoolBoundHandle BindHandleCore(SafeHandle handle)
        {
            Debug.Assert(handle != null);
            Debug.Assert(!handle.IsClosed);
            Debug.Assert(!handle.IsInvalid);

            try
            {
                Debug.Assert(OperatingSystem.IsWindows());
                // ThreadPool.BindHandle will always return true, otherwise, it throws.
                bool succeeded = ThreadPool.BindHandle(handle);
                Debug.Assert(succeeded);
            }
            catch (Exception ex)
            {   // BindHandle throws ApplicationException on full CLR and Exception on CoreCLR.
                // We do not let either of these leak and convert them to ArgumentException to
                // indicate that the specified handles are invalid.

                if (ex.HResult == HResults.E_HANDLE)         // Bad handle
                    throw new ArgumentException(SR.Argument_InvalidHandle, nameof(handle));

                if (ex.HResult == HResults.E_INVALIDARG)     // Handle already bound or sync handle
                    throw new ArgumentException(SR.Argument_AlreadyBoundOrSyncHandle, nameof(handle));

                throw;
            }

            return new ThreadPoolBoundHandle(handle);
        }

        public static unsafe ThreadPoolBoundHandle BindHandle(SafeHandle handle)
        {
            ArgumentNullException.ThrowIfNull(handle);

            if (handle.IsClosed || handle.IsInvalid)
                throw new ArgumentException(SR.Argument_InvalidHandle, nameof(handle));

            return BindHandleCore(handle);
        }

        [CLSCompliant(false)]
        public unsafe NativeOverlapped* AllocateNativeOverlapped(IOCompletionCallback callback, object? state, object? pinData) =>
            AllocateNativeOverlapped(callback, state, pinData, flowExecutionContext: true);

        [CLSCompliant(false)]
        public unsafe NativeOverlapped* UnsafeAllocateNativeOverlapped(IOCompletionCallback callback, object? state, object? pinData) =>
            AllocateNativeOverlapped(callback, state, pinData, flowExecutionContext: false);

        private unsafe NativeOverlapped* AllocateNativeOverlapped(IOCompletionCallback callback, object? state, object? pinData, bool flowExecutionContext)
        {
            ArgumentNullException.ThrowIfNull(callback);
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            ThreadPoolBoundHandleOverlapped overlapped = new ThreadPoolBoundHandleOverlapped(callback, state, pinData, preAllocated: null, flowExecutionContext);
            overlapped._boundHandle = this;
            return overlapped._nativeOverlapped;
        }

        [CLSCompliant(false)]
        public unsafe NativeOverlapped* AllocateNativeOverlapped(PreAllocatedOverlapped preAllocated)
        {
            ArgumentNullException.ThrowIfNull(preAllocated);
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            preAllocated.AddRef();
            try
            {
                ThreadPoolBoundHandleOverlapped overlapped = preAllocated._overlapped;

                if (overlapped._boundHandle != null)
                    throw new ArgumentException(SR.Argument_PreAllocatedAlreadyAllocated, nameof(preAllocated));

                overlapped._boundHandle = this;

                return overlapped._nativeOverlapped;
            }
            catch
            {
                preAllocated.Release();
                throw;
            }
        }

        private static unsafe ThreadPoolBoundHandleOverlapped GetOverlappedWrapper(NativeOverlapped* overlapped)
        {
            ThreadPoolBoundHandleOverlapped wrapper;
            try
            {
                wrapper = (ThreadPoolBoundHandleOverlapped)Overlapped.Unpack(overlapped);
            }
            catch (NullReferenceException ex)
            {
                throw new ArgumentException(SR.Argument_NativeOverlappedAlreadyFree, nameof(overlapped), ex);
            }

            return wrapper;
        }

        [CLSCompliant(false)]
        public unsafe void FreeNativeOverlapped(NativeOverlapped* overlapped)
        {
            ArgumentNullException.ThrowIfNull(overlapped);

            // Note: we explicitly allow FreeNativeOverlapped calls after the ThreadPoolBoundHandle has been Disposed.

            ThreadPoolBoundHandleOverlapped wrapper = GetOverlappedWrapper(overlapped);

            if (wrapper._boundHandle != this)
                throw new ArgumentException(SR.Argument_NativeOverlappedWrongBoundHandle, nameof(overlapped));

            if (wrapper._preAllocated != null)
                wrapper._preAllocated.Release();
            else
                Overlapped.Free(overlapped);
        }

        [CLSCompliant(false)]
        public static unsafe object? GetNativeOverlappedState(NativeOverlapped* overlapped)
        {
            ArgumentNullException.ThrowIfNull(overlapped);

            Win32ThreadPoolNativeOverlapped* threadPoolOverlapped = Win32ThreadPoolNativeOverlapped.FromNativeOverlapped(overlapped);
            Win32ThreadPoolNativeOverlapped.OverlappedData data = GetOverlappedData(threadPoolOverlapped, null);

            return data._state;
        }

        private static unsafe Win32ThreadPoolNativeOverlapped.OverlappedData GetOverlappedData(Win32ThreadPoolNativeOverlapped* overlapped, ThreadPoolBoundHandle? expectedBoundHandle)
        {
            Win32ThreadPoolNativeOverlapped.OverlappedData? data = overlapped->Data;
            Debug.Assert(data != null);

            if (data._boundHandle == null)
                throw new ArgumentException(SR.Argument_NativeOverlappedAlreadyFree, nameof(overlapped));

            if (expectedBoundHandle != null && data._boundHandle != expectedBoundHandle)
                throw new ArgumentException(SR.Argument_NativeOverlappedWrongBoundHandle, nameof(overlapped));

            return data;
        }

        [UnmanagedCallersOnly]
        private static unsafe void OnNativeIOCompleted(IntPtr instance, IntPtr context, IntPtr overlappedPtr, uint ioResult, nuint numberOfBytesTransferred, IntPtr ioPtr)
        {
            var wrapper = ThreadPoolCallbackWrapper.Enter();
            Win32ThreadPoolNativeOverlapped* overlapped = (Win32ThreadPoolNativeOverlapped*)overlappedPtr;

            Debug.Assert(overlapped != null);
            var data = overlapped->Data;
            Debug.Assert(data != null);
            ThreadPoolBoundHandle? boundHandle = data._boundHandle;
            if (boundHandle == null)
                throw new InvalidOperationException(SR.Argument_NativeOverlappedAlreadyFree);

            boundHandle.Release();

            Win32ThreadPoolNativeOverlapped.CompleteWithCallback(ioResult, (uint)numberOfBytesTransferred, overlapped);
            ThreadPool.IncrementCompletedWorkItemCount();
            wrapper.Exit();
        }

        private bool AddRef()
        {
            return _lifetime.AddRef();
        }

        private void Release()
        {
            _lifetime.Release(this);
        }

        public void Dispose()
        {
            _isDisposed = true;
            _lifetime.Dispose(this);
            GC.SuppressFinalize(this);
        }

        ~ThreadPoolBoundHandle()
        {
            //
            // During shutdown, don't automatically clean up, because this instance may still be
            // reachable/usable by other code.
            //
            if (!Environment.HasShutdownStarted)
                Dispose();
        }

        void IDeferredDisposable.OnFinalRelease(bool disposed)
        {
            // if (disposed)
            //     _threadPoolHandle.Dispose();
        }
    }
}
