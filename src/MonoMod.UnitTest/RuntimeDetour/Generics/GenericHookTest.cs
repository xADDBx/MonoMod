extern alias New;

using MonoMod.Backports;
using New::MonoMod.RuntimeDetour;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace MonoMod.UnitTest.RuntimeDetour.Generics
{
    [Collection("RuntimeDetour")]
    public class GenericHookTest(ITestOutputHelper helper) : TestBase(helper)
    {
        internal static class Methods
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Shared<T>(T value) => "orig " + value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Late<T>(T value) => "orig " + value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Queried<T>(T value) => "orig " + value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Mixed<T1, T2>(T1 a, T2 b) => "orig " + a + b;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Toggled<T>(T value) => "orig " + value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Composed<T>(T value) => "orig " + value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Ordered<T>(T value) => "orig " + value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Repeated<T>(T value) => "orig " + value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Described<T>() => typeof(T).Name;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Replaced<T>(T value) => "orig " + value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Constrained<T>(T value) => "orig " + value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string ConstrainedSource<T>(T value) where T : class, IFoo => "orig " + value;

            // small enough to be inlined into their callers
            public static T Inlinable<T>(T value) => value;
            public static string InlinableString(string value) => value;
        }

        internal class Box<T>(T value)
        {
            public T Value = value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public string Inst(int x) => "orig " + Value + x;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public string Combine<TM>(TM second) => "orig " + Value + second;
        }

        internal class DerivedBox(string value) : Box<string>(value)
        {
            public override string ToString() => "box" + Value;
        }

        internal interface IFoo;

        internal sealed class Foo(string name) : IFoo
        {
            public override string ToString() => name;
        }

        // no method is compiled for it before the hook on that method
        internal struct LateStruct(int value)
        {
            public override readonly string ToString() => "s" + value;
        }

        internal struct MixedStruct(int value)
        {
            public override readonly string ToString() => "m" + value;
        }

        internal delegate string StringOrig(string value);

        internal static string Generic<T>(Func<T, string> orig, T value) => "generic " + orig(value);
        internal static string GenericMixed<T1, T2>(Func<T1, T2, string> orig, T1 a, T2 b) => "generic " + orig(a, b);
        internal static string BoxInst<T>(Func<Box<T>, int, string> orig, Box<T> self, int x) => "generic " + orig(self, x);
        internal static string Described<T>(Func<string> orig) => typeof(T).Name + " " + orig();
        internal static string Replace<T>(T value) => "replaced " + typeof(T).Name + value;
        internal static string BoxesOnly<T>(Func<T, string> orig, T value) where T : Box<string> => "box " + orig(value);
        internal static string FoosOnly<T>(Func<T, string> orig, T value) where T : class, IFoo => "foo " + orig(value);
        internal static string BoxCombine<T, TM>(Func<Box<T>, TM, string> orig, Box<T> self, TM second) => "generic " + orig(self, second);
        // a different orig delegate type than the one of GenericHook's target
        internal static string Closed(StringOrig orig, string value) => "closed " + orig(value);
        internal static string ClosedObject(Func<object, string> orig, object value) => "closed " + orig(value);
        internal static string Inlined(Func<string, string> orig, string value) => orig(value) + "!";

        private static MethodInfo Source(string name) => typeof(Methods).GetMethod(name)!;
        private static MethodInfo Target(string name) => typeof(GenericHookTest).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

        [Fact]
        public void HooksSharedInstantiations()
        {
            Assert.Equal("orig a", Methods.Shared("a"));
            using (new GenericHook(Source(nameof(Methods.Shared)), Target(nameof(Generic))))
            {
                Assert.Equal("generic orig a", Methods.Shared("a"));
                Assert.Equal("generic orig b", Methods.Shared<object>("b"));
                Assert.Equal("generic orig sb", Methods.Shared(new StringBuilder("sb")));
            }
            Assert.Equal("orig a", Methods.Shared("a"));
            Assert.Equal("orig b", Methods.Shared<object>("b"));
        }

        [Fact]
        public void HooksInstantiationsCompiledLater()
        {
            using var hook = new GenericHook(Source(nameof(Methods.Late)), Target(nameof(Generic)));
            Assert.Equal("generic orig s1", Methods.Late(new LateStruct(1)));
        }

        [Fact]
        public void DetourInfoQueriesDoNotHook()
        {
            var queried = Source(nameof(Methods.Queried));
            _ = DetourManager.GetDetourInfo(queried.MakeGenericMethod(typeof(LateStruct)));
            using var hook = new GenericHook(queried, Target(nameof(Generic)));
            _ = DetourManager.GetDetourInfo(queried.MakeGenericMethod(typeof(MixedStruct)));
            Assert.Empty(hook.Instantiations);
        }

        [Fact]
        public void HooksMixedSharedFormsCompiledLater()
        {
            using (new GenericHook(Source(nameof(Methods.Mixed)), Target(nameof(GenericMixed))))
            {
                Assert.Equal("generic orig m1a", Methods.Mixed(new MixedStruct(1), "a"));
                Assert.Equal("generic orig m2b", Methods.Mixed<MixedStruct, object>(new MixedStruct(2), "b"));
            }
            Assert.Equal("orig m1a", Methods.Mixed(new MixedStruct(1), "a"));
        }

        [Fact]
        public void ConstraintsOfTheTargetFilterInstantiations()
        {
            using var hook = new GenericHook(Source(nameof(Methods.Constrained)), Target(nameof(BoxesOnly)));
            Assert.Equal("orig a", Methods.Constrained("a"));
            Assert.Equal("box orig boxb", Methods.Constrained(new DerivedBox("b")));
            Assert.Equal("orig c", Methods.Constrained("c"));
            Assert.Equal("orig 1", Methods.Constrained(1));
        }

        [Fact]
        public void SourceConstraintsWhichObjectDoesNotSatisfy()
        {
            // compiled before the hook exists
            Assert.Equal("orig a", Methods.ConstrainedSource(new Foo("a")));
            using (new GenericHook(Source(nameof(Methods.ConstrainedSource)), Target(nameof(FoosOnly))))
                Assert.Equal("foo orig b", Methods.ConstrainedSource(new Foo("b")));
        }

        [Fact]
        public void TargetsWithoutOrig()
        {
            using (new GenericHook(Source(nameof(Methods.Replaced)), Target(nameof(Replace))))
            {
                Assert.Equal("replaced Stringa", Methods.Replaced("a"));
                Assert.Equal("replaced Objectb", Methods.Replaced<object>("b"));
            }
        }

        [Fact]
        public void GenericMethodsOnGenericTypes()
        {
            using (new GenericHook(typeof(Box<>).GetMethod(nameof(Box<int>.Combine))!, Target(nameof(BoxCombine))))
            {
                Assert.Equal("generic orig ab", new Box<string>("a").Combine("b"));
                Assert.Equal("generic orig cd", new Box<object>("c").Combine<object>("d"));
                Assert.Equal("generic orig 1e", new Box<int>(1).Combine("e"));
            }
            Assert.Equal("orig ab", new Box<string>("a").Combine("b"));
        }

        [Fact]
        public void MethodsOnGenericTypes()
        {
            using (new GenericHook(typeof(Box<>).GetMethod(nameof(Box<int>.Inst))!, Target(nameof(BoxInst))))
            {
                Assert.Equal("generic orig a1", new Box<string>("a").Inst(1));
                Assert.Equal("generic orig b2", new DerivedBox("b").Inst(2));
                Assert.Equal("generic orig c3", new Box<object>("c").Inst(3));
                Assert.Equal("generic orig s14", new Box<LateStruct>(new LateStruct(1)).Inst(4));
            }
            Assert.Equal("orig a1", new Box<string>("a").Inst(1));
        }

        [Fact]
        public void OrigKeepsItsInstantiation()
        {
            // no arguments: only the generic context tells the instantiations apart
            using (new GenericHook(Source(nameof(Methods.Described)), Target(nameof(Described))))
            {
                Assert.Equal("String String", Methods.Described<string>());
                Assert.Equal("Object Object", Methods.Described<object>());
            }
            Assert.Equal("String", Methods.Described<string>());
        }

        [Fact]
        public void UndoAndApply()
        {
            using var hook = new GenericHook(Source(nameof(Methods.Toggled)), Target(nameof(Generic)), applyByDefault: false);
            Assert.False(hook.IsApplied);
            Assert.Equal("orig a", Methods.Toggled("a"));
            hook.Apply();
            Assert.Equal("generic orig a", Methods.Toggled("a"));
            hook.Undo();
            Assert.Equal("orig a", Methods.Toggled("a"));
            Assert.Equal("orig b", Methods.Toggled<object>("b"));
            hook.Apply();
            Assert.Equal("generic orig a", Methods.Toggled("a"));
            Assert.Equal("generic orig b", Methods.Toggled<object>("b"));
            hook.Dispose();
            Assert.False(hook.IsValid);
            Assert.Empty(hook.Instantiations);
            Assert.Equal("orig a", Methods.Toggled("a"));
        }

        [Fact]
        public void ComposesWithClosedHooks()
        {
            var closed = Source(nameof(Methods.Composed)).MakeGenericMethod(typeof(string));
            using var first = new Hook(closed, Target(nameof(Closed)));
            using (var generic = new GenericHook(Source(nameof(Methods.Composed)), Target(nameof(Generic))))
            {
                Assert.Equal("generic closed orig a", Methods.Composed("a"));
                Assert.Equal("generic orig b", Methods.Composed<object>("b"));
                Assert.Equal(2, generic.Instantiations.Count());
            }
            Assert.Equal("closed orig a", Methods.Composed("a"));
            Assert.Equal("orig b", Methods.Composed<object>("b"));
        }

        [Fact]
        public void ConfigOrdersEachInstantiation()
        {
            var source = Source(nameof(Methods.Ordered));
            using var generic = new GenericHook(source, Target(nameof(Generic)), new DetourConfig("generic", priority: 10));
            using var high = new Hook(source.MakeGenericMethod(typeof(string)), Target(nameof(Closed)), new DetourConfig("high", priority: 20));
            using var low = new Hook(source.MakeGenericMethod(typeof(object)), Target(nameof(ClosedObject)), new DetourConfig("low", priority: 5));
            Assert.Equal("closed generic orig a", Methods.Ordered("a"));
            Assert.Equal("generic closed orig b", Methods.Ordered<object>("b"));
        }

        [Fact]
        public void RepeatedHooksOnOneSharedBody()
        {
            // compiling a detoured shared body again crashes .NET Core 3.x; the second round reuses the detour of the first
            var source = Source(nameof(Methods.Repeated));
            using var kept = new Hook(source.MakeGenericMethod(typeof(string)), Target(nameof(Closed)));
            for (var i = 0; i < 2; i++)
            {
                using (new Hook(source.MakeGenericMethod(typeof(object)), Target(nameof(ClosedObject))))
                using (new GenericHook(source, Target(nameof(Generic))))
                {
                    Assert.Equal("generic closed orig a", Methods.Repeated("a"));
                    Assert.Equal("generic closed orig b", Methods.Repeated<object>("b"));
                }
                Assert.Equal("closed orig a", Methods.Repeated("a"));
                Assert.Equal("orig b", Methods.Repeated<object>("b"));
            }
        }

        // compiled with inlining from the first call, even with tiered compilation
        [MethodImpl(MethodImplOptionsEx.AggressiveOptimization)]
        private static string CallsInlinable() => Methods.Inlinable("a");

        [MethodImpl(MethodImplOptionsEx.AggressiveOptimization)]
        private static string CallsInlinableString() => Methods.InlinableString("a");

        [Fact]
        public void HookedInstantiationsAreInlinedLikeOtherMethods()
        {
            // callers compiled after a small method was hooked still inline it on .NET Framework, .NET Core 2.1 and Mono
            string generic, plain;
            using (new Hook(Source(nameof(Methods.Inlinable)).MakeGenericMethod(typeof(string)), Target(nameof(Inlined))))
                generic = CallsInlinable();
            using (new Hook(Source(nameof(Methods.InlinableString)), Target(nameof(Inlined))))
                plain = CallsInlinableString();
            Assert.Equal(plain, generic);
        }

        [Fact]
        public void RejectsNonDefinitions()
        {
            Assert.Throws<ArgumentException>(() => new GenericHook(Source(nameof(Methods.Shared)).MakeGenericMethod(typeof(string)), Target(nameof(Generic))));
            Assert.Throws<ArgumentException>(() => new GenericHook(typeof(Box<string>).GetMethod(nameof(Box<int>.Inst))!, Target(nameof(BoxInst))));
            Assert.Throws<ArgumentException>(() => new GenericHook(Source(nameof(Methods.Shared)), Target(nameof(GenericMixed))));
            Assert.Throws<ArgumentException>(() => new GenericHook(Source(nameof(Methods.Shared)), Target(nameof(Closed))));
        }
    }
}
