extern alias New;

using MonoMod.Cil;
using New::MonoMod.RuntimeDetour;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Abstractions;

namespace MonoMod.UnitTest.RuntimeDetour.Generics
{
    [Collection("RuntimeDetour")]
    public class ClosedGenericHookTest(ITestOutputHelper helper) : TestBase(helper)
    {
        internal static class Methods
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string M<T>(T value) => "orig " + value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string M2<T1, T2>(T1 a, T2 b) => "orig " + a + b;
        }

        internal class Box<T>(T value)
        {
            public T Value = value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public string Inst(int x) => "orig " + Value + x;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Static(T value) => "orig " + value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public string Plain() => "orig";
        }

        internal class DerivedBox(string value) : Box<string>(value);

        internal struct ValueBox<T>(T value)
        {
            public T Value = value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public readonly string Inst(int x) => "orig " + Value + x;
        }

        private static MethodInfo M(Type t) => typeof(Methods).GetMethod(nameof(Methods.M))!.MakeGenericMethod(t);
        private static MethodInfo Hooks(string name) => typeof(ClosedGenericHookTest).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

        internal static string MString(Func<string, string> orig, string value) => "hooked " + orig(value);
        internal static string MString2(Func<string, string> orig, string value) => "second " + orig(value);
        internal static string MObject(Func<object, string> orig, object value) => "object " + orig(value);
        internal static string MInt(Func<int, string> orig, int value) => "int " + orig(value);
        internal static string M2IntString(Func<int, string, string> orig, int a, string b) => "hooked " + orig(a, b);
        internal static string BoxInst(Func<Box<string>, int, string> orig, Box<string> self, int x) => "hooked " + orig(self, x);
        internal static string BoxStatic(Func<string, string> orig, string value) => "hooked " + orig(value);
        internal static string BoxPlain(Func<Box<string>, string> orig, Box<string> self) => "hooked " + orig(self);
        internal static string ValueBoxInst(ValueBoxInstOrig orig, ref ValueBox<string> self, int x) => "hooked " + orig(ref self, x);
        internal delegate string ValueBoxInstOrig(ref ValueBox<string> self, int x);

        [Fact]
        public void HookOnlyAffectsItsInstantiation()
        {
            Assert.Equal("orig a", Methods.M("a"));
            using (new Hook(M(typeof(string)), Hooks(nameof(MString))))
            {
                Assert.Equal("hooked orig a", Methods.M("a"));
                Assert.Equal("orig b", Methods.M<object>("b"));
                Assert.Equal("orig 1", Methods.M(1));

                using (new Hook(M(typeof(object)), Hooks(nameof(MObject))))
                using (new Hook(M(typeof(int)), Hooks(nameof(MInt))))
                {
                    Assert.Equal("hooked orig a", Methods.M("a"));
                    Assert.Equal("object orig b", Methods.M<object>("b"));
                    Assert.Equal("int orig 1", Methods.M(1));
                }
                Assert.Equal("orig b", Methods.M<object>("b"));
            }
            Assert.Equal("orig a", Methods.M("a"));
        }

        [Fact]
        public void PartiallySharedInstantiation()
        {
            // M2<int, __Canon> on CoreCLR and Framework
            var method = typeof(Methods).GetMethod(nameof(Methods.M2))!.MakeGenericMethod(typeof(int), typeof(string));
            using (new Hook(method, Hooks(nameof(M2IntString))))
            {
                Assert.Equal("hooked orig 1a", Methods.M2(1, "a"));
                Assert.Equal("orig 1b", Methods.M2<int, object>(1, "b"));
            }
            Assert.Equal("orig 1a", Methods.M2(1, "a"));
        }

        [Fact]
        public void HooksChainAndCompose()
        {
            var method = M(typeof(string));
            using var first = new Hook(method, Hooks(nameof(MString)));
            using var second = new Hook(method, Hooks(nameof(MString2)));
            using var il = new ILHook(method, ctx =>
            {
                var c = new ILCursor(ctx);
                Assert.True(c.TryGotoNext(i => i.MatchLdstr("orig ")));
                c.Next!.Operand = "il ";
            });
            Assert.Equal("second hooked il a", Methods.M("a"));
            Assert.Equal("orig b", Methods.M<object>("b"));

            first.Undo();
            Assert.Equal("second il a", Methods.M("a"));
            il.Undo();
            Assert.Equal("second orig a", Methods.M("a"));
            second.Undo();
            Assert.Equal("orig a", Methods.M("a"));
        }

        [Fact]
        public void MethodsOnGenericTypes()
        {
            using (new Hook(typeof(Box<string>).GetMethod(nameof(Box<string>.Inst))!, Hooks(nameof(BoxInst))))
            using (new Hook(typeof(Box<string>).GetMethod(nameof(Box<string>.Static))!, Hooks(nameof(BoxStatic))))
            using (new Hook(typeof(ValueBox<string>).GetMethod(nameof(ValueBox<string>.Inst))!, Hooks(nameof(ValueBoxInst))))
            {
                Assert.Equal("hooked orig a1", new Box<string>("a").Inst(1));
                Assert.Equal("hooked orig b2", new DerivedBox("b").Inst(2));
                Assert.Equal("orig c3", new Box<object>("c").Inst(3));
                Assert.Equal("hooked orig a", Box<string>.Static("a"));
                Assert.Equal("orig b", Box<object>.Static("b"));
                Assert.Equal("hooked orig a1", new ValueBox<string>("a").Inst(1));
                Assert.Equal("orig b2", new ValueBox<object>("b").Inst(2));
            }
            Assert.Equal("orig a1", new Box<string>("a").Inst(1));
            Assert.Equal("orig a", Box<string>.Static("a"));
        }

        [Fact]
        public void NullThisRunsTheOriginal()
        {
            // an open instance delegate calls a non-virtual method without checking this
            var call = (Func<Box<object>?, string>)Delegate.CreateDelegate(typeof(Func<Box<object>?, string>), typeof(Box<object>).GetMethod(nameof(Box<object>.Plain))!);
            using (new Hook(typeof(Box<string>).GetMethod(nameof(Box<string>.Plain))!, Hooks(nameof(BoxPlain))))
            {
                Assert.Equal("hooked orig", new Box<string>("a").Plain());
                Assert.Equal("orig", call(null));
            }
        }

        [Fact]
        public void OpenDefinitionsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new Hook(typeof(Methods).GetMethod(nameof(Methods.M))!, Hooks(nameof(MString))));
            Assert.Throws<ArgumentException>(() => new Hook(typeof(Box<>).GetMethod(nameof(Box<string>.Static))!, Hooks(nameof(BoxStatic))));
        }
    }
}
