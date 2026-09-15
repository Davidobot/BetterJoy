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

        // Returns an enumerator over a snapshot copy taken while holding the lock, not the live
        // list - the lock is released as soon as this method returns, so a caller's foreach
        // iterates entirely outside it. Enumerating the live list left every caller vulnerable
        // to InvalidOperationException ("Collection was modified") - or worse, an inconsistent
        // partial view - the moment another thread added/removed anything mid-iteration, which
        // defeated the point of this class being called "concurrent" at all.
        public IEnumerator<T> GetEnumerator() {
            return LockInternalListAndQuery(l => new List<T>(l)).GetEnumerator();
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
            return GetEnumerator();
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
