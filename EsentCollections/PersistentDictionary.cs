//-----------------------------------------------------------------------
// <copyright file="PersistentDictionary.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation.
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Isam.Esent.Interop;

namespace Microsoft.Isam.Esent.Collections.Generic
{
    /// <summary>
    /// Represents a collection of persistent keys and values.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    public sealed partial class PersistentDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IDisposable
        where TKey : IComparable<TKey>
    {
        /// <summary>
        /// The ESENT instance this dictionary uses. An Instance object inherits
        /// from SafeHandle so this instance will be (eventually) terminated even
        /// if the dictionary isn't disposed. 
        /// </summary>
        private readonly Instance instance;

        /// <summary>
        /// This object should be locked when the Dictionary is being updated. 
        /// Read operations can proceed without any locks (the cursor cache has
        /// its own lock to control access to the cursors).
        /// </summary>
        private readonly object updateLock;

        /// <summary>
        /// Methods to set and retrieve data in ESE.
        /// </summary>
        private readonly PersistentDictionaryConverters<TKey, TValue> converters;

        /// <summary>
        /// Meta-data information for the dictionary database.
        /// </summary>
        private readonly PersistentDictionaryConfig config;

        /// <summary>
        /// Cache of cursors used to access the dictionary.
        /// </summary>
        private readonly PersistentDictionaryCursorCache<TKey, TValue> cursors;

        /// <summary>
        /// Path to the database.
        /// </summary>
        private readonly string databasePath;

        /// <summary>
        /// Initializes a new instance of the PersistentDictionary class.
        /// </summary>
        /// <param name="directory">
        /// The directory to create the database in.
        /// </param>
        public PersistentDictionary(string directory)
        {
            if (null == directory)
            {
                throw new ArgumentNullException("directory");
            }

            Globals.Init();

            this.updateLock = new object();
            this.converters = new PersistentDictionaryConverters<TKey, TValue>();
            this.config = new PersistentDictionaryConfig();

            this.instance = new Instance(Guid.NewGuid().ToString());            
            this.instance.Parameters.SystemDirectory = directory;
            this.instance.Parameters.LogFileDirectory = directory;
            this.instance.Parameters.TempDirectory = directory;

            // If the database has been moved while inconsistent recovery
            // won't be able to find the database (logfiles contain the
            // absolute path of the referenced database). Set this parameter
            // to indicate a directory which contains any databases that couldn't
            // be found by recovery.
            this.instance.Parameters.AlternateDatabaseRecoveryDirectory = directory;

            this.instance.Parameters.CreatePathIfNotExist = true;
            this.instance.Parameters.BaseName = this.config.BaseName;
            this.instance.Parameters.EnableIndexChecking = false;       // TODO: fix unicode indexes
            this.instance.Parameters.CircularLog = true;
            this.instance.Parameters.LogFileSize = 256;    // 256KB logs
            this.instance.Parameters.LogBuffers = 256;     // buffers = 1/2 of logfile
            this.instance.Parameters.PageTempDBMin = 0;
            this.instance.Init();

            this.databasePath = Path.Combine(directory, this.config.Database);
            if (!File.Exists(this.databasePath))
            {
                this.CreateDatabase(this.databasePath);
            }

            this.cursors = new PersistentDictionaryCursorCache<TKey, TValue>(
                this.instance, this.databasePath, this.converters, this.config);
        }

        /// <summary>
        /// Gets the number of elements contained in the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <returns>
        /// The number of elements contained in the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </returns>
        public int Count
        {
            get
            {
                return this.UsingCursor(cursor => cursor.RetrieveCount());
            }
        }

        /// <summary>
        /// Gets a value indicating whether the <see cref="PersistentDictionary{TKey,TValue}"/> is read-only.
        /// </summary>
        /// <returns>
        /// True if the <see cref="PersistentDictionary{TKey,TValue}"/> is read-only; otherwise, false.
        /// </returns>
        public bool IsReadOnly
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Gets an <see cref="ICollection"/> containing the keys of the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <returns>
        /// An <see cref="PersistentDictionaryKeyCollection{TKey,TValue}"/> containing the keys of the object that implements <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </returns>
        public ICollection<TKey> Keys
        {
            get
            {
                return new PersistentDictionaryKeyCollection<TKey, TValue>(this);
            }
        }

        /// <summary>
        /// Gets an <see cref="ICollection"/> containing the values in the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <returns>
        /// An <see cref="PersistentDictionary{TKey,TValue}"/> containing the values in the object that implements <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </returns>
        public ICollection<TValue> Values
        {
            get
            {
                return new PersistentDictionaryValueCollection<TKey, TValue>(this);
            }
        }

        /// <summary>
        /// Gets or sets the element with the specified key.
        /// </summary>
        /// <returns>
        /// The element with the specified key.
        /// </returns>
        /// <param name="key">The key of the element to get or set.</param>
        /// <exception cref="T:System.Collections.Generic.KeyNotFoundException">
        /// The property is retrieved and <paramref name="key"/> is not found.
        /// </exception>
        public TValue this[TKey key]
        {
            get
            {
                return this.UsingCursor(
                    cursor =>
                    {
                        using (var transaction = cursor.BeginTransaction())
                        {
                            cursor.SeekWithKeyNotFoundException(key);
                            var value = cursor.RetrieveCurrentValue();
                            transaction.Commit(CommitTransactionGrbit.LazyFlush);
                            return value;
                        }
                    });
            }

            set
            {
                lock (this.updateLock)
                {
                    this.UsingCursor(
                        cursor =>
                        {
                            using (var transaction = cursor.BeginTransaction())
                            {
                                if (cursor.TrySeek(key))
                                {
                                    cursor.ReplaceCurrentValue(value);
                                }
                                else
                                {
                                    cursor.Insert(new KeyValuePair<TKey, TValue>(key, value));
                                }

                                transaction.Commit(CommitTransactionGrbit.LazyFlush);
                            }
                        });
                }
            }
        }

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Collections.Generic.IEnumerator`1"/> 
        /// that can be used to iterate through the collection.
        /// </returns>
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return this.GetGenericEnumerator(c => c.RetrieveCurrent(), new KeyRange<TKey>(null, null));
        }

        /// <summary>
        /// Returns an enumerator that iterates through a collection.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Collections.IEnumerator"/>
        /// object that can be used to iterate through the collection.
        /// </returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        /// <summary>
        /// Removes the first occurrence of a specific object from the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <returns>
        /// True if <paramref name="item"/> was successfully removed from the <see cref="PersistentDictionary{TKey,TValue}"/>;
        /// otherwise, false. This method also returns false if <paramref name="item"/> is not found in the original
        /// <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </returns>
        /// <param name="item">The object to remove from the <see cref="PersistentDictionary{TKey,TValue}"/>.</param>
        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            lock (this.updateLock)
            {
                return this.UsingCursor(
                    cursor =>
                    {
                        // Having the update lock means the record can't be
                        // deleted after we seek to it.
                        if (cursor.TrySeek(item.Key)
                            && cursor.RetrieveCurrentValue().Equals(item.Value))
                        {
                            using (var transaction = cursor.BeginTransaction())
                            {
                                cursor.DeleteCurrent();
                                transaction.Commit(CommitTransactionGrbit.LazyFlush);
                                return true;
                            }
                        }

                        return false;
                    });
            }
        }

        /// <summary>
        /// Adds an item to the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <param name="item">The object to add to the <see cref="PersistentDictionary{TKey,TValue}"/>.</param>
        public void Add(KeyValuePair<TKey, TValue> item)
        {
            lock (this.updateLock)
            {
                this.UsingCursor(
                    cursor =>
                    {
                        using (var transaction = cursor.BeginTransaction())
                        {
                            if (cursor.TrySeek(item.Key))
                            {
                                throw new ArgumentException("An item with this key already exists", "key");
                            }

                            cursor.Insert(item);
                            transaction.Commit(CommitTransactionGrbit.LazyFlush);
                        }
                    });
            }
        }

        /// <summary>
        /// Determines whether the <see cref="PersistentDictionary{TKey,TValue}"/> contains a specific value.
        /// </summary>
        /// <returns>
        /// True if <paramref name="item"/> is found in the
        /// <see cref="PersistentDictionary{TKey,TValue}"/>;
        /// otherwise, false.
        /// </returns>
        /// <param name="item">
        /// The object to locate in the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </param>
        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return this.UsingCursor(
                cursor =>
                {
                    // Start a transaction here to avoid the case where the record
                    // is deleted after we seek to it.
                    using (var transaction = cursor.BeginTransaction())
                    {
                        bool isPresent = cursor.TrySeek(item.Key) && cursor.RetrieveCurrentValue().Equals(item.Value);
                        transaction.Commit(CommitTransactionGrbit.LazyFlush);
                        return isPresent;
                    }
                });
        }

        /// <summary>
        /// Copies the elements of the <see cref="PersistentDictionary{TKey,TValue}"/> to an <see cref="T:System.Array"/>, starting at a particular <see cref="T:System.Array"/> index.
        /// </summary>
        /// <param name="array">
        /// The one-dimensional <see cref="T:System.Array"/> that is the destination
        /// of the elements copied from <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// The <see cref="T:System.Array"/> must have zero-based indexing.</param>
        /// <param name="arrayIndex">
        /// The zero-based index in <paramref name="array"/> at which copying begins.
        /// </param>
        /// <exception cref="T:System.ArgumentNullException"><paramref name="array"/> is null.</exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="arrayIndex"/> is less than 0.</exception>
        /// <exception cref="T:System.ArgumentException">
        /// <paramref name="arrayIndex"/> is equal to or greater than the length of <paramref name="array"/>.
        /// -or-The number of elements in the source <see cref="PersistentDictionary{TKey,TValue}"/> is greater
        /// than the available space from <paramref name="arrayIndex"/> to the end of the destination <paramref name="array"/>.
        /// </exception>
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Removes all items from the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        public void Clear()
        {
            lock (this.updateLock)
            {
                this.UsingCursor(
                    cursor =>
                    {
                        cursor.MoveBeforeFirst();
                        while (cursor.TryMoveNext())
                        {
                            using (var transaction = cursor.BeginTransaction())
                            {
                                cursor.DeleteCurrent();
                                transaction.Commit(CommitTransactionGrbit.LazyFlush);
                            }
                        }
                    });
            }
        }

        /// <summary>
        /// Determines whether the <see cref="PersistentDictionary{TKey,TValue}"/> contains an element with the specified key.
        /// </summary>
        /// <returns>
        /// True if the <see cref="PersistentDictionary{TKey,TValue}"/> contains an element with the key; otherwise, false.
        /// </returns>
        /// <param name="key">The key to locate in the <see cref="PersistentDictionary{TKey,TValue}"/>.</param>
        public bool ContainsKey(TKey key)
        {
            return this.UsingCursor(cursor => cursor.TrySeek(key));
        }

        /// <summary>
        /// Determines whether the <see cref="PersistentDictionary{TKey,TValue}"/> contains an element with the specified value.
        /// </summary>
        /// <remarks>
        /// This method requires a complete enumeration of all items in the dictionary so it can be much slower than
        /// <see cref="ContainsKey"/>.
        /// </remarks>
        /// <returns>
        /// True if the <see cref="PersistentDictionary{TKey,TValue}"/> contains an element with the value; otherwise, false.
        /// </returns>
        /// <param name="value">The value to locate in the <see cref="PersistentDictionary{TKey,TValue}"/>.</param>
        public bool ContainsValue(TValue value)
        {
            return this.Values.Contains(value);
        }

        /// <summary>
        /// Adds an element with the provided key and value to the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <param name="key">The object to use as the key of the element to add.</param>
        /// <param name="value">The object to use as the value of the element to add.</param>
        /// <exception cref="T:System.ArgumentException">An element with the same key already exists in the <see cref="PersistentDictionary{TKey,TValue}"/>.</exception>
        public void Add(TKey key, TValue value)
        {
            this.Add(new KeyValuePair<TKey, TValue>(key, value));
        }

        /// <summary>
        /// Removes the element with the specified key from the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <returns>
        /// True if the element is successfully removed; otherwise, false. This method also returns false if
        /// <paramref name="key"/> was not found in the original <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </returns>
        /// <param name="key">The key of the element to remove.</param>
        public bool Remove(TKey key)
        {
            lock (this.updateLock)
            {
                return this.UsingCursor(
                    cursor =>
                    {
                        if (cursor.TrySeek(key))
                        {
                            using (var transaction = cursor.BeginTransaction())
                            {
                                cursor.DeleteCurrent();
                                transaction.Commit(CommitTransactionGrbit.LazyFlush);
                                return true;
                            }
                        }

                        return false;
                    });
            }
        }

        /// <summary>
        /// Gets the value associated with the specified key.
        /// </summary>
        /// <returns>
        /// True if the object that implements <see cref="PersistentDictionary{TKey,TValue}"/>
        /// contains an element with the specified key; otherwise, false.
        /// </returns>
        /// <param name="key">
        /// The key whose value to get.</param>
        /// <param name="value">When this method returns, the value associated
        /// with the specified key, if the key is found; otherwise, the default
        /// value for the type of the <paramref name="value"/> parameter. This
        /// parameter is passed uninitialized.
        /// </param>
        public bool TryGetValue(TKey key, out TValue value)
        {
            TValue retrievedValue = default(TValue);
            bool found = this.UsingCursor(
                cursor =>
                {
                    // Start a transaction so the record can't be deleted after
                    // we seek to it.
                    bool isPresent = false;
                    using (var transaction = cursor.BeginTransaction())
                    {
                        if (cursor.TrySeek(key))
                        {
                            retrievedValue = cursor.RetrieveCurrentValue();
                            isPresent = true;
                        }

                        transaction.Commit(CommitTransactionGrbit.LazyFlush);
                    }

                    return isPresent;
                });
            value = retrievedValue;
            return found;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            this.cursors.Dispose();
            this.instance.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Force all changes made to this dictionary to be written to disk.
        /// </summary>
        public void Flush()
        {
            lock (this.updateLock)
            {
                this.UsingCursor(c => c.Flush());
            }
        }

        /// <summary>
        /// Returns an enumerator that iterates through the keys.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Collections.Generic.IEnumerator`1"/> that can be used to iterate through the keys.
        /// </returns>
        internal IEnumerator<TKey> GetKeyEnumerator()
        {
            return this.GetGenericEnumerator(c => c.RetrieveCurrentKey(), new KeyRange<TKey>(null, null));
        }

        /// <summary>
        /// Returns an enumerator that iterates through the keys.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Collections.Generic.IEnumerator`1"/> that can be used to iterate through the values.
        /// </returns>
        internal IEnumerator<TValue> GetValueEnumerator()
        {
            return this.GetGenericEnumerator(c => c.RetrieveCurrentValue(), new KeyRange<TKey>(null, null));
        }

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <typeparam name="T">The type returned by the iterator.</typeparam>
        /// <param name="getter">A function that generates a value from a cursor.</param>
        /// <param name="range">The range of keys to iterate.</param>
        /// <returns>
        /// A <see cref="T:System.Collections.Generic.IEnumerator`1"/> that can be used to iterate through the collection.
        /// </returns>
        internal IEnumerator<T> GetGenericEnumerator<T>(Func<PersistentDictionaryCursor<TKey, TValue>, T> getter, KeyRange<TKey> range)
        {
            // This is a long-running operation so we create a new cursor
            var iterator = this.cursors.GetCursor();
            try
            {
                // On the first iteration we want to set up an index range and on subsequent
                // iterations we will move through the index range. We use this variable to
                // keep track of which action is needed.
                bool firstIteration = true;
                while (true)
                {
                    // Use a transaction to move to the next record and retrieve its data.
                    // Even if the record has been deleted we will be able to move to the 
                    // next record.
                    using (var transaction = iterator.BeginTransaction())
                    {
                        if (firstIteration)
                        {
                            if (!iterator.SetIndexRange(range))
                            {
                                yield break;
                            }
                        }
                        else if (!iterator.TryMoveNext())
                        {
                            yield break;
                        }

                        firstIteration = false;

                        T item = getter(iterator);

                        // Commit the transaction before returning (so the external user doesn't keep a 
                        transaction.Commit(CommitTransactionGrbit.LazyFlush);
                        yield return item;
                    }
                }
            }
            finally
            {
                this.cursors.FreeCursor(iterator);
            }
        }

        #region Database Creation

        /// <summary>
        /// Create the database.
        /// </summary>
        /// <param name="database">The name of the database to create.</param>
        private void CreateDatabase(string database)
        {
            using (var session = new Session(this.instance))
            {
                JET_DBID dbid;
                Api.JetCreateDatabase(session, database, String.Empty, out dbid, CreateDatabaseGrbit.None);
                try
                {
                    using (var transaction = new Transaction(session))
                    {
                        this.CreateGlobalsTable(session, dbid);
                        this.CreateDataTable(session, dbid);
                        transaction.Commit(CommitTransactionGrbit.None);
                        Api.JetCloseDatabase(session, dbid, CloseDatabaseGrbit.None);
                        Api.JetDetachDatabase(session, database);
                    }
                }
                catch (Exception)
                {
                    // Delete the partially constructed database
                    Api.JetCloseDatabase(session, dbid, CloseDatabaseGrbit.None);
                    Api.JetDetachDatabase(session, database);
                    File.Delete(database);
                    throw;
                }
            }
        }

        /// <summary>
        /// Create the globals table.
        /// </summary>
        /// <param name="session">The session to use.</param>
        /// <param name="dbid">The database to create the table in.</param>
        private void CreateGlobalsTable(Session session, JET_DBID dbid)
        {
            JET_TABLEID tableid;
            JET_COLUMNID versionColumnid;
            JET_COLUMNID countColumnid;

            Api.JetCreateTable(session, dbid, this.config.GlobalsTableName, 1, 100, out tableid);
            Api.JetAddColumn(
                session,
                tableid,
                this.config.VersionColumnName,
                new JET_COLUMNDEF { coltyp = JET_coltyp.LongText },
                null,
                0,
                out versionColumnid);

            byte[] defaultValue = BitConverter.GetBytes(0);

            Api.JetAddColumn(
                session,
                tableid,
                this.config.CountColumnName,
                new JET_COLUMNDEF { coltyp = JET_coltyp.Long, grbit = ColumndefGrbit.ColumnEscrowUpdate },
                defaultValue,
                defaultValue.Length,
                out countColumnid);

            Api.JetAddColumn(
                session,
                tableid,
                this.config.FlushColumnName,
                new JET_COLUMNDEF { coltyp = JET_coltyp.Long, grbit = ColumndefGrbit.ColumnEscrowUpdate },
                defaultValue,
                defaultValue.Length,
                out countColumnid);

            using (var update = new Update(session, tableid, JET_prep.Insert))
            {
                Api.SetColumn(session, tableid, versionColumnid, "PersistentDictionary V1", Encoding.Unicode);
                update.Save();
            }

            Api.JetCloseTable(session, tableid);
        }

        /// <summary>
        /// Create the data table.
        /// </summary>
        /// <param name="session">The session to use.</param>
        /// <param name="dbid">The database to create the table in.</param>
        private void CreateDataTable(Session session, JET_DBID dbid)
        {
            JET_TABLEID tableid;
            JET_COLUMNID keyColumnid;
            JET_COLUMNID valueColumnid;

            Api.JetCreateTable(session, dbid, this.config.DataTableName, 1, 100, out tableid);
            Api.JetAddColumn(
                session,
                tableid,
                this.config.KeyColumnName,
                new JET_COLUMNDEF { coltyp = this.converters.KeyColtyp, cp = JET_CP.Unicode },
                null,
                0,
                out keyColumnid);

            Api.JetAddColumn(
                session,
                tableid,
                this.config.ValueColumnName,
                new JET_COLUMNDEF { coltyp = this.converters.ValueColtyp, cp = JET_CP.Unicode },
                null,
                0,
                out valueColumnid);

            string indexKey = String.Format(CultureInfo.InvariantCulture, "+{0}\0\0", this.config.KeyColumnName);
            var indexcreates = new[]
            {
                new JET_INDEXCREATE
                {
                    cbKeyMost = SystemParameters.KeyMost,
                    grbit = CreateIndexGrbit.IndexPrimary,
                    szIndexName = "primary",
                    szKey = indexKey,
                    cbKey = indexKey.Length,
                    pidxUnicode = new JET_UNICODEINDEX
                    {
                        lcid = CultureInfo.CurrentCulture.LCID,
                        dwMapFlags = Conversions.LCMapFlagsFromCompareOptions(CompareOptions.None),
                    },
                },
            };
            Api.JetCreateIndex2(session, tableid, indexcreates, indexcreates.Length);

            Api.JetCloseTable(session, tableid);
        }

        #endregion

        /// <summary>
        /// Get a cursor, perform the specified action and release the cursor.
        /// </summary>
        /// <param name="action">The action to perform.</param>
        private void UsingCursor(Action<PersistentDictionaryCursor<TKey, TValue>> action)
        {
            PersistentDictionaryCursor<TKey, TValue> cursor = this.cursors.GetCursor();
            try
            {
                action(cursor);
            }
            finally
            {
                this.cursors.FreeCursor(cursor);
            }    
        }

        /// <summary>
        /// Get a cursor, execute the specified function, release the cursor and
        /// return the function's result.
        /// </summary>
        /// <typeparam name="T">The return type of the function.</typeparam>
        /// <param name="func">The function to execute.</param>
        /// <returns>The return value of the function.</returns>
        private T UsingCursor<T>(Func<PersistentDictionaryCursor<TKey, TValue>, T> func)
        {
            PersistentDictionaryCursor<TKey, TValue> cursor = this.cursors.GetCursor();
            try
            {
                return func(cursor);
            }
            finally
            {
                this.cursors.FreeCursor(cursor);
            }
        }
    }
}