using System;
using System.Collections.Generic;
using UnityEngine;

namespace AceLand.Injection
{
    public class ObjectPool<T> : IObjectPool<T>
    {
        readonly Stack<T> _stack;
        readonly Func<T> _create;
        readonly Action<T> _onRent, _onReturn, _onDestroy;
        readonly int _maxSize;
        int _active;
        bool _disposed;

        public ObjectPool(Func<T> create, Action<T> onRent = null, Action<T> onReturn = null,
                          Action<T> onDestroy = null, int prewarm = 0, int maxSize = 0)
        {
            _create = create ?? throw new ArgumentNullException(nameof(create));
            _onRent = onRent; _onReturn = onReturn; _onDestroy = onDestroy;
            _maxSize = maxSize <= 0 ? int.MaxValue : maxSize;
            _stack = new Stack<T>(Mathf.Max(prewarm, 4));
            if (prewarm > 0) Prewarm(prewarm);
        }

        public int CountInactive => _stack.Count;
        public int CountActive => _active;

        public T Rent()
        {
            ThrowIfDisposed();
            var item = _stack.Count > 0 ? _stack.Pop() : _create();
            _active++;
            _onRent?.Invoke(item);
            return item;
        }

        public PooledObject<T> Rent(out T item)
        {
            item = Rent();
            return new PooledObject<T>(item, this);
        }

        public void Return(T item)
        {
            if (_disposed || item == null) return;
            _onReturn?.Invoke(item);
            _active = Mathf.Max(0, _active - 1);
            if (_stack.Count < _maxSize) _stack.Push(item);
            else _onDestroy?.Invoke(item);
        }

        public void Prewarm(int count)
        {
            ThrowIfDisposed();
            for (int i = 0; i < count; i++)
            {
                var item = _create();
                _onReturn?.Invoke(item);
                _stack.Push(item);
            }
        }

        public void Clear()
        {
            while (_stack.Count > 0) _onDestroy?.Invoke(_stack.Pop());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException($"ObjectPool<{typeof(T).Name}>");
        }
    }
}