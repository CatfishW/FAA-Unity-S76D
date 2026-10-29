using System;using System.Collections.Generic;using System.Threading;
namespace AOT{public sealed class MonoPInvokeCallbackAttribute:Attribute{public MonoPInvokeCallbackAttribute(Type t){}}}
namespace UnityEngine{public static class Debug{public static int Errors;public static void LogException(Exception e){System.Threading.Interlocked.Increment(ref Errors);Console.WriteLine("SDK_EXCEPTION:"+e.GetType().Name);}}}
namespace LeapInternal{public enum eLeapAllocatorType{eLeapAllocatorType_Uint8,eLeapAllocatorType_Float}}
class Program{
static int Main(string[] args){if(args.Length>0&&args[0]=="stress")return Stress();
var allocations=new List<IntPtr>();for(int i=0;i<66;i++)allocations.Add(LeapInternal.MemoryManager.Pin(256,0,IntPtr.Zero));
foreach(var a in allocations)LeapInternal.MemoryManager.Unpin(a,IntPtr.Zero);
IntPtr original=LeapInternal.MemoryManager.Pin(256,0,IntPtr.Zero);
object originalBuffer=LeapInternal.MemoryManager.GetPinnedObject(original);
var spares=new List<IntPtr>();for(int i=0;i<64;i++)spares.Add(LeapInternal.MemoryManager.Pin(256,0,IntPtr.Zero));
Thread retiring=null;
using var published=new ManualResetEventSlim();using var resume=new ManualResetEventSlim();
LeapInternal.MemoryManager.PublishedForTest=()=>{if(Thread.CurrentThread!=retiring)return;published.Set();if(!resume.Wait(5000))throw new Exception("Test scheduling timeout");};
retiring=new Thread(()=>LeapInternal.MemoryManager.Unpin(original,IntPtr.Zero));retiring.Start();
if(!published.Wait(5000)){resume.Set();retiring.Join();Console.WriteLine("No scheduling seam");return 2;}
foreach(var a in spares)LeapInternal.MemoryManager.Unpin(a,IntPtr.Zero);
var held=new List<IntPtr>();IntPtr reused=IntPtr.Zero;
for(int i=0;i<65;i++){var a=LeapInternal.MemoryManager.Pin(256,0,IntPtr.Zero);held.Add(a);if(ReferenceEquals(originalBuffer,LeapInternal.MemoryManager.GetPinnedObject(a)))reused=a;}
LeapInternal.MemoryManager.PublishedForTest=null;resume.Set();retiring.Join();
if(reused==IntPtr.Zero){Console.WriteLine("Test_precondition_reuse_missing");return 2;}
bool ownerPresent=LeapInternal.MemoryManager.GetPinnedObject(reused)!=null;
Console.WriteLine("Returned_same_buffer="+(reused!=IntPtr.Zero));Console.WriteLine("Allocated_buffer_retains_ownership="+ownerPresent);
// The test only checks managed ownership; it never dereferences a native address.
foreach(var a in held)LeapInternal.MemoryManager.Unpin(a,IntPtr.Zero);
Console.WriteLine("Active_allocations_after_release="+ActiveCount());return ownerPresent&&ActiveCount()==0?0:1;}
static int ActiveCount(){var f=typeof(LeapInternal.MemoryManager).GetField("_activeMemory",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);object map=f.GetValue(null);return (int)map.GetType().GetProperty("Count").GetValue(map);}
static int Stress(){int failures=0;System.Threading.Tasks.Parallel.For(0,8,worker=>{for(int i=0;i<25000;i++){uint size=(uint)(128+(i%8)*16);var a=LeapInternal.MemoryManager.Pin(size,0,IntPtr.Zero);object buffer=LeapInternal.MemoryManager.GetPinnedObject(a);if(a==IntPtr.Zero||!(buffer is byte[] bytes)||bytes.Length<size)Interlocked.Increment(ref failures);if((i&127)==0)Thread.Yield();if(!ReferenceEquals(buffer,LeapInternal.MemoryManager.GetPinnedObject(a)))Interlocked.Increment(ref failures);LeapInternal.MemoryManager.Unpin(a,IntPtr.Zero);}});Console.WriteLine("Concurrent_allocation_cycles=200000");Console.WriteLine("Ownership_failures="+failures);Console.WriteLine("Sdk_exceptions="+UnityEngine.Debug.Errors);Console.WriteLine("Active_allocations_after_release="+ActiveCount());return failures==0&&UnityEngine.Debug.Errors==0&&ActiveCount()==0?0:1;}}

