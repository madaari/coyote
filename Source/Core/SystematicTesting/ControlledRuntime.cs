﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.Coyote.Actors;
using Microsoft.Coyote.Actors.Mocks;
using Microsoft.Coyote.Actors.Timers;
using Microsoft.Coyote.Actors.Timers.Mocks;
using Microsoft.Coyote.Coverage;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.SystematicTesting.Strategies;
using CoyoteTasks = Microsoft.Coyote.Tasks;
using EventInfo = Microsoft.Coyote.Actors.EventInfo;
using Monitor = Microsoft.Coyote.Specifications.Monitor;

namespace Microsoft.Coyote.SystematicTesting
{
    /// <summary>
    /// Runtime for controlling asynchronous operations.
    /// </summary>
    internal sealed class ControlledRuntime : ActorRuntime
    {
        /// <summary>
        /// The currently executing runtime.
        /// </summary>
        internal static new ControlledRuntime Current => CoyoteRuntime.Current as ControlledRuntime;

        /// <summary>
        /// The asynchronous operation scheduler.
        /// </summary>
        internal readonly OperationScheduler Scheduler;

        /// <summary>
        /// Responsible for controlling the execution of tasks.
        /// </summary>
        internal TaskController TaskController { get; private set; }

        /// <summary>
        /// Data structure containing information regarding testing coverage.
        /// </summary>
        internal CoverageInfo CoverageInfo;

        /// <summary>
        /// Map that stores all unique names and their corresponding actor ids.
        /// </summary>
        internal readonly ConcurrentDictionary<string, ActorId> NameValueToActorId;

        /// <summary>
        /// The root task id.
        /// </summary>
        internal readonly int? RootTaskId;

        /// <summary>
        /// Initializes a new instance of the <see cref="ControlledRuntime"/> class.
        /// </summary>
        internal ControlledRuntime(Configuration configuration, ISchedulingStrategy strategy,
            IRandomValueGenerator valueGenerator)
            : base(configuration, valueGenerator)
        {
            IncrementExecutionControlledUseCount();

            this.RootTaskId = Task.CurrentId;
            this.NameValueToActorId = new ConcurrentDictionary<string, ActorId>();

            this.CoverageInfo = new CoverageInfo();

            var scheduleTrace = new ScheduleTrace();
            if (configuration.IsLivenessCheckingEnabled)
            {
                strategy = new TemperatureCheckingStrategy(configuration, this.Monitors, strategy);
            }

            this.Scheduler = new OperationScheduler(this, strategy, scheduleTrace, this.Configuration);
            this.TaskController = new TaskController(this, this.Scheduler);
        }

        /// <inheritdoc/>
        public override ActorId CreateActorIdFromName(Type type, string name)
        {
            // It is important that all actor ids use the monotonically incrementing
            // value as the id during testing, and not the unique name.
            var id = new ActorId(type, name, this);
            return this.NameValueToActorId.GetOrAdd(name, id);
        }

        /// <inheritdoc/>
        public override ActorId CreateActor(Type type, Event initialEvent = null, EventGroup group = null) =>
            this.CreateActor(null, type, null, initialEvent, group);

        /// <inheritdoc/>
        public override ActorId CreateActor(Type type, string name, Event initialEvent = null, EventGroup group = null) =>
            this.CreateActor(null, type, name, initialEvent, group);

        /// <inheritdoc/>
        public override ActorId CreateActor(ActorId id, Type type, Event initialEvent = null, EventGroup group = null)
        {
            this.Assert(id != null, "Cannot create an actor using a null actor id.");
            return this.CreateActor(id, type, null, initialEvent, group);
        }

        /// <inheritdoc/>
        public override Task<ActorId> CreateActorAndExecuteAsync(Type type, Event e = null, EventGroup group = null) =>
            this.CreateActorAndExecuteAsync(null, type, null, e, group);

        /// <inheritdoc/>
        public override Task<ActorId> CreateActorAndExecuteAsync(Type type, string name, Event e = null, EventGroup group = null) =>
            this.CreateActorAndExecuteAsync(null, type, name, e, group);

        /// <inheritdoc/>
        public override Task<ActorId> CreateActorAndExecuteAsync(ActorId id, Type type, Event e = null, EventGroup group = null)
        {
            this.Assert(id != null, "Cannot create an actor using a null actor id.");
            return this.CreateActorAndExecuteAsync(id, type, null, e, group);
        }

        /// <inheritdoc/>
        public override void SendEvent(ActorId targetId, Event e, EventGroup group = null, SendOptions options = null)
        {
            var senderOp = this.Scheduler.GetExecutingOperation<ActorOperation>();
            this.SendEvent(targetId, e, senderOp?.Actor, group, options);
        }

        /// <inheritdoc/>
        public override Task<bool> SendEventAndExecuteAsync(ActorId targetId, Event e, EventGroup group = null,
            SendOptions options = null)
        {
            var senderOp = this.Scheduler.GetExecutingOperation<ActorOperation>();
            return this.SendEventAndExecuteAsync(targetId, e, senderOp?.Actor, group, options);
        }

        /// <inheritdoc/>
        public override EventGroup GetCurrentEventGroup(ActorId currentActorId)
        {
            var callerOp = this.Scheduler.GetExecutingOperation<ActorOperation>();
            this.Assert(callerOp != null && currentActorId == callerOp.Actor.Id,
                "Trying to access the event group id of {0}, which is not the currently executing actor.",
                currentActorId);
            return callerOp.Actor.CurrentEventGroup;
        }

