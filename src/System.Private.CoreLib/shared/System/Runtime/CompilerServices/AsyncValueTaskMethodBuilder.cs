// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;
using System.Security;
using System.Threading.Tasks;

namespace System.Runtime.CompilerServices
{
    /// <summary>Represents a builder for asynchronous methods that return a <see cref="ValueTask"/>.</summary>
    [StructLayout(LayoutKind.Auto)]
    public struct AsyncValueTaskMethodBuilder
#if PROJECTN
        : IMethodBuilder<VoidTaskResult>
#endif
    {
        /// <summary>The lazily-initialized built task.</summary>
        private Task<VoidTaskResult> m_task; // Debugger depends on the exact name of this field.

        /// <summary>Creates an instance of the <see cref="AsyncValueTaskMethodBuilder"/> struct.</summary>
        /// <returns>The initialized instance.</returns>
        public static AsyncValueTaskMethodBuilder Create()
#if PROJECTN
            => AsyncMethodBuilderCore.Create<AsyncValueTaskMethodBuilder, VoidTaskResult>();
#else
            => default;
#endif

#if PROJECTN
        Task<VoidTaskResult> IMethodBuilder<VoidTaskResult>.Task { set => m_task = value; }
#endif

        /// <summary>Begins running the builder with the associated state machine.</summary>
        /// <typeparam name="TStateMachine">The type of the state machine.</typeparam>
        /// <param name="stateMachine">The state machine instance, passed by reference.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine =>
            // will provide the right ExecutionContext semantics
            AsyncMethodBuilderCore.Start(ref stateMachine);

        /// <summary>Associates the builder with the specified state machine.</summary>
        /// <param name="stateMachine">The state machine instance to associate with the builder.</param>
        public void SetStateMachine(IAsyncStateMachine stateMachine)
            => AsyncMethodBuilderCore.SetStateMachine(stateMachine, m_task);

        /// <summary>Marks the task as successfully completed.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetResult()
        {
            if (m_task is null)
            {
                m_task = System.Threading.Tasks.Task.s_cachedCompleted;
            }
            else
            {
                AsyncMethodBuilderCore.SetExistingTaskResult(m_task, default);
            }
        }

        /// <summary>Marks the task as failed and binds the specified exception to the task.</summary>
        /// <param name="exception">The exception to bind to the task.</param>
        public void SetException(Exception exception)
            => AsyncMethodBuilderCore.SetException(ref m_task, exception);

        /// <summary>Gets the task for this builder.</summary>
        public ValueTask Task
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (ReferenceEquals(m_task, System.Threading.Tasks.Task.s_cachedCompleted))
                {
                    return default;
                }
                else
                {
                    return new ValueTask(m_task ?? AsyncMethodBuilderCore.InitializeTaskAsPromise(ref m_task!)); // TODO-NULLABLE: Remove ! when nullable attributes are respected
                }
            }
        }

        /// <summary>Schedules the state machine to proceed to the next action when the specified awaiter completes.</summary>
        /// <typeparam name="TAwaiter">The type of the awaiter.</typeparam>
        /// <typeparam name="TStateMachine">The type of the state machine.</typeparam>
        /// <param name="awaiter">The awaiter.</param>
        /// <param name="stateMachine">The state machine.</param>
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => AsyncMethodBuilderCore.AwaitOnCompleted(ref awaiter, ref stateMachine, ref m_task);

        /// <summary>Schedules the state machine to proceed to the next action when the specified awaiter completes.</summary>
        /// <typeparam name="TAwaiter">The type of the awaiter.</typeparam>
        /// <typeparam name="TStateMachine">The type of the state machine.</typeparam>
        /// <param name="awaiter">The awaiter.</param>
        /// <param name="stateMachine">The state machine.</param>
        [SecuritySafeCritical]
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => AsyncMethodBuilderCore.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine, ref m_task);
    }

    /// <summary>Represents a builder for asynchronous methods that returns a <see cref="ValueTask{TResult}"/>.</summary>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    [StructLayout(LayoutKind.Auto)]
    public struct AsyncValueTaskMethodBuilder<TResult>
