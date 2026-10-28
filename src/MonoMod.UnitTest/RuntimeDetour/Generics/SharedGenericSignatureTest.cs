extern alias New;

using New::MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Abstractions;

namespace MonoMod.UnitTest.RuntimeDetour.Generics
{
    // dispatch has to leave arguments around this and return buffers, in float registers and on the stack in place
    [Collection("RuntimeDetour")]
    public class SharedGenericSignatureTest(ITestOutputHelper helper) : TestBase(helper)
    {
        internal struct Pair(long a, long b)
        {
            public long A = a, B = b;
        }

        // returned through a buffer
        internal struct Big(long a, long f)
        {
            public long A = a, B = 2, C = 3, D = 4, E = 5, F = f;
        }

        // two words with a reference: returned in registers on SysV, through a buffer on Windows
        internal struct Result
        {
            public object? Value;
            public bool Wrapped;
        }

        internal static class Statics<T>
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static Result Get(List<object> values) => new() { Value = values[0] };
        }

        internal sealed class Instance(long seed)
        {
            public long Seed = seed;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public Big Make<T>(T value) => new(Seed, value!.ToString()!.Length);
        }

        internal class Speaker
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public virtual string Speak<T>(T value) => "base " + value;
        }

        internal sealed class LoudSpeaker : Speaker
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public override string Speak<T>(T value) => "loud " + value;
        }

        internal static class Methods
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Mixed<T>(Pair p, int a, double b, T c, long d, float e, int f) => "orig " + p.A + p.B + a + b + c + d + e + f;

            // with the generic context, more integer arguments than SysV passes in registers
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string ManyInts<T>(int a, int b, int c, int d, int e, int f, int g, T value) => "orig " + a + b + c + d + e + f + g + value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Swap<T>(ref T a, out T b)
            {
                b = a;
                a = default!;
                return "orig " + b;
            }
        }

        internal delegate string SwapOrig(ref string a, out string b);

        internal static Result Get(Func<List<object>, Result> orig, List<object> values)
        {
            var result = orig(values);
            result.Wrapped = true;
            return result;
        }
        internal static Big Make(Func<Instance, string, Big> orig, Instance self, string value)
        {
            var big = orig(self, value);
            big.A += 1000;
            return big;
        }
        internal static string Speak(Func<Speaker, string, string> orig, Speaker self, string value) => "hooked " + orig(self, value);
        internal static string SpeakLoud(Func<LoudSpeaker, string, string> orig, LoudSpeaker self, string value) => "hooked " + orig(self, value);
        internal static string Swap(SwapOrig orig, ref string a, out string b)
        {
            var result = orig(ref a, out b);
            a = "x";
            return "hooked " + result;
        }
        // through GenericHook, so that the first call of each instantiation resolves its hook while its arguments wait
        internal static string Mixed<T>(Func<Pair, int, double, T, long, float, int, string> orig, Pair p, int a, double b, T c, long d, float e, int f)
            => "hooked " + orig(p, a, b, c, d, e, f);
        internal static string ManyInts<T>(Func<int, int, int, int, int, int, int, T, string> orig, int a, int b, int c, int d, int e, int f, int g, T value)
            => "hooked " + orig(a, b, c, d, e, f, g, value);

        private static MethodInfo Target(string name) => typeof(SharedGenericSignatureTest).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        private static MethodInfo OfString(Type type, string name) => type.GetMethod(name)!.MakeGenericMethod(typeof(string));

        [Fact]
        public void StaticStructReturn()
        {
            var values = new List<object> { new() };
            // twice, so that the second hook reuses the detour of the shared body
            for (var i = 0; i < 2; i++)
            {
                using (new Hook(typeof(Statics<string>).GetMethod(nameof(Statics<string>.Get))!, Target(nameof(Get))))
                {
                    var result = Statics<string>.Get(values);
                    Assert.Same(values[0], result.Value);
                    Assert.True(result.Wrapped);
                    Assert.False(Statics<object>.Get(values).Wrapped);
                }
                Assert.False(Statics<string>.Get(values).Wrapped);
            }
        }

        [Fact]
        public void InstanceStructReturn()
        {
            using (new Hook(OfString(typeof(Instance), nameof(Instance.Make)), Target(nameof(Make))))
            {
                var big = new Instance(7).Make("abc");
                Assert.Equal((1007, 2, 3, 4, 5, 3), (big.A, big.B, big.C, big.D, big.E, big.F));
                Assert.Equal(7, new Instance(7).Make<object>("abc").A);
            }
        }

        [Fact]
        public void StructIntegerAndFloatArguments()
        {
            using (new GenericHook(typeof(Methods).GetMethod(nameof(Methods.Mixed))!, Target(nameof(Mixed))))
            {
                Assert.Equal("hooked orig 8912a345", Methods.Mixed(new Pair(8, 9), 1, 2.0, "a", 3L, 4.0f, 5));
                Assert.Equal("hooked orig 1267b890", Methods.Mixed<object>(new Pair(1, 2), 6, 7.0, "b", 8L, 9.0f, 0));
            }
        }

        [Fact]
        public void ArgumentsOnTheStack()
        {
            using (new GenericHook(typeof(Methods).GetMethod(nameof(Methods.ManyInts))!, Target(nameof(ManyInts))))
            {
                Assert.Equal("hooked orig 1234567a", Methods.ManyInts(1, 2, 3, 4, 5, 6, 7, "a"));
                Assert.Equal("hooked orig 7654321b", Methods.ManyInts<object>(7, 6, 5, 4, 3, 2, 1, "b"));
            }
        }

        [Fact]
        public void ByRefArguments()
        {
            using (new Hook(OfString(typeof(Methods), nameof(Methods.Swap)), Target(nameof(Swap))))
            {
                var a = "a";
                Assert.Equal("hooked orig a", Methods.Swap(ref a, out var b));
                Assert.Equal(("x", "a"), (a, b));

                object? c = "c";
                Assert.Equal("orig c", Methods.Swap(ref c, out var d));
                Assert.Equal((null, "c"), (c, d));
            }
        }

        [Fact]
        public void GenericVirtualMethods()
        {
            using (new Hook(OfString(typeof(Speaker), nameof(Speaker.Speak)), Target(nameof(Speak))))
            {
                Assert.Equal("hooked base a", new Speaker().Speak("a"));
                Assert.Equal("base b", new Speaker().Speak<object>("b"));
                // the override is a method of its own
                Assert.Equal("loud c", ((Speaker)new LoudSpeaker()).Speak("c"));
                using (new Hook(OfString(typeof(LoudSpeaker), nameof(LoudSpeaker.Speak)), Target(nameof(SpeakLoud))))
                {
                    Assert.Equal("hooked loud c", ((Speaker)new LoudSpeaker()).Speak("c"));
                    Assert.Equal("hooked base a", new Speaker().Speak("a"));
                }
            }
        }
    }
}