        /// <summary>
        /// Runs the specified test method.
        /// </summary>
#if !DEBUG
        [DebuggerHidden]
#endif
        internal void RunTest(Delegate testMethod, string testName)
        {
            testName = string.IsNullOrEmpty(testName) ? string.Empty : $" '{testName}'";
            this.Logger.WriteLine($"<TestLog> Running test{testName}.");
            this.Assert(testMethod != null, "Unable to execute a null test method.");
            this.Assert(Task.CurrentId != null, "The test must execute inside a controlled task.");

            TaskOperation op = this.CreateTaskOperation();
            Task task = new Task(() =>
            {
                try
                {
                    // Update the current asynchronous control flow with the current runtime instance,
                    // allowing future retrieval in the same asynchronous call stack.
                    AssignAsyncControlFlowRuntime(this);

                    this.Scheduler.StartOperation(op);

                    Task testMethodTask = null;
                    if (testMethod is Action<IActorRuntime> actionWithRuntime)
                    {
                        actionWithRuntime(this);
                    }
                    else if (testMethod is Action action)
                    {
                        action();
                    }
                    else if (testMethod is Func<IActorRuntime, Task> functionWithRuntime)
                    {
                        testMethodTask = functionWithRuntime(this);
                    }
                    else if (testMethod is Func<Task> function)
                    {
                        testMethodTask = function();
                    }
                    else if (testMethod is Func<IActorRuntime, CoyoteTasks.Task> functionWithRuntime2)
                    {
                        testMethodTask = functionWithRuntime2(this).UncontrolledTask;
                    }
                    else if (testMethod is Func<CoyoteTasks.Task> function2)
                    {
                        testMethodTask = function2().UncontrolledTask;
                    }
                    else
                    {
                        throw new InvalidOperationException($"Unsupported test delegate of type '{testMethod.GetType()}'.");
                    }

                    if (testMethodTask != null)
                    {
                        // The test method is asynchronous, so wait on the task to complete.
                        op.OnWaitTask(testMethodTask);
                        if (testMethodTask.Exception != null)
                        {
                            // The test method failed with an unhandled exception.
                            ExceptionDispatchInfo.Capture(testMethodTask.Exception).Throw();
                        }
                        else if (testMethodTask.IsCanceled)
                        {
                            throw new TaskCanceledException(testMethodTask);
                        }
                    }

                    IO.Debug.WriteLine("<ScheduleDebug> Completed operation {0} on task '{1}'.", op.Name, Task.CurrentId);
                    op.OnCompleted();

                    // Task has completed, schedule the next enabled operation, which terminates exploration.
                    this.Scheduler.ScheduleNextOperation();
                }
                catch (Exception ex)
                {
                    this.ProcessUnhandledExceptionInOperation(op, ex);
                }
            });

            task.Start();
            this.Scheduler.WaitOperationStart(op);
        }

        /// <summary>
        /// Creates a new task operation.
        /// </summary>
        internal TaskOperation CreateTaskOperation()
        {
            ulong operationId = this.GetNextOperationId();
            var op = new TaskOperation(operationId, $"Task({operationId})", this.Scheduler);
            this.Scheduler.RegisterOperation(op);
            return op;
        }

        /// <summary>
        /// Creates a new actor of the specified <see cref="Type"/> and name, using the specified
        /// unbound actor id, and passes the specified optional <see cref="Event"/>. This event
        /// can only be used to access its payload, and cannot be handled.
        /// </summary>
        internal ActorId CreateActor(ActorId id, Type type, string name, Event initialEvent = null, EventGroup group = null)
        {
            var creatorOp = this.Scheduler.GetExecutingOperation<ActorOperation>();
            return this.CreateActor(id, type, name, initialEvent, creatorOp?.Actor, group);
        }

        /// <inheritdoc/>
        internal override ActorId CreateActor(ActorId id, Type type, string name, Event initialEvent, Actor creator, EventGroup group)
        {
            this.AssertExpectedCallerActor(creator, "CreateActor");

            Actor actor = this.CreateActor(id, type, name, creator, group);
            this.RunActorEventHandler(actor, initialEvent, true, null);
            return actor.Id;
        }

        /// <summary>
        /// Creates a new actor of the specified <see cref="Type"/> and name, using the specified
        /// unbound actor id, and passes the specified optional <see cref="Event"/>. This event
        /// can only be used to access its payload, and cannot be handled. The method returns only
        /// when the actor is initialized and the <see cref="Event"/> (if any) is handled.
        /// </summary>
        internal Task<ActorId> CreateActorAndExecuteAsync(ActorId id, Type type, string name, Event initialEvent = null,
            EventGroup group = null)
        {
            var creatorOp = this.Scheduler.GetExecutingOperation<ActorOperation>();
            return this.CreateActorAndExecuteAsync(id, type, name, initialEvent, creatorOp?.Actor, group);
        }

