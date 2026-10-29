# Ultraleap allocator regression

Run `python3 Tools/UltraleapAllocatorTests/run.py --output /absolute/path/to/test-output --dotnet /path/to/dotnet`. Requires a .NET8 SDK.

The harness compiles the actual embedded MemoryManager.cs with minimal Unity/AOT declarations, outside Unity. It adds a test-only scheduling seam immediately after pool publication in a generated copy. Production source is never modified. At the default reserve of64 objects, the deterministic two-thread case holds one release while other completed buffers refill the pool. It verifies that a new allocation retains its ownership until its own release. The original7.3.0 ordering reproduces lost ownership. The second case checks200000 concurrent allocation/release cycles and zero active allocations afterward. No native address is dereferenced and no sensor, service or application process is involved.

This regression proves the allocator lifetime defect and its repair. It does not, by itself, attribute a separate Windows/Unity native crash.
