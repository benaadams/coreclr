// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

/*============================================================
**
**
**
** Purpose: Capture execution  context for a thread
**
**
===========================================================*/

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;

using Thread = Internal.Runtime.Augments.RuntimeThread;

namespace System.Threading
{
    public delegate void ContextCallback(Object state);

    public sealed class ExecutionContext : IDisposable, ISerializable
    {
        internal static readonly ExecutionContext Default = new ExecutionContext();

        private readonly IAsyncLocalValueMap m_localValues;
        private readonly IAsyncLocal[] m_localChangeNotifications;
        private readonly bool m_isFlowSuppressed;

        private ExecutionContext()
        {
            m_localValues = AsyncLocalValueMap.Empty;
            m_localChangeNotifications = Array.Empty<IAsyncLocal>();
        }

        private ExecutionContext(
            IAsyncLocalValueMap localValues,
            IAsyncLocal[] localChangeNotifications,
            bool isFlowSuppressed)
        {
            m_localValues = localValues;
            m_localChangeNotifications = localChangeNotifications;
            m_isFlowSuppressed = isFlowSuppressed;
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            throw new PlatformNotSupportedException();
        }

        public static ExecutionContext Capture()
        {
            ExecutionContext executionContext = Thread.CurrentThread.ExecutionContext;
            return
                executionContext == null ? Default :
                executionContext.m_isFlowSuppressed ? null :
                executionContext;
        }

        private ExecutionContext ShallowClone(bool isFlowSuppressed)
        {
            Debug.Assert(isFlowSuppressed != m_isFlowSuppressed);

            if (!isFlowSuppressed &&
                m_localValues == Default.m_localValues &&
                m_localChangeNotifications == Default.m_localChangeNotifications)
            {
                return null; // implies the default context
            }
            return new ExecutionContext(m_localValues, m_localChangeNotifications, isFlowSuppressed);
        }

        public static AsyncFlowControl SuppressFlow()
        {
            Thread currentThread = Thread.CurrentThread;
            ExecutionContext executionContext = currentThread.ExecutionContext ?? Default;
            if (executionContext.m_isFlowSuppressed)
            {
                throw new InvalidOperationException(SR.InvalidOperation_CannotSupressFlowMultipleTimes);
            }

            executionContext = executionContext.ShallowClone(isFlowSuppressed: true);
            var asyncFlowControl = new AsyncFlowControl();
            currentThread.ExecutionContext = executionContext;
            asyncFlowControl.Initialize(currentThread);
            return asyncFlowControl;
        }

        public static void RestoreFlow()
        {
            Thread currentThread = Thread.CurrentThread;
            ExecutionContext executionContext = currentThread.ExecutionContext;
            if (executionContext == null || !executionContext.m_isFlowSuppressed)
            {
                throw new InvalidOperationException(SR.InvalidOperation_CannotRestoreUnsupressedFlow);
            }

            currentThread.ExecutionContext = executionContext.ShallowClone(isFlowSuppressed: false);
        }

        public static bool IsFlowSuppressed()
        {
            ExecutionContext executionContext = Thread.CurrentThread.ExecutionContext;
            return executionContext != null && executionContext.m_isFlowSuppressed;
        }

        public static void Run(ExecutionContext executionContext, ContextCallback callback, Object state)
        {
            // Note: ExecutionContext.Run is an extremly hot function and used by every await, threadpool execution etc
            if (executionContext == null)
            {
                throw new InvalidOperationException(SR.InvalidOperation_NullContext);
            }

            Thread currentThread = Thread.CurrentThread;
            // Capture references to Thread Contexts
            ref ExecutionContext current = ref currentThread.ExecutionContext;
            ref SynchronizationContext currentSyncCtx = ref currentThread.SynchronizationContext;

            // Store current ExecutionContext and SynchronizationContext as "previous"
            // This allows us to restore them and undo any Context changes made in callback.Invoke
            // so that they won't "leak" back into caller.
            ExecutionContext previous = current;
            SynchronizationContext previousSyncCtx = currentSyncCtx;

            if (executionContext == Default)
            {
                // Default is a null ExecutionContext internally
                executionContext = null;
            }

            if (previous != executionContext)
            {
                // Restore changed ExecutionContext
                Restore(ref current, executionContext);
            }

            ExceptionDispatchInfo edi = null;
            try
            {
                callback.Invoke(state);
            }
            catch (Exception ex)
            {
                // Note: we have a "catch" rather than a "finally" because we want
                // to stop the first pass of EH here.  That way we can restore the previous
                // context before any of our callers' EH filters run.
                edi = ExceptionDispatchInfo.Capture(ex);
            }

            // The common case is that these have not changed, so avoid the cost of a write barrier if not needed.
            if (currentSyncCtx != previousSyncCtx)
            {
                // Restore changed SynchronizationContext back to previous
                currentSyncCtx = previousSyncCtx;
            }

            if (current != previous)
            {
                // Restore changed ExecutionContext back to previous
                Restore(ref current, previous);
            }

            // If exception was thrown by callback, rethrow it now original contexts are restored
            edi?.Throw();
        }