        /// <inheritdoc/>
        internal override async Task<ActorId> CreateActorAndExecuteAsync(ActorId id, Type type, string name,
            Event initialEvent, Actor creator, EventGroup group = null)
        {
            this.AssertExpectedCallerActor(creator, "CreateActorAndExecuteAsync");
            this.Assert(creator != null, "Only an actor can call 'CreateActorAndExecuteAsync': avoid calling " +
                "it directly from the test method; instead call it through a test driver actor.");

            Actor actor = this.CreateActor(id, type, name, creator, group);
            this.RunActorEventHandler(actor, initialEvent, true, creator);

            // Wait until the actor reaches quiescence.
            await creator.ReceiveEventAsync(typeof(QuiescentEvent), rev => (rev as QuiescentEvent).ActorId == actor.Id);
            return await Task.FromResult(actor.Id);
        }

        /// <summary>
        /// Creates a new actor of the specified <see cref="Type"/>.
        /// </summary>
        private Actor CreateActor(ActorId id, Type type, string name, Actor creator, EventGroup group)
        {
            this.Assert(type.IsSubclassOf(typeof(Actor)), "Type '{0}' is not an actor.", type.FullName);

            // Using ulong.MaxValue because a Create operation cannot specify
            // the id of its target, because the id does not exist yet.
            this.Scheduler.ScheduleNextOperation();
            ResetProgramCounter(creator);

            if (id is null)
            {
                id = new ActorId(type, name, this);
            }
            else
            {
                this.Assert(id.Runtime is null || id.Runtime == this, "Unbound actor id '{0}' was created by another runtime.", id.Value);
                this.Assert(id.Type == type.FullName, "Cannot bind actor id '{0}' of type '{1}' to an actor of type '{2}'.",
                    id.Value, id.Type, type.FullName);
                id.Bind(this);
            }

            // If a group was not provided, inherit the current event group from the creator (if any).
            if (group == null && creator != null)
            {
                group = creator.Manager.CurrentEventGroup;
            }

            Actor actor = ActorFactory.Create(type);
            IActorManager actorManager;
            if (actor is StateMachine stateMachine)
            {
                actorManager = new MockStateMachineManager(this, stateMachine, group);
            }
            else
            {
                actorManager = new MockActorManager(this, actor, group);
            }

            IEventQueue eventQueue = new MockEventQueue(actorManager, actor);
            actor.Configure(this, id, actorManager, eventQueue);
            actor.SetupEventHandlers();

            if (this.Configuration.ReportActivityCoverage)
            {
                this.ReportActivityCoverageOfActor(actor);
            }

            bool result = this.Scheduler.RegisterOperation(new ActorOperation(actor, this.Scheduler));
            this.Assert(result, "Actor id '{0}' is used by an existing or previously halted actor.", id.Value);
            if (actor is StateMachine)
            {
                this.LogWriter.LogCreateStateMachine(id, creator?.Id.Name, creator?.Id.Type);
            }
            else
            {
                this.LogWriter.LogCreateActor(id, creator?.Id.Name, creator?.Id.Type);
            }

            return actor;
        }

        /// <inheritdoc/>
        internal override void SendEvent(ActorId targetId, Event e, Actor sender, EventGroup group, SendOptions options)
        {
            if (e is null)
            {
                string message = sender != null ?
                    string.Format("{0} is sending a null event.", sender.Id.ToString()) :
                    "Cannot send a null event.";
                this.Assert(false, message);
            }

            if (sender != null)
            {
                this.Assert(targetId != null, "{0} is sending event {1} to a null actor.", sender.Id, e);
            }
            else
            {
                this.Assert(targetId != null, "Cannot send event {1} to a null actor.", e);
            }

            this.AssertExpectedCallerActor(sender, "SendEvent");

            EnqueueStatus enqueueStatus = this.EnqueueEvent(targetId, e, sender, group, options, out Actor target);
            if (enqueueStatus is EnqueueStatus.EventHandlerNotRunning)
            {
                this.RunActorEventHandler(target, null, false, null);
            }
        }

         /// <inheritdoc/>
        internal override async Task<bool> SendEventAndExecuteAsync(ActorId targetId, Event e, Actor sender,
            EventGroup group, SendOptions options)
        {
            this.Assert(sender is StateMachine, "Only an actor can call 'SendEventAndExecuteAsync': avoid " +
                "calling it directly from the test method; instead call it through a test driver actor.");
            this.Assert(e != null, "{0} is sending a null event.", sender.Id);
            this.Assert(targetId != null, "{0} is sending event {1} to a null actor.", sender.Id, e);
            this.AssertExpectedCallerActor(sender, "SendEventAndExecuteAsync");
            EnqueueStatus enqueueStatus = this.EnqueueEvent(targetId, e, sender, group, options, out Actor target);
            if (enqueueStatus is EnqueueStatus.EventHandlerNotRunning)
            {
                this.RunActorEventHandler(target, null, false, sender as StateMachine);
                // Wait until the actor reaches quiescence.
                await (sender as StateMachine).ReceiveEventAsync(typeof(QuiescentEvent), rev => (rev as QuiescentEvent).ActorId == targetId);
                return true;
            }

            // EnqueueStatus.EventHandlerNotRunning is not returned by EnqueueEvent
            // (even when the actor was previously inactive) when the event e requires
            // no action by the actor (i.e., it implicitly handles the event).
            return enqueueStatus is EnqueueStatus.Dropped || enqueueStatus is EnqueueStatus.NextEventUnavailable;
        }

