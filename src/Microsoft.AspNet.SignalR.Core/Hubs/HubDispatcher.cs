﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR.Infrastructure;

namespace Microsoft.AspNet.SignalR.Hubs
{
    /// <summary>
    /// Handles all communication over the hubs persistent connection.
    /// </summary>
    public class HubDispatcher : PersistentConnection
    {
        private readonly List<HubDescriptor> _hubs = new List<HubDescriptor>();
        private readonly string _url;

        private IJavaScriptProxyGenerator _proxyGenerator;
        private IHubManager _manager;
        private IHubRequestParser _requestParser;
        private IParameterResolver _binder;
        private IHubPipelineInvoker _pipelineInvoker;
        private IPerformanceCounterManager _counters;
        private bool _isDebuggingEnabled;

        private static readonly MethodInfo _continueWithMethod = typeof(HubDispatcher).GetMethod("ContinueWith", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>
        /// Initializes an instance of the <see cref="HubDispatcher"/> class.
        /// </summary>
        /// <param name="url">The base url of the connection url.</param>
        public HubDispatcher(string url)
        {
            _url = url;
        }

        protected override TraceSource Trace
        {
            get
            {
                return TraceManager["SignalR.HubDispatcher"];
            }
        }

        public override void Initialize(IDependencyResolver resolver, HostContext context)
        {
            if (resolver == null)
            {
                throw new ArgumentNullException("resolver");
            }

            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            _proxyGenerator = resolver.Resolve<IJavaScriptProxyGenerator>();
            _manager = resolver.Resolve<IHubManager>();
            _binder = resolver.Resolve<IParameterResolver>();
            _requestParser = resolver.Resolve<IHubRequestParser>();
            _pipelineInvoker = resolver.Resolve<IHubPipelineInvoker>();

            _counters = resolver.Resolve<IPerformanceCounterManager>();

            // Call base initializer before populating _hubs so the _jsonSerializer is initialized
            base.Initialize(resolver, context);

            // Populate _hubs
            string data = context.Request.QueryStringOrForm("connectionData");

            if (!String.IsNullOrEmpty(data))
            {
                var clientHubInfo = JsonSerializer.Parse<IEnumerable<ClientHubInfo>>(data);
                if (clientHubInfo != null)
                {
                    foreach (var hubInfo in clientHubInfo)
                    {
                        // Try to find the associated hub type
                        HubDescriptor hubDescriptor = _manager.EnsureHub(hubInfo.Name,
                            _counters.ErrorsHubResolutionTotal,
                            _counters.ErrorsHubResolutionPerSec,
                            _counters.ErrorsAllTotal,
                            _counters.ErrorsAllPerSec);

                        if (_pipelineInvoker.AuthorizeConnect(hubDescriptor, context.Request))
                        {
                            // Add this to the list of hub descriptors this connection is interested in
                            _hubs.Add(hubDescriptor);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Processes the hub's incoming method calls.
        /// </summary>
        protected override Task OnReceivedAsync(IRequest request, string connectionId, string data)
        {
            HubRequest hubRequest = _requestParser.Parse(data);

            // Create the hub
            HubDescriptor descriptor = _manager.EnsureHub(hubRequest.Hub,
                _counters.ErrorsHubInvocationTotal,
                _counters.ErrorsHubInvocationPerSec,
                _counters.ErrorsAllTotal,
                _counters.ErrorsAllPerSec);

            IJsonValue[] parameterValues = hubRequest.ParameterValues;

            // Resolve the method
            MethodDescriptor methodDescriptor = _manager.GetHubMethod(descriptor.Name, hubRequest.Method, parameterValues);

            if (methodDescriptor == null)
            {
                _counters.ErrorsHubInvocationTotal.Increment();
                _counters.ErrorsHubInvocationPerSec.Increment();

                // Empty (noop) method descriptor
                // Use: Forces the hub pipeline module to throw an error.  This error is encapsulated in the HubDispatcher.
                //      Encapsulating it in the HubDispatcher prevents the error from bubbling up to the transport level.
                //      Specifically this allows us to return a faulted task (call .fail on client) and to not cause the
                //      transport to unintentionally fail.
                methodDescriptor = new MethodDescriptor
                {
                    Invoker = (emptyHub, emptyParameters) =>
                    {
                        throw new InvalidOperationException(String.Format(CultureInfo.CurrentCulture, Resources.Error_MethodCouldNotBeResolved, hubRequest.Method));
                    },
                    Attributes = new List<Attribute>(),
                    Parameters = new List<ParameterDescriptor>()
                };
            }

            // Resolving the actual state object
            var tracker = new StateChangeTracker(hubRequest.State);
            var hub = CreateHub(request, descriptor, connectionId, tracker, throwIfFailedToCreate: true);

            return InvokeHubPipeline(hub, parameterValues, methodDescriptor, hubRequest, tracker)
                .ContinueWith(task => hub.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
        }

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Exceptions are flown to the caller.")]
        private Task InvokeHubPipeline(IHub hub,
                                       IJsonValue[] parameterValues,
                                       MethodDescriptor methodDescriptor,
                                       HubRequest hubRequest,
                                       StateChangeTracker tracker)
        {
            Task<object> piplineInvocation;

            try
            {
                var args = _binder.ResolveMethodParameters(methodDescriptor, parameterValues);
                var context = new HubInvokerContext(hub, tracker, methodDescriptor, args);

                // Invoke the pipeline and save the task
                piplineInvocation = _pipelineInvoker.Invoke(context);
            }
            catch (Exception ex)
            {
                piplineInvocation = TaskAsyncHelper.FromError<object>(ex);
            }

            // Determine if we have a faulted task or not and handle it appropriately.
            return piplineInvocation.ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    return ProcessResponse(tracker, result: null, request: hubRequest, error: task.Exception);
                }
                else if (task.IsCanceled)
                {
                    return ProcessResponse(tracker, result: null, request: hubRequest, error: new OperationCanceledException());
                }
                else
                {
                    return ProcessResponse(tracker, task.Result, hubRequest, error: null);
                }
            })
            .FastUnwrap();
        }

        public override Task ProcessRequestAsync(HostContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            // Trim any trailing slashes
            string normalized = context.Request.Url.LocalPath.TrimEnd('/');

            if (normalized.EndsWith("/hubs", StringComparison.OrdinalIgnoreCase))
            {
                // Generate the proxy
                context.Response.ContentType = "application/x-javascript";
                return context.Response.EndAsync(_proxyGenerator.GenerateProxy(_url, includeDocComments: true));
            }

            _isDebuggingEnabled = context.IsDebuggingEnabled();

            return base.ProcessRequestAsync(context);
        }

        internal static Task Connect(IHub hub)
        {
            return hub.OnConnected();
        }

        internal static Task Reconnect(IHub hub)
        {
            return hub.OnReconnected();
        }

        internal static Task Disconnect(IHub hub)
        {
            return hub.OnDisconnected();
        }

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "A faulted task is returned.")]
        internal static Task<object> Incoming(IHubIncomingInvokerContext context)
        {
            var tcs = new TaskCompletionSource<object>();

            try
            {
                var result = context.MethodDescriptor.Invoker.Invoke(context.Hub, context.Args);
                Type returnType = context.MethodDescriptor.ReturnType;

                if (typeof(Task).IsAssignableFrom(returnType))
                {
                    var task = (Task)result;
                    if (!returnType.IsGenericType)
                    {
                        task.ContinueWith(tcs);
                    }
                    else
                    {
                        // Get the <T> in Task<T>
                        Type resultType = returnType.GetGenericArguments().Single();

                        Type genericTaskType = typeof(Task<>).MakeGenericType(resultType);

                        // Get the correct ContinueWith overload
                        var parameter = Expression.Parameter(typeof(object));

                        // TODO: Cache this whole thing
                        // Action<object> callback = result => ContinueWith((Task<T>)result, tcs);
                        MethodInfo continueWithMethod = _continueWithMethod.MakeGenericMethod(resultType);

                        Expression body = Expression.Call(continueWithMethod,
                                                          Expression.Convert(parameter, genericTaskType),
                                                          Expression.Constant(tcs));

                        var continueWithInvoker = Expression.Lambda<Action<object>>(body, parameter).Compile();
                        continueWithInvoker.Invoke(result);
                    }
                }
                else
                {
                    tcs.TrySetResult(result);
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }

        internal static Task Outgoing(IHubOutgoingInvokerContext context)
        {
            var message = new ConnectionMessage(context.Signal, context.Invocation)
            {
                ExcludedSignals = context.ExcludedSignals
            };

            return context.Connection.Send(message);
        }

        protected override Task OnConnectedAsync(IRequest request, string connectionId)
        {
            return ExecuteHubEventAsync(request, connectionId, hub => _pipelineInvoker.Connect(hub));
        }

        protected override Task OnReconnectedAsync(IRequest request, string connectionId)
        {
            return ExecuteHubEventAsync(request, connectionId, hub => _pipelineInvoker.Reconnect(hub));
        }

        protected override IEnumerable<string> OnRejoiningGroups(IRequest request, IEnumerable<string> groups, string connectionId)
        {
            return _hubs.Select(hubDescriptor =>
            {
                string groupPrefix = hubDescriptor.Name + ".";
                IEnumerable<string> groupsToRejoin = _pipelineInvoker.RejoiningGroups(hubDescriptor,
                                                                                      request,
                                                                                      groups.Where(g => g.StartsWith(groupPrefix, StringComparison.OrdinalIgnoreCase))
                                                                                            .Select(g => g.Substring(groupPrefix.Length)))
                                                                     .Select(g => groupPrefix + g);
                return groupsToRejoin;
            }).SelectMany(groupsToRejoin => groupsToRejoin);
        }

        protected override Task OnDisconnectAsync(IRequest request, string connectionId)
        {
            return ExecuteHubEventAsync(request, connectionId, hub => _pipelineInvoker.Disconnect(hub));
        }

        protected override IEnumerable<string> GetSignals(string connectionId)
        {
            return _hubs.SelectMany(info => new[] { info.Name, info.CreateQualifiedName(connectionId) })
                        .Concat(new[] { connectionId, "ACK_" + connectionId });
        }

        private Task ExecuteHubEventAsync(IRequest request, string connectionId, Func<IHub, Task> action)
        {
            var hubs = GetHubs(request, connectionId).ToList();
            var operations = hubs.Select(instance => action(instance).Catch().OrEmpty()).ToArray();

            if (operations.Length == 0)
            {
                DisposeHubs(hubs);
                return TaskAsyncHelper.Empty;
            }

            var tcs = new TaskCompletionSource<object>();
            Task.Factory.ContinueWhenAll(operations, tasks =>
            {
                DisposeHubs(hubs);
                var faulted = tasks.FirstOrDefault(t => t.IsFaulted);
                if (faulted != null)
                {
                    tcs.SetException(faulted.Exception);
                }
                else if (tasks.Any(t => t.IsCanceled))
                {
                    tcs.SetCanceled();
                }
                else
                {
                    tcs.SetResult(null);
                }
            });

            return tcs.Task;
        }

        private IHub CreateHub(IRequest request, HubDescriptor descriptor, string connectionId, StateChangeTracker tracker = null, bool throwIfFailedToCreate = false)
        {
            try
            {
                var hub = _manager.ResolveHub(descriptor.Name);

                if (hub != null)
                {
                    tracker = tracker ?? new StateChangeTracker();

                    hub.Context = new HubCallerContext(request, connectionId);
                    hub.Clients = new HubConnectionContext(_pipelineInvoker, Connection, descriptor.Name, connectionId, tracker);
                    hub.Groups = new GroupManager(Connection, descriptor.Name);
                }

                return hub;
            }
            catch (Exception ex)
            {
                Trace.TraceInformation(String.Format(CultureInfo.CurrentCulture, Resources.Error_ErrorCreatingHub + ex.Message, descriptor.Name));

                if (throwIfFailedToCreate)
                {
                    throw;
                }

                return null;
            }
        }

        private IEnumerable<IHub> GetHubs(IRequest request, string connectionId)
        {
            return from descriptor in _hubs
                   select CreateHub(request, descriptor, connectionId) into hub
                   where hub != null
                   select hub;
        }

        private static void DisposeHubs(IEnumerable<IHub> hubs)
        {
            foreach (var hub in hubs)
            {
                hub.Dispose();
            }
        }

        private Task ProcessResponse(StateChangeTracker tracker, object result, HubRequest request, Exception error)
        {
            var exception = error.Unwrap();
            string stackTrace = (exception != null && _isDebuggingEnabled) ? exception.StackTrace : null;
            string errorMessage = exception != null ? exception.Message : null;

            if (exception != null)
            {
                _counters.ErrorsHubInvocationTotal.Increment();
                _counters.ErrorsHubInvocationPerSec.Increment();
                _counters.ErrorsAllTotal.Increment();
                _counters.ErrorsAllPerSec.Increment();
            }

            var hubResult = new HubResponse
            {
                State = tracker.GetChanges(),
                Result = result,
                Id = request.Id,
                Error = errorMessage,
                StackTrace = stackTrace
            };

            return Transport.Send(hubResult);
        }

        private static void ContinueWith<T>(Task<T> task, TaskCompletionSource<object> tcs)
        {
            if (task.IsCompleted)
            {
                // Fast path for tasks that completed synchronously
                ContinueSync<T>(task, tcs);
            }
            else
            {
                ContinueAsync<T>(task, tcs);
            }
        }

        private static void ContinueSync<T>(Task<T> task, TaskCompletionSource<object> tcs)
        {
            if (task.IsFaulted)
            {
                tcs.TrySetException(task.Exception);
            }
            else if (task.IsCanceled)
            {
                tcs.TrySetCanceled();
            }
            else
            {
                tcs.TrySetResult(task.Result);
            }
        }

        private static void ContinueAsync<T>(Task<T> task, TaskCompletionSource<object> tcs)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    tcs.TrySetException(t.Exception);
                }
                else if (t.IsCanceled)
                {
                    tcs.TrySetCanceled();
                }
                else
                {
                    tcs.TrySetResult(t.Result);
                }
            });
        }

        [SuppressMessage("Microsoft.Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "It is instantiated through JSON deserialization.")]
        private class ClientHubInfo
        {
            public string Name { get; set; }
        }
    }
}