        internal static void Restore(ref ExecutionContext current, ExecutionContext next)
        {
            Debug.Assert(current != next);
            // Capture current for change notification comparisions
            ExecutionContext previous = current;
            // Set current to next
            current = next;

            // Fire Change Notifications if any
            try
            {
                if (previous != null)
                {
                    foreach (IAsyncLocal local in previous.m_localChangeNotifications)
                    {
                        previous.m_localValues.TryGetValue(local, out object previousValue);
                        object currentValue = null;
                        next?.m_localValues.TryGetValue(local, out currentValue);

                        if (previousValue != currentValue)
                        {
                            local.OnValueChanged(previousValue, currentValue, true);
                        }
                    }
                }

                if (next != null && next.m_localChangeNotifications != previous?.m_localChangeNotifications)
                {
                    foreach (IAsyncLocal local in next.m_localChangeNotifications)
                    {
                        // If the local has a value in the previous context, we already fired the event for that local
                        // in the code above.
                        object previousValue = null;
                        if (previous == null || !previous.m_localValues.TryGetValue(local, out previousValue))
                        {
                            next.m_localValues.TryGetValue(local, out object currentValue);

                            if (previousValue != currentValue)
                            {
                                local.OnValueChanged(previousValue, currentValue, true);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Environment.FailFast(
                    SR.ExecutionContext_ExceptionInAsyncLocalNotification,
                    ex);
            }
        }

        internal static object GetLocalValue(IAsyncLocal local)
        {
            ExecutionContext current = Thread.CurrentThread.ExecutionContext;
            if (current == null)
            {
                return null;
            }

            current.m_localValues.TryGetValue(local, out object value);
            return value;
        }

        internal static void SetLocalValue(IAsyncLocal local, object newValue, bool needChangeNotifications)
        {
            ExecutionContext current = Thread.CurrentThread.ExecutionContext;

            object previousValue = null;
            bool hadPreviousValue = false;
            bool isFlowSuppressed = false;
            IAsyncLocal[] newChangeNotifications = null;
            IAsyncLocalValueMap newValues;
            if (current != null)
            {
                isFlowSuppressed = current.m_isFlowSuppressed;
                hadPreviousValue = current.m_localValues.TryGetValue(local, out previousValue);
                newValues = current.m_localValues;
                newChangeNotifications = current.m_localChangeNotifications;
            }
            else
            {
                newValues = AsyncLocalValueMap.Empty;
            }

            if (previousValue == newValue)
            {
                return;
            }

            newValues = newValues.Set(local, newValue);

            //
            // Either copy the change notification array, or create a new one, depending on whether we need to add a new item.
            //
            if (needChangeNotifications)
            {
                if (hadPreviousValue)
                {
                    Debug.Assert(Array.IndexOf(newChangeNotifications, local) >= 0);
                }
                else if (newChangeNotifications == null)
                {
                    newChangeNotifications = new IAsyncLocal[1];
                    newChangeNotifications[0] = local;
                }
                else
                {
                    int newNotificationIndex = newChangeNotifications.Length;
                    Array.Resize(ref newChangeNotifications, newNotificationIndex + 1);
                    newChangeNotifications[newNotificationIndex] = local;
                }
            }

            Thread.CurrentThread.ExecutionContext =
                new ExecutionContext(newValues, newChangeNotifications, isFlowSuppressed);

            if (needChangeNotifications)
            {
                local.OnValueChanged(previousValue, newValue, false);
            }
        }

        public ExecutionContext CreateCopy()
        {
            return this; // since CoreCLR's ExecutionContext is immutable, we don't need to create copies.
        }

        public void Dispose()
        {
            // For CLR compat only
        }
    }

    public struct AsyncFlowControl : IDisposable
    {
        private Thread _thread;

        internal void Initialize(Thread currentThread)
        {
            Debug.Assert(currentThread == Thread.CurrentThread);
            _thread = currentThread;
        }

        public void Undo()
        {
            if (_thread == null)
            {
                throw new InvalidOperationException(SR.InvalidOperation_CannotUseAFCMultiple);
            }
            if (Thread.CurrentThread != _thread)
            {
                throw new InvalidOperationException(SR.InvalidOperation_CannotUseAFCOtherThread);
            }

            // An async flow control cannot be undone when a different execution context is applied. The desktop framework
            // mutates the execution context when its state changes, and only changes the instance when an execution context
            // is applied (for instance, through ExecutionContext.Run). The framework prevents a suppressed-flow execution
            // context from being applied by returning null from ExecutionContext.Capture, so the only type of execution
            // context that can be applied is one whose flow is not suppressed. After suppressing flow and changing an async
            // local's value, the desktop framework verifies that a different execution context has not been applied by
            // checking the execution context instance against the one saved from when flow was suppressed. In .NET Core,
            // since the execution context instance will change after changing the async local's value, it verifies that a
            // different execution context has not been applied, by instead ensuring that the current execution context's
            // flow is suppressed.
            if (!ExecutionContext.IsFlowSuppressed())
            {
                throw new InvalidOperationException(SR.InvalidOperation_AsyncFlowCtrlCtxMismatch);
            }

            _thread = null;
            ExecutionContext.RestoreFlow();
        }

        public void Dispose()
        {
            Undo();
        }

        public override bool Equals(object obj)
        {
            return obj is AsyncFlowControl && Equals((AsyncFlowControl)obj);
        }

        public bool Equals(AsyncFlowControl obj)
        {
            return _thread == obj._thread;
        }

        public override int GetHashCode()
        {
            return _thread?.GetHashCode() ?? 0;
        }

        public static bool operator ==(AsyncFlowControl a, AsyncFlowControl b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(AsyncFlowControl a, AsyncFlowControl b)
        {
            return !(a == b);
        }
    }
}