        /// <summary>
        /// Enqueues an event to the actor with the specified id.
        /// </summary>
        private EnqueueStatus EnqueueEvent(ActorId targetId, Event e, Actor sender, EventGroup group,
            SendOptions options, out Actor target)
        {
            target = this.Scheduler.GetOperationWithId<ActorOperation>(targetId.Value)?.Actor;
            this.Assert(target != null,
                "Cannot send event '{0}' to actor id '{1}' that is not bound to an actor instance.",
                e.GetType().FullName, targetId.Value);

            this.Scheduler.ScheduleNextOperation();
            ResetProgramCounter(sender as StateMachine);

            // If no group is provided we default to passing along the group from the sender.
            if (group == null && sender != null)
            {
                group = sender.Manager.CurrentEventGroup;
            }

            if (target.IsHalted)
            {
                Guid groupId = group == null ? Guid.Empty : group.Id;
                this.LogWriter.LogSendEvent(targetId, sender?.Id.Name, sender?.Id.Type,
                    (sender as StateMachine)?.CurrentStateName ?? string.Empty, e, groupId, isTargetHalted: true);
                this.Assert(options is null || !options.MustHandle,
                    "A must-handle event '{0}' was sent to {1} which has halted.", e.GetType().FullName, targetId);
                this.TryHandleDroppedEvent(e, targetId);
                return EnqueueStatus.Dropped;
            }

            EnqueueStatus enqueueStatus = this.EnqueueEvent(target, e, sender, group, options);
            if (enqueueStatus == EnqueueStatus.Dropped)
            {
                this.TryHandleDroppedEvent(e, targetId);
            }

            return enqueueStatus;
        }

        /// <summary>
        /// Enqueues an event to the actor with the specified id.
        /// </summary>
        private EnqueueStatus EnqueueEvent(Actor actor, Event e, Actor sender, EventGroup group, SendOptions options)
        {
            EventOriginInfo originInfo;

            string stateName = null;
            if (sender is StateMachine senderStateMachine)
            {
                originInfo = new EventOriginInfo(sender.Id, senderStateMachine.GetType().FullName,
                    NameResolver.GetStateNameForLogging(senderStateMachine.CurrentState));
                stateName = senderStateMachine.CurrentStateName;
            }
            else if (sender is Actor senderActor)
            {
                originInfo = new EventOriginInfo(sender.Id, senderActor.GetType().FullName, string.Empty);
            }
            else
            {
                // Message comes from the environment.
                originInfo = new EventOriginInfo(null, "Env", "Env");
            }

            EventInfo eventInfo = new EventInfo(e, originInfo)
            {
                MustHandle = options?.MustHandle ?? false,
                Assert = options?.Assert ?? -1
            };

            Guid opId = group == null ? Guid.Empty : group.Id;
            this.LogWriter.LogSendEvent(actor.Id, sender?.Id.Name, sender?.Id.Type, stateName,
                e, opId, isTargetHalted: false);
            return actor.Enqueue(e, group, eventInfo);
        }

        /// <summary>
        /// Runs a new asynchronous event handler for the specified actor.
        /// This is a fire and forget invocation.
        /// </summary>
        /// <param name="actor">The actor that executes this event handler.</param>
        /// <param name="initialEvent">Optional event for initializing the actor.</param>
        /// <param name="isFresh">If true, then this is a new actor.</param>
        /// <param name="syncCaller">Caller actor that is blocked for quiscence.</param>
        private void RunActorEventHandler(Actor actor, Event initialEvent, bool isFresh, Actor syncCaller)
        {
            var op = this.Scheduler.GetOperationWithId<ActorOperation>(actor.Id.Value);
            Task task = new Task(async () =>
            {
                try
                {
                    // Update the current asynchronous control flow with this runtime instance,
                    // allowing future retrieval in the same asynchronous call stack.
                    AssignAsyncControlFlowRuntime(this);

                    this.Scheduler.StartOperation(op);

                    if (isFresh)
                    {
                        await actor.InitializeAsync(initialEvent);
                    }

                    await actor.RunEventHandlerAsync();
                    if (syncCaller != null)
                    {
                        this.EnqueueEvent(syncCaller, new QuiescentEvent(actor.Id), actor, actor.CurrentEventGroup, null);
                    }

                    if (!actor.IsHalted)
                    {
                        ResetProgramCounter(actor);
                    }

                    IO.Debug.WriteLine("<ScheduleDebug> Completed operation {0} on task '{1}'.", actor.Id, Task.CurrentId);
                    op.OnCompleted();

                    // The actor is inactive or halted, schedule the next enabled operation.
                    this.Scheduler.ScheduleNextOperation();
                }
                catch (Exception ex)
                {
                    this.ProcessUnhandledExceptionInOperation(op, ex);
                }
            });

            task.Start();
            this.Scheduler.WaitOperationStart(op);
        }

