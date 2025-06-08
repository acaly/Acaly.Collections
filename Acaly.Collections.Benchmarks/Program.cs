using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Acaly.Collections.Benchmarks
{
    internal class Program
    {
        static void Main()
        {
            BenchmarkRunner.Run<TryGetValueBenchmark>();
        }
    }

    [DisassemblyDiagnoser(printInstructionAddresses: true, syntax: DisassemblySyntax.Intel)]
    [WarmupCount(5)]
    public class TryGetValueBenchmark
    {
        private readonly ConcurrentLookupTable<int, int> _lookupTable;
        private readonly Dictionary<int, int> _dict;
        private readonly ConcurrentDictionary<int, int> _concurrentDict;
        private readonly int[] _keys;

        private const int KeyCount = 8;
        private const int KeyRead = 5;

        public TryGetValueBenchmark()
        {
            _keys = [.. Enumerable.Range(0, KeyCount)];

            _lookupTable = new ConcurrentLookupTable<int, int>(23, 17);
            foreach (var i in _keys)
                _lookupTable.GetOrAdd(i, i * 2);

            _dict = new Dictionary<int, int>(23);
            foreach (var i in _keys)
                _dict[i] = i * 2;

            _concurrentDict = new ConcurrentDictionary<int, int>(2, 23);
            foreach (var i in _keys)
                _concurrentDict[i] = i * 2;
        }

        [Benchmark]
        public int ConcurrentLookupTable()
        {
            _lookupTable.TryGetValue(KeyRead, out var v);
            return v;
        }

        [Benchmark]
        public int Dictionary()
        {
            var dict = _dict;
            dict.TryGetValue(KeyRead, out var v);
            return v;
        }

        [Benchmark]
        public int LockedDictionary()
        {
            var dict = _dict;
            lock (dict)
            {
                dict.TryGetValue(KeyRead, out var v);
                return v;
            }
        }

        [Benchmark]
        public int ConcurrentDictionary()
        {
            _concurrentDict.TryGetValue(KeyRead, out var v);
            return v;
        }

        [GlobalSetup(Target = nameof(ConcurrentLookupTable_MT))]
        public void SetupConcurrentLookupTable_MT()
        {
            new Thread(() =>
            {
                var dict = _lookupTable;
                while (true)
                {
                    dict.TryGetValue(KeyRead, out _);
                }
            })
            {
                IsBackground = true,
            }.Start();
        }

        [Benchmark]
        public int ConcurrentLookupTable_MT()
        {
            _lookupTable.TryGetValue(KeyRead, out var v);
            return v;
        }

        [GlobalSetup(Target = nameof(LockedDictionary_MT))]
        public void SetupLockedDictionary_MT()
        {
            new Thread(() =>
            {
                var dict = _dict;
                while (true)
                {
                    lock (dict)
                    {
                        dict.TryGetValue(KeyRead, out _);
                    }
                }
            })
            {
                IsBackground = true,
            }.Start();
        }

        [Benchmark]
        public int LockedDictionary_MT()
        {
            var dict = _dict;
            lock (dict)
            {
                dict.TryGetValue(KeyRead, out var v);
                return v;
            }
        }

        [GlobalSetup(Target = nameof(ConcurrentDictionary_MT))]
        public void SetupConcurrentDictionary_MT()
        {
            new Thread(() =>
            {
                var dict = _concurrentDict;
                while (true)
                {
                    dict.TryGetValue(KeyRead, out _);
                }
            })
            {
                IsBackground = true,
            }.Start();
        }

        [Benchmark]
        public int ConcurrentDictionary_MT()
        {
            _concurrentDict.TryGetValue(KeyRead, out var v);
            return v;
        }
    }
}
