// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Internal.TypeSystem;

namespace Internal.IL.Stubs
{
    public static class AsyncThunkILEmitter
    {
        public static MethodIL EmitTaskReturningThunk(MethodDesc taskReturningMethod, MethodDesc asyncMethod)
        {
            TypeSystemContext context = taskReturningMethod.Context;

            var emitter = new ILEmitter();
            var codestream = emitter.NewCodeStream();

            // Matches EmitTaskReturningThunk in CoreCLR VM (src\coreclr\vm\asyncthunks.cpp)
            MethodSignature sig = asyncMethod.Signature;

            TypeDesc returnType = sig.ReturnType;
            bool isValueTask = returnType.IsValueType;

            TypeDesc logicalReturnType;
            ILLocalVariable logicalResultLocal;
            if (returnType.HasInstantiation)
            {
                // The return type is either Task<T> or ValueTask<T>, exactly one generic argument
                logicalReturnType = returnType.Instantiation[0];
                logicalResultLocal = emitter.NewLocal(logicalReturnType);
            }

            ILLocalVariable returnTaskLocal = emitter.NewLocal(returnType);
            TypeDesc executionAndSyncBlockStoreType = context.SystemModule.GetKnownType("System.Runtime.CompilerServices"u8, "ExecutionAndSyncBlockStore"u8);
            ILLocalVariable executionAndSyncBlockStoreLocal = emitter.NewLocal(executionAndSyncBlockStoreType);

            ILCodeLabel returnTaskLabel = emitter.NewCodeLabel();
            ILCodeLabel suspendedLabel = emitter.NewCodeLabel();
            ILCodeLabel finishedLabel = emitter.NewCodeLabel();

            codestream.EmitLdLoca(executionAndSyncBlockStoreLocal);
            codestream.Emit(ILOpcode.call, emitter.NewToken(executionAndSyncBlockStoreType.GetKnownMethod("Push"u8, null)));

            int numParams = (sig.IsStatic || sig.IsExplicitThis) ? sig.Length : sig.Length + 1;

            for (int i = 0; i < numParams; i++)
                codestream.EmitLdArg(i);

            codestream.Emit(ILOpcode.call, emitter.NewToken(asyncMethod));

            if (sig.ReturnType.IsVoid)
            {
                codestream.Emit(ILOpcode.call, emitter.NewToken(context.SystemModule.GetKnownType("System.Threading.Tasks"u8, "Task"u8).GetKnownMethod("get_CompletedTask"u8, null)));
            }
            else
            {
                codestream.Emit(ILOpcode.call, emitter.NewToken(context.SystemModule.GetKnownType("System.Threading.Tasks"u8, "Task"u8).GetKnownMethod("FromResult"u8, null).MakeInstantiatedMethod(sig.ReturnType)));
            }

            // pCode->EmitRET();
            codestream.Emit(ILOpcode.ret);

            return emitter.Link(taskReturningMethod);
        }
    }
}