        /// <summary>
        /// Processes an unhandled exception in the specified asynchronous operation.
        /// </summary>
        private void ProcessUnhandledExceptionInOperation(AsyncOperation op, Exception ex)
        {
            string message = null;
            Exception exception = UnwrapException(ex);
            if (exception is ExecutionCanceledException || exception is TaskSchedulerException)
            {
                IO.Debug.WriteLine("<Exception> {0} was thrown from operation '{1}'.",
                    exception.GetType().Name, op.Name);
            }
            else if (exception is ObjectDisposedException)
            {
                IO.Debug.WriteLine("<Exception> {0} was thrown from operation '{1}' with reason '{2}'.",
                    exception.GetType().Name, op.Name, ex.Message);
            }
            else if (op is ActorOperation actorOp)
            {
                message = string.Format(CultureInfo.InvariantCulture,
                    $"Unhandled exception. {exception.GetType()} was thrown in actor {actorOp.Name}, " +
                    $"'{exception.Source}':\n" +
                    $"   {exception.Message}\n" +
                    $"The stack trace is:\n{exception.StackTrace}");
            }
            else
            {
                message = string.Format(CultureInfo.InvariantCulture, $"Unhandled exception. {exception}");
            }

            if (message != null)
            {
                // Report the unhandled exception.
                this.Scheduler.NotifyUnhandledException(exception, message);
            }
        }

        /// <summary>
        /// Unwraps the specified exception.
        /// </summary>
        internal static Exception UnwrapException(Exception ex)
        {
            Exception exception = ex;
            while (exception is TargetInvocationException)
            {
                exception = exception.InnerException;
            }

            if (exception is AggregateException)
            {
                exception = exception.InnerException;
            }

            return exception;
        }

        /// <inheritdoc/>
        internal override IActorTimer CreateActorTimer(TimerInfo info, Actor owner)
        {
            var id = this.CreateActorId(typeof(MockStateMachineTimer));
            this.CreateActor(id, typeof(MockStateMachineTimer), new TimerSetupEvent(info, owner, this.Configuration.TimeoutDelay));
            return this.Scheduler.GetOperationWithId<ActorOperation>(id.Value).Actor as MockStateMachineTimer;
        }

        /// <inheritdoc/>
        internal override void TryCreateMonitor(Type type)
        {
            if (this.Monitors.Any(m => m.GetType() == type))
            {
                // Idempotence: only one monitor per type can exist.
                return;
            }

            this.Assert(type.IsSubclassOf(typeof(Monitor)), "Type '{0}' is not a subclass of Monitor.", type.FullName);

            Monitor monitor = Activator.CreateInstance(type) as Monitor;
            monitor.Initialize(this);
            monitor.InitializeStateInformation();

            this.LogWriter.LogCreateMonitor(type.FullName);

            if (this.Configuration.ReportActivityCoverage)
            {
                this.ReportActivityCoverageOfMonitor(monitor);
            }

            this.Monitors.Add(monitor);

            monitor.GotoStartState();
        }

        /// <inheritdoc/>
        internal override void Monitor(Type type, Event e, string senderName, string senderType, string senderStateName)
        {
            foreach (var monitor in this.Monitors)
            {
                if (monitor.GetType() == type)
                {
                    monitor.MonitorEvent(e, senderName, senderType, senderStateName);
                    break;
                }
            }
        }

        /// <inheritdoc/>
#if !DEBUG
        [DebuggerHidden]
#endif
        public override void Assert(bool predicate)
        {
            if (!predicate)
            {
                this.Scheduler.NotifyAssertionFailure("Detected an assertion failure.");
            }
        }

        /// <inheritdoc/>
#if !DEBUG
        [DebuggerHidden]
#endif
        public override void Assert(bool predicate, string s, object arg0)
        {
            if (!predicate)
            {
                var msg = string.Format(CultureInfo.InvariantCulture, s, arg0?.ToString());
                this.Scheduler.NotifyAssertionFailure(msg);
            }
        }

        /// <inheritdoc/>
#if !DEBUG
        [DebuggerHidden]
#endif
        public override void Assert(bool predicate, string s, object arg0, object arg1)
        {
            if (!predicate)
            {
                var msg = string.Format(CultureInfo.InvariantCulture, s, arg0?.ToString(), arg1?.ToString());
                this.Scheduler.NotifyAssertionFailure(msg);
            }
        }

        /// <inheritdoc/>
#if !DEBUG
        [DebuggerHidden]
#endif
        public override void Assert(bool predicate, string s, object arg0, object arg1, object arg2)
        {
            if (!predicate)
            {
                var msg = string.Format(CultureInfo.InvariantCulture, s, arg0?.ToString(), arg1?.ToString(), arg2?.ToString());
                this.Scheduler.NotifyAssertionFailure(msg);
            }
        }

        /// <inheritdoc/>
#if !DEBUG
        [DebuggerHidden]
#endif
        public override void Assert(bool predicate, string s, params object[] args)
        {
            if (!predicate)
            {
                var msg = string.Format(CultureInfo.InvariantCulture, s, args);
                this.Scheduler.NotifyAssertionFailure(msg);
            }
        }

        /// <summary>
        /// Asserts that the actor calling an actor method is also
        /// the actor that is currently executing.
        /// </summary>
#if !DEBUG
        [DebuggerHidden]
#endif
        private void AssertExpectedCallerActor(Actor caller, string calledAPI)
        {
            if (caller is null)
            {
                return;
            }

            var op = this.Scheduler.GetExecutingOperation<ActorOperation>();
            if (op is null)
            {
                return;
            }

            this.Assert(op.Actor.Equals(caller), "{0} invoked {1} on behalf of {2}.",
                op.Actor.Id, calledAPI, caller.Id);
        }

