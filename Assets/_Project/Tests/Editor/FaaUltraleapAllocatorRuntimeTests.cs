using System;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

namespace FAA.Customization.Tests
{
    /// <summary>Exercise the real loaded SDK's managed allocator under Unity Mono; pointers are opaque tokens only.</summary>
    public sealed class FaaUltraleapAllocatorRuntimeTests
    {
        [Test]
        public void RealSdkAllocatorKeepsByteAndFloatOwnershipAcrossFortyThousandConcurrentCycles()
        {
            Type allocator=null;
            foreach(Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                allocator=assembly.GetType("LeapInternal.MemoryManager",false);
                if(allocator!=null)break;
            }
            Assert.That(allocator,Is.Not.Null,"The embedded SDK must be loaded; no replacement allocator or SDK stub is used.");
            const BindingFlags publicStatic=BindingFlags.Public|BindingFlags.Static;
            MethodInfo pin=allocator.GetMethod("Pin",publicStatic),lookup=allocator.GetMethod("GetPinnedObject",publicStatic),unpin=allocator.GetMethod("Unpin",publicStatic);
            Assert.That(pin,Is.Not.Null);Assert.That(lookup,Is.Not.Null);Assert.That(unpin,Is.Not.Null);
            Type hintType=pin.GetParameters()[1].ParameterType;
            object bytesHint=Enum.Parse(hintType,"eLeapAllocatorType_Uint8"),floatHint=Enum.Parse(hintType,"eLeapAllocatorType_Float");
            FieldInfo activeField=allocator.GetField("_activeMemory",BindingFlags.NonPublic|BindingFlags.Static);
            Assert.That(activeField,Is.Not.Null);object active=activeField.GetValue(null);PropertyInfo count=active.GetType().GetProperty("Count");
            int baseline=(int)count.GetValue(active,null),failures=0,completed=0;Exception firstError=null;
            var workers=new Thread[8];using(var start=new ManualResetEventSlim(false))
            {
                for(int worker=0;worker<workers.Length;worker++)
                {
                    int id=worker;
                    workers[worker]=new Thread(()=>
                    {
                        start.Wait();
                        try
                        {
                            for(int i=0;i<5000;i++)
                            {
                                uint requested=(uint)(129+(i%7)*17);bool floats=((i+id)&1)!=0;
                                IntPtr token=(IntPtr)pin.Invoke(null,new object[]{requested,floats?floatHint:bytesHint,IntPtr.Zero});
                                if(token==IntPtr.Zero){Interlocked.Increment(ref failures);continue;}
                                try
                                {
                                    object buffer=lookup.Invoke(null,new object[]{token});
                                    if(floats)
                                    {
                                        var values=buffer as float[];
                                        if(values==null||values.LongLength*4<requested||values.LongLength*4>=requested+4)Interlocked.Increment(ref failures);
                                    }
                                    else
                                    {
                                        var values=buffer as byte[];
                                        if(values==null||values.LongLength!=requested)Interlocked.Increment(ref failures);
                                    }
                                    if((i&127)==0)Thread.Yield();
                                    if(!ReferenceEquals(buffer,lookup.Invoke(null,new object[]{token})))Interlocked.Increment(ref failures);
                                }
                                finally{unpin.Invoke(null,new object[]{token,IntPtr.Zero});}
                                Interlocked.Increment(ref completed);
                            }
                        }
                        catch(Exception e){Interlocked.CompareExchange(ref firstError,e,null);}
                    }){IsBackground=true,Name="Managed SDK allocator test "+worker};
                    workers[worker].Start();
                }
                start.Set();
                foreach(Thread worker in workers)Assert.That(worker.Join(20000),Is.True,"Managed allocator worker must terminate.");
            }
            Assert.That(firstError,Is.Null,firstError!=null?firstError.ToString():null);
            Assert.That(completed,Is.EqualTo(40000));Assert.That(failures,Is.Zero);
            Assert.That((int)count.GetValue(active,null),Is.EqualTo(baseline),"Every test allocation must release its active ownership.");
            TestContext.WriteLine("Real SDK under Unity Mono: 8 workers, 40000 byte/float allocation cycles, 0 ownership failures, active allocations restored to "+baseline+". No native address dereferenced.");
        }
    }
}
