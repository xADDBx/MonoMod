extern alias New;
using MonoMod.Cil;
using New::MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Abstractions;

namespace MonoMod.UnitTest
{
    [Collection("RuntimeDetour")]
    public class SharedGenericHookTest : TestBase
    {
        public SharedGenericHookTest(ITestOutputHelper helper) : base(helper)
        {
        }

        [SharedGenericFact]
        public void HookInstantiationsOfSharedGenericMethod()
        {
            var describe = typeof(SharedGenericHookTest).GetMethod(nameof(Describe), BindingFlags.Static | BindingFlags.NonPublic)!;
            using (new Hook(describe.MakeGenericMethod(typeof(string)),
                new Func<Func<string, int, string>, string, int, string>((orig, value, count) => "hooked " + orig(value, count))))
            using (new Hook(describe.MakeGenericMethod(typeof(Version)),
                new Func<Func<Version, int, string>, Version, int, string>((orig, value, count) => "other " + orig(value, count))))
            {
                Assert.Equal("hooked String a 1", Describe("a", 1));
                Assert.Equal("other Version 1.0 2", Describe(new Version(1, 0), 2));
                Assert.Equal("Object a 3", Describe<object>("a", 3));
            }

            Assert.Equal("String a 1", Describe("a", 1));
        }

        [SharedGenericFact]
        public void HookInstanceMethodOfSharedGenericValueType()
        {
            using (new Hook(typeof(Wrapper<string>).GetMethod(nameof(Wrapper<string>.Combine))!,
                new CombineHook((CombineOrig orig, ref Wrapper<string> self, long a, double b, string c, float d) =>
                {
                    var result = orig(ref self, a, b, c, d);
                    result.A++;
                    return result;
                })))
            {
                Assert.Equal(new Large(2, 2.5, "a b", 3.5f, "String"), new Wrapper<string>("a").Combine(1, 2.5, "b", 3.5f));
                Assert.Equal(new Large(1, 2.5, "a b", 3.5f, "Object"), new Wrapper<object>("a").Combine(1, 2.5, "b", 3.5f));
            }

            Assert.Equal(new Large(1, 2.5, "a b", 3.5f, "String"), new Wrapper<string>("a").Combine(1, 2.5, "b", 3.5f));
        }

        [SharedGenericFact]
        public void HookSharedGenericMethodOfSharedGenericType()
        {
            using (new Hook(typeof(Container<string>).GetMethod(nameof(Container<string>.Pair))!.MakeGenericMethod(typeof(Version)),
                new Func<Func<Container<string>, Version, string>, Container<string>, Version, string>((orig, self, value) => "hooked " + orig(self, value))))
            {
                Assert.Equal("hooked String Version", new Container<string>().Pair(new Version()));
                Assert.Equal("String Object", new Container<string>().Pair(new object()));
                Assert.Equal("Object Version", new Container<object>().Pair(new Version()));
            }

            Assert.Equal("String Version", new Container<string>().Pair(new Version()));
        }

        [SharedGenericFact]
        public void HookPartiallySharedInstantiation()
        {
            using (new Hook(typeof(Container<KeyValuePair<string, int>>).GetMethod(nameof(Container<int>.Pair))!.MakeGenericMethod(typeof(int)),
                new Func<Func<Container<KeyValuePair<string, int>>, int, string>, Container<KeyValuePair<string, int>>, int, string>(
                    (orig, self, value) => "hooked " + orig(self, value))))
            {
                Assert.Equal("hooked KeyValuePair`2 Int32", new Container<KeyValuePair<string, int>>().Pair(1));
                Assert.Equal("KeyValuePair`2 Int32", new Container<KeyValuePair<object, int>>().Pair(1));
            }
        }

        [SharedGenericFact]
        public void ILHookSharedInstantiation()
        {
            using (new ILHook(typeof(Container<string>).GetMethod(nameof(Container<string>.Name))!, il =>
            {
                var c = new ILCursor(il);
                Assert.True(c.TryGotoNext(i => i.MatchLdstr("name ")));
                c.Next!.Operand = "il ";
            }))
            {
                Assert.Equal("il String", new Container<string>().Name());
                Assert.Equal("name Object", new Container<object>().Name());
            }

            Assert.Equal("name String", new Container<string>().Name());
        }

        [SharedGenericFact]
        public void NullThisRunsOriginal()
        {
            var call = (Func<Container<object>?, string>)Delegate.CreateDelegate(
                typeof(Func<Container<object>?, string>), typeof(Container<object>).GetMethod(nameof(Container<object>.NoThis))!);
            using (new Hook(typeof(Container<string>).GetMethod(nameof(Container<string>.NoThis))!,
                new Func<Container<string>, string>(self => "hooked")))
            {
                Assert.Equal("hooked", new Container<string>().NoThis());
                Assert.Equal("original", call(null));
            }
        }

        [SharedGenericFact]
        public void HookInstantiationSharingCodeWithDispatcher()
        {
            // the dispatcher has a Dictionary<IntPtr, MethodBase>
            var hooked = false;
            using (new Hook(typeof(Dictionary<IntPtr, string>).GetMethod("set_Item")!,
                new Action<Action<Dictionary<IntPtr, string>, IntPtr, string>, Dictionary<IntPtr, string>, IntPtr, string>((orig, self, key, value) =>
                {
                    hooked = true;
                    orig(self, key, value);
                })))
            {
                SetItem(new Dictionary<IntPtr, object>(), "a");
                Assert.False(hooked);
                SetItem(new Dictionary<IntPtr, string>(), "a");
                Assert.True(hooked);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SetItem<T>(Dictionary<IntPtr, T> dictionary, T value) => dictionary[IntPtr.Zero] = value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string Describe<T>(T value, int count) => $"{typeof(T).Name} {value} {count}";

        private delegate Large CombineOrig(ref Wrapper<string> self, long a, double b, string c, float d);

        private delegate Large CombineHook(CombineOrig orig, ref Wrapper<string> self, long a, double b, string c, float d);

        public record struct Large(long A, double B, string C, float D, string E);

        public readonly struct Wrapper<T>(T value)
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public Large Combine(long a, double b, string c, float d) => new(a, b, $"{value} {c}", d, typeof(T).Name);
        }

        public class Container<T>
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public string Pair<U>(U value) => $"{typeof(T).Name} {typeof(U).Name}";

            [MethodImpl(MethodImplOptions.NoInlining)]
            public string Name() => "name " + typeof(T).Name;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public string NoThis() => "original";
        }
    }
}