        /// <summary>
        /// Checks that no monitor is in a hot state upon program termination.
        /// If the program is still running, then this method returns without
        /// performing a check.
        /// </summary>
#if !DEBUG
        [DebuggerHidden]
#endif
        internal void CheckNoMonitorInHotStateAtTermination()
        {
            if (!this.Scheduler.HasFullyExploredSchedule)
            {
                return;
            }

            foreach (var monitor in this.Monitors)
            {
                if (monitor.IsInHotState(out string stateName))
                {
                    string message = string.Format(CultureInfo.InvariantCulture,
                        "{0} detected liveness bug in hot state '{1}' at the end of program execution.",
                        monitor.GetType().FullName, stateName);
                    this.Scheduler.NotifyAssertionFailure(message, killTasks: false, cancelExecution: false);
                }
            }
        }

        /// <inheritdoc/>
        internal override bool GetNondeterministicBooleanChoice(int maxValue, string callerName, string callerType)
        {
            var caller = this.Scheduler.GetExecutingOperation<ActorOperation>()?.Actor;
            if (caller is StateMachine callerStateMachine)
            {
                (callerStateMachine.Manager as MockStateMachineManager).ProgramCounter++;
            }
            else if (caller is Actor callerActor)
            {
                (callerActor.Manager as MockActorManager).ProgramCounter++;
            }

            var choice = this.Scheduler.GetNextNondeterministicBooleanChoice(maxValue);
            this.LogWriter.LogRandom(choice, callerName ?? caller?.Id.Name, callerType ?? caller?.Id.Type);
            return choice;
        }

        /// <inheritdoc/>
        internal override int GetNondeterministicIntegerChoice(int maxValue, string callerName, string callerType)
        {
            var caller = this.Scheduler.GetExecutingOperation<ActorOperation>()?.Actor;
            if (caller is StateMachine callerStateMachine)
            {
                (callerStateMachine.Manager as MockStateMachineManager).ProgramCounter++;
            }
            else if (caller is Actor callerActor)
            {
                (callerActor.Manager as MockActorManager).ProgramCounter++;
            }

            var choice = this.Scheduler.GetNextNondeterministicIntegerChoice(maxValue);
            this.LogWriter.LogRandom(choice, callerName ?? caller?.Id.Name, callerType ?? caller?.Id.Type);
            return choice;
        }

        /// <summary>
        /// Gets the <see cref="AsyncOperation"/> that is executing on the current
        /// synchronization context, or null if no such operation is executing.
        /// </summary>
        internal TAsyncOperation GetExecutingOperation<TAsyncOperation>()
            where TAsyncOperation : AsyncOperation =>
            this.Scheduler.GetExecutingOperation<TAsyncOperation>();

        /// <summary>
        /// Schedules the next controlled asynchronous operation. This method
        /// is only used during testing.
        /// </summary>
        internal void ScheduleNextOperation()
        {
            var callerOp = this.Scheduler.GetExecutingOperation<AsyncOperation>();
            if (callerOp != null)
            {
                this.Scheduler.ScheduleNextOperation();
            }
        }

        /// <inheritdoc/>
        internal override void NotifyInvokedAction(Actor actor, MethodInfo action, string handlingStateName,
            string currentStateName, Event receivedEvent)
        {
            this.LogWriter.LogExecuteAction(actor.Id, handlingStateName, currentStateName, action.Name);
        }

        /// <inheritdoc/>
        internal override void NotifyDequeuedEvent(Actor actor, Event e, EventInfo eventInfo)
        {
            var op = this.Scheduler.GetOperationWithId<ActorOperation>(actor.Id.Value);

            // Skip `ReceiveEventAsync` if the last operation exited the previous event handler,
            // to avoid scheduling duplicate `ReceiveEventAsync` operations.
            if (op.SkipNextReceiveSchedulingPoint)
            {
                op.SkipNextReceiveSchedulingPoint = false;
            }
            else
            {
                this.Scheduler.ScheduleNextOperation();
                ResetProgramCounter(actor);
            }

            string stateName = actor is StateMachine stateMachine ? stateMachine.CurrentStateName : null;
            this.LogWriter.LogDequeueEvent(actor.Id, stateName, e);
        }

        /// <inheritdoc/>
        internal override void NotifyDefaultEventDequeued(Actor actor)
        {
            this.Scheduler.ScheduleNextOperation();
            ResetProgramCounter(actor);
        }

        /// <inheritdoc/>
        internal override void NotifyDefaultEventHandlerCheck(Actor actor)
        {
            this.Scheduler.ScheduleNextOperation();
        }

        /// <inheritdoc/>
        internal override void NotifyRaisedEvent(Actor actor, Event e, EventInfo eventInfo)
        {
            string stateName = actor is StateMachine stateMachine ? stateMachine.CurrentStateName : null;
            this.LogWriter.LogRaiseEvent(actor.Id, stateName, e);
        }

        /// <inheritdoc/>
        internal override void NotifyHandleRaisedEvent(Actor actor, Event e)
        {
            string stateName = actor is StateMachine stateMachine ? stateMachine.CurrentStateName : null;
            this.LogWriter.LogHandleRaisedEvent(actor.Id, stateName, e);
        }

        /// <inheritdoc/>
        internal override void NotifyReceiveCalled(Actor actor)
        {
            this.AssertExpectedCallerActor(actor, "ReceiveEventAsync");
        }

