using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace FAA.Customization.Tests
{
    public sealed class FaaSourceDiscoveryLivenessTests
    {
        private const string Prefix="FAA.XPlaneIntegration.Runtime.";
        private static Type RuntimeType(string name)=>Type.GetType(Prefix+name+", Assembly-CSharp",true);
        private static readonly BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
        private static Task Worker(Func<Task> run,Action<bool> active,Action<string> diagnostic,Func<int,CancellationToken,Task> delay,CancellationToken token)=>
            (Task)RuntimeType("XPlaneDiscoveryEngine").GetMethod("RunWorkerLoop",Static).Invoke(null,new object[]{"web:http://127.0.0.1:8086",run,active,diagnostic,delay,token});
        private static Task Inventory(Action scan,Action defaults,Action<string> diagnostic,Func<int,CancellationToken,Task> delay,CancellationToken token)=>
            (Task)RuntimeType("XPlaneDiscoveryEngine").GetMethod("RunInventoryLoop",Static).Invoke(null,new object[]{scan,defaults,diagnostic,delay,token});
        private static void Completed(Task task)
        {
            Assert.That(task.Wait(2000),Is.True,"Discovery loop did not observe cancellation/completion.");
            Assert.That(task.IsFaulted,Is.False);
        }
        private static object StoppedEngine()=>Activator.CreateInstance(RuntimeType("XPlaneDiscoveryEngine"),BindingFlags.Instance|BindingFlags.NonPublic,null,
            new[]{Activator.CreateInstance(RuntimeType("XPlaneSourceConfig")),"","",(object)false},null);

        [TestCase(true)] [TestCase(false)]
        public void UnexpectedWorkerFaultOrExitReleasesActiveLeaseAndWaitsBeforeSuccessfulRelaunch(bool fault)
        {
            using(var stop=new CancellationTokenSource())
            {
                int attempts=0,active=0;var waits=new List<int>();var diagnostics=new List<string>();
                var backoff=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task task=Worker(()=>
                {
                    attempts++;Assert.That(active,Is.EqualTo(1));
                    if(attempts==1){if(fault)throw new InvalidOperationException("fixture");return Task.CompletedTask;}
                    stop.Cancel();return Task.CompletedTask;
                },on=>active+=on?1:-1,diagnostics.Add,(milliseconds,token)=>
                {Assert.That(active,Is.Zero,"Attempt lease must be released before retry backoff.");waits.Add(milliseconds);return backoff.Task;},stop.Token);
                try
                {
                    Assert.That(attempts,Is.EqualTo(1));Assert.That(active,Is.Zero);Assert.That(task.IsCompleted,Is.False);
                    Assert.That(waits,Is.EqualTo(new[]{12000}));
                    Assert.That(diagnostics[0],Does.Contain("127.0.0.1:8086").And.Contain("[worker]").And.Contain("12s"));
                    backoff.SetResult(true);Completed(task);Assert.That(attempts,Is.EqualTo(2));Assert.That(active,Is.Zero);
                }
                finally{stop.Cancel();backoff.TrySetResult(true);}
            }
        }
        [Test]
        public void InventoryFailureKeepsDefaultsBeforeScanAndRecoversWithBoundedCadence()
        {
            using(var stop=new CancellationTokenSource())
            {
                int attempts=0,successes=0;var events=new List<string>();var waits=new List<int>();var diagnostics=new List<string>();
                var backoff=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task task=Inventory(()=>
                {events.Add("scan");if(++attempts==1)throw new InvalidOperationException("fixture");successes++;},
                ()=>events.Add("defaults"),diagnostics.Add,(milliseconds,token)=>
                {waits.Add(milliseconds);if(waits.Count==1)return backoff.Task;stop.Cancel();return Task.FromCanceled(stop.Token);},stop.Token);
                try
                {
                    Assert.That(events,Is.EqualTo(new[]{"defaults","scan"}));Assert.That(attempts,Is.EqualTo(1));
                    Assert.That(task.IsCompleted,Is.False);Assert.That(waits,Is.EqualTo(new[]{12000}));
                    Assert.That(diagnostics[0],Does.Contain("[scan]").And.Contain("InvalidOperationException").And.Contain("default probes continue"));
                    backoff.SetResult(true);Completed(task);
                    Assert.That(events,Is.EqualTo(new[]{"defaults","scan","defaults","scan"}));Assert.That(successes,Is.EqualTo(1));
                    Assert.That(waits,Is.EqualTo(new[]{12000,30000}));
                }
                finally{stop.Cancel();backoff.TrySetResult(true);}
            }
        }
        [TestCase(true)] [TestCase(false)]
        public void CancelledBeforeStartNeverRunsInventoryOrWorker(bool inventory)
        {
            using(var stop=new CancellationTokenSource())
            {
                stop.Cancel();int calls=0;
                Task task=inventory?Inventory(()=>calls++,()=>calls++,_=>calls++,(ms,ct)=>{calls++;return Task.CompletedTask;},stop.Token):
                    Worker(()=>{calls++;return Task.CompletedTask;},_=>calls++,_=>calls++,(ms,ct)=>{calls++;return Task.CompletedTask;},stop.Token);
                Completed(task);Assert.That(calls,Is.Zero);
            }
        }
        [TestCase(true)] [TestCase(false)]
        public void CancellationDuringBackoffNeverRestartsInventoryOrWorker(bool inventory)
        {
            using(var stop=new CancellationTokenSource())
            {
                int attempts=0,waits=0,active=0;
                Func<int,CancellationToken,Task> delay=(ms,ct)=>{waits++;Assert.That(ms,Is.EqualTo(12000));return Task.Delay(Timeout.Infinite,ct);};
                Task task=inventory?Inventory(()=>{attempts++;throw new InvalidOperationException();},()=>{},_=>{},delay,stop.Token):
                    Worker(()=>{attempts++;throw new InvalidOperationException();},on=>active+=on?1:-1,_=>{},delay,stop.Token);
                Assert.That(attempts,Is.EqualTo(1));Assert.That(waits,Is.EqualTo(1));Assert.That(task.IsCompleted,Is.False);
                stop.Cancel();Completed(task);Assert.That(attempts,Is.EqualTo(1));Assert.That(active,Is.Zero);
            }
        }
        [Test]
        public void CancellationDuringWorkerReleasesLeaseWithoutRetryOrFailureDiagnostic()
        {
            using(var stop=new CancellationTokenSource())
            {
                int active=0,waits=0,diagnostics=0;
                Task task=Worker(()=>Task.Delay(Timeout.Infinite,stop.Token),on=>active+=on?1:-1,_=>diagnostics++,(ms,ct)=>{waits++;return Task.CompletedTask;},stop.Token);
                Assert.That(active,Is.EqualTo(1));stop.Cancel();Completed(task);
                Assert.That(active,Is.Zero);Assert.That(waits,Is.Zero);Assert.That(diagnostics,Is.Zero);
            }
        }
        [Test]
        public void FatalWorkerFaultIsNotConvertedIntoRetryAndStillReleasesLease()
        {
            using(var stop=new CancellationTokenSource())
            {
                int active=0,waits=0,diagnostics=0;
                Task task=Worker(()=>Task.FromException(new OutOfMemoryException("synthetic fixture")),on=>active+=on?1:-1,_=>diagnostics++,(ms,ct)=>{waits++;return Task.CompletedTask;},stop.Token);
                Assert.That(task.IsFaulted,Is.True);Assert.That(task.Exception.GetBaseException(),Is.TypeOf<OutOfMemoryException>());
                Assert.That(active,Is.Zero);Assert.That(waits,Is.Zero);Assert.That(diagnostics,Is.Zero);
            }
        }
        [TestCase(typeof(OutOfMemoryException))] [TestCase(typeof(StackOverflowException))]
        [TestCase(typeof(AccessViolationException))] [TestCase(typeof(System.Runtime.InteropServices.SEHException))]
        public void FatalExceptionCategoriesAreNeverRetryable(Type exceptionType)=>
            Assert.That(RuntimeType("XPlaneDiscoveryEngine").GetMethod("IsRecoverable",Static).Invoke(null,new[]{Activator.CreateInstance(exceptionType)}),Is.False);

        [Test]
        public void WrappedFatalExceptionIsNeverRetryable()=>
            Assert.That(RuntimeType("XPlaneDiscoveryEngine").GetMethod("IsRecoverable",Static).Invoke(null,new object[]{new AggregateException(new InvalidOperationException(),new OutOfMemoryException("synthetic fixture"))}),Is.False);

        [Test]
        public void ActualEngineDeduplicatesEndpointDuringBackoffAndDisposedEngineCannotLaunch()
        {
            var engine=StoppedEngine();var disposable=(IDisposable)engine;int attempts=0;
            var launch=engine.GetType().GetMethod("Launch",BindingFlags.Instance|BindingFlags.NonPublic);
            Func<Action<string>,Task> run=_=>{Interlocked.Increment(ref attempts);return Task.CompletedTask;};
            try
            {
                launch.Invoke(engine,new object[]{"web:fixture",run});
                Assert.That(SpinWait.SpinUntil(()=>Volatile.Read(ref attempts)==1,2000),Is.True);
                for(int i=0;i<100;i++)launch.Invoke(engine,new object[]{"web:fixture",run});
                Assert.That(Volatile.Read(ref attempts),Is.EqualTo(1));
                disposable.Dispose();launch.Invoke(engine,new object[]{"web:new-after-stop",run});
                Assert.That(Volatile.Read(ref attempts),Is.EqualTo(1));
            }
            finally{disposable.Dispose();}
        }
        [Test]
        public void NativeWebEndpointStageRemainsVisibleAfterUdpAndMqttErrors()
        {
            var engine=StoppedEngine();
            try
            {
                var diagnostic=engine.GetType().GetMethod("Diagnostic",BindingFlags.Instance|BindingFlags.NonPublic);
                diagnostic.Invoke(engine,new object[]{"web:http://127.0.0.1:8086","Native Web API 127.0.0.1:8086 [catalog] timeout; retry in 12s"});
                diagnostic.Invoke(engine,new object[]{"rref:127.0.0.1:49000","UDP unavailable"});
                diagnostic.Invoke(engine,new object[]{"mqtt:127.0.0.1:1883","MQTT unavailable"});
                string status=(string)engine.GetType().GetProperty("LastDiagnostic").GetValue(engine);
                Assert.That(status,Does.StartWith("Native Web API 127.0.0.1:8086 [catalog]"));
                Assert.That(((string[])engine.GetType().GetProperty("EndpointDiagnostics").GetValue(engine)).Length,Is.EqualTo(3));
            }
            finally{((IDisposable)engine).Dispose();}
        }
        [TestCase("warming")] [TestCase("stale")]
        public void UnhealthySourceShowsNativeDiagnosticAheadOfUnvalidatedCandidates(string state)
        {
            string diagnostic="Native Web API 127.0.0.1:8086 [catalog] timeout; retry in 12s";
            Assert.That(CandidateCaption(false,diagnostic,new[]{"Native candidate "+state}),Is.EqualTo(diagnostic));
        }
        [Test]
        public void HealthySourceRetainsCandidateDisplayAndCount()=>
            Assert.That(CandidateCaption(true,"Native diagnostic",new[]{"Validated local source","Other source"}),Is.EqualTo("Validated local source  (+1 more)"));
        [Test]
        public void MissingDiagnosticStillShowsAvailableCandidates()=>
            Assert.That(CandidateCaption(false,"",new[]{"Native candidate warming"}),Is.EqualTo("Native candidate warming"));
        [Test]
        public void DiagnosticCaptionIsPlainTextAndBoundedToExistingTwoLineLayout()
        {
            string caption=CandidateCaption(false,"Native Web API 127.0.0.1:8086 [catalog] <bad>\n"+new string('x',200),new[]{"stale"});
            Assert.That(caption.Length,Is.EqualTo(140));Assert.That(caption,Does.StartWith("Native Web API 127.0.0.1:8086 [catalog]"));
            Assert.That(caption,Does.Not.Contain("<").And.Not.Contain(">").And.Not.Contain("\n"));
        }
        private static string CandidateCaption(bool valid,string diagnostic,string[] descriptions)=>
            (string)Type.GetType("FAA.Customization.FaaSpatialWorkspace, Assembly-CSharp",true).GetMethod("SourceCandidateCaption",Static).Invoke(null,new object[]{valid,diagnostic,descriptions});

        [Test]
        public void WebValidationDiagnosticsIdentifyClockIdentityAndCoreWithoutFabricatingData()
        {
            var values=new Dictionary<string,double>();
            var reason=RuntimeType("XPlaneDiscoveryWeb").GetMethod("WaitingReason",Static);
            Assert.That(reason.Invoke(null,new object[]{values,10d}),Is.EqualTo("awaiting simulator clock"));
            values["sim/time/total_running_time_sec"]=10;
            Assert.That(reason.Invoke(null,new object[]{values,10d}),Is.EqualTo("awaiting advancing simulator clock"));
            values["sim/time/paused"]=1;
            Assert.That(reason.Invoke(null,new object[]{values,10d}),Is.EqualTo("awaiting valid X-Plane 12 identity"));
            values["sim/version/xplane_internal_version"]=120400;
            Assert.That(reason.Invoke(null,new object[]{values,10d}),Is.EqualTo("awaiting complete valid flight core"));
        }
    }
}
