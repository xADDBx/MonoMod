#pragma warning disable CS1720 // Expression will always cause a System.NullReferenceException because the type's default value is null
#pragma warning disable xUnit1013 // Public method should be marked as test

extern alias New;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using New::MonoMod.RuntimeDetour;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Abstractions;

namespace MonoMod.UnitTest
{
    [Collection("RuntimeDetour")]
    public class ILHookTest : TestBase
    {
        private bool DidNothing = true;

        public ILHookTest(ITestOutputHelper helper) : base(helper)
        {
        }

        [Fact]
        public void TestILHooks()
        {
            DidNothing = true;
            DoNothing();
            Assert.True(DidNothing);

            using (var h = new ILHook(
                typeof(ILHookTest).GetMethod("DoNothing", BindingFlags.Instance | BindingFlags.NonPublic),
                il =>
                {
                    var c = new ILCursor(il);
                    c.Emit(OpCodes.Ldarg_0);
                    c.Emit(OpCodes.Ldc_I4_0);
                    c.Emit(OpCodes.Stfld, typeof(ILHookTest).GetField("DidNothing", BindingFlags.NonPublic | BindingFlags.Instance));
                }
            ))
            {
                DidNothing = true;
                DoNothing();
                Assert.False(DidNothing);
            }

            DidNothing = true;
            DoNothing();
            Assert.True(DidNothing);
        }

        [Fact]
        public void TestILHookOnGenericMethodReflectedThroughDerivedType()
        {
            using (var h = new ILHook(
                typeof(Derived).GetMethod("Name").MakeGenericMethod(typeof(int)),
                il =>
                {
                    var c = new ILCursor(il);
                    c.Emit(OpCodes.Ldstr, "Hooked");
                    c.Emit(OpCodes.Ret);
                }
            ))
            {
                Assert.Equal("Hooked", new Derived().Name<int>());
            }
        }

        internal class Base
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public string Name<T>() => typeof(T).Name;
        }

        internal class Derived : Base
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void DoNothing()
        {
        }

    }
}