        /// <inheritdoc/>
        internal override void NotifyReceivedEvent(Actor actor, Event e, EventInfo eventInfo)
        {
            string stateName = actor is StateMachine stateMachine ? stateMachine.CurrentStateName : null;
            this.LogWriter.LogReceiveEvent(actor.Id, stateName, e, wasBlocked: true);
            var op = this.Scheduler.GetOperationWithId<ActorOperation>(actor.Id.Value);
            op.OnReceivedEvent();
        }

        /// <inheritdoc/>
        internal override void NotifyReceivedEventWithoutWaiting(Actor actor, Event e, EventInfo eventInfo)
        {
            string stateName = actor is StateMachine stateMachine ? stateMachine.CurrentStateName : null;
            this.LogWriter.LogReceiveEvent(actor.Id, stateName, e, wasBlocked: false);
            this.Scheduler.ScheduleNextOperation();
            ResetProgramCounter(actor);
        }

        /// <inheritdoc/>
        internal override void NotifyWaitTask(Actor actor, Task task)
        {
            this.Assert(task != null, "{0} is waiting for a null task to complete.", actor.Id);

            bool finished = task.IsCompleted || task.IsCanceled || task.IsFaulted;
            if (!finished)
            {
                this.Assert(finished,
                    "Controlled task '{0}' is trying to wait for an uncontrolled task or awaiter to complete. Please " +
                    "make sure to avoid using concurrency APIs (e.g. 'Task.Run', 'Task.Delay' or 'Task.Yield' from " +
                    "the 'System.Threading.Tasks' namespace) inside actor handlers. If you are using external libraries " +
                    "that are executing concurrently, you will need to mock them during testing.",
                    Task.CurrentId);
            }
        }

        /// <inheritdoc/>
        internal override void NotifyWaitEvent(Actor actor, IEnumerable<Type> eventTypes)
        {
            string stateName = actor is StateMachine stateMachine ? stateMachine.CurrentStateName : null;
            var op = this.Scheduler.GetOperationWithId<ActorOperation>(actor.Id.Value);
            op.OnWaitEvent(eventTypes);

            var eventWaitTypesArray = eventTypes.ToArray();
            if (eventWaitTypesArray.Length == 1)
            {
                this.LogWriter.LogWaitEvent(actor.Id, stateName, eventWaitTypesArray[0]);
            }
            else
            {
                this.LogWriter.LogWaitEvent(actor.Id, stateName, eventWaitTypesArray);
            }

            this.Scheduler.ScheduleNextOperation();
            ResetProgramCounter(actor);
        }

        /// <inheritdoc/>
        internal override void NotifyEnteredState(StateMachine stateMachine)
        {
            string stateName = stateMachine.CurrentStateName;
            this.LogWriter.LogStateTransition(stateMachine.Id, stateName, isEntry: true);
        }

        /// <inheritdoc/>
        internal override void NotifyExitedState(StateMachine stateMachine)
        {
            this.LogWriter.LogStateTransition(stateMachine.Id, stateMachine.CurrentStateName, isEntry: false);
        }

        /// <inheritdoc/>
        internal override void NotifyPopState(StateMachine stateMachine)
        {
            this.AssertExpectedCallerActor(stateMachine, "Pop");
            this.LogWriter.LogPopState(stateMachine.Id, string.Empty, stateMachine.CurrentStateName);
        }

        /// <inheritdoc/>
        internal override void NotifyInvokedOnEntryAction(StateMachine stateMachine, MethodInfo action, Event receivedEvent)
        {
            string stateName = stateMachine.CurrentStateName;
            this.LogWriter.LogExecuteAction(stateMachine.Id, stateName, stateName, action.Name);
        }

        /// <inheritdoc/>
        internal override void NotifyInvokedOnExitAction(StateMachine stateMachine, MethodInfo action, Event receivedEvent)
        {
            string stateName = stateMachine.CurrentStateName;
            this.LogWriter.LogExecuteAction(stateMachine.Id, stateName, stateName, action.Name);
        }

        /// <inheritdoc/>
        internal override void NotifyEnteredState(Monitor monitor)
        {
            string monitorState = monitor.CurrentStateName;
            this.LogWriter.LogMonitorStateTransition(monitor.GetType().FullName, monitorState, true, monitor.GetHotState());
        }

        /// <inheritdoc/>
        internal override void NotifyExitedState(Monitor monitor)
        {
            this.LogWriter.LogMonitorStateTransition(monitor.GetType().FullName,
                monitor.CurrentStateName, false, monitor.GetHotState());
        }

        /// <inheritdoc/>
        internal override void NotifyInvokedAction(Monitor monitor, MethodInfo action, string stateName, Event receivedEvent)
        {
            this.LogWriter.LogMonitorExecuteAction(monitor.GetType().FullName, stateName, action.Name);
        }

        /// <inheritdoc/>
        internal override void NotifyRaisedEvent(Monitor monitor, Event e)
        {
            string monitorState = monitor.CurrentStateName;
            this.LogWriter.LogMonitorRaiseEvent(monitor.GetType().FullName, monitorState, e);
        }

