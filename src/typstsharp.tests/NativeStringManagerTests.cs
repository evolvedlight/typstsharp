
namespace typstsharp.tests;

#if DEBUG
public class NativeStringManagerTests
{
    [Test]
    public async Task EnsureAdded()
    {
        using var manager = new NativeStringManager();
        await Assert.That(manager.CurrentCount).IsEqualTo(0);
        
        var ptr = manager.GetNativeUtf8FromString("test");
        await Assert.That(manager.CurrentCount).IsEqualTo(1);
        await Assert.That(manager.DebugNativeStringPointers[0]).IsEqualTo(ptr);
    }

    [Test]
    public async Task EnsureCorrectCount()
    {
        var expectedCount = Math.Min(NativeStringManager.DefaultCapacity, 10);
        using var manager = new NativeStringManager();
        for(var i=0; i < expectedCount; ++i)
        {
            _ = manager.GetNativeUtf8FromString($"test{i}");
        }
        await Assert.That(manager.CurrentCount).IsEqualTo(expectedCount);
        await Assert.That(manager.DebugNativeStringPointers.Length).IsEqualTo(expectedCount);
        await Assert.That(manager.NativeStringPointersArray).IsNotNull();
        await Assert.That(manager.NativeStringPointersArray[expectedCount - 1]).IsNotEqualTo(IntPtr.Zero);
        await Assert.That(manager.NativeStringPointersArray[expectedCount]).IsEqualTo(IntPtr.Zero);
    }
    
    [Test]
    [Arguments(16)]
    [Arguments(64)]
    [Arguments(256)]
    [Arguments(1024)]
    public async Task EnsureContainsAllPointers(int pointerCount)
    {
        var pointers = new List<IntPtr>(pointerCount);
        using var manager = new NativeStringManager();
        for(var i=0; i < pointerCount; ++i)
        {
            var ptr = manager.GetNativeUtf8FromString($"test{i}");
            pointers.Add(ptr);
        }

        await Assert.That(manager.DebugNativeStringPointers.Length).IsEqualTo(pointers.Count);
        
        var managerPointers = manager.DebugNativeStringPointers.ToArray();
        await Assert.That(managerPointers).IsEquivalentTo(pointers);
    }

    [Test]
    [Arguments(1)]
    [Arguments(16)]
    [Arguments(128)]
    [Arguments(1024)]
    public async Task EnsureDispose(int pointerCount)
    {
        var manager = new NativeStringManager();
        var pointers = new List<IntPtr>(pointerCount);
        for(var i=0; i < pointerCount; ++i)
        {
            var ptr = manager.GetNativeUtf8FromString($"test{i}");
            pointers.Add(ptr);
        }
        await Assert.That(manager.CurrentCount).IsEqualTo(pointerCount);

        manager.Dispose();
        await Assert.That(manager.CurrentCount).IsEqualTo(0);
        await Assert.That(manager.NativeStringPointersArray).IsNull();
        await Assert.That(manager.DisposedPointers).IsEquivalentTo(pointers);
    }
}
#endif