using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GMO.OpenTelemetry.Tests
{
    public class ActivityExecutorTests : IDisposable
    {
        private readonly ActivitySource _source;
        private readonly ActivityListener _listener;

        // New instance per use: ActivityOptions is mutable.
        private static ActivityOptions Detached => new ActivityOptions { ForceNewDetachedRoot = true };

        public ActivityExecutorTests()
        {
            _source = new ActivitySource("Test.ActivityExecutor");
            _listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose()
        {
            _listener.Dispose();
            _source.Dispose();
            GC.SuppressFinalize(this);
        }

        [Fact]
        public async Task ExecuteAsync_ReturnsActionResult()
        {
            var result = await ActivityExecutor.ExecuteAsync("op", _source, _ => Task.FromResult(42));
            Assert.Equal(42, result);
        }

        [Fact]
        public async Task ExecuteAsync_AppliesOperationNameAndAdditionalData()
        {
            Activity? captured = null;
            await ActivityExecutor.ExecuteAsync("op", _source, wrapper =>
            {
                captured = wrapper.Activity;
                return Task.FromResult(0);
            }, options: new ActivityOptions { AdditionalData = new Dictionary<string, object> { ["index"] = "candidate" } });

            Assert.NotNull(captured);
            Assert.Equal("op", captured!.OperationName);
            Assert.Equal("candidate", captured.GetTagItem("index"));
        }

        // Dispose stamps an Unset span as Ok, so an unrecorded exception would look like success.
        [Fact]
        public async Task ExecuteAsync_WhenActionThrows_RecordsExceptionAndRethrows()
        {
            Activity? captured = null;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ActivityExecutor.ExecuteAsync<int>("op", _source, wrapper =>
                {
                    captured = wrapper.Activity;
                    throw new InvalidOperationException("boom");
                }));

            Assert.NotNull(captured);
            Assert.Equal(ActivityStatusCode.Error, captured!.Status);
            Assert.Equal("boom", captured.GetTagItem("exception.message"));
            Assert.Equal(typeof(InvalidOperationException).FullName, captured.GetTagItem("exception.type"));
        }

        [Fact]
        public async Task ExecuteAsync_Void_WhenActionThrows_RecordsExceptionAndRethrows()
        {
            Activity? captured = null;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ActivityExecutor.ExecuteAsync("op", _source, wrapper =>
                {
                    captured = wrapper.Activity;
                    throw new InvalidOperationException("boom");
                }));

            Assert.Equal(ActivityStatusCode.Error, captured!.Status);
        }

        [Fact]
        public async Task ExecuteAsync_WhenActionSucceeds_StatusIsOk()
        {
            Activity? captured = null;
            await ActivityExecutor.ExecuteAsync("op", _source, wrapper =>
            {
                captured = wrapper.Activity;
                return Task.CompletedTask;
            });

            Assert.Equal(ActivityStatusCode.Ok, captured!.Status);
        }

        [Fact]
        public async Task ExecuteRootAsync_ReturnsActionResult()
        {
            var result = await ActivityExecutor.ExecuteRootAsync("root", _source, _ => Task.FromResult("done"));
            Assert.Equal("done", result);
        }

        // Roots are what tail-based sampling keys a whole trace on, so their status has to come from the
        // executor observing success - not from Dispose defaulting to it.
        [Fact]
        public async Task ExecuteRootAsync_WhenActionSucceeds_StatusIsOk()
        {
            Activity? captured = null;

            await ActivityExecutor.ExecuteRootAsync("root", _source, wrapper =>
            {
                captured = wrapper.Activity;
                return Task.CompletedTask;
            }, options: Detached);

            Assert.Equal(ActivityStatusCode.Ok, captured!.Status);
        }

        [Fact]
        public void ExecuteRoot_WhenActionSucceeds_StatusIsOk()
        {
            Activity? captured = null;

            ActivityExecutor.ExecuteRoot("root", _source, wrapper => captured = wrapper.Activity,
                options: Detached);

            Assert.Equal(ActivityStatusCode.Ok, captured!.Status);
        }

        [Fact]
        public async Task ExecuteRootAsync_WhenActionThrows_RecordsExceptionAndRethrows()
        {
            Activity? captured = null;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ActivityExecutor.ExecuteRootAsync<int>("root", _source, wrapper =>
                {
                    captured = wrapper.Activity;
                    throw new InvalidOperationException("boom");
                }));

            Assert.NotNull(captured);
            Assert.Equal(ActivityStatusCode.Error, captured!.Status);
        }

        [Fact]
        public async Task ExecuteRootAsync_Void_WhenActionThrows_RecordsExceptionAndRethrows()
        {
            Activity? captured = null;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ActivityExecutor.ExecuteRootAsync("root", _source, wrapper =>
                {
                    captured = wrapper.Activity;
                    throw new InvalidOperationException("boom");
                }));

            Assert.Equal(ActivityStatusCode.Error, captured!.Status);
        }

        [Fact]
        public async Task ExecuteRootAsync_ForceNewDetachedRoot_DetachesFromAmbientParent()
        {
            using var outer = _source.StartActivity("outer");
            Assert.NotNull(Activity.Current);

            Activity? inner = null;
            await ActivityExecutor.ExecuteRootAsync("root", _source, wrapper =>
            {
                inner = wrapper.Activity;
                return Task.CompletedTask;
            }, options: Detached);

            Assert.NotNull(inner);
            Assert.Null(inner!.Parent);
            // ParentId is the assertion with teeth: Ids are unique per Activity so comparing them can
            // never fail, and TraceId reports zeros under net48's hierarchical format.
            Assert.Null(inner.ParentId);
            Assert.NotEqual(outer!.Id, inner.Id);
        }

        // Pins the surprising default: forceNewDetachedRoot: false renames the caller's ambient root
        // instead of creating a span.
        [Fact]
        public async Task ExecuteRootAsync_WithAmbientRoot_RenamesExistingRootInsteadOfCreatingSpan()
        {
            using var outer = _source.StartActivity("outer");
            Assert.NotNull(outer);

            Activity? inner = null;
            await ActivityExecutor.ExecuteRootAsync("root", _source, wrapper =>
            {
                inner = wrapper.Activity;
                return Task.CompletedTask;
            });

            Assert.Same(outer, inner);
            Assert.Equal("root", outer!.DisplayName);
        }

        // Holds because the executor starts the activity inside its async core, whose copy-on-write
        // ExecutionContext scope keeps the Activity.Current write from escaping. Building the wrapper
        // in the public method instead leaks "root" to the caller.
        [Fact]
        public async Task ExecuteRootAsync_DoesNotLeakAmbientActivityToCaller()
        {
            using var outer = _source.StartActivity("outer");

            await ActivityExecutor.ExecuteRootAsync("root", _source, _ => Task.CompletedTask, options: Detached);

            Assert.Same(outer, Activity.Current);
        }

        [Fact]
        public async Task ExecuteAsync_WhenCancelled_DoesNotRecordErrorAndAddsCancelledEvent()
        {
            Activity? captured = null;

            await Assert.ThrowsAsync<TaskCanceledException>(() =>
                ActivityExecutor.ExecuteAsync<int>("op", _source, wrapper =>
                {
                    captured = wrapper.Activity;
                    throw new TaskCanceledException();
                }));

            Assert.NotNull(captured);
            // Unset, not merely "not Error": Ok would also satisfy NotEqual, and Ok is precisely the
            // wrong answer for a cancelled operation - it asserts a success nobody observed.
            Assert.Equal(ActivityStatusCode.Unset, captured!.Status);
            Assert.Null(captured.GetTagItem("exception.type"));
            Assert.Contains(captured.Events, e => e.Name == "cancelled");
        }

        [Fact]
        public async Task ExecuteRootAsync_WhenCancelled_DoesNotRecordError()
        {
            Activity? captured = null;

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                ActivityExecutor.ExecuteRootAsync("root", _source, wrapper =>
                {
                    captured = wrapper.Activity;
                    throw new OperationCanceledException();
                }, options: Detached));

            Assert.NotNull(captured);
            // Unset, not merely "not Error": Ok would also satisfy NotEqual, and Ok is precisely the
            // wrong answer for a cancelled operation - it asserts a success nobody observed.
            Assert.Equal(ActivityStatusCode.Unset, captured!.Status);
            Assert.Contains(captured.Events, e => e.Name == "cancelled");
        }

        [Fact]
        public void ExecuteAsync_NullAction_ThrowsEagerly()
        {
            // Void-bodied lambdas, so a faulted Task would fail these rather than satisfy them.
            Assert.Throws<ArgumentNullException>(() =>
            {
                _ = ActivityExecutor.ExecuteAsync<int>("op", _source, null!);
            });
            Assert.Throws<ArgumentNullException>(() =>
            {
                _ = ActivityExecutor.ExecuteAsync("op", _source, (Func<ActivityWrapper, Task>)null!);
            });
            Assert.Throws<ArgumentNullException>(() =>
            {
                _ = ActivityExecutor.ExecuteRootAsync<int>("root", _source, null!);
            });
            Assert.Throws<ArgumentNullException>(() =>
            {
                _ = ActivityExecutor.ExecuteRootAsync("root", _source, (Func<RootActivityWrapper, Task>)null!);
            });
        }

        [Fact]
        public void Execute_ReturnsActionResultAndSetsOkStatus()
        {
            Activity? captured = null;
            var result = ActivityExecutor.Execute("op", _source, wrapper =>
            {
                captured = wrapper.Activity;
                return 42;
            });

            Assert.Equal(42, result);
            Assert.Equal(ActivityStatusCode.Ok, captured!.Status);
        }

        // Statement-bodied lambda so this binds to the Action overload; an expression lambda would
        // return a value and silently resolve to Execute<T> instead.
        [Fact]
        public void Execute_Void_AppliesOperationNameAndAdditionalData()
        {
            Activity? captured = null;
            ActivityExecutor.Execute("op", _source, wrapper => { captured = wrapper.Activity; },
                options: new ActivityOptions { AdditionalData = new Dictionary<string, object> { ["index"] = "candidate" } });

            Assert.NotNull(captured);
            Assert.Equal("op", captured!.OperationName);
            Assert.Equal("candidate", captured.GetTagItem("index"));
            Assert.Equal(ActivityStatusCode.Ok, captured.Status);
        }

        [Fact]
        public void Execute_Void_WhenActionThrows_RecordsExceptionAndRethrows()
        {
            Activity? captured = null;

            Assert.Throws<InvalidOperationException>(() =>
                ActivityExecutor.Execute("op", _source, wrapper =>
                {
                    captured = wrapper.Activity;
                    throw new InvalidOperationException("boom");
                }));

            Assert.Equal(ActivityStatusCode.Error, captured!.Status);
            Assert.Equal("boom", captured.GetTagItem("exception.message"));
        }

        [Fact]
        public void Execute_Void_WhenCancelled_DoesNotRecordError()
        {
            Activity? captured = null;

            Assert.Throws<OperationCanceledException>(() =>
                ActivityExecutor.Execute("op", _source, wrapper =>
                {
                    captured = wrapper.Activity;
                    throw new OperationCanceledException();
                }));

            // Unset, not merely "not Error": Ok would also satisfy NotEqual, and Ok is precisely the
            // wrong answer for a cancelled operation - it asserts a success nobody observed.
            Assert.Equal(ActivityStatusCode.Unset, captured!.Status);
            Assert.Contains(captured.Events, e => e.Name == "cancelled");
        }

        [Fact]
        public void Execute_WhenActionThrows_RecordsExceptionAndRethrows()
        {
            Activity? captured = null;

            Assert.Throws<InvalidOperationException>(() =>
                ActivityExecutor.Execute<int>("op", _source, wrapper =>
                {
                    captured = wrapper.Activity;
                    throw new InvalidOperationException("boom");
                }));

            Assert.NotNull(captured);
            Assert.Equal(ActivityStatusCode.Error, captured!.Status);
            Assert.Equal("boom", captured.GetTagItem("exception.message"));
        }

        [Fact]
        public void Execute_WhenCancelled_DoesNotRecordError()
        {
            Activity? captured = null;

            Assert.Throws<OperationCanceledException>(() =>
                ActivityExecutor.Execute<int>("op", _source, wrapper =>
                {
                    captured = wrapper.Activity;
                    throw new OperationCanceledException();
                }));

            // Unset, not merely "not Error": Ok would also satisfy NotEqual, and Ok is precisely the
            // wrong answer for a cancelled operation - it asserts a success nobody observed.
            Assert.Equal(ActivityStatusCode.Unset, captured!.Status);
            Assert.Contains(captured.Events, e => e.Name == "cancelled");
        }

        [Fact]
        public void ExecuteRoot_Void_ForceNewDetachedRoot_DetachesFromAmbientParent()
        {
            using var outer = _source.StartActivity("outer");

            Activity? inner = null;
            ActivityExecutor.ExecuteRoot("root", _source, wrapper => { inner = wrapper.Activity; },
                options: Detached);

            Assert.NotNull(inner);
            Assert.Null(inner!.Parent);
            Assert.Null(inner.ParentId);
            Assert.Equal(ActivityStatusCode.Ok, inner.Status);
        }

        [Fact]
        public void ExecuteRoot_Void_WhenActionThrows_RecordsExceptionAndRethrows()
        {
            Activity? captured = null;

            Assert.Throws<InvalidOperationException>(() =>
                ActivityExecutor.ExecuteRoot("root", _source, wrapper =>
                {
                    captured = wrapper.Activity;
                    throw new InvalidOperationException("boom");
                }, options: Detached));

            Assert.Equal(ActivityStatusCode.Error, captured!.Status);
        }

        [Fact]
        public void ExecuteRoot_ReturnsActionResult()
        {
            var result = ActivityExecutor.ExecuteRoot("root", _source, _ => "done", options: Detached);
            Assert.Equal("done", result);
        }

        [Fact]
        public void ExecuteRoot_WhenActionThrows_RecordsExceptionAndRethrows()
        {
            Activity? captured = null;

            Assert.Throws<InvalidOperationException>(() =>
                ActivityExecutor.ExecuteRoot<int>("root", _source, wrapper =>
                {
                    captured = wrapper.Activity;
                    throw new InvalidOperationException("boom");
                }, options: Detached));

            Assert.Equal(ActivityStatusCode.Error, captured!.Status);
        }

        /// <summary>
        /// Stands in for the classic ASP.NET/WinForms context a net48 caller blocks on: posted
        /// continuations are dropped, so anything marshalled back here never runs.
        /// </summary>
        private sealed class NonPumpingSynchronizationContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state) { }
        }

        // Guards ConfigureAwait(false): without it the continuation after `await action(...)` is
        // posted back to the blocked context and never runs.
        //
        // Suppressions are deliberate - blocking under this context IS the scenario under test, and
        // Task.Delay is what forces the await to suspend. Wait is bounded so a regression fails the
        // assert instead of hanging the run.
#pragma warning disable xUnit1031, xUnit1051
        [Fact]
        public void ExecuteAsync_BlockingCallUnderSynchronizationContext_DoesNotDeadlock()
        {
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());

                // The action uses ConfigureAwait(false) so only the executor's own awaits are under test.
                var task = ActivityExecutor.ExecuteAsync("op", _source, async _ =>
                {
                    await Task.Delay(10).ConfigureAwait(false);
                    return 7;
                });

                Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "ExecuteAsync deadlocked under a SynchronizationContext");
                Assert.Equal(7, task.Result);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
#pragma warning restore xUnit1031, xUnit1051

        [Fact]
        public void Execute_NullAction_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                ActivityExecutor.Execute<int>("op", _source, null!));
            Assert.Throws<ArgumentNullException>(() =>
                ActivityExecutor.Execute("op", _source, (Action<ActivityWrapper>)null!));
            Assert.Throws<ArgumentNullException>(() =>
                ActivityExecutor.ExecuteRoot<int>("root", _source, null!));
            Assert.Throws<ArgumentNullException>(() =>
                ActivityExecutor.ExecuteRoot("root", _source, (Action<RootActivityWrapper>)null!));
        }
    }
}
