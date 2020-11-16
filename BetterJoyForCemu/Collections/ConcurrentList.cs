using System;
using System.Collections;
using System.Collections.Generic;

namespace BetterJoyForCemu.Collections {

    // https://codereview.stackexchange.com/a/125341
    public class ConcurrentList<T> : IList<T> {
        #region Fields

        private IList<T> _internalList;

        private readonly object lockObject = new object();

        #endregion

        #region ctor

        public ConcurrentList() {
            _internalList = new List<T>();
        }

        public ConcurrentList(int capacity) {
            _internalList = new List<T>(capacity);
        }

        public ConcurrentList(IEnumerable<T> list) {
            _internalList = new List<T>();
            foreach (T item in list) {
                _internalList.Add(item);
            }
        }

        #endregion

        public T this[int index] {
            get {
                return LockInternalListAndGet(l => l[index]);
            }

            set {
                LockInternalListAndCommand(l => l[index] = value);
            }
        }

        public int Count {
            get {
                return LockInternalListAndQuery(l => l.Count);
            }
        }

        public bool IsReadOnly => false;

        public void Add(T item) {
            LockInternalListAndCommand(l => l.Add(item));
        }

        public void Clear() {
            LockInternalListAndCommand(l => l.Clear());
        }

        public bool Contains(T item) {
            return LockInternalListAndQuery(l => l.Contains(item));
        }

        public void CopyTo(T[] array, int arrayIndex) {
            LockInternalListAndCommand(l => l.CopyTo(array, arrayIndex));
        }

        public IEnumerator<T> GetEnumerator() {
            return LockInternalListAndQuery(l => l.GetEnumerator());
        }

        public int IndexOf(T item) {
            return LockInternalListAndQuery(l => l.IndexOf(item));
        }

        public void Insert(int index, T item) {
            LockInternalListAndCommand(l => l.Insert(index, item));
        }

        public bool Remove(T item) {
            return LockInternalListAndQuery(l => l.Remove(item));
        }

        public void RemoveAt(int index) {
            LockInternalListAndCommand(l => l.RemoveAt(index));
        }

        IEnumerator IEnumerable.GetEnumerator() {
            return LockInternalListAndQuery(l => l.GetEnumerator());
        }

        #region Utilities

        protected virtual void LockInternalListAndCommand(Action<IList<T>> action) {
            lock (lockObject) {
                action(_internalList);
            }
        }

        protected virtual T LockInternalListAndGet(Func<IList<T>, T> func) {
            lock (lockObject) {
                return func(_internalList);
            }
        }

        protected virtual TObject LockInternalListAndQuery<TObject>(Func<IList<T>, TObject> query) {
            lock (lockObject) {
                return query(_internalList);
            }
        }

        #endregion
    }
}
