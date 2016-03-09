// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Data.Entity;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;

using System.Reactive.Disposables;

namespace Microsoft.Its.Domain.Sql
{
    /// <summary>
    /// Represents sole access to the read model database for the purpose of updating read models.
    /// </summary>
#if DEBUG
        public 
#else
        internal
#endif
        class AppLock : IDisposable
    {
        private readonly DbContext db;
        private readonly string lockResourceName;
        private readonly bool disposeDbContext;
        private readonly int? resultCode;
        private readonly DbConnection connection;
        private readonly IDisposable disposables;

#if DEBUG
        public static readonly ConcurrentDictionary<AppLock, AppLock> Active = new ConcurrentDictionary<AppLock, AppLock>();
        private readonly Stopwatch timeSpentInAppLockStopwatch = Stopwatch.StartNew();
#endif

        /// <summary>
        /// Initializes a new instance of the <see cref="AppLock" /> class.
        /// </summary>
        /// <param name="db">The database.</param>
        /// <param name="lockResourceName">The lock resource.</param>
        /// <param name="disposeDbContext">if set to <c>true</c> dispose the database context when the AppLock is disposed.</param>
        public AppLock(
            DbContext db,
            string lockResourceName,
            bool disposeDbContext = true)
        {
            this.db = db;
            this.lockResourceName = lockResourceName;
            this.disposeDbContext = disposeDbContext;
            connection = (DbConnection) db.OpenConnection();

            const string cmd = @"
DECLARE @result int;
EXEC @result = sp_getapplock @Resource = @lockResource,
                                @LockMode = 'Exclusive',
                                @LockOwner = 'Session',
                                @LockTimeout = 60000;
SELECT @result";

            var result = -1000;

            using (var getAppLock = connection.CreateCommand())
            {
                getAppLock.Parameters.Add(new SqlParameter("lockResource", lockResourceName));
                getAppLock.CommandText = cmd;
                
                try
                {
                    Debug.WriteLine("Trying to acquire app lock '{0}' (#{1})", lockResourceName, GetHashCode());

                    result = (int)getAppLock.ExecuteScalar();
                }
                catch (SqlException exception)
                {
                    if (exception.Message.StartsWith("Timeout expired."))
                    {
                        Debug.WriteLine("Timeout expired waiting for sp_getapplock. (#{0})", GetHashCode());
                        DebugWriteLocks();
                        return;
                    }

                    throw;
                }
            }

            resultCode = result;

            if (result >= 0)
            {
                Debug.WriteLine("Acquired app lock '{0}' with result {1} (#{2})",
                                              lockResourceName,
                                              result,
                                              GetHashCode());
            }
            else
            {
                Debug.WriteLine("Failed to acquire app lock '{0}' with code {1} (#{2})",
                                              lockResourceName,
                                              result,
                                              GetHashCode());
            }

            disposables = Disposable.Create(OnDispose);

#if DEBUG
            Active[this] = this;
#endif
        }

        private void DebugWriteLocks()
        {
#if DEBUG
            Debug.WriteLine("Existing app locks:");
            var viewExistingLocks = connection.CreateCommand();
            viewExistingLocks.CommandText = @"SELECT TOP 1000 * FROM [sys].[dm_tran_locks] where resource_description != ''";
            foreach (DbDataRecord row in viewExistingLocks.ExecuteReader())
            {
                var values = new object[row.FieldCount];
                row.GetValues(values);
                Debug.WriteLine(string.Join(Environment.NewLine,
                                            values.Select((v, i) => row.GetName(i) + ": " + v)));
            }
#endif
        }

        /// <summary>
        /// Gets a value indicating whether a lock is acquired.
        /// </summary>
        /// <value>
        ///   <c>true</c> if [is acquired]; otherwise, <c>false</c>.
        /// </value>
        public bool IsAcquired
        {
            get
            {
                // status codes are:
                // 0     The lock was successfully granted synchronously.
                // 1     The lock was granted successfully after waiting for other incompatible locks to be released.
                // -1    The lock request timed out.
                // -2    The lock request was canceled.
                // -3    The lock request was chosen as a deadlock victim.
                // -999  Indicates a parameter validation or other call error.
                // http://technet.microsoft.com/en-us/library/ms189823.aspx
                return resultCode >= 0;
            }
        }

        private void TryReleaseSqlAppLock()
        {
            const string cmd = @"
DECLARE @result int;
EXEC @result = sp_releaseapplock @Resource = @lockResource,
                                 @LockOwner = 'Session';
SELECT @result";

            Debug.WriteLine("Trying to release app lock '{0}' (#{1})", lockResourceName, GetHashCode());

            try
            {
                using (var releaseAppLock = connection.CreateCommand())
                {
                    releaseAppLock.Parameters.Add(new SqlParameter("lockResource", lockResourceName));
                    releaseAppLock.CommandText = cmd;
                    var result = (int)releaseAppLock.ExecuteScalar();
                    Debug.WriteLine("Releasing app lock '{0}' succeeded with result {1} (#{2})", lockResourceName, result, GetHashCode());
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Exception occurred while releasing app lock. {0} (#{1})", ex, GetHashCode());
            }
        }

        private void OnDispose()
        {
#if DEBUG
            Debug.WriteLine("Disposing {0} AppLock after {1}ms for '{2}' (#{3})",
                            IsAcquired ? "acquired" : "unacquired",
                            timeSpentInAppLockStopwatch.Elapsed.TotalMilliseconds,
                            lockResourceName,
                            GetHashCode());

            AppLock @lock;
            Active.TryRemove(this, out @lock);
#endif

            TryReleaseSqlAppLock();

            if (disposeDbContext)
            {
                connection.Dispose();
                db.Dispose();
            }
        }

        public void Dispose()
        {
            disposables.Dispose();
        }
    }
}