        /// <summary>
        /// Get the coverage graph information (if any). This information is only available
        /// when <see cref="Configuration.ReportActivityCoverage"/> is enabled.
        /// </summary>
        /// <returns>A new CoverageInfo object.</returns>
        public CoverageInfo GetCoverageInfo()
        {
            var result = this.CoverageInfo;
            if (result != null)
            {
                var builder = this.LogWriter.GetLogsOfType<ActorRuntimeLogGraphBuilder>().FirstOrDefault();
                if (builder != null)
                {
                    result.CoverageGraph = builder.SnapshotGraph(this.Configuration.IsDgmlBugGraph);
                }

                var eventCoverage = this.LogWriter.GetLogsOfType<ActorRuntimeLogEventCoverage>().FirstOrDefault();
                if (eventCoverage != null)
                {
                    result.EventInfo = eventCoverage.EventCoverage;
                }
            }

            return result;
        }

        /// <summary>
        /// Reports actors that are to be covered in coverage report.
        /// </summary>
        private void ReportActivityCoverageOfActor(Actor actor)
        {
            var name = actor.GetType().FullName;
            if (this.CoverageInfo.IsMachineDeclared(name))
            {
                return;
            }

            if (actor is StateMachine stateMachine)
            {
                // Fetch states.
                var states = stateMachine.GetAllStates();
                foreach (var state in states)
                {
                    this.CoverageInfo.DeclareMachineState(name, state);
                }

                // Fetch registered events.
                var pairs = stateMachine.GetAllStateEventPairs();
                foreach (var tup in pairs)
                {
                    this.CoverageInfo.DeclareStateEvent(name, tup.Item1, tup.Item2);
                }
            }
            else
            {
                var fakeStateName = actor.GetType().Name;
                this.CoverageInfo.DeclareMachineState(name, fakeStateName);

                foreach (var eventId in actor.GetAllRegisteredEvents())
                {
                    this.CoverageInfo.DeclareStateEvent(name, fakeStateName, eventId);
                }
            }
        }

        /// <summary>
        /// Reports coverage for the specified monitor.
        /// </summary>
        private void ReportActivityCoverageOfMonitor(Monitor monitor)
        {
            var monitorName = monitor.GetType().FullName;
            if (this.CoverageInfo.IsMachineDeclared(monitorName))
            {
                return;
            }

            // Fetch states.
            var states = monitor.GetAllStates();

            foreach (var state in states)
            {
                this.CoverageInfo.DeclareMachineState(monitorName, state);
            }

            // Fetch registered events.
            var pairs = monitor.GetAllStateEventPairs();

            foreach (var tup in pairs)
            {
                this.CoverageInfo.DeclareStateEvent(monitorName, tup.Item1, tup.Item2);
            }
        }

        /// <summary>
        /// Resets the program counter of the specified actor.
        /// </summary>
        private static void ResetProgramCounter(Actor actor)
        {
            if (actor is StateMachine stateMachine)
            {
                (stateMachine.Manager as MockStateMachineManager).ProgramCounter = 0;
            }
            else if (actor != null)
            {
                (actor.Manager as MockActorManager).ProgramCounter = 0;
            }
        }

        /// <summary>
        /// Returns the current hashed state of the execution using the specified
        /// level of abstraction. The hash is updated in each execution step.
        /// </summary>
        [DebuggerStepThrough]
        internal int GetProgramState()
        {
            unchecked
            {
                int hash = 19;

                foreach (var operation in this.Scheduler.GetRegisteredOperations().OrderBy(op => op.Id))
                {
                    if (operation is ActorOperation actorOperation)
                    {
                        hash *= 31 + actorOperation.Actor.GetHashedState();
                    }
                }

                foreach (var monitor in this.Monitors)
                {
                    hash = (hash * 397) + monitor.GetHashedState();
                }

                return hash;
            }
        }

        /// <inheritdoc/>
        [DebuggerStepThrough]
        internal override void WrapAndThrowException(Exception exception, string s, params object[] args)
        {
            string msg = string.Format(CultureInfo.InvariantCulture, s, args);
            string message = string.Format(CultureInfo.InvariantCulture,
                "Exception '{0}' was thrown in {1}: {2}\n" +
                "from location '{3}':\n" +
                "The stack trace is:\n{4}",
                exception.GetType(), msg, exception.Message, exception.Source, exception.StackTrace);

            this.Scheduler.NotifyAssertionFailure(message);
        }

        /// <summary>
        /// Waits until all actors have finished execution.
        /// </summary>
        [DebuggerStepThrough]
        internal async Task WaitAsync()
        {
            await this.Scheduler.WaitAsync();
            this.IsRunning = false;
        }

        /// <inheritdoc/>
        protected internal override void RaiseOnFailureEvent(Exception exception)
        {
            if (exception is ExecutionCanceledException ||
                (exception is ActionExceptionFilterException ae && ae.InnerException is ExecutionCanceledException))
            {
                // Internal exception used during testing.
                return;
            }

            base.RaiseOnFailureEvent(exception);
        }

        /// <inheritdoc/>
        [DebuggerStepThrough]
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.Monitors.Clear();

                // Note: this makes it possible to run a Controlled unit test followed by a production
                // unit test, whereas before that would throw "Uncontrolled Task" exceptions.
                // This does not solve mixing unit test type in parallel.
                DecrementExecutionControlledUseCount();
            }

            base.Dispose(disposing);
        }
    }
}
