/******************************************************************************
 * Copyright (C) Ultraleap, Inc. 2011-2025.                                   *
 *                                                                            *
 * Use subject to the terms of the Apache License 2.0 available at            *
 * http://www.apache.org/licenses/LICENSE-2.0, or another agreement           *
 * between Ultraleap and you, your company or other organization.             *
 ******************************************************************************/

// FAA local modification (2026-10-29): make pooled buffer release and pin ownership
// safe across concurrent allocator callbacks; retain managed references for lookups.

using AOT;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LeapInternal
{

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr Allocate(UInt32 size, eLeapAllocatorType typeHint, IntPtr state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void Deallocate(IntPtr buffer, IntPtr state);

    public static class MemoryManager
    {

        /// <summary>
        /// Specifies whether or not a pooling strategy should be used for the
        /// internal MemoryManager.  If enabled, memory will be periodically 
        /// recycled to be used again instead of being deallocated.  
        /// 
        /// An object may be reclaimed from the pool at any time on the 
        /// worker thread.  If you are running into issues where an object
        /// you are working with is being overwritten, consider making a copy,
        /// or turning up the MinPoolSize.
        /// </summary>
        public static bool EnablePooling = true;

        /// <summary>
        /// Specifies how many objects of a specific type need to be in the pool
        /// before they will start to be recycled.  Turning this number up can
        /// help prevent issues where objects you are working with are being
        /// overwritten with new objects.  Turning this number down can reduce
        /// the total memory footprint used by the memory manager.
        /// </summary>
        public static uint MinPoolSize = 64;

        private static ConcurrentDictionary<IntPtr, ActiveMemoryInfo> _activeMemory =
          new ConcurrentDictionary<IntPtr, ActiveMemoryInfo>();
        private static ConcurrentDictionary<PoolKey, ConcurrentQueue<object>> _pooledMemory =
          new ConcurrentDictionary<PoolKey, ConcurrentQueue<object>>();

        [MonoPInvokeCallback(typeof(Allocate))]
        public static IntPtr Pin(UInt32 size, eLeapAllocatorType typeHint, IntPtr state)
        {
            GCHandle handle = default(GCHandle);
            try
            {
                //Construct a key to identify the desired allocation
                PoolKey key = new PoolKey()
                {
                    type = typeHint,
                    size = size
                };

                //Attempt to find the pool that holds this type of allocation
                ConcurrentQueue<object> pool = _pooledMemory.GetOrAdd(key, _ => new ConcurrentQueue<object>());

                //Count is only advisory: another allocator can empty the queue before TryDequeue.
                object memory = null;
                if (!EnablePooling || pool.Count <= MinPoolSize || !pool.TryDequeue(out memory) || memory == null)
                {
                    //If the pool is empty, we need to construct a new object
                    switch (typeHint)
                    {
                        default:
                        case eLeapAllocatorType.eLeapAllocatorType_Uint8:
                            memory = new byte[size];
                            break;
                        case eLeapAllocatorType.eLeapAllocatorType_Float:
                            memory = new float[size / sizeof(float) + (size % sizeof(float) == 0 ? 0U : 1U)];
                            break;
                    }
                }

                //Pin the object so its address will not change
                handle = GCHandle.Alloc(memory, GCHandleType.Pinned);
                IntPtr ptr = handle.AddrOfPinnedObject();
                if (ptr == IntPtr.Zero) return IntPtr.Zero;

                //Put the information about the newly pinned allocation into the
                //active memory map so it can be retrieved and freed layer.
                if (!_activeMemory.TryAdd(ptr, new ActiveMemoryInfo()
                {
                    handle = handle,
                    key = key,
                    memory = memory
                }))
                {
                    //A duplicate address must not replace another allocation's ownership.
                    return IntPtr.Zero;
                }

                //The active map now owns this handle; failure cleanup must not free it.
                handle = default(GCHandle);
                return ptr;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
            finally
            {
                if (handle.IsAllocated) handle.Free();
            }

            return IntPtr.Zero;
        }

        [MonoPInvokeCallback(typeof(Deallocate))]
        public static void Unpin(IntPtr ptr, IntPtr state)
        {
            try
            {
                //Remove ownership before making the buffer reusable. Only the successful remover may free it.
                ActiveMemoryInfo info;
                if (!_activeMemory.TryRemove(ptr, out info)) return;
                object memory = info.memory;
                info.handle.Free();

                //Publishing before Free lets another allocation reuse the same address while the old
                //release can still remove its new ownership record or expose a movable native buffer.
                if (EnablePooling && memory != null)
                {
                    _pooledMemory.GetOrAdd(info.key, _ => new ConcurrentQueue<object>()).Enqueue(memory);
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        public static object GetPinnedObject(IntPtr ptr)
        {
            //The managed reference remains safe even if Unpin concurrently frees/recycles the GCHandle.
            //Never read Target from a handle copied before its ownership was removed.
            ActiveMemoryInfo info;
            return _activeMemory.TryGetValue(ptr, out info) ? info.memory : null;
        }

        private struct PoolKey : IEquatable<PoolKey>
        {
            public eLeapAllocatorType type;
            public UInt32 size;

            public override int GetHashCode()
            {
                return (int)type | (int)size << 4;
            }

            public bool Equals(PoolKey other)
            {
                return type == other.type &&
                       size == other.size;
            }

            public override bool Equals(object obj)
            {
                if (obj is PoolKey)
                {
                    return Equals((PoolKey)obj);
                }
                else
                {
                    return false;
                }
            }
        }

        private struct ActiveMemoryInfo
        {
            public GCHandle handle;
            public PoolKey key;
            public object memory;
        }
    }
}