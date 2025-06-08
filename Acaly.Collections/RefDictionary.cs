using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Acaly.Collections
{
    internal sealed class RefDictionary<TKey, TValue>
        where TKey : notnull
    {
        private const int _bufferSize = 8;

        private readonly List<TValue[]> _buffers = [new TValue[_bufferSize]];
        private readonly Dictionary<TKey, (int Buffer, int Index)> _dictionary = [];
        private int _currentBufferCount = 0;

        public ref TValue Find(TKey key)
        {
            if (!_dictionary.TryGetValue(key, out var pos))
            {
                return ref Unsafe.NullRef<TValue>();
            }
            return ref _buffers[pos.Buffer][pos.Index];
        }

        public ref TValue GetOrAdd(TKey key, TValue value, out bool exists)
        {
            ref var rpos = ref CollectionsMarshal.GetValueRefOrAddDefault(_dictionary, key, out exists);
            if (!exists)
            {
                var buffer = _buffers.Count - 1;
                var index = _currentBufferCount++;
                if (index >= _bufferSize)
                {
                    _buffers.Add(new TValue[_bufferSize]);
                    _currentBufferCount = 1;
                    buffer += 1;
                    index = 0;
                }
                rpos.Buffer = buffer;
                rpos.Index = index;
                ref TValue r = ref _buffers[buffer][index];
                r = value;
                return ref r;
            }
            else
            {
                return ref _buffers[rpos.Buffer][rpos.Index];
            }
        }
    }
}
