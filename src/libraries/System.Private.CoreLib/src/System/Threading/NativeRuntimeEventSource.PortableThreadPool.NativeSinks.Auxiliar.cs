// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;

namespace System.Diagnostics.Tracing
{
    // This is part of the NativeRuntimeEventsource, which is the managed version of the Microsoft-Windows-DotNETRuntime provider.
    // Contains the implementation of ThreadPool events. This implementation is used by runtime not supporting NativeRuntimeEventSource.PortableThreadPool.NativeSinks.cs.
    internal sealed partial class NativeRuntimeEventSource : EventSource
    {
        // We don't have these keywords defined from the genRuntimeEventSources.py, so we need to manually define them here.
        public static class Keywords
        {
            public const EventKeywords ThreadingKeyword = (EventKeywords)0x10000;
            public const EventKeywords ThreadTransferKeyword = (EventKeywords)0x80000000;
        }

    }
}
