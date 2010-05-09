﻿// 
// Copyright (c) 2004-2010 Jaroslaw Kowalski <jaak@jkowalski.net>
// 
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without 
// modification, are permitted provided that the following conditions 
// are met:
// 
// * Redistributions of source code must retain the above copyright notice, 
//   this list of conditions and the following disclaimer. 
// 
// * Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution. 
// 
// * Neither the name of Jaroslaw Kowalski nor the names of its 
//   contributors may be used to endorse or promote products derived from this
//   software without specific prior written permission. 
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE 
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE 
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE 
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR 
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN 
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) 
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF 
// THE POSSIBILITY OF SUCH DAMAGE.
// 

namespace NLog.Internal
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using NLog.Common;

    /// <summary>
    /// Helpers for asynchronous operations.
    /// </summary>
    public static class AsyncHelpers
    {
        public static void LogException(Exception ex)
        {
            InternalLogger.Error("EXCEPTION: {0}", ex);
        }

        public static void ForEachItemSequentially<T>(IEnumerable<T> items, AsyncContinuation asyncContinuation, AsynchronousAction<T> action)
        {
            action = ExceptionGuard(action);
            AsyncContinuation invokeNext = null;
            IEnumerator<T> enumerator = items.GetEnumerator();

            invokeNext = ex =>
            {
                if (ex != null)
                {
                    asyncContinuation(ex);
                    return;
                }

                if (!enumerator.MoveNext())
                {
                    asyncContinuation(null);
                    return;
                }

                action(invokeNext.OneTimeOnly(), enumerator.Current);
            };

            invokeNext(null);
        }

        public static void Repeat(int repeatCount, AsyncContinuation asyncContinuation, AsynchronousAction action)
        {
            action = ExceptionGuard(action);
            AsyncContinuation invokeNext = null;
            int remaining = repeatCount;

            invokeNext = ex =>
                {
                    if (ex != null)
                    {
                        asyncContinuation(ex);
                        return;
                    }

                    if (remaining-- <= 0)
                    {
                        asyncContinuation(null);
                        return;
                    }

                    action(invokeNext.OneTimeOnly());
                };

            invokeNext(null);
        }

        private static AsynchronousAction ExceptionGuard(AsynchronousAction action)
        {
            return cont =>
                {
                    try
                    {
                        action(cont);
                    }
                    catch (Exception ex)
                    {
                        cont(ex);
                    }
                };
        }

        private static AsynchronousAction<T> ExceptionGuard<T>(AsynchronousAction<T> action)
        {
            return (AsyncContinuation cont, T argument) =>
            {
                try
                {
                    action(cont, argument);
                }
                catch (Exception ex)
                {
                    cont(ex);
                }
            };
        }

        /// <summary>
        /// Modifies the continuation by pre-pending given action to execute just before it.
        /// </summary>
        /// <param name="asyncContinuation">The async continuation.</param>
        /// <param name="action">The action to pre-pend.</param>
        /// <returns>Continuation which will execute the given action before forwarding to the actual continuation.</returns>
        public static AsyncContinuation PrecededBy(this AsyncContinuation asyncContinuation, AsynchronousAction action)
        {
            action = ExceptionGuard(action);

            AsyncContinuation continuation =
                ex =>
                {
                    if (ex != null)
                    {
                        // if got exception from from original invocation, don't execute action
                        asyncContinuation(ex);
                        return;
                    }

                    // call the action and continue
                    action(asyncContinuation.OneTimeOnly());
                };

            return continuation;
        }

        public static AsyncContinuation PrecededByRegardlessOfResult(this AsyncContinuation asyncContinuation, SynchronousAction action)
        {
            throw new NotImplementedException();
        }

        public static AsyncContinuation WithTimeout(this AsyncContinuation asyncContinuation, TimeSpan timeout)
        {
            return new TimeoutContinuation(asyncContinuation, timeout).Function;
        }

        public static void RunInParallel<T>(IEnumerable<T> values, AsyncContinuation asyncContinuation, AsynchronousAction<T> action)
        {
            action = ExceptionGuard(action);

            var items = new List<T>(values);
            int remaining = items.Count;
            var exceptions = new List<Exception>();

            InternalLogger.Trace("RunInParallel() {0} items", items.Count);

            if (remaining == 0)
            {
                asyncContinuation(null);
                return;
            }

            AsyncContinuation continuation =
                ex =>
                    {
                        int r;

                        if (ex == null)
                        {
                            r = Interlocked.Decrement(ref remaining);
                            InternalLogger.Trace("Parallel task completed. {0} items remaining", r);
                            if (r == 0)
                            {
                                if (exceptions.Count == 0)
                                {
                                    asyncContinuation(null);
                                }
                                else
                                {
                                    asyncContinuation(new NLogRuntimeException("TODO - combine all exceptions into one."));
                                }
                            }

                            return;
                        }

                        lock (exceptions)
                        {
                            exceptions.Add(ex);
                        }

                        r = Interlocked.Decrement(ref remaining);
                        InternalLogger.Trace("Parallel task failed {0}. {1} items remaining", ex, r);
                        if (r == 0)
                        {
                            asyncContinuation(new NLogRuntimeException("TODO - combine all exceptions into one."));
                        }
                    };

            foreach (var v in items)
            {
                action(OneTimeOnly(continuation), v);
            }
        }

        public static void RunSynchronously(AsynchronousAction action)
        {
            var ev = new ManualResetEvent(false);
            Exception lastException = null;

            action(OneTimeOnly(ex => { lastException = ex; ev.Set(); }));
            ev.WaitOne();
            if (lastException != null)
            {
                throw new NLogRuntimeException("Asynchronous exception has occured.");
            }
        }

        public static AsyncContinuation OneTimeOnly(this AsyncContinuation asyncContinuation)
        {
#if !NETCF2_0
            // target is not available on .NET CF 2.0
            if (asyncContinuation.Target is SingleCallContinuation)
            {
                return asyncContinuation;
            }
#endif

            return new SingleCallContinuation(asyncContinuation).Function;
        }
    }
}