#if PROJECTN
        : IMethodBuilder<TResult>
#endif
    {
        /// <summary>used if <see cref="_result"/> contains the synchronous result for the async method.</summary>
        private static readonly Task<TResult> s_haveResultSentinel = new Task<TResult>();

        private Task<TResult> m_task; // Debugger depends on the exact name of this field.
        /// <summary>The result for this builder, if it's completed before any awaits occur.</summary>
        private TResult _result;

        /// <summary>Creates an instance of the <see cref="AsyncValueTaskMethodBuilder{TResult}"/> struct.</summary>
        /// <returns>The initialized instance.</returns>
        public static AsyncValueTaskMethodBuilder<TResult> Create()
#if PROJECTN
            => AsyncMethodBuilderCore.Create<AsyncValueTaskMethodBuilder<TResult>, TResult>();
#else
            => default;
#endif

#if PROJECTN
        Task<TResult> IMethodBuilder<TResult>.Task { set => m_task = value; }
#endif
        /// <summary>Begins running the builder with the associated state machine.</summary>
        /// <typeparam name="TStateMachine">The type of the state machine.</typeparam>
        /// <param name="stateMachine">The state machine instance, passed by reference.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine =>
            // will provide the right ExecutionContext semantics
            AsyncMethodBuilderCore.Start(ref stateMachine);

        /// <summary>Associates the builder with the specified state machine.</summary>
        /// <param name="stateMachine">The state machine instance to associate with the builder.</param>
        public void SetStateMachine(IAsyncStateMachine stateMachine)
            => AsyncMethodBuilderCore.SetStateMachine(stateMachine, m_task);

        /// <summary>Marks the task as successfully completed.</summary>
        /// <param name="result">The result to use to complete the task.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetResult(TResult result)
        {
            if (m_task is null)
            {
                _result = result;
                m_task = s_haveResultSentinel;
            }
            else
            {
                AsyncMethodBuilderCore.SetExistingTaskResult(m_task, result);
            }
        }

        /// <summary>Marks the task as failed and binds the specified exception to the task.</summary>
        /// <param name="exception">The exception to bind to the task.</param>
        public void SetException(Exception exception)
            => AsyncMethodBuilderCore.SetException(ref m_task, exception);

        /// <summary>Gets the task for this builder.</summary>
        public ValueTask<TResult> Task
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (ReferenceEquals(s_haveResultSentinel, m_task))
                {
                    return new ValueTask<TResult>(_result);
                }
                else
                {
                    return new ValueTask<TResult>(m_task ?? AsyncMethodBuilderCore.InitializeTaskAsPromise(ref m_task!)); // TODO-NULLABLE: Remove ! when nullable attributes are respected
                }
            }
        }

        /// <summary>Schedules the state machine to proceed to the next action when the specified awaiter completes.</summary>
        /// <typeparam name="TAwaiter">The type of the awaiter.</typeparam>
        /// <typeparam name="TStateMachine">The type of the state machine.</typeparam>
        /// <param name="awaiter">the awaiter</param>
        /// <param name="stateMachine">The state machine.</param>
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => AsyncMethodBuilderCore.AwaitOnCompleted(ref awaiter, ref stateMachine, ref m_task);

        /// <summary>Schedules the state machine to proceed to the next action when the specified awaiter completes.</summary>
        /// <typeparam name="TAwaiter">The type of the awaiter.</typeparam>
        /// <typeparam name="TStateMachine">The type of the state machine.</typeparam>
        /// <param name="awaiter">the awaiter</param>
        /// <param name="stateMachine">The state machine.</param>
        [SecuritySafeCritical]
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion 
            where TStateMachine : IAsyncStateMachine
            => AsyncMethodBuilderCore.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine, ref m_task);
    }
}
