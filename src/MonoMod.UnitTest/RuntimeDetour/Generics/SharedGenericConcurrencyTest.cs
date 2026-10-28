extern alias New;

using New::MonoMod.RuntimeDetour;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace MonoMod.UnitTest.RuntimeDetour.Generics
{
    [Collection("RuntimeDetour")]
    public class SharedGenericConcurrencyTest(ITestOutputHelper helper) : TestBase(helper)
    {
        internal static class Methods
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Churned<T>(T value) => "orig " + value;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static string Discovered<T>(T value) => "orig " + value;
        }

        // distinct instantiations of the shared body, each resolved on its first call
        internal sealed class Tag<T>
        {
            public override string ToString() => typeof(T).Name;
        }

        internal static string Hooked<T>(Func<T, string> orig, T value) => "hooked " + orig(value);
        internal static string HookedString(Func<string, string> orig, string value) => "hooked " + orig(value);
        internal static string HookedObject(Func<object, string> orig, object value) => "hooked " + orig(value);

        private static MethodInfo Target(string name) => typeof(SharedGenericConcurrencyTest).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

        private static void Run(int threads, Action<int> body)
        {
            Exception? failure = null;
            using var start = new Barrier(threads);
            var workers = new Thread[threads];
            for (var t = 0; t < threads; t++)
            {
                var id = t;
                workers[t] = new Thread(() =>
                {
                    try
                    {
                        start.SignalAndWait();
                        body(id);
                    }
                    catch (Exception e)
                    {
                        Interlocked.CompareExchange(ref failure, e, null);
                    }
                })
                { IsBackground = true };
                workers[t].Start();
            }
            foreach (var worker in workers)
                worker.Join();
            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [Fact]
        public void CallsWhileHooksChange()
        {
            var source = typeof(Methods).GetMethod(nameof(Methods.Churned))!;
            using var kept = new Hook(source.MakeGenericMethod(typeof(string)), Target(nameof(HookedString)));
            var churning = true;
            Run(5, id =>
            {
                if (id == 0)
                {
                    try
                    {
                        // hooks on both instantiations of the same body come and go while the callers run
                        for (var i = 0; i < 20; i++)
                        {
                            using (new Hook(source.MakeGenericMethod(typeof(object)), Target(nameof(HookedObject))))
                            using (new Hook(source.MakeGenericMethod(typeof(string)), Target(nameof(HookedString))))
                                Assert.Equal("hooked orig c", Methods.Churned<object>("c"));
                        }
                    }
                    finally
                    {
                        Volatile.Write(ref churning, false);
                    }
                    return;
                }
                do
                {
                    Assert.EndsWith("hooked orig a", Methods.Churned("a"), StringComparison.Ordinal);
                    Assert.EndsWith("orig b", Methods.Churned<object>("b"), StringComparison.Ordinal);
                }
                while (Volatile.Read(ref churning));
            });
            Assert.Equal("orig b", Methods.Churned<object>("b"));
        }

        [Fact]
        public void InstantiationsResolvedConcurrently()
        {
            using var hook = new GenericHook(typeof(Methods).GetMethod(nameof(Methods.Discovered))!, Target(nameof(Hooked)));
            Run(8, id =>
            {
                for (var i = 0; i < 6; i++)
                {
                    var (expected, actual) = ((id + i) % 6) switch
                    {
                        0 => ("Byte", Methods.Discovered(new Tag<byte>())),
                        1 => ("Int16", Methods.Discovered(new Tag<short>())),
                        2 => ("Int32", Methods.Discovered(new Tag<int>())),
                        3 => ("Int64", Methods.Discovered(new Tag<long>())),
                        4 => ("Single", Methods.Discovered(new Tag<float>())),
                        _ => ("Double", Methods.Discovered(new Tag<double>())),
                    };
                    Assert.Equal("hooked orig " + expected, actual);
                }
            });
        }
    }
}
